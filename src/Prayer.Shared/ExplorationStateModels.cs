using System;
using System.Collections.Generic;

public sealed class ExplorationStateSnapshot
{
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    // Galaxy-level exploration memory. This is intentionally shared and not keyed by player.
    public HashSet<string> ExploredSystems { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> ExploredPois { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> SurveyedSystems { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<string>> MiningCheckedPoisByResource { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, HashSet<string>> MiningExploredSystemsByResource { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    // Rich POI knowledge cache, including discovered resources, shared across bots/processes.
    public Dictionary<string, GalaxyPoiKnowledge> PoisById { get; set; } = new(StringComparer.Ordinal);
}
