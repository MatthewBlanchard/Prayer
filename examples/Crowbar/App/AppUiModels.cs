using System.Collections.Generic;

public sealed record SpaceUiPoi(
    string Target,
    string Label,
    string Type,
    double? X,
    double? Y,
    bool HasBase = false,
    string BaseName = "",
    int? Online = null,
    IReadOnlyList<string>? Resources = null);

public sealed record SpaceUiSystemNode(
    string Id,
    double? X,
    double? Y,
    string Empire,
    bool IsStronghold,
    bool HasStation,
    bool IsCurrent,
    IReadOnlyList<string> Connections);

public sealed record SpaceUiCargoItem(
    string ItemId,
    int Quantity,
    decimal? MedianPrice);

public sealed record SpaceUiModel(
    string System,
    string Poi,
    string Docked,
    int Credits,
    string Fuel,
    string Hull,
    string Shield,
    string Cargo,
    IReadOnlyList<SpaceUiPoi> Pois,
    IReadOnlyList<SpaceUiCargoItem> CargoItems,
    IReadOnlyList<SpaceUiSystemNode> LocalSystems);

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
    bool HasMarket,
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

public sealed record CraftingUiEntry(
    string RecipeId,
    string Name,
    string Category,
    int? Tier,
    string IngredientsSummary,
    string DisplayText);

public sealed record CraftingUiModel(
    bool HasCrafting,
    string StationId,
    IReadOnlyList<CraftingUiEntry> Recipes);

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
    string ShipName,
    string ShipClassId,
    string Fuel,
    string Hull,
    string Shield,
    string Cargo,
    IReadOnlyList<string> InstalledModules,
    string CatalogPage,
    int? TotalShips,
    IReadOnlyList<ShipyardUiEntry> Showroom,
    IReadOnlyList<ShipyardUiEntry> PlayerListings,
    IReadOnlyList<ShipyardUiEntry> CatalogShips);

public sealed record BotStateEntry(
    string BotId,
    SpaceUiModel? SpaceModel,
    IReadOnlyList<string> SpaceConnectedSystems,
    TradeUiModel? TradeModel,
    ShipyardUiModel? ShipyardModel,
    CatalogUiModel? CatalogModel,
    IReadOnlyList<MissionPromptOption> ActiveMissionPrompts,
    IReadOnlyList<MissionPromptOption> AvailableMissionPrompts,
    IReadOnlyList<string> Memory,
    IReadOnlyList<string> ExecutionStatusLines,
    string? ControlInput,
    int? CurrentScriptLine,
    string? LastGenerationPrompt,
    int? CurrentTick,
    DateTime? LastSpaceMoltPostUtc,
    Prayer.Contracts.ActiveGoRouteDto? ActiveRoute,
    CraftingUiModel? CraftingModel);

public sealed record BotRouteOverlay(
    string BotId,
    string Label,
    string ColorHex,
    string CurrentSystemId,
    string? TargetSystemId,
    IReadOnlyList<string> Hops,
    int? TotalJumps,
    int? EstimatedFuel,
    int? FuelAvailable,
    int? ShipSpeed = null);
