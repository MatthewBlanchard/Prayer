using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

public static class DslInterpreter
{
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

        foreach (var cmd in commands)
        {
            if (string.IsNullOrWhiteSpace(cmd.Action))
                continue;

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
        InterpretNodes(tree.Statements, state, result);
        return result;
    }

    private static void InterpretNodes(
        IReadOnlyList<DslAstNode> nodes,
        GameState? state,
        List<CommandResult> output)
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
                default:
                    throw new FormatException("Unknown DSL AST node.");
            }
        }
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
