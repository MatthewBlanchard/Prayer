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

    public static bool TryEvaluate(
        DslConditionAstNode? condition,
        GameState state,
        out bool value,
        IReadOnlyDictionary<string, string>? bindings = null)
    {
        value = false;
        if (state == null || condition == null) return false;

        if (condition is DslMetricCallConditionAstNode call)
        {
            if (!BooleanPredicateByName.TryGetValue(call.Name.ToUpperInvariant(), out var predicate))
                return false;
            var expandedArgs = ExpandArgs(call.Args, state, bindings);
            try
            {
                value = predicate.Evaluate(state, expandedArgs);
                return true;
            }
            catch (Exception ex)
            {
                throw new FormatException(
                    $"Condition evaluation failed for '{RenderCall(call.Name, call.Args)}': {ex.Message}",
                    ex);
            }
        }

        if (condition is DslComparisonConditionAstNode comparison)
        {
            if (!TryResolveOperand(comparison.Left, state, bindings, out var left) ||
                !TryResolveOperand(comparison.Right, state, bindings, out var right))
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
        if (operand is DslArgumentRefOperandAstNode) return true; // param/macro ref, resolved at runtime
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

    private static bool TryResolveOperand(
        DslNumericOperandAstNode operand,
        GameState state,
        IReadOnlyDictionary<string, string>? bindings,
        out int value)
    {
        value = 0;
        if (operand is DslIntegerOperandAstNode i) { value = i.Value; return true; }
        if (operand is DslMetricCallOperandAstNode call &&
            NumericPredicateByName.TryGetValue(call.Name.ToUpperInvariant(), out var predicate))
        {
            value = predicate.Resolve(state, ExpandArgs(call.Args, state, bindings));
            return true;
        }
        if (operand is DslArgumentRefOperandAstNode refOp)
        {
            var resolved = ResolveArgToken(refOp.Token, state, bindings);
            return int.TryParse(resolved, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }
        return false;
    }

    private static IReadOnlyList<string> ExpandArgs(
        IReadOnlyList<string> args,
        GameState state,
        IReadOnlyDictionary<string, string>? bindings)
    {
        if (args.Count == 0) return args;
        var expanded = new string[args.Count];
        for (int i = 0; i < args.Count; i++)
            expanded[i] = ResolveArgToken(args[i], state, bindings);
        return expanded;
    }

    private static string ResolveArgToken(string arg, GameState state, IReadOnlyDictionary<string, string>? bindings)
    {
        if (string.IsNullOrEmpty(arg) || !arg.StartsWith("$", StringComparison.Ordinal))
            return arg;

        var name = arg[1..];
        if (bindings != null && bindings.TryGetValue(name, out var bound))
            return bound;

        return DslParser.ExpandMacroArg(arg, state);
    }

    private static string RenderOperand(DslNumericOperandAstNode operand) => operand switch
    {
        DslIntegerOperandAstNode i          => i.Value.ToString(CultureInfo.InvariantCulture),
        DslMetricCallOperandAstNode call    => RenderCall(call.Name, call.Args),
        DslArgumentRefOperandAstNode r      => r.Token,
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
