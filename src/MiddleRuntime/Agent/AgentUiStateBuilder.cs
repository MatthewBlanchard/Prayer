using System;
using System.Linq;

public interface IAgentUiStateBuilder
{
    (
        string SpaceStateMarkdown,
        string? TradeStateMarkdown,
        string? ShipyardStateMarkdown,
        string? CantinaStateMarkdown,
        string? CatalogStateMarkdown)
        BuildUiState(GameState state);
}

public sealed class AgentUiStateBuilder : IAgentUiStateBuilder
{
    public (
        string SpaceStateMarkdown,
        string? TradeStateMarkdown,
        string? ShipyardStateMarkdown,
        string? CantinaStateMarkdown,
        string? CatalogStateMarkdown)
        BuildUiState(GameState state)
    {
        var spaceState = BuildSpaceUiState(state);
        var tradeState = state.Docked
            ? BuildTradeUiState(state)
            : null;
        var shipyardState = state.Docked && state.CurrentPOI.IsStation
            ? BuildShipyardUiState(state)
            : null;
        var cantinaState = state.Docked && state.CurrentPOI.IsStation
            ? BuildCantinaUiState(state)
            : null;
        var catalogState = BuildCatalogUiState(state);

        return (spaceState, tradeState, shipyardState, cantinaState, catalogState);
    }

    private static string BuildSpaceUiState(GameState state)
    {
        var baseSpaceState = state.ToDisplayText();
        var activeMissions = FormatActiveMissionsForSpace(state.ActiveMissions);

        return
$@"{baseSpaceState}

ACTIVE MISSIONS
{activeMissions}";
    }

    private static string BuildTradeUiState(GameState state)
    {
        var prices = state.BuildEstimatedItemPrices();
        var cargo = GameState.StripMarkdown(GameState.FormatCargo(state.Cargo, prices));
        var storage = state.StorageItems != null && state.StorageItems.Count > 0
            ? GameState.StripMarkdown(GameState.FormatCargo(state.StorageItems, prices))
            : "";
        var economy = GameState.StripMarkdown(
            GameState.FormatEconomy(state.EconomyDeals, state.OwnBuyOrders, state.OwnSellOrders));
        var storageSection = string.IsNullOrWhiteSpace(storage)
            ? ""
            : $"\nSTORAGE\n{storage}\n";

        return
$@"CONTEXT: TRADE TERMINAL
STATION: {state.CurrentPOI.Id}
CREDITS: {state.Credits}
STATION CREDITS: {state.StorageCredits}
FUEL: {state.Fuel}/{state.MaxFuel}
CARGO: {state.CargoUsed}/{state.CargoCapacity}

CARGO ITEMS
{cargo}
{storageSection}
ECONOMY
{economy}{state.BuildNotificationsDisplaySection()}";
    }

    private static string BuildShipyardUiState(GameState state)
    {
        var prices = state.BuildEstimatedItemPrices();
        var cargo = GameState.StripMarkdown(GameState.FormatCargo(state.Cargo, prices));
        var showroom = GameState.StripMarkdown(GameState.FormatShipyardShowroomLines(state.ShipyardShowroomLines));
        var listings = GameState.StripMarkdown(GameState.FormatShipyardShowroomLines(state.ShipyardListingLines));
        int currentPage = state.ShipCatalogue.Page ?? 1;
        int totalPages = state.ShipCatalogue.TotalPages ?? 1;
        int totalItems = state.ShipCatalogue.Total ?? state.ShipCatalogue.TotalItems ?? 0;
        int entriesOnPage = state.ShipCatalogue.NormalizedEntries.Length;
        string catalogEntries = GameState.StripMarkdown(
            GameState.FormatCatalogueEntries(state.ShipCatalogue.NormalizedEntries));

        return
$@"CONTEXT: SHIPYARD
STATION: {state.CurrentPOI.Id}
CREDITS: {state.Credits}
STATION CREDITS: {state.StorageCredits}
FUEL: {state.Fuel}/{state.MaxFuel}
CARGO: {state.CargoUsed}/{state.CargoCapacity}

SHOWROOM
{showroom}

PLAYER LISTINGS
{listings}

CARGO ITEMS
{cargo}{state.BuildNotificationsDisplaySection()}

CATALOG CACHE
PAGE: {currentPage}/{totalPages}
ENTRIES ON PAGE: {entriesOnPage}
TOTAL SHIPS: {totalItems}

SHIPS
{catalogEntries}";
    }

    private static string BuildCantinaUiState(GameState state)
    {
        string availableMissions = GameState.StripMarkdown(GameState.FormatMissions(state.AvailableMissions));
        if (string.IsNullOrWhiteSpace(availableMissions))
            availableMissions = "- _(none)_";

        return
$@"CONTEXT: CANTINA
STATION: {state.CurrentPOI.Id}
CREDITS: {state.Credits}

AVAILABLE MISSIONS
{availableMissions}{state.BuildNotificationsDisplaySection()}";
    }

    private static string FormatActiveMissionsForSpace(MissionInfo[] missions)
    {
        if (missions == null || missions.Length == 0)
            return "- (none)";

        return string.Join("\n", missions.Select(FormatActiveMissionForSpace));
    }

    private static string FormatActiveMissionForSpace(MissionInfo mission)
    {
        var name = FirstNonEmpty(
            mission.Title,
            mission.MissionId,
            mission.Id,
            "mission");
        var objectives = FirstNonEmpty(
            mission.ObjectivesSummary,
            mission.Description,
            "(none)");
        var progress = FirstNonEmpty(
            mission.ProgressText,
            mission.ProgressSummary,
            "(none)");

        return
$@"- {name}
  objectives: {objectives}
  progress: {progress}";
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private static string BuildCatalogUiState(GameState state)
    {
        var items = state.Galaxy.Catalog.ItemsById.Values
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToList();
        var ships = state.Galaxy.Catalog.ShipsById.Values
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToList();

        string RenderItem(CatalogueEntry e)
        {
            var price = e.Price.HasValue ? $" | Price: {e.Price.Value:0}" : "";
            var category = string.IsNullOrWhiteSpace(e.Category) ? "" : $" | Category: {e.Category}";
            var tier = e.Tier.HasValue ? $" | Tier: {e.Tier.Value}" : "";
            return $"- {e.Id} ({e.Name}){category}{tier}{price}";
        }

        string RenderShip(CatalogueEntry e)
        {
            var classId = string.IsNullOrWhiteSpace(e.ClassId) ? e.Class : e.ClassId;
            var classPart = string.IsNullOrWhiteSpace(classId) ? "" : $" | Class: {classId}";
            var hull = e.Hull ?? e.BaseHull;
            var shield = e.Shield ?? e.BaseShield;
            var cargo = e.Cargo ?? e.CargoCapacity;
            var speed = e.Speed ?? e.BaseSpeed;
            var stats = $" | Hull: {hull?.ToString() ?? "-"} | Shield: {shield?.ToString() ?? "-"} | Cargo: {cargo?.ToString() ?? "-"} | Speed: {speed?.ToString() ?? "-"}";
            var price = e.Price.HasValue ? $" | Price: {e.Price.Value:0}" : "";
            return $"- {e.Id} ({e.Name}){classPart}{stats}{price}";
        }

        var itemLines = items.Select(RenderItem).ToList();
        var shipLines = ships.Select(RenderShip).ToList();
        if (itemLines.Count == 0)
            itemLines.Add("- (no item catalog entries)");
        if (shipLines.Count == 0)
            shipLines.Add("- (no ship catalog entries)");

        return
$@"ITEMS
{string.Join("\n", itemLines)}

SHIPS
{string.Join("\n", shipLines)}";
    }
}
