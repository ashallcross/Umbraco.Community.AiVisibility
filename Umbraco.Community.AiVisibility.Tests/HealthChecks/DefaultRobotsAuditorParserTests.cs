using Umbraco.Community.AiVisibility.Caching;
using LlmsTxt.Umbraco.Configuration;
using LlmsTxt.Umbraco.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.Cache;

namespace LlmsTxt.Umbraco.Tests.HealthChecks;

/// <summary>
/// Story 4.2 — pinpoints the parser/match semantics in
/// <see cref="DefaultRobotsAuditor.ParseAndMatch"/>. Avoids the HTTP layer
/// entirely; the dispatch path (fetch → parse) is exercised by manual gate
/// Step 5.
/// </summary>
[TestFixture]
public class DefaultRobotsAuditorParserTests
{
    private static DefaultRobotsAuditor BuildAuditor(IReadOnlyList<string> tokens)
    {
        var settings = new LlmsTxtSettings();
        var monitor = Substitute.For<IOptionsMonitor<LlmsTxtSettings>>();
        monitor.CurrentValue.Returns(settings);
        var caches = new AppCaches(
            new ObjectCacheAppCache(),
            Substitute.For<IRequestCache>(),
            new IsolatedCaches(_ => new ObjectCacheAppCache()));
        var httpFactory = Substitute.For<IHttpClientFactory>();

        return new DefaultRobotsAuditor(
            httpFactory,
            caches,
            AiBotList.ForTesting(tokens),
            monitor,
            NullLogger<DefaultRobotsAuditor>.Instance);
    }

    [Test]
    public void ParseAndMatch_HappyPath_FullDisallowOnKnownAgent_ReturnsFinding()
    {
        var auditor = BuildAuditor(new[] { "GPTBot" });
        var body = "User-agent: GPTBot\nDisallow: /\n";

        var findings = auditor.ParseAndMatch(body);

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Bot.Token, Is.EqualTo("GPTBot"));
        Assert.That(findings[0].SuggestedRemoval, Does.Contain("Disallow: /"));
    }

    [Test]
    public void ParseAndMatch_PartialDisallow_NotFlagged()
    {
        var auditor = BuildAuditor(new[] { "GPTBot" });
        var body = "User-agent: GPTBot\nDisallow: /private/\n";

        var findings = auditor.ParseAndMatch(body);

        Assert.That(findings, Is.Empty,
            "partial-path disallow does not trigger a finding — bot can still crawl public content");
    }

    [Test]
    public void ParseAndMatch_EmptyDisallow_NotFlagged()
    {
        var auditor = BuildAuditor(new[] { "GPTBot" });
        var body = "User-agent: GPTBot\nDisallow:\n";

        var findings = auditor.ParseAndMatch(body);

        Assert.That(findings, Is.Empty,
            "empty Disallow is the canonical 'allow everything' directive");
    }

    [Test]
    public void ParseAndMatch_WildcardUserAgent_FlagsAllKnownBots()
    {
        var auditor = BuildAuditor(new[] { "GPTBot", "ClaudeBot" });
        var body = "User-agent: *\nDisallow: /\n";

        var findings = auditor.ParseAndMatch(body);

        Assert.Multiple(() =>
        {
            Assert.That(findings, Has.Count.EqualTo(2));
            Assert.That(findings.Select(f => f.Bot.Token),
                Is.EquivalentTo(new[] { "GPTBot", "ClaudeBot" }));
            Assert.That(findings.All(f => f.MatchedDirective.Contains("User-agent: *")),
                Is.True);
        });
    }

    [Test]
    public void ParseAndMatch_UnknownToken_NotFlagged()
    {
        var auditor = BuildAuditor(new[] { "GPTBot" });
        var body = "User-agent: SomeRandomBot\nDisallow: /\n";

        var findings = auditor.ParseAndMatch(body);

        Assert.That(findings, Is.Empty,
            "unknown User-agent → not in our list → not flagged");
    }

    [Test]
    public void ParseAndMatch_MultipleAgentsInSameBlock_FlagAllKnown()
    {
        // Per RFC 9309: contiguous User-agent lines share a single rule block.
        var auditor = BuildAuditor(new[] { "GPTBot", "ClaudeBot" });
        var body = "User-agent: GPTBot\nUser-agent: ClaudeBot\nDisallow: /\n";

        var findings = auditor.ParseAndMatch(body);

        Assert.That(findings.Select(f => f.Bot.Token),
            Is.EquivalentTo(new[] { "GPTBot", "ClaudeBot" }));
    }

    [Test]
    public void ParseAndMatch_DistinctGroups_OnlyFlagsThoseWithFullDisallow()
    {
        var auditor = BuildAuditor(new[] { "GPTBot", "ClaudeBot" });
        var body = """
                   User-agent: GPTBot
                   Disallow: /

                   User-agent: ClaudeBot
                   Disallow: /private/
                   """;

        var findings = auditor.ParseAndMatch(body);

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Bot.Token, Is.EqualTo("GPTBot"));
    }

    [Test]
    public void ParseAndMatch_MalformedLines_TolerantSkip()
    {
        var auditor = BuildAuditor(new[] { "GPTBot" });
        var body = "this is not a directive\nUser-agent: GPTBot\nrandom text\nDisallow: /\nstill not a directive";

        var findings = auditor.ParseAndMatch(body);

        Assert.That(findings, Has.Count.EqualTo(1),
            "malformed lines are skipped — parser tolerance per § Failure & Edge Cases");
    }

    [Test]
    public void ParseAndMatch_InlineComment_Ignored()
    {
        var auditor = BuildAuditor(new[] { "GPTBot" });
        var body = "User-agent: GPTBot # operator: OpenAI\nDisallow: / # full-site block\n";

        var findings = auditor.ParseAndMatch(body);

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Bot.Token, Is.EqualTo("GPTBot"),
            "inline comments stripped per RFC 9309 § 2.2.1");
    }

    [Test]
    public void ParseAndMatch_EmptyBody_NoFindings()
    {
        var auditor = BuildAuditor(new[] { "GPTBot" });
        Assert.That(auditor.ParseAndMatch(string.Empty), Is.Empty);
    }
}
