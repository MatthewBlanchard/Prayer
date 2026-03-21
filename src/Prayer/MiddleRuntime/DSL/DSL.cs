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
            ["nearest_station"] = new(
                "the id of the nearest station by jump count",
                ResolveNearestStation),
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

    private static readonly TextParser<string> ParamRef =
        Span.Regex("\\$[A-Za-z_][A-Za-z0-9_-]*").Select(x => x.ToStringValue());

    private static readonly TextParser<DslNumericOperandAstNode> NumericOperand =
        MetricCall.Try()
            .Select(c => (DslNumericOperandAstNode)new DslMetricCallOperandAstNode(c.Name, c.Args))
        .Or(ParamRef.Try().Select(r => (DslNumericOperandAstNode)new DslArgumentRefOperandAstNode(r)))
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
        => ParseTree(text, extraSkills: null);

    /// <summary>
    /// Parses a DSL script, validating skill calls against their declared parameter types.
    /// </summary>
    public static DslAstProgram ParseTree(
        string text,
        IReadOnlyDictionary<string, IReadOnlyList<DslSkillParamDef>>? extraSkills)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new DslAstProgram(Array.Empty<DslAstNode>());

        RejectRemovedConstructs(text);

        try
        {
            var tree = ProgramAstParser.Parse(text);
            tree = AnnotateSourceLines(text, tree);
            ValidateTree(tree, extraSkills);
            return tree;
        }
        catch (ParseException ex)
        {
            throw new FormatException($"Invalid DSL script: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Parses a DSL body (statements only, no skill/override declarations) as used inside
    /// skill and override blocks. Skill param tokens like $station pass validation because
    /// they match the identifier grammar; global macro resolution happens at runtime.
    /// </summary>
    internal static DslAstProgram ParseBodyForSkillLibrary(
        string bodyText,
        IReadOnlySet<string>? allSkillNames)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            return new DslAstProgram(Array.Empty<DslAstNode>());

        RejectRemovedConstructs(bodyText);

        try
        {
            var tree = ProgramAstParser.Parse(bodyText);
            // Note: no source-line annotation needed for library bodies
            ValidateTree(tree, allSkillNames, allowDollarArgs: true);
            return tree;
        }
        catch (ParseException ex)
        {
            throw new FormatException($"Invalid skill/override body: {ex.Message}", ex);
        }
    }

    private static void RejectRemovedConstructs(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var repeatMatch = System.Text.RegularExpressions.Regex.Match(
            text,
            @"\brepeat\b\s*\{",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!repeatMatch.Success)
            return;

        int line = 1;
        for (int i = 0; i < repeatMatch.Index; i++)
        {
            if (text[i] == '\n')
                line++;
        }

        throw new FormatException(
            $"Line {line}: 'repeat {{ ... }}' is no longer supported. Use an 'until <CONDITION> {{ ... }}' block instead.");
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
            DslIfAstNode ifNode => AnnotateIfNode(ifNode, entries, ref entryIndex),
            DslUntilAstNode untilNode => AnnotateUntilNode(untilNode, entries, ref entryIndex),
            _ => node
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
            if (c.Args.Count > 0)
                parts.AddRange(c.Args.Where(a => !string.IsNullOrWhiteSpace(a)));
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
        IReadOnlyList<string>? preferredCommandNames,
        IReadOnlyList<DslSkillAstNode>? skills = null)
    {
        var commands = SelectPromptCommands(
            userInput,
            exampleScripts,
            preferredCommandNames);
        return BuildPromptDslReferenceBlockCore(commands, skills);
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

    private static string BuildPromptDslReferenceBlockCore(
        IReadOnlyList<ICommand> commands,
        IReadOnlyList<DslSkillAstNode>? skills = null)
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

        if (skills != null && skills.Count > 0)
        {
            sb.AppendLine("Skills (reusable subscripts, call like commands):");
            foreach (var skill in skills.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                var paramList = skill.Params.Count == 0
                    ? "none"
                    : string.Join(", ", skill.Params.Select(p => $"{p.Name}: {DescribePromptArgType(new DslArgumentSpec(p.Type))}"));
                sb.AppendLine($"- {skill.Name}; (args: {paramList})");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Keywords:");
        sb.AppendLine("- until: runtime loop block that exits when a condition is true");
        sb.AppendLine("- if: conditional block executed only when a condition is true");
        sb.AppendLine("- halt: stop script execution");
        sb.AppendLine();
        sb.AppendLine("Rules:");
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
        sb.AppendLine("- MINED(item_id) and STASHED(item_id) are script-scoped counters; both reset to 0 when a new script is loaded.");
        sb.AppendLine("- All commands still end with ';' inside blocks.");
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
        sb.AppendLine("if_stmt ::= \"if\" ws condition_expr ws \"{\" ws statement* ws \"}\"");
        sb.AppendLine("until_stmt ::= \"until\" ws condition_expr ws \"{\" ws statement* ws \"}\"");
        sb.AppendLine("halt_stmt ::= \"halt\" ws \";\"");
        sb.AppendLine($"statement ::= {string.Join(" | ", statementRules)} | if_stmt | until_stmt | halt_stmt");
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
                parts.Add($"ws {ArgKindPattern(specs[i].Type)}");
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

        if (syntax.ArgType == DslArgType.None)
            return Array.Empty<DslArgumentSpec>();

        return new[] { new DslArgumentSpec(syntax.ArgType, syntax.ArgRequired, syntax.DefaultArg) };
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
        return spec.Type switch
        {
            DslArgType.Integer => "int",
            DslArgType.ItemId => "item_id",
            DslArgType.SystemId => "system_id",
            DslArgType.PoiId => "poi_id",
            DslArgType.GoTarget => "go_target",
            DslArgType.ShipId => "ship_id",
            DslArgType.ListingId => "listing_id",
            DslArgType.MissionId => "mission_id",
            DslArgType.ModuleId => "module_id",
            DslArgType.RecipeId => "recipe_id",
            DslArgType.Enum => !string.IsNullOrWhiteSpace(spec.EnumType)
                ? $"enum:{spec.EnumType}"
                : (spec.EnumValues != null && spec.EnumValues.Count > 0
                    ? $"enum[{string.Join("|", spec.EnumValues)}]"
                    : "enum"),
            DslArgType.Any => "identifier",
            _ => "value"
        };
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
                    : InferPromptArgBaseName(spec.Type);

            used.TryGetValue(baseName, out var seenCount);
            var tokenName = seenCount == 0 ? baseName : $"{baseName}{seenCount + 1}";
            used[baseName] = seenCount + 1;

            tokens.Add(optional ? $"<{tokenName}?>" : $"<{tokenName}>");
        }

        return tokens;
    }

    private static string InferPromptArgBaseName(DslArgType kind)
        => kind switch
        {
            DslArgType.Integer => "count",
            DslArgType.ItemId => "item",
            DslArgType.SystemId => "system",
            DslArgType.PoiId => "poi",
            DslArgType.GoTarget => "target",
            DslArgType.ShipId => "ship",
            DslArgType.ListingId => "listing",
            DslArgType.MissionId => "mission",
            DslArgType.ModuleId => "mod",
            DslArgType.RecipeId => "recipe",
            DslArgType.Enum => "option",
            _ => "value"
        };

    private static string FormatPredicateSig(string name, string[] paramNames)
        => paramNames.Length == 0
            ? $"{name}()"
            : $"{name}({string.Join(", ", paramNames.Select(p => $"<{p}>"))})";

    private static string ArgKindPattern(DslArgType kind)
    {
        return kind == DslArgType.Integer ? "integer" : "identifier";
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
            var firstKind = specs[0].Type;
            bool allowsIdentifier = firstKind != DslArgType.Integer && firstKind != DslArgType.None;
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

    private static void ValidateTree(
        DslAstProgram tree,
        IReadOnlyDictionary<string, IReadOnlyList<DslSkillParamDef>>? extraSkills = null,
        bool allowDollarArgs = false)
    {
        ValidateNodes(tree.Statements, extraSkills, allowDollarArgs);
    }

    // Overload for ParseBodyForSkillLibrary, which only has names available during parse.
    private static void ValidateTree(
        DslAstProgram tree,
        IReadOnlySet<string>? extraCommandNames,
        bool allowDollarArgs)
    {
        ValidateNodes(tree.Statements, extraCommandNames, allowDollarArgs);
    }

    private static void ValidateNodes(
        IReadOnlyList<DslAstNode> nodes,
        IReadOnlyDictionary<string, IReadOnlyList<DslSkillParamDef>>? extraSkills = null,
        bool allowDollarArgs = false)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case DslCommandAstNode commandNode:
                {
                    var normalizedName = (commandNode.Name ?? "").Trim().ToLowerInvariant();
                    var args = commandNode.Args ?? Array.Empty<string>();

                    if (!IsCommandAllowed(normalizedName, args, extraSkills))
                    {
                        throw new FormatException(
                            $"Command '{commandNode.Name}' is not recognized.");
                    }

                    if (extraSkills != null &&
                        extraSkills.TryGetValue(commandNode.Name ?? string.Empty, out var skillParams))
                    {
                        var skillSyntax = new DslCommandSyntax(ArgSpecs: skillParams
                            .Select(p => new DslArgumentSpec(p.Type, Required: true))
                            .ToList());
                        ValidateCommandArgs(commandNode.Name!, args, skillSyntax, allowDollarArgs);
                    }
                    else if (CommandSyntaxByName.TryGetValue(normalizedName, out var commandSyntax))
                    {
                        ValidateCommandArgs(normalizedName, args, commandSyntax, allowDollarArgs);
                    }

                    break;
                }
                case DslIfAstNode ifNode:
                {
                    if (!DslBooleanEvaluator.TryValidateCondition(ifNode.Condition, out var error))
                    {
                        throw new FormatException(
                            $"Invalid condition '{DslBooleanEvaluator.RenderCondition(ifNode.Condition)}': {error}.");
                    }

                    ValidateNodes(ifNode.Body ?? Array.Empty<DslAstNode>(), extraSkills, allowDollarArgs);
                    break;
                }
                case DslUntilAstNode untilNode:
                {
                    if (!DslBooleanEvaluator.TryValidateCondition(untilNode.Condition, out var error))
                    {
                        throw new FormatException(
                            $"Invalid condition '{DslBooleanEvaluator.RenderCondition(untilNode.Condition)}': {error}.");
                    }

                    ValidateNodes(untilNode.Body ?? Array.Empty<DslAstNode>(), extraSkills, allowDollarArgs);
                    break;
                }
                default:
                    throw new FormatException("Unknown DSL AST node.");
            }
        }
    }

    private static void ValidateNodes(
        IReadOnlyList<DslAstNode> nodes,
        IReadOnlySet<string>? extraCommandNames,
        bool allowDollarArgs)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case DslCommandAstNode commandNode:
                {
                    var normalizedName = (commandNode.Name ?? "").Trim().ToLowerInvariant();
                    var args = commandNode.Args ?? Array.Empty<string>();

                    if (!IsCommandAllowed(normalizedName, args, extraCommandNames))
                    {
                        throw new FormatException(
                            $"Command '{commandNode.Name}' is not recognized.");
                    }

                    if (CommandSyntaxByName.TryGetValue(normalizedName, out var commandSyntax))
                        ValidateCommandArgs(normalizedName, args, commandSyntax, allowDollarArgs);

                    break;
                }
                case DslIfAstNode ifNode:
                {
                    if (!DslBooleanEvaluator.TryValidateCondition(ifNode.Condition, out var error))
                    {
                        throw new FormatException(
                            $"Invalid condition '{DslBooleanEvaluator.RenderCondition(ifNode.Condition)}': {error}.");
                    }

                    ValidateNodes(ifNode.Body ?? Array.Empty<DslAstNode>(), extraCommandNames, allowDollarArgs);
                    break;
                }
                case DslUntilAstNode untilNode:
                {
                    if (!DslBooleanEvaluator.TryValidateCondition(untilNode.Condition, out var error))
                    {
                        throw new FormatException(
                            $"Invalid condition '{DslBooleanEvaluator.RenderCondition(untilNode.Condition)}': {error}.");
                    }

                    ValidateNodes(untilNode.Body ?? Array.Empty<DslAstNode>(), extraCommandNames, allowDollarArgs);
                    break;
                }
                default:
                    throw new FormatException("Unknown DSL AST node.");
            }
        }
    }

    private static bool IsCommandAllowed(
        string commandName,
        IReadOnlyList<string>? commandArgs,
        IReadOnlyDictionary<string, IReadOnlyList<DslSkillParamDef>>? extraSkills = null)
    {
        var normalized = commandName.ToLowerInvariant();
        if (normalized == HaltKeyword)
            return commandArgs == null || commandArgs.Count == 0;

        if (CommandNameSet.Contains(normalized))
            return true;

        if (extraSkills != null && extraSkills.ContainsKey(commandName))
            return true;

        return (commandArgs == null || commandArgs.Count == 0) &&
               TrySplitCollapsedCommand(commandName, out var splitName, out _) &&
               CommandNameSet.Contains(splitName);
    }

    private static bool IsCommandAllowed(
        string commandName,
        IReadOnlyList<string>? commandArgs,
        IReadOnlySet<string>? extraCommandNames)
    {
        var normalized = commandName.ToLowerInvariant();
        if (normalized == HaltKeyword)
            return commandArgs == null || commandArgs.Count == 0;

        if (CommandNameSet.Contains(normalized))
            return true;

        if (extraCommandNames != null && extraCommandNames.Contains(commandName))
            return true;

        return (commandArgs == null || commandArgs.Count == 0) &&
               TrySplitCollapsedCommand(commandName, out var splitName, out _) &&
               CommandNameSet.Contains(splitName);
    }

    private static void ValidateCommandArgs(
        string commandName,
        IReadOnlyList<string> args,
        DslCommandSyntax syntax,
        bool allowDollarArgs = false)
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
            // Inside skill/override bodies, $param tokens are allowed for any typed arg.
            if (allowDollarArgs &&
                args[i].StartsWith("$", StringComparison.Ordinal) &&
                args[i].Length > 1)
                continue;

            if (!IsArgValueValid(args[i], specs[i]))
            {
                throw new FormatException(
                    $"Command '{commandName}' argument {i + 1} must be {specs[i].Type.ToString().ToLowerInvariant()}.");
            }
        }
    }

    private static bool IsArgValueValid(string value, DslArgumentSpec spec)
    {
        var type = spec.Type;
        if (type == DslArgType.None)
            return false;

        if (type == DslArgType.Integer && int.TryParse(value, out _))
            return true;

        return IsValidIdentifier(value);
    }

    private static string? ResolveNearestStation(GameState state)
        => GalaxyStateHub.GetNearestStationId(state.System);

    /// <summary>
    /// Expands a single argument token, resolving $macro references.
    /// Returns the token unchanged (including the leading $) if state is null.
    /// Throws FormatException for unknown macros.
    /// </summary>
    internal static string ExpandMacroArg(string? arg, GameState? state)
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
