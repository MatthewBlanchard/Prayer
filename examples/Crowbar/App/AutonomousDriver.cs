using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Runs an autonomous LLM agent loop for a single bot.
/// Each cycle: wait for halt → build overview → tool loop (state/do) → generate script → run → repeat.
/// </summary>
public sealed class AutonomousDriver
{
    private const int MaxStateCalls = 3;
    private const int MaxTokens = 512;
    private const float Temperature = 0.7f;
    private const int PollWaitMs = 1000;
    private const int MaxMissionRebuildAttempts = 3;
    private const int MaxPromptExamples = 8;
    private const string ExitMissionSelectionToken = "__EXIT__";
    private static readonly string[] CommandVerbHints =
    {
        "mine", "survey", "go", "accept_mission", "abandon_mission", "dock", "repair",
        "sell", "buy", "cancel_buy", "cancel_sell", "retrieve", "stash",
        "switch_ship", "install_mod", "uninstall_mod", "buy_ship", "buy_listed_ship",
        "commission_quote", "commission_ship", "commission_status", "sell_ship",
        "list_ship_for_sale", "wait", "halt"
    };
    private static readonly string PromptExamplesBlock = BuildPromptExamplesBlock();
    private sealed record MissionSelectionChoice(
        string MissionId,
        string Title,
        string Objective,
        bool AcceptRequired);

    private readonly PrayerApiClient _api;
    private readonly Func<AppPrayerRuntimeState?> _getState;
    private readonly string _prayerSessionId;
    private readonly string _botLabel;
    private readonly Action<string> _log;
    private readonly Action<string> _generationLog;

    private CancellationTokenSource? _cts;
    private Task? _task;

    public string? StatusMessage { get; private set; }
    public bool IsRunning => _task != null && !_task.IsCompleted;

    public AutonomousDriver(
        PrayerApiClient api,
        string prayerSessionId,
        string botLabel,
        Func<AppPrayerRuntimeState?> getState,
        Action<string> log,
        Action<string> generationLog)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _prayerSessionId = prayerSessionId ?? throw new ArgumentNullException(nameof(prayerSessionId));
        _botLabel = botLabel ?? "bot";
        _getState = getState ?? throw new ArgumentNullException(nameof(getState));
        _log = log ?? (_ => { });
        _generationLog = generationLog ?? (_ => { });
    }

    public void Start(string persona)
    {
        if (IsRunning)
            return;

        _cts = new CancellationTokenSource();
        StatusMessage = "Starting...";
        var token = _cts.Token;
        _task = Task.Run(() => RunLoopAsync(persona, token), token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        StatusMessage = "Stopped.";
    }

    // ── Main loop ────────────────────────────────────────────────────────────

    private async Task RunLoopAsync(string persona, CancellationToken ct)
    {
        _log($"[{_botLabel}] autonomous_driver_start");
        _generationLog($"[{_botLabel}] autonomous_driver_start");
        int cycle = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                cycle++;
                SetStatus("Waiting for script to finish...");
                _log($"[{_botLabel}] cycle={cycle} waiting_for_halt");

                // 1. Wait until the bot is idle (not executing a script).
                await _api.WaitForHaltAsync(_prayerSessionId, ct);
                if (ct.IsCancellationRequested) break;

                // 2. Get current game state.
                var state = _getState();
                if (state?.State == null)
                {
                    SetStatus("No game state available. Retrying in 2s...");
                    _log($"[{_botLabel}] cycle={cycle} no_state_retry");
                    await Task.Delay(2000, ct);
                    continue;
                }

                // 3. Go home before picking the next mission.
                SetStatus($"Cycle {cycle}: Returning home...");
                _log($"[{_botLabel}] cycle={cycle} go_home_start");
                if (!await TryExecuteScriptAsync("go $home;", "go_home", cycle, ct))
                {
                    await Task.Delay(2000, ct);
                    continue;
                }

                state = await GetFreshStateAsync(ct);
                if (state?.State == null)
                {
                    SetStatus("No game state available after go home. Retrying...");
                    await Task.Delay(2000, ct);
                    continue;
                }

                // 4. Intake missions until we have at least one active mission.
                var hasAnyActive = await OfferMissionsUntilReadyAsync(persona, cycle, ct);
                if (!hasAnyActive)
                {
                    SetStatus("No mission candidates available yet. Waiting...");
                    await Task.Delay(PollWaitMs, ct);
                    continue;
                }

                // 5. Process every active mission until none remain.
                await ProcessActiveMissionsUntilEmptyAsync(persona, cycle, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            SetStatus($"Driver crashed: {ex.Message}");
            _log($"[{_botLabel}] autonomous_driver_crash | {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _log($"[{_botLabel}] autonomous_driver_stop | cycles={cycle}");
            _generationLog($"[{_botLabel}] autonomous_driver_stop | cycles={cycle}");
            if (!ct.IsCancellationRequested)
                SetStatus("Driver stopped.");
        }
    }

    private async Task<bool> TryExecuteScriptAsync(string script, string context, int cycle, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(script))
            return false;

        try
        {
            _log($"[{_botLabel}] cycle={cycle} execute_script | context={context} | script_len={script.Length}");
            await _api.RunScriptAsync(_prayerSessionId, script, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetStatus($"Execute error ({context}): {ex.Message}");
            _log($"[{_botLabel}] cycle={cycle} execute_error | context={context} | {ex.Message}");
            return false;
        }
    }

    private async Task<bool> OfferMissionsUntilReadyAsync(string persona, int cycle, CancellationToken ct)
    {
        var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rounds = 0;

        while (!ct.IsCancellationRequested && rounds < 24)
        {
            rounds++;
            await _api.WaitForHaltAsync(_prayerSessionId, ct);

            var state = await GetFreshStateAsync(ct);
            if (state?.State == null)
                return false;

            var activeCount = state.State.ActiveMissions?.Length ?? 0;
            var hasAnyActive = activeCount > 0;

            var candidates = BuildAvailableMissionCandidates(state.State, rejected);
            if (candidates.Count == 0)
                return hasAnyActive;

            var allowExit = hasAnyActive;
            SetStatus($"Cycle {cycle}: Mission intake ({activeCount} active, {candidates.Count} available)...");
            var selected = await SelectMissionAsync(persona, state.State, cycle, candidates, allowExit, ct);
            if (selected == null)
                selected = candidates[0];

            if (string.Equals(selected.MissionId, ExitMissionSelectionToken, StringComparison.OrdinalIgnoreCase))
                return hasAnyActive;

            var acceptScript = $"accept_mission {selected.MissionId};";
            var accepted = await TryExecuteScriptAsync(acceptScript, "accept_mission", cycle, ct);
            if (!accepted)
                rejected.Add(selected.MissionId);
        }

        var finalState = await GetFreshStateAsync(ct);
        return (finalState?.State?.ActiveMissions?.Length ?? 0) > 0;
    }

    private async Task ProcessActiveMissionsUntilEmptyAsync(string persona, int cycle, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _api.WaitForHaltAsync(_prayerSessionId, ct);

            var state = await GetFreshStateAsync(ct);
            if (state?.State == null)
                return;

            var activeCandidates = BuildActiveMissionCandidates(state.State);
            if (activeCandidates.Count == 0)
                return;

            var selected = await SelectMissionAsync(
                persona,
                state.State,
                cycle,
                activeCandidates,
                allowExit: false,
                ct);
            if (selected == null)
                selected = activeCandidates[0];

            await RunMissionAttemptsAsync(persona, cycle, selected, ct);
        }
    }

    private async Task RunMissionAttemptsAsync(string persona, int cycle, MissionSelectionChoice choice, CancellationToken ct)
    {
        var state = await GetFreshStateAsync(ct);
        if (state?.State == null)
            return;

        var trackedMission = ResolveTrackedMission(state.State, choice.MissionId, choice.Title);
        if (trackedMission == null)
            return;

        var completed = false;
        var attemptsUsed = 0;
        while (attemptsUsed < MaxMissionRebuildAttempts && !ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            var latestState = await GetFreshStateAsync(ct);
            if (latestState?.State != null)
            {
                var latestMission = ResolveTrackedMission(latestState.State, choice.MissionId, choice.Title);
                if (latestMission == null)
                {
                    completed = true;
                    _log($"[{_botLabel}] cycle={cycle} mission_complete | mission_id={choice.MissionId} | detected_before_next_attempt");
                    break;
                }

                trackedMission = latestMission;
            }

            var attempt = attemptsUsed + 1;
            var trackedMissionId = FirstNonEmpty(trackedMission.Id, trackedMission.MissionId, trackedMission.TemplateId, choice.MissionId);
            SetStatus($"Cycle {cycle}: Mission attempt {attempt}/{MaxMissionRebuildAttempts}...");
            _log($"[{_botLabel}] cycle={cycle} mission_attempt={attempt} start | mission_id={trackedMissionId} | attempts_used={attemptsUsed}");

            var missionInstruction = BuildMissionExecutionInstruction(persona, trackedMission, attempt);
            _generationLog(
                $"[{_botLabel}] cycle={cycle} mission_attempt={attempt} generate_prompt{Environment.NewLine}" +
                $"---PROMPT---{Environment.NewLine}{missionInstruction}{Environment.NewLine}---END_PROMPT---");

            // Mission attempt budget is strictly a script-generation budget:
            // each generation call consumes one attempt, regardless of outcome.
            attemptsUsed++;
            _log($"[{_botLabel}] cycle={cycle} mission_attempt={attempt} increment_attempt | reason=script_generation_call | attempts_used={attemptsUsed}");

            string script;
            try
            {
                script = await _api.GenerateScriptAsync(_prayerSessionId, missionInstruction);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log($"[{_botLabel}] cycle={cycle} mission_attempt={attempt} generate_error | attempts_used={attemptsUsed} | {ex.Message}");
                _generationLog($"[{_botLabel}] cycle={cycle} mission_attempt={attempt} generate_error | {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (string.IsNullOrWhiteSpace(script))
            {
                _log($"[{_botLabel}] cycle={cycle} mission_attempt={attempt} generate_empty_script | attempts_used={attemptsUsed}");
                continue;
            }

            _generationLog(
                $"[{_botLabel}] cycle={cycle} mission_attempt={attempt} generate_script{Environment.NewLine}" +
                $"---SCRIPT---{Environment.NewLine}{script}{Environment.NewLine}---END_SCRIPT---");

            if (!await TryExecuteScriptAsync(script, "mission_script", cycle, ct))
            {
                _log($"[{_botLabel}] cycle={cycle} mission_attempt={attempt} execute_failed | attempts_used={attemptsUsed}");
                continue;
            }

            await _api.WaitForHaltAsync(_prayerSessionId, ct);

            state = await GetFreshStateAsync(ct);
            if (state?.State == null)
            {
                _log($"[{_botLabel}] cycle={cycle} mission_attempt={attempt} missing_state_after_halt | attempts_used={attemptsUsed}");
                continue;
            }

            var stillActive = ResolveTrackedMission(state.State, choice.MissionId, choice.Title);
            if (stillActive == null)
            {
                completed = true;
                _log($"[{_botLabel}] cycle={cycle} mission_complete | mission_id={choice.MissionId}");
                break;
            }

            trackedMission = stillActive;
            trackedMissionId = FirstNonEmpty(trackedMission.Id, trackedMission.MissionId, trackedMission.TemplateId, choice.MissionId);
            _log($"[{_botLabel}] cycle={cycle} mission_attempt={attempt} mission_not_complete_yet | mission_id={trackedMissionId} | attempts_used={attemptsUsed}");
        }

        if (!completed && attemptsUsed >= MaxMissionRebuildAttempts)
        {
            state = await GetFreshStateAsync(ct);
            var toAbandon = state?.State != null
                ? ResolveTrackedMission(state.State, choice.MissionId, choice.Title)
                : trackedMission;

            if (toAbandon != null)
            {
                var abandonId = FirstNonEmpty(toAbandon.Id, toAbandon.MissionId, toAbandon.TemplateId, choice.MissionId);
                SetStatus($"Cycle {cycle}: Abandoning mission {abandonId} after {MaxMissionRebuildAttempts} failed rebuilds...");
                var abandonScript = $"abandon_mission {abandonId};";
                await TryExecuteScriptAsync(abandonScript, "abandon_mission", cycle, ct);
            }
        }

        await WaitUntilMissionLeavesActiveAsync(choice, ct);
    }

    private async Task<MissionSelectionChoice?> SelectMissionAsync(
        string persona,
        GameState state,
        int cycle,
        IReadOnlyList<MissionSelectionChoice> candidates,
        bool allowExit,
        CancellationToken ct)
    {
        if (candidates == null || candidates.Count == 0)
            return null;

        var prompt = BuildMissionSelectionPrompt(persona, state, candidates, allowExit);
        _generationLog(
            $"[{_botLabel}] cycle={cycle} mission_pick_prompt{Environment.NewLine}" +
            $"---PROMPT---{Environment.NewLine}{prompt}{Environment.NewLine}---END_PROMPT---");

        string response;
        try
        {
            response = await _api.GenerateAsync(
                _prayerSessionId,
                prompt,
                maxTokens: MaxTokens,
                temperature: 0.2f,
                cancellationToken: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"[{_botLabel}] cycle={cycle} mission_pick_error | {ex.GetType().Name}: {ex.Message}");
            return candidates[0];
        }

        response = (response ?? string.Empty).Trim();
        _generationLog(
            $"[{_botLabel}] cycle={cycle} mission_pick_response{Environment.NewLine}" +
            $"---RESPONSE---{Environment.NewLine}{response}{Environment.NewLine}---END_RESPONSE---");

        var selected = ResolveMissionChoiceFromResponse(response, candidates, allowExit);
        if (selected == null)
            selected = candidates[0];

        _log($"[{_botLabel}] cycle={cycle} mission_selected | mission_id={selected.MissionId} | accept_required={selected.AcceptRequired}");
        return selected;
    }

    private static List<MissionSelectionChoice> BuildAvailableMissionCandidates(GameState state, HashSet<string> excludedMissionIds)
    {
        var candidates = new List<MissionSelectionChoice>();
        foreach (var mission in state.AvailableMissions ?? Array.Empty<MissionInfo>())
        {
            if (mission == null)
                continue;

            var missionId = FirstNonEmpty(mission.Id, mission.MissionId, mission.TemplateId);
            if (string.IsNullOrWhiteSpace(missionId))
                continue;
            if (excludedMissionIds.Contains(missionId))
                continue;

            var title = FirstNonEmpty(mission.Title, missionId);
            var objective = FirstNonEmpty(mission.ObjectivesSummary, mission.ProgressText, mission.ProgressSummary, mission.Description, "(no objective)");
            candidates.Add(new MissionSelectionChoice(missionId, title, objective, AcceptRequired: true));
        }

        return candidates;
    }

    private static List<MissionSelectionChoice> BuildActiveMissionCandidates(GameState state)
    {
        var candidates = new List<MissionSelectionChoice>();
        foreach (var mission in state.ActiveMissions ?? Array.Empty<MissionInfo>())
        {
            if (mission == null)
                continue;

            var missionId = FirstNonEmpty(mission.Id, mission.MissionId, mission.TemplateId);
            if (string.IsNullOrWhiteSpace(missionId))
                continue;

            var title = FirstNonEmpty(mission.Title, missionId);
            var objective = FirstNonEmpty(mission.ObjectivesSummary, mission.ProgressText, mission.ProgressSummary, mission.Description, "(no objective)");
            candidates.Add(new MissionSelectionChoice(missionId, title, objective, AcceptRequired: false));
        }

        return candidates;
    }

    private static string BuildMissionSelectionPrompt(string persona, GameState state, IReadOnlyList<MissionSelectionChoice> candidates, bool allowExit)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<|start_header_id|>system<|end_header_id|>");
        sb.AppendLine(persona.Trim());
        sb.AppendLine();
        sb.AppendLine("Pick exactly one option from the list that best fits your persona and current ship state.");
        if (allowExit)
            sb.AppendLine($"If no candidate fits better, return exactly: {ExitMissionSelectionToken}");
        sb.AppendLine("Return ONLY the mission id token (or EXIT token). No explanation.");
        sb.AppendLine("<|eot_id|>");
        sb.AppendLine("<|start_header_id|>user<|end_header_id|>");
        sb.Append("System: ").AppendLine(state.System);
        sb.Append("Current POI: ").Append(state.CurrentPOI?.Id ?? "(unknown)");
        if (state.Docked) sb.Append(" (docked)");
        sb.AppendLine();
        sb.Append("Credits: ").AppendLine(state.Credits.ToString());
        sb.Append("Fuel: ").Append(state.Ship?.Fuel).Append('/').AppendLine((state.Ship?.MaxFuel).ToString());
        sb.AppendLine("Mission candidates:");
        for (int i = 0; i < candidates.Count; i++)
        {
            var mission = candidates[i];
            sb.Append(i + 1).Append(". id=").Append(mission.MissionId)
              .Append(" | title=").Append(mission.Title)
              .Append(" | objective=").AppendLine(mission.Objective);
        }
        if (allowExit)
            sb.Append(candidates.Count + 1).Append(". id=").Append(ExitMissionSelectionToken).AppendLine(" | title=Exit mission intake");
        sb.AppendLine("<|eot_id|>");
        sb.Append("<|start_header_id|>assistant<|end_header_id|>");
        return sb.ToString();
    }

    private static MissionSelectionChoice? ResolveMissionChoiceFromResponse(
        string response,
        IReadOnlyList<MissionSelectionChoice> candidates,
        bool allowExit)
    {
        if (candidates.Count == 0)
            return null;

        var trimmed = (response ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return null;
        if (allowExit && string.Equals(trimmed, ExitMissionSelectionToken, StringComparison.OrdinalIgnoreCase))
            return new MissionSelectionChoice(ExitMissionSelectionToken, "Exit mission intake", "", AcceptRequired: false);

        foreach (var candidate in candidates)
        {
            if (string.Equals(trimmed, candidate.MissionId, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        foreach (var candidate in candidates)
        {
            if (trimmed.Contains(candidate.MissionId, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        var firstLine = trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (!string.IsNullOrWhiteSpace(firstLine))
        {
            if (int.TryParse(firstLine, out var index) && index >= 1 && index <= candidates.Count)
                return candidates[index - 1];
            if (allowExit && int.TryParse(firstLine, out var exitIndex) && exitIndex == candidates.Count + 1)
                return new MissionSelectionChoice(ExitMissionSelectionToken, "Exit mission intake", "", AcceptRequired: false);

            foreach (var candidate in candidates)
            {
                if (candidate.MissionId.StartsWith(firstLine, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
        }

        return null;
    }

    private static MissionInfo? ResolveTrackedMission(GameState state, string missionId, string title)
    {
        var active = state.ActiveMissions ?? Array.Empty<MissionInfo>();
        foreach (var mission in active)
        {
            if (mission == null)
                continue;

            if (MissionMatchesSelector(mission, missionId))
                return mission;

            if (!string.IsNullOrWhiteSpace(title) &&
                StartsWithIgnoreCase(mission.Title, title))
            {
                return mission;
            }
        }

        return null;
    }

    private static string BuildMissionExecutionInstruction(string persona, MissionInfo mission, int attempt)
    {
        var missionId = FirstNonEmpty(mission.Id, mission.MissionId, mission.TemplateId);
        var missionIdToken = missionId.Length > 6 ? missionId[..6] : missionId;
        var objective = FirstNonEmpty(
            mission.ObjectivesSummary,
            mission.ProgressText,
            mission.ProgressSummary,
            mission.Description,
            "Complete mission objectives.");

        return $"{objective}\nmission_id={missionIdToken}";
    }

    private async Task WaitUntilMissionLeavesActiveAsync(MissionSelectionChoice choice, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            var state = await GetFreshStateAsync(ct);
            if (state?.State == null)
            {
                await Task.Delay(PollWaitMs, ct);
                continue;
            }

            var mission = ResolveTrackedMission(state.State, choice.MissionId, choice.Title);
            if (mission == null)
                return;

            await Task.Delay(PollWaitMs, ct);
        }
    }

    private async Task<AppPrayerRuntimeState?> GetFreshStateAsync(CancellationToken ct)
    {
        try
        {
            var poll = await _api.GetRuntimeStateLongPollAsync(
                _prayerSessionId,
                sinceVersion: 0,
                waitMs: 0,
                cancellationToken: ct);

            if (poll.State != null)
                return poll.State;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch { }

        return null;
    }

    // ── Tool loop ─────────────────────────────────────────────────────────────

    private async Task<string> RunToolLoopAsync(
        string persona,
        AppPrayerRuntimeState runtimeState,
        string lastResult,
        int cycle,
        CancellationToken ct)
    {
        var gameState = runtimeState.State!;
        var overview = BuildCompactOverview(gameState, runtimeState, lastResult);
        var conversation = BuildInitialPrompt(persona, overview);
        int stateCallsUsed = 0;

        for (int turn = 0; turn < MaxStateCalls + 2; turn++)
        {
            ct.ThrowIfCancellationRequested();
            _generationLog(
                $"[{_botLabel}] cycle={cycle} tool_turn={turn} llm_generate_request{Environment.NewLine}" +
                $"---PROMPT---{Environment.NewLine}{conversation}{Environment.NewLine}---END_PROMPT---");

            var response = await _api.GenerateAsync(
                _prayerSessionId,
                conversation,
                maxTokens: MaxTokens,
                temperature: Temperature,
                cancellationToken: ct);

            response = (response ?? "").Trim();
            _log($"[{_botLabel}] tool_turn={turn} response_preview={Truncate(response, 120)}");
            _generationLog(
                $"[{_botLabel}] cycle={cycle} tool_turn={turn} llm_generate_response{Environment.NewLine}" +
                $"---RESPONSE---{Environment.NewLine}{response}{Environment.NewLine}---END_RESPONSE---");

            // Check for do("instruction"). Keep prayer_generate_script(...) as a compatibility alias.
            var prayerInstruction = ExtractToolCallArgument(response, "do");
            if (string.IsNullOrWhiteSpace(prayerInstruction))
                prayerInstruction = ExtractToolCallArgument(response, "prayer_generate_script");
            if (!string.IsNullOrWhiteSpace(prayerInstruction))
            {
                _generationLog(
                    $"[{_botLabel}] cycle={cycle} tool_turn={turn} do_selected{Environment.NewLine}" +
                    $"---INSTRUCTION---{Environment.NewLine}{prayerInstruction.Trim()}{Environment.NewLine}---END_INSTRUCTION---");
                return prayerInstruction.Trim();
            }

            // Check for domission("mission_id_or_title_prefix"), then build a do(...) instruction
            // from mission objective text + mission_id.
            var doMissionSelector = ExtractToolCallArgument(response, "domission");
            if (doMissionSelector != null)
            {
                var missionInstruction = BuildDoMissionInstruction(gameState, doMissionSelector);
                if (!string.IsNullOrWhiteSpace(missionInstruction))
                {
                    _generationLog(
                        $"[{_botLabel}] cycle={cycle} tool_turn={turn} domission_selected selector={doMissionSelector}{Environment.NewLine}" +
                        $"---INSTRUCTION---{Environment.NewLine}{missionInstruction.Trim()}{Environment.NewLine}---END_INSTRUCTION---");
                    return missionInstruction.Trim();
                }
            }

            // Check for state("path") drill-down.
            var statePath = ExtractToolCallArgument(response, "state");
            if (!string.IsNullOrWhiteSpace(statePath) && stateCallsUsed < MaxStateCalls)
            {
                stateCallsUsed++;
                var normalizedPath = NormalizeStatePath(statePath);
                var stateResult = BuildStateResult(gameState, normalizedPath);
                _log($"[{_botLabel}] tool_state path={statePath} normalized={normalizedPath} state_calls_used={stateCallsUsed}");
                _generationLog(
                    $"[{_botLabel}] cycle={cycle} tool_turn={turn} state_call path={statePath} normalized={normalizedPath} calls={stateCallsUsed}{Environment.NewLine}" +
                    $"---STATE_RESULT---{Environment.NewLine}{stateResult}{Environment.NewLine}---END_STATE_RESULT---");

                // Append the assistant response + tool result to conversation and continue.
                conversation += $"\n<|start_header_id|>assistant<|end_header_id|>\n{response}<|eot_id|>";
                conversation += $"\n<|start_header_id|>user<|end_header_id|>\n[state(\"{statePath.Trim()}\") result]\n{stateResult}<|eot_id|>";
                continue;
            }

            // If state budget exhausted, fail fast and restart next cycle.
            if (stateCallsUsed >= MaxStateCalls)
            {
                _log($"[{_botLabel}] tool_loop_fail | reason=state_call_budget_exhausted | used={stateCallsUsed}");
                _generationLog($"[{_botLabel}] cycle={cycle} tool_loop_fail | reason=state_call_budget_exhausted | used={stateCallsUsed}");
                throw new InvalidOperationException($"State call budget exhausted ({MaxStateCalls}).");
            }
            else
            {
                conversation += $"\n<|start_header_id|>assistant<|end_header_id|>\n{response}<|eot_id|>";
                conversation += $"\n<|start_header_id|>user<|end_header_id|>\nRespond with state(\"<path>\"), do(\"<instruction>\"), or domission(\"<active_mission_id_or_title_prefix>\"). IMPORTANT: DoMission is only for already accepted active missions. If a mission is only in Available, accept it first before DoMission. do(...) is action-only (no inspection) and must focus on one sequence only (no multi-quest or multi-objective plans). Keep instruction 1-2 sentences max.<|eot_id|>";
            }
        }

        // Fallback: return a generic instruction based on state.
        return BuildFallbackInstruction(gameState);
    }

    // ── Prompt builders ───────────────────────────────────────────────────────

    private static string BuildInitialPrompt(string persona, string overview)
    {
        var system =
            $"<|start_header_id|>system<|end_header_id|>\n" +
            $"{persona.Trim()}\n\n" +
            $"You are piloting a spaceship in an online game. Each turn you may inspect details with state(path) or decide what to do with do(instruction).\n\n" +
            $"Available command verbs for do(...) instructions: {string.Join(", ", CommandVerbHints)}.\n\n" +
            $"{PromptExamplesBlock}" +
            $"Available tools:\n" +
            $"  state(\"ship\")      — full ship and module details\n" +
            $"  state(\"missions\")  — active and available missions\n" +
            $"  state(\"market\")    — trade economy and prices (only when docked)\n" +
            $"  state(\"space\")     — location, POIs, connected systems\n" +
            $"  do(\"<instruction>\") — decide your next action (e.g. 'mine iron at current asteroid')\n" +
            $"  DoMission(\"<active_mission_id_or_title_prefix>\") — only for accepted active missions; builds a do-instruction with mission text + mission_id\n\n" +
            $"Rules: call state(...) at most 3 times, then output exactly one do(...) or DoMission(...). DoMission requires the mission to already be accepted and present in Active missions. If a mission is only in Available missions, first accept it (e.g. via a do-instruction that accepts the mission), then use DoMission on a later turn. do(...) must contain only an action plan and must never ask for or perform state inspection; use state(...) for all inspection. do(...) must focus on one sequence only; do not combine multiple quests or objectives in one instruction. Instruction must be 1-2 sentences max. Be concise and in-character.\n" +
            $"<|eot_id|>";

        var user =
            $"<|start_header_id|>user<|end_header_id|>\n" +
            $"{overview}\n" +
            $"<|eot_id|>";

        return system + "\n" + user + "\n<|start_header_id|>assistant<|end_header_id|>\n";
    }

    private static string BuildCompactOverview(
        GameState state,
        AppPrayerRuntimeState runtime,
        string lastResult)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Current State ===");
        sb.Append("System: ").AppendLine(string.IsNullOrWhiteSpace(state.System) ? "(unknown)" : state.System);
        sb.Append("POI: ").Append(state.CurrentPOI?.Id ?? "(unknown)").Append(" (").Append(state.CurrentPOI?.Type ?? "?").Append(")");
        if (state.Docked) sb.Append(" DOCKED");
        sb.AppendLine();
        if (state.CurrentPOI != null)
        {
            sb.Append("POI Details: ").AppendLine(FormatPoiFlags(state.CurrentPOI));
            sb.Append("POI Resources: ").AppendLine(FormatPoiResources(state.CurrentPOI));
        }
        sb.Append("Credits: ").AppendLine(state.Credits.ToString());
        sb.Append("Fuel: ").Append(state.Ship?.Fuel).Append('/').AppendLine((state.Ship?.MaxFuel).ToString());
        sb.Append("Hull: ").Append(state.Ship?.Hull).Append('/').AppendLine((state.Ship?.MaxHull).ToString());
        sb.Append("Cargo: ").Append(state.Ship?.CargoUsed).Append('/').AppendLine((state.Ship?.CargoCapacity).ToString());

        int activeMissions = state.ActiveMissions?.Length ?? 0;
        int availableMissions = state.AvailableMissions?.Length ?? 0;
        sb.Append("Missions: ").Append(activeMissions).Append(" active, ").Append(availableMissions).AppendLine(" available");
        sb.AppendLine("Active mission details:");
        if (state.ActiveMissions?.Length > 0)
        {
            foreach (var mission in state.ActiveMissions.Take(5))
            {
                if (mission == null)
                    continue;

                var missionId = string.IsNullOrWhiteSpace(mission.Id)
                    ? (string.IsNullOrWhiteSpace(mission.MissionId) ? "(unknown)" : mission.MissionId)
                    : mission.Id;
                var objective = FirstNonEmpty(
                    mission.ObjectivesSummary,
                    mission.ProgressText,
                    mission.ProgressSummary,
                    mission.Description,
                    "(no objective text)");

                sb.Append("- ").Append(missionId).Append(": ").AppendLine(objective);
            }
        }
        else
        {
            sb.AppendLine("- (none)");
        }
        sb.Append("Last result: ").AppendLine(lastResult);

        if (runtime.ExecutionStatusLines?.Count > 0)
        {
            var lastLine = runtime.ExecutionStatusLines[^1];
            if (!string.IsNullOrWhiteSpace(lastLine))
                sb.Append("Last status: ").AppendLine(lastLine);
        }

        sb.AppendLine();
        sb.AppendLine("Respond with state(\"<path>\") to inspect, do(\"<instruction>\") to act, or DoMission(\"<active_mission_id_or_title_prefix>\") to execute an accepted mission objective. Available missions must be accepted before DoMission. Keep instruction 1-2 sentences max.");
        return sb.ToString();
    }

    private static string? BuildDoMissionInstruction(GameState state, string selector)
    {
        var mission = ResolveMissionForDoMission(state, selector);
        if (mission == null)
            return null;

        var missionId = FirstNonEmpty(
            mission.Id,
            mission.MissionId,
            mission.TemplateId,
            selector)?.Trim();
        if (string.IsNullOrWhiteSpace(missionId))
            return null;

        var missionText = FirstNonEmpty(
            mission.ObjectivesSummary,
            mission.ProgressText,
            mission.ProgressSummary,
            mission.Description,
            mission.Title,
            "Complete the mission objectives.");

        return $"{missionText.Trim()}\nmission_id={missionId}";
    }

    private static MissionInfo? ResolveMissionForDoMission(GameState state, string selector)
    {
        var key = (selector ?? string.Empty).Trim();
        var active = state.ActiveMissions ?? Array.Empty<MissionInfo>();

        if (key.Length == 0)
            return active.FirstOrDefault(m => m != null);

        foreach (var mission in active)
        {
            if (mission == null)
                continue;
            if (MissionMatchesSelector(mission, key))
                return mission;
        }

        return null;
    }

    private static bool MissionMatchesSelector(MissionInfo mission, string selector)
    {
        return StartsWithIgnoreCase(mission.Id, selector) ||
               StartsWithIgnoreCase(mission.MissionId, selector) ||
               StartsWithIgnoreCase(mission.TemplateId, selector) ||
               StartsWithIgnoreCase(mission.Title, selector);
    }

    private static bool StartsWithIgnoreCase(string? value, string prefix)
    {
        var input = (value ?? string.Empty).Trim();
        return input.Length > 0 && input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildStateResult(GameState state, string topic)
    {
        return topic switch
        {
            "ship" => BuildShipExpand(state),
            "missions" => BuildMissionsExpand(state),
            "market" or "trade" => BuildMarketExpand(state),
            "space" or "map" => BuildSpaceExpand(state),
            _ => $"(unknown topic '{topic}' — use ship, missions, market, or space)"
        };
    }

    private static string BuildShipExpand(GameState state)
    {
        var ship = state.Ship;
        var sb = new StringBuilder();
        sb.AppendLine("=== Ship ===");
        sb.Append("Name: ").AppendLine(ship.Name ?? "-");
        sb.Append("Class: ").AppendLine(ship.ClassId ?? "-");
        sb.Append("Fuel: ").Append(ship.Fuel).Append('/').AppendLine(ship.MaxFuel.ToString());
        sb.Append("Hull: ").Append(ship.Hull).Append('/').AppendLine(ship.MaxHull.ToString());
        sb.Append("Shield: ").Append(ship.Shield).Append('/').AppendLine(ship.MaxShield.ToString());
        sb.Append("Cargo: ").Append(ship.CargoUsed).Append('/').AppendLine(ship.CargoCapacity.ToString());
        sb.Append("Speed: ").AppendLine(ship.Speed.ToString());
        sb.Append("Armor: ").AppendLine(ship.Armor.ToString());
        sb.Append("CPU: ").Append(ship.CpuUsed).Append('/').AppendLine(ship.CpuCapacity.ToString());
        if (ship.Cargo?.Count > 0)
        {
            sb.AppendLine("### Cargo");
            foreach (var (itemId, stack) in ship.Cargo)
                sb.Append("  ").Append(itemId).Append(": ").AppendLine(stack.Quantity.ToString());
        }
        return sb.ToString();
    }

    private static string BuildMissionsExpand(GameState state)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Missions ===");
        sb.AppendLine("### Active");
        if (state.ActiveMissions?.Length > 0)
        {
            foreach (var m in state.ActiveMissions)
                sb.Append("- ").Append(m.Id).Append(": ").AppendLine(string.IsNullOrWhiteSpace(m.ObjectivesSummary) ? m.Title : m.ObjectivesSummary);
        }
        else
        {
            sb.AppendLine("(none)");
        }

        if (state.AvailableMissions?.Length > 0)
        {
            sb.AppendLine("### Available");
            foreach (var m in state.AvailableMissions)
                sb.Append("- ").Append(m.Id).Append(": ").AppendLine(string.IsNullOrWhiteSpace(m.ObjectivesSummary) ? m.Title : m.ObjectivesSummary);
        }
        return sb.ToString();
    }

    private static string BuildMarketExpand(GameState state)
    {
        if (!state.Docked)
            return "(not docked — no market data)";

        var sb = new StringBuilder();
        sb.AppendLine("=== Market ===");
        sb.Append("Station: ").AppendLine(state.CurrentPOI?.Id ?? "(unknown)");
        sb.Append("Credits: ").AppendLine(state.Credits.ToString());

        if (state.EconomyDeals?.Length > 0)
        {
            sb.AppendLine("### Deals");
            foreach (var deal in state.EconomyDeals.Take(20))
                sb.Append("- ").Append(deal.ItemId).Append(": buy=").Append(deal.BuyPrice).Append(" sell=").AppendLine(deal.SellPrice.ToString());
        }

        if (state.OwnSellOrders?.Length > 0)
        {
            sb.AppendLine("### Your Sell Orders");
            foreach (var order in state.OwnSellOrders)
                sb.Append("- ").Append(order.ItemId).Append(" x").Append(order.Quantity).Append(" @ ").AppendLine(order.PriceEach.ToString());
        }

        if (state.OwnBuyOrders?.Length > 0)
        {
            sb.AppendLine("### Your Buy Orders");
            foreach (var order in state.OwnBuyOrders)
                sb.Append("- ").Append(order.ItemId).Append(" x").Append(order.Quantity).Append(" @ ").AppendLine(order.PriceEach.ToString());
        }

        return sb.ToString();
    }

    private static string BuildSpaceExpand(GameState state)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Space ===");
        sb.Append("System: ").AppendLine(state.System ?? "(unknown)");
        sb.Append("POI: ").Append(state.CurrentPOI?.Id ?? "(unknown)").Append(" (").Append(state.CurrentPOI?.Type ?? "?").Append(')');
        if (state.Docked) sb.Append(" DOCKED");
        sb.AppendLine();

        if (state.POIs?.Length > 0)
        {
            sb.AppendLine("### POIs in System");
            foreach (var poi in state.POIs)
            {
                sb.Append("- ").Append(poi.Id ?? "(unknown)").Append(" (").Append(poi.Type ?? "?").Append(")");
                var flags = FormatPoiFlags(poi);
                if (!string.IsNullOrWhiteSpace(flags))
                    sb.Append(" | ").Append(flags);
                var resources = FormatPoiResources(poi);
                if (!string.IsNullOrWhiteSpace(resources) && !string.Equals(resources, "(none)", StringComparison.Ordinal))
                    sb.Append(" | resources: ").Append(resources);
                sb.AppendLine();
            }
        }

        if (state.Systems?.Length > 0)
        {
            sb.AppendLine("### Connected Systems");
            foreach (var sys in state.Systems)
                sb.Append("- ").AppendLine(sys);
        }

        if (state.Notifications?.Length > 0)
        {
            sb.AppendLine("### Notifications");
            foreach (var n in state.Notifications.Take(5))
                sb.Append("- ").AppendLine(n.Summary ?? n.Type ?? "(empty)");
        }

        return sb.ToString();
    }

    private static string BuildFallbackInstruction(GameState state)
    {
        if (state.ActiveMissions?.Length > 0)
            return "Continue working on active missions.";
        if (!state.Docked && state.Ship?.Fuel < (state.Ship?.MaxFuel / 3))
            return "Find a station to refuel.";
        return "Explore the area and look for opportunities.";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return string.Empty;
    }

    private static bool HasScriptFailureStatus(AppPrayerRuntimeState state)
    {
        var lines = state.ExecutionStatusLines;
        if (lines == null || lines.Count == 0)
            return false;

        var last = lines[^1];
        if (string.IsNullOrWhiteSpace(last))
            return false;

        var text = last.Trim();
        return text.Contains("Script step failed", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Script condition error", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Script halted", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("timed out after", StringComparison.OrdinalIgnoreCase);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetStatus(string message)
    {
        StatusMessage = message;
    }

    private static string NormalizeStatePath(string path)
    {
        var normalized = (path ?? string.Empty).Trim().Trim('/').ToLowerInvariant();
        if (normalized.Length == 0)
            return "space";

        if (normalized.StartsWith("ship", StringComparison.Ordinal))
            return "ship";
        if (normalized.StartsWith("mission", StringComparison.Ordinal))
            return "missions";
        if (normalized.StartsWith("market", StringComparison.Ordinal) ||
            normalized.StartsWith("trade", StringComparison.Ordinal) ||
            normalized.StartsWith("economy", StringComparison.Ordinal))
            return "market";
        if (normalized.StartsWith("space", StringComparison.Ordinal) ||
            normalized.StartsWith("overview", StringComparison.Ordinal) ||
            normalized.StartsWith("navigation", StringComparison.Ordinal) ||
            normalized.StartsWith("poi", StringComparison.Ordinal))
            return "space";

        var firstSegment = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstSegment) ? "space" : firstSegment;
    }

    private static string? ExtractToolCallArgument(string text, string toolName)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(toolName))
            return null;

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            var marker = toolName + "(";
            var start = trimmed.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                continue;

            var open = trimmed.IndexOf('(', start + toolName.Length);
            if (open < 0)
                continue;

            var close = trimmed.IndexOf(')', open + 1);
            if (close <= open)
                continue;

            var argument = trimmed.Substring(open + 1, close - open - 1).Trim();
            if (argument.Length == 0)
                return string.Empty;

            if ((argument.StartsWith("\"", StringComparison.Ordinal) && argument.EndsWith("\"", StringComparison.Ordinal)) ||
                (argument.StartsWith("'", StringComparison.Ordinal) && argument.EndsWith("'", StringComparison.Ordinal)))
                argument = argument[1..^1].Trim();

            return argument;
        }

        return null;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private static string FormatPoiFlags(POIInfo poi)
    {
        if (poi == null)
            return "(none)";

        var flags = new List<string>(4);
        if (poi.IsStation)
            flags.Add("station");
        if (poi.HasBase)
            flags.Add(!string.IsNullOrWhiteSpace(poi.BaseName)
                ? $"dockable ({poi.BaseName})"
                : "dockable");
        if (poi.IsMiningTarget)
            flags.Add("mining");
        if (poi.Online > 0)
            flags.Add($"online={poi.Online}");

        return flags.Count == 0 ? "(none)" : string.Join(", ", flags);
    }

    private static string FormatPoiResources(POIInfo poi)
    {
        if (poi?.Resources == null || poi.Resources.Length == 0)
            return "(none)";

        var resources = poi.Resources
            .Where(r => r != null && !string.IsNullOrWhiteSpace(r.ResourceId))
            .Take(5)
            .Select(r =>
            {
                var richness = r.Richness.HasValue
                    ? r.Richness.Value.ToString()
                    : (string.IsNullOrWhiteSpace(r.RichnessText) ? "?" : r.RichnessText);
                var remaining = !string.IsNullOrWhiteSpace(r.RemainingDisplay)
                    ? r.RemainingDisplay
                    : (r.Remaining.HasValue ? r.Remaining.Value.ToString() : "?");
                return $"{r.ResourceId} (rich {richness}, rem {remaining})";
            })
            .ToArray();

        return resources.Length == 0 ? "(none)" : string.Join("; ", resources);
    }

    private static string BuildPromptExamplesBlock()
    {
        try
        {
            if (!File.Exists(AppPaths.ScriptGenerationExamplesFile))
                return string.Empty;

            using var doc = JsonDocument.Parse(File.ReadAllText(AppPaths.ScriptGenerationExamplesFile));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return string.Empty;

            var prompts = doc.RootElement
                .EnumerateArray()
                .Select(entry =>
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                        return null;
                    if (!entry.TryGetProperty("Prompt", out var promptElement))
                        return null;
                    if (promptElement.ValueKind != JsonValueKind.String)
                        return null;
                    var prompt = (promptElement.GetString() ?? string.Empty).Trim();
                    return prompt.Length == 0 ? null : prompt;
                })
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.Ordinal)
                .TakeLast(MaxPromptExamples)
                .ToList();

            if (prompts.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("Good prompt examples from cache:");
            foreach (var prompt in prompts)
                sb.Append("- ").Append(prompt).AppendLine();
            sb.AppendLine();
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }
}
