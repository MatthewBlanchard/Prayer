using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

internal sealed class SpaceMoltApiTransport
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public SpaceMoltApiTransport(HttpClient http, string baseUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
    }

    public async Task<JsonElement> ExecuteCommandAsync(
        string sessionId,
        string command,
        object? payload,
        bool debugEnabled,
        string? debugContext,
        long requestId,
        Action<JsonElement>? observePayload)
    {
        var timer = Stopwatch.StartNew();

        var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + command);
        request.Headers.Add("X-Session-Id", sessionId);

        if (payload != null)
            request.Content = JsonContent.Create(payload);

        var response = await _http.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();

        if (debugEnabled)
        {
            await SpaceMoltHttpLogging.LogApiResponseAsync(
                command,
                payload,
                (int)response.StatusCode,
                response.ReasonPhrase,
                raw,
                requestId,
                timer.ElapsedMilliseconds,
                debugContext);
        }

        JsonElement content;
        try
        {
            content = JsonSerializer.Deserialize<JsonElement>(raw);
        }
        catch
        {
            return CreateMessage("Invalid JSON response");
        }

        observePayload?.Invoke(content);

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw BuildRateLimitException(command, response, content);

            if (response.StatusCode == HttpStatusCode.BadRequest)
                await SpaceMoltHttpLogging.LogBadRequestAsync(command, payload, raw);

            return content;
        }

        if (TryExtractApiError(content, out var code, out var message, out var retryAfterSeconds))
        {
            if (string.Equals(code, "rate_limited", StringComparison.OrdinalIgnoreCase))
            {
                throw new RateLimitStopException(
                    $"SpaceMolt API `{command}` rate limited: {message ?? "Too many requests."}",
                    retryAfterSeconds);
            }

            string details = string.IsNullOrWhiteSpace(code)
                ? (message ?? "Unknown API error")
                : $"{code}: {message ?? "Unknown API error"}";
            if (retryAfterSeconds.HasValue)
                details += $" (retry_after={retryAfterSeconds.Value}s)";
            return CreateMessage(details);
        }

        if (content.TryGetProperty("result", out var result))
            return result;

        return CreateMessage("No result returned");
    }

    public static void EnsureResponseSuccessful(
        HttpResponseMessage response,
        string raw,
        string commandContext)
    {
        JsonElement content = default;
        bool hasJson = false;

        try
        {
            content = JsonSerializer.Deserialize<JsonElement>(raw);
            hasJson = content.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            // Some failures may not be JSON.
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw BuildRateLimitException(
                commandContext,
                response,
                hasJson ? content : (JsonElement?)null);
        }

        if (hasJson &&
            TryExtractApiError(content, out var code, out var message, out var retryAfterSeconds))
        {
            if (string.Equals(code, "rate_limited", StringComparison.OrdinalIgnoreCase))
            {
                throw new RateLimitStopException(
                    $"SpaceMolt API `{commandContext}` rate limited: {message ?? "Too many requests."}",
                    retryAfterSeconds);
            }

            throw new SpaceMoltApiException($"API Error: {code ?? "unknown"} - {message ?? "Unknown API error"}");
        }

        response.EnsureSuccessStatusCode();
    }

    public static void EnsureCommandSucceeded(string command, JsonElement payload)
    {
        if (TryExtractApiError(payload, out var code, out var message, out var retryAfterSeconds))
        {
            string details = string.IsNullOrWhiteSpace(code)
                ? (message ?? "Unknown API error")
                : $"{code}: {message ?? "Unknown API error"}";
            if (retryAfterSeconds.HasValue)
                details += $" (retry_after={retryAfterSeconds.Value}s)";
            throw new InvalidOperationException($"SpaceMolt API `{command}` failed: {details}");
        }
    }

    public static JsonElement RequireObjectProperty(JsonElement obj, string property, string command)
    {
        if (obj.ValueKind != JsonValueKind.Object ||
            !obj.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Unexpected `{command}` payload: missing object `{property}`. Payload: {SummarizeJson(obj)}");
        }

        return value;
    }

    public static JsonElement RequireArrayProperty(JsonElement obj, string property, string command)
    {
        if (obj.ValueKind != JsonValueKind.Object ||
            !obj.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Unexpected `{command}` payload: missing array `{property}`. Payload: {SummarizeJson(obj)}");
        }

        return value;
    }

    public static string RequireStringProperty(JsonElement obj, string property, string command)
    {
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? "";
        }

        throw new InvalidOperationException(
            $"Unexpected `{command}` payload: missing string `{property}`. Payload: {SummarizeJson(obj)}");
    }

    public static bool TryExtractApiError(
        JsonElement content,
        out string? code,
        out string? message,
        out int? retryAfterSeconds)
    {
        code = null;
        message = null;
        retryAfterSeconds = null;

        if (content.ValueKind != JsonValueKind.Object ||
            !content.TryGetProperty("error", out var error) ||
            error.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (error.ValueKind == JsonValueKind.Object)
        {
            if (error.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String)
                code = codeEl.GetString();
            if (error.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
                message = msgEl.GetString();
        }
        else if (error.ValueKind == JsonValueKind.String)
        {
            code = error.GetString();
        }

        if (content.TryGetProperty("message", out var topMsgEl) && topMsgEl.ValueKind == JsonValueKind.String)
            message ??= topMsgEl.GetString();

        if (content.TryGetProperty("retry_after", out var retryEl) &&
            retryEl.ValueKind == JsonValueKind.Number &&
            retryEl.TryGetInt32(out var retry))
        {
            retryAfterSeconds = retry;
        }

        message ??= "Unknown API error";
        return true;
    }

    public static RateLimitStopException BuildRateLimitException(
        string command,
        HttpResponseMessage response,
        JsonElement? payload)
    {
        int? retryAfterSeconds = null;
        string message = "Too many requests.";

        if (payload.HasValue &&
            TryExtractApiError(payload.Value, out _, out var extractedMessage, out var extractedRetryAfter))
        {
            message = extractedMessage ?? message;
            retryAfterSeconds = extractedRetryAfter;
        }

        if (!retryAfterSeconds.HasValue &&
            response.Headers.RetryAfter?.Delta is TimeSpan retryDelta)
        {
            retryAfterSeconds = (int)Math.Ceiling(retryDelta.TotalSeconds);
        }

        string details = $"SpaceMolt API `{command}` returned HTTP 429: {message}";
        if (retryAfterSeconds.HasValue)
            details += $" (retry_after={retryAfterSeconds.Value}s)";

        return new RateLimitStopException(details, retryAfterSeconds);
    }

    private static JsonElement CreateMessage(string? message)
    {
        var json = JsonSerializer.Serialize(new { message });
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private static string SummarizeJson(JsonElement payload)
    {
        string raw = payload.GetRawText();
        const int maxLength = 240;
        if (raw.Length <= maxLength)
            return raw;
        return raw.Substring(0, maxLength) + "...";
    }
}
