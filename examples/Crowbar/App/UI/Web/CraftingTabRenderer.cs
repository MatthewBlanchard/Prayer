using System.Linq;
using System.Net;
using System.Text;

internal static class CraftingTabRenderer
{
    public static string Build(CraftingUiModel? model)
    {
        if (model == null || !model.HasCrafting)
            return "<section class='space-page'><div class='small'>(crafting unavailable – dock at a station with a crafting service)</div></section>";

        var sb = new StringBuilder();
        sb.AppendLine("<section class='space-page'>");

        sb.AppendLine("<div class='space-header'>");
        sb.Append("<h4 class='space-title'>Crafting • ").Append(E(model.StationId)).AppendLine("</h4>");
        sb.Append("<div class='space-subtitle'>").Append(model.Recipes.Count).AppendLine(" recipe(s) available</div>");
        sb.AppendLine("</div>");

        sb.AppendLine("<section class='space-panel'>");
        sb.AppendLine("<div class='space-panel-title'>Available Recipes</div>");

        sb.AppendLine("<label class='trade-only-orders-toggle'><input type='checkbox' checked onchange='window.toggleCraftingOnlyWithSkills(this.checked)'> Only recipes matching my skills</label>");
        sb.AppendLine("<input class='catalog-search' type='search' placeholder='Search recipes...' oninput='window.filterCraftingRecipes(this.value)'>");

        var byCategory = model.Recipes
            .GroupBy(r => r.Category)
            .OrderBy(g => g.Key, System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool first = true;
        foreach (var group in byCategory)
        {
            sb.Append("<details class='catalog-group'");
            if (first)
                sb.Append(" open");
            sb.Append("><summary>")
                .Append(E($"{group.Key} ({group.Count()})"))
                .AppendLine("</summary>");
            sb.AppendLine("<div class='cargo-list'>");

            foreach (var recipe in group.OrderBy(r => r.Tier ?? int.MaxValue).ThenBy(r => r.Name, System.StringComparer.OrdinalIgnoreCase))
            {
                var searchText = $"{recipe.RecipeId} {recipe.Name} {recipe.Category} {recipe.Tier}".ToLowerInvariant();
                sb.Append("<div class='cargo-row' data-search='")
                    .Append(E(searchText))
                    .Append("' data-meets-skill='")
                    .Append(recipe.MeetsSkillRequirements ? "true" : "false")
                    .AppendLine("'>");

                sb.Append("<div class='cargo-item-main'>");
                sb.Append("<div class='cargo-label'>").Append(E(recipe.Name)).Append("</div>");
                sb.Append("<div class='cargo-meta'><code>").Append(E(recipe.RecipeId)).Append("</code>");
                if (recipe.Tier.HasValue)
                    sb.Append(" • T").Append(recipe.Tier.Value);
                sb.AppendLine("</div>");
                sb.Append("<div class='cargo-meta'>").Append(E(recipe.IngredientsSummary)).AppendLine("</div>");
                sb.Append("<div class='cargo-meta'>").Append(E(recipe.OutputsSummary)).AppendLine("</div>");
                sb.AppendLine("</div>");

                AppendCraftForm(sb, recipe.RecipeId);

                sb.AppendLine("</div>");
            }

            sb.AppendLine("</div></details>");
            first = false;
        }

        sb.AppendLine("</section>");
        sb.AppendLine("</section>");
        return sb.ToString();
    }

    private static void AppendCraftForm(StringBuilder sb, string recipeId)
    {
        if (string.IsNullOrWhiteSpace(recipeId))
            return;

        sb.Append("<form class='trade-buy-form' data-item-id='")
            .Append(E(recipeId))
            .Append("' hx-post='api/control-input' hx-swap='none' hx-on::after-request='window.executeIfOk(event)' onsubmit='return window.submitCraft(event, this)'>")
            .Append("<input type='hidden' name='script' value=''>")
            .Append("<input type='number' name='qty' min='1' max='10' step='1' value='1' class='trade-qty-input'>")
            .Append("<button type='submit' class='space-chip'>Craft</button>")
            .AppendLine("</form>");
    }

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
