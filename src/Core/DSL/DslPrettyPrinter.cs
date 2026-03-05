using System;
using System.Collections.Generic;
using System.Text;

internal static class DslPrettyPrinter
{
    public static string Render(IReadOnlyList<CommandResult> commands)
    {
        if (commands == null || commands.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        int index = 0;
        AppendRendered(commands, ref index, sb, indent: 0, closeRepeatId: null, closeIfId: null);

        return sb.ToString();
    }

    public static bool ParseConditionalStartArg(string? arg, out string blockId, out string condition)
    {
        blockId = "";
        condition = "";

        var value = (arg ?? string.Empty).Trim();
        if (value.Length == 0)
            return false;

        int sep = value.IndexOf(':');
        if (sep <= 0 || sep >= value.Length - 1)
            return false;

        blockId = value[..sep].Trim();
        condition = value[(sep + 1)..].Trim();
        return blockId.Length > 0 && condition.Length > 0;
    }

    private static void AppendRendered(
        IReadOnlyList<CommandResult> commands,
        ref int index,
        StringBuilder sb,
        int indent,
        string? closeRepeatId,
        string? closeIfId)
    {
        while (index < commands.Count)
        {
            var cmd = commands[index++];
            if (string.IsNullOrWhiteSpace(cmd.Action))
                continue;

            if (string.Equals(cmd.Action, DslInterpreter.RepeatStartAction, StringComparison.Ordinal))
            {
                AppendIndent(sb, indent);
                sb.AppendLine("repeat {");
                AppendRendered(commands, ref index, sb, indent + 2, closeRepeatId: cmd.Arg1, closeIfId: null);
                AppendIndent(sb, indent);
                sb.AppendLine("}");
                continue;
            }

            if (string.Equals(cmd.Action, DslInterpreter.RepeatEndAction, StringComparison.Ordinal))
            {
                if (closeRepeatId != null && string.Equals(cmd.Arg1, closeRepeatId, StringComparison.Ordinal))
                    return;

                continue;
            }

            if (string.Equals(cmd.Action, DslInterpreter.IfStartAction, StringComparison.Ordinal))
            {
                ParseConditionalStartArg(cmd.Arg1, out var ifId, out var condition);
                AppendIndent(sb, indent);
                sb.Append("if ");
                sb.Append(string.IsNullOrWhiteSpace(condition) ? "UNKNOWN" : condition);
                sb.AppendLine(" {");
                AppendRendered(commands, ref index, sb, indent + 2, closeRepeatId: null, closeIfId: ifId);
                AppendIndent(sb, indent);
                sb.AppendLine("}");
                continue;
            }

            if (string.Equals(cmd.Action, DslInterpreter.IfEndAction, StringComparison.Ordinal))
            {
                if (closeIfId != null && string.Equals(cmd.Arg1, closeIfId, StringComparison.Ordinal))
                    return;

                continue;
            }

            if (string.Equals(cmd.Action, DslInterpreter.UntilStartAction, StringComparison.Ordinal))
            {
                ParseConditionalStartArg(cmd.Arg1, out var untilId, out var condition);
                AppendIndent(sb, indent);
                sb.Append("until ");
                sb.Append(string.IsNullOrWhiteSpace(condition) ? "UNKNOWN" : condition);
                sb.AppendLine(" {");
                AppendRendered(commands, ref index, sb, indent + 2, closeRepeatId: null, closeIfId: untilId);
                AppendIndent(sb, indent);
                sb.AppendLine("}");
                continue;
            }

            if (string.Equals(cmd.Action, DslInterpreter.UntilEndAction, StringComparison.Ordinal))
            {
                if (closeIfId != null && string.Equals(cmd.Arg1, closeIfId, StringComparison.Ordinal))
                    return;

                continue;
            }

            AppendIndent(sb, indent);
            sb.Append(cmd.Action);
            if (!string.IsNullOrWhiteSpace(cmd.Arg1))
            {
                sb.Append(' ');
                sb.Append(cmd.Arg1);
            }

            if (cmd.Quantity.HasValue)
            {
                sb.Append(' ');
                sb.Append(cmd.Quantity.Value);
            }

            sb.AppendLine(";");
        }
    }

    private static void AppendIndent(StringBuilder sb, int indent)
    {
        if (indent > 0)
            sb.Append(' ', indent);
    }
}
