using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Prayer.Contracts;
using Contracts = Prayer.Contracts;

public sealed class PrayerApiClient
{
    private readonly HttpClient _http;

    public PrayerApiClient(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("Prayer base URL is required.", nameof(baseUrl));

        _http = new HttpClient
        {
            BaseAddress = new Uri(EnsureTrailingSlash(baseUrl.Trim()))
        };
    }

    public async Task<string> CreateSessionAsync(string username, string password, string? label)
    {
        var response = await _http.PostAsJsonAsync(
            "api/runtime/sessions",
            new Contracts.CreateSessionRequest(username, password, label));
        await EnsureSuccessWithDetailsAsync(response);

        var session = await response.Content.ReadFromJsonAsync<Contracts.SessionSummary>();
        if (session == null || string.IsNullOrWhiteSpace(session.Id))
            throw new InvalidOperationException("Prayer did not return a valid session id.");

        return session.Id;
    }

    public async Task<Contracts.LlmCatalogResponse> GetLlmCatalogAsync()
    {
        var catalog = await _http.GetFromJsonAsync<Contracts.LlmCatalogResponse>("api/llm/catalog");
        if (catalog == null)
            throw new InvalidOperationException("Prayer did not return LLM catalog.");

        return catalog;
    }

    public async Task<(string SessionId, string Password)> RegisterSessionAsync(
        string username,
        string empire,
        string registrationCode,
        string? label)
    {
        var response = await _http.PostAsJsonAsync(
            "api/runtime/sessions/register",
            new Contracts.RegisterSessionRequest(username, empire, registrationCode, label));
        await EnsureSuccessWithDetailsAsync(response);

        var result = await response.Content.ReadFromJsonAsync<Contracts.RegisterSessionResponse>();
        if (result == null || string.IsNullOrWhiteSpace(result.SessionId) || string.IsNullOrWhiteSpace(result.Password))
            throw new InvalidOperationException("Prayer did not return a valid register session response.");

        return (result.SessionId, result.Password);
    }

    public async Task SendRuntimeCommandAsync(string sessionId, string command, string? argument = null)
    {
        var response = await _http.PostAsJsonAsync(
            $"api/runtime/sessions/{sessionId}/commands",
            new Contracts.RuntimeCommandRequest(command, argument));
        await EnsureSuccessWithDetailsAsync(response);
    }

    public async Task SetLoopEnabledAsync(string sessionId, bool enabled)
    {
        var response = await _http.PutAsJsonAsync(
            $"api/runtime/sessions/{sessionId}/loop",
            new Contracts.LoopUpdateRequest(enabled));
        await EnsureSuccessWithDetailsAsync(response);
    }

    public async Task<AppPrayerRuntimeState> GetRuntimeStateAsync(string sessionId)
    {
        var snapshot = await _http.GetFromJsonAsync<Contracts.RuntimeStateResponse>(
            $"api/runtime/sessions/{sessionId}/state");
        if (snapshot == null)
            throw new InvalidOperationException("Prayer did not return a runtime state snapshot.");

        GameState? state = null;
        if (snapshot.State.HasValue)
        {
            var stateElement = snapshot.State.Value;
            if (stateElement.ValueKind != JsonValueKind.Null &&
                stateElement.ValueKind != JsonValueKind.Undefined)
            {
                state = JsonSerializer.Deserialize<GameState>(stateElement.GetRawText());
            }
        }

        return new AppPrayerRuntimeState(
            state,
            snapshot.Memory,
            snapshot.ExecutionStatusLines,
            snapshot.ControlInput,
            snapshot.CurrentScriptLine,
            snapshot.LastGenerationPrompt,
            snapshot.LoopEnabled);
    }

    public async Task DeleteSessionAsync(string sessionId)
    {
        var response = await _http.DeleteAsync($"api/runtime/sessions/{sessionId}");
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            await EnsureSuccessWithDetailsAsync(response);
    }

    public async Task SetSessionLlmAsync(string sessionId, string provider, string model)
    {
        var response = await _http.PutAsJsonAsync(
            $"api/runtime/sessions/{sessionId}/llm",
            new Contracts.UpdateSessionLlmRequest(provider, model));
        await EnsureSuccessWithDetailsAsync(response);
    }

    public async Task<IReadOnlyList<Contracts.BotProfile>> GetSavedBotsAsync()
    {
        var response = await _http.GetFromJsonAsync<Contracts.BotProfilesResponse>("api/preferences/bots");
        return response?.Bots ?? Array.Empty<Contracts.BotProfile>();
    }

    public async Task UpsertSavedBotAsync(string username, string password)
    {
        var response = await _http.PutAsJsonAsync(
            "api/preferences/bots",
            new Contracts.UpsertBotProfileRequest(username, password));
        await EnsureSuccessWithDetailsAsync(response);
    }

    public async Task<Contracts.DefaultLlmPreferenceResponse?> GetDefaultLlmPreferenceAsync()
    {
        return await _http.GetFromJsonAsync<Contracts.DefaultLlmPreferenceResponse>("api/preferences/llm");
    }

    public async Task SetDefaultLlmPreferenceAsync(string provider, string model)
    {
        var response = await _http.PutAsJsonAsync(
            "api/preferences/llm",
            new Contracts.UpdateDefaultLlmPreferenceRequest(provider, model));
        await EnsureSuccessWithDetailsAsync(response);
    }

    private static string EnsureTrailingSlash(string url)
    {
        return url.EndsWith("/", StringComparison.Ordinal) ? url : url + "/";
    }

    private static async Task EnsureSuccessWithDetailsAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        string body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync();
        }
        catch
        {
            // Best effort.
        }

        var reason = string.IsNullOrWhiteSpace(body)
            ? $"{(int)response.StatusCode} {response.ReasonPhrase}"
            : $"{(int)response.StatusCode} {response.ReasonPhrase}: {body}";
        throw new HttpRequestException(
            $"Prayer request failed: {reason}",
            null,
            response.StatusCode);
    }
}

public sealed record AppPrayerRuntimeState(
    GameState? State,
    IReadOnlyList<string> Memory,
    IReadOnlyList<string> ExecutionStatusLines,
    string? ControlInput,
    int? CurrentScriptLine,
    string? LastGenerationPrompt,
    bool LoopEnabled);
