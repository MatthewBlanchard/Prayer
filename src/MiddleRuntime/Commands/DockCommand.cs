using System.Threading.Tasks;

public class DockCommand : ISingleTurnCommand
{
    public string Name => "dock";

    public bool IsAvailable(GameState state)
        => !state.Docked &&
           AutoDockCommandState.HasAutoDockPath(state, requiresStation: false);
    public string BuildHelp(GameState state)
        => "- dock → dock at current POI, or auto-travel to a nearby dockable POI and dock";

    public async Task<CommandExecutionResult?> ExecuteAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        var initial = AutoDockCommandState.GetLatestStateOrFallback(client, state);
        string initialPoiId = initial.CurrentPOI?.Id ?? "";
        bool startedDocked = initial.Docked;

        var ensured = await AutoDockCommandState.EnsureDockedReadyAsync(
            client,
            initial,
            Name,
            requiresStation: false);

        if (!ensured.Ok)
        {
            return new CommandExecutionResult
            {
                ResultMessage = ensured.ErrorMessage
            };
        }

        var finalState = ensured.State;
        string finalPoiId = finalState.CurrentPOI?.Id ?? "";
        bool changedPoi = !string.Equals(initialPoiId, finalPoiId, System.StringComparison.Ordinal);

        return new CommandExecutionResult
        {
            ResultMessage = startedDocked
                ? $"Already docked at `{finalPoiId}`."
                : (changedPoi
                    ? $"Auto-docked at `{finalPoiId}`."
                    : $"Docked at `{finalPoiId}`.")
        };
    }
}

// =====================================================
// UNDOCK
// =====================================================
