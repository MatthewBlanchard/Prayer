using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public class SellCommand : AutoDockMultiTurnCommand, IDslCommandGrammar
{
    public override string Name => "sell";
    protected override bool RequiresStation => true;
    public DslCommandSyntax GetDslSyntax() => new(
        ArgSpecs: new[]
        {
            new DslArgumentSpec(
                DslArgKind.Item | DslArgKind.Enum,
                Required: false,
                DefaultValue: "cargo",
                EnumType: "cargo_keyword")
        });

    private List<string>? _sellQueue;

    protected override bool IsAvailableWhenDocked(GameState state)
    {
        if (!state.Docked || state.CurrentMarket == null)
            return false;

        return state.Cargo.Any(kvp => kvp.Value.Quantity > 0 && IsSellable(state, kvp.Key));
    }

    public override string BuildHelp(GameState state)
        => "- sell <item|cargo> → sell one item or all cargo";

    protected override async Task<CommandExecutionResult?> StartDockedAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        if (cmd.Arg1 == null)
            return null;

        // --- SINGLE ITEM MODE ---
        if (!string.Equals(cmd.Arg1, "cargo", StringComparison.OrdinalIgnoreCase))
        {
            return await SellOneAsync(client, state, cmd.Arg1);
        }

        // --- MULTI-STEP MODE ---
        _sellQueue = state.Cargo
            .Where(kvp => kvp.Value.Quantity > 0 && IsSellable(state, kvp.Key))
            .Select(kvp => kvp.Key)
            .ToList();

        if (_sellQueue.Count == 0)
        {
            return new CommandExecutionResult
            {
                ResultMessage = "No sellable cargo."
            };
        }

        var first = _sellQueue[0];
        _sellQueue.RemoveAt(0);

        return await SellOneAsync(client, state, first);
    }

    protected override async Task<(bool finished, CommandExecutionResult? result)> ContinueDockedAsync(
        IRuntimeTransport client,
        GameState state)
    {
        if (_sellQueue == null)
        {
            return (true, new CommandExecutionResult
            {
                ResultMessage = "Finished selling cargo."
            });
        }

        while (_sellQueue.Count > 0)
        {
            var next = _sellQueue[0];
            _sellQueue.RemoveAt(0);

            var result = await SellOneAsync(client, state, next);
            if (result != null)
                return (false, result);
        }

        return (true, new CommandExecutionResult
        {
            ResultMessage = "Finished selling cargo."
        });
    }

    // ==========================================
    // HELPERS
    // ==========================================
    private static bool IsSellable(GameState state, string item)
    {
        return
            (state.CurrentMarket!.SellOrders.TryGetValue(item, out var sells) && sells.Count > 0) ||
            (state.CurrentMarket.BuyOrders.TryGetValue(item, out var buys) && buys.Count > 0);
    }

    private async Task<CommandExecutionResult?> SellOneAsync(
        IRuntimeTransport client,
        GameState state,
        string item)
    {
        if (!state.Cargo.TryGetValue(item, out var stack))
            return null;

        if (stack.Quantity <= 0)
            return null;

        var buyOrders = state.CurrentMarket!.BuyOrders.TryGetValue(item, out var bids)
            ? bids
            : new List<MarketOrder>();
        var sellOrders = state.CurrentMarket.SellOrders.TryGetValue(item, out var asks)
            ? asks
            : new List<MarketOrder>();

        var filteredBuyOrders = ExcludeOwnOrders(buyOrders, state.OwnBuyOrders, item);
        var filteredSellOrders = ExcludeOwnOrders(sellOrders, state.OwnSellOrders, item);

        var targetPrice = ComputeTargetSellPrice(
            filteredBuyOrders,
            filteredSellOrders,
            state.Galaxy.Market.GlobalWeightedMidPrices.TryGetValue(item, out var globalMid) ? globalMid : null);
        if (!targetPrice.HasValue)
            return null;

        decimal price = targetPrice.Value;
        price = Math.Max(1, Math.Floor(price));

        JsonElement response = (await client.ExecuteCommandAsync(
            "create_sell_order",
            new
            {
                item_id = item,
                quantity = stack.Quantity,
                price_each = price
            })).Payload;

        return new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response)
        };
    }

    private static decimal? ComputeTargetSellPrice(
        List<MarketOrder> buyOrders,
        List<MarketOrder> sellOrders,
        decimal? globalWeightedMid)
    {
        // Buy-order-first baseline; fallback to lowest ask only when no bid data exists.
        decimal? priceBaseline = globalWeightedMid
            ?? ComputeMedianPrice(buyOrders)
            ?? ComputeLowestAsk(sellOrders);

        if (!priceBaseline.HasValue)
            return null;

        decimal target = priceBaseline.Value;

        // Prefer satisfying the highest bid that is at/above the baseline.
        var highestBidAboveMedian = buyOrders
            .Where(b => b.Quantity > 0 && b.PriceEach >= priceBaseline.Value)
            .Select(b => (decimal?)b.PriceEach)
            .DefaultIfEmpty(null)
            .Max();
        if (highestBidAboveMedian.HasValue)
            target = highestBidAboveMedian.Value;

        return target;
    }

    private static List<MarketOrder> ExcludeOwnOrders(
        List<MarketOrder> marketOrders,
        OpenOrderInfo[] ownOrders,
        string itemId)
    {
        if (marketOrders == null || marketOrders.Count == 0)
            return new List<MarketOrder>();

        var remainingOwnByPrice = ownOrders
            .Where(o => string.Equals(o.ItemId, itemId, StringComparison.Ordinal) && o.Quantity > 0)
            .GroupBy(o => o.PriceEach)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

        if (remainingOwnByPrice.Count == 0)
            return marketOrders
                .Where(o => o.Quantity > 0)
                .Select(o => new MarketOrder
                {
                    ItemId = o.ItemId,
                    PriceEach = o.PriceEach,
                    Quantity = o.Quantity
                })
                .ToList();

        var adjusted = new List<MarketOrder>(marketOrders.Count);

        foreach (var order in marketOrders)
        {
            if (order.Quantity <= 0)
                continue;

            int qty = order.Quantity;

            if (remainingOwnByPrice.TryGetValue(order.PriceEach, out var ownQtyAtPrice) &&
                ownQtyAtPrice > 0)
            {
                int removed = Math.Min(qty, ownQtyAtPrice);
                qty -= removed;
                remainingOwnByPrice[order.PriceEach] = ownQtyAtPrice - removed;
            }

            if (qty <= 0)
                continue;

            adjusted.Add(new MarketOrder
            {
                ItemId = order.ItemId,
                PriceEach = order.PriceEach,
                Quantity = qty
            });
        }

        return adjusted;
    }

    private static decimal? ComputeMedianPrice(List<MarketOrder> orders)
    {
        if (orders == null || orders.Count == 0)
            return null;

        var expanded = new List<decimal>();

        foreach (var o in orders)
        {
            if (o.Quantity <= 0 || o.PriceEach <= 0)
                continue;

            for (int i = 0; i < o.Quantity; i++)
                expanded.Add(o.PriceEach);
        }

        if (expanded.Count == 0)
            return null;

        expanded.Sort();
        int n = expanded.Count;
        int mid = n / 2;

        if (n % 2 == 1)
            return expanded[mid];

        return (expanded[mid - 1] + expanded[mid]) / 2m;
    }

    private static decimal? ComputeLowestAsk(List<MarketOrder> orders)
    {
        if (orders == null || orders.Count == 0)
            return null;

        return orders
            .Where(o => o.Quantity > 0 && o.PriceEach > 0)
            .Select(o => (decimal?)o.PriceEach)
            .DefaultIfEmpty(null)
            .Min();
    }

}

// =====================================================
// BUY
// =====================================================
