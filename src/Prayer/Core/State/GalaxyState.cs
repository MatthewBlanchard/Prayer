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
    private static readonly Dictionary<string, GalaxyPoiKnowledge> PoiKnowledgeById = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, GalaxySystemKnowledge> SystemKnowledgeById = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, HashSet<string>> MiningCheckedPoisByResource = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<string>> MiningExploredSystemsByResource = new(StringComparer.OrdinalIgnoreCase);
    private static GalaxyState _snapshot = new();

    static GalaxyStateHub()
    {
        lock (Sync)
        {
            HydrateExplorationStateNoLock();
            RebuildSnapshotNoLock();
        }
    }

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

            UpsertPoiKnowledgeNoLock(
                poi.Id,
                poi.SystemId,
                poi.Name,
                poi.Type,
                poi.X,
                poi.Y,
                poi.HasBase,
                poi.BaseId,
                poi.BaseName,
                resources: null,
                markVisited: false,
                lastSeenUtc: poi.LastSeenUtc == default ? DateTime.UtcNow : poi.LastSeenUtc);
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
            {
                PersistExplorationStateNoLock();
                RebuildSnapshotNoLock();
            }
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

                string systemId = !string.IsNullOrWhiteSpace(poi.SystemId)
                    ? poi.SystemId
                    : defaultSystemId;
                if (string.IsNullOrWhiteSpace(systemId))
                    continue;

                if (UpsertPoiKnowledgeNoLock(
                        poi.Id,
                        systemId,
                        poi.Name,
                        poi.Type,
                        poi.X,
                        poi.Y,
                        poi.HasBase,
                        poi.BaseId,
                        poi.BaseName,
                        poi.Resources,
                        markVisited: poi.Resources != null && poi.Resources.Length > 0,
                        lastSeenUtc: DateTime.UtcNow))
                {
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

    public static bool MarkPoiVisited(
        string poiId,
        string? systemId = null,
        string? poiName = null,
        string? poiType = null,
        double? x = null,
        double? y = null,
        bool hasBase = false,
        string? baseId = null,
        string? baseName = null)
    {
        if (string.IsNullOrWhiteSpace(poiId))
            return false;

        lock (Sync)
        {
            bool changed = UpsertPoiKnowledgeNoLock(
                poiId,
                systemId,
                poiName,
                poiType,
                x,
                y,
                hasBase,
                baseId,
                baseName,
                resources: null,
                markVisited: true,
                lastSeenUtc: DateTime.UtcNow);

            if (!changed)
                return false;

            PersistExplorationStateNoLock();
            RebuildSnapshotNoLock();
            return true;
        }
    }

    public static bool MarkSystemSurveyed(string systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
            return false;

        lock (Sync)
        {
            if (!SystemKnowledgeById.TryGetValue(systemId, out var system))
            {
                system = new GalaxySystemKnowledge
                {
                    Id = systemId,
                    Surveyed = true
                };
                SystemKnowledgeById[systemId] = system;
            }
            else if (!system.Surveyed)
            {
                system.Surveyed = true;
            }
            else
            {
                return false;
            }

            PersistExplorationStateNoLock();
            RebuildSnapshotNoLock();
            return true;
        }
    }

    public static bool MarkMiningPoiChecked(string resourceId, string poiId)
    {
        if (string.IsNullOrWhiteSpace(resourceId) || string.IsNullOrWhiteSpace(poiId))
            return false;

        lock (Sync)
        {
            if (!MiningCheckedPoisByResource.TryGetValue(resourceId, out var poiIds))
            {
                poiIds = new HashSet<string>(StringComparer.Ordinal);
                MiningCheckedPoisByResource[resourceId] = poiIds;
            }

            if (!poiIds.Add(poiId))
                return false;

            PersistExplorationStateNoLock();
            RebuildSnapshotNoLock();
            return true;
        }
    }

    public static bool MarkMiningSystemExplored(string resourceId, string systemId)
    {
        if (string.IsNullOrWhiteSpace(resourceId) || string.IsNullOrWhiteSpace(systemId))
            return false;

        lock (Sync)
        {
            if (!MiningExploredSystemsByResource.TryGetValue(resourceId, out var systemIds))
            {
                systemIds = new HashSet<string>(StringComparer.Ordinal);
                MiningExploredSystemsByResource[resourceId] = systemIds;
            }

            if (!systemIds.Add(systemId))
                return false;

            PersistExplorationStateNoLock();
            RebuildSnapshotNoLock();
            return true;
        }
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

        var resourceSystemsById = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var resourcePoisById = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var poi in PoiKnowledgeById.Values)
        {
            if (poi == null || string.IsNullOrWhiteSpace(poi.Id))
                continue;
            if (poi.Resources == null || poi.Resources.Length == 0)
                continue;
            if (string.IsNullOrWhiteSpace(poi.SystemId))
                continue;

            foreach (var resource in poi.Resources)
            {
                string resourceId = resource?.ResourceId ?? "";
                if (string.IsNullOrWhiteSpace(resourceId))
                    continue;

                if (!resourceSystemsById.TryGetValue(resourceId, out var systemSet))
                {
                    systemSet = new HashSet<string>(StringComparer.Ordinal);
                    resourceSystemsById[resourceId] = systemSet;
                }

                if (!resourcePoisById.TryGetValue(resourceId, out var poiSet))
                {
                    poiSet = new HashSet<string>(StringComparer.Ordinal);
                    resourcePoisById[resourceId] = poiSet;
                }

                systemSet.Add(poi.SystemId);
                poiSet.Add(poi.Id);
            }
        }

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
                SystemsByResource = BuildResourceIndexSnapshot(resourceSystemsById),
                PoisByResource = BuildResourceIndexSnapshot(resourcePoisById)
            },
            Exploration = BuildExplorationSnapshotNoLock(),
            Knowledge = BuildKnowledgeSnapshotNoLock(),
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    private static GalaxyExplorationState BuildExplorationSnapshotNoLock()
    {
        var visitedPois = PoiKnowledgeById.Values
            .Where(p => p != null && p.Visited && !string.IsNullOrWhiteSpace(p.Id))
            .Select(p => p.Id)
            .ToHashSet(StringComparer.Ordinal);

        var poisBySystem = PoiKnowledgeById.Values
            .Where(p => p != null && !string.IsNullOrWhiteSpace(p.SystemId) && !string.IsNullOrWhiteSpace(p.Id))
            .GroupBy(p => p.SystemId, StringComparer.Ordinal);

        var exploredSystems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in poisBySystem)
        {
            if (group.All(p => p.Visited))
                exploredSystems.Add(group.Key);
        }

        var surveyedSystems = SystemKnowledgeById.Values
            .Where(s => s != null && s.Surveyed && !string.IsNullOrWhiteSpace(s.Id))
            .Select(s => s.Id)
            .ToHashSet(StringComparer.Ordinal);

        return new GalaxyExplorationState
        {
            ExploredSystems = exploredSystems,
            VisitedPois = visitedPois,
            SurveyedSystems = surveyedSystems,
            MiningCheckedPoisByResource = BuildResourceIndexSnapshot(MiningCheckedPoisByResource),
            MiningExploredSystemsByResource = BuildResourceIndexSnapshot(MiningExploredSystemsByResource)
        };
    }

    private static GalaxyKnowledgeState BuildKnowledgeSnapshotNoLock()
    {
        return new GalaxyKnowledgeState
        {
            PoisById = PoiKnowledgeById.ToDictionary(
                kvp => kvp.Key,
                kvp => ClonePoiKnowledge(kvp.Value),
                StringComparer.Ordinal),
            SystemsById = SystemKnowledgeById.ToDictionary(
                kvp => kvp.Key,
                kvp => new GalaxySystemKnowledge
                {
                    Id = kvp.Value.Id,
                    Surveyed = kvp.Value.Surveyed
                },
                StringComparer.Ordinal)
        };
    }

    private static void HydrateExplorationStateNoLock()
    {
        var legacy = ExplorationStateStore.Load();
        PoiKnowledgeById.Clear();
        SystemKnowledgeById.Clear();
        MiningCheckedPoisByResource.Clear();
        MiningExploredSystemsByResource.Clear();

        foreach (var poiId in legacy.ExploredPois ?? new HashSet<string>(StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(poiId))
            {
                PoiKnowledgeById[poiId] = new GalaxyPoiKnowledge
                {
                    Id = poiId,
                    Visited = true,
                    LastSeenUtc = legacy.UpdatedAtUtc == default ? DateTime.UtcNow : legacy.UpdatedAtUtc
                };
            }
        }

        foreach (var systemId in legacy.SurveyedSystems ?? new HashSet<string>(StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(systemId))
            {
                SystemKnowledgeById[systemId] = new GalaxySystemKnowledge
                {
                    Id = systemId,
                    Surveyed = true
                };
            }
        }

        MergeResourceIndexNoLock(legacy.MiningCheckedPoisByResource, MiningCheckedPoisByResource);
        MergeResourceIndexNoLock(legacy.MiningExploredSystemsByResource, MiningExploredSystemsByResource);
    }

    private static void PersistExplorationStateNoLock()
    {
        var exploration = BuildExplorationSnapshotNoLock();
        var snapshot = new ExplorationStateSnapshot
        {
            ExploredSystems = new HashSet<string>(exploration.ExploredSystems, StringComparer.Ordinal),
            ExploredPois = new HashSet<string>(exploration.VisitedPois, StringComparer.Ordinal),
            SurveyedSystems = new HashSet<string>(exploration.SurveyedSystems, StringComparer.Ordinal),
            MiningCheckedPoisByResource = CloneResourceIndexToSets(MiningCheckedPoisByResource),
            MiningExploredSystemsByResource = CloneResourceIndexToSets(MiningExploredSystemsByResource)
        };
        ExplorationStateStore.Save(snapshot);
    }

    private static void MergeResourceIndexNoLock(
        IDictionary<string, HashSet<string>>? source,
        IDictionary<string, HashSet<string>> target)
    {
        if (source == null || source.Count == 0)
            return;

        foreach (var (resourceId, ids) in source)
        {
            if (string.IsNullOrWhiteSpace(resourceId))
                continue;

            if (!target.TryGetValue(resourceId, out var existing))
            {
                existing = new HashSet<string>(StringComparer.Ordinal);
                target[resourceId] = existing;
            }

            foreach (var id in ids ?? new HashSet<string>(StringComparer.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(id))
                    existing.Add(id);
            }
        }
    }

    private static Dictionary<string, HashSet<string>> CloneResourceIndexToSets(
        Dictionary<string, HashSet<string>> source)
    {
        var clone = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (resourceId, ids) in source)
        {
            if (string.IsNullOrWhiteSpace(resourceId))
                continue;

            clone[resourceId] = new HashSet<string>(
                (ids ?? new HashSet<string>(StringComparer.Ordinal))
                .Where(v => !string.IsNullOrWhiteSpace(v)),
                StringComparer.Ordinal);
        }

        return clone;
    }

    private static bool UpsertPoiKnowledgeNoLock(
        string poiId,
        string? systemId,
        string? poiName,
        string? poiType,
        double? x,
        double? y,
        bool hasBase,
        string? baseId,
        string? baseName,
        PoiResourceInfo[]? resources,
        bool markVisited,
        DateTime lastSeenUtc)
    {
        if (string.IsNullOrWhiteSpace(poiId))
            return false;

        bool changed = false;
        if (!PoiKnowledgeById.TryGetValue(poiId, out var knowledge))
        {
            knowledge = new GalaxyPoiKnowledge { Id = poiId };
            PoiKnowledgeById[poiId] = knowledge;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(systemId) && !string.Equals(knowledge.SystemId, systemId, StringComparison.Ordinal))
        {
            knowledge.SystemId = systemId;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(poiName) && !string.Equals(knowledge.Name, poiName, StringComparison.Ordinal))
        {
            knowledge.Name = poiName;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(poiType) && !string.Equals(knowledge.Type, poiType, StringComparison.Ordinal))
        {
            knowledge.Type = poiType;
            changed = true;
        }

        if (!knowledge.X.HasValue && x.HasValue)
        {
            knowledge.X = x;
            changed = true;
        }

        if (!knowledge.Y.HasValue && y.HasValue)
        {
            knowledge.Y = y;
            changed = true;
        }

        if (hasBase && !knowledge.HasBase)
        {
            knowledge.HasBase = true;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(baseId) && !string.Equals(knowledge.BaseId, baseId, StringComparison.Ordinal))
        {
            knowledge.BaseId = baseId;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(baseName) && !string.Equals(knowledge.BaseName, baseName, StringComparison.Ordinal))
        {
            knowledge.BaseName = baseName;
            changed = true;
        }

        if (markVisited && !knowledge.Visited)
        {
            knowledge.Visited = true;
            changed = true;
        }

        if (resources != null && resources.Length > 0 && !HasSameResources(knowledge.Resources, resources))
        {
            knowledge.Resources = resources
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.ResourceId))
                .Select(ClonePoiResource)
                .ToArray();
            changed = true;
        }

        if (lastSeenUtc != default && lastSeenUtc >= knowledge.LastSeenUtc)
        {
            if (knowledge.LastSeenUtc != lastSeenUtc)
            {
                knowledge.LastSeenUtc = lastSeenUtc;
                changed = true;
            }
        }

        return changed;
    }

    private static bool HasSameResources(PoiResourceInfo[]? left, PoiResourceInfo[]? right)
    {
        var leftIds = (left ?? Array.Empty<PoiResourceInfo>())
            .Where(r => r != null && !string.IsNullOrWhiteSpace(r.ResourceId))
            .Select(r => r.ResourceId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        var rightIds = (right ?? Array.Empty<PoiResourceInfo>())
            .Where(r => r != null && !string.IsNullOrWhiteSpace(r.ResourceId))
            .Select(r => r.ResourceId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        return leftIds.SequenceEqual(rightIds, StringComparer.Ordinal);
    }

    private static PoiResourceInfo ClonePoiResource(PoiResourceInfo source)
    {
        return new PoiResourceInfo
        {
            ResourceId = source.ResourceId,
            Name = source.Name,
            RichnessText = source.RichnessText,
            Richness = source.Richness,
            Remaining = source.Remaining,
            RemainingDisplay = source.RemainingDisplay
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
            Exploration = CloneExploration(source.Exploration ?? new GalaxyExplorationState()),
            Knowledge = CloneKnowledge(source.Knowledge ?? new GalaxyKnowledgeState()),
            UpdatedAtUtc = source.UpdatedAtUtc
        };
    }

    private static GalaxyExplorationState CloneExploration(GalaxyExplorationState source)
    {
        return new GalaxyExplorationState
        {
            ExploredSystems = new HashSet<string>(
                source.ExploredSystems?.Where(v => !string.IsNullOrWhiteSpace(v)) ?? Array.Empty<string>(),
                StringComparer.Ordinal),
            VisitedPois = new HashSet<string>(
                source.VisitedPois?.Where(v => !string.IsNullOrWhiteSpace(v)) ?? Array.Empty<string>(),
                StringComparer.Ordinal),
            SurveyedSystems = new HashSet<string>(
                source.SurveyedSystems?.Where(v => !string.IsNullOrWhiteSpace(v)) ?? Array.Empty<string>(),
                StringComparer.Ordinal),
            MiningCheckedPoisByResource = CloneResourceIndex(
                source.MiningCheckedPoisByResource ?? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase),
            MiningExploredSystemsByResource = CloneResourceIndex(
                source.MiningExploredSystemsByResource ?? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private static GalaxyKnowledgeState CloneKnowledge(GalaxyKnowledgeState source)
    {
        return new GalaxyKnowledgeState
        {
            PoisById = (source.PoisById ?? new Dictionary<string, GalaxyPoiKnowledge>(StringComparer.Ordinal))
                .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value != null)
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => ClonePoiKnowledge(kvp.Value),
                    StringComparer.Ordinal),
            SystemsById = (source.SystemsById ?? new Dictionary<string, GalaxySystemKnowledge>(StringComparer.Ordinal))
                .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value != null)
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => new GalaxySystemKnowledge
                    {
                        Id = kvp.Value.Id,
                        Surveyed = kvp.Value.Surveyed
                    },
                    StringComparer.Ordinal)
        };
    }

    private static GalaxyPoiKnowledge ClonePoiKnowledge(GalaxyPoiKnowledge source)
    {
        return new GalaxyPoiKnowledge
        {
            Id = source.Id,
            SystemId = source.SystemId,
            Name = source.Name,
            Type = source.Type,
            X = source.X,
            Y = source.Y,
            HasBase = source.HasBase,
            BaseId = source.BaseId,
            BaseName = source.BaseName,
            Visited = source.Visited,
            LastSeenUtc = source.LastSeenUtc,
            Resources = (source.Resources ?? Array.Empty<PoiResourceInfo>())
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.ResourceId))
                .Select(ClonePoiResource)
                .ToArray()
        };
    }

    private static Dictionary<string, string[]> CloneResourceIndex(
        Dictionary<string, string[]> source,
        StringComparer? keyComparer = null)
    {
        return source.ToDictionary(
            kvp => kvp.Key,
            kvp => (kvp.Value ?? Array.Empty<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToArray(),
            keyComparer ?? StringComparer.Ordinal);
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
