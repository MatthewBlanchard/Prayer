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
    private const int HaltPollWaitMs = 500;
    private const int MaxHaltWaitSeconds = 120;
    private const int MaxPromptExamples = 8;
    private static readonly string[] CommandVerbHints =
    {
        "mine", "survey", "go", "accept_mission", "abandon_mission", "dock", "repair",
        "sell", "buy", "cancel_buy", "cancel_sell", "retrieve", "stash",
        "switch_ship", "install_mod", "uninstall_mod", "buy_ship", "buy_listed_ship",
        "commission_quote", "commission_ship", "commission_status", "sell_ship",
        "list_ship_for_sale", "wait", "halt"
    };
    private static readonly string PromptExamplesBlock = BuildPromptExamplesBlock();

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
        string lastResult = "(none)";
        int? lastPlannedTick = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                cycle++;
                SetStatus("Waiting for script to finish...");
                _log($"[{_botLabel}] cycle={cycle} waiting_for_halt");

                // 1. Wait until the bot is idle (not executing a script).
                var isIdle = await WaitForHaltAsync(ct);
                if (ct.IsCancellationRequested) break;
                if (!isIdle)
                {
                    SetStatus("Previous script still running. Waiting...");
                    _log($"[{_botLabel}] cycle={cycle} wait_for_halt_timeout");
                    await Task.Delay(PollWaitMs, ct);
                    continue;
                }

                // 2. Get current game state.
                var state = _getState();
                if (state?.State == null)
                {
                    SetStatus("No game state available. Retrying in 2s...");
                    _log($"[{_botLabel}] cycle={cycle} no_state_retry");
                    await Task.Delay(2000, ct);
                    continue;
                }

                if (state.CurrentTick.HasValue &&
                    lastPlannedTick.HasValue &&
                    state.CurrentTick.Value == lastPlannedTick.Value)
                {
                    SetStatus($"Waiting for next tick (current={state.CurrentTick.Value})...");
                    _log($"[{_botLabel}] cycle={cycle} skip_replan_same_tick | tick={state.CurrentTick.Value}");
                    await Task.Delay(PollWaitMs, ct);
                    continue;
                }

                // 3. Run the state/do tool loop.
                SetStatus($"Cycle {cycle}: Thinking...");
                _log($"[{_botLabel}] cycle={cycle} tool_loop_start");

                string prayerInstruction;
                try
                {
                    prayerInstruction = await RunToolLoopAsync(persona, state, lastResult, cycle, ct);
                    lastPlannedTick = state.CurrentTick;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SetStatus($"Tool loop error: {ex.Message}");
                    _log($"[{_botLabel}] cycle={cycle} tool_loop_error | {ex.Message}");
                    await Task.Delay(3000, ct);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(prayerInstruction))
                {
                    SetStatus("No instruction generated. Retrying in 2s...");
                    await Task.Delay(2000, ct);
                    continue;
                }

                // 4. Generate script from the prayer instruction.
                SetStatus($"Cycle {cycle}: Generating script...");
                _log($"[{_botLabel}] cycle={cycle} generate_script | instruction_len={prayerInstruction.Length}");
                _generationLog(
                    $"[{_botLabel}] cycle={cycle} do_call_prompt{Environment.NewLine}" +
                    $"---PROMPT---{Environment.NewLine}{prayerInstruction}{Environment.NewLine}---END_PROMPT---");

                string script;
                try
                {
                    script = await _api.GenerateScriptAsync(_prayerSessionId, prayerInstruction);
                    _generationLog(
                        $"[{_botLabel}] cycle={cycle} do_call_result{Environment.NewLine}" +
                        $"---SCRIPT---{Environment.NewLine}{script}{Environment.NewLine}---END_SCRIPT---");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SetStatus($"Script generation failed: {ex.Message}");
                    _log($"[{_botLabel}] cycle={cycle} generate_script_error | {ex.Message}");
                    _generationLog($"[{_botLabel}] cycle={cycle} do_call_error | {ex.GetType().Name}: {ex.Message}");
                    await Task.Delay(3000, ct);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(script))
                {
                    SetStatus("Empty script generated. Retrying in 2s...");
                    await Task.Delay(2000, ct);
                    continue;
                }

                // 5. Set and execute the script.
                SetStatus($"Cycle {cycle}: Running script...");
                _log($"[{_botLabel}] cycle={cycle} set_and_execute | script_len={script.Length}");

                try
                {
                    await _api.SendRuntimeCommandAsync(_prayerSessionId, RuntimeCommandNames.SetScript, script);
                    await _api.SendRuntimeCommandAsync(_prayerSessionId, RuntimeCommandNames.ExecuteScript);
                    lastResult = $"Script executed (cycle {cycle})";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SetStatus($"Execute error: {ex.Message}");
                    _log($"[{_botLabel}] cycle={cycle} execute_error | {ex.Message}");
                    await Task.Delay(2000, ct);
                    continue;
                }
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
                conversation += $"\n<|start_header_id|>user<|end_header_id|>\nRespond with state(\"<path>\") or do(\"<instruction>\"). Keep instruction 1-2 sentences max.<|eot_id|>";
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
            $"  do(\"<instruction>\") — decide your next action (e.g. 'mine iron at current asteroid')\n\n" +
            $"Rules: call state(...) at most 3 times, then output exactly one do(...). Instruction must be 1-2 sentences max. Be concise and in-character.\n" +
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
        sb.Append("Last result: ").AppendLine(lastResult);

        if (runtime.ExecutionStatusLines?.Count > 0)
        {
            var lastLine = runtime.ExecutionStatusLines[^1];
            if (!string.IsNullOrWhiteSpace(lastLine))
                sb.Append("Last status: ").AppendLine(lastLine);
        }

        sb.AppendLine();
        sb.AppendLine("Respond with state(\"<path>\") to inspect, or do(\"<instruction>\") to act. Keep instruction 1-2 sentences max.");
        return sb.ToString();
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

    // ── Halt detection ────────────────────────────────────────────────────────

    private async Task<bool> WaitForHaltAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(MaxHaltWaitSeconds);
        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            try
            {
                var snapshot = await _api.GetRuntimeSnapshotAsync(_prayerSessionId, ct);
                if (IsIdleBySnapshot(snapshot))
                    return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Fallback to local state cache if snapshot endpoint is temporarily unavailable.
                _log($"[{_botLabel}] wait_for_halt_snapshot_error | {ex.GetType().Name}: {ex.Message}");
                var state = _getState();
                if (IsHaltedByState(state))
                    return true;
            }

            await Task.Delay(HaltPollWaitMs, ct);
        }

        return false;
    }

    private static bool IsIdleBySnapshot(Prayer.Contracts.RuntimeSnapshotResponse snapshot)
    {
        var host = snapshot?.Snapshot;
        if (host == null)
            return false;

        if (host.HasActiveCommand)
            return false;

        if (host.CurrentScriptLine != null)
            return false;

        return host.IsHalted;
    }

    private static bool IsHaltedByState(AppPrayerRuntimeState? state)
    {
        if (state == null)
            return true; // No state yet — treat as halted so we can get state first.

        // Script is running if CurrentScriptLine has a value.
        return state.CurrentScriptLine == null;
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
