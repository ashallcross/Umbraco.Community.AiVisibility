using System.Text.RegularExpressions;

namespace Umbraco.Community.AiVisibility.TestSite.Spikes;

/// <summary>
/// Diff helper for the spike — compares two HTML strings after stripping noise that
/// would show up as a difference even when both renders are semantically identical
/// (whitespace runs, comments, dynamic timestamps, anti-forgery tokens, asp-route ids).
/// Used as a SPIKE TOOL ONLY — production code uses ReverseMarkdown semantic diffs.
/// </summary>
internal static partial class HtmlDiffer
{
    public static SpikeDiff Compare(string inProcess, string http)
    {
        var a = Normalize(inProcess);
        var b = Normalize(http);

        if (a.Length == b.Length && a == b)
        {
            return new SpikeDiff(Identical: true, inProcess.Length, http.Length, FirstDifferenceContext: null);
        }

        var firstDiffIndex = FirstDifferenceIndex(a, b);
        var contextStart = Math.Max(0, firstDiffIndex - 60);
        // contextStart can exceed the shorter string's length (e.g. one side is a
        // prefix of the other); clamp the slice length to non-negative so
        // Substring doesn't throw ArgumentOutOfRangeException.
        var contextLengthA = Math.Max(0, Math.Min(120, a.Length - contextStart));
        var contextLengthB = Math.Max(0, Math.Min(120, b.Length - contextStart));
        var snippetA = contextStart <= a.Length ? a.Substring(contextStart, contextLengthA) : string.Empty;
        var snippetB = contextStart <= b.Length ? b.Substring(contextStart, contextLengthB) : string.Empty;

        return new SpikeDiff(
            Identical: false,
            InProcessLength: inProcess.Length,
            HttpLength: http.Length,
            FirstDifferenceContext: $"@{firstDiffIndex}: in-process=[{snippetA}] http=[{snippetB}]");
    }

    private static string Normalize(string html)
    {
        var s = HtmlComments().Replace(html, string.Empty);
        s = AntiForgeryTokens().Replace(s, "[__RVT__]");
        s = TimeElements().Replace(s, "<time>[__TS__]</time>");
        s = MetaGenerator().Replace(s, "<meta name=\"generator\" />");
        s = WhitespaceRuns().Replace(s, " ");
        return s.Trim();
    }

    private static int FirstDifferenceIndex(string a, string b)
    {
        var min = Math.Min(a.Length, b.Length);
        for (var i = 0; i < min; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }
        return min;
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComments();

    [GeneratedRegex(@"name=""__RequestVerificationToken""\s+value=""[^""]+""")]
    private static partial Regex AntiForgeryTokens();

    [GeneratedRegex(@"<time[^>]*>.*?</time>", RegexOptions.Singleline)]
    private static partial Regex TimeElements();

    [GeneratedRegex(@"<meta\s+name=""generator""[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaGenerator();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRuns();
}
