using System;
using System.Linq;

internal static class FuzzyMatchScoring
{
    public static double ComputeScore(string query, string candidateAlias)
    {
        if (query.Length == 0 || candidateAlias.Length == 0)
            return -1d;

        if (string.Equals(query, candidateAlias, StringComparison.Ordinal))
            return 1d;

        if (candidateAlias.StartsWith(query, StringComparison.Ordinal))
            return 0.94d;

        if (query.StartsWith(candidateAlias, StringComparison.Ordinal))
            return 0.88d;

        if (candidateAlias.Contains(query, StringComparison.Ordinal))
            return 0.82d;

        var tokenScore = TokenOverlapScore(query, candidateAlias);
        var editScore = LevenshteinSimilarity(query, candidateAlias);

        return (editScore * 0.65d) + (tokenScore * 0.35d);
    }

    public static int LevenshteinDistance(string a, string b)
    {
        int n = a.Length;
        int m = b.Length;
        if (n == 0)
            return m;
        if (m == 0)
            return n;

        var prev = new int[m + 1];
        var cur = new int[m + 1];

        for (int j = 0; j <= m; j++)
            prev[j] = j;

        for (int i = 1; i <= n; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(
                    Math.Min(cur[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[m];
    }

    private static double TokenOverlapScore(string a, string b)
    {
        var aTokens = a.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var bTokens = b.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (aTokens.Length == 0 || bTokens.Length == 0)
            return 0d;

        var aSet = aTokens.ToHashSet(StringComparer.Ordinal);
        var bSet = bTokens.ToHashSet(StringComparer.Ordinal);

        int overlap = aSet.Count(t => bSet.Contains(t));
        int union = aSet.Count + bSet.Count - overlap;
        if (union <= 0)
            return 0d;

        return overlap / (double)union;
    }

    private static double LevenshteinSimilarity(string a, string b)
    {
        int maxLen = Math.Max(a.Length, b.Length);
        if (maxLen == 0)
            return 1d;

        int distance = LevenshteinDistance(a, b);
        return Math.Max(0d, 1d - (distance / (double)maxLen));
    }
}
