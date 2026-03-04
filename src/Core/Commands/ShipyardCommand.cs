using System.Threading.Tasks;

public class ShipyardCommand : AutoDockSingleTurnCommand
{
    public override string Name => "shipyard";

    protected override bool RequiresStation => true;

    protected override bool IsAvailableWhenDocked(GameState state)
        => state.Docked &&
           state.CurrentPOI.IsStation;
    public override string BuildHelp(GameState state)
        => "- shipyard → show shipyard data";

    protected override Task<CommandExecutionResult?> ExecuteDockedAsync(
        SpaceMoltHttpClient client,
        CommandResult cmd,
        GameState state)
    {
        return Task.FromResult<CommandExecutionResult?>(new CommandExecutionResult
        {
            ResultMessage = "Shipyard data is always available while docked."
        });
    }
}
