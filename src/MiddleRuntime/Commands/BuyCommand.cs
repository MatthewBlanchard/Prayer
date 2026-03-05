using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public class BuyCommand : AutoDockSingleTurnCommand, IDslCommandGrammar
{
    public override string Name => "buy";
    protected override bool RequiresStation => true;
    public DslCommandSyntax GetDslSyntax() => new(
        ArgSpecs: new[]
        {
            new DslArgumentSpec(DslArgKind.Item, Required: true),
            new DslArgumentSpec(DslArgKind.Integer, Required: true),
        });

    protected override bool IsAvailableWhenDocked(GameState state)
    {
        var market = state.CurrentMarket;
        if (!state.Docked || market == null)
            return false;

        if (state.Credits <= 0)
            return false;

        if (state.CargoCapacity - state.CargoUsed <= 0)
            return false;

        return market.SellOrders.Any(kvp => kvp.Value.Count > 0);
    }

    public override string BuildHelp(GameState state)
        => "- buy <itemId> <quantity:int> → buy at station market price";

    protected override async Task<CommandExecutionResult?> ExecuteDockedAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        var market = state.CurrentMarket;
        if (!state.Docked || market == null || string.IsNullOrWhiteSpace(cmd.Arg1))
            return null;

        int requested = cmd.Quantity ?? 1;
        if (requested <= 0)
            requested = 1;

        if (!market.SellOrders.TryGetValue(cmd.Arg1, out var sellOrders) &&
            !market.BuyOrders.TryGetValue(cmd.Arg1, out _))
        {
            return new CommandExecutionResult
            {
                ResultMessage = $"No market data for {cmd.Arg1}."
            };
        }

        sellOrders ??= new List<MarketOrder>();

        int available = sellOrders.Sum(o => Math.Max(0, o.Quantity));
        int cargoFree = Math.Max(0, state.CargoCapacity - state.CargoUsed);
        int quantity = available > 0
            ? Math.Min(requested, Math.Min(available, cargoFree))
            : Math.Min(requested, cargoFree);

        if (quantity <= 0)
        {
            return new CommandExecutionResult
            {
                ResultMessage = cargoFree <= 0
                    ? "No cargo space."
                    : $"No quantity available for {cmd.Arg1}."
            };
        }

        decimal? lowestSellPrice = sellOrders.Count > 0
            ? sellOrders.Min(o => o.PriceEach)
            : null;

        decimal? highestBuyPrice = null;
        if (market.BuyOrders.TryGetValue(cmd.Arg1, out var buyOrders) &&
            buyOrders.Count > 0)
        {
            highestBuyPrice = buyOrders.Max(o => o.PriceEach);
        }

        if (!lowestSellPrice.HasValue && !highestBuyPrice.HasValue)
        {
            return new CommandExecutionResult
            {
                ResultMessage = $"No price data for {cmd.Arg1}."
            };
        }

        // Use buy-order-first price baseline; fallback to lowest ask.
        decimal price = highestBuyPrice ?? lowestSellPrice!.Value;
        price = Math.Max(1, Math.Floor(price));

        JsonElement response = (await client.ExecuteCommandAsync(
            "create_buy_order",
            new
            {
                item_id = cmd.Arg1,
                quantity = quantity,
                price_each = price
            })).Payload;

        if (IsCrossingOrder(response))
        {
            var conflictingOrderIds = GetConflictingOrderIds(response, state, cmd.Arg1, price);
            string? cancelMessage = null;

            foreach (var orderId in conflictingOrderIds)
            {
                JsonElement cancelResponse = (await client.ExecuteCommandAsync(
                    "cancel_order",
                    new { order_id = orderId })).Payload;

                string? msg = CommandJson.TryGetResultMessage(cancelResponse);
                if (!string.IsNullOrWhiteSpace(msg))
                {
                    cancelMessage = string.IsNullOrWhiteSpace(cancelMessage)
                        ? msg
                        : $"{cancelMessage} {msg}";
                }
            }

            JsonElement retryResponse = (await client.ExecuteCommandAsync(
                "create_buy_order",
                new
                {
                    item_id = cmd.Arg1,
                    quantity = quantity,
                    price_each = price
                })).Payload;

            string? retryMessage = CommandJson.TryGetResultMessage(retryResponse);
            if (string.IsNullOrWhiteSpace(cancelMessage) && conflictingOrderIds.Count > 0)
            {
                cancelMessage = $"Canceled {conflictingOrderIds.Count} conflicting sell order(s).";
            }
            string merged = string.IsNullOrWhiteSpace(cancelMessage)
                ? (retryMessage ?? "Retried buy after canceling conflicting order.")
                : $"{cancelMessage} Retry: {retryMessage}";

            return new CommandExecutionResult
            {
                ResultMessage = merged
            };
        }

        return new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response)
        };
    }

    private static bool IsCrossingOrder(JsonElement response)
    {
        if (!CommandJson.TryGetError(response, out var errorCode, out var errorMessage))
            return false;

        if (!string.Equals(errorCode, "crossing_order", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static List<string> GetConflictingOrderIds(
        JsonElement response,
        GameState state,
        string itemId,
        decimal buyPrice)
    {
        var ids = new List<string>();
        if (TryGetCrossingOrderId(response, out var parsedOrderId))
        {
            ids.Add(parsedOrderId!);
            return ids;
        }

        // No structured order id in the error payload: infer conflicts from current open sell orders.
        var fallback = state.OwnSellOrders
            .Where(o =>
                string.Equals(o.ItemId, itemId, StringComparison.Ordinal) &&
                o.PriceEach <= buyPrice &&
                !string.IsNullOrWhiteSpace(o.OrderId))
            .Select(o => o.OrderId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        ids.AddRange(fallback);
        return ids;
    }

    private static bool TryGetCrossingOrderId(JsonElement response, out string? orderId)
    {
        orderId = null;

        if (response.TryGetProperty("error", out var errorObj) && errorObj.ValueKind == JsonValueKind.Object)
        {
            if (TryGetNestedOrderId(errorObj, "order_id", out orderId) ||
                TryGetNestedOrderId(errorObj, "orderId", out orderId))
            {
                return true;
            }

            if (errorObj.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object)
            {
                if (TryGetNestedOrderId(details, "order_id", out orderId) ||
                    TryGetNestedOrderId(details, "orderId", out orderId))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryGetNestedOrderId(JsonElement obj, string propertyName, out string? orderId)
    {
        orderId = null;

        if (!obj.TryGetProperty(propertyName, out var orderIdEl) ||
            orderIdEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        orderId = orderIdEl.GetString();
        return !string.IsNullOrWhiteSpace(orderId);
    }
}

// =====================================================
// REFUEL
// =====================================================
