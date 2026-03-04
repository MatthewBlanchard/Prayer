using System;
using System.Collections.Generic;

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
                objective.Trim()));
        }

        return options;
    }
}
