using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public class MineCommand : IMultiTurnCommand, IDslCommandGrammar
{
    public string Name => "mine";
    public DslCommandSyntax GetDslSyntax() => new(
        ArgSpecs: new[]
        {
            new DslArgumentSpec(
                DslArgKind.Enum | DslArgKind.Item | DslArgKind.Any,
                Required: false,
                EnumType: "mining_target")
        });

    private bool _stopRequested;
    private string? _stopReason;
    private string? _completionMessage;

    private bool _resourceMode;
    private string? _resourceId;

    private string? _targetPoiId;
    private string? _targetSystemId;
    private Queue<string> _bfsSystems = new();
    private readonly HashSet<string> _exploredSystems = new(StringComparer.Ordinal);
    private readonly HashSet<string> _checkedPoiIds = new(StringComparer.Ordinal);
    private static readonly object ExplorationSync = new();
    private static readonly Dictionary<string, HashSet<string>> ExhaustedPoisByResource =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<string>> ExploredSystemsByResource =
        new(StringComparer.OrdinalIgnoreCase);

    public bool IsAvailable(GameState state)
        => !string.IsNullOrWhiteSpace(state.System);

    public string BuildHelp(GameState state)
        => "- mine [asteroid_belt|asteroid|gas_cloud|ice_field|resourceId] → mine here, auto-go to local POI type, or find+mine a resource";

    public async Task<CommandExecutionResult?> StartAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        ResetState();

        var rawArg = (cmd.Arg1 ?? string.Empty).Trim();
        var requestedType = NormalizeMiningType(rawArg);

        if (string.IsNullOrWhiteSpace(rawArg))
        {
            if (state.CurrentPOI?.IsMiningTarget == true && !string.IsNullOrWhiteSpace(state.CurrentPOI.Id))
            {
                _targetPoiId = state.CurrentPOI.Id;
            }
            else
            {
                var defaultLocalPoi = SelectDefaultMiningPoi(state);
                if (defaultLocalPoi == null)
                {
                    _stopRequested = true;
                    _stopReason = $"No local mining POI found in system {state.System}.";
                    return new CommandExecutionResult { ResultMessage = _stopReason };
                }

                _targetPoiId = defaultLocalPoi.Id;
            }
        }
        else if (requestedType != null)
        {
            if (state.CurrentPOI != null &&
                string.Equals(state.CurrentPOI.Type, requestedType, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(state.CurrentPOI.Id))
            {
                _targetPoiId = state.CurrentPOI.Id;
            }
            else
            {
                var matchingLocalPoi = state.POIs
                    .Where(p => string.Equals(p.Type, requestedType, StringComparison.Ordinal))
                    .OrderByDescending(p => p.Online)
                    .FirstOrDefault();

                if (matchingLocalPoi == null)
                {
                    _stopRequested = true;
                    _stopReason = $"No local POI of type '{requestedType}' in system {state.System}.";
                    return new CommandExecutionResult { ResultMessage = _stopReason };
                }

                _targetPoiId = matchingLocalPoi.Id;
            }
        }
        else
        {
            _resourceMode = true;
            _resourceId = rawArg;
            LoadPersistentResourceExploration();

            if (CurrentPoiHasResource(state))
            {
                _targetSystemId = state.System;
                _targetPoiId = state.CurrentPOI.Id;
            }
            else if (TryResolveNearestKnownTarget(state, out var knownSystem, out var knownPoi))
            {
                _targetSystemId = knownSystem;
                _targetPoiId = knownPoi;
            }
            else
            {
                InitializeBfsQueue(state);
            }
        }

        var response = await ExecuteStepAsync(client, state);

        return new CommandExecutionResult
        {
            ResultMessage = _stopReason
                ?? _completionMessage
                ?? CommandJson.TryGetResultMessage(response)
                ?? (_resourceMode
                    ? $"Mining resource `{_resourceId}`..."
                    : "Mining...")
        };
    }

    public async Task<(bool finished, CommandExecutionResult? result)> ContinueAsync(
        IRuntimeTransport client,
        GameState state)
    {
        if (_stopRequested)
            return FinishWithStopReason();

        if (!string.IsNullOrWhiteSpace(_completionMessage))
            return FinishWithCompletionMessage();

        if (_resourceMode)
        {
            if (state.CargoUsed >= state.CargoCapacity)
            {
                _completionMessage = "Mining complete.";
                return FinishWithCompletionMessage();
            }
        }
        else
        {
            bool navigatingToTarget = !string.IsNullOrWhiteSpace(_targetPoiId) &&
                                      !string.Equals(state.CurrentPOI?.Id, _targetPoiId, StringComparison.Ordinal);

            if (!navigatingToTarget && !IsClassicMineAvailable(state))
            {
                _completionMessage = "Mining complete.";
                return FinishWithCompletionMessage();
            }
        }

        var response = await ExecuteStepAsync(client, state);

        if (_stopRequested)
            return FinishWithStopReason();

        if (!string.IsNullOrWhiteSpace(_completionMessage))
            return FinishWithCompletionMessage();

        return (false, new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response)
        });
    }

    private async Task<JsonElement> ExecuteStepAsync(
        IRuntimeTransport client,
        GameState state)
    {
        return _resourceMode
            ? await ExecuteResourceStepAsync(client, state)
            : await ExecuteClassicMineStepAsync(client, state);
    }

    private async Task<JsonElement> ExecuteClassicMineStepAsync(
        IRuntimeTransport client,
        GameState state)
    {
        if (!string.IsNullOrWhiteSpace(_targetPoiId) &&
            !string.Equals(state.CurrentPOI?.Id, _targetPoiId, StringComparison.Ordinal))
        {
            if (state.Docked)
            {
                await client.ExecuteCommandAsync("undock");
                return default;
            }

            await client.ExecuteCommandAsync("travel", new { target_poi = _targetPoiId });
            return default;
        }

        if (state.Docked)
        {
            await client.ExecuteCommandAsync("undock");
            return default;
        }

        JsonElement response = (await client.ExecuteCommandAsync("mine")).Payload;
        CaptureStopReasonFromResponse(response);
        return response;
    }

    private async Task<JsonElement> ExecuteResourceStepAsync(
        IRuntimeTransport client,
        GameState state)
    {
        if (string.IsNullOrWhiteSpace(_resourceId))
        {
            _stopRequested = true;
            _stopReason = "Mining stopped: missing resource target.";
            return default;
        }

        if (state.CargoUsed >= state.CargoCapacity)
        {
            _completionMessage = "Mining complete.";
            return default;
        }

        if (CurrentPoiHasResource(state))
        {
            if (!state.CurrentPOI.IsMiningTarget)
            {
                _stopRequested = true;
                _stopReason = $"Found `{_resourceId}` at `{state.CurrentPOI.Id}`, but POI is not mineable.";
                return default;
            }

            if (state.Docked)
            {
                await client.ExecuteCommandAsync("undock");
                return default;
            }

            JsonElement mineResponse = (await client.ExecuteCommandAsync("mine")).Payload;
            CaptureStopReasonFromResponse(mineResponse);
            return mineResponse;
        }

        if (!string.IsNullOrWhiteSpace(_targetPoiId) && !string.IsNullOrWhiteSpace(_targetSystemId))
            return await ContinueToKnownTargetAsync(client, state);

        return await ContinueBfsExplorationAsync(client, state);
    }

    private async Task<JsonElement> ContinueToKnownTargetAsync(
        IRuntimeTransport client,
        GameState state)
    {
        if (!string.Equals(state.System, _targetSystemId, StringComparison.Ordinal))
        {
            if (state.Docked)
            {
                await client.ExecuteCommandAsync("undock");
                return default;
            }

            string? nextHop = await ResolveNextHopAsync(client, state, _targetSystemId!);
            if (string.IsNullOrWhiteSpace(nextHop))
            {
                _targetPoiId = null;
                _targetSystemId = null;
                InitializeBfsQueue(state);
                return default;
            }

            await client.ExecuteCommandAsync("jump", new { target_system = nextHop });
            return default;
        }

        if (string.Equals(state.CurrentPOI.Id, _targetPoiId, StringComparison.Ordinal))
        {
            _targetPoiId = null;
            _targetSystemId = null;

            if (CurrentPoiHasResource(state))
                return default;

            InitializeBfsQueue(state);
            return default;
        }

        if (state.Docked)
        {
            await client.ExecuteCommandAsync("undock");
            return default;
        }

        await client.ExecuteCommandAsync("travel", new { target_poi = _targetPoiId });
        return default;
    }

    private async Task<JsonElement> ContinueBfsExplorationAsync(
        IRuntimeTransport client,
        GameState state)
    {
        var currentMineable = GetCurrentSystemMineablePois(state);

        var uncheckedMineable = currentMineable
            .Where(p => !_checkedPoiIds.Contains(p.Id))
            .ToList();

        if (state.CurrentPOI.IsMiningTarget &&
            !_checkedPoiIds.Contains(state.CurrentPOI.Id))
        {
            _checkedPoiIds.Add(state.CurrentPOI.Id);
            RememberCheckedPoi(state.CurrentPOI.Id);
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
                await client.ExecuteCommandAsync("undock");
                return default;
            }

            await client.ExecuteCommandAsync("travel", new { target_poi = nextMineablePoi.Id });
            return default;
        }

        _exploredSystems.Add(state.System);
        RememberExploredSystem(state.System);

        while (_bfsSystems.Count > 0)
        {
            string candidate = _bfsSystems.Peek();

            if (_exploredSystems.Contains(candidate))
            {
                _bfsSystems.Dequeue();
                continue;
            }

            if (string.Equals(candidate, state.System, StringComparison.Ordinal))
                return default;

            if (state.Docked)
            {
                await client.ExecuteCommandAsync("undock");
                return default;
            }

            string? nextHop = await ResolveNextHopAsync(client, state, candidate);
            if (string.IsNullOrWhiteSpace(nextHop))
            {
                _exploredSystems.Add(candidate);
                _bfsSystems.Dequeue();
                continue;
            }

            await client.ExecuteCommandAsync("jump", new { target_system = nextHop });
            return default;
        }

        _completionMessage = $"Mining complete: `{_resourceId}` not found in explored mineable POIs.";
        return default;
    }

    private void ResetState()
    {
        _stopRequested = false;
        _stopReason = null;
        _completionMessage = null;

        _resourceMode = false;
        _resourceId = null;

        _targetPoiId = null;
        _targetSystemId = null;

        _bfsSystems = new Queue<string>();
        _exploredSystems.Clear();
        _checkedPoiIds.Clear();
    }

    private (bool finished, CommandExecutionResult? result) FinishWithStopReason()
    {
        var reason = _stopReason ?? "Mining stopped.";
        _stopRequested = false;
        _stopReason = null;

        return (true, new CommandExecutionResult
        {
            ResultMessage = reason
        });
    }

    private (bool finished, CommandExecutionResult? result) FinishWithCompletionMessage()
    {
        var message = _completionMessage ?? "Mining complete.";
        _completionMessage = null;

        return (true, new CommandExecutionResult
        {
            ResultMessage = message
        });
    }

    private static readonly HashSet<string> SupportedMiningTypes =
        new(StringComparer.Ordinal)
        {
            "asteroid_belt",
            "asteroid",
            "gas_cloud",
            "ice_field"
        };

    private static readonly string[] DefaultMiningPriority =
    {
        "asteroid_belt",
        "asteroid",
        "ice_field",
        "gas_cloud"
    };

    private static string? NormalizeMiningType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var token = raw.Trim().ToLowerInvariant();
        return SupportedMiningTypes.Contains(token) ? token : null;
    }

    private static POIInfo? SelectDefaultMiningPoi(GameState state)
    {
        foreach (var type in DefaultMiningPriority)
        {
            var match = state.POIs
                .Where(p => string.Equals(p.Type, type, StringComparison.Ordinal))
                .OrderByDescending(p => p.Online)
                .FirstOrDefault();

            if (match != null)
                return match;
        }

        return null;
    }

    private bool IsClassicMineAvailable(GameState state)
        => !state.Docked &&
           state.CurrentPOI?.IsMiningTarget == true &&
           state.CargoUsed < state.CargoCapacity;

    private void CaptureStopReasonFromResponse(JsonElement response)
    {
        if (!CommandJson.TryGetError(response, out var code, out var message))
            return;

        if (string.Equals(code, "depleted", StringComparison.OrdinalIgnoreCase))
            return;

        _stopRequested = true;
        _stopReason = $"Error: {(string.IsNullOrWhiteSpace(code) ? "" : $" ({code})")}: {message ?? "unknown"}";
    }

    private void InitializeBfsQueue(GameState state)
    {
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

    private static List<POIInfo> GetCurrentSystemMineablePois(GameState state)
    {
        var list = new List<POIInfo>();

        if (state.CurrentPOI?.IsMiningTarget == true)
            list.Add(state.CurrentPOI);

        if (state.POIs != null)
            list.AddRange(state.POIs.Where(p => p.IsMiningTarget));

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
            .Where(id => !_checkedPoiIds.Contains(id))
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

    private void LoadPersistentResourceExploration()
    {
        if (string.IsNullOrWhiteSpace(_resourceId))
            return;

        lock (ExplorationSync)
        {
            if (ExhaustedPoisByResource.TryGetValue(_resourceId, out var checkedPois))
                _checkedPoiIds.UnionWith(checkedPois);

            if (ExploredSystemsByResource.TryGetValue(_resourceId, out var exploredSystems))
                _exploredSystems.UnionWith(exploredSystems);
        }
    }

    private void RememberCheckedPoi(string poiId)
    {
        if (!_resourceMode ||
            string.IsNullOrWhiteSpace(_resourceId) ||
            string.IsNullOrWhiteSpace(poiId))
        {
            return;
        }

        lock (ExplorationSync)
        {
            if (!ExhaustedPoisByResource.TryGetValue(_resourceId, out var checkedPois))
            {
                checkedPois = new HashSet<string>(StringComparer.Ordinal);
                ExhaustedPoisByResource[_resourceId] = checkedPois;
            }

            checkedPois.Add(poiId);
        }
    }

    private void RememberExploredSystem(string systemId)
    {
        if (!_resourceMode ||
            string.IsNullOrWhiteSpace(_resourceId) ||
            string.IsNullOrWhiteSpace(systemId))
        {
            return;
        }

        lock (ExplorationSync)
        {
            if (!ExploredSystemsByResource.TryGetValue(_resourceId, out var exploredSystems))
            {
                exploredSystems = new HashSet<string>(StringComparer.Ordinal);
                ExploredSystemsByResource[_resourceId] = exploredSystems;
            }

            exploredSystems.Add(systemId);
        }
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
        IRuntimeTransport client,
        GameState state,
        string targetSystem)
    {
        JsonElement routeResult = (await client.FindRouteAsync(targetSystem)).Payload;
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

// =====================================================
// HALT (PAUSE AUTONOMY)
// =====================================================
