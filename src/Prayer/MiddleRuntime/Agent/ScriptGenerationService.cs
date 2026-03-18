using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
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
    private readonly HttpClient? _embeddingHttp;
    private readonly string _embeddingModel = "text-embedding-3-small";
    private readonly SemaphoreSlim _commandEmbeddingGate = new(1, 1);
    private Dictionary<string, float[]>? _commandEmbeddingsByName;

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

        var embeddingApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(embeddingApiKey))
        {
            _embeddingHttp = new HttpClient
            {
                BaseAddress = new Uri("https://api.openai.com")
            };
            _embeddingHttp.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", embeddingApiKey);
        }
    }

    public async Task<ScriptGenerationResult> GenerateScriptFromUserInputAsync(
        string userInput,
        GameState state,
        int maxAttempts = 3,
        CancellationToken cancellationToken = default)
    {
        var attempts = Math.Max(1, maxAttempts);
        var generationInput = (userInput ?? string.Empty).Trim();
        var stateContextBlock = BuildScriptGenerationStateContextBlock(state, generationInput);
        var (examplesBlock, exampleScripts) = await BuildScriptGenerationExamplesBlockAsync(generationInput);
        var cosineCommands = await SelectCommandsByCosineSimilarityAsync(
            generationInput,
            maxMatches: 12,
            cancellationToken);
        var dslCommandReferenceBlock = DslParser.BuildPromptDslReferenceBlock(
            generationInput,
            exampleScripts,
            cosineCommands);
        string? previousScript = null;
        string? previousError = null;

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            var prompt = AgentPrompt.BuildScriptFromUserInputPrompt(
                baseSystemPrompt: _baseSystemPrompt,
                userInput: generationInput,
                stateContextBlock: stateContextBlock,
                dslCommandReferenceBlock: dslCommandReferenceBlock,
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
                topP: 0.9f,
                cancellationToken: cancellationToken);

            var script = ExtractScript(result);

            try
            {
                var tree = DslParser.ParseTree(script);
                _ = DslScriptTransformer.Translate(tree, state);
                var normalizedScript = DslScriptTransformer.RenderScript(tree).TrimEnd();
                await _logger.LogPromptGenerationPairAsync(
                    attempt,
                    attempts,
                    prompt,
                    normalizedScript,
                    parseSucceeded: true);
                _logger.LogScriptNormalization($"generation_attempt_{attempt}", script, normalizedScript);
                return new ScriptGenerationResult(generationInput, normalizedScript);
            }
            catch (FormatException ex)
            {
                await _logger.LogPromptGenerationPairAsync(
                    attempt,
                    attempts,
                    prompt,
                    script,
                    parseSucceeded: false);
                previousScript = script;
                previousError = ex.Message;
                _setStatus?.Invoke($"Script generation retry {attempt}/{attempts}");
            }
        }

        throw new FormatException(
            "Failed to generate a valid script after retries. Last error: " +
            (previousError ?? "Unknown script error."));
    }

    private async Task<(string examplesBlock, IReadOnlyList<string> exampleScripts)> BuildScriptGenerationExamplesBlockAsync(
        string generationInput)
    {
        var sb = new StringBuilder();
        IReadOnlyList<PromptScriptMatch> matches = await _exampleStore.FindTopMatchesAsync(generationInput, maxMatches: 5);

        if (matches.Count == 0)
            matches = _exampleStore.GetRecentMatches(5);

        var scripts = new List<string>(matches.Count);

        for (int i = 0; i < matches.Count; i++)
        {
            var example = matches[i];
            sb.Append(example.Prompt.Trim());
            sb.Append(" ->\n");
            var normalizedScript = NormalizeScriptExampleForPrompt(example.Script);
            sb.Append(normalizedScript);
            sb.Append("\n\n");
            if (!string.IsNullOrWhiteSpace(normalizedScript))
                scripts.Add(normalizedScript);
        }

        return (sb.ToString().TrimEnd(), scripts);
    }

    private async Task<IReadOnlyList<string>> SelectCommandsByCosineSimilarityAsync(
        string generationInput,
        int maxMatches,
        CancellationToken ct)
    {
        if (_embeddingHttp == null ||
            string.IsNullOrWhiteSpace(generationInput) ||
            maxMatches <= 0)
        {
            return Array.Empty<string>();
        }

        try
        {
            var docs = DslParser.GetPromptCommandDocs();
            if (docs.Count == 0)
                return Array.Empty<string>();

            await _commandEmbeddingGate.WaitAsync(ct);
            try
            {
                if (_commandEmbeddingsByName == null || _commandEmbeddingsByName.Count == 0)
                {
                    var docVectors = await EmbedBatchAsync(
                        docs.Select(d => d.Text).ToList(),
                        ct);

                    var map = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < docs.Count && i < docVectors.Count; i++)
                    {
                        var vec = docVectors[i];
                        if (vec.Length > 0)
                            map[docs[i].Name] = vec;
                    }

                    _commandEmbeddingsByName = map;
                }
            }
            finally
            {
                _commandEmbeddingGate.Release();
            }

            var queryVectors = await EmbedBatchAsync(new[] { generationInput }, ct);
            if (queryVectors.Count == 0 || queryVectors[0].Length == 0)
                return Array.Empty<string>();

            var query = queryVectors[0];
            var ranked = new List<(string Name, double Score)>();
            foreach (var doc in docs)
            {
                if (_commandEmbeddingsByName == null ||
                    !_commandEmbeddingsByName.TryGetValue(doc.Name, out var vec))
                {
                    continue;
                }

                var score = CosineSimilarity(query, vec);
                if (score > 0d)
                    ranked.Add((doc.Name, score));
            }

            return ranked
                .OrderByDescending(v => v.Score)
                .Take(maxMatches)
                .Select(v => v.Name)
                .ToList();
        }
        catch
        {
            // Embedding retrieval should never block script generation.
            return Array.Empty<string>();
        }
    }

    private async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> inputs,
        CancellationToken ct)
    {
        if (_embeddingHttp == null || inputs.Count == 0)
            return Array.Empty<float[]>();

        var payload = new
        {
            model = _embeddingModel,
            input = inputs
        };

        var json = JsonSerializer.Serialize(payload);
        using var response = await _embeddingHttp.PostAsync(
            "/v1/embeddings",
            new StringContent(json, Encoding.UTF8, "application/json"),
            ct);

        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            return Array.Empty<float[]>();

        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<float[]>();
        }

        var vectors = new List<float[]>(data.GetArrayLength());
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("embedding", out var emb) ||
                emb.ValueKind != JsonValueKind.Array)
            {
                vectors.Add(Array.Empty<float>());
                continue;
            }

            var vec = new float[emb.GetArrayLength()];
            int idx = 0;
            foreach (var n in emb.EnumerateArray())
                vec[idx++] = n.GetSingle();
            vectors.Add(vec);
        }

        return vectors;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length)
            return -1d;

        double dot = 0d;
        double normA = 0d;
        double normB = 0d;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA <= 0d || normB <= 0d)
            return -1d;

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    private static string NormalizeScriptExampleForPrompt(string script)
    {
        try
        {
            return DslScriptTransformer.NormalizeScript(script);
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
        var cargoPrimary = state.Ship.Cargo.Values
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

        foreach (var itemId in state.Ship.Cargo.Keys)
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
