using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

internal static class SpaceTabRenderer
{
    public static string BuildStateStrip(string spaceStateMarkdown)
    {
        var vm = Parse(spaceStateMarkdown);
        var sb = new StringBuilder();
        sb.AppendLine("<div class='space-stats state-strip'>");
        AppendStatCard(sb, "Credits", vm.Credits);
        AppendBarStatCard(sb, "Fuel", vm.Fuel);
        AppendBarStatCard(sb, "Hull", vm.Hull);
        AppendBarStatCard(sb, "Shield", vm.Shield);
        AppendBarStatCard(sb, "Cargo", vm.Cargo);
        sb.AppendLine("</div>");
        return sb.ToString();
    }

    public static string Build(string spaceStateMarkdown, IReadOnlyList<string> connectedSystems)
    {
        var vm = Parse(spaceStateMarkdown);
        var systems = (connectedSystems ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Where(s => !string.Equals(s, vm.System, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("<section class='space-page'>");
        sb.AppendLine("<div class='space-header'>");
        sb.Append("<h4 class='space-title'>").Append(E(vm.SystemOrFallback)).AppendLine("</h4>");
        sb.Append("<div class='space-subtitle'>POI ").Append(E(vm.PoiOrFallback))
            .Append(" • ").Append(E(vm.DockedOrFallback)).AppendLine("</div>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div class='space-grid'>");
        sb.AppendLine("<section class='space-panel'>");
        sb.AppendLine("<div class='space-panel-title'>POIs</div>");
        if (vm.Pois.Count == 0)
        {
            sb.AppendLine("<div class='small'>(none)</div>");
        }
        else
        {
            foreach (var poi in vm.Pois)
                AppendGoChip(sb, poi.Target, poi.Label);
        }
        sb.AppendLine("</section>");

        sb.AppendLine("<section class='space-panel'>");
        sb.AppendLine("<div class='space-panel-title'>Connected Systems</div>");
        if (systems.Count == 0)
        {
            sb.AppendLine("<div class='small'>(none)</div>");
        }
        else
        {
            foreach (var system in systems)
                AppendGoChip(sb, system, system);
        }
        sb.AppendLine("</section>");
        sb.AppendLine("</div>");

        sb.AppendLine("<section class='space-panel'>");
        sb.AppendLine("<div class='space-panel-title'>Cargo Items</div>");
        if (vm.CargoItems.Count == 0)
            sb.AppendLine("<div class='small'>(empty)</div>");
        else
            AppendList(sb, vm.CargoItems);
        sb.AppendLine("</section>");

        sb.AppendLine("</section>");
        return sb.ToString();
    }

    private static void AppendStatCard(StringBuilder sb, string label, string value)
    {
        sb.Append("<div class='space-stat'><div class='space-stat-label'>")
            .Append(E(label))
            .Append("</div><div class='space-stat-value'>")
            .Append(E(string.IsNullOrWhiteSpace(value) ? "-" : value))
            .AppendLine("</div></div>");
    }

    private static void AppendBarStatCard(StringBuilder sb, string label, string value)
    {
        var ratio = TryParseRatio(value, out var current, out var max)
            ? Math.Clamp((double)current / Math.Max(1, max), 0.0d, 1.0d)
            : 0.0d;
        var widthPct = (int)Math.Round(ratio * 100.0d, MidpointRounding.AwayFromZero);
        var valueText = string.IsNullOrWhiteSpace(value) ? "-" : value;

        sb.Append("<div class='space-stat compact'><div class='space-stat-label'>")
            .Append(E(label))
            .Append("</div><div class='space-stat-row'><div class='space-stat-value'>")
            .Append(E(valueText))
            .AppendLine("</div></div>");

        sb.Append("<div class='space-meter' role='img' aria-label='")
            .Append(E($"{label}: {valueText} ({widthPct}%)"))
            .Append("'><div class='space-bar'><div class='space-bar-fill' style='width:")
            .Append(widthPct)
            .AppendLine("%'></div></div><div class='space-meter-pct'>")
            .Append(widthPct)
            .AppendLine("%</div></div></div>");
    }

    private static bool TryParseRatio(string value, out int current, out int max)
    {
        current = 0;
        max = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return false;

        if (!int.TryParse(parts[0], out current))
            return false;
        if (!int.TryParse(parts[1], out max))
            return false;

        return max > 0;
    }

    private static void AppendGoChip(StringBuilder sb, string target, string label)
    {
        sb.Append("<form class='space-chip-form' hx-post='api/control-input' hx-swap='none' hx-on::after-request='window.executeIfOk(event)'>")
            .Append("<input type='hidden' name='script' value='go ").Append(E(target)).Append(";'>")
            .Append("<button type='submit' class='space-chip'>")
            .Append(E(label))
            .AppendLine("</button></form>");
    }

    private static void AppendList(StringBuilder sb, IReadOnlyList<string> lines)
    {
        sb.AppendLine("<ul class='space-list'>");
        foreach (var line in lines)
            sb.Append("<li>").Append(E(line)).AppendLine("</li>");
        sb.AppendLine("</ul>");
    }

    private static SpaceViewModel Parse(string markdown)
    {
        var vm = new SpaceViewModel();
        var lines = (markdown ?? string.Empty).Replace("\r\n", "\n").Split('\n');

        bool inPois = false;
        bool inCargo = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("SYSTEM:", StringComparison.OrdinalIgnoreCase))
            {
                vm.System = line["SYSTEM:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("POI:", StringComparison.OrdinalIgnoreCase))
            {
                vm.Poi = line["POI:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("DOCKED:", StringComparison.OrdinalIgnoreCase))
            {
                vm.Docked = line["DOCKED:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("CREDITS:", StringComparison.OrdinalIgnoreCase))
            {
                vm.Credits = line["CREDITS:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("FUEL:", StringComparison.OrdinalIgnoreCase))
            {
                vm.Fuel = line["FUEL:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("HULL:", StringComparison.OrdinalIgnoreCase))
            {
                vm.Hull = line["HULL:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("SHIELD:", StringComparison.OrdinalIgnoreCase))
            {
                vm.Shield = line["SHIELD:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("CARGO:", StringComparison.OrdinalIgnoreCase))
            {
                vm.Cargo = line["CARGO:".Length..].Trim();
                continue;
            }

            if (line.Equals("POIS", StringComparison.OrdinalIgnoreCase))
            {
                inPois = true;
                inCargo = false;
                continue;
            }

            if (line.Equals("CARGO ITEMS", StringComparison.OrdinalIgnoreCase))
            {
                inPois = false;
                inCargo = true;
                continue;
            }

            if (!line.StartsWith("- ", StringComparison.Ordinal))
                continue;

            var item = line[2..].Trim();
            if (item.Length == 0 || item.Equals("(none)", StringComparison.OrdinalIgnoreCase))
                continue;

            if (inPois)
            {
                var target = item;
                int idx = item.IndexOf(" (", StringComparison.Ordinal);
                if (idx > 0)
                    target = item[..idx].Trim();

                if (target.Length > 0 &&
                    !vm.Pois.Any(p => string.Equals(p.Target, target, StringComparison.Ordinal)))
                {
                    vm.Pois.Add((target, item));
                }
            }
            else if (inCargo)
            {
                vm.CargoItems.Add(item);
            }
        }

        return vm;
    }

    private sealed class SpaceViewModel
    {
        public string System { get; set; } = "";
        public string Poi { get; set; } = "";
        public string Docked { get; set; } = "";
        public string Credits { get; set; } = "";
        public string Fuel { get; set; } = "";
        public string Hull { get; set; } = "";
        public string Shield { get; set; } = "";
        public string Cargo { get; set; } = "";
        public List<(string Target, string Label)> Pois { get; } = new();
        public List<string> CargoItems { get; } = new();

        public string SystemOrFallback => string.IsNullOrWhiteSpace(System) ? "(unknown system)" : System;
        public string PoiOrFallback => string.IsNullOrWhiteSpace(Poi) ? "(unknown poi)" : Poi;
        public string DockedOrFallback => string.IsNullOrWhiteSpace(Docked) ? "(dock state unknown)" : $"Docked: {Docked}";
    }

    private static string E(string value) => WebUtility.HtmlEncode(value ?? "");
}
