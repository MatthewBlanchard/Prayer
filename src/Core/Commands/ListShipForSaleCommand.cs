using System;
using System.Text.Json;
using System.Threading.Tasks;

public class ListShipForSaleCommand : AutoDockSingleTurnCommand
{
    public override string Name => "list_ship_for_sale";
    protected override bool RequiresStation => true;

    protected override bool IsAvailableWhenDocked(GameState state)
        => state.Docked && state.CurrentPOI.IsStation;
    public override string BuildHelp(GameState state)
        => "- list_ship_for_sale <shipId> <price> → list your ship on the exchange";

    protected override async Task<CommandExecutionResult?> ExecuteDockedAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        var shipId = cmd.Arg1?.Trim();
        if (string.IsNullOrWhiteSpace(shipId) || !cmd.Quantity.HasValue || cmd.Quantity.Value <= 0)
        {
            return new CommandExecutionResult
            {
                ResultMessage = "Usage: list_ship_for_sale <shipId> <price>."
            };
        }

        JsonElement response = (await client.ExecuteCommandAsync(
            "list_ship_for_sale",
            new
            {
                ship_id = shipId,
                price = cmd.Quantity.Value
            })).Payload;

        return new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response) ?? $"Listed ship {shipId} for {cmd.Quantity.Value}cr."
        };
    }
}
