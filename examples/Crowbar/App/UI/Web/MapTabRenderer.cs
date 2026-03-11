using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;

internal static class MapTabRenderer
{
    public static string Build(
        SpaceUiModel? model,
        IReadOnlyList<BotMapMarker>? botMapMarkers = null,
        IReadOnlyList<BotRouteOverlay>? botRoutes = null,
        string? activeBotId = null,
        IReadOnlyList<SpaceUiSystemNode>? allKnownSystems = null)
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

        var localSystems = (vm.LocalSystems ?? Array.Empty<SpaceUiSystemNode>())
            .Where(s => !string.IsNullOrWhiteSpace(s.Id))
            .ToList();

        var pois = (vm.Pois ?? Array.Empty<SpaceUiPoi>())
            .Where(p => !string.IsNullOrWhiteSpace(p.Target))
            .ToList();
        var current = localSystems.FirstOrDefault(s => s.IsCurrent);
        var currentPoi = pois
            .FirstOrDefault(p => string.Equals(p.Target, vm.Poi, StringComparison.OrdinalIgnoreCase));
        var miningPoi = pois.FirstOrDefault(p => IsMiningPoiType(p.Type));
        var stationPoi = pois.FirstOrDefault(p => IsStationPoi(p));
        var isDocked = string.Equals(vm.Docked ?? string.Empty, "true", StringComparison.OrdinalIgnoreCase);

        var payload = JsonSerializer.Serialize(new
        {
            currentSystem = current?.Id ?? vm.System,
            currentPoi = currentPoi?.Target ?? vm.Poi,
            systems = localSystems.Select(s => new
            {
                id = s.Id,
                x = s.X,
                y = s.Y,
                empire = s.Empire,
                isStronghold = s.IsStronghold,
                hasStation = s.HasStation,
                hasKnownPois = s.HasKnownPois,
                connections = s.Connections ?? Array.Empty<string>()
            }),
            pois = pois
                .Select(p => new
                {
                    id = p.Target,
                    label = p.Label,
                    type = p.Type,
                    x = p.X,
                    y = p.Y,
                    isCurrent = string.Equals(p.Target, vm.Poi, StringComparison.OrdinalIgnoreCase)
                }),
            // allSystems contains every bot's known systems for route coordinate resolution.
            // Not rendered as visible map nodes — only used to look up positions for route hops
            // that pass through systems the selected bot hasn't visited yet.
            allSystems = (allKnownSystems ?? Array.Empty<SpaceUiSystemNode>())
                .Where(s => !string.IsNullOrWhiteSpace(s.Id) &&
                            s.X.HasValue && s.Y.HasValue)
                .Select(s => new { id = s.Id, x = s.X!.Value, y = s.Y!.Value }),
            botMarkers = (botMapMarkers ?? Array.Empty<BotMapMarker>())
                .Where(b => b != null && !string.IsNullOrWhiteSpace(b.SystemId))
                .Select(b => new
                {
                    botId = b.BotId,
                    label = b.Label,
                    systemId = b.SystemId.Trim(),
                    color = b.ColorHex,
                    isActive = string.Equals(b.BotId, activeBotId, StringComparison.Ordinal)
                }),
            botRoutes = (botRoutes ?? Array.Empty<BotRouteOverlay>())
                .Where(r => r != null &&
                            !string.IsNullOrWhiteSpace(r.BotId) &&
                            !string.IsNullOrWhiteSpace(r.CurrentSystemId) &&
                            r.Hops != null &&
                            r.Hops.Count > 0)
                .Select(r => new
                {
                    botId = r.BotId,
                    label = r.Label,
                    color = r.ColorHex,
                    isActive = string.Equals(r.BotId, activeBotId, StringComparison.Ordinal),
                    currentSystemId = r.CurrentSystemId,
                    targetSystemId = r.TargetSystemId,
                    hops = r.Hops,
                    shipSpeed = r.ShipSpeed,
                    arrivalTime = r.ArrivalTime?.ToString("O")
                })
        });

        var sb = new StringBuilder();
        sb.AppendLine("<section class='space-page map-page'>");
        sb.AppendLine("<div class='map-toolbar'>");
        sb.AppendLine("<div class='map-subtabs' role='tablist' aria-label='Map Views'>");
        sb.AppendLine("<button type='button' class='map-subtab-btn active' data-map-tab='system' aria-selected='true'>System</button>");
        sb.AppendLine("<button type='button' class='map-subtab-btn' data-map-tab='galaxy' aria-selected='false'>Galaxy</button>");
        sb.AppendLine("</div>");
        sb.AppendLine("<button type='button' class='space-chip map-center-btn' data-map-action='center-current'>Center On Current</button>");
        sb.AppendLine("</div>");
        sb.AppendLine("<section class='space-panel map-panel'>");
        sb.Append("<div class='map-subtab-pane active' data-map-pane='system'>");
        sb.Append("<canvas class='galaxy-map-canvas local-system-map-canvas' data-map-mode='system' data-map='")
            .Append(E(payload))
            .AppendLine("'></canvas>");
        AppendActionOverlay(sb, currentPoi, miningPoi, stationPoi, isDocked);
        sb.AppendLine("</div>");
        sb.Append("<div class='map-subtab-pane' data-map-pane='galaxy'>");
        sb.Append("<canvas class='galaxy-map-canvas galaxy-overview-map-canvas' data-map-mode='galaxy' data-map='")
            .Append(E(payload))
            .AppendLine("'></canvas>");
        sb.AppendLine("</div>");
        sb.AppendLine("<details class='map-context-box' open>");
        sb.AppendLine("<summary>Context</summary>");
        sb.AppendLine("<div class='map-context-body'>");
        sb.Append("<div class='map-context-row'><span class='map-context-k'>System</span><span class='map-context-v'>")
            .Append(E(string.IsNullOrWhiteSpace(vm.System) ? "(unknown)" : vm.System))
            .AppendLine("</span></div>");
        sb.Append("<div class='map-context-row'><span class='map-context-k'>POI</span><span class='map-context-v'>")
            .Append(E(currentPoi?.Label ?? (string.IsNullOrWhiteSpace(vm.Poi) ? "(unknown)" : vm.Poi)))
            .AppendLine("</span></div>");
        sb.Append("<div class='map-context-row'><span class='map-context-k'>Type</span><span class='map-context-v'>")
            .Append(E(string.IsNullOrWhiteSpace(currentPoi?.Type) ? "-" : currentPoi!.Type))
            .AppendLine("</span></div>");
        sb.Append("<div class='map-context-row'><span class='map-context-k'>Base</span><span class='map-context-v'>")
            .Append(E(currentPoi?.HasBase == true
                ? (!string.IsNullOrWhiteSpace(currentPoi.BaseName) ? currentPoi.BaseName : "Yes")
                : "No"))
            .AppendLine("</span></div>");
        if (currentPoi?.Online.HasValue == true)
        {
            sb.Append("<div class='map-context-row'><span class='map-context-k'>Online</span><span class='map-context-v'>")
                .Append(E(currentPoi.Online.Value.ToString()))
                .AppendLine("</span></div>");
        }
        sb.AppendLine("<div class='map-context-section'>Resources</div>");
        var resources = currentPoi?.Resources ?? Array.Empty<string>();
        if (resources.Count == 0)
        {
            sb.AppendLine("<div class='small'>(none)</div>");
        }
        else
        {
            sb.AppendLine("<ul class='map-context-list'>");
            foreach (var resource in resources.Take(8))
            {
                sb.Append("<li>").Append(E(resource)).AppendLine("</li>");
            }
            sb.AppendLine("</ul>");
        }
        sb.AppendLine("</div>");
        sb.AppendLine("</details>");
        sb.AppendLine("</section>");
        sb.AppendLine("</section>");
        return sb.ToString();
    }

    private static string Fallback(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static bool IsMiningPoiType(string? type)
    {
        var normalized = (type ?? string.Empty).Trim().ToLowerInvariant();
        return normalized == "asteroid_belt" ||
               normalized == "asteroid" ||
               normalized == "gas_cloud" ||
               normalized == "ice_field";
    }

    private static bool IsStationPoi(SpaceUiPoi poi)
    {
        if (poi == null)
            return false;

        var type = (poi.Type ?? string.Empty).Trim().ToLowerInvariant();
        if (type == "station")
            return true;
        return poi.HasBase;
    }

    private static void AppendActionOverlay(
        StringBuilder sb,
        SpaceUiPoi? currentPoi,
        SpaceUiPoi? miningPoi,
        SpaceUiPoi? stationPoi,
        bool isDocked)
    {
        sb.AppendLine("<div class='map-actions-overlay'>");
        AppendMapActionButton(sb, "Survey", "survey;");
        if (miningPoi != null)
        {
            var mineScript = currentPoi != null &&
                             string.Equals(currentPoi.Target, miningPoi.Target, StringComparison.OrdinalIgnoreCase)
                ? "mine;"
                : $"go {miningPoi.Target}; mine;";
            AppendMapActionButton(sb, "Mine", mineScript);
        }
        if (stationPoi != null && !isDocked)
        {
            var dockScript = currentPoi != null &&
                             string.Equals(currentPoi.Target, stationPoi.Target, StringComparison.OrdinalIgnoreCase)
                ? "dock;"
                : $"go {stationPoi.Target}; dock;";
            AppendMapActionButton(sb, "Dock", dockScript);
        }
        sb.AppendLine("</div>");
    }

    private static void AppendMapActionButton(StringBuilder sb, string label, string script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return;

        sb.Append("<form class='map-action-form' hx-post='api/control-input' hx-swap='none' hx-on::after-request='window.executeIfOk(event)'>")
            .Append("<input type='hidden' name='script' value='")
            .Append(E(script))
            .Append("'>")
            .Append("<button type='submit' class='map-action-btn'>")
            .Append(E(label))
            .AppendLine("</button></form>");
    }

    private static string E(string value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
