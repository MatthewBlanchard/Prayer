using System;
using System.Collections.Generic;
using System.Linq;

public static class AppUiStateBuilder
{
    public static (
        SpaceUiModel SpaceModel,
        TradeUiModel? TradeModel,
        ShipyardUiModel? ShipyardModel,
        CatalogUiModel? CatalogModel,
        CraftingUiModel? CraftingModel)
        BuildUiState(GameState state)
    {
        var spaceModel = BuildSpaceModel(state);
        var tradeModel = BuildTradeModel(state);
        var shipyardModel = state.Docked && string.Equals(state.CurrentPOI?.Type, "station", StringComparison.Ordinal)
            ? BuildShipyardModel(state)
            : null;
        var catalog = BuildCatalogModel(state);
        var crafting = BuildCraftingModel(state);
        return (spaceModel, tradeModel, shipyardModel, catalog, crafting);
    }

    private static SpaceUiModel BuildSpaceModel(GameState state)
    {
        var currentSystem = (state.System ?? string.Empty).Trim();
        var currentPoiId = (state.CurrentPOI?.Id ?? string.Empty).Trim();

        var mapBySystemId = (state.Galaxy?.Map?.Systems ?? new List<GalaxySystemInfo>())
            .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Id))
            .GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        mapBySystemId.TryGetValue(currentSystem, out var currentSystemEntry);

        var mapSystems = mapBySystemId.Values
            .Select(systemEntry =>
            {
                var id = systemEntry.Id;
                var connections = (systemEntry.Connections ?? new List<string>())
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c => c.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return new SpaceUiSystemNode(
                    id,
                    systemEntry.X,
                    systemEntry.Y,
                    systemEntry.Empire ?? string.Empty,
                    systemEntry.IsStronghold,
                    string.Equals(id, currentSystem, StringComparison.OrdinalIgnoreCase),
                    connections);
            })
            .OrderByDescending(s => s.IsCurrent)
            .ThenBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var poiMap = new Dictionary<string, SpaceUiPoi>(StringComparer.OrdinalIgnoreCase);

        foreach (var poi in currentSystemEntry?.Pois ?? new List<GalaxyPoiInfo>())
        {
            if (poi == null || string.IsNullOrWhiteSpace(poi.Id))
                continue;
            var poiId = poi.Id.Trim();
            poiMap[poiId] = new SpaceUiPoi(
                poiId,
                poiId,
                string.Empty,
                poi.X,
                poi.Y);
        }

        foreach (var knownPoi in state.Galaxy?.Map?.KnownPois ?? new List<GalaxyKnownPoiInfo>())
        {
            if (knownPoi == null || string.IsNullOrWhiteSpace(knownPoi.Id))
                continue;
            if (!string.Equals(knownPoi.SystemId ?? string.Empty, currentSystem, StringComparison.OrdinalIgnoreCase))
                continue;

            var poiId = knownPoi.Id.Trim();
            var label = !string.IsNullOrWhiteSpace(knownPoi.Name) ? knownPoi.Name.Trim() : poiId;
            var type = knownPoi.Type ?? string.Empty;
            poiMap[poiId] = new SpaceUiPoi(
                poiId,
                label,
                type,
                knownPoi.X ?? poiMap.GetValueOrDefault(poiId)?.X,
                knownPoi.Y ?? poiMap.GetValueOrDefault(poiId)?.Y,
                knownPoi.HasBase,
                knownPoi.BaseName ?? string.Empty);
        }

        if (!string.IsNullOrWhiteSpace(state.CurrentPOI?.Id))
        {
            var currentPoiLabel = !string.IsNullOrWhiteSpace(state.CurrentPOI.Name)
                ? state.CurrentPOI.Name.Trim()
                : state.CurrentPOI.Id.Trim();
            poiMap[state.CurrentPOI.Id.Trim()] = new SpaceUiPoi(
                state.CurrentPOI.Id.Trim(),
                currentPoiLabel,
                state.CurrentPOI.Type ?? string.Empty,
                state.CurrentPOI.X,
                state.CurrentPOI.Y,
                state.CurrentPOI.HasBase,
                state.CurrentPOI.BaseName ?? state.CurrentPOI.BaseId ?? string.Empty,
                state.CurrentPOI.Online,
                FormatPoiResources(state.CurrentPOI.Resources));
        }

        var pois = poiMap.Values
            .OrderBy(p => p.Target, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var cargoItems = (state.Ship.Cargo ?? new Dictionary<string, ItemStack>())
            .Values
            .OrderBy(v => v.ItemId, StringComparer.OrdinalIgnoreCase)
            .Select(v =>
            {
                var itemId = v.ItemId ?? string.Empty;
                return new SpaceUiCargoItem(
                    itemId,
                    Math.Max(0, v.Quantity),
                    ResolveMedianBidPrice(state, itemId) ?? ResolveMedianAskPrice(state, itemId));
            })
            .ToArray();

        return new SpaceUiModel(
            currentSystem,
            string.IsNullOrWhiteSpace(currentPoiId) ? "(unknown)" : currentPoiId,
            state.Docked ? "True" : "False",
            state.Credits,
            $"{state.Ship.Fuel}/{state.Ship.MaxFuel}",
            $"{state.Ship.Hull}/{state.Ship.MaxHull}",
            $"{state.Ship.Shield}/{state.Ship.MaxShield}",
            $"{state.Ship.CargoUsed}/{state.Ship.CargoCapacity}",
            pois,
            cargoItems,
            mapSystems);
    }

    private static IReadOnlyList<string> FormatPoiResources(IReadOnlyList<PoiResourceInfo>? resources)
    {
        if (resources == null || resources.Count == 0)
            return Array.Empty<string>();

        return resources
            .Where(r => r != null && !string.IsNullOrWhiteSpace(r.ResourceId))
            .Select(r =>
            {
                var name = !string.IsNullOrWhiteSpace(r.Name)
                    ? r.Name.Trim()
                    : r.ResourceId.Trim();
                var richness = !string.IsNullOrWhiteSpace(r.RichnessText)
                    ? r.RichnessText.Trim()
                    : (r.Richness.HasValue ? $"{r.Richness.Value}%" : "n/a");
                var remaining = !string.IsNullOrWhiteSpace(r.RemainingDisplay)
                    ? r.RemainingDisplay.Trim()
                    : (r.Remaining.HasValue ? r.Remaining.Value.ToString() : "n/a");
                return $"{name} - rich {richness} - rem {remaining}";
            })
            .OrderBy(line => line, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static TradeUiModel BuildTradeModel(GameState state)
    {
        var cargoItems = (state.Ship.Cargo ?? new Dictionary<string, ItemStack>())
            .Values
            .OrderBy(v => v.ItemId, StringComparer.OrdinalIgnoreCase)
            .Select(v =>
            {
                var itemId = v.ItemId ?? string.Empty;
                return new TradeUiItem(
                    itemId,
                    Math.Max(0, v.Quantity),
                    ResolveMedianBidPrice(state, itemId),
                    ResolveMedianAskPrice(state, itemId),
                    $"{itemId} x{v.Quantity}");
            })
            .ToArray();

        var storageItems = (state.StorageItems ?? new Dictionary<string, ItemStack>())
            .Values
            .OrderBy(v => v.ItemId, StringComparer.OrdinalIgnoreCase)
            .Select(v =>
            {
                var itemId = v.ItemId ?? string.Empty;
                return new TradeUiItem(
                    itemId,
                    Math.Max(0, v.Quantity),
                    ResolveMedianBidPrice(state, itemId),
                    ResolveMedianAskPrice(state, itemId),
                    $"{itemId} x{v.Quantity}");
            })
            .ToArray();

        var buyOrders = (state.OwnBuyOrders ?? Array.Empty<OpenOrderInfo>())
            .Select(order =>
            {
                var itemId = order.ItemId ?? string.Empty;
                return new TradeUiOrder(
                    order.OrderId ?? string.Empty,
                    itemId,
                    Math.Max(0, order.Quantity),
                    order.PriceEach,
                    $"BUY {itemId} qty={order.Quantity} price={order.PriceEach}");
            })
            .ToArray();

        var sellOrders = (state.OwnSellOrders ?? Array.Empty<OpenOrderInfo>())
            .Select(order =>
            {
                var itemId = order.ItemId ?? string.Empty;
                return new TradeUiOrder(
                    order.OrderId ?? string.Empty,
                    itemId,
                    Math.Max(0, order.Quantity),
                    order.PriceEach,
                    $"SELL {itemId} qty={order.Quantity} price={order.PriceEach}");
            })
            .ToArray();

        var allItems = (state.Galaxy?.Catalog?.ItemsById?.Values ?? Enumerable.Empty<CatalogueEntry>())
            .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Id))
            .OrderBy(e => string.IsNullOrWhiteSpace(e.Name) ? e.Id : e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new TradeCatalogItem(
                e.Id,
                string.IsNullOrWhiteSpace(e.Name) ? e.Id : e.Name,
                string.IsNullOrWhiteSpace(e.Category) ? "Unknown" : e.Category,
                e.Tier,
                HasLocalBuyOrders(state, e.Id),
                HasLocalSellOrders(state, e.Id),
                ResolveMedianBidPrice(state, e.Id),
                ResolveMedianAskPrice(state, e.Id),
                ResolveGlobalMedianBidPrice(state, e.Id),
                ResolveGlobalMedianAskPrice(state, e.Id)))
            .ToArray();

        return new TradeUiModel(
            state.CurrentMarket != null,
            state.CurrentPOI?.Id ?? "(unknown)",
            state.Credits,
            state.StorageCredits,
            $"{state.Ship.Fuel}/{state.Ship.MaxFuel}",
            $"{state.Ship.CargoUsed}/{state.Ship.CargoCapacity}",
            cargoItems,
            storageItems,
            allItems,
            buyOrders,
            sellOrders);
    }

    private static ShipyardUiModel BuildShipyardModel(GameState state)
    {
        var showroom = (state.ShipyardShowroom ?? Array.Empty<ShipyardShowroomEntry>())
            .Where(v => !string.IsNullOrWhiteSpace(v.ShipClassId))
            .Select(v =>
            {
                return new ShipyardUiEntry(
                    Id: v.ShipClassId,
                    DisplayText: FormatShowroomLine(v),
                    Faction: "Local",
                    Name: string.IsNullOrWhiteSpace(v.Name) ? null : v.Name,
                    ClassId: v.ShipClassId,
                    Category: string.IsNullOrWhiteSpace(v.Category) ? null : v.Category,
                    Tier: v.Tier,
                    Scale: v.Scale,
                    Hull: v.Hull,
                    Shield: v.Shield,
                    Cargo: v.Cargo,
                    Speed: v.Speed,
                    Price: v.Price);
            })
            .ToArray();

        var listings = (state.ShipyardListings ?? Array.Empty<ShipyardListingEntry>())
            .Where(v => !string.IsNullOrWhiteSpace(v.ListingId))
            .Select(v =>
            {
                return new ShipyardUiEntry(
                    Id: v.ListingId,
                    DisplayText: FormatListingLine(v),
                    Faction: "Listings",
                    Name: string.IsNullOrWhiteSpace(v.Name) ? null : v.Name,
                    ClassId: string.IsNullOrWhiteSpace(v.ClassId) ? null : v.ClassId,
                    Price: v.Price);
            })
            .ToArray();

        var galaxyShips = state.Galaxy?.Catalog?.ShipsById?.Values
            ?? Enumerable.Empty<CatalogueEntry>();
        var catalogShips = galaxyShips
            .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Id))
            .OrderBy(e => ResolveShipFaction(e), StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => string.IsNullOrWhiteSpace(e.Name) ? e.Id : e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new ShipyardUiEntry(
                Id: e.Id,
                DisplayText: string.IsNullOrWhiteSpace(e.Name) ? e.Id : $"{e.Id} ({e.Name})",
                Faction: ResolveShipFaction(e),
                Name: string.IsNullOrWhiteSpace(e.Name) ? null : e.Name,
                ClassId: string.IsNullOrWhiteSpace(e.ClassId) ? e.Class : e.ClassId,
                Category: string.IsNullOrWhiteSpace(e.Category) ? null : e.Category,
                Tier: e.Tier,
                Scale: e.Scale,
                Hull: e.Hull ?? e.BaseHull,
                Shield: e.Shield ?? e.BaseShield,
                Cargo: e.Cargo ?? e.CargoCapacity,
                Speed: e.Speed ?? e.BaseSpeed,
                Price: e.Price))
            .ToArray();

        int totalShips = catalogShips.Length;

        return new ShipyardUiModel(
            state.CurrentPOI?.Id ?? "(unknown)",
            state.Credits,
            state.StorageCredits,
            $"{state.Ship.Fuel}/{state.Ship.MaxFuel}",
            $"{state.Ship.CargoUsed}/{state.Ship.CargoCapacity}",
            "Galaxy",
            totalShips,
            showroom,
            listings,
            catalogShips);
    }

    private static CatalogUiModel BuildCatalogModel(GameState state)
    {
        var itemEntries = (state.Galaxy?.Catalog?.ItemsById?.Values ?? Enumerable.Empty<CatalogueEntry>())
            .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Id))
            .OrderBy(e => string.IsNullOrWhiteSpace(e.Category) ? "Unknown" : e.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Tier ?? int.MaxValue)
            .ThenBy(e => string.IsNullOrWhiteSpace(e.Name) ? e.Id : e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new CatalogUiEntry(
                e.Id,
                string.IsNullOrWhiteSpace(e.Name) ? e.Id : e.Name,
                string.IsNullOrWhiteSpace(e.Category) ? "Unknown" : e.Category,
                e.Tier,
                e.Price,
                string.IsNullOrWhiteSpace(e.Name) ? e.Id : $"{e.Id} ({e.Name})"))
            .ToArray();

        var shipEntries = (state.Galaxy?.Catalog?.ShipsById?.Values ?? Enumerable.Empty<CatalogueEntry>())
            .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Id))
            .OrderBy(e => string.IsNullOrWhiteSpace(e.Category) ? "Unknown" : e.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Tier ?? int.MaxValue)
            .ThenBy(e => string.IsNullOrWhiteSpace(e.Name) ? e.Id : e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new CatalogUiEntry(
                e.Id,
                string.IsNullOrWhiteSpace(e.Name) ? e.Id : e.Name,
                string.IsNullOrWhiteSpace(e.Category) ? "Unknown" : e.Category,
                e.Tier,
                e.Price,
                string.IsNullOrWhiteSpace(e.Name) ? e.Id : $"{e.Id} ({e.Name})"))
            .ToArray();

        return new CatalogUiModel(itemEntries, shipEntries);
    }

    private static CraftingUiModel BuildCraftingModel(GameState state)
    {
        var recipes = (state.AvailableRecipes ?? Array.Empty<CatalogueEntry>())
            .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Id))
            .OrderBy(e => string.IsNullOrWhiteSpace(e.Category) ? "Unknown" : e.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Tier ?? int.MaxValue)
            .ThenBy(e => string.IsNullOrWhiteSpace(e.Name) ? e.Id : e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new CraftingUiEntry(
                e.Id,
                string.IsNullOrWhiteSpace(e.Name) ? e.Id : e.Name,
                string.IsNullOrWhiteSpace(e.Category) ? "Unknown" : e.Category,
                e.Tier,
                string.IsNullOrWhiteSpace(e.Name) ? e.Id : $"`{e.Id}`: {e.Name}"))
            .ToArray();

        return new CraftingUiModel(
            state.Docked && recipes.Length > 0,
            state.CurrentPOI?.Id ?? "(unknown)",
            recipes);
    }

    private static string FormatShowroomLine(ShipyardShowroomEntry entry)
    {
        var name = string.IsNullOrWhiteSpace(entry.Name) ? "ship" : entry.Name;
        var classId = string.IsNullOrWhiteSpace(entry.ShipClassId) ? "-" : entry.ShipClassId;
        var parts = new List<string> { $"`{name}` ({classId})" };
        if (entry.Hull.HasValue || entry.Shield.HasValue || entry.Cargo.HasValue || entry.Speed.HasValue)
        {
            parts.Add(
                $"Hull {entry.Hull?.ToString() ?? "-"} | Shield {entry.Shield?.ToString() ?? "-"} | Cargo {entry.Cargo?.ToString() ?? "-"} | Speed {entry.Speed?.ToString() ?? "-"}");
        }

        if (entry.Price.HasValue)
            parts.Add($"@ {Math.Round(entry.Price.Value, 2):0.##}cr");

        return string.Join(" | ", parts);
    }

    private static string FormatListingLine(ShipyardListingEntry entry)
    {
        var listingId = string.IsNullOrWhiteSpace(entry.ListingId) ? "-" : entry.ListingId;
        var name = string.IsNullOrWhiteSpace(entry.Name) ? "ship" : entry.Name;
        var classId = string.IsNullOrWhiteSpace(entry.ClassId) ? "-" : entry.ClassId;
        var text = $"`{listingId}`: `{name}` ({classId})";
        if (entry.Price.HasValue)
            return $"{text} | @ {Math.Round(entry.Price.Value, 2):0.##}cr";
        return text;
    }

    private static string ResolveShipFaction(CatalogueEntry entry)
    {
        var category = (entry.Category ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(category))
            return category;

        var classId = !string.IsNullOrWhiteSpace(entry.ClassId)
            ? entry.ClassId
            : entry.Class;
        var raw = (classId ?? string.Empty).Trim();
        if (raw.Length == 0)
            return "Unknown";

        int sep = raw.IndexOf('_');
        var token = sep > 0 ? raw[..sep] : raw;
        return string.IsNullOrWhiteSpace(token)
            ? "Unknown"
            : char.ToUpperInvariant(token[0]) + token[1..];
    }

    private static decimal? ResolveMedianBidPrice(GameState state, string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return null;

        if (state.CurrentMarket?.BuyOrders != null &&
            state.CurrentMarket.BuyOrders.TryGetValue(itemId, out var localBids))
        {
            var localMedian = ComputeMedianPriceFromOrders(localBids);
            if (localMedian.HasValue && localMedian.Value > 0m)
                return localMedian.Value;
        }

        if (state.Galaxy?.Market?.GlobalMedianBuyPrices != null &&
            state.Galaxy.Market.GlobalMedianBuyPrices.TryGetValue(itemId, out var globalMedian) &&
            globalMedian > 0m)
        {
            return globalMedian;
        }

        return null;
    }

    private static decimal? ResolveMedianAskPrice(GameState state, string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return null;

        if (state.CurrentMarket?.SellOrders != null &&
            state.CurrentMarket.SellOrders.TryGetValue(itemId, out var localAsks))
        {
            var localMedian = ComputeMedianPriceFromOrders(localAsks);
            if (localMedian.HasValue && localMedian.Value > 0m)
                return localMedian.Value;
        }

        if (state.Galaxy?.Market?.GlobalMedianSellPrices != null &&
            state.Galaxy.Market.GlobalMedianSellPrices.TryGetValue(itemId, out var globalMedian) &&
            globalMedian > 0m)
        {
            return globalMedian;
        }

        return null;
    }

    private static decimal? ResolveGlobalMedianBidPrice(GameState state, string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return null;

        if (state.Galaxy?.Market?.GlobalMedianBuyPrices != null &&
            state.Galaxy.Market.GlobalMedianBuyPrices.TryGetValue(itemId, out var globalMedian) &&
            globalMedian > 0m)
        {
            return globalMedian;
        }

        return null;
    }

    private static decimal? ResolveGlobalMedianAskPrice(GameState state, string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return null;

        if (state.Galaxy?.Market?.GlobalMedianSellPrices != null &&
            state.Galaxy.Market.GlobalMedianSellPrices.TryGetValue(itemId, out var globalMedian) &&
            globalMedian > 0m)
        {
            return globalMedian;
        }

        return null;
    }

    private static bool HasLocalBuyOrders(GameState state, string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return false;

        return state.CurrentMarket?.BuyOrders != null &&
               state.CurrentMarket.BuyOrders.TryGetValue(itemId, out var bids) &&
               bids != null &&
               bids.Any(o => o != null && o.Quantity > 0 && o.PriceEach > 0);
    }

    private static bool HasLocalSellOrders(GameState state, string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return false;

        return state.CurrentMarket?.SellOrders != null &&
               state.CurrentMarket.SellOrders.TryGetValue(itemId, out var asks) &&
               asks != null &&
               asks.Any(o => o != null && o.Quantity > 0 && o.PriceEach > 0);
    }

    private static decimal? ComputeMedianPriceFromOrders(List<MarketOrder>? orders)
    {
        if (orders == null || orders.Count == 0)
            return null;

        var expanded = new List<decimal>();
        foreach (var order in orders.Where(o => o.Quantity > 0 && o.PriceEach > 0))
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
}
