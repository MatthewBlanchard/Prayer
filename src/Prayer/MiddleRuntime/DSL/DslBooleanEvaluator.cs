using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

internal static class DslBooleanEvaluator
{
    private static readonly IReadOnlyDictionary<string, DslBooleanPredicate> BooleanPredicateByName =
        BuildLookup(DslConditionCatalog.BooleanPredicates, p => p.Name);

    private static readonly IReadOnlyDictionary<string, DslNumericPredicate> NumericPredicateByName =
        BuildLookup(DslConditionCatalog.NumericPredicates, p => p.Name);

    internal static bool IsKnownBooleanMetric(string name)
        => BooleanPredicateByName.ContainsKey((name ?? string.Empty).ToUpperInvariant());

    internal static bool IsKnownNumericMetric(string name)
        => NumericPredicateByName.ContainsKey((name ?? string.Empty).ToUpperInvariant());

    public static bool TryValidateCondition(DslConditionAstNode? condition, out string? error)
    {
        error = null;
        if (condition == null) { error = "condition is empty"; return false; }

        if (condition is DslMetricCallConditionAstNode call)
        {
            if (!IsKnownBooleanMetric(call.Name))
            {
                if (IsKnownNumericMetric(call.Name))
                {
                    error = $"unexpected type 'numeric' for predicate '{call.Name}', expected 'boolean'";
                    return false;
                }

                error = $"unknown boolean predicate '{call.Name}'";
                return false;
            }
            return true;
        }

        if (condition is DslComparisonConditionAstNode comparison)
        {
            if (!IsSupportedOp(comparison.Operator))
            {
                error = $"unsupported operator '{comparison.Operator}'";
                return false;
            }
            return IsValidOperand(comparison.Left, out error) &&
                   IsValidOperand(comparison.Right, out error);
        }

        error = "unknown condition type";
        return false;
    }

    public static bool TryEvaluate(DslConditionAstNode? condition, GameState state, out bool value)
    {
        value = false;
        if (state == null || condition == null) return false;

        if (condition is DslMetricCallConditionAstNode call)
        {
            if (!BooleanPredicateByName.TryGetValue(call.Name.ToUpperInvariant(), out var predicate))
                return false;
            value = predicate.Evaluate(state, call.Args);
            return true;
        }

        if (condition is DslComparisonConditionAstNode comparison)
        {
            if (!TryResolveOperand(comparison.Left, state, out var left) ||
                !TryResolveOperand(comparison.Right, state, out var right))
                return false;

            value = comparison.Operator switch
            {
                ">"  => left > right,
                ">=" => left >= right,
                "<"  => left < right,
                "<=" => left <= right,
                "==" => left == right,
                "!=" => left != right,
                _    => false
            };
            return true;
        }

        return false;
    }

    public static string RenderCondition(DslConditionAstNode? condition) => condition switch
    {
        DslMetricCallConditionAstNode call       => RenderCall(call.Name, call.Args),
        DslComparisonConditionAstNode comparison =>
            $"{RenderOperand(comparison.Left)} {comparison.Operator} {RenderOperand(comparison.Right)}",
        _ => string.Empty
    };

    private static bool IsValidOperand(DslNumericOperandAstNode operand, out string? error)
    {
        error = null;
        if (operand is DslIntegerOperandAstNode) return true;
        if (operand is DslMetricCallOperandAstNode call)
        {
            if (!IsKnownNumericMetric(call.Name))
            {
                error = $"unknown numeric predicate '{call.Name}'";
                return false;
            }
            return true;
        }
        error = "unknown operand type";
        return false;
    }

    private static bool TryResolveOperand(DslNumericOperandAstNode operand, GameState state, out int value)
    {
        value = 0;
        if (operand is DslIntegerOperandAstNode i) { value = i.Value; return true; }
        if (operand is DslMetricCallOperandAstNode call &&
            NumericPredicateByName.TryGetValue(call.Name.ToUpperInvariant(), out var predicate))
        {
            value = predicate.Resolve(state, call.Args);
            return true;
        }
        return false;
    }

    private static string RenderOperand(DslNumericOperandAstNode operand) => operand switch
    {
        DslIntegerOperandAstNode i          => i.Value.ToString(CultureInfo.InvariantCulture),
        DslMetricCallOperandAstNode call    => RenderCall(call.Name, call.Args),
        _                                   => string.Empty
    };

    private static string RenderCall(string name, IReadOnlyList<string> args)
        => args.Count == 0 ? $"{name}()" : $"{name}({string.Join(", ", args)})";

    private static bool IsSupportedOp(string op)
        => op is ">" or ">=" or "<" or "<=" or "==" or "!=";

    private static IReadOnlyDictionary<string, T> BuildLookup<T>(
        IReadOnlyList<T> items,
        Func<T, string> getName)
    {
        var map = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items ?? Array.Empty<T>())
        {
            var name = getName(item).Trim().ToUpperInvariant();
            if (map.ContainsKey(name))
                throw new InvalidOperationException($"Duplicate predicate '{name}'.");
            map[name] = item;
        }
        return map;
    }
}
