using System.Net;
using System.Text;

internal static class ShipyardTabRenderer
{
    public static string Build(ShipyardUiModel? model)
    {
        if (model == null)
            return "<section class='space-page'><div class='small'>(shipyard unavailable)</div></section>";

        var sb = new StringBuilder();
        sb.AppendLine("<section class='space-page'>");
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

        sb.AppendLine("<div class='space-stats shipyard-stats'>");
        AppendStatCard(sb, "Credits", model.Credits.ToString());
        AppendStatCard(sb, "Station Credits", model.StationCredits.ToString());
        AppendStatCard(sb, "Fuel", model.Fuel);
        AppendStatCard(sb, "Cargo", model.Cargo);
        AppendStatCard(sb, "Catalog Page", model.CatalogPage);
        if (model.TotalShips.HasValue)
            AppendStatCard(sb, "Total Ships", model.TotalShips.Value.ToString());
        sb.AppendLine("</div>");

        sb.AppendLine("<div class='space-grid'>");
        AppendPanel(sb, "Showroom", model.Showroom, "commission_quote", "Quote", "buy_ship", "Buy");
        AppendPanel(sb, "Player Listings", model.PlayerListings, "buy_listed_ship", "Buy Listing", null, null);
        sb.AppendLine("</div>");

        sb.AppendLine("<section class='space-panel'>");
        sb.AppendLine("<div class='space-panel-title'>Ship Catalog Cache</div>");
        if (model.CatalogShips.Count == 0)
        {
            sb.AppendLine("<div class='small'>(none)</div>");
        }
        else
        {
            foreach (var entry in model.CatalogShips)
            {
                sb.Append("<div class='mission-item shipyard-card'><div class='mission-title'>")
                    .Append(E(entry.DisplayText))
                    .AppendLine("</div>");
                if (!string.IsNullOrWhiteSpace(entry.Id))
                {
                    sb.AppendLine("<div class='mission-actions'>");
                    AppendScriptChip(sb, $"commission_quote {entry.Id};", "Quote");
                    AppendScriptChip(sb, $"buy_ship {entry.Id};", "Buy");
                    sb.AppendLine("</div>");
                }
                sb.AppendLine("</div>");
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
