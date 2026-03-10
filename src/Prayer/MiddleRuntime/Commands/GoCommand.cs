using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public class GoCommand : IMultiTurnCommand, IDslCommandGrammar, IActiveRouteSource
{
    public string Name => "go";
    public DslCommandSyntax GetDslSyntax() => new(
        ArgSpecs: new[]
        {
            new DslArgumentSpec(
                DslArgKind.System | DslArgKind.Any,
                Required: true,
                ArgTypeWeights: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["system"] = 1.08,
                    ["poi"] = 1.00
                })
        });

    private string? _target;
    private string? _resolvedSystemTarget;
    private string? _resolvedPoiTarget;
    private bool _didMoveToTarget;

    private List<string>? _plannedHops;
    private int _plannedTotalJumps;
    private int _fuelPerJump;
    private int _estimatedFuel;
    private int _fuelAvailable;

    public bool IsAvailable(GameState state)
    {
        if (state.Systems.Length > 0)
            return true;

        if (state.POIs.Length > 0)
            return true;

        return !string.IsNullOrWhiteSpace(state.CurrentPOI?.Id);
    }

    public string BuildHelp(GameState state)
        => "- go <identifier> → go to a POI or any system name; auto-pathfinds (not current POI)";

    public async Task<(bool finished, CommandExecutionResult? result)> StartAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        _target = cmd.Arg1?.Trim();
        _resolvedSystemTarget = null;
        _resolvedPoiTarget = null;
        _didMoveToTarget = false;
        _plannedHops = null;

        if (string.IsNullOrWhiteSpace(_target))
        {
            return (true, new CommandExecutionResult
            {
                ResultMessage = "No go target provided."
            });
        }

        var resolved = await ResolveTargetAsync(client, state, _target);
        if (!resolved.found)
        {
            string unknownTarget = _target;
            _target = null;
            return (true, new CommandExecutionResult
            {
                ResultMessage = $"Unknown go target: {unknownTarget}. Target is not in the known galaxy map cache."
            });
        }

        _resolvedSystemTarget = resolved.systemId;
        _resolvedPoiTarget = resolved.poiId;

        return await ExecuteNextStepAsync(client, state);
    }

    public async Task<(bool finished, CommandExecutionResult? result)> ContinueAsync(
        IRuntimeTransport client,
        GameState state)
    {
        if (string.IsNullOrWhiteSpace(_target))
        {
            return (true, new CommandExecutionResult
            {
                ResultMessage = "Go complete."
            });
        }

        return await ExecuteNextStepAsync(client, state);
    }

    private async Task<(bool finished, CommandExecutionResult? result)> ExecuteNextStepAsync(
        IRuntimeTransport client,
        GameState state)
    {
        string target = _target!;
        string systemTarget = _resolvedSystemTarget ?? target;
        string? poiTarget = _resolvedPoiTarget;

        bool targetIsCurrentSystem = string.Equals(state.System, systemTarget, StringComparison.Ordinal);
        bool targetIsCurrentPoi = !string.IsNullOrWhiteSpace(poiTarget) &&
                                  string.Equals(state.CurrentPOI.Id, poiTarget, StringComparison.Ordinal);
        bool targetIsPoiInCurrentSystem = !string.IsNullOrWhiteSpace(poiTarget) &&
                                          targetIsCurrentSystem &&
                                          (targetIsCurrentPoi || state.POIs.Any(p => p.Id == poiTarget));

        if (targetIsCurrentPoi)
        {
            _target = null;
            _resolvedSystemTarget = null;
            _resolvedPoiTarget = null;
            _plannedHops = null;
            return (true, new CommandExecutionResult
            {
                ResultMessage = $"Invalid go target: {target} is the current POI."
            });
        }

        if (targetIsCurrentSystem && string.IsNullOrWhiteSpace(poiTarget))
        {
            _target = null;
            _resolvedSystemTarget = null;
            _resolvedPoiTarget = null;
            _plannedHops = null;
            return (true, new CommandExecutionResult
            {
                ResultMessage = _didMoveToTarget
                    ? $"Arrived at {target}."
                    : $"Already at {target}."
            });
        }

        if (targetIsPoiInCurrentSystem)
        {
            if (state.Docked)
            {
                await client.ExecuteCommandAsync("undock");
                return (false, null);
            }

            JsonElement travel = (await client.ExecuteCommandAsync(
                "travel",
                new { target_poi = poiTarget })).Payload;

            _target = null;
            _resolvedSystemTarget = null;
            _resolvedPoiTarget = null;
            _plannedHops = null;
            _didMoveToTarget = true;
            return (true, new CommandExecutionResult
            {
                ResultMessage = CommandJson.TryGetResultMessage(travel) ?? $"Arrived at {target}."
            });
        }

        // Otherwise move across systems first.
        if (state.Docked)
        {
            await client.ExecuteCommandAsync("undock");
            return (false, null);
        }

        JsonElement routeResult = (await client.FindRouteAsync(systemTarget)).Payload;
        StoreRouteInfo(routeResult, state.System);
        string? nextHop = TryGetNextHop(routeResult, state.System, systemTarget);

        if (string.IsNullOrWhiteSpace(nextHop))
        {
            if (state.Systems.Contains(systemTarget))
            {
                nextHop = systemTarget;
            }
            else
            {
                string connected = state.Systems.Length > 0
                    ? string.Join(", ", state.Systems)
                    : "none";

                _target = null;
                _resolvedSystemTarget = null;
                _resolvedPoiTarget = null;
                _plannedHops = null;
                return (true, new CommandExecutionResult
                {
                    ResultMessage = $"No route found to {target}!",
                    HaltScript = true
                });
            }
        }

        await client.ExecuteCommandAsync(
            "jump",
            new { target_system = nextHop });
        _didMoveToTarget = true;

        return (false, null);
    }

    public ActiveGoRoute? GetActiveRoute()
    {
        if (_target == null || _plannedHops == null)
            return null;

        return new ActiveGoRoute(
            _resolvedSystemTarget ?? _target,
            _plannedHops,
            _plannedTotalJumps,
            _fuelPerJump,
            _estimatedFuel,
            _fuelAvailable);
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

    private static async Task<(bool found, string? systemId, string? poiId)> ResolveTargetAsync(
        IRuntimeTransport client,
        GameState state,
        string rawTarget)
    {
        string target = rawTarget.Trim();

        if (string.Equals(state.System, target, StringComparison.Ordinal))
            return (true, state.System, null);

        if (state.Systems.Contains(target))
            return (true, target, null);

        var localPoi = state.POIs.FirstOrDefault(p => string.Equals(p.Id, target, StringComparison.Ordinal));
        if (localPoi != null)
            return (true, state.System, target);

        GalaxyMapSnapshot map = await client.GetMapSnapshotAsync();
        foreach (var systemObj in map.Systems)
        {
            string? systemId = systemObj.Id;

            if (string.IsNullOrWhiteSpace(systemId))
                continue;

            if (string.Equals(systemId, target, StringComparison.Ordinal))
                return (true, systemId, null);

            foreach (var poi in systemObj.Pois)
            {
                string? poiId = poi.Id;

                if (string.IsNullOrWhiteSpace(poiId))
                    continue;

                if (string.Equals(poiId, target, StringComparison.Ordinal))
                    return (true, systemId, poiId);
            }
        }

        return (false, null, null);
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

            if (route.Count == 0)
                continue;

            while (route.Count > 0 && string.Equals(route[0], currentSystem, StringComparison.Ordinal))
                route.RemoveAt(0);

            if (route.Count == 0)
                continue;

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

        // Common API shape: { found: true, route: [ { system_id: "..." }, ... ] }
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

            if (entry.TryGetProperty("system_id", out var systemIdProp) &&
                systemIdProp.ValueKind == JsonValueKind.String)
            {
                var id = systemIdProp.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                    systems.Add(id);
                continue;
            }

            if (entry.TryGetProperty("id", out var idProp) &&
                idProp.ValueKind == JsonValueKind.String)
            {
                var id = idProp.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                    systems.Add(id);
            }
        }

        return systems;
    }
}

// =====================================================
// DOCK
// =====================================================
