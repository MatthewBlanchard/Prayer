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
public sealed record DslIfAstNode(string Condition, IReadOnlyList<DslAstNode> Body, int SourceLine = 0) : DslAstNode;
public sealed record DslUntilAstNode(string Condition, IReadOnlyList<DslAstNode> Body, int SourceLine = 0) : DslAstNode;

public static class DslParser
{
    private const string HaltKeyword = "halt";
    private static readonly string[] BooleanTokens =
    {
        "MISSION_COMPLETE"
    };
    private static readonly HashSet<string> BooleanTokenSet =
        BooleanTokens.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string[]> PromptArgNameOverrides =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["go"] = new[] { "destination" },
            ["mine"] = new[] { "target_or_resource" },
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
            ["commission_quote"] = new[] { "ship_class" },
            ["commission_ship"] = new[] { "ship_class" },
            ["commission_status"] = new[] { "commission" },
            ["accept_mission"] = new[] { "mission_id" },
            ["abandon_mission"] = new[] { "mission_id" },
            ["sell_ship"] = new[] { "ship" },
            ["list_ship_for_sale"] = new[] { "price" },
        };

    private static readonly IReadOnlyList<ICommand> Commands =
        BuildCommands();
    private static readonly HashSet<string> CommandNameSet =
        BuildCommandNameSet(Commands);
    private static readonly IReadOnlyDictionary<string, DslCommandSyntax> CommandSyntaxByName =
        BuildCommandSyntaxByName(Commands);
    private static readonly TextParser<Unit> Ws =
        Character.WhiteSpace.Many().Value(Unit.Value);

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

    private static readonly TextParser<string> ArgumentToken =
        Integer.Try().Or(Identifier);

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
        from condition in Identifier
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
        from condition in Identifier
        from __ in Ws
        from _open in Character.EqualTo('{')
        from ___ in Ws
        from body in Superpower.Parse.Ref(() => StatementAst!).Many()
        from ____ in Ws
        from _close in Character.EqualTo('}')
        select (DslAstNode)new DslUntilAstNode(condition, body);

    private static readonly TextParser<DslAstNode> StatementAst =
        from statement in UntilAst.Try().Or(IfAst.Try()).Or(RepeatAst.Try()).Or(CommandAst)
        from _ in Ws
        select statement;

    private static readonly TextParser<DslAstProgram> ProgramAstParser =
        from _ in Ws
        from statements in (
            from statement in StatementAst!
            select statement).Many()
        select new DslAstProgram(statements);

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
        if (i >= text.Length || !IsIdentifierStart(text[i]))
            return false;

        _ = ReadIdentifier(text, ref i);
        SkipWhitespace(text, ref i);
        return i < text.Length && text[i] == '{';
    }

    private static bool IsUntilToken(string token, string text, int indexAfterToken)
    {
        if (!token.Equals("until", StringComparison.OrdinalIgnoreCase))
            return false;

        int i = indexAfterToken;
        SkipWhitespace(text, ref i);
        if (i >= text.Length || !IsIdentifierStart(text[i]))
            return false;

        _ = ReadIdentifier(text, ref i);
        SkipWhitespace(text, ref i);
        return i < text.Length && text[i] == '{';
    }

    private static void SkipRepeatHeader(string text, ref int index, ref int line)
    {
        SkipWhitespaceAndCountLines(text, ref index, ref line);
        if (index < text.Length && text[index] == '{')
            index++;
    }

    private static void SkipIfHeader(string text, ref int index, ref int line)
    {
        SkipWhitespaceAndCountLines(text, ref index, ref line);
        if (index < text.Length && IsIdentifierStart(text[index]))
            _ = ReadIdentifier(text, ref index);
        SkipWhitespaceAndCountLines(text, ref index, ref line);
        if (index < text.Length && text[index] == '{')
            index++;
    }

    private static void SkipUntilHeader(string text, ref int index, ref int line)
    {
        SkipWhitespaceAndCountLines(text, ref index, ref line);
        if (index < text.Length && IsIdentifierStart(text[index]))
            _ = ReadIdentifier(text, ref index);
        SkipWhitespaceAndCountLines(text, ref index, ref line);
        if (index < text.Length && text[index] == '{')
            index++;
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
        var commands = DslInterpreter.Translate(tree);
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
        sb.AppendLine("identifier ::= [A-Za-z_][A-Za-z0-9_-]*");
        sb.AppendLine("integer ::= [0-9]+");
        sb.AppendLine();
        BuildGrammar(sb);

        return sb.ToString().TrimEnd();
    }

    public static string BuildPromptDslReferenceBlock()
    {
        var commands = Commands
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var entries = commands
            .Select(c => (Command: c, Syntax: CommandSyntaxByName[c.Name]))
            .ToList();

        var commandSignatures = entries
            .Select(e => BuildPromptCommandSignature(e.Command.Name, e.Syntax))
            .ToList();
        commandSignatures.Add("halt;");

        var sb = new StringBuilder();
        sb.AppendLine("DSL command reference (terminate commands with ;):");

        if (commandSignatures.Count > 0)
        {
            sb.AppendLine("Commands:");
            foreach (var signature in commandSignatures)
                sb.AppendLine($"- {signature}");
        }

        sb.AppendLine();
        sb.AppendLine("Keywords:");
        sb.AppendLine("- repeat: infinite runtime loop block");
        sb.AppendLine("- until: runtime loop block that exits when a boolean flag is true");
        sb.AppendLine("- if: conditional block executed only when a boolean flag is true");
        sb.AppendLine("- halt: stop script execution");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Blocks are supported via: repeat { ... }");
        sb.AppendLine("- Conditional blocks are supported via: if <BOOLEAN_FLAG> { ... }");
        sb.AppendLine("- Until blocks are supported via: until <BOOLEAN_FLAG> { ... }");
        sb.AppendLine($"- Boolean flags: {string.Join(", ", BooleanTokens)}");
        sb.AppendLine("- All commands still end with ';' inside repeat blocks.");
        sb.AppendLine();
        sb.AppendLine("Examples:");
        sb.AppendLine("- if MISSION_COMPLETE {");
        sb.AppendLine("    halt;");
        sb.AppendLine("  }");
        sb.AppendLine("- until MISSION_COMPLETE {");
        sb.AppendLine("    mine carbon_ore;");
        sb.AppendLine("    go sol;");
        sb.AppendLine("    sell cargo;");
        sb.AppendLine("  }");
        sb.AppendLine();

        return sb.ToString();
    }

    internal static string NormalizeCommandStep(
        string commandName,
        IReadOnlyList<string>? commandArgs)
    {
        var token = commandName.ToLowerInvariant();
        var args = (commandArgs ?? Array.Empty<string>()).ToList();
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

            var syntax = command is IDslCommandGrammar provider
                ? provider.GetDslSyntax()
                : new DslCommandSyntax();
            map[command.Name] = syntax;
        }

        return map;
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

        sb.AppendLine("repeat_stmt ::= \"repeat\" ws \"{\" ws statement* ws \"}\"");
        sb.AppendLine("if_stmt ::= \"if\" ws identifier ws \"{\" ws statement* ws \"}\"");
        sb.AppendLine("until_stmt ::= \"until\" ws identifier ws \"{\" ws statement* ws \"}\"");
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

        if (!(char.IsLetter(value[0]) || value[0] == '_'))
            return false;

        for (int i = 1; i < value.Length; i++)
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
                    if (string.IsNullOrWhiteSpace(ifNode.Condition) ||
                        !BooleanTokenSet.Contains(ifNode.Condition.Trim()))
                    {
                        throw new FormatException(
                            $"Unknown boolean flag '{ifNode.Condition}'. " +
                            $"Allowed: {string.Join(", ", BooleanTokens)}.");
                    }

                    ValidateNodes(ifNode.Body ?? Array.Empty<DslAstNode>());
                    break;
                }
                case DslUntilAstNode untilNode:
                {
                    if (string.IsNullOrWhiteSpace(untilNode.Condition) ||
                        !BooleanTokenSet.Contains(untilNode.Condition.Trim()))
                    {
                        throw new FormatException(
                            $"Unknown boolean flag '{untilNode.Condition}'. " +
                            $"Allowed: {string.Join(", ", BooleanTokens)}.");
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

}
