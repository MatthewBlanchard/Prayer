using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

internal static class SpaceTabRenderer
{
    public static string BuildStateStrip(SpaceUiModel? model)
    {
        var vm = model ?? new SpaceUiModel(
            System: string.Empty,
            Poi: string.Empty,
            Docked: string.Empty,
            Credits: 0,
            Fuel: string.Empty,
            Hull: string.Empty,
            Shield: string.Empty,
            Cargo: string.Empty,
            Pois: Array.Empty<SpaceUiPoi>(),
            CargoItems: Array.Empty<SpaceUiCargoItem>(),
            LocalSystems: Array.Empty<SpaceUiSystemNode>());

        var sb = new StringBuilder();
        sb.AppendLine("<div class='space-stats state-strip'>");
        AppendStatCard(sb, "Credits", vm.Credits.ToString());
        AppendBarStatCard(sb, "Fuel", vm.Fuel);
        AppendBarStatCard(sb, "Hull", vm.Hull);
        AppendBarStatCard(sb, "Shield", vm.Shield);
        AppendBarStatCard(sb, "Cargo", vm.Cargo);
        sb.AppendLine("</div>");
        return sb.ToString();
    }

    public static string Build(SpaceUiModel? model, IReadOnlyList<string> connectedSystems)
    {
        var vm = model ?? new SpaceUiModel(
            System: string.Empty,
            Poi: string.Empty,
            Docked: string.Empty,
            Credits: 0,
            Fuel: string.Empty,
            Hull: string.Empty,
            Shield: string.Empty,
            Cargo: string.Empty,
            Pois: Array.Empty<SpaceUiPoi>(),
            CargoItems: Array.Empty<SpaceUiCargoItem>(),
            LocalSystems: Array.Empty<SpaceUiSystemNode>());

        var systems = (connectedSystems ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Where(s => !string.Equals(s, vm.System, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("<section class='space-page'>");
        sb.AppendLine("<div class='space-header'>");
        sb.Append("<h4 class='space-title'>").Append(E(SystemOrFallback(vm.System))).AppendLine("</h4>");
        sb.Append("<div class='space-subtitle'>POI ").Append(E(PoiOrFallback(vm.Poi)))
            .Append(" • ").Append(E(DockedOrFallback(vm.Docked))).AppendLine("</div>");
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
        if (vm.CargoItems.Count > 0)
        {
            AppendCargoList(sb, vm.CargoItems);
            AppendCargoAllActions(sb);
        }
        sb.AppendLine("</section>");

        sb.AppendLine("</section>");
        return sb.ToString();
    }

    private static void AppendStatCard(StringBuilder sb, string label, string value)
    {
        var statKind = label.Trim().ToLowerInvariant();
        sb.Append("<div class='space-stat ")
            .Append(E($"stat-{statKind}"))
            .Append("'><div class='space-stat-label'>")
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
        var meterKind = label.Trim().ToLowerInvariant();

        sb.Append("<div class='space-stat compact'><div class='space-stat-label'>")
            .Append(E(label))
            .Append("</div><div class='space-stat-row'><div class='space-stat-value'>")
            .Append(E(valueText))
            .AppendLine("</div></div>");

        sb.Append("<div class='space-meter' role='img' aria-label='")
            .Append(E($"{label}: {valueText} ({widthPct}%)"))
            .Append("'><div class='space-bar'><div class='space-bar-fill ")
            .Append(E($"meter-{meterKind}"))
            .Append("' style='width:")
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

    private static void AppendCargoList(StringBuilder sb, IReadOnlyList<SpaceUiCargoItem> items)
    {
        sb.AppendLine("<div class='cargo-list'>");
        foreach (var item in items)
        {
            var itemId = item.ItemId ?? string.Empty;
            sb.AppendLine("<div class='cargo-row'>");
            sb.Append("<div class='cargo-item-main'><div class='cargo-label'>")
                .Append(E($"{itemId} x{item.Quantity}"))
                .AppendLine("</div>");
            if (item.MedianPrice.HasValue && item.MedianPrice.Value > 0m)
            {
                sb.Append("<div class='cargo-meta'><span class='trade-order-price'>")
                    .Append(E($"{item.MedianPrice.Value:0.##}cr"))
                    .AppendLine("</span></div>");
            }
            sb.AppendLine("</div>");

            if (!string.IsNullOrWhiteSpace(itemId))
            {
                sb.Append("<div class='cargo-actions'>");
                AppendScriptChip(sb, $"sell {itemId};", "Sell");
                AppendScriptChip(sb, $"stash {itemId};", "Stash");
                sb.AppendLine("</div>");
            }
            sb.AppendLine("</div>");
        }
        sb.AppendLine("</div>");
    }

    private static void AppendCargoAllActions(StringBuilder sb)
    {
        sb.AppendLine("<div class='cargo-all-actions'>");
        AppendScriptChip(sb, "sell;", "Sell All");
        AppendScriptChip(sb, "stash;", "Stash All");
        sb.AppendLine("</div>");
    }

    private static void AppendScriptChip(StringBuilder sb, string script, string label)
    {
        sb.Append("<form class='space-chip-form' hx-post='api/control-input' hx-swap='none' hx-on::after-request='window.executeIfOk(event)'>")
            .Append("<input type='hidden' name='script' value='").Append(E(script)).Append("'>")
            .Append("<button type='submit' class='space-chip'>")
            .Append(E(label))
            .AppendLine("</button></form>");
    }

    private static string SystemOrFallback(string system)
        => string.IsNullOrWhiteSpace(system) ? "(unknown system)" : system;

    private static string PoiOrFallback(string poi)
        => string.IsNullOrWhiteSpace(poi) ? "(unknown poi)" : poi;

    private static string DockedOrFallback(string docked)
        => string.IsNullOrWhiteSpace(docked) ? "(dock state unknown)" : $"Docked: {docked}";

    private static string E(string value) => WebUtility.HtmlEncode(value ?? "");
}
