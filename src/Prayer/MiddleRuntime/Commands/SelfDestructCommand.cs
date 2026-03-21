using System.Text.Json;
using System.Threading.Tasks;

public class SelfDestructCommand : ISingleTurnCommand, IDslCommandGrammar
{
    public string Name => "self_destruct";

    public bool IsAvailable(GameState state) => true;

    public string BuildHelp(GameState state)
        => "- self_destruct → destroy your ship";

    public DslCommandSyntax GetDslSyntax() => new();

    public async Task<CommandExecutionResult?> ExecuteAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        JsonElement response = (await client.ExecuteCommandAsync("self_destruct")).Payload;

        return new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response)
        };
    }
}
