public abstract record DslConditionAstNode;

public sealed record DslMetricCallConditionAstNode(
    string Name,
    IReadOnlyList<string> Args) : DslConditionAstNode;

public sealed record DslComparisonConditionAstNode(
    DslNumericOperandAstNode Left,
    string Operator,
    DslNumericOperandAstNode Right) : DslConditionAstNode;

public abstract record DslNumericOperandAstNode;

public sealed record DslIntegerOperandAstNode(int Value) : DslNumericOperandAstNode;

public sealed record DslMetricCallOperandAstNode(
    string Name,
    IReadOnlyList<string> Args) : DslNumericOperandAstNode;

/// <summary>A $param or $macro reference used as a numeric operand (resolved at runtime).</summary>
public sealed record DslArgumentRefOperandAstNode(string Token) : DslNumericOperandAstNode;
