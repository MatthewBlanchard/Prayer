using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public sealed class ScriptGenerationExampleStore
{
    private readonly PromptScriptRag? _rag;
    private readonly List<ScriptGenerationExample> _examples = new();

    public ScriptGenerationExampleStore(PromptScriptRag? rag = null)
    {
        _rag = rag;
        Load();
        SyncRagExamples();
    }

    public int Count => _examples.Count;

    public bool Contains(string prompt, string script)
    {
        var normalizedPrompt = (prompt ?? string.Empty).Trim();
        var normalizedScript = (script ?? string.Empty).Trim();

        return _examples.Any(e =>
            string.Equals(e.Script, normalizedScript, StringComparison.Ordinal) &&
            string.Equals(e.Prompt, normalizedPrompt, StringComparison.Ordinal));
    }

    public async Task<bool> TryAddAsync(string prompt, string script)
    {
        var candidatePrompt = (prompt ?? string.Empty).Trim();
        var candidateScript = (script ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(candidatePrompt) || string.IsNullOrWhiteSpace(candidateScript))
            return false;

        if (Contains(candidatePrompt, candidateScript))
            return false;

        _examples.Add(new ScriptGenerationExample
        {
            Prompt = candidatePrompt,
            Script = candidateScript,
            CreatedUtc = DateTime.UtcNow
        });

        _rag?.AddExample(new PromptScriptExample(candidatePrompt, candidateScript));
        await SaveAsync();
        return true;
    }

    public async Task<IReadOnlyList<PromptScriptMatch>> FindTopMatchesAsync(string prompt, int maxMatches)
    {
        if (_rag == null || string.IsNullOrWhiteSpace(prompt))
            return Array.Empty<PromptScriptMatch>();

        try
        {
            return await _rag.FindTopMatchesAsync(prompt, maxMatches);
        }
        catch
        {
            // Retrieval failure should never block script generation.
            return Array.Empty<PromptScriptMatch>();
        }
    }

    public IReadOnlyList<PromptScriptMatch> GetRecentMatches(int maxMatches)
    {
        if (maxMatches <= 0)
            return Array.Empty<PromptScriptMatch>();

        return _examples
            .TakeLast(maxMatches)
            .Select(e => new PromptScriptMatch(e.Prompt, e.Script, 0d))
            .ToList();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.ScriptGenerationExamplesFile))
                return;

            var raw = File.ReadAllText(AppPaths.ScriptGenerationExamplesFile);
            var loaded = JsonSerializer.Deserialize<List<ScriptGenerationExample>>(raw);
            if (loaded == null || loaded.Count == 0)
                return;

            _examples.Clear();
            _examples.AddRange(
                loaded.Where(e =>
                    !string.IsNullOrWhiteSpace(e.Prompt) &&
                    !string.IsNullOrWhiteSpace(e.Script)));
        }
        catch
        {
            // Loading examples should never block startup.
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_examples, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(AppPaths.ScriptGenerationExamplesFile, json);
        }
        catch
        {
            // Persisting examples should never block gameplay.
        }
    }

    private void SyncRagExamples()
    {
        _rag?.ReplaceExamples(_examples.Select(e => new PromptScriptExample(e.Prompt, e.Script)));
    }

    private sealed class ScriptGenerationExample
    {
        public string Prompt { get; set; } = string.Empty;
        public string Script { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
    }
}
