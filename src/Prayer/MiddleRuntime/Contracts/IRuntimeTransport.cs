using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public interface IRuntimeTransport
{
    Task<RuntimeCommandResult> ExecuteCommandAsync(
        string command,
        object? payload = null,
        CancellationToken cancellationToken = default);

    RouteInfo? FindPath(GameState state, string targetSystem);

    void SetActiveRoute(RouteInfo? route);
    RouteInfo? GetActiveRoute();

    Task<Catalogue> GetCatalogueAsync(
        string type,
        string? category = null,
        string? id = null,
        int? page = null,
        int? pageSize = null,
        string? search = null);

    Task<GalaxyMapSnapshot> GetMapSnapshotAsync(bool forceRefresh = false);

    Task<IReadOnlyDictionary<string, ItemCatalogueEntry>> GetFullItemCatalogByIdAsync(
        bool forceRefresh = false);

    Task<IReadOnlyDictionary<string, ShipCatalogueEntry>> GetFullShipCatalogByIdAsync(
        bool forceRefresh = false);

    GameState GetLatestState();

    int ShipCatalogPage { get; }

    void ResetShipCatalogPage();

    bool MoveShipCatalogToNextPage(int? totalPages);

    bool MoveShipCatalogToLastPage();
}
