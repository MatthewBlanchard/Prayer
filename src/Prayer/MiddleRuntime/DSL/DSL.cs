using Superpower;
using Superpower.Model;
using Superpower.Parsers;
using System.Text;

public sealed class DslProgram
{
    private readonly Queue<string> _steps;

    public DslProgram(IEnumerable<string> steps)
    {
        _steps = new Queue<string>(steps);
    }

    public bool IsEmpty => _steps.Count == 0;

    public string? Current => _steps.Count > 0 ? _steps.Peek() : null;

    public void Advance()
    {
        if (_steps.Count > 0)
            _steps.Dequeue();
    }

    public IReadOnlyList<string> AllSteps => _steps.ToList();
}

public sealed class DslAstProgram
{
    public DslAstProgram(IReadOnlyList<DslAstNode> statements)
    {
        Statements = statements ?? Array.Empty<DslAstNode>();
    }

    public IReadOnlyList<DslAstNode> Statements { get; }
}

public abstract record DslAstNode;

public sealed record DslCommandAstNode(string Name, IReadOnlyList<string> Args, int SourceLine = 0) : DslAstNode;
public sealed record DslRepeatAstNode(IReadOnlyList<DslAstNode> Body, int SourceLine = 0) : DslAstNode;
public sealed record DslIfAstNode(DslConditionAstNode Condition, IReadOnlyList<DslAstNode> Body, int SourceLine = 0) : DslAstNode;
public sealed record DslUntilAstNode(DslConditionAstNode Condition, IReadOnlyList<DslAstNode> Body, int SourceLine = 0) : DslAstNode;

public static class DslParser
{
    private const string HaltKeyword = "halt";
    private readonly record struct DslMacroDefinition(
        string Description,
        Func<GameState, string?> Resolver);

    private static readonly GameState PromptHelpState = new()
    {
        CurrentPOI = new POIInfo()
    };

    private static readonly IReadOnlyDictionary<string, string[]> PromptArgNameOverrides =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["go"] = new[] { "destination" },
            ["mine"] = new[] { "resource" },
            ["buy"] = new[] { "item", "count" },
            ["sell"] = new[] { "item" },
            ["cancel_buy"] = new[] { "item" },
            ["cancel_sell"] = new[] { "item" },
            ["retrieve"] = new[] { "item", "count" },
            ["stash"] = new[] { "item" },
            ["switch_ship"] = new[] { "ship" },
            ["install_mod"] = new[] { "mod" },
            ["uninstall_mod"] = new[] { "mod" },
            ["buy_ship"] = new[] { "ship_class" },
            ["buy_listed_ship"] = new[] { "listing" },
            ["commission_ship"] = new[] { "ship_class" },
            ["accept_mission"] = new[] { "mission_id" },
            ["abandon_mission"] = new[] { "mission_id" },
            ["sell_ship"] = new[] { "ship" },
            ["list_ship_for_sale"] = new[] { "ship", "price" },
            ["craft"] = new[] { "recipe_id", "count" },
        };

    private static readonly IReadOnlyList<ICommand> Commands =
        BuildCommands();
    private static readonly HashSet<string> CommandNameSet =
        BuildCommandNameSet(Commands);
    private static readonly IReadOnlyDictionary<string, DslCommandSyntax> CommandSyntaxByName =
        BuildCommandSyntaxByName(Commands);
    private static readonly IReadOnlyDictionary<string, DslMacroDefinition> Macros =
        new Dictionary<string, DslMacroDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["home"] = new(
                "your home base id",
                state => state.HomeBase),
            ["here"] = new(
                "your current system id",
                state => state.System),
        };
    private static readonly TextParser<Unit> Ws =
        Character.WhiteSpace.Many().Value(Unit.Value);

    public readonly record struct DslCommandPromptDoc(string Name, string Text);

    private static readonly TextParser<Unit> Ws1 =
        Character.WhiteSpace.AtLeastOnce().Value(Unit.Value);

    private static readonly TextParser<string> Identifier =
        from first in Character.Letter.Or(Character.EqualTo('_'))
        from rest in Character.LetterOrDigit
            .Or(Character.EqualTo('_'))
            .Or(Character.EqualTo('-'))
            .Many()
        select first + new string(rest.ToArray());

    private static readonly TextParser<string> Integer =
        Span.Regex("[0-9]+").Select(x => x.ToStringValue());

    // Command args must support ids like mission UUID fragments that can start
    // with digits and then include letters/hyphens (e.g. "6a1b-...").
    private static readonly TextParser<string> ArgIdentifier =
        Span.Regex("\\$?[A-Za-z0-9_][A-Za-z0-9_-]*").Select(x => x.ToStringValue());

    private static readonly TextParser<string> ArgumentToken =
        ArgIdentifier.Try().Or(Integer);

    // Metric call arg list: zero or more ArgumentTokens separated by commas, inside parens.
    private static readonly TextParser<string[]> MetricArgList =
        from _open in Character.EqualTo('(')
        from args in (
            from _ws in Ws
            from first in ArgumentToken
            from rest in (
                from _ws2 in Ws
                from _c in Character.EqualTo(',')
                from _ws3 in Ws
                from a in ArgumentToken
                select a).Many()
            from _ws4 in Ws
            select new[] { first }.Concat(rest).ToArray()
        ).Try().Or(Ws.Select(_ => Array.Empty<string>()))
        from _close in Character.EqualTo(')')
        select args;

    // A metric call: IDENTIFIER(args...) — name is uppercased.
    private static readonly TextParser<(string Name, string[] Args)> MetricCall =
        from name in Identifier
        from args in MetricArgList
        select (name.ToUpperInvariant(), args);

    private static readonly TextParser<DslNumericOperandAstNode> NumericOperand =
        MetricCall.Try()
            .Select(c => (DslNumericOperandAstNode)new DslMetricCallOperandAstNode(c.Name, c.Args))
        .Or(Integer.Select(n => (DslNumericOperandAstNode)new DslIntegerOperandAstNode(int.Parse(n))));

    private static readonly TextParser<string> ComparisonOp =
        Span.EqualTo(">=").Value(">=").Try()
        .Or(Span.EqualTo("<=").Value("<=").Try())
        .Or(Span.EqualTo("==").Value("==").Try())
        .Or(Span.EqualTo("!=").Value("!=").Try())
        .Or(Span.EqualTo(">").Value(">").Try())
        .Or(Span.EqualTo("<").Value("<"));

    internal static readonly TextParser<DslConditionAstNode> ConditionParser =
        (from left in NumericOperand
         from _ in Ws
         from op in ComparisonOp
         from __ in Ws
         from right in NumericOperand
         select (DslConditionAstNode)new DslComparisonConditionAstNode(left, op, right)).Try()
        .Or(MetricCall.Select(c =>
            (DslConditionAstNode)new DslMetricCallConditionAstNode(c.Name, c.Args)));

    private static readonly TextParser<DslAstNode> CommandAst =
        from commandName in Identifier
        from commandArgs in (
            from _ in Ws1
            from arg in ArgumentToken
            select arg).Many()
        from _ in Ws
        from _semi in Character.EqualTo(';')
        select (DslAstNode)new DslCommandAstNode(commandName, commandArgs);

    private static readonly TextParser<DslAstNode> RepeatAst =
        from _repeat in Span.EqualToIgnoreCase("repeat").Value(Unit.Value)
        from _ in Ws
        from _open in Character.EqualTo('{')
        from __ in Ws
        from body in Superpower.Parse.Ref(() => StatementAst!).Many()
        from ___ in Ws
        from _close in Character.EqualTo('}')
        select (DslAstNode)new DslRepeatAstNode(body);

    private static readonly TextParser<DslAstNode> IfAst =
        from _if in Span.EqualToIgnoreCase("if").Value(Unit.Value)
        from _ in Ws1
        from condition in ConditionParser
        from __ in Ws
        from _open in Character.EqualTo('{')
        from ___ in Ws
        from body in Superpower.Parse.Ref(() => StatementAst!).Many()
        from ____ in Ws
        from _close in Character.EqualTo('}')
        select (DslAstNode)new DslIfAstNode(condition, body);

    private static readonly TextParser<DslAstNode> UntilAst =
        from _until in Span.EqualToIgnoreCase("until").Value(Unit.Value)
        from _ in Ws1
        from condition in ConditionParser
        from __ in Ws
        from _open in Character.EqualTo('{')
        from ___ in Ws
        from body in Superpower.Parse.Ref(() => StatementAst!).Many()
        from ____ in Ws
        from _close in Character.EqualTo('}')
        select (DslAstNode)new DslUntilAstNode(condition, body);

    private static readonly TextParser<DslAstNode> StatementCore =
        input => ParseStatementAst(input);

    private static readonly TextParser<DslAstNode> StatementAst =
        from statement in StatementCore
        from _ in Ws
        select statement;

    private static readonly TextParser<DslAstProgram> ProgramAstParser =
        from _ in Ws
        from statements in (
            from statement in StatementAst!
            select statement).Many()
        select new DslAstProgram(statements);

    // Avoid backtracking through command parsing for keyword-led blocks so
    // parse errors are attributed to the actual failing token/location.
    private static Result<DslAstNode> ParseStatementAst(TextSpan input)
    {
        if (StartsWithKeyword(input, "until", requireWhitespaceAfter: true))
            return UntilAst(input);

        if (StartsWithKeyword(input, "if", requireWhitespaceAfter: true))
            return IfAst(input);

        if (StartsWithKeyword(input, "repeat", requireWhitespaceAfter: false))
            return RepeatAst(input);

        return CommandAst(input);
    }

    private static bool StartsWithKeyword(TextSpan input, string keyword, bool requireWhitespaceAfter)
    {
        if (input.Length < keyword.Length)
            return false;

        for (int i = 0; i < keyword.Length; i++)
        {
            if (char.ToUpperInvariant(input[i]) != char.ToUpperInvariant(keyword[i]))
                return false;
        }

        if (input.Length == keyword.Length)
            return !requireWhitespaceAfter;

        char next = input[keyword.Length];
        if (requireWhitespaceAfter)
            return char.IsWhiteSpace(next);

        return char.IsWhiteSpace(next) || next == '{';
    }

    public static bool TryParseCondition(string? text, out DslConditionAstNode? condition, out string? error)
    {
        condition = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "condition is empty";
            return false;
        }

        var result = ConditionParser(new TextSpan(text.Trim()));
        if (!result.HasValue)
        {
            error = result.ToString();
            return false;
        }

        condition = result.Value;
        return true;
    }

    static DslParser()
    {
        ValidateCommandMetadata();
    }

    public static DslAstProgram ParseTree(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new DslAstProgram(Array.Empty<DslAstNode>());

        try
        {
            var tree = ProgramAstParser.Parse(text);
            tree = AnnotateSourceLines(text, tree);
            ValidateTree(tree);
            return tree;
        }
        catch (ParseException ex)
        {
            throw new FormatException($"Invalid DSL script: {ex.Message}", ex);
        }
    }

    private readonly record struct StatementLineEntry(int Line);

    private static DslAstProgram AnnotateSourceLines(string text, DslAstProgram tree)
    {
        var entries = ExtractStatementLineEntries(text);
        int entryIndex = 0;

        var annotated = new List<DslAstNode>(tree.Statements.Count);
        foreach (var node in tree.Statements)
            annotated.Add(AnnotateNodeLine(node, entries, ref entryIndex));

        return new DslAstProgram(annotated);
    }

    private static DslAstNode AnnotateNodeLine(
        DslAstNode node,
        IReadOnlyList<StatementLineEntry> entries,
        ref int entryIndex)
    {
        return node switch
        {
            DslCommandAstNode commandNode => commandNode with
            {
                SourceLine = ConsumeLine(entries, ref entryIndex)
            },
            DslRepeatAstNode repeatNode => AnnotateRepeatNode(repeatNode, entries, ref entryIndex),
            DslIfAstNode ifNode => AnnotateIfNode(ifNode, entries, ref entryIndex),
            DslUntilAstNode untilNode => AnnotateUntilNode(untilNode, entries, ref entryIndex),
            _ => node
        };
    }

    private static DslRepeatAstNode AnnotateRepeatNode(
        DslRepeatAstNode repeatNode,
        IReadOnlyList<StatementLineEntry> entries,
        ref int entryIndex)
    {
        var body = new List<DslAstNode>(repeatNode.Body.Count);
        foreach (var child in repeatNode.Body)
            body.Add(AnnotateNodeLine(child, entries, ref entryIndex));

        int sourceLine = repeatNode.SourceLine > 0
            ? repeatNode.SourceLine
            : InferRepeatSourceLine(repeatNode with { Body = body }, entries, entryIndex);

        return repeatNode with
        {
            SourceLine = sourceLine,
            Body = body
        };
    }

    private static DslIfAstNode AnnotateIfNode(
        DslIfAstNode ifNode,
        IReadOnlyList<StatementLineEntry> entries,
        ref int entryIndex)
    {
        var body = new List<DslAstNode>(ifNode.Body.Count);
        foreach (var child in ifNode.Body)
            body.Add(AnnotateNodeLine(child, entries, ref entryIndex));

        int sourceLine = ifNode.SourceLine > 0
            ? ifNode.SourceLine
            : InferIfSourceLine(ifNode with { Body = body }, entries, entryIndex);

        return ifNode with
        {
            SourceLine = sourceLine,
            Body = body
        };
    }

    private static DslUntilAstNode AnnotateUntilNode(
        DslUntilAstNode untilNode,
        IReadOnlyList<StatementLineEntry> entries,
        ref int entryIndex)
    {
        var body = new List<DslAstNode>(untilNode.Body.Count);
        foreach (var child in untilNode.Body)
            body.Add(AnnotateNodeLine(child, entries, ref entryIndex));

        int sourceLine = untilNode.SourceLine > 0
            ? untilNode.SourceLine
            : InferUntilSourceLine(untilNode with { Body = body }, entries, entryIndex);

        return untilNode with
        {
            SourceLine = sourceLine,
            Body = body
        };
    }

    private static int InferRepeatSourceLine(
        DslRepeatAstNode repeatNode,
        IReadOnlyList<StatementLineEntry> entries,
        int entryIndex)
    {
        if (repeatNode.Body != null && repeatNode.Body.Count > 0)
        {
            var firstLine = FindFirstCommandLine(repeatNode.Body);
            if (firstLine > 0)
                return firstLine;
        }

        if (entryIndex < entries.Count)
            return entries[entryIndex].Line;

        return 1;
    }

    private static int InferIfSourceLine(
        DslIfAstNode ifNode,
        IReadOnlyList<StatementLineEntry> entries,
        int entryIndex)
    {
        if (ifNode.Body != null && ifNode.Body.Count > 0)
        {
            var firstLine = FindFirstCommandLine(ifNode.Body);
            if (firstLine > 0)
                return firstLine;
        }

        if (entryIndex < entries.Count)
            return entries[entryIndex].Line;

        return 1;
    }

    private static int InferUntilSourceLine(
        DslUntilAstNode untilNode,
        IReadOnlyList<StatementLineEntry> entries,
        int entryIndex)
    {
        if (untilNode.Body != null && untilNode.Body.Count > 0)
        {
            var firstLine = FindFirstCommandLine(untilNode.Body);
            if (firstLine > 0)
                return firstLine;
        }

        if (entryIndex < entries.Count)
            return entries[entryIndex].Line;

        return 1;
    }

    private static int FindFirstCommandLine(IReadOnlyList<DslAstNode> nodes)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case DslCommandAstNode commandNode when commandNode.SourceLine > 0:
                    return commandNode.SourceLine;
                case DslRepeatAstNode repeatNode:
                {
                    var nested = FindFirstCommandLine(repeatNode.Body);
                    if (nested > 0)
                        return nested;
                    break;
                }
                case DslIfAstNode ifNode:
                {
                    var nested = FindFirstCommandLine(ifNode.Body);
                    if (nested > 0)
                        return nested;
                    break;
                }
                case DslUntilAstNode untilNode:
                {
                    var nested = FindFirstCommandLine(untilNode.Body);
                    if (nested > 0)
                        return nested;
                    break;
                }
            }
        }

        return 0;
    }

    private static int ConsumeLine(
        IReadOnlyList<StatementLineEntry> entries,
        ref int entryIndex)
    {
        if (entryIndex >= entries.Count)
            return 1;

        return entries[entryIndex++].Line;
    }

    private static IReadOnlyList<StatementLineEntry> ExtractStatementLineEntries(string text)
    {
        var entries = new List<StatementLineEntry>();
        bool expectingCommand = true;
        int currentCommandLine = 1;
        int line = 1;

        int i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c))
            {
                if (c == '\n')
                    line++;
                i++;
                continue;
            }

            if (expectingCommand)
            {
                if (IsIdentifierStart(c))
                {
                    int identifierLine = line;
                    string token = ReadIdentifier(text, ref i);
                    if (IsRepeatToken(token, text, i))
                    {
                        SkipRepeatHeader(text, ref i, ref line);
                        expectingCommand = true;
                        continue;
                    }
                    if (IsIfToken(token, text, i))
                    {
                        SkipIfHeader(text, ref i, ref line);
                        expectingCommand = true;
                        continue;
                    }
                    if (IsUntilToken(token, text, i))
                    {
                        SkipUntilHeader(text, ref i, ref line);
                        expectingCommand = true;
                        continue;
                    }

                    currentCommandLine = identifierLine;
                    expectingCommand = false;
                    continue;
                }

                i++;
                continue;
            }

            if (c == ';')
            {
                entries.Add(new StatementLineEntry(currentCommandLine));
                expectingCommand = true;
                i++;
                continue;
            }

            if (c == '\n')
                line++;

            i++;
        }

        return entries;
    }

    private static bool IsRepeatToken(string token, string text, int indexAfterToken)
    {
        if (!token.Equals("repeat", StringComparison.OrdinalIgnoreCase))
            return false;

        int i = indexAfterToken;
        SkipWhitespace(text, ref i);
        return i < text.Length && text[i] == '{';
    }

    private static bool IsIfToken(string token, string text, int indexAfterToken)
    {
        if (!token.Equals("if", StringComparison.OrdinalIgnoreCase))
            return false;

        int i = indexAfterToken;
        SkipWhitespace(text, ref i);
        return HasConditionAndOpeningBrace(text, i);
    }

    private static bool IsUntilToken(string token, string text, int indexAfterToken)
    {
        if (!token.Equals("until", StringComparison.OrdinalIgnoreCase))
            return false;

        int i = indexAfterToken;
        SkipWhitespace(text, ref i);
        return HasConditionAndOpeningBrace(text, i);
    }

    private static void SkipRepeatHeader(string text, ref int index, ref int line)
    {
        SkipWhitespaceAndCountLines(text, ref index, ref line);
        if (index < text.Length && text[index] == '{')
            index++;
    }

    private static void SkipIfHeader(string text, ref int index, ref int line)
    {
        SkipConditionHeader(text, ref index, ref line);
    }

    private static void SkipUntilHeader(string text, ref int index, ref int line)
    {
        SkipConditionHeader(text, ref index, ref line);
    }

    private static bool HasConditionAndOpeningBrace(string text, int index)
    {
        bool sawNonWhitespace = false;
        int i = index;

        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\r' || c == '\n')
                return false;

            if (c == '{')
                return sawNonWhitespace;

            if (!char.IsWhiteSpace(c))
                sawNonWhitespace = true;

            i++;
        }

        return false;
    }

    private static void SkipConditionHeader(string text, ref int index, ref int line)
    {
        while (index < text.Length)
        {
            char c = text[index];
            if (c == '\n')
                line++;

            if (c == '{')
            {
                index++;
                return;
            }

            index++;
        }
    }

    private static void SkipWhitespace(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
    }

    private static void SkipWhitespaceAndCountLines(string text, ref int index, ref int line)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            if (text[index] == '\n')
                line++;
            index++;
        }
    }

    private static bool IsIdentifierStart(char c)
    {
        return char.IsLetter(c) || c == '_';
    }

    private static bool IsIdentifierPart(char c)
    {
        return char.IsLetterOrDigit(c) || c == '_' || c == '-';
    }

    private static string ReadIdentifier(string text, ref int index)
    {
        int start = index;
        index++;

        while (index < text.Length && IsIdentifierPart(text[index]))
            index++;

        return text.Substring(start, index - start);
    }

    public static DslProgram Parse(string text)
    {
        var tree = ParseTree(text);
        var commands = DslScriptTransformer.Translate(tree);
        var steps = commands.Select(c =>
        {
            var parts = new List<string> { c.Action };
            if (!string.IsNullOrWhiteSpace(c.Arg1))
                parts.Add(c.Arg1!);
            if (c.Quantity.HasValue)
                parts.Add(c.Quantity.Value.ToString());
            return string.Join(" ", parts);
        });
        return new DslProgram(steps);
    }

    public static string BuildLlamaCppGrammar()
    {
        var sb = new StringBuilder();

        sb.AppendLine("root ::= ws script ws");
        sb.AppendLine("ws ::= [ \\t\\n\\r]*");
        sb.AppendLine("identifier ::= [\\$]?[A-Za-z_][A-Za-z0-9_-]*");
        sb.AppendLine("integer ::= [0-9]+");
        sb.AppendLine();
        BuildGrammar(sb);

        return sb.ToString().TrimEnd();
    }

    public static string BuildPromptDslReferenceBlock()
    {
        return BuildPromptDslReferenceBlock(
            userInput: null,
            exampleScripts: null);
    }

    public static string BuildPromptDslReferenceBlock(
        string? userInput,
        IReadOnlyList<string>? exampleScripts)
    {
        var commands = SelectPromptCommands(
            userInput,
            exampleScripts,
            preferredCommandNames: null);
        return BuildPromptDslReferenceBlockCore(commands);
    }

    public static string BuildPromptDslReferenceBlock(
        string? userInput,
        IReadOnlyList<string>? exampleScripts,
        IReadOnlyList<string>? preferredCommandNames)
    {
        var commands = SelectPromptCommands(
            userInput,
            exampleScripts,
            preferredCommandNames);
        return BuildPromptDslReferenceBlockCore(commands);
    }

    public static IReadOnlyList<DslCommandPromptDoc> GetPromptCommandDocs()
    {
        return Commands
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c =>
            {
                var help = (c.BuildHelp(PromptHelpState) ?? string.Empty).Trim();
                return new DslCommandPromptDoc(
                    c.Name,
                    $"{c.Name} {help}".Trim());
            })
            .ToList();
    }

    private static string BuildPromptDslReferenceBlockCore(IReadOnlyList<ICommand> commands)
    {
        var ordered = commands
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var entries = ordered
            .Select(c => (Command: c, Syntax: CommandSyntaxByName[c.Name]))
            .ToList();

        var commandReferences = entries
            .Select(e => BuildPromptCommandReferenceLine(e.Command, e.Syntax))
            .ToList();
        commandReferences.Add("halt; (args: none) -> stop script execution");

        var sb = new StringBuilder();
        sb.AppendLine("DSL command reference (terminate commands with ;):");

        if (commandReferences.Count > 0)
        {
            sb.AppendLine("Commands:");
            foreach (var commandReference in commandReferences)
                sb.AppendLine($"- {commandReference}");
        }

        sb.AppendLine();
        sb.AppendLine("Keywords:");
        sb.AppendLine("- repeat: infinite runtime loop block");
        sb.AppendLine("- until: runtime loop block that exits when a condition is true");
        sb.AppendLine("- if: conditional block executed only when a condition is true");
        sb.AppendLine("- halt: stop script execution");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Blocks are supported via: repeat { ... }");
        sb.AppendLine("- Conditional blocks are supported via: if <CONDITION> { ... }");
        sb.AppendLine("- Until blocks are supported via: until <CONDITION> { ... }");
        if (Macros.Count > 0)
        {
            var macroReferences = Macros
                .OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
                .Select(m => $"`${m.Key}` expands to {m.Value.Description}");
            sb.AppendLine($"- Text macros: {string.Join(", ", macroReferences)}.");
        }
        var booleanSigs = DslConditionCatalog.BooleanPredicates
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => FormatPredicateSig(p.Name, p.ParamNames))
            .ToList();
        var numericSigs = DslConditionCatalog.NumericPredicates
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => FormatPredicateSig(p.Name, p.ParamNames))
            .ToList();
        if (booleanSigs.Count > 0)
            sb.AppendLine($"- Boolean conditions: {string.Join(", ", booleanSigs)}");
        if (numericSigs.Count > 0)
            sb.AppendLine($"- Numeric conditions: {string.Join(", ", numericSigs)}");
        sb.AppendLine("- All commands still end with ';' inside repeat blocks.");
        sb.AppendLine("- Do not add a trailing 'halt;' at script end unless user explicitly asks to stop/pause.");
        sb.AppendLine();

        return sb.ToString();
    }

    private static IReadOnlyList<ICommand> SelectPromptCommands(
        string? userInput,
        IReadOnlyList<string>? exampleScripts,
        IReadOnlyList<string>? preferredCommandNames)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in preferredCommandNames ?? Array.Empty<string>())
        {
            if (CommandNameSet.Contains(name))
                selected.Add(name);
        }

        foreach (var name in ExtractCommandsFromExampleScripts(exampleScripts))
            selected.Add(name);

        foreach (var name in SelectCommandsByPromptSimilarity(userInput))
            selected.Add(name);

        if (selected.Count == 0)
        {
            return Commands
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();
        }

        return Commands
            .Where(c => selected.Contains(c.Name))
            .ToList();
    }

    private static IReadOnlyList<string> ExtractCommandsFromExampleScripts(
        IReadOnlyList<string>? exampleScripts)
    {
        if (exampleScripts == null || exampleScripts.Count == 0)
            return Array.Empty<string>();

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var script in exampleScripts)
        {
            if (string.IsNullOrWhiteSpace(script))
                continue;

            try
            {
                var tree = ParseTree(script);
                CollectCommandNames(tree.Statements, names);
            }
            catch
            {
                // Best-effort extraction; invalid examples should not break prompt building.
            }
        }

        return names.ToList();
    }

    private static void CollectCommandNames(
        IReadOnlyList<DslAstNode> nodes,
        HashSet<string> names)
    {
        foreach (var node in nodes ?? Array.Empty<DslAstNode>())
        {
            switch (node)
            {
                case DslCommandAstNode commandNode:
                {
                    var normalized = (commandNode.Name ?? string.Empty).Trim().ToLowerInvariant();
                    if (CommandNameSet.Contains(normalized))
                        names.Add(normalized);
                    break;
                }
                case DslRepeatAstNode repeatNode:
                    CollectCommandNames(repeatNode.Body ?? Array.Empty<DslAstNode>(), names);
                    break;
                case DslIfAstNode ifNode:
                    CollectCommandNames(ifNode.Body ?? Array.Empty<DslAstNode>(), names);
                    break;
                case DslUntilAstNode untilNode:
                    CollectCommandNames(untilNode.Body ?? Array.Empty<DslAstNode>(), names);
                    break;
            }
        }
    }

    private static IReadOnlyList<string> SelectCommandsByPromptSimilarity(string? userInput)
    {
        var terms = BuildPromptTerms(userInput);
        if (terms.Count == 0)
            return Array.Empty<string>();

        var matches = new List<string>();
        foreach (var command in Commands)
        {
            var haystack =
                $"{command.Name} {(command.BuildHelp(PromptHelpState) ?? string.Empty)}"
                .ToLowerInvariant();
            if (terms.Any(t => haystack.Contains(t, StringComparison.Ordinal)))
                matches.Add(command.Name);
        }

        return matches;
    }

    private static IReadOnlyList<string> BuildPromptTerms(string? userInput)
    {
        if (string.IsNullOrWhiteSpace(userInput))
            return Array.Empty<string>();

        var tokens = userInput
            .ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\r', '\n', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return tokens;
    }

    internal static string NormalizeCommandStep(
        string commandName,
        IReadOnlyList<string>? commandArgs,
        GameState? state = null)
    {
        var token = commandName.ToLowerInvariant();
        var args = (commandArgs ?? Array.Empty<string>())
            .Select(arg => ExpandMacroArg(arg, state))
            .ToList();
        string normalizedName;
        DslCommandSyntax syntax;

        if (CommandSyntaxByName.TryGetValue(token, out var directSyntax))
        {
            normalizedName = token;
            syntax = directSyntax;
        }
        else if (args.Count == 0 &&
                 TrySplitCollapsedCommand(commandName, out var splitName, out var splitArg))
        {
            normalizedName = splitName;
            syntax = CommandSyntaxByName[splitName];
            args.Add(splitArg);
        }
        else
        {
            normalizedName = token;
            return args.Count == 0
                ? normalizedName
                : $"{normalizedName} {string.Join(" ", args)}";
        }

        var specs = ResolveArgSpecs(syntax);
        if (specs.Count == 0)
        {
            return normalizedName;
        }

        for (int i = args.Count; i < specs.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(specs[i].DefaultValue))
                args.Add(specs[i].DefaultValue!);
        }

        return args.Count == 0
            ? normalizedName
            : $"{normalizedName} {string.Join(" ", args)}";
    }

    internal static IReadOnlyList<DslArgumentSpec> GetArgSpecsForCommand(string commandName)
    {
        if (string.IsNullOrWhiteSpace(commandName))
            return Array.Empty<DslArgumentSpec>();

        if (!CommandSyntaxByName.TryGetValue(commandName, out var syntax))
            return Array.Empty<DslArgumentSpec>();

        return ResolveArgSpecs(syntax);
    }

    private static IReadOnlyList<ICommand> BuildCommands()
    {
        return UniqueByName(CommandCatalog.All);
    }

    private static HashSet<string> BuildCommandNameSet(
        IReadOnlyList<ICommand> commands)
    {
        return commands
            .Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, DslCommandSyntax> BuildCommandSyntaxByName(
        IReadOnlyList<ICommand> commands)
    {
        var map = new Dictionary<string, DslCommandSyntax>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in commands)
        {
            if (map.ContainsKey(command.Name))
                continue;

            map[command.Name] = command.GetDslSyntax() ?? new DslCommandSyntax();
        }

        return map;
    }

    private static void ValidateCommandMetadata()
    {
        foreach (var overrideName in PromptArgNameOverrides.Keys)
        {
            if (!CommandSyntaxByName.ContainsKey(overrideName))
            {
                throw new InvalidOperationException(
                    $"Prompt argument override references unknown command '{overrideName}'.");
            }
        }

        foreach (var command in Commands)
        {
            if (!CommandSyntaxByName.TryGetValue(command.Name, out var syntax))
                throw new InvalidOperationException($"Missing DSL syntax metadata for command '{command.Name}'.");

            var specs = ResolveArgSpecs(syntax);
            if (!PromptArgNameOverrides.TryGetValue(command.Name, out var overrideNames))
                continue;

            if (overrideNames.Length > specs.Count)
            {
                throw new InvalidOperationException(
                    $"Prompt argument override for command '{command.Name}' has {overrideNames.Length} names, but command exposes {specs.Count} DSL args.");
            }
        }
    }

    private static IReadOnlyList<ICommand> UniqueByName(IReadOnlyList<ICommand> commands)
        => commands
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

    private static void BuildGrammar(StringBuilder sb)
    {
        var commands = Commands
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var statementRules = new List<string>();

        foreach (var command in commands)
        {
            var commandName = command.Name.ToLowerInvariant();
            var syntax = CommandSyntaxByName[commandName];
            var commandRuleName = $"cmd_{RuleToken(commandName)}";
            var headPattern = BuildCommandHeadPattern(commandName, syntax);

            sb.AppendLine($"{commandRuleName} ::= {headPattern} ws \";\"");

            statementRules.Add(commandRuleName);
        }

        sb.AppendLine("condition_expr ::= [^\\{\\n\\r][^\\{\\n\\r]*");
        sb.AppendLine("repeat_stmt ::= \"repeat\" ws \"{\" ws statement* ws \"}\"");
        sb.AppendLine("if_stmt ::= \"if\" ws condition_expr ws \"{\" ws statement* ws \"}\"");
        sb.AppendLine("until_stmt ::= \"until\" ws condition_expr ws \"{\" ws statement* ws \"}\"");
        sb.AppendLine("halt_stmt ::= \"halt\" ws \";\"");
        sb.AppendLine($"statement ::= {string.Join(" | ", statementRules)} | repeat_stmt | if_stmt | until_stmt | halt_stmt");
        sb.AppendLine("script ::= (ws statement)*");
        sb.AppendLine();
    }

    private static string BuildCommandHeadPattern(string commandName, DslCommandSyntax syntax)
    {
        var nameLiteral = Quote(commandName);
        var specs = ResolveArgSpecs(syntax);
        if (specs.Count == 0)
            return nameLiteral;

        var requiredCount = specs.Count(s => s.Required && string.IsNullOrWhiteSpace(s.DefaultValue));
        var maxCount = specs.Count;
        var patterns = new List<string>();
        for (int count = requiredCount; count <= maxCount; count++)
        {
            var parts = new List<string> { nameLiteral };
            for (int i = 0; i < count; i++)
                parts.Add($"ws {ArgKindPattern(specs[i].Kind)}");
            patterns.Add(string.Join(" ", parts));
        }

        return patterns.Count == 1
            ? patterns[0]
            : $"({string.Join(" | ", patterns)})";
    }

    private static IReadOnlyList<DslArgumentSpec> ResolveArgSpecs(DslCommandSyntax syntax)
    {
        if (syntax.ArgSpecs != null && syntax.ArgSpecs.Count > 0)
            return syntax.ArgSpecs;

        if (syntax.ArgKind == DslArgKind.None)
            return Array.Empty<DslArgumentSpec>();

        return new[] { new DslArgumentSpec(syntax.ArgKind, syntax.ArgRequired, syntax.DefaultArg) };
    }

    private static string BuildPromptCommandSignature(string commandName, DslCommandSyntax syntax)
    {
        var specs = ResolveArgSpecs(syntax);
        if (specs.Count == 0)
            return $"{commandName};";

        var argTokens = BuildPromptArgTokens(commandName, specs);

        return $"{commandName} {string.Join(" ", argTokens)};";
    }

    private static string BuildPromptCommandReferenceLine(ICommand command, DslCommandSyntax syntax)
    {
        var signature = BuildPromptCommandSignature(command.Name, syntax);
        var specs = ResolveArgSpecs(syntax);
        var argsSummary = BuildPromptArgsSummary(command.Name, specs);
        var description = BuildPromptCommandDescription(command);

        if (string.IsNullOrWhiteSpace(argsSummary))
        {
            return string.IsNullOrWhiteSpace(description)
                ? signature
                : $"{signature} -> {description}";
        }

        return string.IsNullOrWhiteSpace(description)
            ? $"{signature} (args: {argsSummary})"
            : $"{signature} (args: {argsSummary}) -> {description}";
    }

    private static string BuildPromptArgsSummary(
        string commandName,
        IReadOnlyList<DslArgumentSpec> specs)
    {
        if (specs.Count == 0)
            return "none";

        var tokens = BuildPromptArgTokens(commandName, specs);
        var details = new List<string>(specs.Count);

        for (int i = 0; i < specs.Count; i++)
        {
            var token = tokens[i].Trim();
            var trimmed = token.Trim('<', '>');
            bool optional = trimmed.EndsWith("?", StringComparison.Ordinal);
            var argName = optional
                ? trimmed[..^1]
                : trimmed;
            var typeLabel = DescribePromptArgType(specs[i]);
            var requiredLabel = optional ? "optional" : "required";
            details.Add($"{argName}:{typeLabel} ({requiredLabel})");
        }

        return string.Join(", ", details);
    }

    private static string DescribePromptArgType(DslArgumentSpec spec)
    {
        if (spec.Kind.HasFlag(DslArgKind.Integer) &&
            !spec.Kind.HasFlag(DslArgKind.Any) &&
            !spec.Kind.HasFlag(DslArgKind.Item) &&
            !spec.Kind.HasFlag(DslArgKind.System) &&
            !spec.Kind.HasFlag(DslArgKind.Enum))
        {
            return "int";
        }

        if (spec.Kind.HasFlag(DslArgKind.Enum))
        {
            if (!string.IsNullOrWhiteSpace(spec.EnumType))
                return $"enum:{spec.EnumType}";
            if (spec.EnumValues != null && spec.EnumValues.Count > 0)
                return $"enum[{string.Join("|", spec.EnumValues)}]";
            return "enum";
        }

        if (spec.Kind.HasFlag(DslArgKind.System) && spec.Kind.HasFlag(DslArgKind.Item))
            return "system_or_item";
        if (spec.Kind.HasFlag(DslArgKind.System) && spec.Kind.HasFlag(DslArgKind.Any))
            return "system_or_identifier";
        if (spec.Kind.HasFlag(DslArgKind.Item) && spec.Kind.HasFlag(DslArgKind.Any))
            return "item_or_identifier";
        if (spec.Kind.HasFlag(DslArgKind.System))
            return "system";
        if (spec.Kind.HasFlag(DslArgKind.Item))
            return "item";
        if (spec.Kind.HasFlag(DslArgKind.Any))
            return "identifier";
        if (spec.Kind.HasFlag(DslArgKind.Integer))
            return "int";

        return "value";
    }

    private static string BuildPromptCommandDescription(ICommand command)
    {
        var help = (command.BuildHelp(PromptHelpState) ?? "").Trim();
        if (help.Length == 0)
            return "";

        if (help.StartsWith("-", StringComparison.Ordinal))
            help = help[1..].TrimStart();

        int arrowIndex = help.IndexOf("→", StringComparison.Ordinal);
        if (arrowIndex < 0)
            arrowIndex = help.IndexOf("->", StringComparison.Ordinal);

        if (arrowIndex >= 0)
        {
            var description = help[(arrowIndex + (help[arrowIndex] == '→' ? 1 : 2))..].Trim();
            if (description.Length > 0)
                return description;
        }

        return help;
    }

    private static IReadOnlyList<string> BuildPromptArgTokens(
        string commandName,
        IReadOnlyList<DslArgumentSpec> specs)
    {
        PromptArgNameOverrides.TryGetValue(commandName, out var overrideNames);
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tokens = new List<string>(specs.Count);

        for (int i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            bool optional = !spec.Required || !string.IsNullOrWhiteSpace(spec.DefaultValue);
            var baseName =
                overrideNames != null &&
                i < overrideNames.Length &&
                !string.IsNullOrWhiteSpace(overrideNames[i])
                    ? overrideNames[i]
                    : InferPromptArgBaseName(spec.Kind);

            used.TryGetValue(baseName, out var seenCount);
            var tokenName = seenCount == 0 ? baseName : $"{baseName}{seenCount + 1}";
            used[baseName] = seenCount + 1;

            tokens.Add(optional ? $"<{tokenName}?>" : $"<{tokenName}>");
        }

        return tokens;
    }

    private static string InferPromptArgBaseName(DslArgKind kind)
        => kind switch
        {
            _ when kind.HasFlag(DslArgKind.Integer) &&
                   !kind.HasFlag(DslArgKind.Item) &&
                   !kind.HasFlag(DslArgKind.System) &&
                   !kind.HasFlag(DslArgKind.Enum) &&
                   !kind.HasFlag(DslArgKind.Any) => "count",
            _ when kind.HasFlag(DslArgKind.Item) && kind.HasFlag(DslArgKind.System) => "target",
            _ when kind.HasFlag(DslArgKind.Item) => "item",
            _ when kind.HasFlag(DslArgKind.System) => "system",
            _ when kind.HasFlag(DslArgKind.Enum) => "option",
            _ => "value"
        };

    private static string FormatPredicateSig(string name, string[] paramNames)
        => paramNames.Length == 0
            ? $"{name}()"
            : $"{name}({string.Join(", ", paramNames.Select(p => $"<{p}>"))})";

    private static string ArgKindPattern(DslArgKind kind)
    {
        bool allowsInteger = kind.HasFlag(DslArgKind.Integer);
        bool allowsIdentifier = kind.HasFlag(DslArgKind.Any) ||
                                kind.HasFlag(DslArgKind.Item) ||
                                kind.HasFlag(DslArgKind.System) ||
                                kind.HasFlag(DslArgKind.Enum);

        if (allowsInteger && allowsIdentifier)
            return "(integer | identifier)";

        if (allowsInteger)
            return "integer";

        return "identifier";
    }

    private static string RuleToken(string value)
        => string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));

    private static string Quote(string value)
        => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    private static bool IsValidIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        int start = 0;
        if (value[0] == '$')
        {
            if (value.Length == 1)
                return false;
            start = 1;
        }

        if (!(char.IsLetterOrDigit(value[start]) || value[start] == '_'))
            return false;

        for (int i = start + 1; i < value.Length; i++)
        {
            var ch = value[i];
            if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-'))
                return false;
        }

        return true;
    }

    private static bool TrySplitCollapsedCommand(
        string token,
        out string commandName,
        out string arg)
    {
        commandName = "";
        arg = "";

        var candidates = CommandNameSet
            .Where(name => token.Length > name.Length &&
                           token.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(name => name.Length);

        foreach (var candidate in candidates)
        {
            if (!CommandSyntaxByName.TryGetValue(candidate, out var syntax))
                continue;

            var specs = ResolveArgSpecs(syntax);
            if (specs.Count != 1)
                continue;
            var firstKind = specs[0].Kind;
            bool allowsIdentifier = firstKind.HasFlag(DslArgKind.Any) ||
                                    firstKind.HasFlag(DslArgKind.Item) ||
                                    firstKind.HasFlag(DslArgKind.System) ||
                                    firstKind.HasFlag(DslArgKind.Enum);
            if (!allowsIdentifier)
                continue;

            var remainder = token[candidate.Length..];
            if (!IsValidIdentifier(remainder))
                continue;

            commandName = candidate.ToLowerInvariant();
            arg = remainder;
            return true;
        }

        return false;
    }

    private static void ValidateTree(DslAstProgram tree)
    {
        ValidateNodes(tree.Statements);
    }

    private static void ValidateNodes(
        IReadOnlyList<DslAstNode> nodes)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case DslCommandAstNode commandNode:
                {
                    var normalizedName = (commandNode.Name ?? "").Trim().ToLowerInvariant();
                    var args = commandNode.Args ?? Array.Empty<string>();

                    if (!IsCommandAllowed(normalizedName, args))
                    {
                        throw new FormatException(
                            $"Command '{commandNode.Name}' is not recognized.");
                    }

                    if (CommandSyntaxByName.TryGetValue(normalizedName, out var commandSyntax))
                        ValidateCommandArgs(normalizedName, args, commandSyntax);

                    break;
                }
                case DslRepeatAstNode repeatNode:
                {
                    ValidateNodes(repeatNode.Body ?? Array.Empty<DslAstNode>());
                    break;
                }
                case DslIfAstNode ifNode:
                {
                    if (!DslBooleanEvaluator.TryValidateCondition(ifNode.Condition, out var error))
                    {
                        throw new FormatException(
                            $"Invalid condition '{DslBooleanEvaluator.RenderCondition(ifNode.Condition)}': {error}.");
                    }

                    ValidateNodes(ifNode.Body ?? Array.Empty<DslAstNode>());
                    break;
                }
                case DslUntilAstNode untilNode:
                {
                    if (!DslBooleanEvaluator.TryValidateCondition(untilNode.Condition, out var error))
                    {
                        throw new FormatException(
                            $"Invalid condition '{DslBooleanEvaluator.RenderCondition(untilNode.Condition)}': {error}.");
                    }

                    ValidateNodes(untilNode.Body ?? Array.Empty<DslAstNode>());
                    break;
                }
                default:
                    throw new FormatException("Unknown DSL AST node.");
            }
        }
    }

    private static bool IsCommandAllowed(
        string commandName,
        IReadOnlyList<string>? commandArgs)
    {
        var normalized = commandName.ToLowerInvariant();
        if (normalized == HaltKeyword)
            return commandArgs == null || commandArgs.Count == 0;

        if (CommandNameSet.Contains(normalized))
            return true;

        return (commandArgs == null || commandArgs.Count == 0) &&
               TrySplitCollapsedCommand(commandName, out var splitName, out _) &&
               CommandNameSet.Contains(splitName);
    }

    private static void ValidateCommandArgs(
        string commandName,
        IReadOnlyList<string> args,
        DslCommandSyntax syntax)
    {
        var specs = ResolveArgSpecs(syntax);
        if (specs.Count == 0)
        {
            if (args.Count > 0)
                throw new FormatException($"Command '{commandName}' does not take arguments.");
            return;
        }

        if (args.Count > specs.Count)
            throw new FormatException($"Command '{commandName}' has too many arguments.");

        var requiredCount = specs.Count(s => s.Required && string.IsNullOrWhiteSpace(s.DefaultValue));
        if (args.Count < requiredCount)
            throw new FormatException($"Command '{commandName}' is missing required arguments.");

        for (int i = 0; i < args.Count; i++)
        {
            if (!IsArgValueValid(args[i], specs[i]))
            {
                throw new FormatException(
                    $"Command '{commandName}' argument {i + 1} must be {specs[i].Kind.ToString().ToLowerInvariant()}.");
            }
        }
    }

    private static bool IsArgValueValid(string value, DslArgumentSpec spec)
    {
        var kind = spec.Kind;
        if (kind == DslArgKind.None)
            return false;

        if (kind.HasFlag(DslArgKind.Integer) && int.TryParse(value, out _))
            return true;

        if (kind.HasFlag(DslArgKind.Enum) && IsValidIdentifier(value))
            return true;

        if ((kind.HasFlag(DslArgKind.Any) ||
             kind.HasFlag(DslArgKind.Item) ||
             kind.HasFlag(DslArgKind.System)) &&
            IsValidIdentifier(value))
        {
            return true;
        }

        return false;
    }

    private static string ExpandMacroArg(string? arg, GameState? state)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return arg ?? string.Empty;

        var trimmed = arg.Trim();
        if (!trimmed.StartsWith("$", StringComparison.Ordinal))
            return trimmed;

        var macroName = trimmed[1..];
        if (!Macros.TryGetValue(macroName, out var macro))
            throw new FormatException($"Unknown macro '{trimmed}'.");

        // Parse-only contexts can keep unresolved macros verbatim.
        if (state == null)
            return trimmed;

        var resolved = macro.Resolver(state)?.Trim();
        if (string.IsNullOrWhiteSpace(resolved))
            throw new FormatException($"Macro '{trimmed}' is not available in current state.");

        return resolved;
    }

}
