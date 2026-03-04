using System;
using System.Collections.Generic;
using System.Linq;

public class GalaxyState
{
    public GalaxyMapSnapshot Map { get; set; } = new();
    public GalaxyMarket Market { get; set; } = new();
    public GalaxyCatalog Catalog { get; set; } = new();
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class GalaxyMarket
{
    public Dictionary<string, MarketState> MarketsByStation { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, decimal> GlobalMedianBuyPrices { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, decimal> GlobalMedianSellPrices { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, decimal> GlobalWeightedMidPrices { get; set; } = new(StringComparer.Ordinal);
}

public class GalaxyCatalog
{
    public Dictionary<string, CatalogueEntry> ItemsById { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, CatalogueEntry> ShipsById { get; set; } = new(StringComparer.Ordinal);
}

internal static class GalaxyStateHub
{
    private static readonly object Sync = new();
    private static GalaxyMapSnapshot _map = new();
    private static readonly Dictionary<string, MarketState> MarketsByStation = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, CatalogueEntry> ItemCatalogById = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, CatalogueEntry> ShipCatalogById = new(StringComparer.Ordinal);
    private static GalaxyState _snapshot = new();

    public static void MergeMap(GalaxyMapSnapshot? map)
    {
        if (map == null || map.Systems == null || map.Systems.Count == 0)
            return;

        lock (Sync)
        {
            _map = CloneMap(map);
            RebuildSnapshotNoLock();
        }
    }

    public static void MergeMarkets(IEnumerable<MarketState?> markets)
    {
        if (markets == null)
            return;

        bool changed = false;

        lock (Sync)
        {
            foreach (var market in markets)
            {
                if (market == null || string.IsNullOrWhiteSpace(market.StationId))
                    continue;

                MarketsByStation[market.StationId] = CloneMarket(market);
                changed = true;
            }

            if (changed)
                RebuildSnapshotNoLock();
        }
    }

    public static void MergeItemCatalog(IReadOnlyDictionary<string, CatalogueEntry>? byId)
    {
        if (byId == null || byId.Count == 0)
            return;

        lock (Sync)
        {
            ItemCatalogById.Clear();
            foreach (var (itemId, entry) in byId)
            {
                if (string.IsNullOrWhiteSpace(itemId) || entry == null)
                    continue;

                ItemCatalogById[itemId] = CloneCatalogueEntry(entry);
            }

            RebuildSnapshotNoLock();
        }
    }

    public static void MergeShipCatalog(IReadOnlyDictionary<string, CatalogueEntry>? byId)
    {
        if (byId == null || byId.Count == 0)
            return;

        lock (Sync)
        {
            ShipCatalogById.Clear();
            foreach (var (shipId, entry) in byId)
            {
                if (string.IsNullOrWhiteSpace(shipId) || entry == null)
                    continue;

                ShipCatalogById[shipId] = CloneCatalogueEntry(entry);
            }

            RebuildSnapshotNoLock();
        }
    }

    public static GalaxyState Snapshot()
    {
        lock (Sync)
            return CloneGalaxyState(_snapshot);
    }

    private static void RebuildSnapshotNoLock()
    {
        var marketsClone = MarketsByStation.ToDictionary(
            kvp => kvp.Key,
            kvp => CloneMarket(kvp.Value),
            StringComparer.Ordinal);

        _snapshot = new GalaxyState
        {
            Map = CloneMap(_map),
            Market = new GalaxyMarket
            {
                MarketsByStation = marketsClone,
                GlobalMedianBuyPrices = BuildGlobalMedianBuyPrices(marketsClone.Values),
                GlobalMedianSellPrices = BuildGlobalMedianSellPrices(marketsClone.Values),
                GlobalWeightedMidPrices = BuildGlobalWeightedMidPrices(marketsClone.Values)
            },
            Catalog = new GalaxyCatalog
            {
                ItemsById = CloneCatalogById(ItemCatalogById),
                ShipsById = CloneCatalogById(ShipCatalogById)
            },
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    private static Dictionary<string, CatalogueEntry> CloneCatalogById(
        IReadOnlyDictionary<string, CatalogueEntry> source)
    {
        var clone = new Dictionary<string, CatalogueEntry>(StringComparer.Ordinal);
        foreach (var (id, entry) in source)
        {
            if (string.IsNullOrWhiteSpace(id) || entry == null)
                continue;

            clone[id] = CloneCatalogueEntry(entry);
        }

        return clone;
    }

    private static Dictionary<string, decimal> BuildGlobalMedianBuyPrices(IEnumerable<MarketState> markets)
    {
        var bidsByItem = new Dictionary<string, List<MarketOrder>>(StringComparer.Ordinal);

        foreach (var market in markets)
        {
            if (market == null)
                continue;

            foreach (var (itemId, bids) in market.BuyOrders)
            {
                if (!bidsByItem.TryGetValue(itemId, out var list))
                {
                    list = new List<MarketOrder>();
                    bidsByItem[itemId] = list;
                }

                list.AddRange(bids ?? Enumerable.Empty<MarketOrder>());
            }
        }

        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (itemId, bids) in bidsByItem)
        {
            decimal? median = ComputeMedianPrice(bids);
            if (median.HasValue && median.Value > 0m)
                result[itemId] = median.Value;
        }

        return result;
    }

    private static Dictionary<string, decimal> BuildGlobalMedianSellPrices(IEnumerable<MarketState> markets)
    {
        var asksByItem = new Dictionary<string, List<MarketOrder>>(StringComparer.Ordinal);

        foreach (var market in markets)
        {
            if (market == null)
                continue;

            foreach (var (itemId, asks) in market.SellOrders)
            {
                if (!asksByItem.TryGetValue(itemId, out var list))
                {
                    list = new List<MarketOrder>();
                    asksByItem[itemId] = list;
                }

                list.AddRange(asks ?? Enumerable.Empty<MarketOrder>());
            }
        }

        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (itemId, asks) in asksByItem)
        {
            decimal? median = ComputeMedianPrice(asks);
            if (median.HasValue && median.Value > 0m)
                result[itemId] = median.Value;
        }

        return result;
    }

    private static Dictionary<string, decimal> BuildGlobalWeightedMidPrices(IEnumerable<MarketState> markets)
    {
        var bidsByItem = new Dictionary<string, List<MarketOrder>>(StringComparer.Ordinal);
        var asksByItem = new Dictionary<string, List<MarketOrder>>(StringComparer.Ordinal);

        foreach (var market in markets)
        {
            if (market == null)
                continue;

            foreach (var (itemId, bids) in market.BuyOrders)
            {
                if (!bidsByItem.TryGetValue(itemId, out var list))
                {
                    list = new List<MarketOrder>();
                    bidsByItem[itemId] = list;
                }

                list.AddRange(bids ?? Enumerable.Empty<MarketOrder>());
            }

            foreach (var (itemId, asks) in market.SellOrders)
            {
                if (!asksByItem.TryGetValue(itemId, out var list))
                {
                    list = new List<MarketOrder>();
                    asksByItem[itemId] = list;
                }

                list.AddRange(asks ?? Enumerable.Empty<MarketOrder>());
            }
        }

        var itemIds = new HashSet<string>(bidsByItem.Keys, StringComparer.Ordinal);
        itemIds.UnionWith(asksByItem.Keys);

        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var itemId in itemIds)
        {
            bidsByItem.TryGetValue(itemId, out var bids);
            asksByItem.TryGetValue(itemId, out var asks);

            decimal? bidMedian = ComputeMedianPrice(bids);
            decimal? askMedian = ComputeMedianPrice(asks);

            decimal? mid = bidMedian.HasValue && askMedian.HasValue
                ? (bidMedian.Value + askMedian.Value) / 2m
                : (bidMedian ?? askMedian);

            if (mid.HasValue && mid.Value > 0m)
                result[itemId] = mid.Value;
        }

        return result;
    }

    internal static decimal? ComputeMedianPrice(List<MarketOrder>? orders)
    {
        if (orders == null || orders.Count == 0)
            return null;

        var expanded = new List<decimal>();
        foreach (var order in orders.Where(o => o != null && o.Quantity > 0 && o.PriceEach > 0))
        {
            for (int i = 0; i < order.Quantity; i++)
                expanded.Add(order.PriceEach);
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

    private static GalaxyState CloneGalaxyState(GalaxyState source)
    {
        return new GalaxyState
        {
            Map = CloneMap(source.Map),
            Market = new GalaxyMarket
            {
                MarketsByStation = source.Market.MarketsByStation.ToDictionary(
                    kvp => kvp.Key,
                    kvp => CloneMarket(kvp.Value),
                    StringComparer.Ordinal),
                GlobalMedianBuyPrices = new Dictionary<string, decimal>(
                    source.Market.GlobalMedianBuyPrices,
                    StringComparer.Ordinal),
                GlobalMedianSellPrices = new Dictionary<string, decimal>(
                    source.Market.GlobalMedianSellPrices,
                    StringComparer.Ordinal),
                GlobalWeightedMidPrices = new Dictionary<string, decimal>(
                    source.Market.GlobalWeightedMidPrices,
                    StringComparer.Ordinal)
            },
            Catalog = new GalaxyCatalog
            {
                ItemsById = CloneCatalogById(source.Catalog.ItemsById),
                ShipsById = CloneCatalogById(source.Catalog.ShipsById)
            },
            UpdatedAtUtc = source.UpdatedAtUtc
        };
    }

    private static GalaxyMapSnapshot CloneMap(GalaxyMapSnapshot source)
    {
        return new GalaxyMapSnapshot
        {
            Systems = (source.Systems ?? new List<GalaxySystemInfo>())
                .Select(system => new GalaxySystemInfo
                {
                    Id = system.Id,
                    Pois = (system.Pois ?? new List<GalaxyPoiInfo>())
                        .Select(poi => new GalaxyPoiInfo { Id = poi.Id })
                        .ToList()
                })
                .ToList()
        };
    }

    private static MarketState CloneMarket(MarketState source)
    {
        var clone = new MarketState
        {
            StationId = source.StationId
        };

        foreach (var (itemId, orders) in source.SellOrders)
        {
            clone.SellOrders[itemId] = (orders ?? new List<MarketOrder>())
                .Select(CloneMarketOrder)
                .ToList();
        }

        foreach (var (itemId, orders) in source.BuyOrders)
        {
            clone.BuyOrders[itemId] = (orders ?? new List<MarketOrder>())
                .Select(CloneMarketOrder)
                .ToList();
        }

        return clone;
    }

    private static MarketOrder CloneMarketOrder(MarketOrder order)
    {
        return new MarketOrder
        {
            ItemId = order.ItemId,
            PriceEach = order.PriceEach,
            Quantity = order.Quantity
        };
    }

    private static CatalogueEntry CloneCatalogueEntry(CatalogueEntry entry)
    {
        return new CatalogueEntry
        {
            Id = entry.Id,
            Name = entry.Name,
            ClassId = entry.ClassId,
            Class = entry.Class,
            Category = entry.Category,
            Tier = entry.Tier,
            Scale = entry.Scale,
            Hull = entry.Hull,
            BaseHull = entry.BaseHull,
            Shield = entry.Shield,
            BaseShield = entry.BaseShield,
            Cargo = entry.Cargo,
            CargoCapacity = entry.CargoCapacity,
            Speed = entry.Speed,
            BaseSpeed = entry.BaseSpeed,
            Price = entry.Price
        };
    }
}
