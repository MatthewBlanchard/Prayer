using System;
using System.Collections.Generic;
using System.Linq;

// =====================================================
// GAME STATE
// =====================================================

public partial class GameState
{
    public string System { get; set; } = "";
    public POIInfo CurrentPOI { get; set; } = null!;

    public POIInfo[] POIs { get; set; } = Array.Empty<POIInfo>();
    public string[] Systems { get; set; } = Array.Empty<string>();
    public GalaxyState Galaxy { get; set; } = new();
    public int StorageCredits { get; set; }
    public Dictionary<string, ItemStack> StorageItems { get; set; } = new();
    public Dictionary<string, Dictionary<string, ItemStack>> StorageCacheByPoi { get; set; } = new();
    public EconomyDeal[] EconomyDeals { get; set; } = Array.Empty<EconomyDeal>();
    public OpenOrderInfo[] OwnBuyOrders { get; set; } = Array.Empty<OpenOrderInfo>();
    public OpenOrderInfo[] OwnSellOrders { get; set; } = Array.Empty<OpenOrderInfo>();
    public PlayerShip Ship { get; set; } = new();
    public int Credits { get; set; }
    public bool Docked { get; set; }
    public string HomeBase { get; set; } = "";
    public ShipyardShowroomEntry[] ShipyardShowroom { get; set; } = Array.Empty<ShipyardShowroomEntry>();
    public ShipyardListingEntry[] ShipyardListings { get; set; } = Array.Empty<ShipyardListingEntry>();
    public Catalogue ShipCatalogue { get; set; } = new();
    public OwnedShipInfo[] OwnedShips { get; set; } = Array.Empty<OwnedShipInfo>();
    public CatalogueEntry[] AvailableRecipes { get; set; } = [];
    public Dictionary<string, int> Skills { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public MissionInfo[] ActiveMissions { get; set; } = Array.Empty<MissionInfo>();
    public MissionInfo[] AvailableMissions { get; set; } = Array.Empty<MissionInfo>();
    public GameNotification[] Notifications { get; set; } = Array.Empty<GameNotification>();
    public GameChatMessage[] ChatMessages { get; set; } = Array.Empty<GameChatMessage>();
    public MarketState? CurrentMarket
    {
        get
        {
            if (!Docked || !CurrentPOI.IsStation)
                return null;

            if (Galaxy?.Market?.MarketsByStation == null)
                return null;

            return Galaxy.Market.MarketsByStation.TryGetValue(CurrentPOI.Id, out var market)
                ? market
                : null;
        }
    }

    public int GetQuantity(string itemId)
    {
        return Ship.Cargo.TryGetValue(itemId, out var stack)
            ? stack.Quantity
            : 0;
    }

    public bool HasItem(string itemId, int minQuantity = 1)
    {
        return GetQuantity(itemId) >= minQuantity;
    }
}

public sealed class PlayerShip
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
    public string[] InstalledModules { get; set; } = Array.Empty<string>();
    public int Fuel { get; set; }
    public int MaxFuel { get; set; }
    public int Hull { get; set; }
    public int MaxHull { get; set; }
    public int Shield { get; set; }
    public int MaxShield { get; set; }
    public int CargoUsed { get; set; }
    public int CargoCapacity { get; set; }
    public Dictionary<string, ItemStack> Cargo { get; set; } = new();
}

public class GalaxyMapSnapshot
{
    public List<GalaxySystemInfo> Systems { get; set; } = new();
    public List<GalaxyKnownPoiInfo> KnownPois { get; set; } = new();
}

public class GalaxySystemInfo
{
    public string Id { get; set; } = "";
    public string Empire { get; set; } = "";
    public bool IsStronghold { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public List<string> Connections { get; set; } = new();
    public List<GalaxyPoiInfo> Pois { get; set; } = new();
}

public class GalaxyPoiInfo
{
    public string Id { get; set; } = "";
    public double? X { get; set; }
    public double? Y { get; set; }
}

public class GalaxyKnownPoiInfo
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

public class MarketState
{
    public string StationId { get; set; } = "";

    public Dictionary<string, List<MarketOrder>> SellOrders { get; set; }
        = new();

    public Dictionary<string, List<MarketOrder>> BuyOrders { get; set; }
        = new();
}

public class MarketOrder
{
    public string ItemId { get; set; } = "";
    public decimal PriceEach { get; set; }
    public int Quantity { get; set; }
}

public class StationInfo
{
    public string StationId { get; set; } = "";
    public int StorageCredits { get; set; }
    public Dictionary<string, ItemStack> StorageItems { get; set; } = new();
    public MarketState? Market { get; set; }
    public List<OpenOrderInfo> BuyOrders { get; set; } = new();
    public List<OpenOrderInfo> SellOrders { get; set; } = new();
    public ShipyardShowroomEntry[] ShipyardShowroom { get; set; } = Array.Empty<ShipyardShowroomEntry>();
    public ShipyardListingEntry[] ShipyardListings { get; set; } = Array.Empty<ShipyardListingEntry>();
}

public class MarketCacheSnapshot
{
    public string StationId { get; set; } = "";
    public DateTime CapturedAtUtc { get; set; }
    public MarketState? Market { get; set; }
}

public class ShipyardCacheSnapshot
{
    public string StationId { get; set; } = "";
    public DateTime CapturedAtUtc { get; set; }
    public ShipyardShowroomEntry[] Showroom { get; set; } = Array.Empty<ShipyardShowroomEntry>();
    public ShipyardListingEntry[] Listings { get; set; } = Array.Empty<ShipyardListingEntry>();
}

public sealed class ShipyardShowroomEntry
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

public sealed class ShipyardListingEntry
{
    public string ListingId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ClassId { get; set; } = "";
    public decimal? Price { get; set; }
}

public class EconomyDeal
{
    public string ItemId { get; set; } = "";
    public string BuyStationId { get; set; } = "";
    public decimal BuyPrice { get; set; }
    public string SellStationId { get; set; } = "";
    public decimal SellPrice { get; set; }
    public decimal ProfitPerUnit { get; set; }
}

public class OpenOrderInfo
{
    public string OrderId { get; set; } = "";
    public string ItemId { get; set; } = "";
    public decimal PriceEach { get; set; }
    public int Quantity { get; set; }
}

public class OwnedShipInfo
{
    public string ShipId { get; set; } = "";
    public string ClassId { get; set; } = "";
    public string Location { get; set; } = "";
    public bool IsActive { get; set; }
}

public class ItemStack
{
    public string ItemId { get; }
    public int Quantity { get; private set; }

    public ItemStack(string itemId, int quantity)
    {
        ItemId = itemId;
        Quantity = quantity;
    }

    public void Add(int amount)
    {
        if (amount > 0)
            Quantity += amount;
    }

    public void Remove(int amount)
    {
        if (amount <= 0) return;

        Quantity -= amount;
        if (Quantity < 0)
            Quantity = 0;
    }

    public override string ToString()
    {
        return $"{ItemId} x{Quantity}";
    }
}

public class POIInfo
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
    public PoiResourceInfo[] Resources { get; set; } = Array.Empty<PoiResourceInfo>();

    public bool IsMiningTarget =>
        Type == "asteroid_belt" ||
        Type == "asteroid" ||
        Type == "gas_cloud" ||
        Type == "ice_field";

    public bool IsStation => Type == "station";
}

public class PoiResourceInfo
{
    public string ResourceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string RichnessText { get; set; } = "";
    public int? Richness { get; set; }
    public int? Remaining { get; set; }
    public string RemainingDisplay { get; set; } = "";
}

public class MissionInfo
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

public class GameNotification
{
    public string Type { get; set; } = "";
    public string Summary { get; set; } = "";
    public string PayloadJson { get; set; } = "";
}

public class GameChatMessage
{
    public string MessageId { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Sender { get; set; } = "";
    public string Content { get; set; } = "";
    public int SeenTick { get; set; }
}
