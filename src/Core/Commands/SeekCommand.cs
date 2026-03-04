using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public class SeekCommand : IMultiTurnCommand, IDslCommandGrammar
{
    public string Name => "seek";
    public DslCommandSyntax GetDslSyntax() => new(
        ArgSpecs: new[]
        {
            new DslArgumentSpec(
                DslArgKind.Item | DslArgKind.Any,
                Required: true)
        });

    private string? _resourceId;
    private string? _targetPoiId;
    private string? _targetSystemId;
    private Queue<string> _bfsSystems = new();
    private readonly HashSet<string> _exploredSystems = new(StringComparer.Ordinal);
    private string _statusMessage = "";
    private string? _completionMessage;

    public bool IsAvailable(GameState state)
        => !string.IsNullOrWhiteSpace(state.System);

    public string BuildHelp(GameState state)
        => "- seek <resourceId> → go to nearest known POI with resource; fallback to BFS exploration of mineable POIs";

    public Task<CommandExecutionResult?> StartAsync(
        SpaceMoltHttpClient client,
        CommandResult cmd,
        GameState state)
    {
        _resourceId = cmd.Arg1?.Trim();
        _targetPoiId = null;
        _targetSystemId = null;
        _bfsSystems = new Queue<string>();
        _exploredSystems.Clear();
        _completionMessage = null;
        _statusMessage = "";

        if (string.IsNullOrWhiteSpace(_resourceId))
        {
            _completionMessage = "Usage: seek <resourceId>.";
            return Task.FromResult<CommandExecutionResult?>(new CommandExecutionResult
            {
                ResultMessage = _completionMessage
            });
        }

        if (CurrentPoiHasResource(state))
        {
            _completionMessage = $"Found `{_resourceId}` at current POI `{state.CurrentPOI.Id}`.";
            return Task.FromResult<CommandExecutionResult?>(new CommandExecutionResult
            {
                ResultMessage = _completionMessage
            });
        }

        if (TryResolveNearestKnownTarget(state, out var knownSystem, out var knownPoi))
        {
            _targetSystemId = knownSystem;
            _targetPoiId = knownPoi;
            _statusMessage = $"Seeking `{_resourceId}` at nearest known POI `{knownPoi}` (system `{knownSystem}`).";
        }
        else
        {
            InitializeBfsQueue(state);
            _statusMessage = $"No known POI for `{_resourceId}`. Starting BFS exploration from `{state.System}`.";
        }

        return Task.FromResult<CommandExecutionResult?>(new CommandExecutionResult
        {
            ResultMessage = _statusMessage
        });
    }

    public async Task<(bool finished, CommandExecutionResult? result)> ContinueAsync(
        SpaceMoltHttpClient client,
        GameState state)
    {
        if (!string.IsNullOrWhiteSpace(_completionMessage))
        {
            var done = _completionMessage;
            _completionMessage = null;
            return (true, new CommandExecutionResult { ResultMessage = done });
        }

        if (string.IsNullOrWhiteSpace(_resourceId))
        {
            return (true, new CommandExecutionResult
            {
                ResultMessage = "Seek stopped: missing resource target."
            });
        }

        if (CurrentPoiHasResource(state))
        {
            return (true, new CommandExecutionResult
            {
                ResultMessage = $"Found `{_resourceId}` at `{state.CurrentPOI.Id}` in `{state.System}`."
            });
        }

        if (!string.IsNullOrWhiteSpace(_targetPoiId) && !string.IsNullOrWhiteSpace(_targetSystemId))
        {
            return await ContinueToKnownTargetAsync(client, state);
        }

        return await ContinueBfsExplorationAsync(client, state);
    }

    private async Task<(bool finished, CommandExecutionResult? result)> ContinueToKnownTargetAsync(
        SpaceMoltHttpClient client,
        GameState state)
    {
        if (!string.Equals(state.System, _targetSystemId, StringComparison.Ordinal))
        {
            if (state.Docked)
            {
                await client.ExecuteAsync("undock");
                return (false, null);
            }

            string? nextHop = await ResolveNextHopAsync(client, state, _targetSystemId!);
            if (string.IsNullOrWhiteSpace(nextHop))
            {
                _targetPoiId = null;
                _targetSystemId = null;
                InitializeBfsQueue(state);
                return (false, new CommandExecutionResult
                {
                    ResultMessage = $"Could not route to known target system. Falling back to BFS from `{state.System}`."
                });
            }

            await client.ExecuteAsync("jump", new { target_system = nextHop });
            return (false, null);
        }

        if (string.Equals(state.CurrentPOI.Id, _targetPoiId, StringComparison.Ordinal))
        {
            _targetPoiId = null;
            _targetSystemId = null;

            if (CurrentPoiHasResource(state))
            {
                return (true, new CommandExecutionResult
                {
                    ResultMessage = $"Found `{_resourceId}` at `{state.CurrentPOI.Id}` in `{state.System}`."
                });
            }

            InitializeBfsQueue(state);
            return (false, new CommandExecutionResult
            {
                ResultMessage = $"Known POI `{state.CurrentPOI.Id}` did not contain `{_resourceId}`. Continuing BFS exploration."
            });
        }

        if (state.Docked)
        {
            await client.ExecuteAsync("undock");
            return (false, null);
        }

        await client.ExecuteAsync("travel", new { target_poi = _targetPoiId });
        return (false, null);
    }

    private async Task<(bool finished, CommandExecutionResult? result)> ContinueBfsExplorationAsync(
        SpaceMoltHttpClient client,
        GameState state)
    {
        if (CurrentPoiHasResource(state))
        {
            return (true, new CommandExecutionResult
            {
                ResultMessage = $"Found `{_resourceId}` at `{state.CurrentPOI.Id}` in `{state.System}`."
            });
        }

        var currentMineable = GetCurrentSystemMineablePois(state);

        var uncheckedMineable = currentMineable
            .Where(p => !_checkedPoiIds.Contains(p.Id))
            .ToList();

        // Current POI should be evaluated first.
        if (state.CurrentPOI.IsMiningTarget &&
            !_checkedPoiIds.Contains(state.CurrentPOI.Id))
        {
            _checkedPoiIds.Add(state.CurrentPOI.Id);
            if (CurrentPoiHasResource(state))
            {
                return (true, new CommandExecutionResult
                {
                    ResultMessage = $"Found `{_resourceId}` at `{state.CurrentPOI.Id}` in `{state.System}`."
                });
            }

            uncheckedMineable = currentMineable
                .Where(p => !_checkedPoiIds.Contains(p.Id))
                .ToList();
        }

        var nextMineablePoi = uncheckedMineable
            .FirstOrDefault(p => !string.Equals(p.Id, state.CurrentPOI.Id, StringComparison.Ordinal));
        if (nextMineablePoi != null)
        {
            if (state.Docked)
            {
                await client.ExecuteAsync("undock");
                return (false, null);
            }

            await client.ExecuteAsync("travel", new { target_poi = nextMineablePoi.Id });
            return (false, null);
        }

        _exploredSystems.Add(state.System);

        while (_bfsSystems.Count > 0)
        {
            string candidate = _bfsSystems.Peek();

            if (_exploredSystems.Contains(candidate))
            {
                _bfsSystems.Dequeue();
                continue;
            }

            if (string.Equals(candidate, state.System, StringComparison.Ordinal))
            {
                // Arrived; let next iteration check this system's POIs.
                return (false, null);
            }

            if (state.Docked)
            {
                await client.ExecuteAsync("undock");
                return (false, null);
            }

            string? nextHop = await ResolveNextHopAsync(client, state, candidate);
            if (string.IsNullOrWhiteSpace(nextHop))
            {
                _exploredSystems.Add(candidate);
                _bfsSystems.Dequeue();
                continue;
            }

            await client.ExecuteAsync("jump", new { target_system = nextHop });
            return (false, null);
        }

        return (true, new CommandExecutionResult
        {
            ResultMessage = $"Seek complete: `{_resourceId}` not found in explored mineable POIs."
        });
    }

    private readonly HashSet<string> _checkedPoiIds = new(StringComparer.Ordinal);

    private void InitializeBfsQueue(GameState state)
    {
        _checkedPoiIds.Clear();

        var adjacency = BuildAdjacency(state);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var bfsQueue = new Queue<string>();
        var ordered = new List<string>();

        string root = state.System;
        if (string.IsNullOrWhiteSpace(root))
        {
            _bfsSystems = new Queue<string>();
            return;
        }

        visited.Add(root);
        bfsQueue.Enqueue(root);

        while (bfsQueue.Count > 0)
        {
            string system = bfsQueue.Dequeue();
            ordered.Add(system);

            if (!adjacency.TryGetValue(system, out var neighbors))
                continue;

            foreach (var neighbor in neighbors)
            {
                if (string.IsNullOrWhiteSpace(neighbor))
                    continue;
                if (!visited.Add(neighbor))
                    continue;
                bfsQueue.Enqueue(neighbor);
            }
        }

        _bfsSystems = new Queue<string>(ordered);
    }

    private Dictionary<string, List<string>> BuildAdjacency(GameState state)
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        void AddEdge(string from, string to)
        {
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                return;

            if (!adjacency.TryGetValue(from, out var list))
            {
                list = new List<string>();
                adjacency[from] = list;
            }

            if (!list.Contains(to, StringComparer.Ordinal))
                list.Add(to);
        }

        if (!string.IsNullOrWhiteSpace(state.System))
        {
            if (!adjacency.ContainsKey(state.System))
                adjacency[state.System] = new List<string>();

            foreach (var neighbor in state.Systems ?? Array.Empty<string>())
            {
                AddEdge(state.System, neighbor);
                AddEdge(neighbor, state.System);
            }
        }

        var map = state.Galaxy?.Map;
        if (map?.Systems != null)
        {
            foreach (var system in map.Systems)
            {
                if (string.IsNullOrWhiteSpace(system?.Id))
                    continue;

                if (!adjacency.ContainsKey(system.Id))
                    adjacency[system.Id] = new List<string>();

                foreach (var neighbor in system.Connections ?? new List<string>())
                {
                    AddEdge(system.Id, neighbor);
                    AddEdge(neighbor, system.Id);
                }
            }
        }

        return adjacency;
    }

    private List<POIInfo> GetCurrentSystemMineablePois(GameState state)
    {
        var list = new List<POIInfo>();

        if (state.CurrentPOI?.IsMiningTarget == true)
            list.Add(state.CurrentPOI);

        if (state.POIs != null)
        {
            list.AddRange(state.POIs.Where(p => p.IsMiningTarget));
        }

        return list
            .Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    private bool CurrentPoiHasResource(GameState state)
    {
        if (string.IsNullOrWhiteSpace(_resourceId))
            return false;

        return PoiHasResource(state.CurrentPOI, _resourceId);
    }

    private static bool PoiHasResource(POIInfo poi, string resourceId)
    {
        if (poi?.Resources == null || poi.Resources.Length == 0)
            return false;

        return poi.Resources.Any(r =>
            !string.IsNullOrWhiteSpace(r.ResourceId) &&
            string.Equals(r.ResourceId, resourceId, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryResolveNearestKnownTarget(
        GameState state,
        out string systemId,
        out string poiId)
    {
        systemId = "";
        poiId = "";

        if (string.IsNullOrWhiteSpace(_resourceId))
            return false;

        var resourceIndex = state.Galaxy?.Resources?.PoisByResource;
        if (resourceIndex == null || resourceIndex.Count == 0)
            return false;

        string? matchedResourceKey = resourceIndex.Keys
            .FirstOrDefault(k => string.Equals(k, _resourceId, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(matchedResourceKey))
            return false;

        var poiCandidates = resourceIndex[matchedResourceKey]
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (poiCandidates.Count == 0)
            return false;

        var poiSystemLookup = new Dictionary<string, string>(StringComparer.Ordinal);
        if (state.CurrentPOI != null && !string.IsNullOrWhiteSpace(state.CurrentPOI.Id))
            poiSystemLookup[state.CurrentPOI.Id] = state.System;
        foreach (var poi in state.POIs ?? Array.Empty<POIInfo>())
        {
            if (!string.IsNullOrWhiteSpace(poi.Id))
                poiSystemLookup[poi.Id] = !string.IsNullOrWhiteSpace(poi.SystemId) ? poi.SystemId : state.System;
        }

        foreach (var known in state.Galaxy?.Map?.KnownPois ?? new List<GalaxyKnownPoiInfo>())
        {
            if (string.IsNullOrWhiteSpace(known.Id) || string.IsNullOrWhiteSpace(known.SystemId))
                continue;
            poiSystemLookup[known.Id] = known.SystemId;
        }

        var distanceBySystem = BuildSystemDistanceIndex(state);
        int bestDistance = int.MaxValue;
        string? bestSystem = null;
        string? bestPoi = null;

        foreach (var candidatePoi in poiCandidates)
        {
            if (!poiSystemLookup.TryGetValue(candidatePoi, out var candidateSystem) ||
                string.IsNullOrWhiteSpace(candidateSystem))
            {
                continue;
            }

            int distance = distanceBySystem.TryGetValue(candidateSystem, out var d)
                ? d
                : int.MaxValue - 1;

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestSystem = candidateSystem;
                bestPoi = candidatePoi;
            }
        }

        if (string.IsNullOrWhiteSpace(bestSystem) || string.IsNullOrWhiteSpace(bestPoi))
            return false;

        systemId = bestSystem;
        poiId = bestPoi;
        return true;
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
                if (string.IsNullOrWhiteSpace(neighbor))
                    continue;
                if (distances.ContainsKey(neighbor))
                    continue;

                distances[neighbor] = currentDistance + 1;
                queue.Enqueue(neighbor);
            }
        }

        return distances;
    }

    private static async Task<string?> ResolveNextHopAsync(
        SpaceMoltHttpClient client,
        GameState state,
        string targetSystem)
    {
        JsonElement routeResult = await client.FindRouteAsync(targetSystem);
        string? nextHop = TryGetNextHop(routeResult, state.System, targetSystem);
        if (!string.IsNullOrWhiteSpace(nextHop))
            return nextHop;

        return state.Systems.Contains(targetSystem, StringComparer.Ordinal)
            ? targetSystem
            : null;
    }

    private static string? TryGetNextHop(JsonElement routeResult, string currentSystem, string targetSystem)
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
        if (root.ValueKind == JsonValueKind.Array)
        {
            var arr = ConvertRouteArrayToSystems(root);
            if (arr.Count > 0)
                yield return arr;
        }

        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        if (root.TryGetProperty("route", out var route) &&
            route.ValueKind == JsonValueKind.Array)
        {
            var arr = ConvertRouteArrayToSystems(route);
            if (arr.Count > 0)
                yield return arr;
        }

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array)
                continue;

            var arr = ConvertRouteArrayToSystems(prop.Value);
            if (arr.Count > 0)
                yield return arr;
        }
    }

    private static List<string> ConvertRouteArrayToSystems(JsonElement routeArray)
    {
        var systems = new List<string>();

        foreach (var entry in routeArray.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                var id = entry.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                    systems.Add(id);
                continue;
            }

            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            if (entry.TryGetProperty("system_id", out var sid) && sid.ValueKind == JsonValueKind.String)
            {
                var id = sid.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                    systems.Add(id!);
            }
        }

        return systems;
    }
}
