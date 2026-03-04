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

        foreach (var itemId in state.Shared.StorageItems.Keys)
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
                var aliasDistance = LevenshteinDistance(query, alias);

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
        if (query.Length == 0 || candidateAlias.Length == 0)
            return -1d;

        if (string.Equals(query, candidateAlias, StringComparison.Ordinal))
            return 1d;

        if (candidateAlias.StartsWith(query, StringComparison.Ordinal))
            return 0.94d;

        if (query.StartsWith(candidateAlias, StringComparison.Ordinal))
            return 0.88d;

        if (candidateAlias.Contains(query, StringComparison.Ordinal))
            return 0.82d;

        var tokenScore = TokenOverlapScore(query, candidateAlias);
        var editScore = LevenshteinSimilarity(query, candidateAlias);

        return (editScore * 0.65d) + (tokenScore * 0.35d);
    }

    private static double TokenOverlapScore(string a, string b)
    {
        var aTokens = a.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var bTokens = b.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (aTokens.Length == 0 || bTokens.Length == 0)
            return 0d;

        var aSet = aTokens.ToHashSet(StringComparer.Ordinal);
        var bSet = bTokens.ToHashSet(StringComparer.Ordinal);

        int overlap = aSet.Count(t => bSet.Contains(t));
        int union = aSet.Count + bSet.Count - overlap;
        if (union <= 0)
            return 0d;

        return overlap / (double)union;
    }

    private static double LevenshteinSimilarity(string a, string b)
    {
        int maxLen = Math.Max(a.Length, b.Length);
        if (maxLen == 0)
            return 1d;

        int distance = LevenshteinDistance(a, b);
        return Math.Max(0d, 1d - (distance / (double)maxLen));
    }

    private static int LevenshteinDistance(string a, string b)
    {
        int n = a.Length;
        int m = b.Length;
        if (n == 0)
            return m;
        if (m == 0)
            return n;

        var prev = new int[m + 1];
        var cur = new int[m + 1];

        for (int j = 0; j <= m; j++)
            prev[j] = j;

        for (int i = 1; i <= n; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(
                    Math.Min(cur[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[m];
    }

    private static GalaxyMapSnapshot LoadMapCache()
    {
        return GalaxyMapSnapshotFile.Load(AppPaths.GalaxyMapFile);
    }
}
