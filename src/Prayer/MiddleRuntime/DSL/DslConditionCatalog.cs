using System;
using System.Collections.Generic;

public sealed record DslBooleanPredicate(
    string Name,
    string[] ParamNames,
    Func<GameState, IReadOnlyList<string>, bool> Evaluate);

public sealed record DslNumericPredicate(
    string Name,
    string[] ParamNames,
    Func<GameState, IReadOnlyList<string>, int> Resolve);

public static class DslConditionCatalog
{
    public static IReadOnlyList<DslBooleanPredicate> BooleanPredicates { get; } = new List<DslBooleanPredicate>
    {
        new("MISSION_COMPLETE", ["mission_id"], IsMissionComplete),
    };

    public static IReadOnlyList<DslNumericPredicate> NumericPredicates { get; } = new List<DslNumericPredicate>
    {
        new("FUEL",    [],           (state, _)    => ResolveFuelPercent(state)),
        new("CREDITS", [],           (state, _)    => state.Credits),
        new("CARGO",   ["item_id"],  (state, args) => ResolveItemCount(state.Ship.Cargo, args)),
        new("STASH",   ["poi_id", "item_id"],  (state, args) => ResolveStashCount(state, args)),
    };

    private static bool IsMissionComplete(GameState state, IReadOnlyList<string> args)
    {
        if (args.Count == 0 || string.IsNullOrWhiteSpace(args[0]))
            return false;

        var prefix = args[0];
        foreach (var mission in state.ActiveMissions ?? Array.Empty<MissionInfo>())
        {
            if (mission == null) continue;
            if (MatchesPrefix(mission.Id, prefix) || MatchesPrefix(mission.MissionId, prefix))
                return mission.Completed;
        }
        return false;
    }

    private static bool MatchesPrefix(string? id, string prefix)
    {
        var s = (id ?? string.Empty).Trim();
        return s.Length > 0 && s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static int ResolveItemCount(Dictionary<string, ItemStack> dict, IReadOnlyList<string> args)
        => args.Count > 0 && dict.TryGetValue(args[0], out var stack) ? stack.Quantity : 0;

    private static int ResolveStashCount(GameState state, IReadOnlyList<string> args)
    {
        if (args.Count < 2)
            return 0;

        string poiId = args[0];
        var itemArgs = new[] { args[1] };

        if (state.Docked && state.CurrentPOI.IsStation &&
            string.Equals(state.CurrentPOI.Id, poiId, StringComparison.OrdinalIgnoreCase))
            return ResolveItemCount(state.StorageItems, itemArgs);

        if (state.StorageCacheByPoi.TryGetValue(poiId, out var cached))
            return ResolveItemCount(cached, itemArgs);

        return 0;
    }

    private static int ResolveFuelPercent(GameState state)
    {
        var maxFuel = state.Ship.MaxFuel;
        if (maxFuel <= 0) return 0;
        return Math.Clamp((state.Ship.Fuel * 100) / maxFuel, 0, 100);
    }
}
