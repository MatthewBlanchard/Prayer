using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public abstract class AutoDockSingleTurnCommand : ISingleTurnCommand
{
    public abstract string Name { get; }
    protected virtual bool RequiresStation => false;

    public abstract string BuildHelp(GameState state);
    protected abstract bool IsAvailableWhenDocked(GameState state);
    protected abstract Task<CommandExecutionResult?> ExecuteDockedAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state);

    public bool IsAvailable(GameState state)
    {
        if (state.Docked)
            return IsDockLocationValid(state) && IsAvailableWhenDocked(state);

        return AutoDockCommandState.HasAutoDockPath(state, RequiresStation);
    }

    public async Task<CommandExecutionResult?> ExecuteAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        var ensured = await AutoDockCommandState.EnsureDockedReadyAsync(
            client,
            state,
            Name,
            RequiresStation);
        if (!ensured.Ok)
        {
            return new CommandExecutionResult
            {
                ResultMessage = ensured.ErrorMessage
            };
        }

        return await ExecuteDockedAsync(client, cmd, ensured.State);
    }

    protected static GameState GetLatestStateOrFallback(IRuntimeTransport client, GameState fallback)
        => AutoDockCommandState.GetLatestStateOrFallback(client, fallback);

    private bool IsDockLocationValid(GameState state)
        => !RequiresStation || state.CurrentPOI.IsStation;
}

public abstract class AutoDockMultiTurnCommand : IMultiTurnCommand
{
    private string? _startFailureMessage;

    public abstract string Name { get; }
    protected virtual bool RequiresStation => false;

    public abstract string BuildHelp(GameState state);
    protected abstract bool IsAvailableWhenDocked(GameState state);
    protected abstract Task<CommandExecutionResult?> StartDockedAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state);
    protected abstract Task<(bool finished, CommandExecutionResult? result)> ContinueDockedAsync(
        IRuntimeTransport client,
        GameState state);

    public bool IsAvailable(GameState state)
    {
        if (state.Docked)
            return IsDockLocationValid(state) && IsAvailableWhenDocked(state);

        return AutoDockCommandState.HasAutoDockPath(state, RequiresStation);
    }

    public async Task<CommandExecutionResult?> StartAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        _startFailureMessage = null;
        var ensured = await AutoDockCommandState.EnsureDockedReadyAsync(
            client,
            state,
            Name,
            RequiresStation);
        if (!ensured.Ok)
        {
            _startFailureMessage = ensured.ErrorMessage;
            return null;
        }

        return await StartDockedAsync(client, cmd, ensured.State);
    }

    public async Task<(bool finished, CommandExecutionResult? result)> ContinueAsync(
        IRuntimeTransport client,
        GameState state)
    {
        if (!string.IsNullOrWhiteSpace(_startFailureMessage))
        {
            var message = _startFailureMessage;
            _startFailureMessage = null;
            return (true, new CommandExecutionResult
            {
                ResultMessage = message
            });
        }

        return await ContinueDockedAsync(client, state);
    }

    private bool IsDockLocationValid(GameState state)
        => !RequiresStation || state.CurrentPOI.IsStation;
}

internal static class AutoDockCommandState
{
    public readonly record struct EnsureDockedResult(
        bool Ok,
        GameState State,
        string ErrorMessage);

    public static GameState GetLatestStateOrFallback(IRuntimeTransport client, GameState fallback)
    {
        try
        {
            return client.GetLatestState();
        }
        catch
        {
            return fallback;
        }
    }

    public static bool HasAutoDockPath(GameState state, bool requiresStation)
        => FindDockTarget(state, requiresStation) != null;

    public static async Task<EnsureDockedResult> EnsureDockedReadyAsync(
        IRuntimeTransport client,
        GameState state,
        string commandName,
        bool requiresStation)
    {
        var effectiveState = GetLatestStateOrFallback(client, state);
        var target = FindDockTarget(effectiveState, requiresStation);
        if (target == null)
        {
            var reason = requiresStation
                ? "no station base is available in the current system"
                : "no dockable base is available in the current system";
            return new EnsureDockedResult(false, effectiveState,
                $"Cannot auto-dock for `{commandName}`: {reason}.");
        }

        if (!string.Equals(effectiveState.CurrentPOI.Id, target.Id, StringComparison.Ordinal))
        {
            if (effectiveState.Docked)
                await client.ExecuteCommandAsync("undock");

            JsonElement travelResponse = (await client.ExecuteCommandAsync(
                "travel",
                new { target_poi = target.Id })).Payload;
            if (CommandJson.TryGetError(travelResponse, out var travelCode, out var travelError))
            {
                var detail = !string.IsNullOrWhiteSpace(travelError)
                    ? travelError
                    : travelCode ?? "travel failed";
                return new EnsureDockedResult(false, effectiveState,
                    $"Cannot auto-dock for `{commandName}`: failed to travel to `{target.Id}` ({detail}).");
            }

            effectiveState = GetLatestStateOrFallback(client, effectiveState);
        }

        if (!effectiveState.Docked)
        {
            JsonElement dockResponse = (await client.ExecuteCommandAsync("dock")).Payload;
            string? dockMessage = CommandJson.TryGetResultMessage(dockResponse);

            effectiveState = GetLatestStateOrFallback(client, effectiveState);
            if (!effectiveState.Docked)
            {
                return new EnsureDockedResult(false, effectiveState,
                    string.IsNullOrWhiteSpace(dockMessage)
                        ? $"Auto-dock failed for `{commandName}`."
                        : $"Auto-dock failed for `{commandName}`: {dockMessage}");
            }
        }

        if (requiresStation && !effectiveState.CurrentPOI.IsStation)
        {
            return new EnsureDockedResult(false, effectiveState,
                $"Auto-dock failed for `{commandName}`: docked location is not a station.");
        }

        return new EnsureDockedResult(true, effectiveState, "");
    }

    private static POIInfo? FindDockTarget(GameState state, bool requiresStation)
    {
        if (IsEligibleDockPoi(state.CurrentPOI, requiresStation))
            return state.CurrentPOI;

        return state.POIs.FirstOrDefault(p => IsEligibleDockPoi(p, requiresStation));
    }

    private static bool IsEligibleDockPoi(POIInfo poi, bool requiresStation)
        => poi.HasBase && (!requiresStation || poi.IsStation);
}
