using System.Text.Json;
using System.Threading.Tasks;

public class UndockCommand : ISingleTurnCommand, IDslCommandGrammar
{
    public string Name => "undock";

    public bool IsAvailable(GameState state) => state.Docked;

    public string BuildHelp(GameState state)
        => "- undock → leave current station";

    public DslCommandSyntax GetDslSyntax() => new();

    public async Task<CommandExecutionResult?> ExecuteAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        if (!state.Docked)
        {
            return new CommandExecutionResult
            {
                ResultMessage = "Not docked."
            };
        }

        JsonElement response = (await client.ExecuteCommandAsync("undock")).Payload;

        return new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response) ?? "Undocked."
        };
    }
}
