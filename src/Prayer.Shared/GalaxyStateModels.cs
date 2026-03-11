using System;
using System.Collections.Generic;

public class GalaxyState
{
    public GalaxyMapSnapshot Map { get; set; } = new();
    public GalaxyMarket Market { get; set; } = new();
    public GalaxyCatalog Catalog { get; set; } = new();
    public GalaxyResources Resources { get; set; } = new();
    public GalaxyExplorationState Exploration { get; set; } = new();
    public GalaxyKnowledgeState Knowledge { get; set; } = new();
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

public class GalaxyExplorationState
{
    public HashSet<string> ExploredSystems { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> VisitedPois { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> SurveyedSystems { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string[]> MiningCheckedPoisByResource { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string[]> MiningExploredSystemsByResource { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class GalaxyKnowledgeState
{
    public Dictionary<string, GalaxyPoiKnowledge> PoisById { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, GalaxySystemKnowledge> SystemsById { get; set; } = new(StringComparer.Ordinal);
}

public class GalaxyPoiKnowledge
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
    public bool Visited { get; set; }
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public PoiResourceInfo[] Resources { get; set; } = Array.Empty<PoiResourceInfo>();
}

public class GalaxySystemKnowledge
{
    public string Id { get; set; } = "";
    public bool Surveyed { get; set; }
}
