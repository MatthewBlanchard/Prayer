using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;

public interface ILLMClient
{
    Task<string> CompleteAsync(
        string prompt,
        int maxTokens,
        float temperature,
        float topP,
        float repeatPenalty = 1.1f,
        CancellationToken cancellationToken = default); // default
}

public class LlamaCppClient : ILLMClient
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string _logFile = AppPaths.LlmLogFile;

    public LlamaCppClient(string baseUrl = "http://localhost:8080", string model = "model")
    {
        _http = new HttpClient();
        _endpoint = $"{baseUrl}/v1/completions";
        _model = string.IsNullOrWhiteSpace(model) ? "model" : model.Trim();
    }

    public async Task<string> CompleteAsync(
        string prompt,
        int maxTokens,
        float temperature,
        float topP,
        float repeatPenalty = 1.2f,
        CancellationToken cancellationToken = default) // default repetition penalty
    {
        var payload = new
        {
            model = _model,
            prompt = prompt,
            max_tokens = maxTokens,
            temperature = temperature,
            top_p = topP,
            repeat_penalty = repeatPenalty
        };

        var json = JsonSerializer.Serialize(payload);

        await LogAsync("PROMPT", prompt);
        var response = await _http.PostAsync(
            _endpoint,
            new StringContent(json, Encoding.UTF8, "application/json"),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

        using var doc = JsonDocument.Parse(responseJson);

        var result = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("text")
            .GetString() ?? "";

        await LogAsync("OUTPUT", result);

        return result;
    }

    private async Task LogAsync(string type, string content)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"[{DateTime.UtcNow:O}] === {type} ===");
        sb.AppendLine($"Model: {_model}");
        sb.AppendLine(content);
        sb.AppendLine();

        await File.AppendAllTextAsync(_logFile, sb.ToString());
    }
}

public class OpenAIClient : ILLMClient
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _logFile = AppPaths.LlmLogFile;
    private readonly string _errorLogFile = AppPaths.OpenAiErrorsLogFile;

    public OpenAIClient(string apiKey, string model = "gpt-4o-mini")
    {
        _model = model;
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.openai.com")
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<string> CompleteAsync(
        string prompt,
        int maxTokens,
        float temperature,
        float topP,
        float repeatPenalty = 1.1f,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var messages = BuildMessages(prompt);
            var resolvedTemperature = ResolveTemperatureForModel(temperature);

            var payload = new
            {
                model = _model,
                messages,
                max_completion_tokens = maxTokens,
                temperature = resolvedTemperature,
                top_p = topP
            };

            var json = JsonSerializer.Serialize(payload);
            await LogAsync("PROMPT", prompt);

            var response = await _http.PostAsync(
                "/v1/chat/completions",
                new StringContent(json, Encoding.UTF8, "application/json"),
                cancellationToken);

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                await LogErrorAsync(
                    "HTTP_ERROR",
                    $"Status: {(int)response.StatusCode} {response.StatusCode}\nResponse:\n{responseJson}");
                response.EnsureSuccessStatusCode();
            }

            using var doc = JsonDocument.Parse(responseJson);

            var result = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            await LogAsync("OUTPUT", result);
            return result;
        }
        catch (Exception ex)
        {
            await LogErrorAsync("EXCEPTION", ex.ToString());
            throw;
        }
    }

    private async Task LogAsync(string type, string content)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{DateTime.UtcNow:O}] === {type} ===");
        sb.AppendLine($"Model: {_model}");
        sb.AppendLine(content);
        sb.AppendLine();

        await File.AppendAllTextAsync(_logFile, sb.ToString());
    }

    private float ResolveTemperatureForModel(float requestedTemperature)
    {
        // GPT-5 chat variants currently reject non-default temperature values.
        if (_model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase))
            return 1f;

        return requestedTemperature;
    }

    private async Task LogErrorAsync(string type, string content)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{DateTime.UtcNow:O}] === {type} ===");
        sb.AppendLine($"Model: {_model}");
        sb.AppendLine(content);
        sb.AppendLine();

        await File.AppendAllTextAsync(_errorLogFile, sb.ToString());
    }

    private static object[] BuildMessages(string prompt)
    {
        const string start = "<|start_header_id|>";
        const string end = "<|end_header_id|>";
        const string eot = "<|eot_id|>";

        if (!prompt.Contains(start, StringComparison.Ordinal))
        {
            return new object[]
            {
                new { role = "user", content = prompt }
            };
        }

        var messages = new List<object>();
        int index = 0;

        while (true)
        {
            int startIdx = prompt.IndexOf(start, index, StringComparison.Ordinal);
            if (startIdx < 0)
                break;

            int roleStart = startIdx + start.Length;
            int endIdx = prompt.IndexOf(end, roleStart, StringComparison.Ordinal);
            if (endIdx < 0)
                break;

            string role = prompt.Substring(roleStart, endIdx - roleStart).Trim();
            int contentStart = endIdx + end.Length;

            int eotIdx = prompt.IndexOf(eot, contentStart, StringComparison.Ordinal);
            int nextStartIdx = prompt.IndexOf(start, contentStart, StringComparison.Ordinal);

            int contentEnd;
            if (eotIdx >= 0 && (nextStartIdx < 0 || eotIdx <= nextStartIdx))
                contentEnd = eotIdx;
            else if (nextStartIdx >= 0)
                contentEnd = nextStartIdx;
            else
                contentEnd = prompt.Length;

            string content = prompt.Substring(contentStart, contentEnd - contentStart).Trim();
            if (!string.IsNullOrWhiteSpace(content))
            {
                string normalizedRole = role switch
                {
                    "system" => "system",
                    "assistant" => "assistant",
                    _ => "user"
                };
                messages.Add(new { role = normalizedRole, content });
            }

            index = contentEnd;
            if (eotIdx >= 0 && eotIdx == contentEnd)
                index += eot.Length;
        }

        return messages.Count > 0
            ? messages.ToArray()
            : new object[] { new { role = "user", content = prompt } };
    }
}

public class GroqClient : ILLMClient
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _logFile = AppPaths.LlmLogFile;
    private readonly string _errorLogFile = AppPaths.OpenAiErrorsLogFile;

    public GroqClient(string apiKey, string model = "llama-3.3-70b-versatile")
    {
        _model = model;
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.groq.com/openai")
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<string> CompleteAsync(
        string prompt,
        int maxTokens,
        float temperature,
        float topP,
        float repeatPenalty = 1.1f,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var messages = OpenAIClientBuildMessages(prompt);

            var payload = new
            {
                model = _model,
                messages,
                max_tokens = maxTokens,
                temperature,
                top_p = topP
            };

            var json = JsonSerializer.Serialize(payload);
            await LogAsync("PROMPT", prompt);

            var response = await _http.PostAsync(
                "/v1/chat/completions",
                new StringContent(json, Encoding.UTF8, "application/json"),
                cancellationToken);

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                await LogErrorAsync(
                    "HTTP_ERROR",
                    $"Status: {(int)response.StatusCode} {response.StatusCode}\nResponse:\n{responseJson}");
                response.EnsureSuccessStatusCode();
            }

            using var doc = JsonDocument.Parse(responseJson);

            var result = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            await LogAsync("OUTPUT", result);
            return result;
        }
        catch (Exception ex)
        {
            await LogErrorAsync("EXCEPTION", ex.ToString());
            throw;
        }
    }

    private async Task LogAsync(string type, string content)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{DateTime.UtcNow:O}] === {type} ===");
        sb.AppendLine($"Model: {_model}");
        sb.AppendLine(content);
        sb.AppendLine();

        await File.AppendAllTextAsync(_logFile, sb.ToString());
    }

    private async Task LogErrorAsync(string type, string content)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{DateTime.UtcNow:O}] === {type} ===");
        sb.AppendLine($"Model: {_model}");
        sb.AppendLine(content);
        sb.AppendLine();

        await File.AppendAllTextAsync(_errorLogFile, sb.ToString());
    }

    private static object[] OpenAIClientBuildMessages(string prompt)
    {
        const string start = "<|start_header_id|>";
        const string end = "<|end_header_id|>";
        const string eot = "<|eot_id|>";

        if (!prompt.Contains(start, StringComparison.Ordinal))
        {
            return new object[]
            {
                new { role = "user", content = prompt }
            };
        }

        var messages = new List<object>();
        int index = 0;

        while (true)
        {
            int startIdx = prompt.IndexOf(start, index, StringComparison.Ordinal);
            if (startIdx < 0)
                break;

            int roleStart = startIdx + start.Length;
            int endIdx = prompt.IndexOf(end, roleStart, StringComparison.Ordinal);
            if (endIdx < 0)
                break;

            string role = prompt.Substring(roleStart, endIdx - roleStart).Trim();
            int contentStart = endIdx + end.Length;

            int eotIdx = prompt.IndexOf(eot, contentStart, StringComparison.Ordinal);
            int nextStartIdx = prompt.IndexOf(start, contentStart, StringComparison.Ordinal);

            int contentEnd;
            if (eotIdx >= 0 && (nextStartIdx < 0 || eotIdx <= nextStartIdx))
                contentEnd = eotIdx;
            else if (nextStartIdx >= 0)
                contentEnd = nextStartIdx;
            else
                contentEnd = prompt.Length;

            string content = prompt.Substring(contentStart, contentEnd - contentStart).Trim();
            if (!string.IsNullOrWhiteSpace(content))
            {
                string normalizedRole = role switch
                {
                    "system" => "system",
                    "assistant" => "assistant",
                    _ => "user"
                };
                messages.Add(new { role = normalizedRole, content });
            }

            index = contentEnd;
            if (eotIdx >= 0 && eotIdx == contentEnd)
                index += eot.Length;
        }

        return messages.Count > 0
            ? messages.ToArray()
            : new object[] { new { role = "user", content = prompt } };
    }
}

public sealed class AnthropicClient : ILLMClient
{
    private const string AnthropicVersion = "2023-06-01";
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _logFile = AppPaths.LlmLogFile;
    private readonly string _errorLogFile = AppPaths.OpenAiErrorsLogFile;

    public AnthropicClient(string apiKey, string model = "claude-3-5-sonnet-latest")
    {
        _model = string.IsNullOrWhiteSpace(model) ? "claude-3-5-sonnet-latest" : model.Trim();
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.anthropic.com")
        };
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", AnthropicVersion);
    }

    public async Task<string> CompleteAsync(
        string prompt,
        int maxTokens,
        float temperature,
        float topP,
        float repeatPenalty = 1.1f,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var parsed = PromptMessageParser.Parse(prompt);
            var systemPrompt = parsed.SystemPrompt;
            var messages = parsed.Messages
                .Select(m => new
                {
                    role = m.Role,
                    content = m.Content
                })
                .ToArray();

            var payload = new
            {
                model = _model,
                max_tokens = maxTokens,
                temperature,
                top_p = topP,
                system = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt,
                messages
            };

            var json = JsonSerializer.Serialize(payload);
            await LogAsync("PROMPT", prompt);

            var response = await _http.PostAsync(
                "/v1/messages",
                new StringContent(json, Encoding.UTF8, "application/json"),
                cancellationToken);

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                await LogErrorAsync(
                    "HTTP_ERROR",
                    $"Status: {(int)response.StatusCode} {response.StatusCode}\nResponse:\n{responseJson}");
                response.EnsureSuccessStatusCode();
            }

            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                return string.Empty;

            var parts = content.EnumerateArray()
                .Select(part =>
                {
                    if (!part.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "text")
                        return null;
                    return part.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
                })
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();

            var result = parts.Count == 0
                ? string.Empty
                : string.Join("\n", parts);

            await LogAsync("OUTPUT", result);
            return result;
        }
        catch (Exception ex)
        {
            await LogErrorAsync("EXCEPTION", ex.ToString());
            throw;
        }
    }

    private async Task LogAsync(string type, string content)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{DateTime.UtcNow:O}] === {type} ===");
        sb.AppendLine($"Model: {_model}");
        sb.AppendLine(content);
        sb.AppendLine();

        await File.AppendAllTextAsync(_logFile, sb.ToString());
    }

    private async Task LogErrorAsync(string type, string content)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{DateTime.UtcNow:O}] === {type} ===");
        sb.AppendLine($"Model: {_model}");
        sb.AppendLine(content);
        sb.AppendLine();

        await File.AppendAllTextAsync(_errorLogFile, sb.ToString());
    }
}

public interface ILLMProvider
{
    string ProviderId { get; }
    string DefaultModel { get; }
    ILLMClient CreateClient(string? model = null);
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default);
}

public sealed class OpenAIProvider : ILLMProvider
{
    private readonly string _apiKey;

    public OpenAIProvider(string apiKey, string defaultModel = "gpt-4o-mini")
    {
        _apiKey = apiKey;
        DefaultModel = string.IsNullOrWhiteSpace(defaultModel) ? "gpt-4o-mini" : defaultModel.Trim();
    }

    public string ProviderId => "openai";
    public string DefaultModel { get; }

    public ILLMClient CreateClient(string? model = null)
    {
        var normalized = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        return new OpenAIClient(_apiKey, normalized);
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        return LlmProviderModelDiscovery.FetchModelsAsync("https://api.openai.com", _apiKey, ct);
    }
}

public sealed class GroqProvider : ILLMProvider
{
    private readonly string _apiKey;

    public GroqProvider(string apiKey, string defaultModel = "llama-3.3-70b-versatile")
    {
        _apiKey = apiKey;
        DefaultModel = string.IsNullOrWhiteSpace(defaultModel)
            ? "llama-3.3-70b-versatile"
            : defaultModel.Trim();
    }

    public string ProviderId => "groq";
    public string DefaultModel { get; }

    public ILLMClient CreateClient(string? model = null)
    {
        var normalized = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        return new GroqClient(_apiKey, normalized);
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        return LlmProviderModelDiscovery.FetchModelsAsync("https://api.groq.com/openai", _apiKey, ct);
    }
}

public sealed class AnthropicProvider : ILLMProvider
{
    private const string AnthropicVersion = "2023-06-01";
    private readonly string _apiKey;

    public AnthropicProvider(string apiKey, string defaultModel = "claude-3-5-sonnet-latest")
    {
        _apiKey = apiKey;
        DefaultModel = string.IsNullOrWhiteSpace(defaultModel)
            ? "claude-3-5-sonnet-latest"
            : defaultModel.Trim();
    }

    public string ProviderId => "anthropic";
    public string DefaultModel { get; }

    public ILLMClient CreateClient(string? model = null)
    {
        var normalized = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        return new AnthropicClient(_apiKey, normalized);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            return await LlmProviderModelDiscovery.FetchAnthropicModelsAsync(_apiKey, AnthropicVersion, ct);
        }
        catch
        {
            // Fallback to default model when model listing is unavailable.
            return new[] { DefaultModel };
        }
    }
}

public sealed class LlamaCppProvider : ILLMProvider
{
    private readonly string _baseUrl;

    public LlamaCppProvider(string baseUrl = "http://localhost:8080", string defaultModel = "model")
    {
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? "http://localhost:8080"
            : baseUrl.Trim().TrimEnd('/');
        DefaultModel = string.IsNullOrWhiteSpace(defaultModel) ? "model" : defaultModel.Trim();
    }

    public string ProviderId => "llamacpp";
    public string DefaultModel { get; }

    public ILLMClient CreateClient(string? model = null)
    {
        var normalized = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        return new LlamaCppClient(_baseUrl, normalized);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var models = await LlmProviderModelDiscovery.FetchModelsAsync(_baseUrl, apiKey: null, ct);
            if (models.Count > 0)
                return models;
        }
        catch
        {
            // Fallback to default model when model listing is unavailable.
        }

        return new[] { DefaultModel };
    }
}

public static class LlmProviderModelDiscovery
{
    public static async Task<IReadOnlyList<string>> FetchModelsAsync(
        string baseAddress,
        string? apiKey,
        CancellationToken ct = default)
    {
        using var http = new HttpClient
        {
            BaseAddress = new Uri(baseAddress)
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await http.GetAsync("/v1/models", ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Model list request failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return data.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    public static async Task<IReadOnlyList<string>> FetchAnthropicModelsAsync(
        string apiKey,
        string anthropicVersion,
        CancellationToken ct = default)
    {
        using var http = new HttpClient
        {
            BaseAddress = new Uri("https://api.anthropic.com")
        };
        http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        http.DefaultRequestHeaders.Add("anthropic-version", anthropicVersion);

        using var response = await http.GetAsync("/v1/models", ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Anthropic model list request failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return data.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }
}

internal static class PromptMessageParser
{
    private const string Start = "<|start_header_id|>";
    private const string End = "<|end_header_id|>";
    private const string Eot = "<|eot_id|>";

    public static ParsedPrompt Parse(string prompt)
    {
        if (!prompt.Contains(Start, StringComparison.Ordinal))
        {
            return new ParsedPrompt(
                string.Empty,
                new[] { new ParsedPromptMessage("user", prompt) });
        }

        var systemBlocks = new List<string>();
        var messages = new List<ParsedPromptMessage>();
        int index = 0;

        while (true)
        {
            int startIdx = prompt.IndexOf(Start, index, StringComparison.Ordinal);
            if (startIdx < 0)
                break;

            int roleStart = startIdx + Start.Length;
            int endIdx = prompt.IndexOf(End, roleStart, StringComparison.Ordinal);
            if (endIdx < 0)
                break;

            string role = prompt.Substring(roleStart, endIdx - roleStart).Trim();
            int contentStart = endIdx + End.Length;

            int eotIdx = prompt.IndexOf(Eot, contentStart, StringComparison.Ordinal);
            int nextStartIdx = prompt.IndexOf(Start, contentStart, StringComparison.Ordinal);

            int contentEnd;
            if (eotIdx >= 0 && (nextStartIdx < 0 || eotIdx <= nextStartIdx))
                contentEnd = eotIdx;
            else if (nextStartIdx >= 0)
                contentEnd = nextStartIdx;
            else
                contentEnd = prompt.Length;

            string content = prompt.Substring(contentStart, contentEnd - contentStart).Trim();
            if (!string.IsNullOrWhiteSpace(content))
            {
                switch (role)
                {
                    case "system":
                        systemBlocks.Add(content);
                        break;
                    case "assistant":
                        messages.Add(new ParsedPromptMessage("assistant", content));
                        break;
                    default:
                        messages.Add(new ParsedPromptMessage("user", content));
                        break;
                }
            }

            index = contentEnd;
            if (eotIdx >= 0 && eotIdx == contentEnd)
                index += Eot.Length;
        }

        if (messages.Count == 0)
            messages.Add(new ParsedPromptMessage("user", prompt));

        return new ParsedPrompt(string.Join("\n\n", systemBlocks), messages);
    }

    internal sealed record ParsedPrompt(string SystemPrompt, IReadOnlyList<ParsedPromptMessage> Messages);
    internal sealed record ParsedPromptMessage(string Role, string Content);
}

public sealed class SwappableLlmClient : ILLMClient
{
    private readonly object _lock = new();
    private ILLMClient _inner;

    public SwappableLlmClient(ILLMClient inner)
    {
        _inner = inner;
    }

    public void SetInner(ILLMClient inner)
    {
        lock (_lock)
        {
            _inner = inner;
        }
    }

    public Task<string> CompleteAsync(
        string prompt,
        int maxTokens,
        float temperature,
        float topP,
        float repeatPenalty = 1.1f,
        CancellationToken cancellationToken = default)
    {
        ILLMClient snapshot;
        lock (_lock)
        {
            snapshot = _inner;
        }

        return snapshot.CompleteAsync(prompt, maxTokens, temperature, topP, repeatPenalty, cancellationToken);
    }
}
