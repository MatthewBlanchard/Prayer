using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public sealed record PromptScriptExample(string Prompt, string Script);

public sealed record PromptScriptMatch(string Prompt, string Script, double Score);

public sealed class PromptScriptRag
{
    private readonly HttpClient _http;
    private readonly string _embeddingModel;
    private readonly List<RagEntry> _entries = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PromptScriptRag(
        string apiKey,
        string embeddingModel = "text-embedding-3-small")
    {
        _embeddingModel = string.IsNullOrWhiteSpace(embeddingModel)
            ? "text-embedding-3-small"
            : embeddingModel.Trim();

        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.openai.com")
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public void ReplaceExamples(IEnumerable<PromptScriptExample> examples)
    {
        _gate.Wait();
        try
        {
            _entries.Clear();

            foreach (var ex in examples)
            {
                if (string.IsNullOrWhiteSpace(ex.Prompt) || string.IsNullOrWhiteSpace(ex.Script))
                    continue;

                _entries.Add(new RagEntry
                {
                    Prompt = ex.Prompt.Trim(),
                    Script = ex.Script.Trim()
                });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void AddExample(PromptScriptExample example)
    {
        if (string.IsNullOrWhiteSpace(example.Prompt) || string.IsNullOrWhiteSpace(example.Script))
            return;

        _gate.Wait();
        try
        {
            bool exists = _entries.Any(e =>
                string.Equals(e.Prompt, example.Prompt.Trim(), StringComparison.Ordinal) &&
                string.Equals(e.Script, example.Script.Trim(), StringComparison.Ordinal));

            if (!exists)
            {
                _entries.Add(new RagEntry
                {
                    Prompt = example.Prompt.Trim(),
                    Script = example.Script.Trim()
                });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PromptScriptMatch>> FindTopMatchesAsync(
        string prompt,
        int maxMatches = 5,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt) || maxMatches <= 0)
            return Array.Empty<PromptScriptMatch>();

        await _gate.WaitAsync(ct);
        try
        {
            if (_entries.Count == 0)
                return Array.Empty<PromptScriptMatch>();

            var missing = _entries.Where(e => e.Embedding == null).ToList();

            var embeddingInputs = new List<string>(1 + missing.Count)
            {
                prompt.Trim()
            };
            embeddingInputs.AddRange(missing.Select(e => e.Prompt));

            var vectors = await EmbedBatchAsync(embeddingInputs, ct);
            if (vectors.Count == 0 || vectors[0] == null || vectors[0].Length == 0)
                return Array.Empty<PromptScriptMatch>();

            var queryVector = vectors[0];

            for (int i = 0; i < missing.Count; i++)
            {
                var v = vectors[i + 1];
                if (v != null && v.Length > 0)
                    missing[i].Embedding = v;
            }

            return _entries
                .Where(e => e.Embedding != null && e.Embedding.Length == queryVector.Length)
                .Select(e => new PromptScriptMatch(
                    e.Prompt,
                    e.Script,
                    CosineSimilarity(queryVector, e.Embedding!)))
                .OrderByDescending(m => m.Score)
                .Take(maxMatches)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> inputs,
        CancellationToken ct)
    {
        if (inputs.Count == 0)
            return Array.Empty<float[]>();

        var payload = new
        {
            model = _embeddingModel,
            input = inputs
        };

        var json = JsonSerializer.Serialize(payload);
        using var response = await _http.PostAsync(
            "/v1/embeddings",
            new StringContent(json, Encoding.UTF8, "application/json"),
            ct);

        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Embedding request failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var data = doc.RootElement.GetProperty("data");

        var vectors = new List<float[]>(data.GetArrayLength());
        foreach (var item in data.EnumerateArray())
        {
            var emb = item.GetProperty("embedding");
            var vec = new float[emb.GetArrayLength()];
            int idx = 0;
            foreach (var n in emb.EnumerateArray())
            {
                vec[idx++] = n.GetSingle();
            }
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

    private sealed class RagEntry
    {
        public string Prompt { get; set; } = string.Empty;
        public string Script { get; set; } = string.Empty;
        public float[]? Embedding { get; set; }
    }
}
