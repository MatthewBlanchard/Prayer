using System;
using System.Text.Json;
using System.Threading.Tasks;

public class BuyShipCommand : AutoDockSingleTurnCommand
{
    public override string Name => "buy_ship";
    protected override bool RequiresStation => true;

    protected override bool IsAvailableWhenDocked(GameState state)
        => state.Docked && state.CurrentPOI.IsStation;
    public override string BuildHelp(GameState state)
        => "- buy_ship <shipClassId> → buy a showroom ship";

    protected override async Task<CommandExecutionResult?> ExecuteDockedAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        var shipClass = cmd.Arg1?.Trim();
        if (string.IsNullOrWhiteSpace(shipClass))
            return new CommandExecutionResult { ResultMessage = "Usage: buy_ship <shipClassId>." };

        JsonElement response = (await client.ExecuteCommandAsync("buy_ship", new { ship_class = shipClass })).Payload;
        return new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response) ?? $"Bought ship class {shipClass}."
        };
    }
}
