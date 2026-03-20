using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>A parameter definition for a skill — name and expected type.</summary>
public sealed record DslSkillParamDef(string Name, DslArgType Type);

/// <summary>AST node for a named, parameterized subscript defined in the skill library.</summary>
public sealed class DslSkillAstNode
{
    public DslSkillAstNode(
        string name,
        IReadOnlyList<DslSkillParamDef> @params,
        IReadOnlyList<DslAstNode> body)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Params = @params ?? Array.Empty<DslSkillParamDef>();
        Body = body ?? Array.Empty<DslAstNode>();
    }

    public string Name { get; }
    public IReadOnlyList<DslSkillParamDef> Params { get; }
    public IReadOnlyList<DslAstNode> Body { get; }
}

/// <summary>AST node for a named, auto-triggered safety behavior defined in the skill library.</summary>
public sealed class DslOverrideAstNode
{
    public DslOverrideAstNode(
        string name,
        DslConditionAstNode condition,
        IReadOnlyList<DslAstNode> body,
        bool enabled = true)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        Body = body ?? Array.Empty<DslAstNode>();
        Enabled = enabled;
    }

    public string Name { get; }
    public DslConditionAstNode Condition { get; }
    public IReadOnlyList<DslAstNode> Body { get; }
    public bool Enabled { get; }

    public DslOverrideAstNode WithEnabled(bool enabled) =>
        new(Name, Condition, Body, enabled);
}

/// <summary>
/// A parsed skill/override library loaded from a .prayer library file.
/// Skills are reusable named subscripts; overrides are always-on safety behaviors.
/// </summary>
public sealed class SkillLibrary
{
    private readonly Dictionary<string, DslSkillAstNode> _skillsByName;

    public SkillLibrary(
        IReadOnlyList<DslSkillAstNode> skills,
        IReadOnlyList<DslOverrideAstNode> overrides)
    {
        Skills = skills ?? Array.Empty<DslSkillAstNode>();
        Overrides = overrides ?? Array.Empty<DslOverrideAstNode>();
        _skillsByName = Skills.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
    }

    public static SkillLibrary Empty { get; } = new(
        Array.Empty<DslSkillAstNode>(),
        Array.Empty<DslOverrideAstNode>());

    public IReadOnlyList<DslSkillAstNode> Skills { get; }
    public IReadOnlyList<DslOverrideAstNode> Overrides { get; }

    /// <summary>Returns the set of skill names for use in DSL validation.</summary>
    public IReadOnlySet<string> SkillNames =>
        new HashSet<string>(_skillsByName.Keys, StringComparer.OrdinalIgnoreCase);

    public bool TryGetSkill(string name, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out DslSkillAstNode skill) =>
        _skillsByName.TryGetValue(name ?? string.Empty, out skill);

    /// <summary>Parse a .prayer library file text into a SkillLibrary.</summary>
    public static SkillLibrary Parse(string text) =>
        SkillLibraryParser.Parse(text ?? string.Empty);

    /// <summary>Serialize this library back to .prayer file text.</summary>
    public string Serialize() =>
        DslPrettyPrinter.RenderSkillLibrary(this);
}
