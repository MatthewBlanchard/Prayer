using System;
using System.Text.Json;
using System.Threading.Tasks;

public class SetHomeCommand : AutoDockSingleTurnCommand
{
    public override string Name => "set_home";
    public override DslCommandSyntax GetDslSyntax() => new();

    protected override bool IsAvailableWhenDocked(GameState state)
        => state.Docked && state.CurrentPOI.HasBase;

    public override string BuildHelp(GameState state)
        => "- set_home → set your currently docked base as home respawn point";

    protected override async Task<CommandExecutionResult?> ExecuteDockedAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        if (!string.IsNullOrWhiteSpace(cmd.Arg1) || cmd.Quantity.HasValue)
        {
            return new CommandExecutionResult
            {
                ResultMessage = "Usage: set_home."
            };
        }

        var baseId = state.CurrentPOI?.Id?.Trim();
        if (string.IsNullOrWhiteSpace(baseId))
        {
            return new CommandExecutionResult
            {
                ResultMessage = "Cannot set home: current docked location has no base id."
            };
        }

        JsonElement response = (await client.ExecuteCommandAsync("set_home_base", new { base_id = baseId })).Payload;
        string? message = CommandJson.TryGetResultMessage(response);
        if (string.IsNullOrWhiteSpace(message))
            message = $"Home base set to `{baseId}`.";

        return new CommandExecutionResult
        {
            ResultMessage = message
        };
    }
}
