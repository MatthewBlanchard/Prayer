using System;
using System.Collections.Generic;
using System.Linq;

public static class DslScriptTransformer
{
    public static string NormalizeScript(string dslScript, GameState? state = null)
    {
        if (string.IsNullOrWhiteSpace(dslScript))
            return string.Empty;

        var tree = DslParser.ParseTree(dslScript);
        if (state != null)
            _ = Translate(tree, state);
        return RenderScript(tree).TrimEnd();
    }

    public static string RenderScript(DslAstProgram tree)
    {
        return DslPrettyPrinter.Render(tree);
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
                case DslRepeatAstNode repeatNode:
                {
                    InterpretNodes(repeatNode.Body ?? Array.Empty<DslAstNode>(), state, output);
                    break;
                }
                case DslIfAstNode ifNode:
                {
                    InterpretNodes(ifNode.Body ?? Array.Empty<DslAstNode>(), state, output);
                    break;
                }
                case DslUntilAstNode untilNode:
                {
                    InterpretNodes(untilNode.Body ?? Array.Empty<DslAstNode>(), state, output);
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
            Args = parts.Skip(1).ToList(),
            SourceLine = null
        };
    }
}
