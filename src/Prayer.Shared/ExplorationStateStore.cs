using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

public static class ExplorationStateStore
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static ExplorationStateSnapshot Load()
    {
        lock (Sync)
        {
            try
            {
                if (!File.Exists(AppPaths.ExplorationGalaxyStateFile))
                    return CreateDefault();

                string raw = File.ReadAllText(AppPaths.ExplorationGalaxyStateFile);
                if (string.IsNullOrWhiteSpace(raw))
                    return CreateDefault();

                var snapshot = JsonSerializer.Deserialize<ExplorationStateSnapshot>(raw);
                return Normalize(snapshot);
            }
            catch
            {
                return CreateDefault();
            }
        }
    }

    public static void Save(ExplorationStateSnapshot snapshot)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.CacheDir);
                snapshot = Normalize(snapshot);
                snapshot.UpdatedAtUtc = DateTime.UtcNow;
                string json = JsonSerializer.Serialize(snapshot, JsonOptions);
                File.WriteAllText(AppPaths.ExplorationGalaxyStateFile, json);
            }
            catch
            {
                // Best-effort persistence.
            }
        }
    }

    private static ExplorationStateSnapshot CreateDefault()
        => Normalize(new ExplorationStateSnapshot());

    private static ExplorationStateSnapshot Normalize(ExplorationStateSnapshot? snapshot)
    {
        snapshot ??= new ExplorationStateSnapshot();
        snapshot.ExploredSystems ??= new HashSet<string>(StringComparer.Ordinal);
        snapshot.ExploredPois ??= new HashSet<string>(StringComparer.Ordinal);
        snapshot.SurveyedSystems ??= new HashSet<string>(StringComparer.Ordinal);
        snapshot.MiningCheckedPoisByResource ??= new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        snapshot.MiningExploredSystemsByResource ??= new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        snapshot.PoisById ??= new Dictionary<string, GalaxyPoiKnowledge>(StringComparer.Ordinal);
        snapshot.WormholeLinksById ??= new Dictionary<string, WormholeLink>(StringComparer.Ordinal);
        return snapshot;
    }
}
