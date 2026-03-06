using System.Collections.Generic;

public sealed record TradeUiItem(
    string ItemId,
    int Quantity,
    string DisplayText);

public sealed record TradeUiOrder(
    string Side,
    string ItemId,
    int Quantity,
    decimal PriceEach,
    string DisplayText);

public sealed record TradeUiModel(
    string StationId,
    int Credits,
    int StationCredits,
    string Fuel,
    string Cargo,
    IReadOnlyList<TradeUiItem> CargoItems,
    IReadOnlyList<TradeUiItem> StorageItems,
    IReadOnlyList<TradeUiOrder> OpenOrders);

public sealed record ShipyardUiEntry(
    string Id,
    string DisplayText);

public sealed record ShipyardUiModel(
    string StationId,
    int Credits,
    int StationCredits,
    string Fuel,
    string Cargo,
    string CatalogPage,
    int? TotalShips,
    IReadOnlyList<ShipyardUiEntry> Showroom,
    IReadOnlyList<ShipyardUiEntry> PlayerListings,
    IReadOnlyList<ShipyardUiEntry> CatalogShips);
