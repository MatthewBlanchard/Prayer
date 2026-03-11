using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Prayer.Contracts;

public sealed class RuntimeGameStateDto
{
    public string System { get; set; } = "";
    public RuntimePoiInfoDto CurrentPOI { get; set; } = new();
    public RuntimePoiInfoDto[] POIs { get; set; } = Array.Empty<RuntimePoiInfoDto>();
    public string[] Systems { get; set; } = Array.Empty<string>();
    public RuntimeGalaxyStateDto Galaxy { get; set; } = new();
    public int StorageCredits { get; set; }
    public Dictionary<string, RuntimeItemStackDto> StorageItems { get; set; } = new(StringComparer.Ordinal);
    public RuntimeEconomyDealDto[] EconomyDeals { get; set; } = Array.Empty<RuntimeEconomyDealDto>();
    public RuntimeOpenOrderInfoDto[] OwnBuyOrders { get; set; } = Array.Empty<RuntimeOpenOrderInfoDto>();
    public RuntimeOpenOrderInfoDto[] OwnSellOrders { get; set; } = Array.Empty<RuntimeOpenOrderInfoDto>();
    public RuntimePlayerShipDto Ship { get; set; } = new();
    public int Credits { get; set; }
    public bool Docked { get; set; }
    public RuntimeShipyardShowroomEntryDto[] ShipyardShowroom { get; set; } = Array.Empty<RuntimeShipyardShowroomEntryDto>();
    public RuntimeShipyardListingEntryDto[] ShipyardListings { get; set; } = Array.Empty<RuntimeShipyardListingEntryDto>();
    public RuntimeCatalogueDto ShipCatalogue { get; set; } = new();
    public RuntimeOwnedShipInfoDto[] OwnedShips { get; set; } = Array.Empty<RuntimeOwnedShipInfoDto>();
    public RuntimeCatalogueEntryDto[] AvailableRecipes { get; set; } = Array.Empty<RuntimeCatalogueEntryDto>();
    public RuntimeMissionInfoDto[] ActiveMissions { get; set; } = Array.Empty<RuntimeMissionInfoDto>();
    public RuntimeMissionInfoDto[] AvailableMissions { get; set; } = Array.Empty<RuntimeMissionInfoDto>();
    public RuntimeGameNotificationDto[] Notifications { get; set; } = Array.Empty<RuntimeGameNotificationDto>();
    public RuntimeGameChatMessageDto[] ChatMessages { get; set; } = Array.Empty<RuntimeGameChatMessageDto>();
    public RuntimeMarketStateDto? CurrentMarket { get; set; }
}

public sealed class RuntimePlayerShipDto
{
    public string Name { get; set; } = "";
    public string ClassId { get; set; } = "";
    public int Armor { get; set; }
    public int Speed { get; set; }
    public int CpuUsed { get; set; }
    public int CpuCapacity { get; set; }
    public int PowerUsed { get; set; }
    public int PowerCapacity { get; set; }
    public int ModuleCount { get; set; }
    public int Fuel { get; set; }
    public int MaxFuel { get; set; }
    public int Hull { get; set; }
    public int MaxHull { get; set; }
    public int Shield { get; set; }
    public int MaxShield { get; set; }
    public int CargoUsed { get; set; }
    public int CargoCapacity { get; set; }
    public Dictionary<string, RuntimeItemStackDto> Cargo { get; set; } = new(StringComparer.Ordinal);
}

public sealed class RuntimeGalaxyStateDto
{
    public RuntimeGalaxyMapSnapshotDto Map { get; set; } = new();
    public RuntimeGalaxyMarketDto Market { get; set; } = new();
    public RuntimeGalaxyCatalogDto Catalog { get; set; } = new();
    public RuntimeGalaxyResourcesDto Resources { get; set; } = new();
    public RuntimeGalaxyExplorationDto Exploration { get; set; } = new();
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class RuntimeGalaxyResourcesDto
{
    public Dictionary<string, string[]> SystemsByResource { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string[]> PoisByResource { get; set; } = new(StringComparer.Ordinal);
}

public sealed class RuntimeGalaxyMarketDto
{
    public Dictionary<string, RuntimeMarketStateDto> MarketsByStation { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, decimal> GlobalMedianBuyPrices { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, decimal> GlobalMedianSellPrices { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, decimal> GlobalWeightedMidPrices { get; set; } = new(StringComparer.Ordinal);
}

public sealed class RuntimeGalaxyCatalogDto
{
    public Dictionary<string, RuntimeItemCatalogueEntryDto> ItemsById { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, RuntimeShipCatalogueEntryDto> ShipsById { get; set; } = new(StringComparer.Ordinal);
}

public sealed class RuntimeGalaxyExplorationDto
{
    public string[] ExploredSystems { get; set; } = Array.Empty<string>();
    public string[] VisitedPois { get; set; } = Array.Empty<string>();
    public string[] SurveyedSystems { get; set; } = Array.Empty<string>();
    public Dictionary<string, string[]> MiningCheckedPoisByResource { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string[]> MiningExploredSystemsByResource { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class RuntimeGalaxyMapSnapshotDto
{
    public List<RuntimeGalaxySystemInfoDto> Systems { get; set; } = new();
    public List<RuntimeGalaxyKnownPoiInfoDto> KnownPois { get; set; } = new();
}

public sealed class RuntimeGalaxySystemInfoDto
{
    public string Id { get; set; } = "";
    public string Empire { get; set; } = "";
    public double? X { get; set; }
    public double? Y { get; set; }
    public List<string> Connections { get; set; } = new();
    public List<RuntimeGalaxyPoiInfoDto> Pois { get; set; } = new();
}

public sealed class RuntimeGalaxyPoiInfoDto
{
    public string Id { get; set; } = "";
    public double? X { get; set; }
    public double? Y { get; set; }
}

public sealed class RuntimeGalaxyKnownPoiInfoDto
{
    public string Id { get; set; } = "";
    public string SystemId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public double? X { get; set; }
    public double? Y { get; set; }
    public bool HasBase { get; set; }
    public string? BaseId { get; set; }
    public string? BaseName { get; set; }
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

public sealed class RuntimeMarketStateDto
{
    public string StationId { get; set; } = "";
    public Dictionary<string, List<RuntimeMarketOrderDto>> SellOrders { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<RuntimeMarketOrderDto>> BuyOrders { get; set; } = new(StringComparer.Ordinal);
}

public sealed class RuntimeMarketOrderDto
{
    public string ItemId { get; set; } = "";
    public decimal PriceEach { get; set; }
    public int Quantity { get; set; }
}

public sealed class RuntimeEconomyDealDto
{
    public string ItemId { get; set; } = "";
    public string BuyStationId { get; set; } = "";
    public decimal BuyPrice { get; set; }
    public string SellStationId { get; set; } = "";
    public decimal SellPrice { get; set; }
    public decimal ProfitPerUnit { get; set; }
}

public sealed class RuntimeOpenOrderInfoDto
{
    public string OrderId { get; set; } = "";
    public string ItemId { get; set; } = "";
    public decimal PriceEach { get; set; }
    public int Quantity { get; set; }
}

public sealed class RuntimeOwnedShipInfoDto
{
    public string ShipId { get; set; } = "";
    public string ClassId { get; set; } = "";
    public string Location { get; set; } = "";
    public bool IsActive { get; set; }
}

public sealed class RuntimeShipyardShowroomEntryDto
{
    public string ShipClassId { get; set; } = "";
    public string? ShipId { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public int? Tier { get; set; }
    public int? Scale { get; set; }
    public int? Hull { get; set; }
    public int? Shield { get; set; }
    public int? Cargo { get; set; }
    public int? Speed { get; set; }
    public decimal? Price { get; set; }
}

public sealed class RuntimeShipyardListingEntryDto
{
    public string ListingId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ClassId { get; set; } = "";
    public decimal? Price { get; set; }
}

public sealed class RuntimeItemStackDto
{
    public string ItemId { get; set; } = "";
    public int Quantity { get; set; }
}

public sealed class RuntimePoiInfoDto
{
    public string Id { get; set; } = "";
    public string SystemId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Hidden { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public bool HasBase { get; set; }
    public string? BaseId { get; set; }
    public string? BaseName { get; set; }
    public int Online { get; set; }
    public RuntimePoiResourceInfoDto[] Resources { get; set; } = Array.Empty<RuntimePoiResourceInfoDto>();
}

public sealed class RuntimePoiResourceInfoDto
{
    public string ResourceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string RichnessText { get; set; } = "";
    public int? Richness { get; set; }
    public int? Remaining { get; set; }
    public string RemainingDisplay { get; set; } = "";
}

public sealed class RuntimeMissionInfoDto
{
    public string Id { get; set; } = "";
    public string MissionId { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
    public string ProgressText { get; set; } = "";
    public bool Completed { get; set; }
    public int? Difficulty { get; set; }
    public int? ExpiresInTicks { get; set; }
    public string AcceptedAt { get; set; } = "";
    public string IssuingBase { get; set; } = "";
    public string IssuingBaseId { get; set; } = "";
    public string GiverName { get; set; } = "";
    public string GiverTitle { get; set; } = "";
    public bool? Repeatable { get; set; }
    public string FactionId { get; set; } = "";
    public string FactionName { get; set; } = "";
    public string ChainNext { get; set; } = "";
    public string ObjectivesSummary { get; set; } = "";
    public string ProgressSummary { get; set; } = "";
    public string RequirementsSummary { get; set; } = "";
    public string RewardsSummary { get; set; } = "";
}

public sealed class RuntimeGameNotificationDto
{
    public string Type { get; set; } = "";
    public string Summary { get; set; } = "";
    public string PayloadJson { get; set; } = "";
}

public sealed class RuntimeGameChatMessageDto
{
    public string MessageId { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Sender { get; set; } = "";
    public string Content { get; set; } = "";
    public int SeenTick { get; set; }
}

public sealed class RuntimeCatalogueDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("page")]
    public int? Page { get; set; }

    [JsonPropertyName("page_size")]
    public int? PageSize { get; set; }

    [JsonPropertyName("total_pages")]
    public int? TotalPages { get; set; }

    [JsonPropertyName("total_items")]
    public int? TotalItems { get; set; }

    [JsonPropertyName("total")]
    public int? Total { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("items")]
    public RuntimeCatalogueEntryDto[] Items { get; set; } = Array.Empty<RuntimeCatalogueEntryDto>();

    [JsonPropertyName("entries")]
    public RuntimeCatalogueEntryDto[] Entries { get; set; } = Array.Empty<RuntimeCatalogueEntryDto>();

    [JsonPropertyName("ships")]
    public RuntimeCatalogueEntryDto[] Ships { get; set; } = Array.Empty<RuntimeCatalogueEntryDto>();
}

public class RuntimeCatalogueEntryDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("class_id")]
    public string ClassId { get; set; } = "";

    [JsonPropertyName("class")]
    public string Class { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("tier")]
    public int? Tier { get; set; }

    [JsonPropertyName("scale")]
    public int? Scale { get; set; }

    [JsonPropertyName("hull")]
    public int? Hull { get; set; }

    [JsonPropertyName("base_hull")]
    public int? BaseHull { get; set; }

    [JsonPropertyName("shield")]
    public int? Shield { get; set; }

    [JsonPropertyName("base_shield")]
    public int? BaseShield { get; set; }

    [JsonPropertyName("cargo")]
    public int? Cargo { get; set; }

    [JsonPropertyName("cargo_capacity")]
    public int? CargoCapacity { get; set; }

    [JsonPropertyName("speed")]
    public int? Speed { get; set; }

    [JsonPropertyName("base_speed")]
    public int? BaseSpeed { get; set; }

    [JsonPropertyName("price")]
    public decimal? Price { get; set; }

    [JsonPropertyName("materials")]
    public Dictionary<string, int>? MaterialsById { get; set; }

    [JsonPropertyName("ingredients")]
    public RuntimeRecipeIngredientEntryDto[] Ingredients { get; set; } = Array.Empty<RuntimeRecipeIngredientEntryDto>();

    [JsonPropertyName("inputs")]
    public RuntimeRecipeIngredientEntryDto[] Inputs { get; set; } = Array.Empty<RuntimeRecipeIngredientEntryDto>();
}

public sealed class RuntimeItemCatalogueEntryDto : RuntimeCatalogueEntryDto
{
}

public sealed class RuntimeShipCatalogueEntryDto : RuntimeCatalogueEntryDto
{
}

public sealed class RuntimeRecipeIngredientEntryDto
{
    [JsonPropertyName("item_id")]
    public string ItemId { get; set; } = "";

    [JsonPropertyName("item")]
    public string Item { get; set; } = "";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("quantity")]
    public int? Quantity { get; set; }

    [JsonPropertyName("amount")]
    public int? Amount { get; set; }

    [JsonPropertyName("count")]
    public int? Count { get; set; }
}
