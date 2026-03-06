using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

internal static class MissionsTabRenderer
{
    public static string Build(
        string? missionsStateMarkdown,
        IReadOnlyList<MissionPromptOption> activeMissionPrompts,
        IReadOnlyList<MissionPromptOption> availableMissionPrompts)
    {
        var active = (activeMissionPrompts ?? Array.Empty<MissionPromptOption>())
            .Where(m => m != null)
            .ToList();
        var availableOptions = (availableMissionPrompts ?? Array.Empty<MissionPromptOption>())
            .Where(m => m != null)
            .ToList();
        var availableFallback = ParseAvailableMissions(missionsStateMarkdown);

        var sb = new StringBuilder();
        sb.AppendLine("<section class='space-page'>");
        sb.AppendLine("<div class='space-header'>");
        sb.AppendLine("<h4 class='space-title'>Missions</h4>");
        sb.Append("<div class='space-subtitle'>Active ")
            .Append(active.Count)
            .Append(" • Available ")
            .Append(availableOptions.Count > 0 ? availableOptions.Count : availableFallback.Count)
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
                AppendUsePromptButton(sb, mission.Prompt);
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
        if (availableOptions.Count == 0 && availableFallback.Count == 0)
        {
            sb.AppendLine("<div class='small'>(none)</div>");
        }
        else if (availableOptions.Count > 0)
        {
            foreach (var mission in availableOptions)
                AppendAvailableMissionCard(sb, mission);
        }
        else
        {
            foreach (var mission in availableFallback)
            {
                var (title, body) = SplitMissionLine(mission);
                sb.Append("<div class='mission-item'><div class='mission-title'>")
                    .Append(E(title))
                    .AppendLine("</div>");
                if (!string.IsNullOrWhiteSpace(body))
                {
                    sb.Append("<div class='mission-body'>")
                        .Append(E(body))
                        .AppendLine("</div>");
                }
                sb.AppendLine("</div>");
            }
        }
        sb.AppendLine("</section>");
        sb.AppendLine("</div>");
        sb.AppendLine("</section>");
        return sb.ToString();
    }

    private static List<string> ParseAvailableMissions(string? markdown)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(markdown))
            return result;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        bool inAvailable = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Equals("AVAILABLE MISSIONS", StringComparison.OrdinalIgnoreCase))
            {
                inAvailable = true;
                continue;
            }

            if (!inAvailable)
                continue;

            if (!line.StartsWith("- ", StringComparison.Ordinal))
                continue;

            var mission = line[2..].Trim();
            if (mission.Length == 0 || mission.Equals("(none)", StringComparison.OrdinalIgnoreCase))
                continue;

            result.Add(mission);
        }

        return result;
    }

    private static (string Title, string Body) SplitMissionLine(string mission)
    {
        var value = (mission ?? string.Empty).Trim();
        if (value.Length == 0)
            return ("(unknown mission)", string.Empty);

        var separatorIndex = value.IndexOf(':');
        if (separatorIndex <= 0 || separatorIndex >= value.Length - 1)
            return (value, string.Empty);

        var title = value[..separatorIndex].Trim();
        var body = value[(separatorIndex + 1)..].Trim();
        return (
            string.IsNullOrWhiteSpace(title) ? value : title,
            body);
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

    private static void AppendUsePromptButton(StringBuilder sb, string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return;

        sb.Append("<button type='button' class='space-chip mission-use-prompt' data-mission-prompt='")
            .Append(E(prompt))
            .AppendLine("'>Use Prompt</button>");
    }

    private static string E(string value) => WebUtility.HtmlEncode(value ?? "");
}
