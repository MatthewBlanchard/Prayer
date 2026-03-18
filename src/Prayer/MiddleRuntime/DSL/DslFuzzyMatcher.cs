using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

internal static class DslFuzzyMatcher
{
    private const double MinDidYouMeanScore = 0.62d;

    private sealed record Candidate(string Canonical, IReadOnlyList<string> Aliases);

    public static IReadOnlyList<string> CastArguments(
        string action,
        IReadOnlyList<string> args,
        IReadOnlyList<DslArgumentSpec> argSpecs,
        GameState state,
        IReadOnlyList<string>? rawArgs = null)
    {
        var casted = new List<string>(args.Count);

        for (int i = 0; i < args.Count; i++)
        {
            var spec = i < argSpecs.Count
                ? argSpecs[i]
                : new DslArgumentSpec(DslArgType.Any, Required: false);

            string rawSourceArg = rawArgs != null && i < rawArgs.Count
                ? rawArgs[i]
                : args[i];
            casted.Add(CastTypedArg(action, i + 1, args[i], rawSourceArg, spec, state));
        }

        return casted;
    }

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var sb = new StringBuilder(value.Length);
        bool prevUnderscore = false;

        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                prevUnderscore = false;
                continue;
            }

            if (ch == '_' || ch == '-' || char.IsWhiteSpace(ch))
            {
                if (!prevUnderscore)
                {
                    sb.Append('_');
                    prevUnderscore = true;
                }
            }
        }

        return sb.ToString().Trim('_');
    }

    private static string CastTypedArg(
        string action,
        int argIndex,
        string rawArg,
        string rawSourceArg,
        DslArgumentSpec spec,
        GameState state)
    {
        if (string.IsNullOrWhiteSpace(rawArg))
            return rawArg ?? string.Empty;

        string trimmed = rawArg.Trim();
        string normalized = Normalize(trimmed);

        if (spec.Type == DslArgType.Any || spec.Type == DslArgType.None)
            return trimmed;

        if (spec.Type == DslArgType.Integer)
        {
            if (!int.TryParse(trimmed, out var n))
                throw new FormatException($"Command '{action}' argument {argIndex} value '{trimmed}' is not a valid integer.");
            return n.ToString();
        }

        var candidates = BuildCandidates(spec, state);
        if (candidates.Count == 0)
            return trimmed;

        var exact = candidates.FirstOrDefault(c => c.Aliases.Any(a => string.Equals(a, normalized, StringComparison.Ordinal)));
        if (exact != null)
            return exact.Canonical;

        bool cameFromMacro = !string.IsNullOrWhiteSpace(rawSourceArg) &&
                             rawSourceArg.TrimStart().StartsWith("$", StringComparison.Ordinal);
        if (spec.Type == DslArgType.GoTarget && cameFromMacro)
            return trimmed;

        if (TryFindBestMatch(normalized, candidates, out var bestCanonical, out var bestScore) &&
            bestScore >= MinDidYouMeanScore)
        {
            throw new FormatException(
                $"Command '{action}' argument {argIndex} value '{trimmed}' is not recognized as {DescribeType(spec.Type)}. Did you mean '{bestCanonical}'?");
        }

        throw new FormatException(
            $"Command '{action}' argument {argIndex} value '{trimmed}' is not recognized as {DescribeType(spec.Type)}.");
    }

    private static string DescribeType(DslArgType type) => type switch
    {
        DslArgType.ItemId => "item id",
        DslArgType.SystemId => "system id",
        DslArgType.PoiId => "POI id",
        DslArgType.GoTarget => "go target",
        DslArgType.ShipId => "ship id",
        DslArgType.ListingId => "listing id",
        DslArgType.MissionId => "mission id",
        DslArgType.ModuleId => "module id",
        DslArgType.RecipeId => "recipe id",
        DslArgType.Enum => "enum value",
        _ => "value"
    };

    private static IReadOnlyList<Candidate> BuildCandidates(
        DslArgumentSpec spec,
        GameState state)
    {
        return spec.Type switch
        {
            DslArgType.ItemId => BuildItemCandidates(state),
            DslArgType.SystemId => BuildSystemCandidates(state),
            DslArgType.PoiId => BuildPoiCandidates(state),
            DslArgType.GoTarget => BuildGoTargetCandidates(state),
            DslArgType.ShipId => BuildShipCandidates(state),
            DslArgType.ListingId => BuildListingCandidates(state),
            DslArgType.MissionId => BuildMissionCandidates(state),
            DslArgType.ModuleId => BuildModuleCandidates(state),
            DslArgType.RecipeId => BuildRecipeCandidates(state),
            DslArgType.Enum => BuildEnumCandidates(spec, state),
            _ => Array.Empty<Candidate>()
        };
    }

    private static IReadOnlyList<Candidate> BuildItemCandidates(GameState state)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        if (state.Galaxy?.Catalog?.ItemsById != null)
        {
            foreach (var (itemId, entry) in state.Galaxy.Catalog.ItemsById)
            {
                AddAlias(map, itemId, itemId);
                AddAlias(map, itemId, entry?.Name);
            }
        }

        foreach (var itemId in state.Ship.Cargo.Keys)
            AddAlias(map, itemId, itemId);

        foreach (var itemId in state.StorageItems.Keys)
            AddAlias(map, itemId, itemId);

        return ToCandidates(map);
    }

    private static IReadOnlyList<Candidate> BuildSystemCandidates(GameState state)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        AddAlias(map, state.System, state.System);

        foreach (var system in state.Systems)
            AddAlias(map, system, system);

        AddMapCandidates(LoadMapCache(), map, poiMap: null);
        AddMapCandidates(state.Galaxy?.Map, map, poiMap: null);

        return ToCandidates(map);
    }

    private static IReadOnlyList<Candidate> BuildPoiCandidates(GameState state)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        AddAlias(map, state.CurrentPOI?.Id, state.CurrentPOI?.Id);
        AddAlias(map, state.CurrentPOI?.Id, state.CurrentPOI?.Name);

        foreach (var poi in state.POIs)
        {
            AddAlias(map, poi.Id, poi.Id);
            AddAlias(map, poi.Id, poi.Name);
        }

        AddMapCandidates(LoadMapCache(), systemMap: null, map);
        AddMapCandidates(state.Galaxy?.Map, systemMap: null, map);

        return ToCandidates(map);
    }

    private static IReadOnlyList<Candidate> BuildGoTargetCandidates(GameState state)
    {
        var systems = BuildSystemCandidates(state).ToDictionary(c => c.Canonical, c => c.Aliases.ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var pois = BuildPoiCandidates(state);

        foreach (var poi in pois)
        {
            if (!systems.TryGetValue(poi.Canonical, out var aliases))
            {
                systems[poi.Canonical] = poi.Aliases.ToHashSet(StringComparer.Ordinal);
                continue;
            }

            foreach (var alias in poi.Aliases)
                aliases.Add(alias);
        }

        return systems
            .Select(kvp => new Candidate(kvp.Key, kvp.Value.ToList()))
            .ToList();
    }

    private static IReadOnlyList<Candidate> BuildShipCandidates(GameState state)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var ship in state.OwnedShips ?? Array.Empty<OwnedShipInfo>())
            AddAlias(map, ship?.ShipId, ship?.ShipId);

        return ToCandidates(map);
    }

    private static IReadOnlyList<Candidate> BuildListingCandidates(GameState state)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var listing in state.ShipyardListings ?? Array.Empty<ShipyardListingEntry>())
            AddAlias(map, listing?.ListingId, listing?.ListingId);
        return ToCandidates(map);
    }

    private static IReadOnlyList<Candidate> BuildMissionCandidates(GameState state)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var mission in state.ActiveMissions ?? Array.Empty<MissionInfo>())
        {
            AddAlias(map, mission?.Id, mission?.Id);
            AddAlias(map, mission?.MissionId, mission?.MissionId);
            AddAlias(map, mission?.TemplateId, mission?.TemplateId);
        }

        foreach (var mission in state.AvailableMissions ?? Array.Empty<MissionInfo>())
        {
            AddAlias(map, mission?.Id, mission?.Id);
            AddAlias(map, mission?.MissionId, mission?.MissionId);
            AddAlias(map, mission?.TemplateId, mission?.TemplateId);
        }

        return ToCandidates(map);
    }

    private static IReadOnlyList<Candidate> BuildModuleCandidates(GameState state)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var moduleId in state.Ship.InstalledModules ?? Array.Empty<string>())
            AddAlias(map, moduleId, moduleId);
        return ToCandidates(map);
    }

    private static IReadOnlyList<Candidate> BuildRecipeCandidates(GameState state)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var recipe in state.AvailableRecipes ?? Array.Empty<CatalogueEntry>())
        {
            AddAlias(map, recipe?.Id, recipe?.Id);
            AddAlias(map, recipe?.Id, recipe?.Name);
        }

        return ToCandidates(map);
    }

    private static IReadOnlyList<Candidate> BuildEnumCandidates(DslArgumentSpec spec, GameState state)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var value in spec.EnumValues ?? Array.Empty<string>())
            AddAlias(map, value, value);

        if (string.Equals(spec.EnumType, "ship_class", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var ship in state.ShipCatalogue?.Ships ?? Array.Empty<CatalogueEntry>())
            {
                AddAlias(map, ship?.ClassId, ship?.ClassId);
                AddAlias(map, ship?.ClassId, ship?.Id);
                AddAlias(map, ship?.ClassId, ship?.Name);
            }

            foreach (var ship in state.Galaxy?.Catalog?.ShipsById?.Values ?? Enumerable.Empty<ShipCatalogueEntry>())
            {
                AddAlias(map, ship?.ClassId, ship?.ClassId);
                AddAlias(map, ship?.ClassId, ship?.Id);
                AddAlias(map, ship?.ClassId, ship?.Name);
            }
        }

        return ToCandidates(map);
    }

    private static void AddMapCandidates(
        GalaxyMapSnapshot? map,
        Dictionary<string, HashSet<string>>? systemMap,
        Dictionary<string, HashSet<string>>? poiMap)
    {
        if (map == null)
            return;

        foreach (var system in map.Systems)
        {
            AddAlias(systemMap, system.Id, system.Id);
            foreach (var poi in system.Pois)
                AddAlias(poiMap, poi.Id, poi.Id);
        }

        foreach (var poi in map.KnownPois)
        {
            AddAlias(poiMap, poi.Id, poi.Id);
            AddAlias(poiMap, poi.Id, poi.Name);
        }
    }

    private static IReadOnlyList<Candidate> ToCandidates(Dictionary<string, HashSet<string>> aliasesByCanonical)
    {
        return aliasesByCanonical
            .Select(kvp => new Candidate(kvp.Key, kvp.Value.ToList()))
            .ToList();
    }

    private static void AddAlias(
        Dictionary<string, HashSet<string>>? map,
        string? canonicalRaw,
        string? aliasRaw)
    {
        if (map == null)
            return;

        var canonical = (canonicalRaw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(canonical))
            return;

        var canonicalNormalized = Normalize(canonical);
        if (string.IsNullOrWhiteSpace(canonicalNormalized))
            return;

        var aliasNormalized = Normalize(aliasRaw ?? string.Empty);
        if (string.IsNullOrWhiteSpace(aliasNormalized))
            aliasNormalized = canonicalNormalized;

        if (!map.TryGetValue(canonical, out var aliases))
        {
            aliases = new HashSet<string>(StringComparer.Ordinal);
            map[canonical] = aliases;
        }

        aliases.Add(canonicalNormalized);
        aliases.Add(aliasNormalized);
    }

    private static bool TryFindBestMatch(
        string query,
        IReadOnlyList<Candidate> candidates,
        out string bestCanonical,
        out double bestScore)
    {
        bestCanonical = string.Empty;
        bestScore = -1d;
        int bestDistance = int.MaxValue;

        foreach (var candidate in candidates)
        {
            double candidateScore = -1d;
            int candidateDistance = int.MaxValue;

            foreach (var alias in candidate.Aliases)
            {
                var aliasScore = ComputeScore(query, alias);
                var aliasDistance = FuzzyMatchScoring.LevenshteinDistance(query, alias);

                if (aliasScore > candidateScore ||
                    (Math.Abs(aliasScore - candidateScore) < 0.0001d && aliasDistance < candidateDistance))
                {
                    candidateScore = aliasScore;
                    candidateDistance = aliasDistance;
                }
            }

            if (candidateScore > bestScore ||
                (Math.Abs(candidateScore - bestScore) < 0.0001d && candidateDistance < bestDistance))
            {
                bestScore = candidateScore;
                bestCanonical = candidate.Canonical;
                bestDistance = candidateDistance;
            }
        }

        return !string.IsNullOrWhiteSpace(bestCanonical);
    }

    private static double ComputeScore(string query, string candidateAlias)
    {
        return FuzzyMatchScoring.ComputeScore(query, candidateAlias);
    }

    private static GalaxyMapSnapshot LoadMapCache()
    {
        return GalaxyMapSnapshotFile.LoadWithKnownPois(
            AppPaths.GalaxyMapFile,
            AppPaths.GalaxyKnownPoisFile);
    }
}
