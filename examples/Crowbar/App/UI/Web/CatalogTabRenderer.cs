using System;
using System.Linq;
using System.Net;
using System.Text;

internal static class CatalogTabRenderer
{
    public static string Build(CatalogUiModel? model)
    {
        if (model == null)
            return "<section class='space-page'><div class='small'>(catalog unavailable)</div></section>";

        var sb = new StringBuilder();
        sb.AppendLine("<section class='space-page'>");
        sb.AppendLine("<div class='space-header'>");
        sb.AppendLine("<h4 class='space-title'>Item Catalog</h4>");
        sb.Append("<div class='space-subtitle'>Items ")
            .Append(model.Items.Count)
            .Append(" • Ships ")
            .Append(model.Ships.Count)
            .AppendLine("</div>");
        sb.AppendLine("</div>");

        sb.AppendLine("<input class='catalog-search' type='search' placeholder='Search items/ships...' oninput='window.filterCatalogEntries(this.value)'>");

        AppendItemCatalog(sb, model.Items);
        AppendShipCatalog(sb, model.Ships);

        sb.AppendLine("</section>");
        return sb.ToString();
    }

    private static void AppendItemCatalog(StringBuilder sb, System.Collections.Generic.IReadOnlyList<CatalogUiEntry> items)
    {
        sb.Append("<details class='catalog-group' open><summary>")
            .Append(E($"All Items ({items.Count})"))
            .AppendLine("</summary>");

        if (items.Count == 0)
        {
            sb.AppendLine("<div class='small'>(none)</div>");
            sb.AppendLine("</details>");
            return;
        }

        var byCategory = items
            .GroupBy(i => string.IsNullOrWhiteSpace(i.Category) ? "Unknown" : i.Category)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var categoryGroup in byCategory)
        {
            sb.Append("<details class='catalog-group'><summary>")
                .Append(E($"{categoryGroup.Key} ({categoryGroup.Count()})"))
                .AppendLine("</summary>");
            sb.AppendLine("<div class='catalog-list'>");

            foreach (var item in categoryGroup)
            {
                var search = $"{item.Id} {item.Name} {item.Category} {item.Tier} {item.Price}";
                sb.Append("<details class='mission-item catalog-entry' data-search='")
                    .Append(E(search.ToLowerInvariant()))
                    .AppendLine("'>");
                sb.Append("<summary class='shipyard-ship-summary'>")
                    .Append("<span class='shipyard-ship-name'>").Append(E(item.Name)).Append("</span>")
                    .Append("<span class='shipyard-ship-meta'>ID: ").Append(E(item.Id))
                    .Append(" | Price: ").Append(E(item.Price.HasValue ? item.Price.Value.ToString("0.##") : "-"))
                    .AppendLine("</span></summary>");
                sb.AppendLine("<div class='shipyard-ship-body'>");
                sb.AppendLine("<div class='shipyard-ship-stats'>");
                AppendEntryStat(sb, "ID", item.Id);
                AppendEntryStat(sb, "Name", item.Name);
                AppendEntryStat(sb, "Category", item.Category);
                AppendEntryStat(sb, "Tier", item.Tier);
                AppendEntryStat(sb, "Price", item.Price.HasValue ? item.Price.Value.ToString("0.##") : null);
                sb.AppendLine("</div>");
                sb.AppendLine("</div>");
                sb.AppendLine("</details>");
            }

            sb.AppendLine("</div>");
            sb.AppendLine("</details>");
        }

        sb.AppendLine("</details>");
    }

    private static void AppendShipCatalog(StringBuilder sb, System.Collections.Generic.IReadOnlyList<CatalogUiEntry> ships)
    {
        sb.Append("<details class='catalog-group'><summary>")
            .Append(E($"Ships ({ships.Count})"))
            .AppendLine("</summary>");
        sb.AppendLine("<div class='catalog-list'>");

        if (ships.Count == 0)
        {
            sb.AppendLine("<div class='catalog-entry' data-search=''>- (none)</div>");
            sb.AppendLine("</div></details>");
            return;
        }

        foreach (var ship in ships)
        {
            var search = $"{ship.Id} {ship.Name} {ship.Category} {ship.Tier} {ship.Price}";
            sb.Append("<div class='catalog-entry' data-search='")
                .Append(E(search.ToLowerInvariant()))
                .Append("'>")
                .Append(E(ship.DisplayText))
                .AppendLine("</div>");
        }

        sb.AppendLine("</div>");
        sb.AppendLine("</details>");
    }

    private static void AppendEntryStat(StringBuilder sb, string label, string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "-" : value;
        sb.Append("<div class='shipyard-ship-stat'><div class='shipyard-ship-stat-label'>")
            .Append(E(label))
            .Append("</div><div class='shipyard-ship-stat-value'>")
            .Append(E(text))
            .AppendLine("</div></div>");
    }

    private static void AppendEntryStat(StringBuilder sb, string label, int? value)
    {
        AppendEntryStat(sb, label, value.HasValue ? value.Value.ToString() : null);
    }

    private static string E(string value) => WebUtility.HtmlEncode(value ?? "");
}
