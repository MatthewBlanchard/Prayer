using System;
using System.Collections.Generic;
using System.Linq;

public static class AppUiStateBuilder
{
    public static (
        string SpaceStateMarkdown,
        string? TradeStateMarkdown,
        TradeUiModel? TradeModel,
        string? ShipyardStateMarkdown,
        ShipyardUiModel? ShipyardModel,
        string? MissionsStateMarkdown,
        CatalogUiModel? CatalogModel)
        BuildUiState(GameState state)
    {
        var space = BuildSpaceState(state);
        var trade = state.Docked ? BuildTradeState(state) : null;
        var tradeModel = state.Docked ? BuildTradeModel(state) : null;
        var shipyard = state.Docked && string.Equals(state.CurrentPOI?.Type, "station", StringComparison.Ordinal)
            ? BuildShipyardState(state)
            : null;
        var shipyardModel = state.Docked && string.Equals(state.CurrentPOI?.Type, "station", StringComparison.Ordinal)
            ? BuildShipyardModel(state)
            : null;
        var missions = state.Docked && string.Equals(state.CurrentPOI?.Type, "station", StringComparison.Ordinal)
            ? BuildMissionsState(state)
            : null;
        var catalog = BuildCatalogModel(state);
        return (space, trade, tradeModel, shipyard, shipyardModel, missions, catalog);
    }

    private static string BuildSpaceState(GameState state)
    {
        var pois = (state.POIs ?? Array.Empty<POIInfo>())
            .Select(p => $"- {p.Id} ({p.Type})")
            .ToArray();
        var cargo = FormatCargoForSpace(state, state.Ship.Cargo);

        return
$@"CONTEXT: SPACE
SYSTEM: {state.System}
POI: {state.CurrentPOI?.Id ?? "(unknown)"}
DOCKED: {state.Docked}
CREDITS: {state.Credits}
FUEL: {state.Ship.Fuel}/{state.Ship.MaxFuel}
HULL: {state.Ship.Hull}/{state.Ship.MaxHull}
SHIELD: {state.Ship.Shield}/{state.Ship.MaxShield}
CARGO: {state.Ship.CargoUsed}/{state.Ship.CargoCapacity}

POIS
{(pois.Length == 0 ? "- (none)" : string.Join("\n", pois))}

CARGO ITEMS
{cargo}";
    }

    private static string BuildTradeState(GameState state)
    {
        var cargo = FormatCargo(state.Ship.Cargo);
        var storage = FormatCargo(state.StorageItems);
        var orders = FormatOrders(state.OwnBuyOrders, state.OwnSellOrders);

        return
$@"CONTEXT: TRADE TERMINAL
STATION: {state.CurrentPOI?.Id ?? "(unknown)"}
CREDITS: {state.Credits}
STATION CREDITS: {state.StorageCredits}

CARGO
{cargo}

STORAGE
{storage}

OPEN ORDERS
{orders}";
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

    private static string BuildShipyardState(GameState state)
    {
        var showroom = state.ShipyardShowroom?.Length > 0
            ? string.Join("\n", state.ShipyardShowroom.Select(FormatShowroomLine))
            : "- (none)";
        var listings = state.ShipyardListings?.Length > 0
            ? string.Join("\n", state.ShipyardListings.Select(FormatListingLine))
            : "- (none)";

        return
$@"CONTEXT: SHIPYARD
STATION: {state.CurrentPOI?.Id ?? "(unknown)"}
SHOWROOM
{showroom}

PLAYER LISTINGS
{listings}";
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

    private static string BuildMissionsState(GameState state)
    {
        return
$@"CONTEXT: MISSIONS
STATION: {state.CurrentPOI?.Id ?? "(unknown)"}
AVAILABLE MISSIONS
{FormatMissions(state.AvailableMissions)}";
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

    private static string FormatCargo(Dictionary<string, ItemStack>? cargo)
    {
        if (cargo == null || cargo.Count == 0)
            return "- (empty)";

        return string.Join(
            "\n",
            cargo.Values
                .OrderBy(v => v.ItemId, StringComparer.OrdinalIgnoreCase)
                .Select(v => $"- {v.ItemId} x{v.Quantity}"));
    }

    private static string FormatCargoForSpace(GameState state, Dictionary<string, ItemStack>? cargo)
    {
        if (cargo == null || cargo.Count == 0)
            return "- (empty)";

        return string.Join(
            "\n",
            cargo.Values
                .OrderBy(v => v.ItemId, StringComparer.OrdinalIgnoreCase)
                .Select(v =>
                {
                    var itemId = v.ItemId ?? string.Empty;
                    var medianPrice = ResolveMedianBidPrice(state, itemId) ?? ResolveMedianAskPrice(state, itemId);
                    var suffix = medianPrice.HasValue && medianPrice.Value > 0m
                        ? $" {Math.Round(medianPrice.Value, 2):0.##}cr"
                        : string.Empty;
                    return $"- {itemId} x{v.Quantity}{suffix}";
                }));
    }

    private static string FormatMissions(MissionInfo[]? missions)
    {
        if (missions == null || missions.Length == 0)
            return "- (none)";

        return string.Join(
            "\n",
            missions.Select(m =>
            {
                var name = !string.IsNullOrWhiteSpace(m.Title)
                    ? m.Title
                    : (!string.IsNullOrWhiteSpace(m.MissionId) ? m.MissionId : m.Id);
                var progress = !string.IsNullOrWhiteSpace(m.ProgressText)
                    ? m.ProgressText
                    : m.ObjectivesSummary;
                return $"- {name}: {progress}";
            }));
    }

    private static string FormatOrders(OpenOrderInfo[]? buy, OpenOrderInfo[]? sell)
    {
        var lines = new List<string>();
        foreach (var order in buy ?? Array.Empty<OpenOrderInfo>())
            lines.Add($"- BUY {order.ItemId} qty={order.Quantity} price={order.PriceEach}");
        foreach (var order in sell ?? Array.Empty<OpenOrderInfo>())
            lines.Add($"- SELL {order.ItemId} qty={order.Quantity} price={order.PriceEach}");

        return lines.Count == 0 ? "- (none)" : string.Join("\n", lines);
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
