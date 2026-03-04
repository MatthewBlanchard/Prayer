using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

internal static class DslFuzzyMatcher
{
    private const double MinDidYouMeanScore = 0.62d;

    private sealed record Candidate(
        string Canonical,
        IReadOnlyList<string> Aliases,
        string ArgType);

    private sealed record MatchResult(
        string Canonical,
        double Score,
        string ArgType);

    public static void ValidateArguments(
        string action,
        IReadOnlyList<string> args,
        IReadOnlyList<DslArgumentSpec> argSpecs,
        GameState state)
    {
        if (args.Count == 0)
            return;

        for (int i = 0; i < args.Count; i++)
        {
            var spec = i < argSpecs.Count
                ? argSpecs[i]
                : new DslArgumentSpec(DslArgKind.Any, Required: false);

            var error = ValidateTypedArg(action, i + 1, args[i], spec, state);
            if (!string.IsNullOrWhiteSpace(error))
                throw new FormatException(error);
        }
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

    private static string? ValidateTypedArg(
        string action,
        int argIndex,
        string rawArg,
        DslArgumentSpec spec,
        GameState state)
    {
        if (string.IsNullOrWhiteSpace(rawArg))
            return null;

        string trimmed = rawArg.Trim();
        string normalized = Normalize(trimmed);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var kind = spec.Kind;

        if (kind.HasFlag(DslArgKind.Integer) && int.TryParse(trimmed, out _))
            return null;

        var candidates = BuildTypedCandidates(action, spec, state);
        if (candidates.Count == 0)
            return null;

        if (candidates.Any(c => string.Equals(c.Canonical, trimmed, StringComparison.Ordinal)))
            return null;

        if (TryFindBestMatch(normalized, candidates, spec, out var best) &&
            best.Score >= MinDidYouMeanScore)
        {
            return $"Command '{action}' argument {argIndex} value '{trimmed}' is not recognized. Did you mean '{best.Canonical}'?";
        }

        return $"Command '{action}' argument {argIndex} value '{trimmed}' is not recognized.";
    }

    private static IReadOnlyList<Candidate> BuildTypedCandidates(
        string action,
        DslArgumentSpec spec,
        GameState state)
    {
        var candidates = new List<Candidate>();

        if (spec.Kind.HasFlag(DslArgKind.Item))
            candidates.AddRange(BuildItemCandidates(state));

        if (spec.Kind.HasFlag(DslArgKind.Enum))
            candidates.AddRange(BuildEnumCandidates(spec));

        if (spec.Kind.HasFlag(DslArgKind.System))
            candidates.AddRange(BuildSystemCandidates(action, state));

        return candidates;
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

        foreach (var itemId in state.Cargo.Keys)
            AddAlias(map, itemId, itemId);

        foreach (var itemId in state.StorageItems.Keys)
            AddAlias(map, itemId, itemId);

        return ToCandidates(map, "item");
    }

    private static IReadOnlyList<Candidate> BuildEnumCandidates(DslArgumentSpec spec)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var value in DslEnumRegistry.ResolveValues(spec))
            AddAlias(map, value, value);
        return ToCandidates(map, "enum");
    }

    private static IReadOnlyList<Candidate> BuildSystemCandidates(string action, GameState state)
    {
        var systemMap = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var poiMap = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        AddAlias(systemMap, state.System, state.System);
        foreach (var system in state.Systems)
            AddAlias(systemMap, system, system);

        // `go` accepts POI IDs in addition to system IDs.
        if (string.Equals(action, "go", StringComparison.OrdinalIgnoreCase))
        {
            AddAlias(poiMap, state.CurrentPOI?.Id, state.CurrentPOI?.Id);
            foreach (var poi in state.POIs)
                AddAlias(poiMap, poi.Id, poi.Id);

            var mapCache = state.Galaxy?.Map?.Systems?.Count > 0
                ? state.Galaxy.Map
                : LoadMapCache();
            foreach (var system in mapCache.Systems)
            {
                AddAlias(systemMap, system.Id, system.Id);
                foreach (var poi in system.Pois)
                    AddAlias(poiMap, poi.Id, poi.Id);
            }

            foreach (var poi in mapCache.KnownPois)
            {
                AddAlias(poiMap, poi.Id, poi.Id);
                AddAlias(poiMap, poi.Id, poi.Name);
            }
        }

        var candidates = new List<Candidate>();
        candidates.AddRange(ToCandidates(systemMap, "system"));
        candidates.AddRange(ToCandidates(poiMap, "poi"));
        return candidates;
    }

    private static IReadOnlyList<Candidate> ToCandidates(
        Dictionary<string, HashSet<string>> aliasesByCanonical,
        string argType)
    {
        return aliasesByCanonical
            .Select(kvp => new Candidate(kvp.Key, kvp.Value.ToList(), argType))
            .ToList();
    }

    private static void AddAlias(
        Dictionary<string, HashSet<string>> map,
        string? canonicalRaw,
        string? aliasRaw)
    {
        var canonical = Normalize(canonicalRaw ?? string.Empty);
        if (string.IsNullOrWhiteSpace(canonical))
            return;

        var alias = Normalize(aliasRaw ?? string.Empty);
        if (string.IsNullOrWhiteSpace(alias))
            alias = canonical;

        if (!map.TryGetValue(canonical, out var aliases))
        {
            aliases = new HashSet<string>(StringComparer.Ordinal);
            map[canonical] = aliases;
        }

        aliases.Add(canonical);
        aliases.Add(alias);
    }

    private static bool TryFindBestMatch(
        string query,
        IReadOnlyList<Candidate> candidates,
        DslArgumentSpec spec,
        out MatchResult match)
    {
        string bestCanonical = string.Empty;
        string bestArgType = string.Empty;
        double bestScore = -1d;
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

            candidateScore = ApplyTypeWeight(spec, candidate.ArgType, candidateScore);

            if (candidateScore > bestScore ||
                (Math.Abs(candidateScore - bestScore) < 0.0001d && candidateDistance < bestDistance))
            {
                bestScore = candidateScore;
                bestCanonical = candidate.Canonical;
                bestArgType = candidate.ArgType;
                bestDistance = candidateDistance;
            }
        }

        if (string.IsNullOrWhiteSpace(bestCanonical))
        {
            match = new MatchResult("", -1d, "");
            return false;
        }

        match = new MatchResult(bestCanonical, bestScore, bestArgType);
        return true;
    }

    private static double ApplyTypeWeight(
        DslArgumentSpec spec,
        string argType,
        double baseScore)
    {
        double weight = GetArgTypeWeight(spec, argType);
        return baseScore * weight;
    }

    private static double GetArgTypeWeight(DslArgumentSpec spec, string argType)
    {
        if (spec.ArgTypeWeights == null ||
            spec.ArgTypeWeights.Count == 0 ||
            string.IsNullOrWhiteSpace(argType))
        {
            return 1d;
        }

        foreach (var (key, weight) in spec.ArgTypeWeights)
        {
            if (!string.Equals(key, argType, StringComparison.OrdinalIgnoreCase))
                continue;

            return weight > 0d
                ? weight
                : 1d;
        }

        return 1d;
    }

    private static double ComputeScore(string query, string candidateAlias)
    {
        return FuzzyMatchScoring.ComputeScore(query, candidateAlias);
    }

    private static GalaxyMapSnapshot LoadMapCache()
    {
        var map = GalaxyMapSnapshotFile.Load(AppPaths.GalaxyMapFile);
        var knownPois = GalaxyKnownPoiSnapshotFile.Load(AppPaths.GalaxyKnownPoisFile);
        GalaxyMapSnapshotFile.MergeKnownPois(map, knownPois);
        return map;
    }
}
