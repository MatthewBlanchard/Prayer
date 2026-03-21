using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

public class ExploreCommand : IMultiTurnCommand
{
    public string Name => "explore";
    public DslCommandSyntax GetDslSyntax() => new();

    private string? _targetSystemId;
    private bool _completed;
    private string? _completionMessage;
    private bool _haltOnFinish;
    private readonly HashSet<string> _unreachableSystems = new(StringComparer.Ordinal);

    public bool IsAvailable(GameState state)
        => !string.IsNullOrWhiteSpace(state.System);

    public string BuildHelp(GameState state)
        => "- explore → visit current system unvisited POIs, then nearest system with unvisited POIs or unexplored status";

    public async Task<(bool finished, CommandExecutionResult? result)> StartAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        ResetRuntimeState(client);
        MarkCurrentPoiVisited(state);
        await ExecuteStepAsync(client, state);
        return BuildStepResult();
    }

    public async Task<(bool finished, CommandExecutionResult? result)> ContinueAsync(
        IRuntimeTransport client,
        GameState state)
    {
        if (_completed)
            return BuildStepResult();

        MarkCurrentPoiVisited(state);
        await ExecuteStepAsync(client, state);
        return BuildStepResult();
    }

    private (bool finished, CommandExecutionResult? result) BuildStepResult()
    {
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
                ? $"Exploring `{_targetSystemId}`..."
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

        if (string.IsNullOrWhiteSpace(_targetSystemId))
            _targetSystemId = SelectNextTargetSystem(state);

        if (string.IsNullOrWhiteSpace(_targetSystemId))
        {
            _completed = true;
            _haltOnFinish = true;
            _completionMessage = "No unexplored systems or POIs found!";
            return;
        }

        if (!string.Equals(state.System, _targetSystemId, StringComparison.Ordinal))
        {
            string? nextHop = ResolveNextHop(client, state, _targetSystemId);
            if (string.IsNullOrWhiteSpace(nextHop))
            {
                client.SetActiveRoute(null);
                _unreachableSystems.Add(_targetSystemId);
                _targetSystemId = null;
                return;
            }

            await client.ExecuteCommandAsync("jump", new { target_system = nextHop });
            return;
        }

        client.SetActiveRoute(null);

        if (TryGetNearestUnvisitedPoiInSystem(state, state.System, out var targetPoiId))
        {
            if (!string.Equals(state.CurrentPOI?.Id, targetPoiId, StringComparison.Ordinal))
            {
                await client.ExecuteCommandAsync("travel", new { target_poi = targetPoiId });
                return;
            }

            MarkCurrentPoiVisited(state);
            return;
        }

        var exploration = state.Galaxy?.Exploration ?? new GalaxyExplorationState();
        if (!exploration.SurveyedSystems.Contains(state.System))
        {
            try
            {
                await client.ExecuteCommandAsync("survey_system");
            }
            catch
            {
                // Survey is best-effort and can fail without scanner modules.
            }

            GalaxyStateHub.MarkSystemSurveyed(state.System);
            return;
        }

        _completed = true;
        _completionMessage = $"Exploration complete: `{state.System}` explored.";
    }

    private void ResetRuntimeState(IRuntimeTransport client)
    {
        _targetSystemId = null;
        _completed = false;
        _completionMessage = null;
        _haltOnFinish = false;
        _unreachableSystems.Clear();
        client.SetActiveRoute(null);
    }

    private void MarkCurrentPoiVisited(GameState state)
    {
        if (string.IsNullOrWhiteSpace(state.CurrentPOI?.Id))
            return;

        GalaxyStateHub.MarkPoiVisited(
            state.CurrentPOI.Id,
            state.CurrentPOI.SystemId,
            state.CurrentPOI.Name,
            state.CurrentPOI.Type,
            state.CurrentPOI.X,
            state.CurrentPOI.Y,
            state.CurrentPOI.HasBase,
            state.CurrentPOI.BaseId,
            state.CurrentPOI.BaseName);
    }

    private string? SelectNextTargetSystem(GameState state)
    {
        var distanceBySystem = BuildSystemDistanceIndex(state);
        if (distanceBySystem.Count == 0)
            return null;

        if (TryGetNearestUnvisitedPoiInSystem(state, state.System, out _))
            return state.System;

        var exploredSystems = state.Galaxy?.Exploration?.ExploredSystems
            ?? new HashSet<string>(StringComparer.Ordinal);

        foreach (var systemId in OrderedSystemsByDistance(distanceBySystem))
        {
            if (_unreachableSystems.Contains(systemId))
                continue;
            if (string.Equals(systemId, state.System, StringComparison.Ordinal))
                continue;

            bool hasUnvisitedPois = TryGetNearestUnvisitedPoiInSystem(state, systemId, out _);
            bool isUnexploredSystem = !exploredSystems.Contains(systemId);
            if (hasUnvisitedPois || isUnexploredSystem)
                return systemId;
        }

        return null;
    }

    private static IEnumerable<string> OrderedSystemsByDistance(Dictionary<string, int> distanceBySystem)
        => distanceBySystem.Keys
            .OrderBy(s => distanceBySystem[s])
            .ThenBy(s => s, StringComparer.Ordinal);

    private static bool TryGetNearestUnvisitedPoiInSystem(
        GameState state,
        string systemId,
        out string poiId)
    {
        poiId = "";
        if (string.IsNullOrWhiteSpace(systemId))
            return false;

        var visited = (state.Galaxy?.Knowledge?.PoisById ?? new Dictionary<string, GalaxyPoiKnowledge>(StringComparer.Ordinal))
            .Where(kvp => kvp.Value != null && kvp.Value.Visited)
            .Select(kvp => kvp.Key)
            .ToHashSet(StringComparer.Ordinal);
        var candidates = CollectKnownPoiIdsForSystem(state, systemId)
            .Where(id => !visited.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0)
            return false;

        var positions = BuildPoiPositionLookup(state, systemId);
        var currentX = state.CurrentPOI?.X;
        var currentY = state.CurrentPOI?.Y;
        var currentId = state.CurrentPOI?.Id ?? "";

        poiId = candidates
            .OrderBy(id => string.Equals(id, currentId, StringComparison.Ordinal) ? -1 : 0)
            .ThenBy(id => ComputeDistanceSquared(positions, id, currentX, currentY) ?? double.MaxValue)
            .ThenBy(id => id, StringComparer.Ordinal)
            .First();

        return true;
    }

    private static IEnumerable<string> CollectKnownPoiIdsForSystem(GameState state, string systemId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var knowledge = state.Galaxy?.Knowledge?.PoisById
            ?? new Dictionary<string, GalaxyPoiKnowledge>(StringComparer.Ordinal);

        foreach (var poi in knowledge.Values)
        {
            if (poi == null || string.IsNullOrWhiteSpace(poi.Id))
                continue;
            if (!string.Equals(poi.SystemId, systemId, StringComparison.Ordinal))
                continue;
            ids.Add(poi.Id);
        }

        if (string.Equals(state.System, systemId, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(state.CurrentPOI?.Id))
                ids.Add(state.CurrentPOI.Id);

            foreach (var poi in state.POIs ?? Array.Empty<POIInfo>())
            {
                if (string.IsNullOrWhiteSpace(poi?.Id))
                    continue;
                if (!string.IsNullOrWhiteSpace(poi.SystemId) &&
                    !string.Equals(poi.SystemId, systemId, StringComparison.Ordinal))
                    continue;
                ids.Add(poi.Id);
            }
        }

        foreach (var system in state.Galaxy?.Map?.Systems ?? new List<GalaxySystemInfo>())
        {
            if (!string.Equals(system?.Id, systemId, StringComparison.Ordinal))
                continue;

            foreach (var poi in system?.Pois ?? new List<GalaxyPoiInfo>())
            {
                if (!string.IsNullOrWhiteSpace(poi?.Id))
                    ids.Add(poi.Id);
            }
        }

        return ids;
    }

    private static Dictionary<string, (double? x, double? y)> BuildPoiPositionLookup(GameState state, string systemId)
    {
        var lookup = new Dictionary<string, (double? x, double? y)>(StringComparer.Ordinal);

        void Merge(string id, double? x, double? y)
        {
            if (string.IsNullOrWhiteSpace(id))
                return;

            if (!lookup.TryGetValue(id, out var existing))
            {
                lookup[id] = (x, y);
                return;
            }

            lookup[id] = (existing.x ?? x, existing.y ?? y);
        }

        foreach (var poi in state.Galaxy?.Knowledge?.PoisById?.Values ?? Enumerable.Empty<GalaxyPoiKnowledge>())
        {
            if (poi == null || !string.Equals(poi.SystemId, systemId, StringComparison.Ordinal))
                continue;
            Merge(poi.Id, poi.X, poi.Y);
        }

        if (string.Equals(state.System, systemId, StringComparison.Ordinal))
        {
            Merge(state.CurrentPOI?.Id ?? "", state.CurrentPOI?.X, state.CurrentPOI?.Y);
            foreach (var poi in state.POIs ?? Array.Empty<POIInfo>())
            {
                if (poi == null)
                    continue;
                if (!string.IsNullOrWhiteSpace(poi.SystemId) &&
                    !string.Equals(poi.SystemId, systemId, StringComparison.Ordinal))
                    continue;
                Merge(poi.Id, poi.X, poi.Y);
            }
        }

        foreach (var system in state.Galaxy?.Map?.Systems ?? new List<GalaxySystemInfo>())
        {
            if (!string.Equals(system?.Id, systemId, StringComparison.Ordinal))
                continue;
            foreach (var poi in system?.Pois ?? new List<GalaxyPoiInfo>())
                Merge(poi?.Id ?? "", poi?.X, poi?.Y);
        }

        return lookup;
    }

    private static double? ComputeDistanceSquared(
        IReadOnlyDictionary<string, (double? x, double? y)> positions,
        string poiId,
        double? originX,
        double? originY)
    {
        if (string.IsNullOrWhiteSpace(poiId) || !originX.HasValue || !originY.HasValue)
            return null;
        if (!positions.TryGetValue(poiId, out var point))
            return null;
        if (!point.x.HasValue || !point.y.HasValue)
            return null;

        double dx = point.x.Value - originX.Value;
        double dy = point.y.Value - originY.Value;
        return (dx * dx) + (dy * dy);
    }

    private static Dictionary<string, int> BuildSystemDistanceIndex(GameState state)
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
            foreach (var neighbor in system.Connections ?? new List<string>())
            {
                AddEdge(system.Id, neighbor);
                AddEdge(neighbor, system.Id);
            }
        }

        return adjacency;
    }

    private static string? ResolveNextHop(IRuntimeTransport client, GameState state, string targetSystem)
    {
        RouteInfo? route = client.FindPath(state, targetSystem);
        client.SetActiveRoute(route);
        return route is { Hops.Count: > 0 } ? route.Hops[0] : null;
    }
}
