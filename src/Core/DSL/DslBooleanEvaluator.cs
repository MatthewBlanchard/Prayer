using System;
using System.Linq;

internal static class DslBooleanEvaluator
{
    public static bool TryEvaluate(string? token, GameState state, out bool value)
    {
        value = false;
        if (state == null)
            return false;

        var normalized = (token ?? string.Empty).Trim().ToUpperInvariant();
        switch (normalized)
        {
            case "MISSION_COMPLETE":
                var missions = state.ActiveMissions ?? Array.Empty<MissionInfo>();
                value = missions.Length == 0 || missions.Any(m => m != null && m.Completed);
                return true;
            default:
                return false;
        }
    }
}
