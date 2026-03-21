using System.Text.Json;

public static class SpaceMoltHttpLogging
{
    public static Task LogBadRequestAsync(string command, object? payload, string rawResponse)
    {
        string payloadText;

        try
        {
            payloadText = payload == null ? "(null)" : JsonSerializer.Serialize(payload);
        }
        catch (Exception ex)
        {
            payloadText = $"(payload serialization failed: {ex.Message})";
        }

        var entry =
            $"[{DateTime.UtcNow:O}] HTTP 400 BadRequest\n" +
            $"Command: {command}\n" +
            $"Payload: {payloadText}\n" +
            $"Response: {rawResponse}\n\n";

        LogSink.Instance.Enqueue(new LogEvent(DateTime.UtcNow, LogKind.HttpBadRequest, entry, AppPaths.HttpBadRequestLogFile));
        return Task.CompletedTask;
    }

    public static Task LogPathfindAsync(string targetSystem, JsonElement routeResult)
    {
        var entry =
            $"[{DateTime.UtcNow:O}] PATHFIND\n" +
            $"TargetSystem: {targetSystem}\n" +
            $"Result: {routeResult.GetRawText()}\n\n";

        LogSink.Instance.Enqueue(new LogEvent(DateTime.UtcNow, LogKind.Pathfind, entry, AppPaths.PathfindLogFile));
        return Task.CompletedTask;
    }

    public static Task LogApiResponseAsync(
        string command,
        object? payload,
        int statusCode,
        string? reasonPhrase,
        string rawResponse,
        long requestId,
        long elapsedMs,
        string? context = null)
    {
        string payloadText;
        try
        {
            payloadText = payload == null ? "(null)" : JsonSerializer.Serialize(payload);
        }
        catch (Exception ex)
        {
            payloadText = $"(payload serialization failed: {ex.Message})";
        }

        var entry =
            $"[{DateTime.UtcNow:O}] SPACEMOLT_API\n" +
            $"Context: {(string.IsNullOrWhiteSpace(context) ? "(none)" : context)}\n" +
            $"RequestId: {requestId}\n" +
            $"Command: {command}\n" +
            $"Payload: {payloadText}\n" +
            $"ElapsedMs: {elapsedMs}\n" +
            $"HTTP: {statusCode} {reasonPhrase}\n" +
            $"Response: {rawResponse}\n\n";

        LogSink.Instance.Enqueue(new LogEvent(DateTime.UtcNow, LogKind.SpaceMoltApi, entry, AppPaths.SpaceMoltApiLogFile));
        return Task.CompletedTask;
    }

    public static Task LogAnalyzeMarketAsync(string stationId, JsonElement analyzeMarketResult)
    {
        var entry =
            $"[{DateTime.UtcNow:O}] ANALYZE_MARKET\n" +
            $"Station: {stationId}\n" +
            $"Result: {analyzeMarketResult.GetRawText()}\n\n";

        LogSink.Instance.Enqueue(new LogEvent(DateTime.UtcNow, LogKind.AnalyzeMarket, entry, AppPaths.AnalyzeMarketLogFile));
        return Task.CompletedTask;
    }

    public static Task LogItemCatalogAsync(
        string type,
        string? category,
        string? id,
        int? page,
        int? pageSize,
        string? search,
        string source,
        string rawPayload)
    {
        var entry =
            $"[{DateTime.UtcNow:O}] ITEM_CATALOG\n" +
            $"Source: {source}\n" +
            $"Params: type={type}, category={category ?? "(null)"}, id={id ?? "(null)"}, page={(page?.ToString() ?? "(null)")}, page_size={(pageSize?.ToString() ?? "(null)")}, search={search ?? "(null)"}\n" +
            $"Payload: {rawPayload}\n\n";

        LogSink.Instance.Enqueue(new LogEvent(DateTime.UtcNow, LogKind.ItemCatalog, entry, AppPaths.ItemCatalogLogFile));
        return Task.CompletedTask;
    }

    public static Task LogApiCommandStatsAsync(string context, string summary)
    {
        var entry =
            $"[{DateTime.UtcNow:O}] SPACEMOLT_API_STATS\n" +
            $"Context: {context}\n" +
            $"{summary}\n\n";

        LogSink.Instance.Enqueue(new LogEvent(DateTime.UtcNow, LogKind.SpaceMoltApiStats, entry, AppPaths.SpaceMoltApiStatsLogFile));
        return Task.CompletedTask;
    }
}
