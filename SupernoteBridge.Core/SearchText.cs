using System.Globalization;
using System.Text;

namespace SupernoteBridge.Core;

/// <summary>
/// Case- and diacritic-insensitive matching (č ć → c, š → s, ž → z, đ → d, …).
/// Normalization maps every character to exactly one character, so positions found in the
/// normalized text are valid in the original text (used for snippets and highlighting).
/// </summary>
public static class SearchText
{
    public static char Fold(char c)
    {
        if (c is 'đ' or 'Đ') return 'd';                // no Unicode decomposition for đ
        if (c < 128) return char.ToLowerInvariant(c);
        if (char.IsSurrogate(c)) return c;              // half of an emoji etc.: can't be normalized alone
        var d = c.ToString().Normalize(NormalizationForm.FormD);
        foreach (char x in d)
            if (CharUnicodeInfo.GetUnicodeCategory(x) != UnicodeCategory.NonSpacingMark)
                return char.ToLowerInvariant(x);
        return char.ToLowerInvariant(c);
    }

    public static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(Fold(c));
        return sb.ToString();
    }

    /// <summary>Query → folded words (whitespace separated, empties dropped).</summary>
    public static string[] Words(string query) =>
        Fold(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>True when every word occurs in the (already folded) text.</summary>
    public static bool MatchesAll(string foldedText, string[] words) =>
        words.Length > 0 && words.All(w => foldedText.Contains(w, StringComparison.Ordinal));

    /// <summary>Snippet of the original text around the first word, with [start,length) ranges of every word hit inside it.</summary>
    public static (string Text, List<(int Start, int Length)> Marks) Snippet(string original, string folded, string[] words, int radius = 70)
    {
        int first = words.Select(w => folded.IndexOf(w, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        int from = Math.Max(0, first - radius), to = Math.Min(original.Length, first + radius + 40);
        // don't cut words in half (but never grow by more than 20 chars on a side)
        for (int n = 0; n < 20 && from > 0 && !char.IsWhiteSpace(original[from - 1]); n++) from--;
        for (int n = 0; n < 20 && to < original.Length && !char.IsWhiteSpace(original[to]); n++) to++;
        string text = original[from..to].Replace('\n', ' ').Replace('\r', ' ');
        string ftext = folded[from..to];
        var marks = new List<(int, int)>();
        foreach (var w in words)
            for (int i = ftext.IndexOf(w, StringComparison.Ordinal); i >= 0; i = ftext.IndexOf(w, i + w.Length, StringComparison.Ordinal))
                marks.Add((i, w.Length));
        marks.Sort();
        return ((from > 0 ? "…" : "") + text + (to < original.Length ? "…" : ""),
                from > 0 ? marks.Select(m => (m.Item1 + 1, m.Item2)).ToList() : marks);
    }
}
