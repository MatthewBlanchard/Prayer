using System.Threading.Tasks;

public class TradeCommand : AutoDockSingleTurnCommand, IDslCommandGrammar
{
    public override string Name => "trade";
    public DslCommandSyntax GetDslSyntax() => new();

    protected override bool RequiresStation => true;

    protected override bool IsAvailableWhenDocked(GameState state)
        => state.Docked &&
           state.CurrentPOI.IsStation;
    public override string BuildHelp(GameState state)
        => "- trade → use trading terminal";

    protected override Task<CommandExecutionResult?> ExecuteDockedAsync(
        SpaceMoltHttpClient client,
        CommandResult cmd,
        GameState state)
    {
        return Task.FromResult<CommandExecutionResult?>(new CommandExecutionResult
        {
            ResultMessage = "Trading terminal is always available while docked."
        });
    }
}
