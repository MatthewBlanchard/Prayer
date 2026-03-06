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

                case DslRepeatAstNode repeatNode:
                    AppendIndent(sb, indent);
                    sb.AppendLine("repeat {");
                    AppendAstNodes(repeatNode.Body ?? Array.Empty<DslAstNode>(), sb, indent + 2);
                    AppendIndent(sb, indent);
                    sb.AppendLine("}");
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
        if (!string.IsNullOrWhiteSpace(normalized.Arg1))
        {
            sb.Append(' ');
            sb.Append(normalized.Arg1);
        }

        if (normalized.Quantity.HasValue)
        {
            sb.Append(' ');
            sb.Append(normalized.Quantity.Value);
        }

        sb.AppendLine(";");
    }

    private static void AppendIndent(StringBuilder sb, int indent)
    {
        if (indent > 0)
            sb.Append(' ', indent);
    }
}
