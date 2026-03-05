using System;
using System.Text.Json;
using System.Threading.Tasks;

public class CommissionQuoteCommand : AutoDockSingleTurnCommand
{
    public override string Name => "commission_quote";
    protected override bool RequiresStation => true;

    protected override bool IsAvailableWhenDocked(GameState state)
        => state.Docked && state.CurrentPOI.IsStation;
    public override string BuildHelp(GameState state)
        => "- commission_quote <shipClassId> → quote ship commission cost";

    protected override async Task<CommandExecutionResult?> ExecuteDockedAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        var shipClass = cmd.Arg1?.Trim();
        if (string.IsNullOrWhiteSpace(shipClass))
            return new CommandExecutionResult { ResultMessage = "Usage: commission_quote <shipClassId>." };

        JsonElement response = (await client.ExecuteCommandAsync("commission_quote", new { ship_class = shipClass })).Payload;
        return new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response) ?? $"Fetched commission quote for {shipClass}."
        };
    }
}
