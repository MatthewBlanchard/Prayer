using System;
using System.Collections.Generic;
using System.Linq;

public static class AppUiStateBuilder
{
    public static (
        string SpaceStateMarkdown,
        string? TradeStateMarkdown,
        string? ShipyardStateMarkdown,
        string? CantinaStateMarkdown,
        string? CatalogStateMarkdown)
        BuildUiState(GameState state)
    {
        var space = BuildSpaceState(state);
        var trade = state.Docked ? BuildTradeState(state) : null;
        var shipyard = state.Docked && string.Equals(state.CurrentPOI?.Type, "station", StringComparison.Ordinal)
            ? BuildShipyardState(state)
            : null;
        var cantina = state.Docked && string.Equals(state.CurrentPOI?.Type, "station", StringComparison.Ordinal)
            ? BuildCantinaState(state)
            : null;
        var catalog = BuildCatalogState(state);
        return (space, trade, shipyard, cantina, catalog);
    }

    private static string BuildSpaceState(GameState state)
    {
        var pois = (state.POIs ?? Array.Empty<POIInfo>())
            .Select(p => $"- {p.Id} ({p.Type})")
            .ToArray();
        var cargo = FormatCargo(state.Cargo);
        var missions = FormatMissions(state.ActiveMissions);

        return
$@"CONTEXT: SPACE
SYSTEM: {state.System}
POI: {state.CurrentPOI?.Id ?? "(unknown)"}
DOCKED: {state.Docked}
CREDITS: {state.Credits}
FUEL: {state.Fuel}/{state.MaxFuel}
HULL: {state.Hull}/{state.MaxHull}
SHIELD: {state.Shield}/{state.MaxShield}
CARGO: {state.CargoUsed}/{state.CargoCapacity}

POIS
{(pois.Length == 0 ? "- (none)" : string.Join("\n", pois))}

CARGO ITEMS
{cargo}

ACTIVE MISSIONS
{missions}";
    }

    private static string BuildTradeState(GameState state)
    {
        var cargo = FormatCargo(state.Cargo);
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

    private static string BuildCantinaState(GameState state)
    {
        return
$@"CONTEXT: CANTINA
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
}
