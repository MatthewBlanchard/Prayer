using System.Threading.Tasks;

public class HangarCommand : AutoDockSingleTurnCommand
{
    public override string Name => "hangar";

    protected override bool RequiresStation => true;

    protected override bool IsAvailableWhenDocked(GameState state)
        => state.Docked &&
           state.CurrentPOI.IsStation;
    public override string BuildHelp(GameState state)
        => "- hangar → manage owned ships at station";

    protected override Task<CommandExecutionResult?> ExecuteDockedAsync(
        SpaceMoltHttpClient client,
        CommandResult cmd,
        GameState state)
    {
        return Task.FromResult<CommandExecutionResult?>(new CommandExecutionResult
        {
            ResultMessage = "Hangar actions are always available while docked."
        });
    }
}
