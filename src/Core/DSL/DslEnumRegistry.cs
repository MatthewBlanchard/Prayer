using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

internal static class DslEnumRegistry
{
    private static readonly Dictionary<string, HashSet<string>> EnumsByType =
        new(StringComparer.Ordinal);

    static DslEnumRegistry()
    {
        Define("mining_target", new[]
        {
            "asteroid_belt",
            "asteroid",
            "gas_cloud",
            "ice_field"
        });

        Define("cargo_keyword", new[]
        {
            "cargo"
        });
    }

    public static void Define(string enumType, IEnumerable<string> values)
    {
        var key = Normalize(enumType);
        if (string.IsNullOrWhiteSpace(key))
            return;

        var set = values
            .Select(Normalize)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToHashSet(StringComparer.Ordinal);

        if (set.Count == 0)
            return;

        EnumsByType[key] = set;
    }

    public static IReadOnlyList<string> GetValues(string? enumType)
    {
        var key = Normalize(enumType ?? string.Empty);
        if (string.IsNullOrWhiteSpace(key))
            return Array.Empty<string>();

        return EnumsByType.TryGetValue(key, out var values)
            ? values.ToList()
            : Array.Empty<string>();
    }

    public static IReadOnlyList<string> ResolveValues(DslArgumentSpec spec)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);

        foreach (var v in GetValues(spec.EnumType))
            values.Add(v);

        if (spec.EnumValues != null)
        {
            foreach (var value in spec.EnumValues)
            {
                var normalized = Normalize(value ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(normalized))
                    values.Add(normalized);
            }
        }

        return values.ToList();
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var sb = new StringBuilder(value.Length);
        bool prevUnderscore = false;

        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                prevUnderscore = false;
                continue;
            }

            if (ch == '_' || ch == '-' || char.IsWhiteSpace(ch))
            {
                if (!prevUnderscore)
                {
                    sb.Append('_');
                    prevUnderscore = true;
                }
            }
        }

        return sb.ToString().Trim('_');
    }
}
