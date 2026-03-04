using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

internal static class SpaceMoltMarketAnalytics
{
    public static bool TryParseStorageSnapshot(
        JsonElement storageResult,
        out int storageCredits,
        out Dictionary<string, ItemStack> storageItems)
    {
        storageCredits = 0;
        storageItems = new Dictionary<string, ItemStack>();

        bool parsedAny = false;

        if (storageResult.TryGetProperty("credits", out var creditsEl) &&
            creditsEl.ValueKind == JsonValueKind.Number)
        {
            storageCredits = creditsEl.GetInt32();
            parsedAny = true;
        }

        if (storageResult.TryGetProperty("items", out var storageItemsEl) &&
            storageItemsEl.ValueKind == JsonValueKind.Array)
        {
            parsedAny = true;
            foreach (var item in storageItemsEl.EnumerateArray())
            {
                if (!item.TryGetProperty("item_id", out var itemIdEl) ||
                    itemIdEl.ValueKind != JsonValueKind.String)
                    continue;

                if (!item.TryGetProperty("quantity", out var quantityEl) ||
                    quantityEl.ValueKind != JsonValueKind.Number)
                    continue;

                var itemId = itemIdEl.GetString();
                if (string.IsNullOrWhiteSpace(itemId))
                    continue;

                var quantity = quantityEl.GetInt32();
                if (quantity <= 0)
                    continue;

                storageItems[itemId] = new ItemStack(itemId, quantity);
            }
        }

        return parsedAny;
    }

    public static bool TryParseMarketSnapshot(
        JsonElement marketResult,
        string stationId,
        out MarketState market)
    {
        market = new MarketState
        {
            StationId = stationId
        };

        if (!marketResult.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("item_id", out var itemIdEl) ||
                itemIdEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string itemId = itemIdEl.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(itemId))
                continue;

            if (item.TryGetProperty("sell_orders", out var sellOrders) &&
                sellOrders.ValueKind == JsonValueKind.Array)
            {
                foreach (var order in sellOrders.EnumerateArray())
                {
                    if (!order.TryGetProperty("price_each", out var priceEl) || priceEl.ValueKind != JsonValueKind.Number ||
                        !order.TryGetProperty("quantity", out var qtyEl) || qtyEl.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    if (!market.SellOrders.ContainsKey(itemId))
                        market.SellOrders[itemId] = new List<MarketOrder>();

                    market.SellOrders[itemId].Add(new MarketOrder
                    {
                        ItemId = itemId,
                        PriceEach = priceEl.GetDecimal(),
                        Quantity = qtyEl.GetInt32()
                    });
                }
            }

            if (item.TryGetProperty("buy_orders", out var buyOrders) &&
                buyOrders.ValueKind == JsonValueKind.Array)
            {
                foreach (var order in buyOrders.EnumerateArray())
                {
                    if (!order.TryGetProperty("price_each", out var priceEl) || priceEl.ValueKind != JsonValueKind.Number ||
                        !order.TryGetProperty("quantity", out var qtyEl) || qtyEl.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    if (!market.BuyOrders.ContainsKey(itemId))
                        market.BuyOrders[itemId] = new List<MarketOrder>();

                    market.BuyOrders[itemId].Add(new MarketOrder
                    {
                        ItemId = itemId,
                        PriceEach = priceEl.GetDecimal(),
                        Quantity = qtyEl.GetInt32()
                    });
                }
            }
        }

        return true;
    }

    public static bool TryParseOwnOrders(
        JsonElement ordersResult,
        out List<OpenOrderInfo> buyOrders,
        out List<OpenOrderInfo> sellOrders)
    {
        var buyList = new List<OpenOrderInfo>();
        var sellList = new List<OpenOrderInfo>();

        bool parsedAny = false;

        void ParseOrderArray(JsonElement arr, string? forcedSide = null)
        {
            foreach (var order in arr.EnumerateArray())
            {
                if (order.ValueKind != JsonValueKind.Object)
                    continue;

                string side = forcedSide ?? "";
                if (string.IsNullOrWhiteSpace(side) &&
                    order.TryGetProperty("order_type", out var sideEl) &&
                    sideEl.ValueKind == JsonValueKind.String)
                {
                    side = sideEl.GetString() ?? "";
                }

                if (string.IsNullOrWhiteSpace(side) &&
                    order.TryGetProperty("type", out var typeEl) &&
                    typeEl.ValueKind == JsonValueKind.String)
                {
                    side = typeEl.GetString() ?? "";
                }

                if (!order.TryGetProperty("item_id", out var itemEl) || itemEl.ValueKind != JsonValueKind.String)
                    continue;
                if (!order.TryGetProperty("price_each", out var priceEl) || priceEl.ValueKind != JsonValueKind.Number)
                    continue;
                if (!order.TryGetProperty("quantity", out var qtyEl) || qtyEl.ValueKind != JsonValueKind.Number)
                    continue;

                var info = new OpenOrderInfo
                {
                    ItemId = itemEl.GetString() ?? "",
                    PriceEach = priceEl.GetDecimal(),
                    Quantity = qtyEl.GetInt32(),
                    OrderId = order.TryGetProperty("order_id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                        ? idEl.GetString() ?? ""
                        : ""
                };

                if (string.IsNullOrWhiteSpace(info.ItemId) || info.Quantity <= 0)
                    continue;

                parsedAny = true;
                if (string.Equals(side, "buy", StringComparison.OrdinalIgnoreCase))
                    buyList.Add(info);
                else if (string.Equals(side, "sell", StringComparison.OrdinalIgnoreCase))
                    sellList.Add(info);
            }
        }

        if (ordersResult.TryGetProperty("orders", out var orders) &&
            orders.ValueKind == JsonValueKind.Array)
        {
            ParseOrderArray(orders);
        }

        if (ordersResult.TryGetProperty("buy_orders", out var buyArr) &&
            buyArr.ValueKind == JsonValueKind.Array)
        {
            ParseOrderArray(buyArr, "buy");
            parsedAny = true;
        }

        if (ordersResult.TryGetProperty("sell_orders", out var sellArr) &&
            sellArr.ValueKind == JsonValueKind.Array)
        {
            ParseOrderArray(sellArr, "sell");
            parsedAny = true;
        }

        buyOrders = buyList;
        sellOrders = sellList;
        return parsedAny;
    }

    public static Dictionary<string, ItemStack> CloneItems(Dictionary<string, ItemStack> source)
    {
        return source.ToDictionary(
            kvp => kvp.Key,
            kvp => new ItemStack(kvp.Value.ItemId, kvp.Value.Quantity),
            StringComparer.Ordinal);
    }

    public static MarketState? CloneMarket(MarketState? source)
    {
        if (source == null)
            return null;

        var clone = new MarketState
        {
            StationId = source.StationId
        };

        foreach (var (itemId, orders) in source.SellOrders)
        {
            clone.SellOrders[itemId] = orders
                .Select(o => new MarketOrder
                {
                    ItemId = o.ItemId,
                    PriceEach = o.PriceEach,
                    Quantity = o.Quantity
                })
                .ToList();
        }

        foreach (var (itemId, orders) in source.BuyOrders)
        {
            clone.BuyOrders[itemId] = orders
                .Select(o => new MarketOrder
                {
                    ItemId = o.ItemId,
                    PriceEach = o.PriceEach,
                    Quantity = o.Quantity
                })
                .ToList();
        }

        return clone;
    }

    public static EconomyDeal[] BuildBestDealsForCurrentStation(
        IReadOnlyDictionary<string, StationInfo> stationCache,
        string currentStationId,
        int maxDeals)
    {
        if (!stationCache.TryGetValue(currentStationId, out var current) || current.Market == null)
            return Array.Empty<EconomyDeal>();

        var deals = new List<EconomyDeal>();

        foreach (var (itemId, sellOrders) in current.Market.SellOrders)
        {
            if (sellOrders == null || sellOrders.Count == 0)
                continue;

            decimal? buyPrice = sellOrders
                .Where(o => o.Quantity > 0 && o.PriceEach > 0)
                .Select(o => (decimal?)o.PriceEach)
                .DefaultIfEmpty(null)
                .Min();
            if (!buyPrice.HasValue || buyPrice.Value <= 0)
                continue;

            string? bestSellStation = null;
            decimal bestSellPrice = 0;

            foreach (var (stationId, stationInfo) in stationCache)
            {
                if (string.Equals(stationId, currentStationId, StringComparison.Ordinal))
                    continue;

                var market = stationInfo.Market;
                if (market == null)
                    continue;

                if (!market.BuyOrders.TryGetValue(itemId, out var buyOrders) ||
                    buyOrders == null ||
                    buyOrders.Count == 0)
                {
                    continue;
                }

                decimal? stationBidMedian = GalaxyStateHub.ComputeMedianPrice(buyOrders);
                if (!stationBidMedian.HasValue || stationBidMedian.Value <= 0)
                    continue;

                if (stationBidMedian.Value > bestSellPrice)
                {
                    bestSellPrice = stationBidMedian.Value;
                    bestSellStation = stationId;
                }
            }

            if (string.IsNullOrWhiteSpace(bestSellStation))
                continue;

            decimal profitPerUnit = bestSellPrice - buyPrice.Value;
            if (profitPerUnit <= 0)
                continue;

            deals.Add(new EconomyDeal
            {
                ItemId = itemId,
                BuyStationId = currentStationId,
                BuyPrice = buyPrice.Value,
                SellStationId = bestSellStation!,
                SellPrice = bestSellPrice,
                ProfitPerUnit = profitPerUnit
            });
        }

        return deals
            .OrderByDescending(d => d.ProfitPerUnit)
            .ThenBy(d => d.ItemId, StringComparer.Ordinal)
            .Take(Math.Max(0, maxDeals))
            .ToArray();
    }
}
