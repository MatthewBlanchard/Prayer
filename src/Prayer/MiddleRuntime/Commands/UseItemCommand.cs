using System.Text.Json;
using System.Threading.Tasks;

public class UseItemCommand : ISingleTurnCommand, IDslCommandGrammar
{
    public string Name => "use_item";

    public bool IsAvailable(GameState state)
        => state.Ship.Cargo.Count > 0;

    public string BuildHelp(GameState state)
        => "- use_item <item_id> [quantity] → consume item from cargo (fuel cells, repair kits, shield cells)";

    public DslCommandSyntax GetDslSyntax() => new(
        ArgSpecs: new[]
        {
            new DslArgumentSpec(DslArgKind.Item, Required: true),
            new DslArgumentSpec(DslArgKind.Integer, Required: false, DefaultValue: "1")
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
                ResultMessage = "use_item requires an item_id argument.",
                HaltScript = true
            };
        }

        int quantity = 1;
        if (!string.IsNullOrWhiteSpace(cmd.Arg2) && int.TryParse(cmd.Arg2, out var parsed) && parsed > 0)
            quantity = parsed;

        JsonElement response = (await client.ExecuteCommandAsync(
            "use_item",
            new { item_id = cmd.Arg1, quantity })).Payload;

        if (CommandJson.TryGetError(response, out var code, out var error))
        {
            return new CommandExecutionResult
            {
                ResultMessage = $"use_item failed: {error ?? code ?? "unknown error"}",
                HaltScript = true
            };
        }

        return new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response) ?? $"Used {quantity}x {cmd.Arg1}."
        };
    }
}
