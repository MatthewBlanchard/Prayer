using System.Collections.Generic;

public sealed record TradeUiItem(
    string ItemId,
    int Quantity,
    decimal? MedianBuyPrice,
    decimal? MedianSellPrice,
    string DisplayText);

public sealed record TradeUiOrder(
    string OrderId,
    string ItemId,
    int Quantity,
    decimal PriceEach,
    string DisplayText);

public sealed record TradeCatalogItem(
    string ItemId,
    string Name,
    string Category,
    int? Tier,
    bool HasLocalBuyOrders,
    bool HasLocalSellOrders,
    decimal? MedianBuyPrice,
    decimal? MedianSellPrice,
    decimal? GlobalMedianBuyPrice,
    decimal? GlobalMedianSellPrice);

public sealed record TradeUiModel(
    string StationId,
    int Credits,
    int StationCredits,
    string Fuel,
    string Cargo,
    IReadOnlyList<TradeUiItem> CargoItems,
    IReadOnlyList<TradeUiItem> StorageItems,
    IReadOnlyList<TradeCatalogItem> AllItems,
    IReadOnlyList<TradeUiOrder> BuyOrders,
    IReadOnlyList<TradeUiOrder> SellOrders);

public sealed record CatalogUiEntry(
    string Id,
    string Name,
    string Category,
    int? Tier,
    decimal? Price,
    string DisplayText);

public sealed record CatalogUiModel(
    IReadOnlyList<CatalogUiEntry> Items,
    IReadOnlyList<CatalogUiEntry> Ships);

public sealed record ShipyardUiEntry(
    string Id,
    string DisplayText,
    string Faction,
    string? Name = null,
    string? ClassId = null,
    string? Category = null,
    int? Tier = null,
    int? Scale = null,
    int? Hull = null,
    int? Shield = null,
    int? Cargo = null,
    int? Speed = null,
    decimal? Price = null);

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
