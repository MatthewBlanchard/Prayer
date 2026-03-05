using System.Threading.Tasks;

public class NextPageCommand : ISingleTurnCommand
{
    public string Name => "next";

    public bool IsAvailable(GameState state)
        => state.Docked && state.CurrentPOI.IsStation;
    public string BuildHelp(GameState state)
        => "- next page → move to next catalog page";

    public Task<CommandExecutionResult?> ExecuteAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        if (!string.Equals(cmd.Arg1, "page", System.StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<CommandExecutionResult?>(new CommandExecutionResult
            {
                ResultMessage = "Usage: next page."
            });
        }

        bool moved = client.MoveShipCatalogToNextPage(state.ShipCatalogue.TotalPages);
        return Task.FromResult<CommandExecutionResult?>(new CommandExecutionResult
        {
            ResultMessage = moved
                ? $"Moved to page {client.ShipCatalogPage}."
                : "Already on the last page."
        });
    }
}
