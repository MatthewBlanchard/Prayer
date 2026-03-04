using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

public static class DslInterpreter
{
    internal const string RepeatStartAction = "__repeat_start";
    internal const string RepeatEndAction = "__repeat_end";
    internal const string IfStartAction = "__if_start";
    internal const string IfEndAction = "__if_end";
    internal const string UntilStartAction = "__until_start";
    internal const string UntilEndAction = "__until_end";

    public static string NormalizeScript(string dslScript, GameState? state = null)
    {
        if (string.IsNullOrWhiteSpace(dslScript))
            return string.Empty;

        var commands = state == null
            ? Translate(dslScript)
            : Translate(dslScript, state);

        return RenderScript(commands).TrimEnd();
    }

    public static string RenderScript(IReadOnlyList<CommandResult> commands)
    {
        if (commands == null || commands.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        int index = 0;
        AppendRendered(commands, ref index, sb, indent: 0, closeRepeatId: null, closeIfId: null);

        return sb.ToString();
    }

    public static IReadOnlyList<CommandResult> Translate(string dslScript)
    {
        var tree = DslParser.ParseTree(dslScript);
        return Translate(tree, state: null);
    }

    public static IReadOnlyList<CommandResult> Translate(string dslScript, GameState state)
    {
        var tree = DslParser.ParseTree(dslScript);
        return Translate(tree, state);
    }

    public static IReadOnlyList<CommandResult> Translate(DslProgram program)
    {
        if (program == null)
            throw new ArgumentNullException(nameof(program));

        return program.AllSteps
            .Select(ParseStep)
            .ToList();
    }

    public static IReadOnlyList<CommandResult> Translate(DslAstProgram tree)
    {
        return Translate(tree, state: null);
    }

    public static IReadOnlyList<CommandResult> Translate(DslAstProgram tree, GameState? state)
    {
        if (tree == null)
            throw new ArgumentNullException(nameof(tree));

        var result = new List<CommandResult>();
        int nextRepeatId = 0;
        int nextIfId = 0;
        int nextUntilId = 0;
        InterpretNodes(tree.Statements, state, result, ref nextRepeatId, ref nextIfId, ref nextUntilId);
        return result;
    }

    private static void InterpretNodes(
        IReadOnlyList<DslAstNode> nodes,
        GameState? state,
        List<CommandResult> output,
        ref int nextRepeatId,
        ref int nextIfId,
        ref int nextUntilId)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case DslCommandAstNode commandNode:
                {
                    var command = new DslCommand(commandNode.Name, commandNode.Args);
                    CommandResult result;
                    try
                    {
                        result = command.ToValidCommand(state, command);
                    }
                    catch (FormatException ex) when (commandNode.SourceLine > 0)
                    {
                        throw new FormatException($"Line {commandNode.SourceLine}: {ex.Message}", ex);
                    }

                    result.SourceLine = commandNode.SourceLine;
                    output.Add(result);
                    break;
                }
                case DslRepeatAstNode repeatNode:
                {
                    string repeatId = $"r{++nextRepeatId}";
                    output.Add(new CommandResult
                    {
                        Action = RepeatStartAction,
                        Arg1 = repeatId,
                        SourceLine = repeatNode.SourceLine
                    });
                    InterpretNodes(
                        repeatNode.Body ?? Array.Empty<DslAstNode>(),
                        state,
                        output,
                        ref nextRepeatId,
                        ref nextIfId,
                        ref nextUntilId);
                    output.Add(new CommandResult
                    {
                        Action = RepeatEndAction,
                        Arg1 = repeatId,
                        SourceLine = repeatNode.SourceLine
                    });
                    break;
                }
                case DslIfAstNode ifNode:
                {
                    string ifId = $"i{++nextIfId}";
                    string condition = (ifNode.Condition ?? string.Empty).Trim().ToUpperInvariant();
                    output.Add(new CommandResult
                    {
                        Action = IfStartAction,
                        Arg1 = $"{ifId}:{condition}",
                        SourceLine = ifNode.SourceLine
                    });
                    InterpretNodes(
                        ifNode.Body ?? Array.Empty<DslAstNode>(),
                        state,
                        output,
                        ref nextRepeatId,
                        ref nextIfId,
                        ref nextUntilId);
                    output.Add(new CommandResult
                    {
                        Action = IfEndAction,
                        Arg1 = ifId,
                        SourceLine = ifNode.SourceLine
                    });
                    break;
                }
                case DslUntilAstNode untilNode:
                {
                    string untilId = $"u{++nextUntilId}";
                    string condition = (untilNode.Condition ?? string.Empty).Trim().ToUpperInvariant();
                    output.Add(new CommandResult
                    {
                        Action = UntilStartAction,
                        Arg1 = $"{untilId}:{condition}",
                        SourceLine = untilNode.SourceLine
                    });
                    InterpretNodes(
                        untilNode.Body ?? Array.Empty<DslAstNode>(),
                        state,
                        output,
                        ref nextRepeatId,
                        ref nextIfId,
                        ref nextUntilId);
                    output.Add(new CommandResult
                    {
                        Action = UntilEndAction,
                        Arg1 = untilId,
                        SourceLine = untilNode.SourceLine
                    });
                    break;
                }
                default:
                    throw new FormatException("Unknown DSL AST node.");
            }
        }
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

            if (string.Equals(cmd.Action, RepeatStartAction, StringComparison.Ordinal))
            {
                AppendIndent(sb, indent);
                sb.AppendLine("repeat {");
                AppendRendered(commands, ref index, sb, indent + 2, closeRepeatId: cmd.Arg1, closeIfId: null);
                AppendIndent(sb, indent);
                sb.AppendLine("}");
                continue;
            }

            if (string.Equals(cmd.Action, RepeatEndAction, StringComparison.Ordinal))
            {
                if (closeRepeatId != null && string.Equals(cmd.Arg1, closeRepeatId, StringComparison.Ordinal))
                    return;

                continue;
            }

            if (string.Equals(cmd.Action, IfStartAction, StringComparison.Ordinal))
            {
                ParseIfStartArg(cmd.Arg1, out var ifId, out var condition);
                AppendIndent(sb, indent);
                sb.Append("if ");
                sb.Append(string.IsNullOrWhiteSpace(condition) ? "UNKNOWN" : condition);
                sb.AppendLine(" {");
                AppendRendered(commands, ref index, sb, indent + 2, closeRepeatId: null, closeIfId: ifId);
                AppendIndent(sb, indent);
                sb.AppendLine("}");
                continue;
            }

            if (string.Equals(cmd.Action, IfEndAction, StringComparison.Ordinal))
            {
                if (closeIfId != null && string.Equals(cmd.Arg1, closeIfId, StringComparison.Ordinal))
                    return;

                continue;
            }

            if (string.Equals(cmd.Action, UntilStartAction, StringComparison.Ordinal))
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

            if (string.Equals(cmd.Action, UntilEndAction, StringComparison.Ordinal))
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

    internal static bool ParseIfStartArg(string? arg, out string ifId, out string condition)
        => ParseConditionalStartArg(arg, out ifId, out condition);

    internal static bool ParseUntilStartArg(string? arg, out string untilId, out string condition)
        => ParseConditionalStartArg(arg, out untilId, out condition);

    internal static bool ParseConditionalStartArg(string? arg, out string blockId, out string condition)
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

    internal static string? GetIfId(string? ifStartArg)
    {
        return ParseIfStartArg(ifStartArg, out var ifId, out _)
            ? ifId
            : null;
    }

    private static CommandResult ParseStep(string step)
    {
        var parts = step
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new CommandResult
        {
            Action = parts.ElementAtOrDefault(0) ?? "",
            Arg1 = parts.ElementAtOrDefault(1),
            Quantity = int.TryParse(parts.ElementAtOrDefault(2), out int n)
                ? n
                : null,
            SourceLine = null
        };
    }
}
