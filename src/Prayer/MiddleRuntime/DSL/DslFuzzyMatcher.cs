using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

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

        if (candidates.Any(c => c.Aliases.Any(alias => string.Equals(alias, normalized, StringComparison.Ordinal))))
            return null;

        if (TryFindBestMatch(normalized, candidates, spec, out var best) &&
            best.Score >= MinDidYouMeanScore)
        {
            LogGoValidationFailureDiagnostics(
                action,
                argIndex,
                trimmed,
                normalized,
                state,
                candidates,
                best.Canonical,
                best.Score);
            return $"Command '{action}' argument {argIndex} value '{trimmed}' is not recognized. Did you mean '{best.Canonical}'?";
        }

        LogGoValidationFailureDiagnostics(
            action,
            argIndex,
            trimmed,
            normalized,
            state,
            candidates,
            bestCanonical: null,
            bestScore: null);
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

        foreach (var itemId in state.Ship.Cargo.Keys)
            AddAlias(map, itemId, itemId);

        foreach (var itemId in state.StorageItems.Keys)
            AddAlias(map, itemId, itemId);

        return ToCandidates(map, "item");
    }

    private static IReadOnlyList<Candidate> BuildEnumCandidates(DslArgumentSpec spec)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var value in spec.EnumValues ?? Array.Empty<string>())
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

            // Merge both in-memory and persisted map snapshots; runtime map can be partial.
            AddMapCandidates(state.Galaxy?.Map, systemMap, poiMap);
            AddMapCandidates(LoadMapCache(), systemMap, poiMap);
        }

        var candidates = new List<Candidate>();
        candidates.AddRange(ToCandidates(systemMap, "system"));
        candidates.AddRange(ToCandidates(poiMap, "poi"));
        return candidates;
    }

    private static void AddMapCandidates(
        GalaxyMapSnapshot? map,
        Dictionary<string, HashSet<string>> systemMap,
        Dictionary<string, HashSet<string>> poiMap)
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
        return GalaxyMapSnapshotFile.LoadWithKnownPois(
            AppPaths.GalaxyMapFile,
            AppPaths.GalaxyKnownPoisFile);
    }

    private static void LogGoValidationFailureDiagnostics(
        string action,
        int argIndex,
        string rawArg,
        string normalizedArg,
        GameState state,
        IReadOnlyList<Candidate> candidates,
        string? bestCanonical,
        double? bestScore)
    {
        if (!string.Equals(action, "go", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var stateMap = state.Galaxy?.Map;
            var diskMap = LoadMapCache();

            bool inStateCurrent = string.Equals(Normalize(state.System), normalizedArg, StringComparison.Ordinal);
            bool inStateNeighbors = state.Systems.Any(s => string.Equals(Normalize(s), normalizedArg, StringComparison.Ordinal));
            bool inStateLivePois = state.POIs.Any(p => string.Equals(Normalize(p.Id), normalizedArg, StringComparison.Ordinal));
            bool inStateMapSystems = (stateMap?.Systems ?? new List<GalaxySystemInfo>())
                .Any(s => string.Equals(Normalize(s.Id), normalizedArg, StringComparison.Ordinal));
            bool inStateMapPois = (stateMap?.KnownPois ?? new List<GalaxyKnownPoiInfo>())
                .Any(p => string.Equals(Normalize(p.Id), normalizedArg, StringComparison.Ordinal) ||
                          string.Equals(Normalize(p.Name), normalizedArg, StringComparison.Ordinal));
            bool inDiskMapSystems = diskMap.Systems
                .Any(s => string.Equals(Normalize(s.Id), normalizedArg, StringComparison.Ordinal));
            bool inDiskMapPois = diskMap.KnownPois
                .Any(p => string.Equals(Normalize(p.Id), normalizedArg, StringComparison.Ordinal) ||
                          string.Equals(Normalize(p.Name), normalizedArg, StringComparison.Ordinal));
            bool inCandidateAliases = candidates.Any(c =>
                c.Aliases.Any(alias => string.Equals(alias, normalizedArg, StringComparison.Ordinal)));

            var sb = new StringBuilder();
            sb.Append('[').Append(DateTime.UtcNow.ToString("O")).Append("] ");
            sb.Append("go-arg-validation-fail ");
            sb.Append("arg_index=").Append(argIndex).Append(' ');
            sb.Append("raw='").Append(rawArg).Append("' ");
            sb.Append("normalized='").Append(normalizedArg).Append("' ");
            sb.Append("candidate_count=").Append(candidates.Count).Append(' ');
            sb.Append("state.system='").Append(state.System ?? "").Append("' ");
            sb.Append("state.systems_count=").Append(state.Systems?.Length ?? 0).Append(' ');
            sb.Append("state.pois_count=").Append(state.POIs?.Length ?? 0).Append(' ');
            sb.Append("state.map.systems_count=").Append(stateMap?.Systems?.Count ?? 0).Append(' ');
            sb.Append("state.map.known_pois_count=").Append(stateMap?.KnownPois?.Count ?? 0).Append(' ');
            sb.Append("disk.map.systems_count=").Append(diskMap.Systems.Count).Append(' ');
            sb.Append("disk.map.known_pois_count=").Append(diskMap.KnownPois.Count).Append(' ');
            sb.Append("found_in_state_current=").Append(inStateCurrent).Append(' ');
            sb.Append("found_in_state_neighbors=").Append(inStateNeighbors).Append(' ');
            sb.Append("found_in_state_live_pois=").Append(inStateLivePois).Append(' ');
            sb.Append("found_in_state_map_systems=").Append(inStateMapSystems).Append(' ');
            sb.Append("found_in_state_map_pois=").Append(inStateMapPois).Append(' ');
            sb.Append("found_in_disk_map_systems=").Append(inDiskMapSystems).Append(' ');
            sb.Append("found_in_disk_map_pois=").Append(inDiskMapPois).Append(' ');
            sb.Append("found_in_candidates=").Append(inCandidateAliases).Append(' ');
            if (!string.IsNullOrWhiteSpace(bestCanonical) && bestScore.HasValue)
            {
                sb.Append("best='").Append(bestCanonical).Append("' ");
                sb.Append("best_score=").Append(bestScore.Value.ToString("0.000"));
            }

            sb.AppendLine();
            LogSink.Instance.Enqueue(new LogEvent(
                DateTime.UtcNow, LogKind.GoArgValidation, sb.ToString(), AppPaths.GoArgValidationLogFile));

            var dump = new StringBuilder();
            dump.AppendLine($"[{DateTime.UtcNow:O}] go-arg-validation-mapdump");
            dump.AppendLine($"raw='{rawArg}' normalized='{normalizedArg}' arg_index={argIndex}");
            dump.AppendLine("state_galaxy_map:");
            dump.AppendLine(JsonSerializer.Serialize(
                stateMap ?? new GalaxyMapSnapshot(),
                new JsonSerializerOptions { WriteIndented = true }));
            dump.AppendLine("disk_loaded_map:");
            dump.AppendLine(JsonSerializer.Serialize(
                diskMap,
                new JsonSerializerOptions { WriteIndented = true }));
            dump.AppendLine();
            LogSink.Instance.Enqueue(new LogEvent(
                DateTime.UtcNow, LogKind.GoArgValidationMapDump, dump.ToString(), AppPaths.GoArgValidationMapDumpLogFile));
        }
        catch
        {
            // Diagnostics must never block command validation.
        }
    }
}
