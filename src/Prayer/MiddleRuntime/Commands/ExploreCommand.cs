using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public class ExploreCommand : IMultiTurnCommand
{
    public string Name => "explore";
    public DslCommandSyntax GetDslSyntax() => new();

    private ExplorationStateSnapshot _snapshot = new();
    private string? _targetSystemId;
    private bool _completed;
    private string? _completionMessage;
    private bool _haltOnFinish;

    public bool IsAvailable(GameState state)
        => !string.IsNullOrWhiteSpace(state.System);

    public string BuildHelp(GameState state)
        => "- explore → go to nearest unexplored system and gather system-level intel";

    public async Task<(bool finished, CommandExecutionResult? result)> StartAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        ResetRuntimeState(client);
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
            string? nextHop = ResolveNextHop(client, state, _targetSystemId);
            if (string.IsNullOrWhiteSpace(nextHop))
            {
                client.SetActiveRoute(null);
                _targetSystemId = SelectNearestUnexploredSystem(state);
                if (string.IsNullOrWhiteSpace(_targetSystemId))
                {
                    _completed = true;
                    _haltOnFinish = true;
                    _completionMessage = "No path found to any unexplored system.";
                }

                return;
            }

            await client.ExecuteCommandAsync("jump", new { target_system = nextHop });
            return;
        }

        client.SetActiveRoute(null);

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

        MarkSystemExplored(state.System);
        _completed = true;
        _completionMessage = $"Exploration complete: `{state.System}` explored.";
    }

    private void ResetRuntimeState(IRuntimeTransport client)
    {
        _snapshot = new ExplorationStateSnapshot();
        _targetSystemId = null;
        _completed = false;
        _completionMessage = null;
        _haltOnFinish = false;
        client.SetActiveRoute(null);
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
            if (IsSystemUnexplored(state, systemId))
                return systemId;
        }

        return null;
    }

    private static bool IsSystemUnexplored(GameState state, string systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
            return false;

        var knownPois = state.Galaxy?.Map?.KnownPois;
        if (knownPois == null || knownPois.Count == 0)
            return true;

        return !knownPois.Any(p => string.Equals(p.SystemId, systemId, StringComparison.Ordinal));
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

    private static string? ResolveNextHop(IRuntimeTransport client, GameState state, string targetSystem)
    {
        RouteInfo? route = client.FindPath(state, targetSystem);
        client.SetActiveRoute(route);
        return route is { Hops.Count: > 0 } ? route.Hops[0] : null;
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

    private static ExplorationStateSnapshot LoadSnapshot()
        => ExplorationStateStore.Load();

    private void SaveSnapshot()
        => ExplorationStateStore.Save(_snapshot);
}
