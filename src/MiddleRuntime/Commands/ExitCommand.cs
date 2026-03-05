using System.Threading.Tasks;

public class ExitCommand : ISingleTurnCommand
{
    public string Name => "exit";

    public bool IsAvailable(GameState state)
        => state.Docked && state.CurrentPOI.IsStation;
    public string BuildHelp(GameState state)
        => "- exit → back to your ship";

    public Task<CommandExecutionResult?> ExecuteAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        return Task.FromResult<CommandExecutionResult?>(new CommandExecutionResult
        {
            ResultMessage = "Already in unified terminal mode."
        });
    }
}
