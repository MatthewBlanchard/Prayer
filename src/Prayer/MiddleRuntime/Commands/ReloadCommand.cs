using System.Text.Json;
using System.Threading.Tasks;

public class ReloadCommand : ISingleTurnCommand, IDslCommandGrammar
{
    public string Name => "reload";

    public bool IsAvailable(GameState state) => true;

    public string BuildHelp(GameState state)
        => "- reload <weapon_instance_id> <ammo_item_id> → reload weapon magazine from cargo ammo";

    public DslCommandSyntax GetDslSyntax() => new(
        ArgSpecs: new[]
        {
            new DslArgumentSpec(DslArgType.Any, Required: true),
            new DslArgumentSpec(DslArgType.ItemId, Required: true)
        });

    public async Task<CommandExecutionResult?> ExecuteAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        var ammoItemId = cmd.Args.Count > 1 ? cmd.Args[1] : null;
        if (string.IsNullOrWhiteSpace(cmd.Arg1) || string.IsNullOrWhiteSpace(ammoItemId))
        {
            return new CommandExecutionResult
            {
                ResultMessage = "reload requires weapon_instance_id and ammo_item_id arguments.",
                HaltScript = true
            };
        }

        JsonElement response = (await client.ExecuteCommandAsync(
            "reload",
            new { weapon_instance_id = cmd.Arg1, ammo_item_id = ammoItemId })).Payload;

        if (CommandJson.TryGetError(response, out var code, out var error))
        {
            return new CommandExecutionResult
            {
                ResultMessage = $"Reload failed: {error ?? code ?? "unknown error"}",
                HaltScript = true
            };
        }

        return new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response) ?? "Reloaded."
        };
    }
}
