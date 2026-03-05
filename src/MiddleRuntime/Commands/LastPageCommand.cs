using System.Threading.Tasks;

public class LastPageCommand : ISingleTurnCommand
{
    public string Name => "last";

    public bool IsAvailable(GameState state)
        => state.Docked && state.CurrentPOI.IsStation;
    public string BuildHelp(GameState state)
        => "- last page → move to previous catalog page";

    public Task<CommandExecutionResult?> ExecuteAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        if (!string.Equals(cmd.Arg1, "page", System.StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<CommandExecutionResult?>(new CommandExecutionResult
            {
                ResultMessage = "Usage: last page."
            });
        }

        bool moved = client.MoveShipCatalogToLastPage();
        return Task.FromResult<CommandExecutionResult?>(new CommandExecutionResult
        {
            ResultMessage = moved
                ? $"Moved to page {client.ShipCatalogPage}."
                : "Already on the first page."
        });
    }
}
