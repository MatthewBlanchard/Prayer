using System;
using System.Collections.Generic;
using System.Text;

internal static class DslPrettyPrinter
{
    public static string Render(DslAstProgram tree)
    {
        if (tree == null || tree.Statements == null || tree.Statements.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        AppendAstNodes(tree.Statements, sb, indent: 0);
        return sb.ToString();
    }

    /// <summary>
    /// Serializes a SkillLibrary back to .prayer file text.
    /// Round-trips cleanly through Parse → RenderSkillLibrary.
    /// </summary>
    public static string RenderSkillLibrary(SkillLibrary library)
    {
        if (library == null)
            return string.Empty;

        var sb = new StringBuilder();

        foreach (var skill in library.Skills)
        {
            AppendSkill(skill, sb);
            sb.AppendLine();
        }

        foreach (var ov in library.Overrides)
        {
            AppendOverride(ov, sb);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd() + (sb.Length > 0 ? "\n" : string.Empty);
    }

    private static void AppendSkill(DslSkillAstNode skill, StringBuilder sb)
    {
        sb.Append("skill ");
        sb.Append(skill.Name);
        sb.Append('(');

        for (int i = 0; i < skill.Params.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var p = skill.Params[i];
            sb.Append(p.Name);
            sb.Append(": ");
            sb.Append(ArgTypeName(p.Type));
        }

        sb.AppendLine(") {");
        AppendAstNodesRaw(skill.Body, sb, indent: 2);
        sb.AppendLine("}");
    }

    private static void AppendOverride(DslOverrideAstNode ov, StringBuilder sb)
    {
        sb.Append("override ");
        sb.Append(ov.Name);
        sb.Append(" when ");
        sb.Append(DslBooleanEvaluator.RenderCondition(ov.Condition));
        sb.AppendLine(" {");
        AppendAstNodesRaw(ov.Body, sb, indent: 2);
        sb.AppendLine("}");
    }

    /// <summary>
    /// Renders skill/override body nodes using raw arg tokens (preserving $param references)
    /// rather than calling ToValidCommand which would reject unknown $param macros.
    /// </summary>
    private static void AppendAstNodesRaw(
        IReadOnlyList<DslAstNode> nodes,
        StringBuilder sb,
        int indent)
    {
        foreach (var node in nodes ?? Array.Empty<DslAstNode>())
        {
            switch (node)
            {
                case DslCommandAstNode commandNode:
                    AppendIndent(sb, indent);
                    sb.Append(commandNode.Name);
                    foreach (var arg in commandNode.Args ?? Array.Empty<string>())
                    {
                        if (string.IsNullOrWhiteSpace(arg)) continue;
                        sb.Append(' ');
                        sb.Append(arg);
                    }
                    sb.AppendLine(";");
                    break;

                case DslIfAstNode ifNode:
                    AppendIndent(sb, indent);
                    sb.Append("if ");
                    sb.Append(DslBooleanEvaluator.RenderCondition(ifNode.Condition));
                    sb.AppendLine(" {");
                    AppendAstNodesRaw(ifNode.Body ?? Array.Empty<DslAstNode>(), sb, indent + 2);
                    AppendIndent(sb, indent);
                    sb.AppendLine("}");
                    break;

                case DslUntilAstNode untilNode:
                    AppendIndent(sb, indent);
                    sb.Append("until ");
                    sb.Append(DslBooleanEvaluator.RenderCondition(untilNode.Condition));
                    sb.AppendLine(" {");
                    AppendAstNodesRaw(untilNode.Body ?? Array.Empty<DslAstNode>(), sb, indent + 2);
                    AppendIndent(sb, indent);
                    sb.AppendLine("}");
                    break;
            }
        }
    }

    private static string ArgTypeName(DslArgType type) => type switch
    {
        DslArgType.PoiId     => "poi_id",
        DslArgType.SystemId  => "system_id",
        DslArgType.ItemId    => "item_id",
        DslArgType.ShipId    => "ship_id",
        DslArgType.MissionId => "mission_id",
        DslArgType.ModuleId  => "module_id",
        DslArgType.RecipeId  => "recipe_id",
        DslArgType.Integer   => "integer",
        _                    => "any",
    };

    private static void AppendAstNodes(
        IReadOnlyList<DslAstNode> nodes,
        StringBuilder sb,
        int indent)
    {
        foreach (var node in nodes ?? Array.Empty<DslAstNode>())
        {
            switch (node)
            {
                case DslCommandAstNode commandNode:
                    AppendAstCommand(commandNode, sb, indent);
                    break;

                case DslIfAstNode ifNode:
                    AppendIndent(sb, indent);
                    sb.Append("if ");
                    sb.Append(DslBooleanEvaluator.RenderCondition(ifNode.Condition));
                    sb.AppendLine(" {");
                    AppendAstNodes(ifNode.Body ?? Array.Empty<DslAstNode>(), sb, indent + 2);
                    AppendIndent(sb, indent);
                    sb.AppendLine("}");
                    break;

                case DslUntilAstNode untilNode:
                    AppendIndent(sb, indent);
                    sb.Append("until ");
                    sb.Append(DslBooleanEvaluator.RenderCondition(untilNode.Condition));
                    sb.AppendLine(" {");
                    AppendAstNodes(untilNode.Body ?? Array.Empty<DslAstNode>(), sb, indent + 2);
                    AppendIndent(sb, indent);
                    sb.AppendLine("}");
                    break;
            }
        }
    }

    private static void AppendAstCommand(
        DslCommandAstNode commandNode,
        StringBuilder sb,
        int indent)
    {
        var command = new DslCommand(commandNode.Name, commandNode.Args);
        var normalized = command.ToValidCommand(state: null, command);

        AppendIndent(sb, indent);
        sb.Append(normalized.Action);
        foreach (var arg in normalized.Args)
        {
            if (string.IsNullOrWhiteSpace(arg))
                continue;

            sb.Append(' ');
            sb.Append(arg);
        }

        sb.AppendLine(";");
    }

    private static void AppendIndent(StringBuilder sb, int indent)
    {
        if (indent > 0)
            sb.Append(' ', indent);
    }
}
