using System.Linq;
using System.Net;
using System.Text;

internal static class ShipyardTabRenderer
{
    public static string Build(ShipyardUiModel? model)
    {
        if (model == null)
            return "<section class='space-page'><div class='small'>(ship unavailable)</div></section>";

        var sb = new StringBuilder();
        sb.AppendLine("<section class='space-page'>");

        if (model.Docked)
        {
            sb.AppendLine("<div class='space-header'>");
            sb.Append("<h4 class='space-title'>Shipyard • ").Append(E(model.StationId)).AppendLine("</h4>");
            sb.Append("<div class='space-subtitle'>Showroom ")
                .Append(model.Showroom.Count)
                .Append(" • Listings ")
                .Append(model.PlayerListings.Count)
                .Append(" • Catalog ")
                .Append(model.CatalogShips.Count)
                .AppendLine("</div>");
            sb.AppendLine("</div>");
        }

        sb.AppendLine("<div class='space-stats shipyard-stats'>");
        AppendStatCard(sb, "Ship", model.ShipName);
        AppendStatCard(sb, "Class", model.ShipClassId);
        AppendStatCard(sb, "Fuel", model.Fuel);
        AppendStatCard(sb, "Hull", model.Hull);
        AppendStatCard(sb, "Shield", model.Shield);
        AppendStatCard(sb, "Cargo", model.Cargo);
        sb.AppendLine("</div>");
        sb.AppendLine("<div class='mission-actions' style='margin-bottom:8px;'>");
        sb.AppendLine("<button type='button' class='space-chip mission-chip-danger' onclick='window.confirmSelfDestruct()'>Self Destruct</button>");
        sb.AppendLine("</div>");

        sb.AppendLine("<section class='space-panel'>");
        sb.Append("<div class='space-panel-title'>Installed Modules (")
            .Append(model.InstalledModules.Count)
            .AppendLine(")</div>");
        if (model.InstalledModules.Count == 0)
        {
            sb.AppendLine("<div class='small'>(none)</div>");
        }
        else
        {
            sb.AppendLine("<div class='catalog-list'>");
            foreach (var module in model.InstalledModules)
            {
                sb.Append("<div class='mission-item shipyard-card'><div class='mission-title'>")
                    .Append(E(module))
                    .AppendLine("</div></div>");
            }
            sb.AppendLine("</div>");
        }
        sb.AppendLine("</section>");

        sb.AppendLine("<section class='space-panel'>");
        sb.Append("<div class='space-panel-title'>Owned Ships (")
            .Append(model.OwnedShips.Count)
            .AppendLine(")</div>");
        if (model.OwnedShips.Count == 0)
        {
            sb.AppendLine("<div class='small'>(none)</div>");
        }
        else
        {
            foreach (var ship in model.OwnedShips)
            {
                sb.Append("<div class='mission-item shipyard-card'><div class='mission-title'>")
                    .Append(E(ship.DisplayText))
                    .AppendLine("</div>");

                if (ship.IsActive)
                {
                    sb.AppendLine("<div class='small'>Current active ship</div>");
                }
                else if (ship.CanSwitchHere)
                {
                    sb.AppendLine("<div class='mission-actions'>");
                    AppendScriptChip(sb, $"switch_ship {ship.ShipId};", "Switch");
                    sb.AppendLine("</div>");
                }
                else
                {
                    sb.Append("<div class='small'>")
                        .Append(E(model.Docked
                            ? "Switch unavailable at this station"
                            : "Dock at the ship's station to switch"))
                        .AppendLine("</div>");
                }
                sb.AppendLine("</div>");
            }
        }
        sb.AppendLine("</section>");

        if (model.Docked)
        {
            sb.AppendLine("<div class='space-grid'>");
            AppendPanel(sb, "Showroom", model.Showroom, "commission_quote", "Quote", "buy_ship", "Buy");
            AppendPanel(sb, "Player Listings", model.PlayerListings, "buy_listed_ship", "Buy Listing", null, null);
            sb.AppendLine("</div>");
        }

        sb.AppendLine("<section class='space-panel'>");
        sb.AppendLine("<div class='space-panel-title'>Galaxy Ship Catalog</div>");
        if (model.CatalogShips.Count == 0)
        {
            sb.AppendLine("<div class='small'>(none)</div>");
        }
        else
        {
            sb.AppendLine("<input class='catalog-search' type='search' placeholder='Search ship catalog...' oninput='window.filterShipCatalogEntries(this.value)'>");
            var byTier = model.CatalogShips
                .GroupBy(e => e.Tier.HasValue ? e.Tier.Value.ToString() : "Unknown")
                .OrderBy(g =>
                {
                    if (int.TryParse(g.Key, out var tier))
                        return tier;
                    return int.MaxValue;
                })
                .ToList();
            bool first = true;
            foreach (var tierGroup in byTier)
            {
                sb.Append("<details class='catalog-group shipyard-faction-group' data-persist-key='tier:")
                    .Append(E(tierGroup.Key))
                    .Append("'");
                if (first)
                    sb.Append(" open");
                sb.Append("><summary>")
                    .Append(E($"Tier {tierGroup.Key} ({tierGroup.Count()})"))
                    .AppendLine("</summary>");
                sb.AppendLine("<div class='catalog-list'>");

                var byType = tierGroup
                    .GroupBy(ResolveShipType)
                    .OrderBy(g => g.Key, System.StringComparer.OrdinalIgnoreCase);

                foreach (var typeGroup in byType)
                {
                    sb.Append("<details class='catalog-group shipyard-faction-group' data-persist-key='type:")
                        .Append(E(tierGroup.Key))
                        .Append(":")
                        .Append(E(typeGroup.Key))
                        .AppendLine("'>");
                    sb.Append("<summary>")
                        .Append(E($"{typeGroup.Key} ({typeGroup.Count()})"))
                        .AppendLine("</summary>");
                    sb.AppendLine("<div class='catalog-list'>");

                    foreach (var entry in typeGroup.OrderBy(e => e.Name ?? e.DisplayText, System.StringComparer.OrdinalIgnoreCase))
                    {
                        var headerName = !string.IsNullOrWhiteSpace(entry.Name) ? entry.Name : entry.DisplayText;
                        var headerClass = string.IsNullOrWhiteSpace(entry.ClassId) ? "-" : entry.ClassId;
                        var headerPrice = entry.Price.HasValue ? entry.Price.Value.ToString("0.##") : "-";
                        var searchText = string.Join(
                                " ",
                                new[]
                                {
                                    entry.Id,
                                    entry.Name,
                                    entry.Faction,
                                    entry.ClassId,
                                    entry.Category,
                                    entry.Tier?.ToString(),
                                    entry.Scale?.ToString(),
                                    entry.Hull?.ToString(),
                                    entry.Shield?.ToString(),
                                    entry.Cargo?.ToString(),
                                    entry.Speed?.ToString(),
                                    entry.Price?.ToString("0.##")
                                }
                                .Where(v => !string.IsNullOrWhiteSpace(v)))
                            .ToLowerInvariant();
                        sb.Append("<details class='mission-item shipyard-card shipyard-ship-detail' data-persist-key='ship:")
                            .Append(E(entry.Id))
                            .Append("' data-search='")
                            .Append(E(searchText))
                            .AppendLine("'>");
                        sb.Append("<summary class='shipyard-ship-summary'>")
                            .Append("<span class='shipyard-ship-name'>").Append(E(headerName)).Append("</span>")
                            .Append("<span class='shipyard-ship-meta'>Class: ").Append(E(headerClass))
                            .Append(" | Price: ").Append(E(headerPrice)).Append("</span>")
                            .AppendLine("</summary>");

                        sb.AppendLine("<div class='shipyard-ship-body'>");
                        sb.AppendLine("<div class='shipyard-ship-stats'>");
                        AppendShipStat(sb, "ID", entry.Id);
                        AppendShipStat(sb, "Name", entry.Name);
                        AppendShipStat(sb, "Faction", entry.Faction);
                        AppendShipStat(sb, "Class", entry.ClassId);
                        AppendShipStat(sb, "Category", entry.Category);
                        AppendShipStat(sb, "Tier", entry.Tier);
                        AppendShipStat(sb, "Scale", entry.Scale);
                        AppendShipStat(sb, "Hull", entry.Hull);
                        AppendShipStat(sb, "Shield", entry.Shield);
                        AppendShipStat(sb, "Cargo", entry.Cargo);
                        AppendShipStat(sb, "Speed", entry.Speed);
                        AppendShipStat(sb, "Price", entry.Price.HasValue ? entry.Price.Value.ToString("0.##") : null);
                        sb.AppendLine("</div>");

                        if (!string.IsNullOrWhiteSpace(entry.Id))
                        {
                            sb.AppendLine("<div class='mission-actions'>");
                            AppendScriptChip(sb, $"commission_ship {entry.Id};", "Commission");
                            sb.AppendLine("</div>");
                        }
                        sb.AppendLine("</div>");
                        sb.AppendLine("</details>");
                    }
                    sb.AppendLine("</div></details>");
                }
                sb.AppendLine("</div></details>");
                first = false;
            }
        }
        sb.AppendLine("</section>");
        sb.AppendLine("</section>");
        return sb.ToString();
    }

    private static void AppendPanel(
        StringBuilder sb,
        string title,
        System.Collections.Generic.IReadOnlyList<ShipyardUiEntry> entries,
        string? primaryCommand,
        string? primaryLabel,
        string? secondaryCommand,
        string? secondaryLabel)
    {
        sb.AppendLine("<section class='space-panel'>");
        sb.Append("<div class='space-panel-title'>").Append(E(title)).AppendLine("</div>");
        if (entries.Count == 0)
        {
            sb.AppendLine("<div class='small'>(none)</div>");
        }
        else
        {
            foreach (var entry in entries)
            {
                sb.Append("<div class='mission-item shipyard-card'><div class='mission-title'>")
                    .Append(E(entry.DisplayText))
                    .AppendLine("</div>");
                if (!string.IsNullOrWhiteSpace(entry.Id) && !string.IsNullOrWhiteSpace(primaryCommand) && !string.IsNullOrWhiteSpace(primaryLabel))
                {
                    sb.AppendLine("<div class='mission-actions'>");
                    AppendScriptChip(sb, $"{primaryCommand} {entry.Id};", primaryLabel);
                    if (!string.IsNullOrWhiteSpace(secondaryCommand) && !string.IsNullOrWhiteSpace(secondaryLabel))
                        AppendScriptChip(sb, $"{secondaryCommand} {entry.Id};", secondaryLabel);
                    sb.AppendLine("</div>");
                }
                sb.AppendLine("</div>");
            }
        }
        sb.AppendLine("</section>");
    }

    private static void AppendStatCard(StringBuilder sb, string label, string value)
    {
        sb.Append("<div class='space-stat'><div class='space-stat-label'>")
            .Append(E(label))
            .Append("</div><div class='space-stat-value'>")
            .Append(E(value))
            .AppendLine("</div></div>");
    }

    private static void AppendShipStat(StringBuilder sb, string label, string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "-" : value;
        sb.Append("<div class='shipyard-ship-stat'><div class='shipyard-ship-stat-label'>")
            .Append(E(label))
            .Append("</div><div class='shipyard-ship-stat-value'>")
            .Append(E(text))
            .AppendLine("</div></div>");
    }

    private static void AppendShipStat(StringBuilder sb, string label, int? value)
    {
        AppendShipStat(sb, label, value.HasValue ? value.Value.ToString() : null);
    }

    private static string ResolveShipType(ShipyardUiEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.ClassId))
            return entry.ClassId!;
        if (!string.IsNullOrWhiteSpace(entry.Category))
            return entry.Category!;
        return "Unknown";
    }

    private static void AppendScriptChip(StringBuilder sb, string script, string label)
    {
        sb.Append("<form class='space-chip-form' hx-post='api/control-input' hx-swap='none' hx-on::after-request='window.executeIfOk(event)'>")
            .Append("<input type='hidden' name='script' value='").Append(E(script)).Append("'>")
            .Append("<button type='submit' class='space-chip'>")
            .Append(E(label))
            .AppendLine("</button></form>");
    }

    private static string E(string value) => WebUtility.HtmlEncode(value ?? "");
}
