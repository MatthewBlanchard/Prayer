using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

internal static class MissionsTabRenderer
{
    public static string Build(
        IReadOnlyList<MissionPromptOption> activeMissionPrompts,
        IReadOnlyList<MissionPromptOption> availableMissionPrompts)
    {
        var active = (activeMissionPrompts ?? Array.Empty<MissionPromptOption>())
            .Where(m => m != null)
            .ToList();
        var availableOptions = (availableMissionPrompts ?? Array.Empty<MissionPromptOption>())
            .Where(m => m != null)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("<section class='space-page'>");
        sb.AppendLine("<div class='space-header'>");
        sb.AppendLine("<h4 class='space-title'>Missions</h4>");
        sb.Append("<div class='space-subtitle'>Active ")
            .Append(active.Count)
            .Append(" • Available ")
            .Append(availableOptions.Count)
            .AppendLine("</div>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div class='space-grid'>");
        sb.AppendLine("<section class='space-panel'>");
        sb.AppendLine("<div class='space-panel-title'>Active Missions</div>");
        if (active.Count == 0)
        {
            sb.AppendLine("<div class='small'>(none)</div>");
        }
        else
        {
            foreach (var mission in active)
            {
                var title = string.IsNullOrWhiteSpace(mission.Label) ? mission.MissionId : mission.Label;
                sb.Append("<div class='mission-item'><div class='mission-title'>")
                    .Append(E(title ?? string.Empty))
                    .AppendLine("</div>");
                if (!string.IsNullOrWhiteSpace(mission.Prompt))
                {
                    sb.Append("<div class='mission-body'>")
                        .Append(E(mission.Prompt))
                        .AppendLine("</div>");
                }
                sb.AppendLine("<div class='mission-actions'>");
                AppendUsePromptButton(sb, mission.Prompt, mission.MissionId);
                if (!string.IsNullOrWhiteSpace(mission.IssuingPoiId))
                {
                    sb.Append("<form class='space-chip-form' hx-post='api/control-input' hx-swap='none' hx-on::after-request='window.executeIfOk(event)'>")
                        .Append("<input type='hidden' name='script' value='go ").Append(E(mission.IssuingPoiId!)).Append(";'>")
                        .Append("<button type='submit' class='space-chip'>Go to ")
                        .Append(E(mission.IssuingPoiId!))
                        .AppendLine("</button></form>");
                }
                if (!string.IsNullOrWhiteSpace(mission.MissionId))
                {
                    sb.Append("<form class='space-chip-form' hx-post='api/control-input' hx-swap='none' hx-on::after-request='window.executeIfOk(event)'>")
                        .Append("<input type='hidden' name='script' value='abandon_mission ").Append(E(mission.MissionId)).Append(";'>")
                        .AppendLine("<button type='submit' class='space-chip mission-chip-danger'>Abandon</button></form>");
                }
                sb.AppendLine("</div>");
                sb.AppendLine("</div>");
            }
        }
        sb.AppendLine("</section>");

        sb.AppendLine("<section class='space-panel'>");
        sb.AppendLine("<div class='space-panel-title'>Available Missions</div>");
        if (availableOptions.Count == 0)
        {
            sb.AppendLine("<div class='small'>(none)</div>");
        }
        else
        {
            foreach (var mission in availableOptions)
                AppendAvailableMissionCard(sb, mission);
        }
        sb.AppendLine("</section>");
        sb.AppendLine("</div>");
        sb.AppendLine("</section>");
        return sb.ToString();
    }

    private static void AppendAvailableMissionCard(StringBuilder sb, MissionPromptOption mission)
    {
        var title = string.IsNullOrWhiteSpace(mission.Label) ? mission.MissionId : mission.Label;
        sb.Append("<div class='mission-item'><div class='mission-title'>")
            .Append(E(title ?? string.Empty))
            .AppendLine("</div>");

        if (!string.IsNullOrWhiteSpace(mission.Prompt))
        {
            sb.Append("<div class='mission-body'>")
                .Append(E(mission.Prompt))
                .AppendLine("</div>");
        }

        sb.AppendLine("<div class='mission-actions'>");
        if (!string.IsNullOrWhiteSpace(mission.MissionId))
        {
            sb.Append("<form class='space-chip-form' hx-post='api/control-input' hx-swap='none' hx-on::after-request='window.executeIfOk(event)'>")
                .Append("<input type='hidden' name='script' value='accept_mission ").Append(E(mission.MissionId)).Append(";'>")
                .AppendLine("<button type='submit' class='space-chip'>Accept</button></form>");
        }
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");
    }

    private static void AppendUsePromptButton(
        StringBuilder sb,
        string? prompt,
        string? missionId)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return;

        var shortMissionId = GetShortMissionId(missionId);
        sb.Append("<button type='button' class='space-chip mission-use-prompt' data-mission-prompt='")
            .Append(E(prompt))
            .Append("' data-mission-id='")
            .Append(E(shortMissionId))
            .AppendLine("'>Use Prompt</button>");
    }

    private static string GetShortMissionId(string? missionId)
    {
        var value = (missionId ?? string.Empty).Trim();
        if (value.Length <= 6)
            return value;
        return value[..6];
    }

    private static string E(string value) => WebUtility.HtmlEncode(value ?? "");
}
