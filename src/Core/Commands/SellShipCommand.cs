using System;
using System.Text.Json;
using System.Threading.Tasks;

public class SellShipCommand : AutoDockSingleTurnCommand
{
    public override string Name => "sell_ship";
    protected override bool RequiresStation => true;

    protected override bool IsAvailableWhenDocked(GameState state)
        => state.Docked && state.CurrentPOI.IsStation;
    public override string BuildHelp(GameState state)
        => "- sell_ship <shipId> → sell a stored ship to the station";

    protected override async Task<CommandExecutionResult?> ExecuteDockedAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        var shipId = cmd.Arg1?.Trim();
        if (string.IsNullOrWhiteSpace(shipId))
            return new CommandExecutionResult { ResultMessage = "Usage: sell_ship <shipId>." };

        JsonElement response = (await client.ExecuteCommandAsync("sell_ship", new { ship_id = shipId })).Payload;
        return new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response) ?? $"Sold ship {shipId}."
        };
    }
}
