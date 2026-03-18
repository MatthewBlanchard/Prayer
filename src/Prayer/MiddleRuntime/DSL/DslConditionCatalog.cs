using System;
using System.Collections.Generic;
using System.Text;

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
        new("CARGO_PCT", [],         (state, _)    => ResolveCargoPercent(state)),
        new("CARGO",   ["item_id"],  (state, args) => ResolveItemCount(state.Ship.Cargo, args)),
        new("STASH",   ["poi_id", "item_id"],  (state, args) => ResolveStashCount(state, args)),
        new("MINED",   ["item_id"],  (state, args) => ResolveTrackedCount(state.ScriptMinedByItem, args)),
        new("STASHED", ["item_id"],  (state, args) => ResolveTrackedCount(state.ScriptStashedByItem, args)),
    };

    private static bool IsMissionComplete(GameState state, IReadOnlyList<string> args)
    {
        if (args.Count == 0 || string.IsNullOrWhiteSpace(args[0]))
            return false;

        var prefix = args[0];
        foreach (var mission in state.ActiveMissions ?? Array.Empty<MissionInfo>())
        {
            if (mission == null) continue;
            if (MatchesMissionPrefix(mission, prefix))
                return mission.Completed;
        }

        throw new InvalidOperationException(
            $"MISSION_COMPLETE({prefix}) could not find a matching active mission. " +
            "Use mission UUID, mission_id/template_id, or underscored title token.");
    }

    private static bool MatchesMissionPrefix(MissionInfo mission, string prefix)
    {
        return MatchesPrefix(mission.Id, prefix) ||
               MatchesPrefix(mission.MissionId, prefix) ||
               MatchesPrefix(mission.TemplateId, prefix) ||
               MatchesPrefix(ToSnakeCaseToken(mission.Title), prefix);
    }

    private static bool MatchesPrefix(string? id, string prefix)
    {
        var s = (id ?? string.Empty).Trim();
        return s.Length > 0 && s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string ToSnakeCaseToken(string? value)
    {
        var input = (value ?? string.Empty).Trim();
        if (input.Length == 0)
            return string.Empty;

        var sb = new StringBuilder(input.Length);
        bool lastWasUnderscore = false;
        foreach (var ch in input)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasUnderscore = false;
                continue;
            }

            if (!lastWasUnderscore)
            {
                sb.Append('_');
                lastWasUnderscore = true;
            }
        }

        return sb.ToString().Trim('_');
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

    private static int ResolveTrackedCount(Dictionary<string, int>? dict, IReadOnlyList<string> args)
    {
        if (dict == null || args.Count == 0 || string.IsNullOrWhiteSpace(args[0]))
            return 0;

        return dict.TryGetValue(args[0], out var value)
            ? Math.Max(0, value)
            : 0;
    }

    private static int ResolveFuelPercent(GameState state)
    {
        var maxFuel = state.Ship.MaxFuel;
        if (maxFuel <= 0) return 0;
        return Math.Clamp((state.Ship.Fuel * 100) / maxFuel, 0, 100);
    }

    private static int ResolveCargoPercent(GameState state)
    {
        var cargoCapacity = state.Ship.CargoCapacity;
        if (cargoCapacity <= 0) return 0;
        return Math.Clamp((state.Ship.CargoUsed * 100) / cargoCapacity, 0, 100);
    }
}
