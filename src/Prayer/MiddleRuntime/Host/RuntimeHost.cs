using System;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

public sealed class RuntimeHost : IRuntimeHost
{
    private const int ScriptStepRetryLimit = 10;
    private readonly object _activeOperationLock = new();
    private readonly string _label;
    private readonly SpaceMoltAgent _agent;
    private readonly IRuntimeTransport _transport;
    private readonly IRuntimeStateProvider _stateProvider;
    private readonly ChannelReader<RuntimeControlInputRequest> _controlInputReader;
    private readonly ChannelReader<string> _generateScriptReader;
    private readonly ChannelReader<bool> _saveExampleReader;
    private readonly ChannelReader<RuntimeHaltRequest> _haltNowReader;
    private readonly Func<GameState?> _getLatestState;
    private readonly Action<GameState> _setLatestState;
    private readonly Func<DateTime> _getLastHaltedSnapshotAt;
    private readonly Action<DateTime> _setLastHaltedSnapshotAt;
    private readonly Action<GameState> _publishSnapshot;
    private readonly Action<string> _publishStatus;
    private readonly Action<string> _log;
    private readonly Action<string> _triggerGlobalStop;
    private readonly int _scriptGenerationMaxAttempts;
    private CancellationTokenSource? _activeOperationCts;

    public RuntimeHost(
        string label,
        SpaceMoltAgent agent,
        IRuntimeTransport transport,
        IRuntimeStateProvider stateProvider,
        ChannelReader<RuntimeControlInputRequest> controlInputReader,
        ChannelReader<string> generateScriptReader,
        ChannelReader<bool> saveExampleReader,
        ChannelReader<RuntimeHaltRequest> haltNowReader,
        Func<GameState?> getLatestState,
        Action<GameState> setLatestState,
        Func<DateTime> getLastHaltedSnapshotAt,
        Action<DateTime> setLastHaltedSnapshotAt,
        Action<GameState> publishSnapshot,
        Action<string> publishStatus,
        Action<string> log,
        Action<string> triggerGlobalStop,
        int scriptGenerationMaxAttempts)
    {
        _label = label;
        _agent = agent;
        _transport = transport;
        _stateProvider = stateProvider;
        _controlInputReader = controlInputReader;
        _generateScriptReader = generateScriptReader;
        _saveExampleReader = saveExampleReader;
        _haltNowReader = haltNowReader;
        _getLatestState = getLatestState;
        _setLatestState = setLatestState;
        _getLastHaltedSnapshotAt = getLastHaltedSnapshotAt;
        _setLastHaltedSnapshotAt = setLastHaltedSnapshotAt;
        _publishSnapshot = publishSnapshot;
        _publishStatus = publishStatus;
        _log = log;
        _triggerGlobalStop = triggerGlobalStop;
        _scriptGenerationMaxAttempts = scriptGenerationMaxAttempts;
    }

    public void SetScript(string script, bool preserveAssociatedPrompt = true)
    {
        _log($"bot_worker | {_label} | set_script | interrupt_active_command");
        _agent.InterruptActiveCommand("Interrupted by direct set script");
        var state = _getLatestState();
        _agent.SetScript(script ?? string.Empty, state, preserveAssociatedPrompt);
        _agent.ActivateScriptControl();
        _publishStatus($"[{_label}] Script loaded and activated");
        _log($"bot_worker | {_label} | set_script | loaded_and_activated");

        if (state != null)
            _publishSnapshot(state);
    }

    public void GenerateScript(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return;

        _ = GenerateScriptAsync(prompt);
    }

    public async Task<string?> GenerateScriptAsync(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return null;

        if (prompt.Contains("(no active mission objectives)", StringComparison.OrdinalIgnoreCase))
        {
            _publishStatus($"[{_label}] No active mission objectives available to generate a script.");
            return null;
        }

        _publishStatus($"[{_label}] Generating script draft");

        var state = _getLatestState() ?? await _stateProvider.GetLatestStateAsync();
        _setLatestState(state);

        string generatedScript;
        try
        {
            generatedScript = await RunWithCancellableOperationAsync(
                operationToken => _agent.GenerateScriptFromUserInputAsync(
                    prompt,
                    state,
                    maxAttempts: _scriptGenerationMaxAttempts,
                    cancellationToken: operationToken),
                CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            _publishStatus($"[{_label}] Script generation canceled");
            _log($"bot_worker | {_label} | script_generation_canceled | direct_request");
            return null;
        }

        _publishStatus($"[{_label}] Script draft generated");
        _publishSnapshot(state);
        return generatedScript;
    }

    public bool Interrupt(string reason = "Interrupted")
    {
        var interrupted = _agent.InterruptActiveCommand(reason);
        _log($"bot_worker | {_label} | interrupt | reason={reason} | interrupted={interrupted}");
        return interrupted;
    }

    public void Halt(string reason = "Halted")
    {
        _log($"bot_worker | {_label} | halt | reason={reason}");
        _agent.Halt(reason);
    }

    public void RequestHaltNow(string reason = "unspecified")
    {
        _log($"bot_worker | {_label} | request_halt_now | reason={reason}");
        CancelActiveOperation();
    }

    public RuntimeHostSnapshot GetSnapshot()
    {
        return new RuntimeHostSnapshot(
            _agent.IsHalted,
            _agent.HasActiveCommand,
            _agent.CurrentScriptLine,
            _agent.CurrentControlInput);
    }

    public Task TickAsync(CancellationToken token = default)
    {
        return Task.CompletedTask;
    }

    public async Task RunAsync(CancellationToken token)
    {
        var scriptStepFailureCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (await HandlePendingHaltsAsync(token))
                        continue;

                    if (_controlInputReader.TryRead(out var controlInput))
                    {
                        var newInput = controlInput.Script;
                        if (string.IsNullOrWhiteSpace(newInput))
                        {
                            // Skip empty updates quickly so halt can be serviced in the next loop pass.
                        }
                        else
                        {
                            LogControlInputRequest(controlInput);
                            _agent.InterruptActiveCommand("Interrupted by control input update");
                            var scriptState = _getLatestState() ?? await _stateProvider.GetLatestStateAsync();
                            _setLatestState(scriptState);

                            try
                            {
                                _agent.SetScript(newInput, scriptState, preserveAssociatedPrompt: true);
                                _agent.ActivateScriptControl();
                                _publishStatus($"[{_label}] Script loaded and activated");
                            }
                            catch (FormatException ex)
                            {
                                _publishStatus($"[{_label}] Script parse error: {ex.Message}");
                            }

                            if (_getLatestState() != null)
                                _publishSnapshot(scriptState);
                        }
                    }

                    if (await HandlePendingHaltsAsync(token))
                        continue;

                    if (_generateScriptReader.TryRead(out var generationInput))
                    {
                        if (string.IsNullOrWhiteSpace(generationInput))
                        {
                            // Skip empty generation requests quickly so halt can be serviced in the next loop pass.
                        }
                        else
                        {
                            if (generationInput.Contains("(no active mission objectives)", StringComparison.OrdinalIgnoreCase))
                            {
                                _publishStatus($"[{_label}] No active mission objectives available to generate a script.");
                            }
                            else
                            {
                                _log($"bot_worker | {_label} | script_generation_requested | interrupt_active_command");
                                _agent.InterruptActiveCommand("Interrupted by script generation request");
                                _publishStatus($"[{_label}] Generating script");

                                var scriptState = _getLatestState() ?? await _stateProvider.GetLatestStateAsync();
                                _setLatestState(scriptState);

                                try
                                {
                                    var generatedScript = await RunWithCancellableOperationAsync(
                                        operationToken => _agent.GenerateScriptFromUserInputAsync(
                                            generationInput,
                                            scriptState,
                                            maxAttempts: _scriptGenerationMaxAttempts,
                                            cancellationToken: operationToken),
                                        token);
                                    _agent.ActivateScriptControl();
                                    _agent.SetScript(generatedScript, scriptState);
                                    _publishStatus($"[{_label}] Generated script loaded and activated");
                                }
                                catch (FormatException ex)
                                {
                                    _publishStatus($"[{_label}] Generated script parse error: {ex.Message}");
                                }
                                catch (OperationCanceledException)
                                {
                                    _publishStatus($"[{_label}] Script generation canceled");
                                    _log($"bot_worker | {_label} | script_generation_canceled | queued_request");
                                }
                                catch (Exception ex)
                                {
                                    _publishStatus($"[{_label}] Script generation failed: {ex.Message}");
                                }

                                if (_getLatestState() != null)
                                    _publishSnapshot(scriptState);
                            }
                        }
                    }

                    if (await HandlePendingHaltsAsync(token))
                        continue;

                    if (_saveExampleReader.TryRead(out _))
                    {
                        var saveResult = await _agent.AddCurrentScriptAsExampleAsync();
                        _publishStatus($"[{_label}] {saveResult.Message}");

                        var latestState = _getLatestState();
                        if (latestState != null)
                            _publishSnapshot(latestState);
                    }

                    if (await HandlePendingHaltsAsync(token))
                        continue;

                    if (_agent.IsHalted)
                    {
                        if (_getLatestState() == null ||
                            DateTime.UtcNow - _getLastHaltedSnapshotAt() > TimeSpan.FromSeconds(1))
                        {
                            var haltedState = await _stateProvider.GetLatestStateAsync();
                            _setLatestState(haltedState);
                            _publishSnapshot(haltedState);
                            _setLastHaltedSnapshotAt(DateTime.UtcNow);
                        }

                        await Task.Delay(100, token);
                        continue;
                    }

                    var currentState = await _stateProvider.GetLatestStateAsync();
                    _setLatestState(currentState);
                    _publishSnapshot(currentState);

                    var result = await _agent.DecideAsync(currentState);
                    _publishSnapshot(currentState);

                    if (result != null)
                    {
                        string stepKey = BuildScriptStepKey(result);
                        _publishStatus($"[{_label}] Executing {FormatCommand(result)}");
                        try
                        {
                            await RunWithCancellableOperationAsync(
                                _ => _agent.ExecuteAsync(_transport, result, currentState),
                                token);
                            scriptStepFailureCounts.Remove(stepKey);
                        }
                        catch (OperationCanceledException)
                        {
                            _publishStatus($"[{_label}] Command canceled by halt");
                            _log($"bot_worker | {_label} | command_canceled_by_halt | command={FormatCommand(result)}");
                            _publishSnapshot(currentState);
                            continue;
                        }
                        catch (Exception ex)
                        {
                            int failures = scriptStepFailureCounts.TryGetValue(stepKey, out var count)
                                ? count + 1
                                : 1;
                            scriptStepFailureCounts[stepKey] = failures;

                            if (failures < ScriptStepRetryLimit)
                            {
                                _agent.RequeueScriptStep(result);
                                _publishStatus($"[{_label}] Script step failed (attempt {failures}/{ScriptStepRetryLimit}), retrying: {FormatCommand(result)} | {ex.Message}");
                            }
                            else
                            {
                                scriptStepFailureCounts.Remove(stepKey);
                                _publishStatus($"[{_label}] Script step failed after {ScriptStepRetryLimit} attempts, skipping: {FormatCommand(result)} | {ex.Message}");
                            }

                            _publishSnapshot(currentState);
                            await Task.Delay(200, token);
                            continue;
                        }

                        try
                        {
                            var postActionState = await RunWithCancellableOperationAsync(
                                async _ =>
                                {
                                    var refreshed = await _stateProvider.GetLatestStateAsync();
                                    return await TryAutoDockedMaintenanceAsync(
                                        refreshed,
                                        includeScriptRefuel: true);
                                },
                                token);
                            _setLatestState(postActionState);
                            _publishSnapshot(postActionState);
                        }
                        catch
                        {
                            _publishSnapshot(currentState);
                        }
                    }
                    else
                    {
                        _publishSnapshot(currentState);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    _publishStatus($"[{_label}] In-flight operation canceled");
                    _log($"bot_worker | {_label} | in_flight_operation_canceled");
                    await Task.Delay(50, token);
                    continue;
                }
                catch (RuntimeRateLimitException ex)
                {
                    _publishStatus($"[{_label}] {ex.Message}");
                    _log($"bot_worker | {_label} | rate_limited_stop | {ex.Message}");
                    _triggerGlobalStop(ex.Message);
                    return;
                }
                catch (Exception ex)
                {
                    _publishStatus($"[{_label}] Bot loop error: {ex.Message}");
                    _log($"bot_worker | {_label} | loop_error | {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex}");
                    await Task.Delay(200, token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
            _log($"bot_worker | {_label} | run_loop_canceled | normal_shutdown");
        }
        catch (Exception ex)
        {
            _log($"bot_worker | {_label} | failed | {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string BuildScriptStepKey(CommandResult step)
    {
        return $"{step.SourceLine?.ToString() ?? "-"}|{step.Action}|{step.Arg1 ?? ""}|{step.Quantity?.ToString() ?? ""}";
    }

    private async Task<GameState> TryAutoDockedMaintenanceAsync(
        GameState state,
        bool includeScriptRefuel)
    {
        var current = state;
        current = await TryAutoCompleteMissionsAsync(current);

        if (!(current.Docked && current.CurrentPOI.IsStation))
            return current;

        current = await TryAutoWithdrawStationCreditsAsync(current);

        if (includeScriptRefuel)
            current = await TryAutoRefuelBetweenScriptStepsAsync(current);

        return current;
    }

    private async Task<GameState> TryAutoWithdrawStationCreditsAsync(GameState state)
    {
        if (state.StorageCredits <= 0)
            return state;

        int storageCreditsBefore = state.StorageCredits;

        try
        {
            await _transport.ExecuteCommandAsync("withdraw_credits", new { amount = storageCreditsBefore });
            var refreshed = await _stateProvider.GetLatestStateAsync();
            int withdrawn = Math.Max(0, storageCreditsBefore - refreshed.StorageCredits);

            if (withdrawn > 0)
                _publishStatus($"[{_label}] Auto-withdrew {withdrawn} station credits");

            return refreshed;
        }
        catch
        {
            return state;
        }
    }

    private async Task<GameState> TryAutoCompleteMissionsAsync(GameState state)
    {
        if (state.ActiveMissions == null || state.ActiveMissions.Length == 0)
            return state;

        var completableMissionIds = new List<string>();
        var seenMissionIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mission in state.ActiveMissions)
        {
            if (!mission.Completed)
                continue;

            string missionId = string.IsNullOrWhiteSpace(mission.MissionId)
                ? mission.Id
                : mission.MissionId;

            if (string.IsNullOrWhiteSpace(missionId))
                continue;

            if (seenMissionIds.Add(missionId))
                completableMissionIds.Add(missionId);
        }

        if (completableMissionIds.Count == 0)
            return state;

        int successfulCompletions = 0;
        int failedCompletions = 0;
        _log($"bot_worker | {_label} | auto_complete_mission_attempt_batch | count={completableMissionIds.Count}");
        foreach (var missionId in completableMissionIds)
        {
            _log($"bot_worker | {_label} | auto_complete_mission_attempt | mission_id={missionId}");
            try
            {
                await _transport.ExecuteCommandAsync("complete_mission", new { mission_id = missionId });
                successfulCompletions++;
            }
            catch (Exception ex)
            {
                failedCompletions++;
                _log($"bot_worker | {_label} | auto_complete_mission_failed | mission_id={missionId} | {ex.GetType().Name}: {ex.Message}");
                _publishStatus($"[{_label}] Auto-complete mission failed ({missionId}): {ex.Message}");
            }
        }

        if (successfulCompletions <= 0)
        {
            if (failedCompletions > 0)
            {
                _publishStatus($"[{_label}] Auto-complete attempted {failedCompletions} mission(s), none succeeded");
            }

            return state;
        }

        try
        {
            var refreshed = await _stateProvider.GetLatestStateAsync();
            int completedCount = Math.Max(0, state.ActiveMissions.Length - refreshed.ActiveMissions.Length);

            if (completedCount > 0)
                _publishStatus($"[{_label}] Auto-completed {completedCount} mission(s)");

            if (failedCompletions > 0)
                _publishStatus($"[{_label}] Auto-complete had {failedCompletions} failure(s); see logs");

            return refreshed;
        }
        catch
        {
            return state;
        }
    }

    private async Task<GameState> TryAutoRefuelBetweenScriptStepsAsync(GameState state)
    {
        if (state.Ship.Fuel >= state.Ship.MaxFuel || state.Credits <= 0)
            return state;

        int fuelBefore = state.Ship.Fuel;

        try
        {
            await _transport.ExecuteCommandAsync("refuel", new { });
            var refreshed = await _stateProvider.GetLatestStateAsync();

            if (refreshed.Ship.Fuel > fuelBefore)
                _publishStatus($"[{_label}] Auto-refueled between script steps");

            return refreshed;
        }
        catch
        {
            return state;
        }
    }

    private static string FormatCommand(CommandResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Arg1) && result.Quantity.HasValue)
            return $"{result.Action} {result.Arg1} {result.Quantity.Value}";

        if (!string.IsNullOrWhiteSpace(result.Arg1))
            return $"{result.Action} {result.Arg1}";

        return result.Action;
    }

    private async Task<bool> HandlePendingHaltsAsync(CancellationToken token)
    {
        bool handled = false;
        while (_haltNowReader.TryRead(out var request))
        {
            handled = true;
            _log($"bot_worker | {_label} | halt_now_received | kind={request.Kind}");
            CancelActiveOperation();
            var interruptReason = request.Kind == RuntimeHaltRequestKind.UserHalt
                ? "Interrupted by user halt"
                : "Interrupted by script restart";
            _agent.InterruptActiveCommand(interruptReason);

            var haltState = _getLatestState() ?? await _stateProvider.GetLatestStateAsync();
            _setLatestState(haltState);

            if (request.Kind == RuntimeHaltRequestKind.UserHalt)
            {
                _agent.Halt("Halted by user");
                _publishStatus($"[{_label}] Halted by user");
                _log($"bot_worker | {_label} | halted_by_user");
            }
            else
            {
                _publishStatus($"[{_label}] Restarting script");
                _log($"bot_worker | {_label} | restarting_script");
            }

            _publishSnapshot(haltState);
            token.ThrowIfCancellationRequested();
        }

        return handled;
    }

    private void CancelActiveOperation()
    {
        CancellationTokenSource? active;
        lock (_activeOperationLock)
        {
            active = _activeOperationCts;
        }

        if (active == null || active.IsCancellationRequested)
            return;

        try
        {
            _log($"bot_worker | {_label} | cancel_active_operation");
            active.Cancel();
        }
        catch
        {
            // Best effort only.
        }
    }

    private void LogControlInputRequest(RuntimeControlInputRequest request)
    {
        var source = string.IsNullOrWhiteSpace(request.Source)
            ? "unknown"
            : request.Source.Trim();
        var script = request.Script ?? string.Empty;
        var normalized = script.Replace("\r\n", "\n");

        int lines = 0;
        if (normalized.Length > 0)
            lines = 1 + normalized.Count(ch => ch == '\n');

        var preview = normalized.Replace("\n", "\\n");
        if (preview.Length > 200)
            preview = preview.Substring(0, 200) + "...";

        _log($"bot_worker | {_label} | script_update_requested | source={source} | chars={normalized.Length} | lines={lines} | interrupt_active_command | preview={preview}");
        _log($"bot_worker | {_label} | script_update_body | source={source}{Environment.NewLine}---SCRIPT---{Environment.NewLine}{normalized}{Environment.NewLine}---END_SCRIPT---");
    }

    private async Task<T> RunWithCancellableOperationAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken runtimeToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(runtimeToken);
        lock (_activeOperationLock)
        {
            _activeOperationCts = linkedCts;
        }

        try
        {
            using var _ = RuntimeOperationCancellationContext.Push(linkedCts.Token);
            return await action(linkedCts.Token);
        }
        finally
        {
            lock (_activeOperationLock)
            {
                if (ReferenceEquals(_activeOperationCts, linkedCts))
                    _activeOperationCts = null;
            }
        }
    }

    private async Task RunWithCancellableOperationAsync(
        Func<CancellationToken, Task> action,
        CancellationToken runtimeToken)
    {
        _ = await RunWithCancellableOperationAsync(async opToken =>
        {
            await action(opToken);
            return true;
        }, runtimeToken);
    }
}
