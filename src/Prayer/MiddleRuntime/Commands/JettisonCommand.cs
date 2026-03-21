using System.Text.Json;
using System.Threading.Tasks;

public class JettisonCommand : ISingleTurnCommand, IDslCommandGrammar
{
    public string Name => "jettison";

    public bool IsAvailable(GameState state)
        => state.Ship.Cargo.Count > 0;

    public string BuildHelp(GameState state)
        => "- jettison <item_id> <quantity> → dump cargo into space (creates lootable container)";

    public DslCommandSyntax GetDslSyntax() => new(
        ArgSpecs: new[]
        {
            new DslArgumentSpec(DslArgType.ItemId, Required: true),
            new DslArgumentSpec(DslArgType.Integer, Required: true)
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
                ResultMessage = "jettison requires item_id and quantity arguments.",
                HaltScript = true
            };
        }

        int quantity = cmd.Quantity is > 0 ? cmd.Quantity.Value : 1;

        JsonElement response = (await client.ExecuteCommandAsync(
            "jettison",
            new { item_id = cmd.Arg1, quantity })).Payload;

        if (CommandJson.TryGetError(response, out var code, out var error))
        {
            return new CommandExecutionResult
            {
                ResultMessage = $"Jettison failed: {error ?? code ?? "unknown error"}",
                HaltScript = true
            };
        }

        return new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response) ?? $"Jettisoned {quantity}x {cmd.Arg1}."
        };
    }
}
