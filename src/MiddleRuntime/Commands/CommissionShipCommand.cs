using System;
using System.Text.Json;
using System.Threading.Tasks;

public class CommissionShipCommand : AutoDockSingleTurnCommand
{
    public override string Name => "commission_ship";
    protected override bool RequiresStation => true;

    protected override bool IsAvailableWhenDocked(GameState state)
        => state.Docked && state.CurrentPOI.IsStation;
    public override string BuildHelp(GameState state)
        => "- commission_ship <shipClassId> → start ship commission";

    protected override async Task<CommandExecutionResult?> ExecuteDockedAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        var shipClass = cmd.Arg1?.Trim();
        if (string.IsNullOrWhiteSpace(shipClass))
            return new CommandExecutionResult { ResultMessage = "Usage: commission_ship <shipClassId>." };

        JsonElement response = (await client.ExecuteCommandAsync("commission_ship", new { ship_class = shipClass })).Payload;
        return new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response) ?? $"Commission started for {shipClass}."
        };
    }
}
