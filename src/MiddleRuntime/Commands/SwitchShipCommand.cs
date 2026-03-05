using System;
using System.Text.Json;
using System.Threading.Tasks;

public class SwitchShipCommand : AutoDockSingleTurnCommand
{
    public override string Name => "switch_ship";
    protected override bool RequiresStation => true;

    protected override bool IsAvailableWhenDocked(GameState state)
        => state.Docked && state.CurrentPOI.IsStation;
    public override string BuildHelp(GameState state)
        => "- switch_ship <shipId> → switch active ship at this station";

    protected override async Task<CommandExecutionResult?> ExecuteDockedAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        var shipId = cmd.Arg1?.Trim();
        if (string.IsNullOrWhiteSpace(shipId))
        {
            return new CommandExecutionResult
            {
                ResultMessage = "Usage: switch_ship <shipId>."
            };
        }

        JsonElement response = (await client.ExecuteCommandAsync("switch_ship", new { ship_id = shipId })).Payload;
        string? message = CommandJson.TryGetResultMessage(response);

        if (string.IsNullOrWhiteSpace(message))
            message = $"Switched to ship {shipId}.";

        return new CommandExecutionResult
        {
            ResultMessage = message
        };
    }
}
