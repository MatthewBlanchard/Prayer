using System.Text.Json;
using System.Threading.Tasks;

public class ScanCommand : ISingleTurnCommand, IDslCommandGrammar
{
    public string Name => "scan";

    public bool IsAvailable(GameState state)
        => !string.IsNullOrWhiteSpace(state.System);

    public string BuildHelp(GameState state)
        => "- scan <target_id> → scan a nearby ship";

    public DslCommandSyntax GetDslSyntax() => new(
        ArgSpecs: new[]
        {
            new DslArgumentSpec(DslArgType.Any, Required: true)
        });

    public async Task<CommandExecutionResult?> ExecuteAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        if (string.IsNullOrWhiteSpace(cmd.Arg1))
        {
            return new CommandExecutionResult
            {
                ResultMessage = "scan requires a target_id argument.",
                HaltScript = true
            };
        }

        JsonElement response = (await client.ExecuteCommandAsync(
            "scan",
            new { target_id = cmd.Arg1 })).Payload;

        return new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response) ?? $"Scanned {cmd.Arg1}."
        };
    }
}
