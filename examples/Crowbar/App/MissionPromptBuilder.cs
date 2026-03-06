using System;
using System.Collections.Generic;
using System.Linq;

public static class MissionPromptBuilder
{
    public static IReadOnlyList<MissionPromptOption> BuildOptions(GameState state)
    {
        if (state.ActiveMissions == null || state.ActiveMissions.Length == 0)
            return Array.Empty<MissionPromptOption>();

        var options = new List<MissionPromptOption>();
        foreach (var mission in state.ActiveMissions)
        {
            if (mission == null)
                continue;

            string objective = !string.IsNullOrWhiteSpace(mission.ObjectivesSummary)
                ? mission.ObjectivesSummary
                : (!string.IsNullOrWhiteSpace(mission.ProgressText)
                    ? mission.ProgressText
                    : mission.Description);

            if (string.IsNullOrWhiteSpace(objective))
                continue;

            string missionId = !string.IsNullOrWhiteSpace(mission.MissionId)
                ? mission.MissionId
                : mission.Id;
            string title = !string.IsNullOrWhiteSpace(mission.Title)
                ? mission.Title
                : missionId;
            string label = string.IsNullOrWhiteSpace(missionId)
                ? title
                : $"{title} ({missionId})";

            options.Add(new MissionPromptOption(
                missionId ?? string.Empty,
                label,
                objective.Trim(),
                ResolveIssuingPoiId(state, mission)));
        }

        return options;
    }

    private static string ResolveIssuingPoiId(GameState state, MissionInfo mission)
    {
        var directBaseId = (mission.IssuingBaseId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(directBaseId))
        {
            var poiFromBaseId = (state.POIs ?? Array.Empty<POIInfo>())
                .FirstOrDefault(p => string.Equals(p.BaseId ?? "", directBaseId, StringComparison.OrdinalIgnoreCase));
            if (poiFromBaseId != null && !string.IsNullOrWhiteSpace(poiFromBaseId.Id))
                return poiFromBaseId.Id.Trim();
        }

        var issuingBase = (mission.IssuingBase ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(issuingBase))
            return directBaseId;

        var pois = state.POIs ?? Array.Empty<POIInfo>();
        var byPoiId = pois.FirstOrDefault(p =>
            string.Equals(p.Id ?? "", issuingBase, StringComparison.OrdinalIgnoreCase));
        if (byPoiId != null && !string.IsNullOrWhiteSpace(byPoiId.Id))
            return byPoiId.Id.Trim();

        var byBaseId = pois.FirstOrDefault(p =>
            string.Equals(p.BaseId ?? "", issuingBase, StringComparison.OrdinalIgnoreCase));
        if (byBaseId != null && !string.IsNullOrWhiteSpace(byBaseId.Id))
            return byBaseId.Id.Trim();

        var byName = pois.FirstOrDefault(p =>
            string.Equals(p.Name ?? "", issuingBase, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.BaseName ?? "", issuingBase, StringComparison.OrdinalIgnoreCase));
        if (byName != null && !string.IsNullOrWhiteSpace(byName.Id))
            return byName.Id.Trim();

        return directBaseId;
    }
}
