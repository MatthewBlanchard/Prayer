using System;
using System.Collections.Generic;
using System.Threading.Tasks;

public sealed class SpaceMoltRuntimeTransportAdapter : IRuntimeTransport
{
    private readonly SpaceMoltHttpClient _client;

    public SpaceMoltRuntimeTransportAdapter(SpaceMoltHttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public int ShipCatalogPage => _client.ShipCatalogPage;

    public async Task<RuntimeCommandResult> ExecuteCommandAsync(string command, object? payload = null)
    {
        try
        {
            var response = await _client.ExecuteAsync(command, payload);
            return ToRuntimeCommandResult(response);
        }
        catch (RateLimitStopException ex)
        {
            throw new RuntimeRateLimitException(ex.Message, ex.RetryAfterSeconds);
        }
    }

    public async Task<RuntimeCommandResult> FindRouteAsync(string targetSystem)
    {
        try
        {
            var response = await _client.FindRouteAsync(targetSystem);
            return ToRuntimeCommandResult(response);
        }
        catch (RateLimitStopException ex)
        {
            throw new RuntimeRateLimitException(ex.Message, ex.RetryAfterSeconds);
        }
    }

    public async Task<Catalogue> GetCatalogueAsync(
        string type,
        string? category = null,
        string? id = null,
        int? page = null,
        int? pageSize = null,
        string? search = null)
    {
        try
        {
            return await _client.GetCatalogueAsync(type, category, id, page, pageSize, search);
        }
        catch (RateLimitStopException ex)
        {
            throw new RuntimeRateLimitException(ex.Message, ex.RetryAfterSeconds);
        }
    }

    public Task<GalaxyMapSnapshot> GetMapSnapshotAsync(bool forceRefresh = false)
    {
        return MapRateLimitAsync(() => _client.GetMapSnapshotAsync(forceRefresh));
    }

    public Task<IReadOnlyDictionary<string, CatalogueEntry>> GetFullItemCatalogByIdAsync(
        bool forceRefresh = false)
    {
        return MapRateLimitAsync(() => _client.GetFullItemCatalogByIdAsync(forceRefresh));
    }

    public Task<IReadOnlyDictionary<string, CatalogueEntry>> GetFullShipCatalogByIdAsync(
        bool forceRefresh = false)
    {
        return MapRateLimitAsync(() => _client.GetFullShipCatalogByIdAsync(forceRefresh));
    }

    public GameState GetLatestState()
    {
        return _client.GetGameState();
    }

    public void ResetShipCatalogPage()
    {
        _client.ResetShipCatalogPage();
    }

    public bool MoveShipCatalogToNextPage(int? totalPages)
    {
        return _client.MoveShipCatalogToNextPage(totalPages);
    }

    public bool MoveShipCatalogToLastPage()
    {
        return _client.MoveShipCatalogToLastPage();
    }

    private static RuntimeCommandResult ToRuntimeCommandResult(System.Text.Json.JsonElement payload)
    {
        bool failed = SpaceMoltApiTransport.TryExtractApiError(payload, out _, out var message, out _);
        return new RuntimeCommandResult(
            Succeeded: !failed,
            Payload: payload,
            ErrorMessage: failed ? message : null);
    }

    private static async Task<T> MapRateLimitAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (RateLimitStopException ex)
        {
            throw new RuntimeRateLimitException(ex.Message, ex.RetryAfterSeconds);
        }
    }
}
