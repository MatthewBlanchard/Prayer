using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;

internal static class MapTabRenderer
{
    public static string Build(SpaceUiModel? model)
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
        var current = localSystems.FirstOrDefault(s => s.IsCurrent);
        var currentPoi = (vm.Pois ?? Array.Empty<SpaceUiPoi>())
            .FirstOrDefault(p => string.Equals(p.Target, vm.Poi, StringComparison.OrdinalIgnoreCase));

        var payload = JsonSerializer.Serialize(new
        {
            currentSystem = current?.Id ?? vm.System,
            currentPoi = currentPoi?.Target ?? vm.Poi,
            systems = localSystems.Select(s => new
            {
                id = s.Id,
                x = s.X,
                y = s.Y,
                connections = s.Connections ?? Array.Empty<string>()
            }),
            pois = (vm.Pois ?? Array.Empty<SpaceUiPoi>())
                .Where(p => !string.IsNullOrWhiteSpace(p.Target))
                .Select(p => new
                {
                    id = p.Target,
                    label = p.Label,
                    type = p.Type,
                    x = p.X,
                    y = p.Y,
                    isCurrent = string.Equals(p.Target, vm.Poi, StringComparison.OrdinalIgnoreCase)
                })
        });

        var sb = new StringBuilder();
        sb.AppendLine("<section class='space-page map-page'>");
        sb.AppendLine("<section class='space-panel map-panel'>");
        sb.Append("<canvas class='galaxy-map-canvas local-system-map-canvas' data-map='")
            .Append(E(payload))
            .AppendLine("'></canvas>");
        sb.AppendLine("</section>");
        sb.AppendLine("</section>");
        return sb.ToString();
    }

    private static string Fallback(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string E(string value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
