using Umbraco.Community.AiVisibility.Robots;
using Umbraco.Community.AiVisibility.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace Umbraco.Community.AiVisibility.Tests.Persistence;

[TestFixture]
public class DefaultUserAgentClassifierTests
{
    private DefaultUserAgentClassifier _classifier = null!;

    [SetUp]
    public void Setup()
    {
        // Real AiBotList — embedded list is reachable at test time per
        // AiBotListTests.Load_RealEmbeddedResource_HasKnownTokens. The
        // classifier's contract depends on the curated map produced by
        // BuildCuratedMap (Story 4.2), so testing against real data is
        // higher-confidence than synthetic stubs.
        var aiBotList = new AiBotList(NullLogger<AiBotList>.Instance);
        _classifier = new DefaultUserAgentClassifier(aiBotList);
    }

    [Test]
    public void Classify_GptBot_ReturnsAiTraining()
    {
        Assert.That(
            _classifier.Classify("Mozilla/5.0 (compatible; GPTBot/1.2; +https://openai.com/gptbot)"),
            Is.EqualTo(UserAgentClass.AiTraining));
    }

    [Test]
    public void Classify_OaiSearchBot_ReturnsAiSearchRetrieval()
    {
        Assert.That(
            _classifier.Classify("OAI-SearchBot/1.0"),
            Is.EqualTo(UserAgentClass.AiSearchRetrieval));
    }

    [Test]
    public void Classify_ChatGptUser_ReturnsAiUserTriggered()
    {
        Assert.That(
            _classifier.Classify("ChatGPT-User/1.0"),
            Is.EqualTo(UserAgentClass.AiUserTriggered));
    }

    [Test]
    public void Classify_AnthropicAi_DeprecatedFlagOverridesCategory()
    {
        // anthropic-ai is curated under BotCategory.Training but flagged
        // IsDeprecated → MapBotCategory returns AiDeprecated regardless.
        Assert.That(
            _classifier.Classify("anthropic-ai/0.0.1"),
            Is.EqualTo(UserAgentClass.AiDeprecated));
    }

    [Test]
    public void Classify_GoogleExtended_OptOutMapsToAiTraining()
    {
        // BotCategory.OptOut → UserAgentClass.AiTraining per spec Task 1.3.
        Assert.That(
            _classifier.Classify("Google-Extended/1.0"),
            Is.EqualTo(UserAgentClass.AiTraining));
    }

    [Test]
    public void Classify_DesktopChrome_ReturnsHumanBrowser()
    {
        Assert.That(
            _classifier.Classify(
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15"),
            Is.EqualTo(UserAgentClass.HumanBrowser));
    }

    [Test]
    public void Classify_Googlebot_ReturnsCrawlerOther()
    {
        // Googlebot is a non-AI crawler (search-index, NOT AI training).
        // The substring "Mozilla" appears in Googlebot's UA so the AI/browser
        // ordering must NOT prefer browser; the crawler list runs before the
        // browser list per Classify's documented match priority.
        Assert.That(
            _classifier.Classify(
                "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)"),
            Is.EqualTo(UserAgentClass.CrawlerOther));
    }

    [Test]
    public void Classify_EmptyString_ReturnsUnknown()
        => Assert.That(_classifier.Classify(string.Empty), Is.EqualTo(UserAgentClass.Unknown));

    [Test]
    public void Classify_Null_ReturnsUnknown()
        => Assert.That(_classifier.Classify(null), Is.EqualTo(UserAgentClass.Unknown));

    [Test]
    public void Classify_RandomString_ReturnsUnknown()
        => Assert.That(
            _classifier.Classify("curl/8.0.1"),
            Is.EqualTo(UserAgentClass.Unknown));

    [Test]
    public void Classify_BotCategoryUnknown_FallsThroughToCuratedList()
    {
        // Task 1.3 line 45 — `BotCategory.Unknown` entries from AiBotList
        // (upstream tokens not in the curated map) must NOT be bucketed
        // as a default AI category. They fall through to the curated
        // crawler/browser substring lists.
        //
        // Two assertions inside one fixture:
        //   1. A token whose category is Unknown does NOT win the AI loop
        //      when paired with a UA carrying a curated browser tell.
        //   2. The same token alone (no browser/crawler tell) → Unknown.
        var aiBotList = AiBotList.ForTesting(new[] { "BrandNewBot" });
        var classifier = new DefaultUserAgentClassifier(aiBotList);

        Assert.Multiple(() =>
        {
            // UA contains the uncategorised AI token AND the Mozilla browser
            // tell. The AI loop must skip the BrandNewBot entry (category
            // Unknown) so the curated browser list wins.
            Assert.That(
                classifier.Classify("Mozilla/5.0 (compatible; BrandNewBot/1.0)"),
                Is.EqualTo(UserAgentClass.HumanBrowser));
            // UA contains only the uncategorised token — falls through to
            // every curated list and lands on Unknown.
            Assert.That(
                classifier.Classify("BrandNewBot/1.0"),
                Is.EqualTo(UserAgentClass.Unknown));
        });
    }
}
