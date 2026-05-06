using System.Globalization;
using System.Text;

namespace Umbraco.Community.AiVisibility.Tests.TestHelpers;

/// <summary>
/// Bespoke unified-diff formatter for <c>ExtractionQualityBenchmarkTests</c> failure
/// messages. Deliberately a test-project-internal helper rather than pulling in a full
/// diff library — fixture diffs are typically &lt;500 lines and an LCS-based diff is
/// fast enough.
///
/// <para>
/// Output format follows the standard unified-diff convention:
/// <c>--- expected</c>, <c>+++ actual</c>, hunks prefixed with
/// <c>@@ -startE,lenE +startA,lenA @@</c>, context lines prefixed with a single space,
/// removed lines with <c>-</c>, added lines with <c>+</c>. Long diffs are truncated
/// with an explicit footer so test runner output stays readable.
/// </para>
/// </summary>
internal static class UnifiedDiffFormatter
{
    public static string Format(
        string expected,
        string actual,
        int contextLines = 3,
        int maxOutputLines = 200)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        // Normalise line endings — CRLF (Windows) and bare CR (legacy Mac) collapse to
        // LF so downstream LCS isn't fooled by literal \r-suffix mismatches when the
        // inputs differ only in line-ending convention. Production callsite already
        // normalises with .ReplaceLineEndings("\n"), but the helper is reused across
        // the test project so we defend the contract here too.
        var normalisedExpected = NormaliseLineEndings(expected);
        var normalisedActual = NormaliseLineEndings(actual);

        if (string.Equals(normalisedExpected, normalisedActual, StringComparison.Ordinal))
        {
            // Inputs differed only in line-ending style — equal under the diff contract.
            return string.Empty;
        }

        var (expectedLines, expectedNewlineAtEof) = SplitLines(normalisedExpected);
        var (actualLines, actualNewlineAtEof) = SplitLines(normalisedActual);

        var ops = ComputeEditScript(expectedLines, actualLines);
        var hunks = GroupIntoHunks(ops, contextLines);
        return EmitUnifiedDiff(
            ops,
            hunks,
            expectedLines,
            actualLines,
            expectedNewlineAtEof,
            actualNewlineAtEof,
            maxOutputLines);
    }

    private static string NormaliseLineEndings(string s)
        => s.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>
    /// Split into content lines without including a trailing-empty entry when the
    /// source ends with <c>\n</c>. Tracks the trailing-newline state separately so
    /// we can surface it via the conventional <c>\ No newline at end of file</c>
    /// marker — emitting a bare <c>-</c> followed by an empty line (the previous
    /// behaviour) is the most common false-failure presentation per the fixtures
    /// README, since it's invisible in test runner output.
    /// </summary>
    private static (string[] Lines, bool NewlineAtEof) SplitLines(string s)
    {
        if (s.Length == 0)
        {
            return (Array.Empty<string>(), false);
        }
        if (s[^1] == '\n')
        {
            return (s.Substring(0, s.Length - 1).Split('\n'), true);
        }
        return (s.Split('\n'), false);
    }

    /// <summary>
    /// Standard LCS via a full DP table; O(N*M) in time and space. Fixture sizes (max ~few
    /// hundred lines) are well under the threshold where this matters; the trade for code
    /// clarity over Myers' O(ND) is deliberate.
    /// </summary>
    private static List<EditOp> ComputeEditScript(string[] a, string[] b)
    {
        var n = a.Length;
        var m = b.Length;

        var dp = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) dp[i, 0] = i;
        for (var j = 0; j <= m; j++) dp[0, j] = j;

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                if (string.Equals(a[i - 1], b[j - 1], StringComparison.Ordinal))
                {
                    dp[i, j] = dp[i - 1, j - 1];
                }
                else
                {
                    dp[i, j] = 1 + Math.Min(dp[i - 1, j], dp[i, j - 1]);
                }
            }
        }

        // Backtrack from (n, m) to (0, 0) emitting Equal/Delete/Insert ops; reverse at end.
        var ops = new List<EditOp>(n + m);
        var ai = n;
        var bi = m;
        while (ai > 0 || bi > 0)
        {
            if (ai > 0 && bi > 0 && string.Equals(a[ai - 1], b[bi - 1], StringComparison.Ordinal))
            {
                ops.Add(new EditOp(EditKind.Equal, ai - 1, bi - 1));
                ai--;
                bi--;
            }
            else if (bi > 0 && (ai == 0 || dp[ai, bi - 1] <= dp[ai - 1, bi]))
            {
                ops.Add(new EditOp(EditKind.Insert, -1, bi - 1));
                bi--;
            }
            else
            {
                ops.Add(new EditOp(EditKind.Delete, ai - 1, -1));
                ai--;
            }
        }
        ops.Reverse();
        return ops;
    }

    /// <summary>
    /// Group consecutive non-Equal ops with up to <paramref name="contextLines"/> of
    /// surrounding Equal context. Adjacent change clusters whose context windows overlap
    /// (i.e. separated by ≤ 2*contextLines unchanged lines) are merged into a single hunk.
    /// </summary>
    private static List<Hunk> GroupIntoHunks(List<EditOp> ops, int contextLines)
    {
        var hunks = new List<Hunk>();
        var i = 0;
        while (i < ops.Count)
        {
            while (i < ops.Count && ops[i].Kind == EditKind.Equal)
            {
                i++;
            }
            if (i == ops.Count) break;

            var firstChange = i;
            var hunkStart = Math.Max(0, firstChange - contextLines);
            var lastChange = firstChange;

            // Walk forward, extending while the next gap is small enough that contexts merge.
            var cursor = firstChange + 1;
            while (cursor < ops.Count)
            {
                if (ops[cursor].Kind != EditKind.Equal)
                {
                    lastChange = cursor;
                    cursor++;
                    continue;
                }
                // Look ahead for the next change.
                var look = cursor;
                while (look < ops.Count && ops[look].Kind == EditKind.Equal)
                {
                    look++;
                }
                if (look == ops.Count) break;
                var gap = look - cursor;
                if (gap <= 2 * contextLines)
                {
                    lastChange = look;
                    cursor = look + 1;
                }
                else
                {
                    break;
                }
            }

            var hunkEnd = Math.Min(ops.Count, lastChange + 1 + contextLines);
            hunks.Add(new Hunk(hunkStart, hunkEnd));
            i = hunkEnd;
        }
        return hunks;
    }

    private static string EmitUnifiedDiff(
        List<EditOp> ops,
        List<Hunk> hunks,
        string[] expectedLines,
        string[] actualLines,
        bool expectedNewlineAtEof,
        bool actualNewlineAtEof,
        int maxOutputLines)
    {
        var sb = new StringBuilder();
        sb.Append("--- expected\n");
        sb.Append("+++ actual\n");
        var emitted = 2;
        var truncated = false;

        foreach (var hunk in hunks)
        {
            // Determine hunk header counts.
            int? expectedStart = null;
            int? actualStart = null;
            var expectedLen = 0;
            var actualLen = 0;
            for (var idx = hunk.Start; idx < hunk.End; idx++)
            {
                var op = ops[idx];
                if (op.Kind != EditKind.Insert)
                {
                    expectedStart ??= op.ExpectedIndex + 1;
                    expectedLen++;
                }
                if (op.Kind != EditKind.Delete)
                {
                    actualStart ??= op.ActualIndex + 1;
                    actualLen++;
                }
            }

            // Empty hunk safety — shouldn't happen but guard against it.
            if (expectedLen == 0 && actualLen == 0) continue;

            var header = string.Format(
                CultureInfo.InvariantCulture,
                "@@ -{0},{1} +{2},{3} @@\n",
                expectedStart ?? 1,
                expectedLen,
                actualStart ?? 1,
                actualLen);

            if (emitted + 1 > maxOutputLines)
            {
                truncated = true;
                break;
            }

            sb.Append(header);
            emitted++;

            for (var idx = hunk.Start; idx < hunk.End; idx++)
            {
                if (emitted >= maxOutputLines)
                {
                    truncated = true;
                    break;
                }

                var op = ops[idx];
                switch (op.Kind)
                {
                    case EditKind.Equal:
                        sb.Append(' ').Append(expectedLines[op.ExpectedIndex]).Append('\n');
                        break;
                    case EditKind.Delete:
                        sb.Append('-').Append(expectedLines[op.ExpectedIndex]).Append('\n');
                        break;
                    case EditKind.Insert:
                        sb.Append('+').Append(actualLines[op.ActualIndex]).Append('\n');
                        break;
                }
                emitted++;
            }

            if (truncated) break;
        }

        if (!truncated && expectedNewlineAtEof != actualNewlineAtEof)
        {
            // Surface the trailing-newline-state difference explicitly instead of
            // letting it manifest as a bare `-` line in the diff body — which is
            // invisible in most test runners and the most common false-failure
            // source per the fixtures README. Standard convention is the
            // `\ No newline at end of file` marker; we attach a one-line tail
            // naming which side lacks the trailing newline.
            var lacksLine = expectedNewlineAtEof
                ? "actual lacks trailing newline (expected has one)"
                : "expected lacks trailing newline (actual has one)";
            sb.Append("\\ No newline at end of file — ").Append(lacksLine).Append('\n');
        }

        if (truncated)
        {
            sb.Append("... (truncated; output capped at ")
                .Append(maxOutputLines.ToString(CultureInfo.InvariantCulture))
                .Append(" lines)\n");
        }

        return sb.ToString();
    }

    private readonly record struct EditOp(EditKind Kind, int ExpectedIndex, int ActualIndex);

    private enum EditKind { Equal, Delete, Insert }

    private readonly record struct Hunk(int Start, int End);
}
