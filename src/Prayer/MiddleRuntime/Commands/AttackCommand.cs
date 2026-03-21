using System.Text.Json;
using System.Threading.Tasks;

public class AttackCommand : ISingleTurnCommand, IDslCommandGrammar
{
    public string Name => "attack";

    public bool IsAvailable(GameState state)
        => !state.Docked && !string.IsNullOrWhiteSpace(state.System);

    public string BuildHelp(GameState state)
        => "- attack <target_id> → initiate combat with a target";

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
                ResultMessage = "attack requires a target_id argument.",
                HaltScript = true
            };
        }

        JsonElement response = (await client.ExecuteCommandAsync(
            "attack",
            new { target_id = cmd.Arg1 })).Payload;

        if (CommandJson.TryGetError(response, out var code, out var error))
        {
            return new CommandExecutionResult
            {
                ResultMessage = $"Attack failed: {error ?? code ?? "unknown error"}",
                HaltScript = true
            };
        }

        return new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response) ?? $"Attacking {cmd.Arg1}."
        };
    }
}
