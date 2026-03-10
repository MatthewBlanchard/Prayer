using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public class ExploreCommand : IMultiTurnCommand, IActiveRouteSource
{
    public string Name => "explore";
    public DslCommandSyntax GetDslSyntax() => new();

    private ExplorationStateSnapshot _snapshot = new();
    private string? _targetSystemId;
    private bool _completed;
    private string? _completionMessage;
    private bool _haltOnFinish;

    private List<string>? _plannedHops;
    private int _plannedTotalJumps;
    private int _fuelPerJump;
    private int _estimatedFuel;
    private int _fuelAvailable;

    public bool IsAvailable(GameState state)
        => !string.IsNullOrWhiteSpace(state.System);

    public string BuildHelp(GameState state)
        => "- explore → go to nearest unexplored system and gather system-level intel";

    public async Task<(bool finished, CommandExecutionResult? result)> StartAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        ResetRuntimeState();
        _snapshot = LoadSnapshot();

        if (!string.IsNullOrWhiteSpace(state.CurrentPOI?.Id))
            MarkPoiExplored(state.CurrentPOI.Id);

        _targetSystemId = SelectNearestUnexploredSystem(state);
        if (string.IsNullOrWhiteSpace(_targetSystemId))
        {
            _completed = true;
            _haltOnFinish = true;
            _completionMessage = "No unexplored systems or POIs found!";
            return (true, new CommandExecutionResult { ResultMessage = _completionMessage, HaltScript = true });
        }

        await ExecuteStepAsync(client, state);
        if (_completed)
            return (true, new CommandExecutionResult { ResultMessage = _completionMessage ?? "Exploration complete.", HaltScript = _haltOnFinish });

        return (false, new CommandExecutionResult
        {
            ResultMessage = $"Exploring system `{_targetSystemId}`..."
        });
    }

    public async Task<(bool finished, CommandExecutionResult? result)> ContinueAsync(
        IRuntimeTransport client,
        GameState state)
    {
        if (_completed)
        {
            return (true, new CommandExecutionResult
            {
                ResultMessage = _completionMessage ?? "Exploration complete.",
                HaltScript = _haltOnFinish
            });
        }

        await ExecuteStepAsync(client, state);

        if (_completed)
        {
            return (true, new CommandExecutionResult
            {
                ResultMessage = _completionMessage ?? "Exploration complete.",
                HaltScript = _haltOnFinish
            });
        }

        return (false, new CommandExecutionResult
        {
            ResultMessage = !string.IsNullOrWhiteSpace(_targetSystemId)
                ? $"Exploring system `{_targetSystemId}`..."
                : "Exploring..."
        });
    }

    private async Task ExecuteStepAsync(IRuntimeTransport client, GameState state)
    {
        if (state.Docked)
        {
            await client.ExecuteCommandAsync("undock");
            return;
        }

        if (!string.IsNullOrWhiteSpace(state.CurrentPOI?.Id))
            MarkPoiExplored(state.CurrentPOI.Id);

        if (string.IsNullOrWhiteSpace(_targetSystemId))
        {
            _targetSystemId = SelectNearestUnexploredSystem(state);
            if (string.IsNullOrWhiteSpace(_targetSystemId))
            {
                _completed = true;
                _haltOnFinish = true;
                _completionMessage = "No unexplored systems or POIs found!";
            }

            return;
        }

        if (!string.Equals(state.System, _targetSystemId, StringComparison.Ordinal))
        {
            string? nextHop = await ResolveNextHopAsync(client, state, _targetSystemId);
            if (string.IsNullOrWhiteSpace(nextHop))
            {
                _plannedHops = null;
                MarkSystemUnreachable(_targetSystemId);
                _targetSystemId = SelectNearestUnexploredSystem(state);
                if (string.IsNullOrWhiteSpace(_targetSystemId))
                {
                    _completed = true;
                    _haltOnFinish = true;
                    _completionMessage = "All unexplored systems are unreachable!";
                }

                return;
            }

            await client.ExecuteCommandAsync("jump", new { target_system = nextHop });
            return;
        }

        _plannedHops = null;

        if (!_snapshot.SurveyedSystems.Contains(state.System))
        {
            try
            {
                await client.ExecuteCommandAsync("survey_system");
            }
            catch
            {
                // Best-effort survey for extra intel.
            }

            _snapshot.SurveyedSystems.Add(state.System);
            SaveSnapshot();
            return;
        }

        _plannedHops = null;
        MarkSystemExplored(state.System);
        _completed = true;
        _completionMessage = $"Exploration complete: `{state.System}` explored.";
    }

    public ActiveGoRoute? GetActiveRoute()
    {
        if (_plannedHops == null || _targetSystemId == null)
            return null;

        return new ActiveGoRoute(
            _targetSystemId,
            _plannedHops,
            _plannedTotalJumps,
            _fuelPerJump,
            _estimatedFuel,
            _fuelAvailable);
    }

    private void ResetRuntimeState()
    {
        _snapshot = new ExplorationStateSnapshot();
        _targetSystemId = null;
        _completed = false;
        _completionMessage = null;
        _haltOnFinish = false;
        _plannedHops = null;
    }

    private string? SelectNearestUnexploredSystem(GameState state)
    {
        var distanceBySystem = BuildSystemDistanceIndex(state);
        if (distanceBySystem.Count == 0)
            return null;

        var systems = distanceBySystem.Keys
            .OrderBy(s => distanceBySystem[s])
            .ThenBy(s => s, StringComparer.Ordinal)
            .ToList();

        foreach (var systemId in systems)
        {
            if (_snapshot.UnreachableSystems.Contains(systemId))
                continue;

            if (IsSystemUnexplored(state, systemId))
                return systemId;
        }

        return null;
    }

    private bool IsSystemUnexplored(GameState state, string systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
            return false;

        return !_snapshot.ExploredSystems.Contains(systemId);
    }

    private Dictionary<string, string> GetKnownPoiIdsBySystem(GameState state)
    {
        var byPoi = new Dictionary<string, string>(StringComparer.Ordinal);

        void Add(string? poiId, string? systemId)
        {
            if (string.IsNullOrWhiteSpace(poiId) || string.IsNullOrWhiteSpace(systemId))
                return;

            byPoi[poiId] = systemId;
        }

        Add(state.CurrentPOI?.Id, state.System);

        foreach (var poi in state.POIs ?? Array.Empty<POIInfo>())
            Add(poi.Id, string.IsNullOrWhiteSpace(poi.SystemId) ? state.System : poi.SystemId);

        foreach (var system in state.Galaxy?.Map?.Systems ?? new List<GalaxySystemInfo>())
        {
            if (string.IsNullOrWhiteSpace(system?.Id))
                continue;

            foreach (var poi in system.Pois ?? new List<GalaxyPoiInfo>())
                Add(poi?.Id, system.Id);
        }

        foreach (var known in state.Galaxy?.Map?.KnownPois ?? new List<GalaxyKnownPoiInfo>())
            Add(known.Id, known.SystemId);

        return byPoi;
    }

    private Dictionary<string, int> BuildSystemDistanceIndex(GameState state)
    {
        var distances = new Dictionary<string, int>(StringComparer.Ordinal);
        var adjacency = BuildAdjacency(state);
        var queue = new Queue<string>();

        if (string.IsNullOrWhiteSpace(state.System))
            return distances;

        distances[state.System] = 0;
        queue.Enqueue(state.System);

        while (queue.Count > 0)
        {
            string system = queue.Dequeue();
            int currentDistance = distances[system];

            if (!adjacency.TryGetValue(system, out var neighbors))
                continue;

            foreach (var neighbor in neighbors)
            {
                if (string.IsNullOrWhiteSpace(neighbor) || distances.ContainsKey(neighbor))
                    continue;

                distances[neighbor] = currentDistance + 1;
                queue.Enqueue(neighbor);
            }
        }

        return distances;
    }

    private Dictionary<string, List<string>> BuildAdjacency(GameState state)
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
            foreach (var neighbor in system.Connections ?? new List<string>())
            {
                AddEdge(system.Id, neighbor);
                AddEdge(neighbor, system.Id);
            }
        }

        return adjacency;
    }

    private async Task<string?> ResolveNextHopAsync(
        IRuntimeTransport client,
        GameState state,
        string targetSystem)
    {
        JsonElement routeResult = (await client.FindRouteAsync(targetSystem)).Payload;
        StoreRouteInfo(routeResult, state.System);
        string? nextHop = TryGetNextHop(routeResult, state.System);
        if (!string.IsNullOrWhiteSpace(nextHop))
            return nextHop;

        return state.Systems.Contains(targetSystem, StringComparer.Ordinal)
            ? targetSystem
            : null;
    }

    private void StoreRouteInfo(JsonElement routeResult, string currentSystem)
    {
        if (routeResult.ValueKind != JsonValueKind.Object)
        {
            _plannedHops = null;
            return;
        }

        var hops = ExtractFullRoute(routeResult, currentSystem);
        if (hops == null)
        {
            _plannedHops = null;
            return;
        }

        _plannedHops = hops;
        _plannedTotalJumps = routeResult.TryGetProperty("total_jumps", out var tj) && tj.ValueKind == JsonValueKind.Number
            ? tj.GetInt32() : 0;
        _fuelPerJump = routeResult.TryGetProperty("fuel_per_jump", out var fpj) && fpj.ValueKind == JsonValueKind.Number
            ? fpj.GetInt32() : 0;
        _estimatedFuel = routeResult.TryGetProperty("estimated_fuel", out var ef) && ef.ValueKind == JsonValueKind.Number
            ? ef.GetInt32() : 0;
        _fuelAvailable = routeResult.TryGetProperty("fuel_available", out var fa) && fa.ValueKind == JsonValueKind.Number
            ? fa.GetInt32() : 0;
    }

    private static List<string>? ExtractFullRoute(JsonElement routeResult, string currentSystem)
    {
        foreach (var candidate in ExtractStringRoutes(routeResult))
        {
            var route = candidate.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (route.Count == 0)
                continue;

            while (route.Count > 0 && string.Equals(route[0], currentSystem, StringComparison.Ordinal))
                route.RemoveAt(0);

            return route.Count > 0 ? route : null;
        }

        return null;
    }

    private static string? TryGetNextHop(JsonElement routeResult, string currentSystem)
    {
        foreach (var candidate in ExtractStringRoutes(routeResult))
        {
            if (candidate.Count == 0)
                continue;

            var route = candidate
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            while (route.Count > 0 && string.Equals(route[0], currentSystem, StringComparison.Ordinal))
                route.RemoveAt(0);

            if (route.Count > 0)
                return route[0];
        }

        return null;
    }

    private static IEnumerable<List<string>> ExtractStringRoutes(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        if (!root.TryGetProperty("route", out var routeElement))
            yield break;

        foreach (var route in ReadStringRouteCandidates(routeElement))
            yield return route;
    }

    private static IEnumerable<List<string>> ReadStringRouteCandidates(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Array)
            yield break;

        var directRoute = new List<string>();
        bool hasNested = false;

        foreach (var part in node.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                directRoute.Add(part.GetString() ?? "");
                continue;
            }

            if (part.ValueKind == JsonValueKind.Array)
            {
                hasNested = true;
                foreach (var nested in ReadStringRouteCandidates(part))
                    yield return nested;
            }
        }

        if (!hasNested && directRoute.Count > 0)
            yield return directRoute;
    }

    private void MarkPoiExplored(string poiId)
    {
        if (string.IsNullOrWhiteSpace(poiId))
            return;

        if (_snapshot.ExploredPois.Add(poiId))
            SaveSnapshot();
    }

    private void MarkSystemExplored(string systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
            return;

        if (_snapshot.ExploredSystems.Add(systemId))
            SaveSnapshot();
    }

    private void MarkSystemUnreachable(string systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
            return;

        if (_snapshot.UnreachableSystems.Add(systemId))
            SaveSnapshot();
    }

    private static ExplorationStateSnapshot LoadSnapshot()
        => ExplorationStateStore.Load();

    private void SaveSnapshot()
        => ExplorationStateStore.Save(_snapshot);
}
