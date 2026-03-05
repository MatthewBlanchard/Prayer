using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

internal sealed class SpaceMoltSessionService
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public SpaceMoltSessionService(HttpClient http, string baseUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
    }

    public async Task<SpaceMoltSessionInfo> CreateSessionAsync()
    {
        var response = await _http.PostAsync(_baseUrl + "session", null);
        var raw = await response.Content.ReadAsStringAsync();

        SpaceMoltApiTransport.EnsureResponseSuccessful(response, raw, "session/auth");

        var json = JsonSerializer.Deserialize<JsonElement>(raw);
        if (!json.TryGetProperty("session", out var sessionJson) || sessionJson.ValueKind != JsonValueKind.Object)
            throw new SpaceMoltApiException("Failed to create session.");

        var sessionId = sessionJson.GetProperty("id").GetString();

        if (string.IsNullOrWhiteSpace(sessionId))
            throw new SpaceMoltApiException("Failed to create session.");

        return new SpaceMoltSessionInfo
        {
            Id = sessionId,
            CreatedAt = TryParseDateTimeOffset(sessionJson, "created_at"),
            ExpiresAt = TryParseDateTimeOffset(sessionJson, "expires_at")
        };
    }

    public async Task LoginAsync(string sessionId, string username, string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "login");
        request.Headers.Add("X-Session-Id", sessionId);
        request.Content = JsonContent.Create(new { username, password });

        var response = await _http.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();

        SpaceMoltApiTransport.EnsureResponseSuccessful(response, raw, "session/auth");
    }

    public async Task<string> RegisterAsync(
        string sessionId,
        string username,
        string empire,
        string registrationCode)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "register");
        request.Headers.Add("X-Session-Id", sessionId);
        request.Content = JsonContent.Create(new
        {
            username,
            empire,
            registration_code = registrationCode
        });

        var response = await _http.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        var content = JsonSerializer.Deserialize<JsonElement>(raw);

        SpaceMoltApiTransport.EnsureResponseSuccessful(response, raw, "session/auth");

        if (content.TryGetProperty("result", out var result) &&
            result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("password", out var passwordElement) &&
            passwordElement.ValueKind == JsonValueKind.String)
        {
            var password = passwordElement.GetString();
            if (!string.IsNullOrWhiteSpace(password))
                return password;
        }

        throw new SpaceMoltApiException("Register succeeded but no password was returned by the API.");
    }

    private static DateTimeOffset? TryParseDateTimeOffset(JsonElement obj, string property)
    {
        if (obj.ValueKind != JsonValueKind.Object ||
            !obj.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var raw = value.GetString();
        return DateTimeOffset.TryParse(raw, out var parsed)
            ? parsed
            : null;
    }
}
