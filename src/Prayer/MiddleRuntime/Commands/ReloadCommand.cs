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
            new DslArgumentSpec(DslArgKind.Any, Required: true),
            new DslArgumentSpec(DslArgKind.Item, Required: true)
        });

    public async Task<CommandExecutionResult?> ExecuteAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        if (string.IsNullOrWhiteSpace(cmd.Arg1) || string.IsNullOrWhiteSpace(cmd.Arg2))
        {
            return new CommandExecutionResult
            {
                ResultMessage = "reload requires weapon_instance_id and ammo_item_id arguments.",
                HaltScript = true
            };
        }

        JsonElement response = (await client.ExecuteCommandAsync(
            "reload",
            new { weapon_instance_id = cmd.Arg1, ammo_item_id = cmd.Arg2 })).Payload;

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
