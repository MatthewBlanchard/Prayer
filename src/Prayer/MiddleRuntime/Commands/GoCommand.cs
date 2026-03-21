using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public class GoCommand : IMultiTurnCommand, IDslCommandGrammar
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
    private WormholeLink? _pendingWormholeJump; // set when we need to jump through a wormhole after traveling to entrance

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
        _pendingWormholeJump = null;
        client.SetActiveRoute(null);

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
        // Step 2 of wormhole traversal: we traveled to the entrance last tick, now jump through it
        if (_pendingWormholeJump != null)
        {
            var wh = _pendingWormholeJump;
            _pendingWormholeJump = null;

            // Wormhole traversal: jump(target_system=entrance_poi_id) — 1 fuel, 1 tick
            await client.ExecuteCommandAsync(
                "jump",
                new { target_system = wh.Id });
            _didMoveToTarget = true;
            return (false, null);
        }

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
            client.SetActiveRoute(null);
            return (true, new CommandExecutionResult
            {
                ResultMessage = $"Invalid go target: {target} is the current POI."
            });
        }

        if (targetIsCurrentSystem && string.IsNullOrWhiteSpace(poiTarget))
        {
            _target = null;
            client.SetActiveRoute(null);
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
            client.SetActiveRoute(null);
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

        RouteInfo? routeInfo = client.FindPath(state, systemTarget);
        client.SetActiveRoute(routeInfo);
        string? nextHop = routeInfo is { Hops.Count: > 0 } ? routeInfo.Hops[0] : null;

        if (string.IsNullOrWhiteSpace(nextHop))
        {
            _target = null;
            client.SetActiveRoute(null);
            return (true, new CommandExecutionResult
            {
                ResultMessage = $"No route found to {target}!",
                HaltScript = true
            });
        }

        // Check if next hop requires wormhole traversal (not a standard jump connection)
        bool isStandardJump = state.Systems?.Contains(nextHop) == true;
        if (!isStandardJump)
        {
            var wormholeLinks = state.Galaxy?.Knowledge?.WormholeLinksById;
            var now = DateTime.UtcNow;
            var wormhole = wormholeLinks?.Values.FirstOrDefault(w =>
                string.Equals(w.FromSystem, state.System, StringComparison.Ordinal) &&
                string.Equals(w.ToSystem, nextHop, StringComparison.Ordinal) &&
                (w.ExpiresAtUtc == null || w.ExpiresAtUtc > now));

            if (wormhole != null)
            {
                // Step 1: travel to the wormhole entrance POI
                // Step 2 (next tick): jump(target_system=entrance_poi_id)
                _pendingWormholeJump = wormhole;
                await client.ExecuteCommandAsync("travel", new { target_poi = wormhole.Id });
                _didMoveToTarget = true;
                return (false, null);
            }
        }

        await client.ExecuteCommandAsync(
            "jump",
            new { target_system = nextHop });
        _didMoveToTarget = true;

        return (false, null);
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
}

// =====================================================
// DOCK
// =====================================================
