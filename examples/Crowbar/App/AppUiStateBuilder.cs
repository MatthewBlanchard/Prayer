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
        string? CatalogStateMarkdown)
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
        var catalog = BuildCatalogState(state);
        return (space, trade, tradeModel, shipyard, shipyardModel, missions, catalog);
    }

    private static string BuildSpaceState(GameState state)
    {
        var pois = (state.POIs ?? Array.Empty<POIInfo>())
            .Select(p => $"- {p.Id} ({p.Type})")
            .ToArray();
        var cargo = FormatCargo(state.Ship.Cargo);

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
            .Select(v => new TradeUiItem(
                v.ItemId ?? string.Empty,
                Math.Max(0, v.Quantity),
                $"{v.ItemId} x{v.Quantity}"))
            .ToArray();

        var storageItems = (state.StorageItems ?? new Dictionary<string, ItemStack>())
            .Values
            .OrderBy(v => v.ItemId, StringComparer.OrdinalIgnoreCase)
            .Select(v => new TradeUiItem(
                v.ItemId ?? string.Empty,
                Math.Max(0, v.Quantity),
                $"{v.ItemId} x{v.Quantity}"))
            .ToArray();

        var openOrders = new List<TradeUiOrder>();
        foreach (var order in state.OwnBuyOrders ?? Array.Empty<OpenOrderInfo>())
        {
            var itemId = order.ItemId ?? string.Empty;
            openOrders.Add(new TradeUiOrder(
                "BUY",
                itemId,
                Math.Max(0, order.Quantity),
                order.PriceEach,
                $"BUY {itemId} qty={order.Quantity} price={order.PriceEach}"));
        }
        foreach (var order in state.OwnSellOrders ?? Array.Empty<OpenOrderInfo>())
        {
            var itemId = order.ItemId ?? string.Empty;
            openOrders.Add(new TradeUiOrder(
                "SELL",
                itemId,
                Math.Max(0, order.Quantity),
                order.PriceEach,
                $"SELL {itemId} qty={order.Quantity} price={order.PriceEach}"));
        }

        return new TradeUiModel(
            state.CurrentPOI?.Id ?? "(unknown)",
            state.Credits,
            state.StorageCredits,
            $"{state.Ship.Fuel}/{state.Ship.MaxFuel}",
            $"{state.Ship.CargoUsed}/{state.Ship.CargoCapacity}",
            cargoItems,
            storageItems,
            openOrders);
    }

    private static string BuildShipyardState(GameState state)
    {
        var showroom = state.ShipyardShowroomLines?.Length > 0
            ? string.Join("\n", state.ShipyardShowroomLines)
            : "- (none)";
        var listings = state.ShipyardListingLines?.Length > 0
            ? string.Join("\n", state.ShipyardListingLines)
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
        var showroom = (state.ShipyardShowroomLines ?? Array.Empty<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v =>
            {
                var trimmed = v.Trim();
                var id = ExtractLeadingToken(trimmed);
                return new ShipyardUiEntry(id, trimmed);
            })
            .ToArray();

        var listings = (state.ShipyardListingLines ?? Array.Empty<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v =>
            {
                var trimmed = v.Trim();
                var id = ExtractLeadingToken(trimmed);
                return new ShipyardUiEntry(id, trimmed);
            })
            .ToArray();

        var catalogShips = state.ShipCatalogue?.NormalizedEntries?
            .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Id))
            .Select(e => new ShipyardUiEntry(
                e.Id,
                string.IsNullOrWhiteSpace(e.Name) ? e.Id : $"{e.Id} ({e.Name})"))
            .ToArray() ?? Array.Empty<ShipyardUiEntry>();

        int currentPage = state.ShipCatalogue?.Page ?? 1;
        int totalPages = state.ShipCatalogue?.TotalPages ?? 1;
        int totalShips = state.ShipCatalogue?.Total ?? state.ShipCatalogue?.TotalItems ?? 0;

        return new ShipyardUiModel(
            state.CurrentPOI?.Id ?? "(unknown)",
            state.Credits,
            state.StorageCredits,
            $"{state.Ship.Fuel}/{state.Ship.MaxFuel}",
            $"{state.Ship.CargoUsed}/{state.Ship.CargoCapacity}",
            $"{currentPage}/{totalPages}",
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

    private static string BuildCatalogState(GameState state)
    {
        var items = state.Galaxy?.Catalog?.ItemsById?.Values
            .Select(e => $"- {e.Id} ({e.Name})")
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();
        var ships = state.Galaxy?.Catalog?.ShipsById?.Values
            .Select(e => $"- {e.Id} ({e.Name})")
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

        return
$@"ITEMS
{(items.Length == 0 ? "- (none)" : string.Join("\n", items))}

SHIPS
{(ships.Length == 0 ? "- (none)" : string.Join("\n", ships))}";
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

    private static string ExtractLeadingToken(string line)
    {
        var value = (line ?? string.Empty).Trim();
        if (value.Length == 0)
            return string.Empty;

        return value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?
            .Trim('`', '[', ']', '(', ')', ',') ?? string.Empty;
    }
}
