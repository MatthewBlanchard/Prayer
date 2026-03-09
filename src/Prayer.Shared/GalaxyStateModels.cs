using System;
using System.Collections.Generic;

public class GalaxyState
{
    public GalaxyMapSnapshot Map { get; set; } = new();
    public GalaxyMarket Market { get; set; } = new();
    public GalaxyCatalog Catalog { get; set; } = new();
    public GalaxyResources Resources { get; set; } = new();
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class GalaxyResources
{
    public Dictionary<string, string[]> SystemsByResource { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string[]> PoisByResource { get; set; } = new(StringComparer.Ordinal);
}

public class GalaxyMarket
{
    public Dictionary<string, MarketState> MarketsByStation { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, decimal> GlobalMedianBuyPrices { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, decimal> GlobalMedianSellPrices { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, decimal> GlobalWeightedMidPrices { get; set; } = new(StringComparer.Ordinal);
}

public class GalaxyCatalog
{
    public Dictionary<string, ItemCatalogueEntry> ItemsById { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ShipCatalogueEntry> ShipsById { get; set; } = new(StringComparer.Ordinal);
}
