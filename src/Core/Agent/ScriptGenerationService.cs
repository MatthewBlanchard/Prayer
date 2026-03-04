using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

public sealed record ScriptGenerationResult(string UserPrompt, string Script);

public sealed class ScriptGenerationService
{
    private const double PromptSearchMatchCutoff = 0.62d;

    private readonly ILLMClient _plannerLlm;
    private readonly ScriptGenerationExampleStore _exampleStore;
    private readonly IAgentLogger _logger;
    private readonly string _baseSystemPrompt;
    private readonly Action<string>? _setStatus;

    public ScriptGenerationService(
        ILLMClient plannerLlm,
        ScriptGenerationExampleStore exampleStore,
        IAgentLogger logger,
        string baseSystemPrompt,
        Action<string>? setStatus = null)
    {
        _plannerLlm = plannerLlm;
        _exampleStore = exampleStore;
        _logger = logger;
        _baseSystemPrompt = baseSystemPrompt;
        _setStatus = setStatus;
    }

    public async Task<ScriptGenerationResult> GenerateScriptFromUserInputAsync(
        string userInput,
        GameState state,
        int maxAttempts = 3)
    {
        var attempts = Math.Max(1, maxAttempts);
        var generationInput = (userInput ?? string.Empty).Trim();
        var stateContextBlock = BuildScriptGenerationStateContextBlock(state, generationInput);
        var examplesBlock = await BuildScriptGenerationExamplesBlockAsync(generationInput);
        string? previousScript = null;
        string? previousError = null;

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            var prompt = AgentPrompt.BuildScriptFromUserInputPrompt(
                baseSystemPrompt: _baseSystemPrompt,
                userInput: generationInput,
                stateContextBlock: stateContextBlock,
                examplesBlock: examplesBlock,
                attemptNumber: attempt,
                previousScript: previousScript,
                previousError: previousError);

            await _logger.LogScriptWriterContextTokensAsync(
                attempt,
                attempts,
                generationInput,
                stateContextBlock,
                examplesBlock,
                previousScript,
                previousError,
                prompt);
            await _logger.LogPlannerPromptAsync($"script_generation_attempt_{attempt}", prompt);

            var result = await _plannerLlm.CompleteAsync(
                prompt,
                maxTokens: 320,
                temperature: 0.2f,
                topP: 0.9f);

            var script = ExtractScript(result);

            try
            {
                var steps = DslInterpreter.Translate(script, state).ToList();
                var normalizedScript = DslInterpreter.RenderScript(steps).TrimEnd();
                _logger.LogScriptNormalization($"generation_attempt_{attempt}", script, normalizedScript);
                return new ScriptGenerationResult(generationInput, normalizedScript);
            }
            catch (FormatException ex)
            {
                previousScript = script;
                previousError = ex.Message;
                _setStatus?.Invoke($"Script generation retry {attempt}/{attempts}");
            }
        }

        throw new FormatException(
            "Failed to generate a valid script after retries. Last error: " +
            (previousError ?? "Unknown script error."));
    }

    private async Task<string> BuildScriptGenerationExamplesBlockAsync(string generationInput)
    {
        var sb = new StringBuilder();
        IReadOnlyList<PromptScriptMatch> matches = await _exampleStore.FindTopMatchesAsync(generationInput, maxMatches: 5);

        if (matches.Count == 0)
            matches = _exampleStore.GetRecentMatches(5);

        for (int i = 0; i < matches.Count; i++)
        {
            var example = matches[i];
            sb.Append(example.Prompt.Trim());
            sb.Append(" ->\n");
            sb.Append(NormalizeScriptExampleForPrompt(example.Script));
            sb.Append("\n\n");
        }

        return sb.ToString().TrimEnd();
    }

    private static string NormalizeScriptExampleForPrompt(string script)
    {
        try
        {
            return DslInterpreter.NormalizeScript(script);
        }
        catch
        {
            return script?.Trim() ?? string.Empty;
        }
    }

    private static string ExtractScript(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var text = raw.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;

        var firstNewline = text.IndexOf('\n');
        if (firstNewline < 0)
            return text.Trim('`').Trim();

        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence <= firstNewline)
            return text[(firstNewline + 1)..].Trim();

        return text.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
    }

    private static string BuildScriptGenerationStateContextBlock(GameState state, string userInput)
    {
        var searchTerms = BuildPromptSearchTerms(userInput);

        var topPoiMatches = FindTopMatches(
            searchTerms,
            BuildPoiAliasMap(state),
            maxMatches: 3);
        var topSystemMatches = FindTopMatches(
            searchTerms,
            BuildSystemAliasMap(state),
            maxMatches: 3);
        var topItemMatches = FindTopMatches(
            searchTerms,
            BuildItemAliasMap(state),
            maxMatches: 3);

        var poiPrimary = state.POIs
            .Select(p => (Key: p.Id, Label: $"{p.Id} ({p.Type})"))
            .ToList();
        var systemPrimary = state.Systems
            .Select(s => (Key: s, Label: s))
            .ToList();
        var cargoPrimary = state.Cargo.Values
            .OrderByDescending(c => c.Quantity)
            .Select(c => (Key: c.ItemId, Label: c.ItemId))
            .ToList();

        var poiLines = InterleaveWithTopMatches(
            poiPrimary,
            topPoiMatches,
            match => match);
        var systemLines = InterleaveWithTopMatches(
            systemPrimary,
            topSystemMatches,
            match => match);
        var cargoLines = InterleaveWithTopMatches(
            cargoPrimary,
            topItemMatches,
            match => match);
        var missionIssuingPoiLines = BuildMissionIssuingPoiLines(state);

        string currentPoiId = state.CurrentPOI?.Id ?? "-";
        string currentPoiType = state.CurrentPOI?.Type ?? "-";

        return
            "Current location:\n" +
            $"- system: {state.System}\n" +
            $"- poi: {currentPoiId} ({currentPoiType})\n\n" +
            "POIs:\n" + FormatPromptSectionLines(poiLines) + "\n\n" +
            "Systems:\n" + FormatPromptSectionLines(systemLines) + "\n\n" +
            "Items:\n" + FormatPromptSectionLines(cargoLines) + "\n\n" +
            "Mission issuing POI IDs:\n" + FormatPromptSectionLines(missionIssuingPoiLines);
    }

    private static IReadOnlyList<string> BuildMissionIssuingPoiLines(GameState state)
    {
        var lines = new List<string>();
        foreach (var mission in state.ActiveMissions ?? Array.Empty<MissionInfo>())
        {
            if (mission == null)
                continue;

            var missionName = string.IsNullOrWhiteSpace(mission.Title)
                ? (!string.IsNullOrWhiteSpace(mission.MissionId) ? mission.MissionId : mission.Id)
                : mission.Title.Trim();
            var issuingPoiId = ResolveIssuingPoiId(state, mission);
            if (string.IsNullOrWhiteSpace(issuingPoiId))
                continue;

            lines.Add($"{missionName} -> {issuingPoiId}");
        }

        return lines.Count == 0
            ? Array.Empty<string>()
            : lines;
    }

    private static string ResolveIssuingPoiId(GameState state, MissionInfo mission)
    {
        var directBaseId = (mission.IssuingBaseId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(directBaseId))
        {
            var poiFromBaseId = (state.POIs ?? Array.Empty<POIInfo>())
                .FirstOrDefault(p => string.Equals(p.BaseId ?? "", directBaseId, StringComparison.OrdinalIgnoreCase));
            if (poiFromBaseId != null && !string.IsNullOrWhiteSpace(poiFromBaseId.Id))
                return poiFromBaseId.Id.Trim();
        }

        var issuingBase = (mission.IssuingBase ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(issuingBase))
            return directBaseId;

        var pois = state.POIs ?? Array.Empty<POIInfo>();
        var byPoiId = pois.FirstOrDefault(p =>
            string.Equals(p.Id ?? "", issuingBase, StringComparison.OrdinalIgnoreCase));
        if (byPoiId != null && !string.IsNullOrWhiteSpace(byPoiId.Id))
            return byPoiId.Id.Trim();

        var byBaseId = pois.FirstOrDefault(p =>
            string.Equals(p.BaseId ?? "", issuingBase, StringComparison.OrdinalIgnoreCase));
        if (byBaseId != null && !string.IsNullOrWhiteSpace(byBaseId.Id))
            return byBaseId.Id.Trim();

        var byName = pois.FirstOrDefault(p =>
            string.Equals(p.Name ?? "", issuingBase, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.BaseName ?? "", issuingBase, StringComparison.OrdinalIgnoreCase));
        if (byName != null && !string.IsNullOrWhiteSpace(byName.Id))
            return byName.Id.Trim();

        return directBaseId;
    }

    private static IReadOnlyList<string> BuildPromptSearchTerms(string userInput)
    {
        if (string.IsNullOrWhiteSpace(userInput))
            return Array.Empty<string>();

        var tokens = new List<string>();
        var sb = new StringBuilder();

        foreach (char ch in userInput)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                continue;
            }

            if (sb.Length > 0)
            {
                tokens.Add(sb.ToString());
                sb.Clear();
            }
        }

        if (sb.Length > 0)
            tokens.Add(sb.ToString());

        if (tokens.Count == 0)
            return Array.Empty<string>();

        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddTerm(string value)
        {
            string normalized = DslFuzzyMatcher.Normalize(value);
            if (string.IsNullOrWhiteSpace(normalized))
                return;

            if (seen.Add(normalized))
                terms.Add(normalized);
        }

        int maxN = Math.Min(3, tokens.Count);
        for (int n = maxN; n >= 1; n--)
        {
            for (int i = 0; i + n <= tokens.Count; i++)
                AddTerm(string.Join('_', tokens.Skip(i).Take(n)));
        }

        return terms;
    }

    private static Dictionary<string, HashSet<string>> BuildSystemAliasMap(GameState state)
    {
        var aliases = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        AddPromptAlias(aliases, state.System, state.System);
        foreach (var systemId in state.Systems)
            AddPromptAlias(aliases, systemId, systemId);

        var map = state.Galaxy?.Map?.Systems?.Count > 0
            ? state.Galaxy.Map
            : LoadMapCache();
        foreach (var system in map.Systems)
            AddPromptAlias(aliases, system.Id, system.Id);

        return aliases;
    }

    private static Dictionary<string, HashSet<string>> BuildPoiAliasMap(GameState state)
    {
        var aliases = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        AddPromptAlias(aliases, state.CurrentPOI?.Id, state.CurrentPOI?.Id);
        foreach (var poi in state.POIs)
        {
            AddPromptAlias(aliases, poi.Id, poi.Id);
            AddPromptAlias(aliases, poi.Id, poi.Name);
        }

        var map = state.Galaxy?.Map?.Systems?.Count > 0
            ? state.Galaxy.Map
            : LoadMapCache();
        foreach (var system in map.Systems)
        {
            foreach (var poi in system.Pois)
                AddPromptAlias(aliases, poi.Id, poi.Id);
        }

        foreach (var poi in map.KnownPois)
        {
            AddPromptAlias(aliases, poi.Id, poi.Id);
            AddPromptAlias(aliases, poi.Id, poi.Name);
        }

        return aliases;
    }

    private static Dictionary<string, HashSet<string>> BuildItemAliasMap(GameState state)
    {
        var aliases = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var itemId in state.Cargo.Keys)
            AddPromptAlias(aliases, itemId, itemId);

        if (state.StorageItems != null)
        {
            foreach (var itemId in state.StorageItems.Keys)
                AddPromptAlias(aliases, itemId, itemId);
        }

        if (state.Galaxy?.Catalog?.ItemsById != null)
        {
            foreach (var (itemId, entry) in state.Galaxy.Catalog.ItemsById)
            {
                AddPromptAlias(aliases, itemId, itemId);
                AddPromptAlias(aliases, itemId, entry?.Name);
            }
        }

        return aliases;
    }

    private static IReadOnlyList<string> FindTopMatches(
        IReadOnlyList<string> searchTerms,
        IReadOnlyDictionary<string, HashSet<string>> aliasesByCanonical,
        int maxMatches)
    {
        if (searchTerms.Count == 0 ||
            aliasesByCanonical.Count == 0 ||
            maxMatches <= 0)
        {
            return Array.Empty<string>();
        }

        var scored = new List<(string Canonical, double Score, int Distance)>();

        foreach (var (canonical, aliases) in aliasesByCanonical)
        {
            double bestScore = -1d;
            int bestDistance = int.MaxValue;

            foreach (var alias in aliases)
            {
                foreach (var term in searchTerms)
                {
                    double score = ComputePromptMatchScore(term, alias);
                    int distance = FuzzyMatchScoring.LevenshteinDistance(term, alias);

                    if (score > bestScore ||
                        (Math.Abs(score - bestScore) < 0.0001d && distance < bestDistance))
                    {
                        bestScore = score;
                        bestDistance = distance;
                    }
                }
            }

            if (bestScore >= PromptSearchMatchCutoff)
                scored.Add((canonical, bestScore, bestDistance));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Distance)
            .ThenBy(s => s.Canonical, StringComparer.Ordinal)
            .Take(maxMatches)
            .Select(s => s.Canonical)
            .ToList();
    }

    private static IReadOnlyList<string> InterleaveWithTopMatches(
        IReadOnlyList<(string Key, string Label)> primaryEntries,
        IReadOnlyList<string> topMatches,
        Func<string, string> matchLabelFactory)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        int primaryIndex = 0;
        int matchIndex = 0;

        while (primaryIndex < primaryEntries.Count || matchIndex < topMatches.Count)
        {
            if (primaryIndex < primaryEntries.Count)
            {
                var primary = primaryEntries[primaryIndex++];
                string key = DslFuzzyMatcher.Normalize(primary.Key);
                if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                    result.Add(primary.Label);
            }

            if (matchIndex < topMatches.Count)
            {
                string match = topMatches[matchIndex++];
                string key = DslFuzzyMatcher.Normalize(match);
                if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                    result.Add(matchLabelFactory(match));
            }
        }

        return result;
    }

    private static string FormatPromptSectionLines(IReadOnlyList<string> lines)
    {
        if (lines == null || lines.Count == 0)
            return "- none";

        return string.Join("\n", lines.Select(l => $"- {l}"));
    }

    private static void AddPromptAlias(
        Dictionary<string, HashSet<string>> aliasesByCanonical,
        string? canonicalRaw,
        string? aliasRaw)
    {
        if (string.IsNullOrWhiteSpace(canonicalRaw))
            return;

        string canonical = canonicalRaw.Trim();
        string alias = DslFuzzyMatcher.Normalize(aliasRaw ?? canonical);
        if (string.IsNullOrWhiteSpace(alias))
            alias = DslFuzzyMatcher.Normalize(canonical);

        if (string.IsNullOrWhiteSpace(alias))
            return;

        if (!aliasesByCanonical.TryGetValue(canonical, out var aliases))
        {
            aliases = new HashSet<string>(StringComparer.Ordinal);
            aliasesByCanonical[canonical] = aliases;
        }

        aliases.Add(alias);
        aliases.Add(DslFuzzyMatcher.Normalize(canonical));
    }

    private static double ComputePromptMatchScore(string query, string candidateAlias)
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
