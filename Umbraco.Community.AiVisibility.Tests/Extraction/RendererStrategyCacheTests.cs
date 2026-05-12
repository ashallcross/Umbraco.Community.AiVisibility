using Umbraco.Community.AiVisibility.Extraction;

namespace Umbraco.Community.AiVisibility.Tests.Extraction;

/// <summary>
/// Story 7.3 AC1 + AC4 + AC5 + AC6 — exercises the
/// <see cref="RendererStrategyCache"/>'s <c>ConcurrentDictionary</c>-backed
/// decision store. Coverage targets:
/// <list type="bullet">
/// <item>Default false / round-trip / idempotence of <c>MarkHijacked</c>.</item>
/// <item>The per-<c>(ContentTypeAlias, TemplateAlias)</c> tuple-key
/// invariant: changing either slot produces a distinct cache key (the
/// mixed-template architectural invariant from architecture.md § Auto Mode
/// — Fallback Internals).</item>
/// <item>The empty-template-alias case stores cleanly as a distinct key
/// from any real template alias.</item>
/// </list>
/// Test density follows project-context.md § Testing Rules ceiling rule:
/// the two distinguishing-slot cases are collapsed into a single
/// <see cref="TestCaseSource"/>-parameterised method (one source per AC4
/// invariant).
/// </summary>
[TestFixture]
public class RendererStrategyCacheTests
{
    [Test]
    public void IsHijacked_BeforeMarkHijacked_ReturnsFalse()
    {
        var cache = new RendererStrategyCache();

        Assert.That(cache.IsHijacked("landingPage", "landingPage"), Is.False,
            "fresh cache must report no hijack for any tuple");
    }

    [Test]
    public void MarkHijacked_FirstCall_ReturnsTrue_AndFlipsIsHijacked()
    {
        var cache = new RendererStrategyCache();

        var firstInsert = cache.MarkHijacked("landingPage", "landingPage");

        Assert.Multiple(() =>
        {
            Assert.That(firstInsert, Is.True,
                "first MarkHijacked for a tuple must report the insert so callers can gate exactly-once side effects (one-time warning log)");
            Assert.That(cache.IsHijacked("landingPage", "landingPage"), Is.True,
                "MarkHijacked → IsHijacked round-trip must return true for the same tuple");
        });
    }

    [Test]
    public void MarkHijacked_SecondCall_ReturnsFalse_StateUnchanged()
    {
        var cache = new RendererStrategyCache();

        cache.MarkHijacked("landingPage", "landingPage");

        bool secondInsert = false;
        Assert.DoesNotThrow(() => secondInsert = cache.MarkHijacked("landingPage", "landingPage"),
            "second MarkHijacked with the same tuple must be a silent no-op (ConcurrentDictionary.TryAdd returns false but does not throw)");

        Assert.Multiple(() =>
        {
            Assert.That(secondInsert, Is.False,
                "second MarkHijacked must report already-marked so callers can suppress duplicate side effects");
            Assert.That(cache.IsHijacked("landingPage", "landingPage"), Is.True,
                "second MarkHijacked must not flip the cache state away from hijacked");
        });
    }

    /// <summary>
    /// AC4 — mixed-template invariant: marking
    /// <c>(landingPage, landingPage)</c> as hijacked must NOT cause
    /// <c>(landingPage, landingPagePrint)</c> to read as hijacked. Same
    /// invariant for marking a doctype and probing a sibling doctype with the
    /// same template alias slot — different tuples = different cache keys.
    /// </summary>
    [TestCase("landingPage", "landingPage", "landingPage", "landingPagePrint",
        TestName = "Different template alias on same doctype")]
    [TestCase("landingPage", "landingPage", "articlePage", "landingPage",
        TestName = "Different content type on same template alias slot")]
    public void IsHijacked_DifferentTupleSlot_ReportsNotHijacked(
        string markedDoctype,
        string markedTemplate,
        string probedDoctype,
        string probedTemplate)
    {
        var cache = new RendererStrategyCache();

        cache.MarkHijacked(markedDoctype, markedTemplate);

        Assert.That(cache.IsHijacked(probedDoctype, probedTemplate), Is.False,
            $"({probedDoctype}, {probedTemplate}) must NOT inherit the hijack mark from ({markedDoctype}, {markedTemplate}) — "
            + "cache key is the FULL tuple; changing either slot produces a distinct key (mixed-template invariant)");
    }

    /// <summary>
    /// AC5 — the empty-template-alias slot (used when
    /// <c>IPublishedContent.TemplateId</c> is null/zero or
    /// <c>ITemplateService.GetAsync</c> returns null) stores cleanly. Both
    /// <c>IsHijacked</c> and <c>MarkHijacked</c> treat empty-string as just
    /// another valid string value at the dictionary level.
    /// </summary>
    [Test]
    public void IsHijacked_EmptyTemplateAlias_TreatsAsValidKey()
    {
        var cache = new RendererStrategyCache();

        cache.MarkHijacked("landingPage", string.Empty);

        Assert.Multiple(() =>
        {
            Assert.That(cache.IsHijacked("landingPage", string.Empty), Is.True,
                "empty-template-alias slot must round-trip cleanly");
            Assert.That(cache.IsHijacked("landingPage", "landingPage"), Is.False,
                "empty-template-alias entry must NOT collide with a same-doctype real-template-alias probe");
        });
    }

    /// <summary>
    /// MarkRazorPermanentlyFailed round-trip + idempotence: same contract
    /// shape as MarkHijacked, but the cached decision drives the Auto strategy
    /// to skip BOTH Razor and Loopback rather than fall back to Loopback.
    /// </summary>
    [Test]
    public void MarkRazorPermanentlyFailed_FirstCall_ReturnsTrue_AndFlipsIsRazorPermanentlyFailed()
    {
        var cache = new RendererStrategyCache();

        var firstInsert = cache.MarkRazorPermanentlyFailed("monthContainer", "monthContainer");

        Assert.Multiple(() =>
        {
            Assert.That(firstInsert, Is.True,
                "first MarkRazorPermanentlyFailed for a tuple must report the insert so callers can gate the one-time warning log");
            Assert.That(cache.IsRazorPermanentlyFailed("monthContainer", "monthContainer"), Is.True,
                "MarkRazorPermanentlyFailed → IsRazorPermanentlyFailed round-trip must return true for the same tuple");
            Assert.That(cache.IsHijacked("monthContainer", "monthContainer"), Is.False,
                "permanently-failed tuple must NOT also report as hijacked — the two decisions are mutually exclusive");
        });
    }

    /// <summary>
    /// First-decision-wins invariant: a tuple cached as hijacked must NOT be
    /// re-cached as permanently-failed (and vice versa). The
    /// <c>ConcurrentDictionary.TryAdd</c> semantics enforce this — once a
    /// tuple has a cached decision, subsequent mark calls of either kind
    /// return false and leave the original decision intact.
    /// </summary>
    [Test]
    public void MarkRazorPermanentlyFailed_AfterMarkHijacked_StateUnchanged()
    {
        var cache = new RendererStrategyCache();

        cache.MarkHijacked("landingPage", "landingPage");
        var secondInsert = cache.MarkRazorPermanentlyFailed("landingPage", "landingPage");

        Assert.Multiple(() =>
        {
            Assert.That(secondInsert, Is.False,
                "MarkRazorPermanentlyFailed must report already-marked when a competing decision (Hijacked) is already cached for the same tuple");
            Assert.That(cache.IsHijacked("landingPage", "landingPage"), Is.True,
                "the original Hijacked decision must survive a competing MarkRazorPermanentlyFailed call");
            Assert.That(cache.IsRazorPermanentlyFailed("landingPage", "landingPage"), Is.False,
                "competing MarkRazorPermanentlyFailed must NOT flip the cached decision away from hijacked");
        });
    }
}
