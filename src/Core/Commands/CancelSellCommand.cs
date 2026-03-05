using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public class CancelSellCommand : AutoDockSingleTurnCommand, IDslCommandGrammar
{
    public override string Name => "cancel_sell";
    protected override bool RequiresStation => true;
    public DslCommandSyntax GetDslSyntax() => new(
        DslArgKind.Item,
        ArgRequired: true);

    protected override bool IsAvailableWhenDocked(GameState state)
    {
        return state.Docked &&
               state.OwnSellOrders != null &&
               state.OwnSellOrders.Any(o => !string.IsNullOrWhiteSpace(o.OrderId));
    }

    public override string BuildHelp(GameState state)
        => "- cancel_sell <itemId> → cancel your open sell orders for an item";

    protected override async Task<CommandExecutionResult?> ExecuteDockedAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        if (!state.Docked || string.IsNullOrWhiteSpace(cmd.Arg1))
            return new CommandExecutionResult { ResultMessage = "Usage: cancel_sell <itemId>." };

        var targetItem = cmd.Arg1.Trim();
        var orders = state.OwnSellOrders
            .Where(o =>
                !string.IsNullOrWhiteSpace(o.OrderId) &&
                string.Equals(o.ItemId, targetItem, StringComparison.Ordinal))
            .ToList();

        if (orders.Count == 0)
        {
            return new CommandExecutionResult
            {
                ResultMessage = $"No open sell orders for {targetItem}."
            };
        }

        int canceled = 0;
        var errors = new List<string>();
        foreach (var order in orders)
        {
            JsonElement response = (await client.ExecuteCommandAsync(
                "cancel_order",
                new { order_id = order.OrderId })).Payload;

            if (CommandJson.TryGetError(response, out _, out var errorMessage))
            {
                if (!string.IsNullOrWhiteSpace(errorMessage))
                    errors.Add(errorMessage!);
                continue;
            }

            canceled++;
        }

        var message = $"Canceled {canceled}/{orders.Count} sell order(s) for {targetItem}.";
        if (errors.Count > 0)
            message += $" Errors: {string.Join(" | ", errors.Distinct(StringComparer.Ordinal))}";

        return new CommandExecutionResult { ResultMessage = message };
    }
}
