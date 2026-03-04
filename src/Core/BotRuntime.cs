using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

public sealed class BotRuntime
{
    private readonly string _label;
    private readonly SpaceMoltAgent _agent;
    private readonly SpaceMoltHttpClient _client;
    private readonly ChannelReader<string> _commandReader;
    private readonly ChannelReader<string> _controlInputReader;
    private readonly ChannelReader<string> _generateScriptReader;
    private readonly ChannelReader<bool> _saveExampleReader;
    private readonly Func<bool> _isLoopEnabled;
    private readonly Func<GameState?> _getLatestState;
    private readonly Action<GameState> _setLatestState;
    private readonly Func<DateTime> _getLastHaltedSnapshotAt;
    private readonly Action<DateTime> _setLastHaltedSnapshotAt;
    private readonly Action<GameState> _publishSnapshot;
    private readonly Action<string> _publishStatus;
    private readonly Action<string> _log;
    private readonly Action<string> _triggerGlobalStop;
    private readonly Func<string, CommandResult> _parseCommand;
    private readonly int _scriptGenerationMaxAttempts;

    public BotRuntime(
        string label,
        SpaceMoltAgent agent,
        SpaceMoltHttpClient client,
        ChannelReader<string> commandReader,
        ChannelReader<string> controlInputReader,
        ChannelReader<string> generateScriptReader,
        ChannelReader<bool> saveExampleReader,
        Func<bool> isLoopEnabled,
        Func<GameState?> getLatestState,
        Action<GameState> setLatestState,
        Func<DateTime> getLastHaltedSnapshotAt,
        Action<DateTime> setLastHaltedSnapshotAt,
        Action<GameState> publishSnapshot,
        Action<string> publishStatus,
        Action<string> log,
        Action<string> triggerGlobalStop,
        Func<string, CommandResult> parseCommand,
        int scriptGenerationMaxAttempts)
    {
        _label = label;
        _agent = agent;
        _client = client;
        _commandReader = commandReader;
        _controlInputReader = controlInputReader;
        _generateScriptReader = generateScriptReader;
        _saveExampleReader = saveExampleReader;
        _isLoopEnabled = isLoopEnabled;
        _getLatestState = getLatestState;
        _setLatestState = setLatestState;
        _getLastHaltedSnapshotAt = getLastHaltedSnapshotAt;
        _setLastHaltedSnapshotAt = setLastHaltedSnapshotAt;
        _publishSnapshot = publishSnapshot;
        _publishStatus = publishStatus;
        _log = log;
        _triggerGlobalStop = triggerGlobalStop;
        _parseCommand = parseCommand;
        _scriptGenerationMaxAttempts = scriptGenerationMaxAttempts;
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
                        var scriptState = _getLatestState() ?? _client.GetGameState();
                        _setLatestState(scriptState);

                        try
                        {
                            _agent.SetScript(newInput, scriptState, preserveAssociatedPrompt: true);
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

                        _agent.InterruptActiveCommand("Interrupted by script generation request");
                        _publishStatus($"[{_label}] Generating script");

                        var scriptState = _getLatestState() ?? _client.GetGameState();
                        _setLatestState(scriptState);

                        try
                        {
                            var generatedScript = await _agent.GenerateScriptFromUserInputAsync(
                                generationInput,
                                scriptState,
                                maxAttempts: _scriptGenerationMaxAttempts);
                            _agent.SetControlMode(ScriptMode.Instance, clearCurrentPlan: true);
                            _agent.SetScript(generatedScript, scriptState);
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

                    while (_commandReader.TryRead(out var commandInput))
                    {
                        await HandleDirectCommandAsync(commandInput);
                    }

                    if (_agent.IsHalted)
                    {
                        if (_isLoopEnabled() && !string.IsNullOrWhiteSpace(_agent.CurrentControlInput))
                        {
                            var scriptState = _getLatestState() ?? _client.GetGameState();
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
                            var haltedState = _client.GetGameState();
                            _setLatestState(haltedState);
                            _publishSnapshot(haltedState);
                            _setLastHaltedSnapshotAt(DateTime.UtcNow);
                        }

                        await Task.Delay(100, token);
                        continue;
                    }

                    var currentState = _client.GetGameState();
                    _setLatestState(currentState);
                    _publishSnapshot(currentState);

                    var result = await _agent.DecideAsync(currentState);
                    _publishSnapshot(currentState);

                    if (result != null)
                    {
                        string stepKey = BuildScriptStepKey(result);
                        try
                        {
                            await _agent.ExecuteAsync(_client, result, currentState);
                            scriptStepFailureCounts.Remove(stepKey);
                        }
                        catch (Exception ex)
                        {
                            if (_agent.CurrentControlModeKind == ControlModeKind.ScriptMode)
                            {
                                int failures = scriptStepFailureCounts.TryGetValue(stepKey, out var count)
                                    ? count + 1
                                    : 1;
                                scriptStepFailureCounts[stepKey] = failures;

                                if (failures < 3)
                                {
                                    _agent.RequeueScriptStep(result);
                                    _publishStatus($"[{_label}] Script step failed (attempt {failures}/3), retrying: {FormatCommand(result)} | {ex.Message}");
                                }
                                else
                                {
                                    scriptStepFailureCounts.Remove(stepKey);
                                    _publishStatus($"[{_label}] Script step failed after 3 attempts, skipping: {FormatCommand(result)} | {ex.Message}");
                                }

                                _publishSnapshot(currentState);
                                await Task.Delay(200, token);
                                continue;
                            }

                            throw;
                        }

                        try
                        {
                            var postActionState = _client.GetGameState();
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
                catch (RateLimitStopException ex)
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

    private async Task HandleDirectCommandAsync(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return;

        _agent.InterruptActiveCommand("Interrupted by user command");

        var state = _getLatestState() ?? _client.GetGameState();
        _setLatestState(state);

        var cmd = _parseCommand(input);
        await _agent.ExecuteAsync(_client, cmd, state);

        try
        {
            var postActionState = _client.GetGameState();
            postActionState = await TryAutoDockedMaintenanceAsync(
                postActionState,
                includeScriptRefuel: false);
            _setLatestState(postActionState);
            _publishSnapshot(postActionState);
        }
        catch
        {
            _publishSnapshot(state);
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

        if (includeScriptRefuel && _agent.CurrentControlModeKind == ControlModeKind.ScriptMode)
            current = await TryAutoRefuelBetweenScriptStepsAsync(current);

        return current;
    }

    private async Task<GameState> TryAutoWithdrawStationCreditsAsync(GameState state)
    {
        if (state.Shared.StorageCredits <= 0)
            return state;

        int storageCreditsBefore = state.Shared.StorageCredits;

        try
        {
            await _client.ExecuteAsync("withdraw_credits", new { amount = storageCreditsBefore });
            var refreshed = _client.GetGameState();
            int withdrawn = Math.Max(0, storageCreditsBefore - refreshed.Shared.StorageCredits);

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
                await _client.ExecuteAsync("complete_mission", new { mission_id = missionId });

            var refreshed = _client.GetGameState();
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
            await _client.ExecuteAsync("refuel", new { });
            var refreshed = _client.GetGameState();

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
