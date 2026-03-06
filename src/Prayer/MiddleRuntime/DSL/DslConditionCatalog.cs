using System;
using System.Collections.Generic;
using System.Linq;

public sealed record DslBooleanPredicate(
    string Name,
    Func<GameState, bool> Evaluate);

public sealed record DslNumericPredicate(
    string Name,
    Func<GameState, int> Resolve);

public static class DslConditionCatalog
{
    public static IReadOnlyList<DslBooleanPredicate> BooleanPredicates { get; } = new List<DslBooleanPredicate>
    {
        new(
            "MISSION_COMPLETE",
            state =>
            {
                var missions = state.ActiveMissions ?? Array.Empty<MissionInfo>();
                return missions.Length == 0 || missions.Any(m => m != null && m.Completed);
            })
    };

    public static IReadOnlyList<DslNumericPredicate> NumericPredicates { get; } = new List<DslNumericPredicate>
    {
        new("FUEL", ResolveFuelPercent),
        new("CREDITS", state => state.Credits),
    };

    private static int ResolveFuelPercent(GameState state)
    {
        var maxFuel = state.Ship.MaxFuel;
        if (maxFuel <= 0)
            return 0;

        var fuel = state.Ship.Fuel;
        var percent = (fuel * 100) / maxFuel;
        return Math.Clamp(percent, 0, 100);
    }
}
