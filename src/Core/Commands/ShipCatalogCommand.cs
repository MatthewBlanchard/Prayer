using System.Threading.Tasks;

public class ShipCatalogCommand : AutoDockSingleTurnCommand
{
    public override string Name => "ship_catalog";

    protected override bool RequiresStation => true;

    protected override bool IsAvailableWhenDocked(GameState state)
        => state.Docked &&
           state.CurrentPOI.IsStation;
    public override string BuildHelp(GameState state)
        => "- ship_catalog → reset catalog to page 1";

    protected override Task<CommandExecutionResult?> ExecuteDockedAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        client.ResetShipCatalogPage();

        return Task.FromResult<CommandExecutionResult?>(new CommandExecutionResult
        {
            ResultMessage = "Viewing ship catalog page 1."
        });
    }
}
