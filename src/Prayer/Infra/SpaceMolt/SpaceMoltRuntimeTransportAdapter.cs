using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public sealed class SpaceMoltRuntimeTransportAdapter : IRuntimeTransport
{
    private readonly SpaceMoltHttpClient _client;
    private RouteInfo? _activeRoute;

    public SpaceMoltRuntimeTransportAdapter(SpaceMoltHttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public void SetActiveRoute(RouteInfo? route) => _activeRoute = route;
    public RouteInfo? GetActiveRoute() => _activeRoute;

    public int ShipCatalogPage => _client.ShipCatalogPage;

    public async Task<RuntimeCommandResult> ExecuteCommandAsync(
        string command,
        object? payload = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client.ExecuteAsync(command, payload, cancellationToken);
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

    public Task<IReadOnlyDictionary<string, ItemCatalogueEntry>> GetFullItemCatalogByIdAsync(
        bool forceRefresh = false)
    {
        return MapRateLimitAsync(() => _client.GetFullItemCatalogByIdAsync(forceRefresh));
    }

    public Task<IReadOnlyDictionary<string, ShipCatalogueEntry>> GetFullShipCatalogByIdAsync(
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

    public RouteInfo? FindPath(GameState state, string targetSystem)
    {
        if (string.IsNullOrWhiteSpace(state.System) || string.IsNullOrWhiteSpace(targetSystem))
            return null;

        if (string.Equals(state.System, targetSystem, StringComparison.Ordinal))
            return new RouteInfo(targetSystem, Array.Empty<string>(), 0, null);

        var adjacency = BuildAdjacency(state);
        if (!adjacency.ContainsKey(targetSystem))
            return null;

        var coords = BuildCoordinateLookup(state);

        var gScore = new Dictionary<string, int>(StringComparer.Ordinal) { [state.System] = 0 };
        var cameFrom = new Dictionary<string, string>(StringComparer.Ordinal);
        var open = new PriorityQueue<string, double>();
        var inOpen = new HashSet<string>(StringComparer.Ordinal) { state.System };

        open.Enqueue(state.System, Heuristic(state.System, targetSystem, coords));

        while (open.Count > 0)
        {
            string current = open.Dequeue();
            inOpen.Remove(current);

            if (string.Equals(current, targetSystem, StringComparison.Ordinal))
            {
                var hops = ReconstructPath(cameFrom, current);
                return new RouteInfo(targetSystem, hops, 0, ComputeArrivalTime(state.Ship.Speed, hops.Count));
            }

            if (!adjacency.TryGetValue(current, out var neighbors))
                continue;

            int tentativeG = gScore[current] + 1;

            foreach (var neighbor in neighbors)
            {
                if (string.IsNullOrWhiteSpace(neighbor))
                    continue;

                if (gScore.TryGetValue(neighbor, out int existing) && existing <= tentativeG)
                    continue;

                cameFrom[neighbor] = current;
                gScore[neighbor] = tentativeG;

                if (!inOpen.Contains(neighbor))
                {
                    open.Enqueue(neighbor, tentativeG + Heuristic(neighbor, targetSystem, coords));
                    inOpen.Add(neighbor);
                }
            }
        }

        return null;
    }

    private static DateTimeOffset? ComputeArrivalTime(int shipSpeed, int hopCount)
    {
        if (hopCount <= 0 || shipSpeed <= 0)
            return null;
        var secsPerJump = (7 - shipSpeed) * 10;
        if (secsPerJump <= 0)
            return null;
        return DateTimeOffset.UtcNow.AddSeconds(secsPerJump * hopCount);
    }

    private static double Heuristic(
        string system,
        string target,
        Dictionary<string, (double x, double y)> coords)
    {
        if (!coords.TryGetValue(system, out var a) || !coords.TryGetValue(target, out var b))
            return 0;

        double dx = a.x - b.x;
        double dy = a.y - b.y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static List<string> ReconstructPath(Dictionary<string, string> cameFrom, string current)
    {
        var path = new List<string>();
        while (cameFrom.TryGetValue(current, out var prev))
        {
            path.Add(current);
            current = prev;
        }
        path.Reverse();
        return path;
    }

    private static Dictionary<string, (double x, double y)> BuildCoordinateLookup(GameState state)
    {
        var coords = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
        foreach (var system in state.Galaxy?.Map?.Systems ?? new List<GalaxySystemInfo>())
        {
            if (!string.IsNullOrWhiteSpace(system?.Id) && system.X.HasValue && system.Y.HasValue)
                coords[system.Id] = (system.X.Value, system.Y.Value);
        }
        return coords;
    }

    private static Dictionary<string, List<string>> BuildAdjacency(GameState state)
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        void AddEdge(string from, string to)
        {
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                return;

            if (!adjacency.TryGetValue(from, out var neighbors))
            {
                neighbors = new List<string>();
                adjacency[from] = neighbors;
            }

            if (!neighbors.Contains(to, StringComparer.Ordinal))
                neighbors.Add(to);
        }

        if (!string.IsNullOrWhiteSpace(state.System))
            adjacency.TryAdd(state.System, new List<string>());

        foreach (var connected in state.Systems ?? Array.Empty<string>())
        {
            AddEdge(state.System, connected);
            AddEdge(connected, state.System);
        }

        foreach (var system in state.Galaxy?.Map?.Systems ?? new List<GalaxySystemInfo>())
        {
            if (string.IsNullOrWhiteSpace(system?.Id))
                continue;

            adjacency.TryAdd(system.Id, new List<string>());
            foreach (var connected in system.Connections ?? new List<string>())
            {
                AddEdge(system.Id, connected);
                AddEdge(connected, system.Id);
            }
        }

        return adjacency;
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
