using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Parses .prayer library files containing skill and override definitions.
/// </summary>
internal static class SkillLibraryParser
{
    private static readonly IReadOnlyDictionary<string, DslArgType> TypeNameMap =
        new Dictionary<string, DslArgType>(StringComparer.OrdinalIgnoreCase)
        {
            ["poi_id"]     = DslArgType.PoiId,
            ["system_id"]  = DslArgType.SystemId,
            ["item_id"]    = DslArgType.ItemId,
            ["ship_id"]    = DslArgType.ShipId,
            ["mission_id"] = DslArgType.MissionId,
            ["module_id"]  = DslArgType.ModuleId,
            ["recipe_id"]  = DslArgType.RecipeId,
            ["integer"]    = DslArgType.Integer,
            ["any"]        = DslArgType.Any,
        };

    public static SkillLibrary Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return SkillLibrary.Empty;

        // First pass: collect all declared skill/override names so bodies can reference them.
        var allDeclaredNames = CollectDeclaredNames(text);

        var skills = new List<DslSkillAstNode>();
        var overrides = new List<DslOverrideAstNode>();

        int pos = 0;
        while (pos < text.Length)
        {
            SkipWhitespaceAndComments(text, ref pos);
            if (pos >= text.Length) break;

            if (StartsWithKeyword(text, pos, "skill", requireWhitespaceAfter: true))
            {
                skills.Add(ParseSkillBlock(text, ref pos, allDeclaredNames));
            }
            else if (StartsWithKeyword(text, pos, "override", requireWhitespaceAfter: true))
            {
                overrides.Add(ParseOverrideBlock(text, ref pos, allDeclaredNames));
            }
            else
            {
                throw new FormatException(
                    $"Unexpected content in skill library at offset {pos}: '{PeekSnippet(text, pos)}'.");
            }
        }

        ValidateNoRecursion(skills);
        return new SkillLibrary(skills, overrides);
    }

    // ─── First pass ──────────────────────────────────────────────────────────

    private static HashSet<string> CollectDeclaredNames(string text)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int pos = 0;

        while (pos < text.Length)
        {
            SkipWhitespaceAndComments(text, ref pos);
            if (pos >= text.Length) break;

            string keyword;
            if (StartsWithKeyword(text, pos, "skill", requireWhitespaceAfter: true))
                keyword = "skill";
            else if (StartsWithKeyword(text, pos, "override", requireWhitespaceAfter: true))
                keyword = "override";
            else
            {
                // Skip to next newline and try again (best-effort)
                while (pos < text.Length && text[pos] != '\n') pos++;
                continue;
            }

            pos += keyword.Length;
            SkipWhitespaceAndComments(text, ref pos);
            var name = ReadIdentifier(text, ref pos);
            if (!string.IsNullOrEmpty(name))
                names.Add(name);

            // Skip the rest of the declaration (find and skip the braced block)
            SkipThroughBraceBlock(text, ref pos);
        }

        return names;
    }

    // ─── Skill block ─────────────────────────────────────────────────────────

    private static DslSkillAstNode ParseSkillBlock(
        string text, ref int pos, HashSet<string> allDeclaredNames)
    {
        pos += 5; // skip "skill"
        SkipWhitespaceAndComments(text, ref pos);

        var name = ReadIdentifier(text, ref pos);
        if (string.IsNullOrEmpty(name))
            throw new FormatException("Expected skill name after 'skill'.");

        SkipWhitespaceAndComments(text, ref pos);

        ExpectChar(text, ref pos, '(');
        var @params = ParseParamList(text, ref pos);
        ExpectChar(text, ref pos, ')');

        SkipWhitespaceAndComments(text, ref pos);

        // Extract the body text and parse it as DSL statements.
        // Param names become allowed "$name" tokens inside the body (they are valid identifiers).
        var bodyText = ExtractBraceBlock(text, ref pos);
        var body = DslParser.ParseBodyForSkillLibrary(bodyText, allDeclaredNames);

        return new DslSkillAstNode(name, @params, body.Statements);
    }

    private static IReadOnlyList<DslSkillParamDef> ParseParamList(string text, ref int pos)
    {
        var @params = new List<DslSkillParamDef>();

        SkipWhitespaceAndComments(text, ref pos);
        if (pos >= text.Length || text[pos] == ')')
            return @params; // empty param list

        while (pos < text.Length)
        {
            SkipWhitespaceAndComments(text, ref pos);

            var paramName = ReadIdentifier(text, ref pos);
            if (string.IsNullOrEmpty(paramName))
                throw new FormatException($"Expected parameter name in skill parameter list.");

            SkipWhitespaceAndComments(text, ref pos);
            ExpectChar(text, ref pos, ':');
            SkipWhitespaceAndComments(text, ref pos);

            var typeName = ReadIdentifier(text, ref pos);
            if (string.IsNullOrEmpty(typeName))
                throw new FormatException($"Expected type after ':' for parameter '{paramName}'.");

            if (!TypeNameMap.TryGetValue(typeName, out var argType))
                throw new FormatException(
                    $"Unknown parameter type '{typeName}' for parameter '{paramName}'. " +
                    $"Valid types: {string.Join(", ", TypeNameMap.Keys.OrderBy(k => k))}.");

            @params.Add(new DslSkillParamDef(paramName, argType));

            SkipWhitespaceAndComments(text, ref pos);
            if (pos >= text.Length || text[pos] == ')')
                break;

            ExpectChar(text, ref pos, ',');
        }

        return @params;
    }

    // ─── Override block ───────────────────────────────────────────────────────

    private static DslOverrideAstNode ParseOverrideBlock(
        string text, ref int pos, HashSet<string> allDeclaredNames)
    {
        pos += 8; // skip "override"
        SkipWhitespaceAndComments(text, ref pos);

        var name = ReadIdentifier(text, ref pos);
        if (string.IsNullOrEmpty(name))
            throw new FormatException("Expected override name after 'override'.");

        SkipWhitespaceAndComments(text, ref pos);

        if (!StartsWithKeyword(text, pos, "when", requireWhitespaceAfter: true))
            throw new FormatException($"Expected 'when' after override name '{name}'.");
        pos += 4;
        SkipWhitespaceAndComments(text, ref pos);

        // Read condition text: everything up to the opening '{'.
        var conditionText = ReadUntilBrace(text, pos).Trim();
        pos += conditionText.Length;
        // pos now points at (or past) whitespace before '{'
        SkipWhitespaceAndComments(text, ref pos);

        if (string.IsNullOrWhiteSpace(conditionText))
            throw new FormatException($"Override '{name}' has no condition.");

        if (!DslParser.TryParseCondition(conditionText, out var condition, out var error))
            throw new FormatException($"Override '{name}' has invalid condition '{conditionText}': {error}.");

        var bodyText = ExtractBraceBlock(text, ref pos);
        var body = DslParser.ParseBodyForSkillLibrary(bodyText, allDeclaredNames);

        return new DslOverrideAstNode(name, condition!, body.Statements);
    }

    // ─── Recursion detection ─────────────────────────────────────────────────

    private static void ValidateNoRecursion(IReadOnlyList<DslSkillAstNode> skills)
    {
        var callGraph = BuildCallGraph(skills);

        foreach (var skill in skills)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (HasCycle(skill.Name, callGraph, visited, skill.Name))
                throw new FormatException(
                    $"Skill '{skill.Name}' is recursive (calls itself directly or indirectly).");
        }
    }

    private static Dictionary<string, HashSet<string>> BuildCallGraph(IReadOnlyList<DslSkillAstNode> skills)
    {
        var skillNames = new HashSet<string>(
            skills.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);

        var graph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in skills)
        {
            var calls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectSkillCalls(skill.Body, skillNames, calls);
            graph[skill.Name] = calls;
        }

        return graph;
    }

    private static void CollectSkillCalls(
        IReadOnlyList<DslAstNode> nodes,
        HashSet<string> skillNames,
        HashSet<string> calls)
    {
        foreach (var node in nodes ?? Array.Empty<DslAstNode>())
        {
            switch (node)
            {
                case DslCommandAstNode cmd when skillNames.Contains(cmd.Name):
                    calls.Add(cmd.Name);
                    break;
                case DslIfAstNode ifNode:
                    CollectSkillCalls(ifNode.Body, skillNames, calls);
                    break;
                case DslUntilAstNode untilNode:
                    CollectSkillCalls(untilNode.Body, skillNames, calls);
                    break;
            }
        }
    }

    private static bool HasCycle(
        string current,
        Dictionary<string, HashSet<string>> graph,
        HashSet<string> visited,
        string origin)
    {
        if (!graph.TryGetValue(current, out var calls))
            return false;

        foreach (var callee in calls)
        {
            if (string.Equals(callee, origin, StringComparison.OrdinalIgnoreCase))
                return true;

            if (visited.Add(callee) && HasCycle(callee, graph, visited, origin))
                return true;
        }

        return false;
    }

    // ─── Text helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the content inside the next balanced { ... } block,
    /// advancing pos past the closing brace.
    /// </summary>
    private static string ExtractBraceBlock(string text, ref int pos)
    {
        if (pos >= text.Length || text[pos] != '{')
            throw new FormatException(
                $"Expected '{{' at offset {pos}, got '{PeekSnippet(text, pos)}'.");

        pos++; // skip '{'
        int start = pos;
        int depth = 1;

        while (pos < text.Length && depth > 0)
        {
            char c = text[pos++];
            if (c == '{') depth++;
            else if (c == '}') depth--;
        }

        if (depth != 0)
            throw new FormatException("Unterminated brace block in skill library.");

        // pos is now one past the closing '}'; the body is [start, pos-1)
        return text.Substring(start, pos - start - 1);
    }

    /// <summary>Skip forward through a brace block without extracting, for first-pass scanning.</summary>
    private static void SkipThroughBraceBlock(string text, ref int pos)
    {
        // Find the opening '{'
        while (pos < text.Length && text[pos] != '{')
            pos++;

        if (pos >= text.Length) return;

        pos++; // skip '{'
        int depth = 1;

        while (pos < text.Length && depth > 0)
        {
            char c = text[pos++];
            if (c == '{') depth++;
            else if (c == '}') depth--;
        }
    }

    /// <summary>Reads text up to (but not including) the next '{' on the same line.</summary>
    private static string ReadUntilBrace(string text, int pos)
    {
        int start = pos;
        while (pos < text.Length && text[pos] != '{')
            pos++;

        return text.Substring(start, pos - start);
    }

    private static string ReadIdentifier(string text, ref int pos)
    {
        if (pos >= text.Length || !IsIdentifierStart(text[pos]))
            return string.Empty;

        int start = pos++;
        while (pos < text.Length && IsIdentifierPart(text[pos]))
            pos++;

        return text.Substring(start, pos - start);
    }

    private static void ExpectChar(string text, ref int pos, char expected)
    {
        if (pos >= text.Length || text[pos] != expected)
            throw new FormatException(
                $"Expected '{expected}' at offset {pos}, got '{PeekSnippet(text, pos)}'.");
        pos++;
    }

    private static void SkipWhitespaceAndComments(string text, ref int pos)
    {
        while (pos < text.Length)
        {
            if (char.IsWhiteSpace(text[pos]))
            {
                pos++;
                continue;
            }

            // Single-line comments: // ... \n
            if (pos + 1 < text.Length && text[pos] == '/' && text[pos + 1] == '/')
            {
                while (pos < text.Length && text[pos] != '\n')
                    pos++;
                continue;
            }

            break;
        }
    }

    private static bool StartsWithKeyword(
        string text, int pos, string keyword, bool requireWhitespaceAfter)
    {
        if (pos + keyword.Length > text.Length) return false;

        for (int i = 0; i < keyword.Length; i++)
        {
            if (char.ToUpperInvariant(text[pos + i]) != char.ToUpperInvariant(keyword[i]))
                return false;
        }

        if (!requireWhitespaceAfter) return true;

        int afterPos = pos + keyword.Length;
        return afterPos >= text.Length || char.IsWhiteSpace(text[afterPos]);
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentifierPart(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '-';

    private static string PeekSnippet(string text, int pos, int length = 20)
    {
        if (pos >= text.Length) return "<end of input>";
        var snippet = text.Substring(pos, Math.Min(length, text.Length - pos));
        return snippet.Replace("\n", "\\n").Replace("\r", "\\r");
    }
}
