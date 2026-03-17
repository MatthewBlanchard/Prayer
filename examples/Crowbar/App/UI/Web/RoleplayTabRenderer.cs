using System.Collections.Generic;
using System.Net;
using System.Text;

internal static class RoleplayTabRenderer
{
    public static string Build(bool isRunning, string statusMessage, string currentPersona, IReadOnlyList<string>? toolTrace)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<section class='space-page roleplay-page'>");

        // Persona input
        sb.AppendLine("<div class='space-panel script-block'>");
        sb.AppendLine("<div class='space-panel-title'>Persona / Objective</div>");
        sb.AppendLine("<form id='roleplay-form' hx-post='api/roleplay-start' hx-swap='none' class='list'>");
        sb.Append("<textarea name='persona' class='roleplay-persona-input' rows='8' placeholder='e.g. You are a scrappy asteroid miner. Mine iron ore and sell it at the nearest station.'>")
          .Append(E(currentPersona))
          .AppendLine("</textarea>");

        if (!isRunning)
        {
            sb.AppendLine("<button type='submit' class='execute-btn'>▶ Start</button>");
        }
        else
        {
            sb.AppendLine("<button type='submit' disabled>▶ Start</button>");
        }

        sb.AppendLine("</form>");
        sb.AppendLine("</div>");

        // Controls
        sb.AppendLine("<div class='space-panel script-block'>");
        sb.AppendLine("<div class='row script-actions'>");
        if (isRunning)
        {
            sb.AppendLine("<form hx-post='api/roleplay-stop' hx-swap='none'><button type='submit' title='Stop roleplay'>⏹️ Stop</button></form>");
        }
        else
        {
            sb.AppendLine("<span class='small'>Roleplay is not active.</span>");
        }
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");

        // Status
        sb.AppendLine("<div id='roleplay-status' class='space-panel script-block' hx-get='partial/roleplay-status' hx-trigger='load, every 1000ms' hx-swap='innerHTML'>");
        sb.Append(BuildStatusInner(isRunning, statusMessage, toolTrace));
        sb.AppendLine("</div>");

        sb.AppendLine("</section>");
        return sb.ToString();
    }

    public static string BuildStatusInner(bool isRunning, string? statusMessage, IReadOnlyList<string>? toolTrace)
    {
        var sb = new StringBuilder();
        sb.Append("<div class='space-panel-title'>Status</div>");
        if (isRunning)
            sb.Append("<span class='small roleplay-active'>● Active</span> ");
        else
            sb.Append("<span class='small roleplay-idle'>○ Idle</span> ");
        sb.AppendLine(E(statusMessage ?? "(no status)"));
        sb.AppendLine("<div class='space-panel-title'>Tool Calls</div>");
        sb.AppendLine("<pre class='log-pre'>");

        var lines = toolTrace ?? Array.Empty<string>();
        if (lines.Count == 0)
        {
            sb.AppendLine("(no tool calls yet)");
        }
        else
        {
            foreach (var line in lines)
                sb.AppendLine(E(line));
        }

        sb.AppendLine("</pre>");
        return sb.ToString();
    }

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
}
