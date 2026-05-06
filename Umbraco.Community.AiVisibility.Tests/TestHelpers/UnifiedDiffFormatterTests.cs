using Umbraco.Community.AiVisibility.Tests.TestHelpers;

namespace Umbraco.Community.AiVisibility.Tests.TestHelpers;

/// <summary>
/// Validates the bespoke unified-diff formatter used by
/// <c>ExtractionQualityBenchmarkTests</c>. Goals: produce output a developer can paste
/// into a code review comment, isolate the actual drift (not show 500 unchanged lines
/// around a single-character change), and gracefully cap pathologically-long diffs.
/// </summary>
[TestFixture]
public class UnifiedDiffFormatterTests
{
    [Test]
    public void Format_IdenticalInputs_ReturnsEmptyString()
    {
        var expected = "alpha\nbeta\ngamma\n";
        var actual = "alpha\nbeta\ngamma\n";

        var result = UnifiedDiffFormatter.Format(expected, actual);

        Assert.That(result, Is.Empty,
            "no drift means no diff output — caller can short-circuit on string.Equals(...) before calling Format()");
    }

    [Test]
    public void Format_SingleLineAdded_EmitsOneHunkWithPlusPrefix()
    {
        var expected = "alpha\nbeta\ngamma\n";
        var actual = "alpha\nbeta\nADDED\ngamma\n";

        var result = UnifiedDiffFormatter.Format(expected, actual);

        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Contain("--- expected"),
                "unified diff must start with the --- header");
            Assert.That(result, Does.Contain("+++ actual"),
                "unified diff must include the +++ header");
            Assert.That(result, Does.Contain("@@"),
                "hunk header must be present");
            Assert.That(result, Does.Contain("+ADDED"),
                "added line must be prefixed with + (no space)");
            Assert.That(result, Does.Contain(" alpha"),
                "context lines must be prefixed with a single space");
        });
    }

    [Test]
    public void Format_SingleLineRemoved_EmitsOneHunkWithMinusPrefix()
    {
        var expected = "alpha\nbeta\nREMOVED\ngamma\n";
        var actual = "alpha\nbeta\ngamma\n";

        var result = UnifiedDiffFormatter.Format(expected, actual);

        Assert.That(result, Does.Contain("-REMOVED"),
            "removed line must be prefixed with - (no space)");
    }

    [Test]
    public void Format_LineModified_EmitsRemoveAndAdd()
    {
        var expected = "alpha\nold-content\ngamma\n";
        var actual = "alpha\nnew-content\ngamma\n";

        var result = UnifiedDiffFormatter.Format(expected, actual);

        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Contain("-old-content"));
            Assert.That(result, Does.Contain("+new-content"));
        });
    }

    [Test]
    public void Format_TwoSeparateHunks_EmitsTwoHunkHeaders()
    {
        // 10 unchanged lines between two drifts → with contextLines=3, the drifts are
        // far enough apart to be reported as two separate hunks (not coalesced).
        var expected = string.Join('\n',
            "header",
            "FIRST-DRIFT-OLD",
            "ctx1", "ctx2", "ctx3", "ctx4", "ctx5", "ctx6", "ctx7", "ctx8", "ctx9", "ctx10",
            "SECOND-DRIFT-OLD",
            "footer") + "\n";
        var actual = string.Join('\n',
            "header",
            "FIRST-DRIFT-NEW",
            "ctx1", "ctx2", "ctx3", "ctx4", "ctx5", "ctx6", "ctx7", "ctx8", "ctx9", "ctx10",
            "SECOND-DRIFT-NEW",
            "footer") + "\n";

        var result = UnifiedDiffFormatter.Format(expected, actual, contextLines: 3);

        var hunkCount = result.Split('\n').Count(line => line.StartsWith("@@"));
        Assert.That(hunkCount, Is.EqualTo(2),
            "two drifts separated by more than 2*contextLines unchanged lines must yield two hunks");
    }

    [Test]
    public void Format_LongDiff_TruncatesAtMaxOutputLinesWithFooter()
    {
        // Drive a diff longer than the cap — every line different.
        var expectedLines = Enumerable.Range(0, 300).Select(i => $"old-{i}");
        var actualLines = Enumerable.Range(0, 300).Select(i => $"new-{i}");
        var expected = string.Join('\n', expectedLines) + "\n";
        var actual = string.Join('\n', actualLines) + "\n";

        var result = UnifiedDiffFormatter.Format(expected, actual, contextLines: 3, maxOutputLines: 50);

        Assert.Multiple(() =>
        {
            var resultLines = result.Split('\n');
            // Exact contract: maxOutputLines (50) content lines + 1 truncation footer
            // = 51 newline-terminated lines; Split('\n') of N newline-terminated lines
            // produces N+1 elements (the trailing empty after the final '\n'). So the
            // hard cap is 52. Tightening this to the exact contract — anything looser
            // lets a regression that emits an extra line slip through silently.
            Assert.That(resultLines.Length, Is.LessThanOrEqualTo(52),
                "exact cap: 50 content + 1 footer + 1 trailing-empty-from-Split = 52");
            Assert.That(result, Does.Contain("truncated"),
                "footer must explicitly call out that the diff was truncated");
        });
    }

    [Test]
    public void Format_TrailingNewlineDifference_EmitsConventionalMarker()
    {
        // Real-world failure mode: handcrafted expected.md ends with \n; live extractor
        // output didn't. The diff must surface this with the conventional
        // `\ No newline at end of file` marker — naming which side lacks the trailing
        // newline — rather than emitting a bare `-` followed by an empty line, which is
        // invisible in test runner output (the most common false-failure source per the
        // fixtures README).
        var expected = "alpha\nbeta\n";
        var actual = "alpha\nbeta";

        var result = UnifiedDiffFormatter.Format(expected, actual);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Empty,
                "trailing-newline-only drift must produce a non-empty diff");
            Assert.That(result, Does.Contain("\\ No newline at end of file"),
                "must surface the conventional EOF-newline marker");
            Assert.That(result, Does.Contain("actual lacks trailing newline"),
                "must name which side lacks the trailing newline");
            // Anti-test for the previous behaviour: the diff must NOT contain a bare
            // `-` line followed by an empty line, which is what Split('\n') with
            // trailing-empty inclusion previously produced.
            Assert.That(result, Does.Not.Match(@"(?m)^-\s*$"),
                "must NOT emit a bare `-` line for the missing trailing newline");
        });
    }

    [Test]
    public void Format_LineEndingsDifferOnly_TreatedAsEqual()
    {
        // CRLF vs LF inputs that are otherwise byte-identical must not produce a diff.
        // Helper is reusable across the test project; production callsite normalises
        // before calling, but the helper itself defends the contract too.
        var expected = "alpha\r\nbeta\r\ngamma\r\n";
        var actual = "alpha\nbeta\ngamma\n";

        var result = UnifiedDiffFormatter.Format(expected, actual);

        Assert.That(result, Is.Empty,
            "inputs differing only in line-ending style must report no drift");
    }
}
