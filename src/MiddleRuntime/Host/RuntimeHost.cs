using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

public sealed class RuntimeHost : IRuntimeHost
{
    private const int ScriptStepRetryLimit = 10;
    private readonly string _label;
    private readonly SpaceMoltAgent _agent;
    private readonly IRuntimeTransport _transport;
    private readonly IRuntimeStateProvider _stateProvider;
    private readonly ChannelReader<string> _controlInputReader;
    private readonly ChannelReader<string> _generateScriptReader;
    private readonly ChannelReader<bool> _saveExampleReader;
    private readonly ChannelReader<bool> _haltNowReader;
    private readonly Func<bool> _isLoopEnabled;
    private readonly Func<GameState?> _getLatestState;
    private readonly Action<GameState> _setLatestState;
    private readonly Func<DateTime> _getLastHaltedSnapshotAt;
    private readonly Action<DateTime> _setLastHaltedSnapshotAt;
    private readonly Action<GameState> _publishSnapshot;
    private readonly Action<string> _publishStatus;
    private readonly Action<string> _log;
    private readonly Action<string> _triggerGlobalStop;
    private readonly int _scriptGenerationMaxAttempts;

    public RuntimeHost(
        string label,
        SpaceMoltAgent agent,
        IRuntimeTransport transport,
        IRuntimeStateProvider stateProvider,
        ChannelReader<string> controlInputReader,
        ChannelReader<string> generateScriptReader,
        ChannelReader<bool> saveExampleReader,
        ChannelReader<bool> haltNowReader,
        Func<bool> isLoopEnabled,
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
        _isLoopEnabled = isLoopEnabled;
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
        _agent.InterruptActiveCommand("Interrupted by direct set script");
        var state = _getLatestState();
        _agent.SetScript(script ?? string.Empty, state, preserveAssociatedPrompt);
        _agent.ActivateScriptControl();
        _publishStatus($"[{_label}] Script loaded and activated");

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

        _agent.InterruptActiveCommand("Interrupted by direct script generation request");
        _publishStatus($"[{_label}] Generating script");

        var state = _getLatestState() ?? await _stateProvider.GetLatestStateAsync();
        _setLatestState(state);

        var generatedScript = await _agent.GenerateScriptFromUserInputAsync(
            prompt,
            state,
            maxAttempts: _scriptGenerationMaxAttempts);

        _agent.ActivateScriptControl();
        _agent.SetScript(generatedScript, state);
        _publishStatus($"[{_label}] Generated script loaded and activated");
        _publishSnapshot(state);
        return generatedScript;
    }

    public bool Interrupt(string reason = "Interrupted")
    {
        return _agent.InterruptActiveCommand(reason);
    }

    public void Halt(string reason = "Halted")
    {
        _agent.Halt(reason);
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
                    while (_controlInputReader.TryRead(out var newInput))
                    {
                        if (string.IsNullOrWhiteSpace(newInput))
                            continue;

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

                    while (_generateScriptReader.TryRead(out var generationInput))
                    {
                        if (string.IsNullOrWhiteSpace(generationInput))
                            continue;

                        if (generationInput.Contains("(no active mission objectives)", StringComparison.OrdinalIgnoreCase))
                        {
                            _publishStatus($"[{_label}] No active mission objectives available to generate a script.");
                            continue;
                        }

                        _agent.InterruptActiveCommand("Interrupted by script generation request");
                        _publishStatus($"[{_label}] Generating script");

                        var scriptState = _getLatestState() ?? await _stateProvider.GetLatestStateAsync();
                        _setLatestState(scriptState);

                        try
                        {
                            var generatedScript = await _agent.GenerateScriptFromUserInputAsync(
                                generationInput,
                                scriptState,
                                maxAttempts: _scriptGenerationMaxAttempts);
                            _agent.ActivateScriptControl();
                            _agent.SetScript(generatedScript, scriptState);
                            _publishStatus($"[{_label}] Generated script loaded and activated");
                        }
                        catch (FormatException ex)
                        {
                            _publishStatus($"[{_label}] Generated script parse error: {ex.Message}");
                        }
                        catch (Exception ex)
                        {
                            _publishStatus($"[{_label}] Script generation failed: {ex.Message}");
                        }

                        if (_getLatestState() != null)
                            _publishSnapshot(scriptState);
                    }

                    while (_saveExampleReader.TryRead(out _))
                    {
                        var saveResult = await _agent.AddCurrentScriptAsExampleAsync();
                        _publishStatus($"[{_label}] {saveResult.Message}");

                        var latestState = _getLatestState();
                        if (latestState != null)
                            _publishSnapshot(latestState);
                    }

                    while (_haltNowReader.TryRead(out _))
                    {
                        _agent.InterruptActiveCommand("Interrupted by user halt");

                        var haltState = _getLatestState() ?? await _stateProvider.GetLatestStateAsync();
                        _setLatestState(haltState);

                        try
                        {
                            _agent.SetScript(string.Empty, haltState, preserveAssociatedPrompt: true);
                        }
                        catch
                        {
                            // Never block force-halt because script clear failed.
                        }

                        _agent.Halt("Halted by user");
                        _publishStatus($"[{_label}] Halted by user");
                        _publishSnapshot(haltState);
                    }

                    if (_agent.IsHalted)
                    {
                        if (_isLoopEnabled() && !string.IsNullOrWhiteSpace(_agent.CurrentControlInput))
                        {
                            var scriptState = _getLatestState() ?? await _stateProvider.GetLatestStateAsync();
                            _setLatestState(scriptState);

                            try
                            {
                                _agent.SetScript(_agent.CurrentControlInput, scriptState);
                                continue;
                            }
                            catch (FormatException ex)
                            {
                                _publishStatus($"[{_label}] Loop restart failed: {ex.Message}");
                            }
                        }

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
                            await _agent.ExecuteAsync(_transport, result, currentState);
                            scriptStepFailureCounts.Remove(stepKey);
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
                            var postActionState = await _stateProvider.GetLatestStateAsync();
                            postActionState = await TryAutoDockedMaintenanceAsync(
                                postActionState,
                                includeScriptRefuel: true);
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
                catch (OperationCanceledException)
                {
                    throw;
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
        if (!(state.Docked && state.CurrentPOI.IsStation))
            return state;

        var current = state;
        current = await TryAutoWithdrawStationCreditsAsync(current);
        current = await TryAutoCompleteMissionsAsync(current);

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

        try
        {
            foreach (var missionId in completableMissionIds)
                await _transport.ExecuteCommandAsync("complete_mission", new { mission_id = missionId });

            var refreshed = await _stateProvider.GetLatestStateAsync();
            int completedCount = Math.Max(0, state.ActiveMissions.Length - refreshed.ActiveMissions.Length);

            if (completedCount > 0)
                _publishStatus($"[{_label}] Auto-completed {completedCount} mission(s)");

            return refreshed;
        }
        catch
        {
            return state;
        }
    }

    private async Task<GameState> TryAutoRefuelBetweenScriptStepsAsync(GameState state)
    {
        if (state.Fuel >= state.MaxFuel || state.Credits <= 0)
            return state;

        int fuelBefore = state.Fuel;

        try
        {
            await _transport.ExecuteCommandAsync("refuel", new { });
            var refreshed = await _stateProvider.GetLatestStateAsync();

            if (refreshed.Fuel > fuelBefore)
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
}
