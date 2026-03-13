using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public class RefuelCommand : IMultiTurnCommand
{
    public string Name => "refuel";
    public DslCommandSyntax GetDslSyntax() => new();

    private string? _targetSystemId;
    private string? _targetPoiId;
    private bool _completed;
    private bool _haltOnFinish;
    private string? _completionMessage;

    public bool IsAvailable(GameState state)
        => !string.IsNullOrWhiteSpace(state.System);

    public string BuildHelp(GameState state)
        => "- refuel → go to nearest known station, dock, and refuel only if needed";

    public async Task<(bool finished, CommandExecutionResult? result)> StartAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        ResetRuntimeState(client);
        await ExecuteStepAsync(client, state);
        return BuildStepResult();
    }

    public async Task<(bool finished, CommandExecutionResult? result)> ContinueAsync(
        IRuntimeTransport client,
        GameState state)
    {
        if (_completed)
            return BuildStepResult();

        await ExecuteStepAsync(client, state);
        return BuildStepResult();
    }

    private (bool finished, CommandExecutionResult? result) BuildStepResult()
    {
        if (_completed)
        {
            return (true, new CommandExecutionResult
            {
                ResultMessage = _completionMessage ?? "Refuel complete.",
                HaltScript = _haltOnFinish
            });
        }

        if (!string.IsNullOrWhiteSpace(_targetPoiId))
        {
            return (false, new CommandExecutionResult
            {
                ResultMessage = $"Refueling via nearest station `{_targetPoiId}`..."
            });
        }

        return (false, new CommandExecutionResult
        {
            ResultMessage = "Refueling via nearest station..."
        });
    }

    private async Task ExecuteStepAsync(IRuntimeTransport client, GameState state)
    {
        if (state.Ship.Fuel >= state.Ship.MaxFuel)
        {
            _completed = true;
            _completionMessage = "Fuel already full.";
            return;
        }

        if (!TryResolveNearestStation(state, out var targetSystemId, out var targetPoiId))
        {
            _completed = true;
            _haltOnFinish = true;
            _completionMessage = "No known station available for refueling.";
            return;
        }

        _targetSystemId = targetSystemId;
        _targetPoiId = targetPoiId;

        if (!string.Equals(state.System, targetSystemId, StringComparison.Ordinal))
        {
            if (state.Docked)
            {
                await client.ExecuteCommandAsync("undock");
                return;
            }

            string? nextHop = ResolveNextHop(client, state, targetSystemId);
            if (string.IsNullOrWhiteSpace(nextHop))
            {
                _completed = true;
                _haltOnFinish = true;
                _completionMessage = $"No route found to nearest station `{targetPoiId}`.";
                return;
            }

            await client.ExecuteCommandAsync("jump", new { target_system = nextHop });
            return;
        }

        client.SetActiveRoute(null);

        if (!string.Equals(state.CurrentPOI?.Id, targetPoiId, StringComparison.Ordinal))
        {
            if (state.Docked)
            {
                await client.ExecuteCommandAsync("undock");
                return;
            }

            await client.ExecuteCommandAsync("travel", new { target_poi = targetPoiId });
            return;
        }

        if (!state.Docked)
        {
            await client.ExecuteCommandAsync("dock");
            return;
        }

        if (state.Ship.Fuel >= state.Ship.MaxFuel)
        {
            _completed = true;
            _completionMessage = "Fuel already full.";
            return;
        }

        JsonElement response = (await client.ExecuteCommandAsync("refuel", new { })).Payload;
        string? message = CommandJson.TryGetResultMessage(response);

        if (CommandJson.TryGetError(response, out var code, out var error))
        {
            string details = !string.IsNullOrWhiteSpace(error)
                ? error!
                : code ?? "refuel failed";
            _completed = true;
            _haltOnFinish = true;
            _completionMessage = $"Refuel failed: {details}";
            return;
        }

        _completed = true;
        _completionMessage = string.IsNullOrWhiteSpace(message)
            ? "Refueled."
            : message;
    }

    private void ResetRuntimeState(IRuntimeTransport client)
    {
        _targetSystemId = null;
        _targetPoiId = null;
        _completed = false;
        _haltOnFinish = false;
        _completionMessage = null;
        client.SetActiveRoute(null);
    }

    private static string? ResolveNextHop(IRuntimeTransport client, GameState state, string targetSystem)
    {
        RouteInfo? route = client.FindPath(state, targetSystem);
        client.SetActiveRoute(route);
        return route is { Hops.Count: > 0 } ? route.Hops[0] : null;
    }

    private static bool TryResolveNearestStation(
        GameState state,
        out string systemId,
        out string poiId)
    {
        systemId = "";
        poiId = "";

        var poiSystemLookup = BuildPoiSystemLookup(state);
        var distanceBySystem = BuildSystemDistanceIndex(state);
        var poiPositions = BuildPoiPositionLookup(state);
        var candidatePoiIds = CollectStationPoiIds(state)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (candidatePoiIds.Count == 0)
            return false;

        double? currentX = state.CurrentPOI?.X;
        double? currentY = state.CurrentPOI?.Y;

        string? bestPoi = null;
        string? bestSystem = null;
        int bestSystemDistance = int.MaxValue;
        double bestLocalDistance = double.MaxValue;

        foreach (var candidatePoiId in candidatePoiIds)
        {
            if (!poiSystemLookup.TryGetValue(candidatePoiId, out var candidateSystem) ||
                string.IsNullOrWhiteSpace(candidateSystem))
            {
                continue;
            }

            int systemDistance = distanceBySystem.TryGetValue(candidateSystem, out var d)
                ? d
                : int.MaxValue;
            if (systemDistance == int.MaxValue)
                continue;

            double localDistance = ComputeLocalDistanceSquared(
                state,
                poiPositions,
                candidateSystem,
                candidatePoiId,
                currentX,
                currentY);

            if (systemDistance < bestSystemDistance ||
                (systemDistance == bestSystemDistance && localDistance < bestLocalDistance) ||
                (systemDistance == bestSystemDistance &&
                 Math.Abs(localDistance - bestLocalDistance) < 0.0001 &&
                 string.CompareOrdinal(candidatePoiId, bestPoi) < 0))
            {
                bestSystemDistance = systemDistance;
                bestLocalDistance = localDistance;
                bestPoi = candidatePoiId;
                bestSystem = candidateSystem;
            }
        }

        if (string.IsNullOrWhiteSpace(bestSystem) || string.IsNullOrWhiteSpace(bestPoi))
            return false;

        systemId = bestSystem;
        poiId = bestPoi;
        return true;
    }

    private static IEnumerable<string> CollectStationPoiIds(GameState state)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        if (state.CurrentPOI?.IsStation == true &&
            state.CurrentPOI.HasBase &&
            !string.IsNullOrWhiteSpace(state.CurrentPOI.Id))
        {
            ids.Add(state.CurrentPOI.Id);
        }

        foreach (var poi in state.POIs ?? Array.Empty<POIInfo>())
        {
            if (poi?.IsStation == true &&
                poi.HasBase &&
                !string.IsNullOrWhiteSpace(poi.Id))
            {
                ids.Add(poi.Id);
            }
        }

        foreach (var known in state.Galaxy?.Map?.KnownPois ?? new List<GalaxyKnownPoiInfo>())
        {
            if (!string.IsNullOrWhiteSpace(known?.Id) &&
                known.HasBase &&
                string.Equals(known.Type, "station", StringComparison.Ordinal))
            {
                ids.Add(known.Id);
            }
        }

        foreach (var known in state.Galaxy?.Knowledge?.PoisById?.Values ?? Enumerable.Empty<GalaxyPoiKnowledge>())
        {
            if (known != null &&
                !string.IsNullOrWhiteSpace(known.Id) &&
                known.HasBase &&
                string.Equals(known.Type, "station", StringComparison.Ordinal))
            {
                ids.Add(known.Id);
            }
        }

        return ids;
    }

    private static Dictionary<string, string> BuildPoiSystemLookup(GameState state)
    {
        var lookup = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(state.CurrentPOI?.Id))
            lookup[state.CurrentPOI.Id] = state.System;

        foreach (var poi in state.POIs ?? Array.Empty<POIInfo>())
        {
            if (string.IsNullOrWhiteSpace(poi?.Id))
                continue;

            lookup[poi.Id] = string.IsNullOrWhiteSpace(poi.SystemId)
                ? state.System
                : poi.SystemId;
        }

        foreach (var known in state.Galaxy?.Map?.KnownPois ?? new List<GalaxyKnownPoiInfo>())
        {
            if (string.IsNullOrWhiteSpace(known.Id) || string.IsNullOrWhiteSpace(known.SystemId))
                continue;

            lookup[known.Id] = known.SystemId;
        }

        foreach (var known in state.Galaxy?.Knowledge?.PoisById?.Values ?? Enumerable.Empty<GalaxyPoiKnowledge>())
        {
            if (known == null || string.IsNullOrWhiteSpace(known.Id) || string.IsNullOrWhiteSpace(known.SystemId))
                continue;

            lookup[known.Id] = known.SystemId;
        }

        foreach (var system in state.Galaxy?.Map?.Systems ?? new List<GalaxySystemInfo>())
        {
            if (string.IsNullOrWhiteSpace(system?.Id))
                continue;

            foreach (var poi in system.Pois ?? new List<GalaxyPoiInfo>())
            {
                if (string.IsNullOrWhiteSpace(poi?.Id))
                    continue;

                lookup[poi.Id] = system.Id;
            }
        }

        return lookup;
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
            int baseDistance = distances[system];

            if (!adjacency.TryGetValue(system, out var neighbors))
                continue;

            foreach (var neighbor in neighbors)
            {
                if (string.IsNullOrWhiteSpace(neighbor) || distances.ContainsKey(neighbor))
                    continue;

                distances[neighbor] = baseDistance + 1;
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
            foreach (var connected in system.Connections ?? new List<string>())
            {
                AddEdge(system.Id, connected);
                AddEdge(connected, system.Id);
            }
        }

        return adjacency;
    }

    private static Dictionary<string, (double x, double y)> BuildPoiPositionLookup(GameState state)
    {
        var lookup = new Dictionary<string, (double x, double y)>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(state.CurrentPOI?.Id) &&
            state.CurrentPOI.X.HasValue &&
            state.CurrentPOI.Y.HasValue)
        {
            lookup[state.CurrentPOI.Id] = (state.CurrentPOI.X.Value, state.CurrentPOI.Y.Value);
        }

        foreach (var poi in state.POIs ?? Array.Empty<POIInfo>())
        {
            if (string.IsNullOrWhiteSpace(poi?.Id) || !poi.X.HasValue || !poi.Y.HasValue)
                continue;

            lookup[poi.Id] = (poi.X.Value, poi.Y.Value);
        }

        foreach (var known in state.Galaxy?.Map?.KnownPois ?? new List<GalaxyKnownPoiInfo>())
        {
            if (string.IsNullOrWhiteSpace(known?.Id) || !known.X.HasValue || !known.Y.HasValue)
                continue;

            lookup[known.Id] = (known.X.Value, known.Y.Value);
        }

        foreach (var known in state.Galaxy?.Knowledge?.PoisById?.Values ?? Enumerable.Empty<GalaxyPoiKnowledge>())
        {
            if (known == null || string.IsNullOrWhiteSpace(known.Id) || !known.X.HasValue || !known.Y.HasValue)
                continue;

            lookup[known.Id] = (known.X.Value, known.Y.Value);
        }

        foreach (var system in state.Galaxy?.Map?.Systems ?? new List<GalaxySystemInfo>())
        {
            foreach (var poi in system.Pois ?? new List<GalaxyPoiInfo>())
            {
                if (string.IsNullOrWhiteSpace(poi?.Id) || !poi.X.HasValue || !poi.Y.HasValue)
                    continue;

                lookup[poi.Id] = (poi.X.Value, poi.Y.Value);
            }
        }

        return lookup;
    }

    private static double ComputeLocalDistanceSquared(
        GameState state,
        Dictionary<string, (double x, double y)> poiPositions,
        string candidateSystem,
        string candidatePoiId,
        double? currentX,
        double? currentY)
    {
        if (!string.Equals(candidateSystem, state.System, StringComparison.Ordinal))
            return double.MaxValue - 1;

        if (!currentX.HasValue || !currentY.HasValue)
            return double.MaxValue - 2;

        if (!poiPositions.TryGetValue(candidatePoiId, out var pos))
            return double.MaxValue - 3;

        double dx = pos.x - currentX.Value;
        double dy = pos.y - currentY.Value;
        return (dx * dx) + (dy * dy);
    }
}
