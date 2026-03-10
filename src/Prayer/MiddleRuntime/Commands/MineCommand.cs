using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public class MineCommand : IMultiTurnCommand, IDslCommandGrammar, IActiveRouteSource
{
    private static readonly TimeSpan DepletedWait = TimeSpan.FromSeconds(10);
    private static readonly HashSet<string> MineablePoiTypes =
        new(StringComparer.Ordinal)
        {
            "asteroid_belt",
            "asteroid",
            "gas_cloud",
            "ice_field"
        };

    public string Name => "mine";
    public DslCommandSyntax GetDslSyntax() => new(
        ArgSpecs: new[]
        {
            new DslArgumentSpec(DslArgKind.Item, Required: false)
        });

    private string? _resourceId;
    private bool _stopRequested;
    private string? _stopReason;
    private string? _completionMessage;
    private bool _haltOnFinish;
    private readonly HashSet<string> _excludedPoiIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _excludedSystems = new(StringComparer.Ordinal);

    private string? _routeTargetSystem;
    private List<string>? _plannedHops;
    private int _plannedTotalJumps;
    private int _fuelPerJump;
    private int _estimatedFuel;
    private int _fuelAvailable;

    public bool IsAvailable(GameState state)
        => !string.IsNullOrWhiteSpace(state.System);

    public string BuildHelp(GameState state)
        => "- mine [resourceId] → mine at nearest known mineable POI (or nearest known POI for resourceId)";

    public async Task<(bool finished, CommandExecutionResult? result)> StartAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        ResetState();
        _resourceId = string.IsNullOrWhiteSpace(cmd.Arg1) ? null : cmd.Arg1!.Trim();

        JsonElement response = await ExecuteStepAsync(client, state);

        if (_stopRequested)
            return FinishWithMessage(_stopReason ?? "Mining stopped.");
        if (!string.IsNullOrWhiteSpace(_completionMessage))
            return FinishWithMessage(_completionMessage!);

        return (false, new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response)
                ?? BuildProgressMessage()
        });
    }

    public async Task<(bool finished, CommandExecutionResult? result)> ContinueAsync(
        IRuntimeTransport client,
        GameState state)
    {
        if (_stopRequested)
            return FinishWithMessage(_stopReason ?? "Mining stopped.");
        if (!string.IsNullOrWhiteSpace(_completionMessage))
            return FinishWithMessage(_completionMessage!);

        JsonElement response = await ExecuteStepAsync(client, state);

        if (_stopRequested)
            return FinishWithMessage(_stopReason ?? "Mining stopped.");
        if (!string.IsNullOrWhiteSpace(_completionMessage))
            return FinishWithMessage(_completionMessage!);

        return (false, new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response)
                ?? BuildProgressMessage()
        });
    }

    private async Task<JsonElement> ExecuteStepAsync(
        IRuntimeTransport client,
        GameState state)
    {
        if (state.Ship.CargoUsed >= state.Ship.CargoCapacity)
        {
            _plannedHops = null;
            _completionMessage = "Mining complete.";
            return default;
        }

        if (state.Docked)
        {
            await client.ExecuteCommandAsync("undock");
            return default;
        }

        if (CurrentPoiCanMine(state))
        {
            JsonElement mineResponse = (await client.ExecuteCommandAsync("mine")).Payload;
            await WaitIfDepletedAsync(mineResponse);
            CaptureStopReasonFromResponse(mineResponse);
            return mineResponse;
        }

        if (state.CurrentPOI?.IsMiningTarget == true &&
            !string.IsNullOrWhiteSpace(state.CurrentPOI.Id))
        {
            _excludedPoiIds.Add(state.CurrentPOI.Id);
        }

        if (!TryResolveNearestKnownTarget(state, out string targetSystemId, out string targetPoiId))
        {
            _plannedHops = null;
            _completionMessage = string.IsNullOrWhiteSpace(_resourceId)
                ? "No minable POIs!"
                : $"No minable POIs for `{_resourceId}`!";
            _haltOnFinish = true;
            return default;
        }

        if (!string.Equals(state.System, targetSystemId, StringComparison.Ordinal))
        {
            string? nextHop = await ResolveNextHopAsync(client, state, targetSystemId);
            if (string.IsNullOrWhiteSpace(nextHop))
            {
                _plannedHops = null;
                _excludedSystems.Add(targetSystemId);
                return default;
            }

            await client.ExecuteCommandAsync("jump", new { target_system = nextHop });
            return default;
        }

        _plannedHops = null;
        await client.ExecuteCommandAsync("travel", new { target_poi = targetPoiId });
        return default;
    }

    public ActiveGoRoute? GetActiveRoute()
    {
        if (_plannedHops == null || _routeTargetSystem == null)
            return null;

        return new ActiveGoRoute(
            _routeTargetSystem,
            _plannedHops,
            _plannedTotalJumps,
            _fuelPerJump,
            _estimatedFuel,
            _fuelAvailable);
    }

    private void ResetState()
    {
        _resourceId = null;
        _stopRequested = false;
        _stopReason = null;
        _completionMessage = null;
        _haltOnFinish = false;
        _routeTargetSystem = null;
        _plannedHops = null;
        _excludedPoiIds.Clear();
        _excludedSystems.Clear();
    }

    private string BuildProgressMessage()
    {
        return string.IsNullOrWhiteSpace(_resourceId)
            ? "Mining nearest known spot..."
            : $"Mining nearest known `{_resourceId}` spot...";
    }

    private async Task<string?> ResolveNextHopAsync(
        IRuntimeTransport client,
        GameState state,
        string targetSystem)
    {
        JsonElement routeResult = (await client.FindRouteAsync(targetSystem)).Payload;
        StoreRouteInfo(routeResult, state.System, targetSystem);
        string? nextHop = TryGetNextHop(routeResult, state.System);
        if (!string.IsNullOrWhiteSpace(nextHop))
            return nextHop;

        return state.Systems.Contains(targetSystem, StringComparer.Ordinal)
            ? targetSystem
            : null;
    }

    private void StoreRouteInfo(JsonElement routeResult, string currentSystem, string targetSystem)
    {
        _routeTargetSystem = targetSystem;

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

    private bool TryResolveNearestKnownTarget(
        GameState state,
        out string systemId,
        out string poiId)
    {
        systemId = "";
        poiId = "";

        var poiSystemLookup = BuildPoiSystemLookup(state);
        var distanceBySystem = BuildSystemDistanceIndex(state);

        var candidatePois = GetCandidatePoiIds(state)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Where(id => !_excludedPoiIds.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        int bestDistance = int.MaxValue;
        string? bestSystem = null;
        string? bestPoi = null;

        foreach (var candidatePoi in candidatePois)
        {
            if (!poiSystemLookup.TryGetValue(candidatePoi, out var candidateSystem))
                continue;
            if (string.IsNullOrWhiteSpace(candidateSystem))
                continue;
            if (_excludedSystems.Contains(candidateSystem))
                continue;

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

    private IEnumerable<string> GetCandidatePoiIds(GameState state)
    {
        if (string.IsNullOrWhiteSpace(_resourceId))
            return GetKnownMineablePoiIds(state);

        var resourceIndex = state.Galaxy?.Resources?.PoisByResource;
        if (resourceIndex == null || resourceIndex.Count == 0)
            return Enumerable.Empty<string>();

        string? key = resourceIndex.Keys.FirstOrDefault(k =>
            string.Equals(k, _resourceId, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(key))
            return Enumerable.Empty<string>();

        return resourceIndex[key];
    }

    private static IEnumerable<string> GetKnownMineablePoiIds(GameState state)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        if (state.CurrentPOI?.IsMiningTarget == true && !string.IsNullOrWhiteSpace(state.CurrentPOI.Id))
            ids.Add(state.CurrentPOI.Id);

        foreach (var poi in state.POIs ?? Array.Empty<POIInfo>())
        {
            if (poi?.IsMiningTarget == true && !string.IsNullOrWhiteSpace(poi.Id))
                ids.Add(poi.Id);
        }

        foreach (var known in state.Galaxy?.Map?.KnownPois ?? new List<GalaxyKnownPoiInfo>())
        {
            if (string.IsNullOrWhiteSpace(known.Id) || string.IsNullOrWhiteSpace(known.Type))
                continue;

            if (MineablePoiTypes.Contains(known.Type))
                ids.Add(known.Id);
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

    private bool CurrentPoiCanMine(GameState state)
    {
        if (state.CurrentPOI?.IsMiningTarget != true)
            return false;

        if (string.IsNullOrWhiteSpace(_resourceId))
            return true;

        return state.CurrentPOI.Resources.Any(r =>
            !string.IsNullOrWhiteSpace(r.ResourceId) &&
            string.Equals(r.ResourceId, _resourceId, StringComparison.OrdinalIgnoreCase));
    }

    private void CaptureStopReasonFromResponse(JsonElement response)
    {
        if (!CommandJson.TryGetError(response, out var code, out var message))
            return;

        if (string.Equals(code, "depleted", StringComparison.OrdinalIgnoreCase))
            return;

        _stopRequested = true;
        _stopReason = $"Error: {(string.IsNullOrWhiteSpace(code) ? "" : $"({code}) ")}{message ?? "unknown"}";
    }

    private static Task WaitIfDepletedAsync(JsonElement response)
    {
        if (CommandJson.TryGetError(response, out var code, out _) &&
            string.Equals(code, "depleted", StringComparison.OrdinalIgnoreCase))
        {
            return Task.Delay(DepletedWait);
        }

        return Task.CompletedTask;
    }

    private (bool finished, CommandExecutionResult? result) FinishWithMessage(string message)
    {
        bool halt = _haltOnFinish;
        _stopRequested = false;
        _stopReason = null;
        _completionMessage = null;
        _haltOnFinish = false;
        return (true, new CommandExecutionResult { ResultMessage = message, HaltScript = halt });
    }
}
