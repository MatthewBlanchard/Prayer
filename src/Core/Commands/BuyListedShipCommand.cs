using System;
using System.Text.Json;
using System.Threading.Tasks;

public class BuyListedShipCommand : AutoDockSingleTurnCommand
{
    public override string Name => "buy_listed_ship";
    protected override bool RequiresStation => true;

    protected override bool IsAvailableWhenDocked(GameState state)
        => state.Docked && state.CurrentPOI.IsStation;
    public override string BuildHelp(GameState state)
        => "- buy_listed_ship <listingId> → buy a player-listed ship";

    protected override async Task<CommandExecutionResult?> ExecuteDockedAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        var listingId = cmd.Arg1?.Trim();
        if (string.IsNullOrWhiteSpace(listingId))
            return new CommandExecutionResult { ResultMessage = "Usage: buy_listed_ship <listingId>." };

        JsonElement response = (await client.ExecuteCommandAsync("buy_listed_ship", new { listing_id = listingId })).Payload;
        return new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response) ?? $"Bought listed ship {listingId}."
        };
    }
}
