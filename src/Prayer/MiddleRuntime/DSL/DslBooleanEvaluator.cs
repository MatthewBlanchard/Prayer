using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

internal static class DslBooleanEvaluator
{
    private static readonly Regex ComparisonRegex =
        new(@"^\s*(?<left>.+?)\s*(?<op><=|>=|==|!=|<|>)\s*(?<right>.+?)\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FunctionRegex =
        new(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(\s*\)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MissionCompleteRegex =
        new(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(\s*(?<mission>[A-Za-z0-9_][A-Za-z0-9_-]*)\s*\)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IdentifierRegex =
        new(@"^[A-Za-z_][A-Za-z0-9_]*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<string, DslBooleanPredicate> BooleanPredicateByName =
        BuildBooleanPredicateByName(DslConditionCatalog.BooleanPredicates);

    private static readonly IReadOnlyDictionary<string, DslNumericPredicate> NumericPredicateByName =
        BuildNumericPredicateByName(DslConditionCatalog.NumericPredicates);

    public static bool TryParseCondition(
        string? token,
        out DslConditionAstNode? condition,
        out string? error)
    {
        condition = null;
        error = null;

        var expr = (token ?? string.Empty).Trim();
        if (expr.Length == 0)
        {
            error = "condition is empty";
            return false;
        }

        if (TryParseMissionCompleteCondition(expr, out var missionId))
        {
            condition = new DslMissionCompleteConditionAstNode(missionId);
            return true;
        }

        if (TryParseBooleanToken(expr, out var booleanToken))
        {
            condition = new DslBooleanTokenConditionAstNode(booleanToken);
            return true;
        }

        if (!TryParseComparison(expr, out var leftRaw, out var op, out var rightRaw))
        {
            error = "expected a known boolean token or comparison like FUEL() > 5";
            return false;
        }

        if (!TryParseNumericOperand(leftRaw, out var left))
        {
            error = $"unsupported left operand '{leftRaw.Trim()}'";
            return false;
        }

        if (!TryParseNumericOperand(rightRaw, out var right))
        {
            error = $"unsupported right operand '{rightRaw.Trim()}'";
            return false;
        }

        condition = new DslComparisonConditionAstNode(left, op, right);
        return true;
    }

    public static bool TryValidateCondition(DslConditionAstNode? condition, out string? error)
    {
        error = null;
        if (condition == null)
        {
            error = "condition is empty";
            return false;
        }

        switch (condition)
        {
            case DslMissionCompleteConditionAstNode missionComplete:
                if (!IsValidMissionIdToken(missionComplete.MissionId))
                {
                    error = "MISSION_COMPLETE requires a mission id argument.";
                    return false;
                }
                return true;
            case DslBooleanTokenConditionAstNode booleanToken:
                if (!IsKnownBooleanToken(booleanToken.Token))
                {
                    error = $"unsupported boolean token '{booleanToken.Token}'";
                    return false;
                }
                return true;
            case DslComparisonConditionAstNode comparison:
                if (!IsSupportedComparisonOperator(comparison.Operator))
                {
                    error = $"unsupported operator '{comparison.Operator}'";
                    return false;
                }
                if (!IsValidNumericOperand(comparison.Left))
                {
                    error = "unsupported left operand";
                    return false;
                }
                if (!IsValidNumericOperand(comparison.Right))
                {
                    error = "unsupported right operand";
                    return false;
                }
                return true;
            default:
                error = "unknown condition node";
                return false;
        }
    }

    public static bool TryEvaluate(DslConditionAstNode? condition, GameState state, out bool value)
    {
        value = false;
        if (state == null || condition == null)
            return false;

        switch (condition)
        {
            case DslMissionCompleteConditionAstNode missionComplete:
                value = IsMissionComplete(missionComplete.MissionId, state);
                return true;
            case DslBooleanTokenConditionAstNode booleanToken:
                return TryEvaluateBooleanToken(booleanToken.Token, state, out value);
            case DslComparisonConditionAstNode comparison:
                if (!TryResolveNumericOperand(comparison.Left, state, out var left) ||
                    !TryResolveNumericOperand(comparison.Right, state, out var right))
                {
                    return false;
                }

                value = comparison.Operator switch
                {
                    ">" => left > right,
                    ">=" => left >= right,
                    "<" => left < right,
                    "<=" => left <= right,
                    "==" => left == right,
                    "!=" => left != right,
                    _ => false
                };
                return true;
            default:
                return false;
        }
    }

    public static string RenderCondition(DslConditionAstNode? condition)
    {
        if (condition == null)
            return string.Empty;

        return condition switch
        {
            DslMissionCompleteConditionAstNode missionComplete =>
                $"MISSION_COMPLETE({missionComplete.MissionId})",
            DslBooleanTokenConditionAstNode booleanToken =>
                booleanToken.Token.Trim().ToUpperInvariant(),
            DslComparisonConditionAstNode comparison =>
                $"{RenderNumericOperand(comparison.Left)} {comparison.Operator} {RenderNumericOperand(comparison.Right)}",
            _ => string.Empty
        };
    }

    private static bool TryParseMissionCompleteCondition(string token, out string missionId)
    {
        missionId = string.Empty;
        var match = MissionCompleteRegex.Match((token ?? string.Empty).Trim());
        if (!match.Success)
            return false;

        var name = match.Groups["name"].Value.Trim();
        if (!name.Equals("MISSION_COMPLETE", StringComparison.OrdinalIgnoreCase))
            return false;

        missionId = match.Groups["mission"].Value.Trim();
        return IsValidMissionIdToken(missionId);
    }

    private static bool TryParseBooleanToken(string token, out string normalizedToken)
    {
        normalizedToken = token.Trim().ToUpperInvariant();
        if (IsKnownBooleanToken(normalizedToken))
            return true;

        var fnMatch = FunctionRegex.Match(token.Trim());
        if (!fnMatch.Success)
            return false;

        normalizedToken = fnMatch.Groups["name"].Value.Trim().ToUpperInvariant();
        return IsKnownBooleanToken(normalizedToken);
    }

    private static bool IsKnownBooleanToken(string token)
    {
        var normalized = (token ?? string.Empty).Trim().ToUpperInvariant();
        return BooleanPredicateByName.ContainsKey(normalized);
    }

    private static bool TryEvaluateBooleanToken(string token, GameState state, out bool value)
    {
        var normalized = token.Trim().ToUpperInvariant();
        if (!BooleanPredicateByName.TryGetValue(normalized, out var predicate))
        {
            value = false;
            return false;
        }

        value = predicate.Evaluate(state);
        return true;
    }

    private static bool TryParseComparison(
        string expr,
        out string left,
        out string op,
        out string right)
    {
        left = string.Empty;
        op = string.Empty;
        right = string.Empty;

        var match = ComparisonRegex.Match(expr);
        if (!match.Success)
            return false;

        left = match.Groups["left"].Value;
        op = match.Groups["op"].Value;
        right = match.Groups["right"].Value;
        return left.Trim().Length > 0 && right.Trim().Length > 0;
    }

    private static bool IsSupportedComparisonOperator(string op)
        => op is ">" or ">=" or "<" or "<=" or "==" or "!=";

    private static bool IsValidNumericOperand(DslNumericOperandAstNode operand)
    {
        return operand switch
        {
            DslIntegerOperandAstNode => true,
            DslMetricOperandAstNode metric => IsKnownMetric(metric.MetricName),
            _ => false
        };
    }

    private static bool TryParseNumericOperand(string raw, out DslNumericOperandAstNode operand)
    {
        operand = new DslIntegerOperandAstNode(0);
        var text = raw.Trim();
        if (text.Length == 0)
            return false;

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            operand = new DslIntegerOperandAstNode(numeric);
            return true;
        }

        if (!TryNormalizeMetricName(text, out var metricName))
            return false;

        if (!IsKnownMetric(metricName))
            return false;

        operand = new DslMetricOperandAstNode(metricName);
        return true;
    }

    private static string RenderNumericOperand(DslNumericOperandAstNode operand)
    {
        return operand switch
        {
            DslIntegerOperandAstNode integer => integer.Value.ToString(CultureInfo.InvariantCulture),
            DslMetricOperandAstNode metric => $"{metric.MetricName}()",
            _ => string.Empty
        };
    }

    private static bool TryResolveNumericOperand(
        DslNumericOperandAstNode operand,
        GameState state,
        out int value)
    {
        value = 0;
        switch (operand)
        {
            case DslIntegerOperandAstNode integer:
                value = integer.Value;
                return true;
            case DslMetricOperandAstNode metric:
                return TryGetMetricValue(metric.MetricName, state, out value);
            default:
                return false;
        }
    }

    private static bool TryNormalizeMetricName(string operand, out string metricName)
    {
        metricName = string.Empty;
        var fnMatch = FunctionRegex.Match(operand);
        if (fnMatch.Success)
        {
            metricName = fnMatch.Groups["name"].Value.Trim().ToUpperInvariant();
            return metricName.Length > 0;
        }

        if (!IdentifierRegex.IsMatch(operand))
            return false;

        metricName = operand.Trim().ToUpperInvariant();
        return metricName.Length > 0;
    }

    private static bool IsKnownMetric(string metricName)
        => NumericPredicateByName.ContainsKey((metricName ?? string.Empty).Trim().ToUpperInvariant());

    private static bool TryGetMetricValue(string metricName, GameState state, out int value)
    {
        var normalized = (metricName ?? string.Empty).Trim().ToUpperInvariant();
        if (!NumericPredicateByName.TryGetValue(normalized, out var predicate))
        {
            value = 0;
            return false;
        }

        value = predicate.Resolve(state);
        return true;
    }

    private static bool IsMissionComplete(string missionId, GameState state)
    {
        if (!IsValidMissionIdToken(missionId))
            return false;

        var missions = state.ActiveMissions ?? Array.Empty<MissionInfo>();
        foreach (var mission in missions)
        {
            if (mission == null)
                continue;

            if (!MissionMatchesPrefix(mission, missionId))
                continue;

            return mission.Completed;
        }

        return false;
    }

    private static bool MissionMatchesPrefix(MissionInfo mission, string missionPrefix)
    {
        if (string.IsNullOrWhiteSpace(missionPrefix))
            return false;

        return MatchesPrefix(mission.Id, missionPrefix) ||
               MatchesPrefix(mission.MissionId, missionPrefix);
    }

    private static bool MatchesPrefix(string? missionIdValue, string prefix)
    {
        var id = (missionIdValue ?? string.Empty).Trim();
        return !string.IsNullOrWhiteSpace(id) &&
               id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidMissionIdToken(string missionId)
    {
        if (string.IsNullOrWhiteSpace(missionId))
            return false;

        if (missionId.Length < 6)
            return false;

        foreach (var ch in missionId)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
                continue;

            return false;
        }

        return true;
    }

    private static IReadOnlyDictionary<string, DslBooleanPredicate> BuildBooleanPredicateByName(
        IReadOnlyList<DslBooleanPredicate> predicates)
    {
        var map = new Dictionary<string, DslBooleanPredicate>(StringComparer.OrdinalIgnoreCase);
        foreach (var predicate in predicates ?? Array.Empty<DslBooleanPredicate>())
        {
            if (string.IsNullOrWhiteSpace(predicate.Name))
                continue;

            var name = predicate.Name.Trim().ToUpperInvariant();
            if (map.ContainsKey(name))
                throw new InvalidOperationException($"Duplicate boolean predicate '{name}'.");

            map[name] = predicate;
        }

        return map;
    }

    private static IReadOnlyDictionary<string, DslNumericPredicate> BuildNumericPredicateByName(
        IReadOnlyList<DslNumericPredicate> predicates)
    {
        var map = new Dictionary<string, DslNumericPredicate>(StringComparer.OrdinalIgnoreCase);
        foreach (var predicate in predicates ?? Array.Empty<DslNumericPredicate>())
        {
            if (string.IsNullOrWhiteSpace(predicate.Name))
                continue;

            var name = predicate.Name.Trim().ToUpperInvariant();
            if (map.ContainsKey(name))
                throw new InvalidOperationException($"Duplicate numeric predicate '{name}'.");

            map[name] = predicate;
        }

        return map;
    }
}
