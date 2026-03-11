using System;
using System.Collections.Generic;
using System.Linq;

internal static class GalaxyStateHub
{
    private static readonly object Sync = new();
    private static GalaxyMapSnapshot _map = new();
    private static readonly Dictionary<string, GalaxyKnownPoiInfo> KnownPoisById = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, MarketState> MarketsByStation = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, ItemCatalogueEntry> ItemCatalogById = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, ShipCatalogueEntry> ShipCatalogById = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, HashSet<string>> ResourceSystemsById = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, HashSet<string>> ResourcePoisById = new(StringComparer.Ordinal);
    private static GalaxyState _snapshot = new();

    public static void MergeMap(GalaxyMapSnapshot? map)
    {
        if (map == null || map.Systems == null || map.Systems.Count == 0)
            return;

        lock (Sync)
        {
            _map = CloneMap(map);
            MergeKnownPoisNoLock(map.KnownPois);
            RebuildSnapshotNoLock();
        }
    }

    public static void MergeKnownPois(IEnumerable<GalaxyKnownPoiInfo>? pois)
    {
        if (pois == null)
            return;

        lock (Sync)
        {
            MergeKnownPoisNoLock(pois);
            RebuildSnapshotNoLock();
        }
    }

    private static void MergeKnownPoisNoLock(IEnumerable<GalaxyKnownPoiInfo>? pois)
    {
        if (pois == null)
            return;

        foreach (var poi in pois)
        {
            if (poi == null || string.IsNullOrWhiteSpace(poi.Id))
                continue;

            if (!KnownPoisById.TryGetValue(poi.Id, out var existing) ||
                poi.LastSeenUtc >= existing.LastSeenUtc)
            {
                KnownPoisById[poi.Id] = poi;
            }
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

    public static void MergeItemCatalog(IReadOnlyDictionary<string, ItemCatalogueEntry>? byId)
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

                ItemCatalogById[itemId] = CloneItemCatalogueEntry(entry);
            }

            RebuildSnapshotNoLock();
        }
    }

    public static void MergeShipCatalog(IReadOnlyDictionary<string, ShipCatalogueEntry>? byId)
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

                ShipCatalogById[shipId] = CloneShipCatalogueEntry(entry);
            }

            RebuildSnapshotNoLock();
        }
    }

    public static void MergeResourceLocations(
        string defaultSystemId,
        IEnumerable<POIInfo>? pois)
    {
        if (pois == null)
            return;

        bool changed = false;

        lock (Sync)
        {
            foreach (var poi in pois)
            {
                if (poi == null || string.IsNullOrWhiteSpace(poi.Id))
                    continue;

                if (poi.Resources == null || poi.Resources.Length == 0)
                    continue;

                string systemId = !string.IsNullOrWhiteSpace(poi.SystemId)
                    ? poi.SystemId
                    : defaultSystemId;
                if (string.IsNullOrWhiteSpace(systemId))
                    continue;

                foreach (var resource in poi.Resources)
                {
                    string resourceId = resource?.ResourceId ?? "";
                    if (string.IsNullOrWhiteSpace(resourceId))
                        continue;

                    if (!ResourceSystemsById.TryGetValue(resourceId, out var systemSet))
                    {
                        systemSet = new HashSet<string>(StringComparer.Ordinal);
                        ResourceSystemsById[resourceId] = systemSet;
                    }

                    if (!ResourcePoisById.TryGetValue(resourceId, out var poiSet))
                    {
                        poiSet = new HashSet<string>(StringComparer.Ordinal);
                        ResourcePoisById[resourceId] = poiSet;
                    }

                    if (systemSet.Add(systemId))
                        changed = true;
                    if (poiSet.Add(poi.Id))
                        changed = true;
                }
            }

            if (changed)
                RebuildSnapshotNoLock();
        }
    }

    public static GalaxyState Snapshot()
    {
        lock (Sync)
            return CloneGalaxyState(_snapshot);
    }

    public static List<GalaxyKnownPoiInfo> GetKnownPois()
    {
        lock (Sync)
            return KnownPoisById.Values
                .Select(poi => new GalaxyKnownPoiInfo
                {
                    Id = poi.Id,
                    SystemId = poi.SystemId,
                    Name = poi.Name,
                    Type = poi.Type,
                    X = poi.X,
                    Y = poi.Y,
                    HasBase = poi.HasBase,
                    BaseId = poi.BaseId,
                    BaseName = poi.BaseName,
                    LastSeenUtc = poi.LastSeenUtc
                })
                .ToList();
    }

    private static void RebuildSnapshotNoLock()
    {
        var marketsClone = MarketsByStation.ToDictionary(
            kvp => kvp.Key,
            kvp => CloneMarket(kvp.Value),
            StringComparer.Ordinal);

        var mapWithKnownPois = CloneMap(_map);
        mapWithKnownPois.KnownPois = KnownPoisById.Values
            .Select(poi => new GalaxyKnownPoiInfo
            {
                Id = poi.Id,
                SystemId = poi.SystemId,
                Name = poi.Name,
                Type = poi.Type,
                X = poi.X,
                Y = poi.Y,
                HasBase = poi.HasBase,
                BaseId = poi.BaseId,
                BaseName = poi.BaseName,
                LastSeenUtc = poi.LastSeenUtc
            })
            .ToList();

        _snapshot = new GalaxyState
        {
            Map = mapWithKnownPois,
            Market = new GalaxyMarket
            {
                MarketsByStation = marketsClone,
                GlobalMedianBuyPrices = BuildGlobalMedianBuyPrices(marketsClone.Values),
                GlobalMedianSellPrices = BuildGlobalMedianSellPrices(marketsClone.Values),
                GlobalWeightedMidPrices = BuildGlobalWeightedMidPrices(marketsClone.Values)
            },
            Catalog = new GalaxyCatalog
            {
                ItemsById = CloneItemCatalogById(ItemCatalogById),
                ShipsById = CloneShipCatalogById(ShipCatalogById)
            },
            Resources = new GalaxyResources
            {
                SystemsByResource = BuildResourceIndexSnapshot(ResourceSystemsById),
                PoisByResource = BuildResourceIndexSnapshot(ResourcePoisById)
            },
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    private static Dictionary<string, string[]> BuildResourceIndexSnapshot(
        Dictionary<string, HashSet<string>> index)
    {
        return index.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .OrderBy(v => v, StringComparer.Ordinal)
                .ToArray(),
            StringComparer.Ordinal);
    }

    private static Dictionary<string, ItemCatalogueEntry> CloneItemCatalogById(
        IReadOnlyDictionary<string, ItemCatalogueEntry> source)
    {
        var clone = new Dictionary<string, ItemCatalogueEntry>(StringComparer.Ordinal);
        foreach (var (id, entry) in source)
        {
            if (string.IsNullOrWhiteSpace(id) || entry == null)
                continue;

            clone[id] = CloneItemCatalogueEntry(entry);
        }

        return clone;
    }

    private static Dictionary<string, ShipCatalogueEntry> CloneShipCatalogById(
        IReadOnlyDictionary<string, ShipCatalogueEntry> source)
    {
        var clone = new Dictionary<string, ShipCatalogueEntry>(StringComparer.Ordinal);
        foreach (var (id, entry) in source)
        {
            if (string.IsNullOrWhiteSpace(id) || entry == null)
                continue;

            clone[id] = CloneShipCatalogueEntry(entry);
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
                ItemsById = CloneItemCatalogById(source.Catalog.ItemsById),
                ShipsById = CloneShipCatalogById(source.Catalog.ShipsById)
            },
            Resources = new GalaxyResources
            {
                SystemsByResource = CloneResourceIndex(
                    source.Resources?.SystemsByResource ?? new Dictionary<string, string[]>(StringComparer.Ordinal)),
                PoisByResource = CloneResourceIndex(
                    source.Resources?.PoisByResource ?? new Dictionary<string, string[]>(StringComparer.Ordinal))
            },
            UpdatedAtUtc = source.UpdatedAtUtc
        };
    }

    private static Dictionary<string, string[]> CloneResourceIndex(
        Dictionary<string, string[]> source)
    {
        return source.ToDictionary(
            kvp => kvp.Key,
            kvp => (kvp.Value ?? Array.Empty<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToArray(),
            StringComparer.Ordinal);
    }

    private static GalaxyMapSnapshot CloneMap(GalaxyMapSnapshot source)
    {
        return new GalaxyMapSnapshot
        {
            Systems = (source.Systems ?? new List<GalaxySystemInfo>())
                .Select(system => new GalaxySystemInfo
                {
                    Id = system.Id,
                    Empire = system.Empire,
                    IsStronghold = system.IsStronghold,
                    X = system.X,
                    Y = system.Y,
                    Connections = (system.Connections ?? new List<string>())
                        .Where(c => !string.IsNullOrWhiteSpace(c))
                        .ToList(),
                    Pois = (system.Pois ?? new List<GalaxyPoiInfo>())
                        .Select(poi => new GalaxyPoiInfo
                        {
                            Id = poi.Id,
                            X = poi.X,
                            Y = poi.Y
                        })
                        .ToList()
                })
                .ToList(),
            KnownPois = (source.KnownPois ?? new List<GalaxyKnownPoiInfo>())
                .Select(poi => new GalaxyKnownPoiInfo
                {
                    Id = poi.Id,
                    SystemId = poi.SystemId,
                    Name = poi.Name,
                    Type = poi.Type,
                    X = poi.X,
                    Y = poi.Y,
                    HasBase = poi.HasBase,
                    BaseId = poi.BaseId,
                    BaseName = poi.BaseName,
                    LastSeenUtc = poi.LastSeenUtc
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

    private static ItemCatalogueEntry CloneItemCatalogueEntry(ItemCatalogueEntry entry)
    {
        return new ItemCatalogueEntry
        {
            Id = entry.Id,
            Name = entry.Name,
            ClassId = entry.ClassId,
            Class = entry.Class,
            Category = entry.Category,
            Type = entry.Type,
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

    private static ShipCatalogueEntry CloneShipCatalogueEntry(ShipCatalogueEntry entry)
    {
        return new ShipCatalogueEntry
        {
            Id = entry.Id,
            Name = entry.Name,
            ClassId = entry.ClassId,
            Class = entry.Class,
            Category = entry.Category,
            Type = entry.Type,
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
