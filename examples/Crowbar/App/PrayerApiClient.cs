using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
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
            BaseAddress = new Uri(EnsureTrailingSlash(baseUrl.Trim())),
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
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

    public async Task WaitForHaltAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync(
            $"api/runtime/sessions/{sessionId}/halt/wait",
            cancellationToken);
        await EnsureSuccessWithDetailsAsync(response);
    }

    public async Task RunScriptAsync(string sessionId, string script, CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync(
            $"api/runtime/sessions/{sessionId}/script/run",
            new Contracts.RunScriptRequest(script),
            cancellationToken);
        await EnsureSuccessWithDetailsAsync(response);
    }

    public async Task<string> GenerateAsync(
        string sessionId,
        string prompt,
        int maxTokens = 512,
        float temperature = 0.7f,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync(
            $"api/runtime/sessions/{sessionId}/generate",
            new Contracts.GenerateRequest(prompt, maxTokens, temperature),
            cancellationToken);
        await EnsureSuccessWithDetailsAsync(response);

        var payload = await response.Content.ReadFromJsonAsync<Contracts.GenerateResponse>(
            cancellationToken: cancellationToken);
        return payload?.Text ?? string.Empty;
    }

    public async Task<string> GenerateScriptAsync(string sessionId, string prompt)
    {
        var response = await _http.PostAsJsonAsync(
            $"api/runtime/sessions/{sessionId}/script/generate",
            new Contracts.GenerateScriptRequest(prompt));
        await EnsureSuccessWithDetailsAsync(response);

        var payload = await response.Content.ReadFromJsonAsync<Contracts.GenerateScriptResponse>();
        if (payload == null || string.IsNullOrWhiteSpace(payload.Script))
            throw new InvalidOperationException("Prayer did not return generated script text.");

        return payload.Script;
    }

    public async Task<AppPrayerRuntimeState> GetRuntimeStateAsync(string sessionId)
    {
        var result = await GetRuntimeStateLongPollAsync(
            sessionId,
            sinceVersion: 0,
            waitMs: 0,
            CancellationToken.None);
        if (!result.Changed || result.State == null)
            throw new InvalidOperationException("Prayer did not return a runtime state snapshot.");

        return result.State;
    }

    public async Task<AppPrayerRuntimeStatePollResult> GetRuntimeStateLongPollAsync(
        string sessionId,
        long sinceVersion,
        int waitMs,
        CancellationToken cancellationToken)
    {
        var response = await _http.GetAsync(
            $"api/runtime/sessions/{sessionId}/state?since={sinceVersion.ToString(CultureInfo.InvariantCulture)}&wait_ms={waitMs.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return new AppPrayerRuntimeStatePollResult(null, sinceVersion, false);

        await EnsureSuccessWithDetailsAsync(response);

        long stateVersion = sinceVersion;
        if (response.Headers.TryGetValues("X-Prayer-State-Version", out var values))
        {
            var raw = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(raw))
                _ = long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out stateVersion);
        }

        var snapshot = await response.Content.ReadFromJsonAsync<Contracts.RuntimeStateResponse>(cancellationToken: cancellationToken);
        if (snapshot == null)
            throw new InvalidOperationException("Prayer did not return a runtime state snapshot.");

        return new AppPrayerRuntimeStatePollResult(DeserializeState(snapshot), stateVersion, true);
    }

    public async Task<Contracts.RuntimeSnapshotResponse> GetRuntimeSnapshotAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync(
            $"api/runtime/sessions/{sessionId}/snapshot",
            cancellationToken);
        await EnsureSuccessWithDetailsAsync(response);

        var snapshot = await response.Content.ReadFromJsonAsync<Contracts.RuntimeSnapshotResponse>(
            cancellationToken: cancellationToken);
        if (snapshot == null)
            throw new InvalidOperationException("Prayer did not return a runtime host snapshot.");

        return snapshot;
    }

    public async Task<Contracts.SpaceMoltPassthroughResponse> ExecuteSpaceMoltPassthroughAsync(
        string sessionId,
        string command,
        JsonElement? payload = null,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync(
            $"api/runtime/sessions/{sessionId}/spacemolt/passthrough",
            new Contracts.SpaceMoltPassthroughRequest(command, payload),
            cancellationToken);
        await EnsureSuccessWithDetailsAsync(response);

        var result = await response.Content.ReadFromJsonAsync<Contracts.SpaceMoltPassthroughResponse>(
            cancellationToken: cancellationToken);
        if (result == null)
            throw new InvalidOperationException("Prayer did not return a passthrough response.");

        return result;
    }

    public async Task<Contracts.ActiveGoRouteDto?> GetActiveRouteAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync(
            $"api/runtime/sessions/{sessionId}/route",
            cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return null;

        await EnsureSuccessWithDetailsAsync(response);
        return await response.Content.ReadFromJsonAsync<Contracts.ActiveGoRouteDto>(
            cancellationToken: cancellationToken);
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

    public async Task<Contracts.SkillLibraryResponse> GetSkillLibraryAsync(string sessionId)
    {
        var result = await _http.GetFromJsonAsync<Contracts.SkillLibraryResponse>(
            $"api/runtime/sessions/{sessionId}/skills");
        return result ?? new Contracts.SkillLibraryResponse(
            Array.Empty<Contracts.SkillEntryDto>(),
            Array.Empty<Contracts.OverrideEntryDto>(),
            string.Empty);
    }

    public async Task<Contracts.SkillLibraryResponse> AppendSkillBlockAsync(string sessionId, string blockText)
    {
        var response = await _http.PostAsJsonAsync(
            $"api/runtime/sessions/{sessionId}/skills/append",
            new Contracts.AppendSkillBlockRequest(blockText));
        await EnsureSuccessWithDetailsAsync(response);
        var result = await response.Content.ReadFromJsonAsync<Contracts.SkillLibraryResponse>();
        return result!;
    }

    public async Task<Contracts.SkillLibraryResponse> ToggleOverrideAsync(string sessionId, string name)
    {
        var response = await _http.PostAsJsonAsync(
            $"api/runtime/sessions/{sessionId}/skills/toggle-override",
            new Contracts.ToggleOverrideRequest(name));
        await EnsureSuccessWithDetailsAsync(response);
        return (await response.Content.ReadFromJsonAsync<Contracts.SkillLibraryResponse>())!;
    }

    public async Task<Contracts.SkillLibraryResponse> DeleteSkillItemAsync(string sessionId, string kind, string name)
    {
        var response = await _http.PostAsJsonAsync(
            $"api/runtime/sessions/{sessionId}/skills/delete",
            new Contracts.DeleteSkillItemRequest(kind, name));
        await EnsureSuccessWithDetailsAsync(response);
        return (await response.Content.ReadFromJsonAsync<Contracts.SkillLibraryResponse>())!;
    }

    public async Task<Contracts.SkillLibraryResponse> ReorderOverrideAsync(string sessionId, string name, string direction)
    {
        var response = await _http.PostAsJsonAsync(
            $"api/runtime/sessions/{sessionId}/skills/reorder-override",
            new Contracts.ReorderOverrideRequest(name, direction));
        await EnsureSuccessWithDetailsAsync(response);
        return (await response.Content.ReadFromJsonAsync<Contracts.SkillLibraryResponse>())!;
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

    private static AppPrayerRuntimeState DeserializeState(Contracts.RuntimeStateResponse snapshot)
    {
        GameState? state = null;
        if (snapshot.State != null)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(snapshot.State);
            state = System.Text.Json.JsonSerializer.Deserialize<GameState>(json);
        }

        return new AppPrayerRuntimeState(
            state,
            snapshot.Memory,
            snapshot.ExecutionStatusLines,
            snapshot.ControlInput,
            snapshot.CurrentScriptLine,
            snapshot.ScriptRunning,
            snapshot.LastGenerationPrompt,
            snapshot.CurrentTick,
            snapshot.LastSpaceMoltPostUtc,
            snapshot.ActiveRoute);
    }
}

public sealed record AppPrayerRuntimeState(
    GameState? State,
    IReadOnlyList<string> Memory,
    IReadOnlyList<string> ExecutionStatusLines,
    string? ControlInput,
    int? CurrentScriptLine,
    bool ScriptRunning,
    string? LastGenerationPrompt,
    int? CurrentTick,
    DateTime? LastSpaceMoltPostUtc,
    Contracts.ActiveGoRouteDto? ActiveRoute = null);

public sealed record AppPrayerRuntimeStatePollResult(
    AppPrayerRuntimeState? State,
    long StateVersion,
    bool Changed);
