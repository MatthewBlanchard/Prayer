using System.Net;
using System.Text;

internal static class SkillsTabRenderer
{
    public static string Build(SkillsUiModel? model)
    {
        var skills = model?.Skills ?? System.Array.Empty<SkillUiEntry>();

        var sb = new StringBuilder();
        sb.AppendLine("<section class='space-page'>");
        sb.AppendLine("<div class='space-header'>");
        sb.AppendLine("<h4 class='space-title'>Skills</h4>");
        sb.Append("<div class='space-subtitle'>")
            .Append(skills.Count)
            .AppendLine(" skill(s)</div>");
        sb.AppendLine("</div>");

        sb.AppendLine("<section class='space-panel'>");
        if (skills.Count == 0)
        {
            sb.AppendLine("<div class='small'>(no skills found)</div>");
        }
        else
        {
            sb.AppendLine("<div class='cargo-list'>");
            foreach (var skill in skills)
            {
                sb.AppendLine("<div class='cargo-row'>");
                sb.Append("<div class='cargo-item-main'><div class='cargo-label'>")
                    .Append(E(skill.SkillId))
                    .Append("</div><div class='cargo-meta'>Level ")
                    .Append(skill.Level)
                    .AppendLine("</div></div>");
                sb.AppendLine("</div>");
            }
            sb.AppendLine("</div>");
        }

        sb.AppendLine("</section>");
        sb.AppendLine("</section>");
        return sb.ToString();
    }

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
