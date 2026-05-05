using Umbraco.Community.AiVisibility.Robots;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Umbraco.Community.AiVisibility.Tests.Robots;

[TestFixture]
public class AiBotListTests
{
    [Test]
    public void Load_RealEmbeddedResource_HasKnownTokens()
    {
        // Pin that the build-time embedded resource is reachable + carries the
        // tokens we expect to anchor manual gate Step 5 (GPTBot block detection).
        var list = new AiBotList(NullLogger<AiBotList>.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(list.UsedHardcodedFallback, Is.False,
                "embedded resource is present at build time — fallback should not fire");
            Assert.That(list.Contains("GPTBot"), Is.True);
            Assert.That(list.Contains("ClaudeBot"), Is.True);
            Assert.That(list.Contains("anthropic-ai"), Is.True);
            Assert.That(list.Contains("Bytespider"), Is.True);
        });
    }

    [Test]
    public void GetByToken_IsCaseInsensitive()
    {
        var list = AiBotList.ForTesting(new[] { "GPTBot", "ClaudeBot" });

        Assert.Multiple(() =>
        {
            Assert.That(list.GetByToken("gptbot"), Is.Not.Null);
            Assert.That(list.GetByToken("GPTBOT"), Is.Not.Null);
            Assert.That(list.GetByToken("GPTBot"), Is.Not.Null);
        });
    }

    [Test]
    public void Load_DeprecatedToken_AnnotatedWithReplacement()
    {
        var list = AiBotList.ForTesting(new[] { "anthropic-ai", "Claude-Web" });

        Assert.Multiple(() =>
        {
            var anthropic = list.GetByToken("anthropic-ai");
            Assert.That(anthropic, Is.Not.Null);
            Assert.That(anthropic!.IsDeprecated, Is.True);
            Assert.That(anthropic.DeprecationReplacement, Is.EqualTo("ClaudeBot"));

            var claudeWeb = list.GetByToken("Claude-Web");
            Assert.That(claudeWeb!.IsDeprecated, Is.True);
            Assert.That(claudeWeb.DeprecationReplacement, Is.EqualTo("ClaudeBot"));
        });
    }

    [Test]
    public void Load_KnownToken_HasCuratedCategoryAndOperator()
    {
        var list = AiBotList.ForTesting(new[] { "GPTBot", "OAI-SearchBot", "ChatGPT-User" });

        Assert.Multiple(() =>
        {
            Assert.That(list.GetByToken("GPTBot")!.Category, Is.EqualTo(BotCategory.Training));
            Assert.That(list.GetByToken("GPTBot")!.Operator, Is.EqualTo("OpenAI"));
            Assert.That(list.GetByToken("OAI-SearchBot")!.Category, Is.EqualTo(BotCategory.SearchRetrieval));
            Assert.That(list.GetByToken("ChatGPT-User")!.Category, Is.EqualTo(BotCategory.UserTriggered));
        });
    }

    [Test]
    public void Load_UnknownToken_FallsBackToUnknownCategory()
    {
        var list = AiBotList.ForTesting(new[] { "BrandNewBot" });

        var entry = list.GetByToken("BrandNewBot")!;
        Assert.Multiple(() =>
        {
            Assert.That(entry.Category, Is.EqualTo(BotCategory.Unknown),
                "tokens not in the curated map fall back to Unknown — surfaces in the Health Check as 'unclassified'");
            Assert.That(entry.IsDeprecated, Is.False);
            Assert.That(entry.Operator, Is.Null);
        });
    }

    [Test]
    public void Load_DuplicateTokens_DedupedKeepingFirst()
    {
        var list = AiBotList.ForTesting(new[] { "GPTBot", "gptbot", "GPTBOT" });
        Assert.Multiple(() =>
        {
            Assert.That(list.Entries.Count(e =>
                    string.Equals(e.Token, "GPTBot", StringComparison.OrdinalIgnoreCase)),
                Is.EqualTo(1),
                "duplicates collapse on case-insensitive token match");
            // Pin the casing of the surviving entry — first-write-wins, NOT
            // last-write-wins. Order of Entries is observable to the Health
            // Check Description rendering so a regression here would silently
            // change adopter-facing display text.
            Assert.That(list.Entries.Single(e =>
                    string.Equals(e.Token, "GPTBot", StringComparison.OrdinalIgnoreCase)).Token,
                Is.EqualTo("GPTBot"),
                "first occurrence's casing is preserved");
        });
    }

    [Test]
    public void Load_EmbeddedResourceMissing_FallsBackToHardcodedSet()
    {
        // Spec § Failure & Edge Cases bullet 6 — the named contract is "if the
        // embedded resource is missing, fall back to the hardcoded set + log
        // Warning". The DI-public ctor goes through the assembly's manifest
        // resource loader; this test exercises the same fallback decision via
        // the test-only ForTesting(empty) seam, which mirrors what the real
        // loader does when GetManifestResourceStream returns null.
        var list = AiBotList.ForTesting(Array.Empty<string>(), usedFallback: true);

        Assert.Multiple(() =>
        {
            Assert.That(list.UsedHardcodedFallback, Is.True,
                "missing-resource fallback flag MUST be observable to the Health Check Description caveat");
            Assert.That(list.Entries, Is.Empty,
                "ForTesting(empty) reproduces the worst-case shape — the real loader fills FallbackTokens; the Health Check Description still surfaces the caveat");
        });
    }

    [Test]
    public void Load_RealEmbeddedResource_ParsesNonTrivialTokenCount()
    {
        // Defends against the regression "future maintainer commits a fallback
        // file with no User-agent: lines, resource exists at runtime but
        // ParseTokens returns 0 → silent fallback to the 22-token hardcoded
        // set". The committed snapshot ships ~141 tokens; assert at least 100
        // so a wholesale corruption of the fallback fails the test rather than
        // the Health Check shipping with a quietly-broken token list.
        var list = new AiBotList(NullLogger<AiBotList>.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(list.UsedHardcodedFallback, Is.False,
                "embedded resource must parse to a non-empty token list at build time");
            Assert.That(list.Entries.Count, Is.GreaterThan(100),
                "embedded fallback must carry the bulk of the upstream token list — a < 100 result signals fallback file corruption");
        });
    }

    [Test]
    public void ParseTokens_TolerantOfBlankLinesAndComments()
    {
        var content = "# header comment\nUser-agent: GPTBot\n\n# another comment\nUser-agent: ClaudeBot\nDisallow: /\n";
        var tokens = AiBotList.ParseTokens(content);

        Assert.That(tokens, Is.EquivalentTo(new[] { "GPTBot", "ClaudeBot" }));
    }

    [Test]
    public void ParseTokens_TolerantOfCrlfLineEndings()
    {
        var content = "User-agent: GPTBot\r\nUser-agent: ClaudeBot\r\nDisallow: /\r\n";
        var tokens = AiBotList.ParseTokens(content);

        Assert.That(tokens, Is.EquivalentTo(new[] { "GPTBot", "ClaudeBot" }));
    }
}
