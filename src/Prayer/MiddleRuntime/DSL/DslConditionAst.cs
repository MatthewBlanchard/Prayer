public abstract record DslConditionAstNode;

public sealed record DslBooleanTokenConditionAstNode(string Token) : DslConditionAstNode;
public sealed record DslMissionCompleteConditionAstNode(string MissionId) : DslConditionAstNode;

public sealed record DslComparisonConditionAstNode(
    DslNumericOperandAstNode Left,
    string Operator,
    DslNumericOperandAstNode Right) : DslConditionAstNode;

public abstract record DslNumericOperandAstNode;

public sealed record DslIntegerOperandAstNode(int Value) : DslNumericOperandAstNode;

public sealed record DslMetricOperandAstNode(string MetricName) : DslNumericOperandAstNode;
