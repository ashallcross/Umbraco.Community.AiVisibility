using System.Text;
using System.Text.RegularExpressions;
using Umbraco.Community.AiVisibility.Configuration;
using LlmsTxt.Umbraco.Extraction;
using LlmsTxt.Umbraco.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace LlmsTxt.Umbraco.Tests.Routing;

[TestFixture]
public class MarkdownResponseWriterTests
{
    private static readonly Guid HomeKey = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime HomeUpdated = new(2026, 4, 29, 0, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task WriteAsync_Found_AppendsVary_DoesNotOverwriteUpstreamTokens()
    {
        // B1 regression — writer must append to existing Vary header (e.g. set by
        // ResponseCompression middleware as `Vary: Accept-Encoding`) rather than
        // overwriting it. The append-not-overwrite contract is documented in
        // getting-started.md and shared with the middleware's OnStarting callback.
        var (writer, ctx, _) = NewHarness();
        ctx.Response.Headers["Vary"] = "Accept-Encoding";

        await writer.WriteAsync(BuildFound("# x\n"), "/home", "en-GB", contentSignal: null, ctx);

        Assert.That(ctx.Response.Headers["Vary"].ToString(),
            Is.EqualTo("Accept-Encoding, Accept"),
            "writer must append, not overwrite — upstream Vary tokens survive");
    }

    [Test]
    public async Task WriteAsync_Found_Sets_ContentType_CacheControl_Vary_ETag_XMarkdownTokens()
    {
        var (writer, ctx, _) = NewHarness();

        await writer.WriteAsync(BuildFound("# Home\n"), "/home", "en-GB", contentSignal: null, ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(ctx.Response.ContentType, Is.EqualTo(Constants.HttpHeaders.MarkdownContentType));
        Assert.That(ctx.Response.Headers["Vary"].ToString(), Is.EqualTo("Accept"));
        Assert.That(ctx.Response.Headers["Cache-Control"].ToString(), Is.EqualTo("public, max-age=60"));
        Assert.That(ctx.Response.Headers["ETag"].ToString(), Does.StartWith("\""));
        Assert.That(ctx.Response.Headers.ContainsKey("X-Markdown-Tokens"), Is.True);
    }

    [Test]
    public async Task WriteAsync_Found_WritesBodyToResponseStream()
    {
        var body = new MemoryStream();
        var (writer, ctx, _) = NewHarness(body: body);

        await writer.WriteAsync(BuildFound("# Body content\nLorem.\n"), "/home", "en-GB", contentSignal: null, ctx);

        body.Position = 0;
        var read = await new StreamReader(body, Encoding.UTF8).ReadToEndAsync();
        Assert.That(read, Does.Contain("# Body content"));
        Assert.That(read, Does.Contain("Lorem."));
    }

    [Test]
    public async Task WriteAsync_Found_ETag_QuotedStrong_16Char()
    {
        var (writer, ctx, _) = NewHarness();

        await writer.WriteAsync(BuildFound("# x\n"), "/home", "en-GB", contentSignal: null, ctx);

        var etag = ctx.Response.Headers["ETag"].ToString();
        // Quoted, 16 base64-url chars (no W/ prefix).
        Assert.That(Regex.IsMatch(etag, "^\"[A-Za-z0-9_-]{16}\"$"), Is.True,
            $"Expected quoted 16-char base64-url ETag, got: {etag}");
        Assert.That(etag, Does.Not.StartWith("W/"));
    }

    [Test]
    public async Task WriteAsync_Found_DifferentRoutes_DifferentETag()
    {
        var a = await CaptureETag(canonicalPath: "/home");
        var b = await CaptureETag(canonicalPath: "/about");
        Assert.That(b, Is.Not.EqualTo(a));
    }

    [Test]
    public async Task WriteAsync_Found_DifferentCultures_DifferentETag()
    {
        var en = await CaptureETag(culture: "en-GB");
        var fr = await CaptureETag(culture: "fr-FR");
        Assert.That(fr, Is.Not.EqualTo(en));
    }

    [Test]
    public async Task WriteAsync_Found_DifferentUpdateDate_DifferentETag()
    {
        var t1 = await CaptureETag(updatedUtc: HomeUpdated);
        var t2 = await CaptureETag(updatedUtc: HomeUpdated.AddSeconds(1));
        Assert.That(t2, Is.Not.EqualTo(t1));
    }

    /// <summary>
    /// Story 1.2 [Review][Patch] — ETag culture-casing. `en-GB` and `en-gb` produce
    /// the same cache content but were producing different ETags before the fix.
    /// </summary>
    [Test]
    public async Task WriteAsync_CultureCasing_DoesNotAffectETag()
    {
        var upper = await CaptureETag(culture: "en-GB");
        var lower = await CaptureETag(culture: "en-gb");
        var mixed = await CaptureETag(culture: "EN-gb");
        Assert.That(lower, Is.EqualTo(upper));
        Assert.That(mixed, Is.EqualTo(upper));
    }

    [Test]
    public async Task WriteAsync_NullCulture_SameETag_AsEmptyCulture()
    {
        var nullCult = await CaptureETag(culture: null);
        var emptyCult = await CaptureETag(culture: string.Empty);
        Assert.That(emptyCult, Is.EqualTo(nullCult));
    }

    [Test]
    public async Task WriteAsync_IfNoneMatch_Matches_Returns304_NoBody_NoContentType_HeadersPreserved()
    {
        var etag = await CaptureETag(canonicalPath: "/home", culture: "en-GB");
        var body = new MemoryStream();
        var (writer, ctx, _) = NewHarness(body: body);
        ctx.Request.Headers[Constants.HttpHeaders.IfNoneMatch] = etag;

        await writer.WriteAsync(BuildFound("# x\n"), "/home", "en-GB", contentSignal: null, ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status304NotModified));
        Assert.That(body.Length, Is.EqualTo(0), "304 must have no body");
        Assert.That(ctx.Response.ContentType, Is.Null, "304 must have no Content-Type");
        // RFC 7232 § 4.1 — these MUST be sent on 304 too.
        Assert.That(ctx.Response.Headers["ETag"].ToString(), Is.EqualTo(etag));
        Assert.That(ctx.Response.Headers["Vary"].ToString(), Is.EqualTo("Accept"));
        Assert.That(ctx.Response.Headers["Cache-Control"].ToString(), Does.StartWith("public, max-age="));
        Assert.That(ctx.Response.Headers.ContainsKey("X-Markdown-Tokens"), Is.False);
    }

    [Test]
    public async Task WriteAsync_IfNoneMatch_Bare_Wildcard_Returns304_HeadersPreserved()
    {
        var body = new MemoryStream();
        var (writer, ctx, _) = NewHarness(body: body);
        ctx.Request.Headers[Constants.HttpHeaders.IfNoneMatch] = "*";

        await writer.WriteAsync(BuildFound("# x\n"), "/home", "en-GB", contentSignal: null, ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status304NotModified));
        // RFC 7232 § 4.1 — same headers must accompany 304 as would have on 200.
        Assert.That(body.Length, Is.EqualTo(0), "304 must have no body");
        Assert.That(ctx.Response.ContentType, Is.Null, "304 must have no Content-Type");
        Assert.That(ctx.Response.Headers["ETag"].ToString(), Does.StartWith("\""));
        Assert.That(ctx.Response.Headers["Vary"].ToString(), Is.EqualTo("Accept"));
        Assert.That(ctx.Response.Headers["Cache-Control"].ToString(), Does.StartWith("public, max-age="));
        Assert.That(ctx.Response.Headers.ContainsKey("X-Markdown-Tokens"), Is.False);
    }

    [Test]
    public async Task WriteAsync_TrailingSlashVariants_ProduceSameETag()
    {
        // B3 regression — `.md` controller normalises `/home/.md` to `/home/`,
        // Accept-negotiation middleware passes raw `/home`. Without canonical-path
        // normalisation both ETags differ. With LlmsCanonicalPath.Normalise stripping
        // trailing slash, the two surfaces converge.
        var noSlash = await CaptureETag(canonicalPath: "/home");
        var trailing = await CaptureETag(canonicalPath: "/home/");
        Assert.That(trailing, Is.EqualTo(noSlash),
            "trailing-slash variant must hash to the same ETag — AC1 byte-identity guarantee");
    }

    [Test]
    public async Task WriteAsync_PercentEncodedPath_ProducesSameETag_AsDecoded()
    {
        // B3 regression — `.md` controller URL-decodes via MarkdownPathNormaliser;
        // Accept-negotiation middleware passes whatever ASP.NET Core surfaces. If
        // a path arrives still percent-encoded (`/caf%C3%A9`), the writer's own
        // normalisation must decode it before hashing.
        var encoded = await CaptureETag(canonicalPath: "/caf%C3%A9");
        var decoded = await CaptureETag(canonicalPath: "/café");
        Assert.That(decoded, Is.EqualTo(encoded),
            "percent-encoded path must hash to the same ETag as the decoded form");
    }

    /// <summary>
    /// Story 1.2 [Review][Patch] — `W/*` was being interpreted as "match anything"
    /// because the controller stripped `W/` then compared to `*`. Per RFC 7232 § 3.2
    /// only the BARE `*` token is the wildcard. `W/*` is malformed.
    /// </summary>
    [Test]
    public async Task WriteAsync_IfNoneMatch_WeakWildcard_DoesNotMatch_Returns200()
    {
        var (writer, ctx, _) = NewHarness();
        ctx.Request.Headers[Constants.HttpHeaders.IfNoneMatch] = "W/*";

        await writer.WriteAsync(BuildFound("# x\n"), "/home", "en-GB", contentSignal: null, ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK),
            "W/* is malformed — must NOT match the bare wildcard");
    }

    [Test]
    public async Task WriteAsync_IfNoneMatch_WeakStrongDifference_StillMatches()
    {
        var etag = await CaptureETag(canonicalPath: "/home", culture: "en-GB");
        var weakened = "W/" + etag;
        var (writer, ctx, _) = NewHarness();
        ctx.Request.Headers[Constants.HttpHeaders.IfNoneMatch] = weakened;

        await writer.WriteAsync(BuildFound("# x\n"), "/home", "en-GB", contentSignal: null, ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status304NotModified));
    }

    [Test]
    public async Task WriteAsync_IfNoneMatch_Mismatch_Returns200()
    {
        var (writer, ctx, _) = NewHarness();
        ctx.Request.Headers[Constants.HttpHeaders.IfNoneMatch] = "\"not-the-tag\"";

        await writer.WriteAsync(BuildFound("# x\n"), "/home", "en-GB", contentSignal: null, ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
    }

    [Test]
    public async Task WriteAsync_IfNoneMatch_Malformed_Returns200()
    {
        var (writer, ctx, _) = NewHarness();
        ctx.Request.Headers[Constants.HttpHeaders.IfNoneMatch] = "garbage-no-quotes";

        await writer.WriteAsync(BuildFound("# x\n"), "/home", "en-GB", contentSignal: null, ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
    }

    [Test]
    public async Task WriteAsync_IfNoneMatch_CommaList_AnyMatch_Returns304()
    {
        var etag = await CaptureETag(canonicalPath: "/home", culture: "en-GB");
        var (writer, ctx, _) = NewHarness();
        ctx.Request.Headers[Constants.HttpHeaders.IfNoneMatch] = $"\"old-tag\", {etag}, \"another\"";

        await writer.WriteAsync(BuildFound("# x\n"), "/home", "en-GB", contentSignal: null, ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status304NotModified));
    }

    [Test]
    public async Task WriteAsync_CachePolicySeconds_Zero_StillEmitsCacheControl_MaxAge0()
    {
        var (writer, ctx, _) = NewHarness(settings: new AiVisibilitySettings { CachePolicySeconds = 0 });

        await writer.WriteAsync(BuildFound("# x\n"), "/home", "en-GB", contentSignal: null, ctx);

        Assert.That(ctx.Response.Headers["Cache-Control"].ToString(),
            Is.EqualTo("public, max-age=0"));
    }

    [Test]
    public async Task WriteAsync_CachePolicySeconds_Negative_ClampedToZero()
    {
        var (writer, ctx, _) = NewHarness(settings: new AiVisibilitySettings { CachePolicySeconds = -10 });

        await writer.WriteAsync(BuildFound("# x\n"), "/home", "en-GB", contentSignal: null, ctx);

        Assert.That(ctx.Response.Headers["Cache-Control"].ToString(),
            Is.EqualTo("public, max-age=0"));
    }

    [Test]
    public void WriteAsync_NotFound_Throws()
    {
        var (writer, ctx, _) = NewHarness();
        var error = MarkdownExtractionResult.Failed(
            new InvalidOperationException("boom"), sourceUrl: null, contentKey: HomeKey);

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await writer.WriteAsync(error, "/home", "en-GB", contentSignal: null, ctx));
    }

    [Test]
    public void WriteAsync_FoundButEmptyBody_Throws()
    {
        var (writer, ctx, _) = NewHarness();
        var empty = MarkdownExtractionResult.Found(
            markdown: string.Empty,
            contentKey: HomeKey,
            culture: "en-GB",
            updatedUtc: HomeUpdated,
            sourceUrl: "https://example.test/home");

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await writer.WriteAsync(empty, "/home", "en-GB", contentSignal: null, ctx));
    }

    // ────────────────────────────────────────────────────────────────────────
    // Story 4.1 — Content-Signal header (Cloudflare Markdown-for-Agents)
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task WriteAsync_Found_ContentSignalConfigured_EmitsHeaderOn200()
    {
        var (writer, ctx, _) = NewHarness();

        await writer.WriteAsync(
            BuildFound("# x\n"),
            "/home",
            "en-GB",
            contentSignal: "ai-train=no, search=yes, ai-input=yes",
            ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(ctx.Response.Headers[Constants.HttpHeaders.ContentSignal].ToString(),
            Is.EqualTo("ai-train=no, search=yes, ai-input=yes"));
        // P11 patch — pin AC8's co-emission contract: X-Markdown-Tokens MUST
        // ride the 200 alongside Content-Signal. The 304 test pins token
        // ABSENCE on the not-modified path; this test pins token PRESENCE
        // on the 200 path with Content-Signal configured.
        Assert.That(ctx.Response.Headers.ContainsKey(Constants.HttpHeaders.XMarkdownTokens), Is.True,
            "X-Markdown-Tokens must ride 200 responses alongside Content-Signal — Cloudflare Markdown-for-Agents AC8 co-emission");
    }

    /// <summary>
    /// P2 patch — Content-Signal value containing CR/LF must be suppressed
    /// (header injection guard). Adopter sets a malformed value either via
    /// appsettings or backoffice; the writer rejects rather than passes the
    /// value through to Kestrel (which would throw InvalidOperationException
    /// on header write).
    /// </summary>
    [TestCase("ai-train=no\r\nX-Evil: 1")]
    [TestCase("ai-train=no\nX-Evil: 1")]
    public async Task WriteAsync_Found_ContentSignalContainsCrLf_HeaderNotEmitted(string adversarialValue)
    {
        var (writer, ctx, _) = NewHarness();

        await writer.WriteAsync(
            BuildFound("# x\n"),
            "/home",
            "en-GB",
            contentSignal: adversarialValue,
            ctx);

        Assert.That(ctx.Response.Headers.ContainsKey(Constants.HttpHeaders.ContentSignal), Is.False,
            "Content-Signal containing CR/LF must be rejected to prevent header injection");
    }

    [Test]
    public async Task WriteAsync_NotModified_ContentSignalConfigured_StillEmitted()
    {
        // RFC 7232 § 4.1 — representation-metadata headers that would be on
        // the 200 must also be on the 304. Distinct from X-Markdown-Tokens
        // (encoded-body-derived; suppressed on 304).
        var (writer, ctx, _) = NewHarness();
        // First request to capture a valid ETag.
        await writer.WriteAsync(
            BuildFound("# x\n"),
            "/home",
            "en-GB",
            contentSignal: "ai-train=no",
            ctx);
        var etag = ctx.Response.Headers[Constants.HttpHeaders.ETag].ToString();

        // Second request with If-None-Match → 304.
        var (writer2, ctx2, _) = NewHarness();
        ctx2.Request.Headers[Constants.HttpHeaders.IfNoneMatch] = etag;

        await writer2.WriteAsync(
            BuildFound("# x\n"),
            "/home",
            "en-GB",
            contentSignal: "ai-train=no",
            ctx2);

        Assert.Multiple(() =>
        {
            Assert.That(ctx2.Response.StatusCode, Is.EqualTo(StatusCodes.Status304NotModified));
            Assert.That(ctx2.Response.Headers[Constants.HttpHeaders.ContentSignal].ToString(),
                Is.EqualTo("ai-train=no"),
                "Content-Signal must travel on 304 — RFC 7232 § 4.1 representation-metadata contract");
            Assert.That(ctx2.Response.Headers.ContainsKey(Constants.HttpHeaders.XMarkdownTokens), Is.False,
                "X-Markdown-Tokens is body-derived; MUST NOT appear on 304");
        });
    }

    [Test]
    public async Task WriteAsync_Found_ContentSignalNullOrWhitespace_HeaderNotEmitted()
    {
        var (writer, ctx, _) = NewHarness();

        await writer.WriteAsync(
            BuildFound("# x\n"),
            "/home",
            "en-GB",
            contentSignal: "   ",
            ctx);

        Assert.That(ctx.Response.Headers.ContainsKey(Constants.HttpHeaders.ContentSignal), Is.False,
            "Whitespace-only content-signal value MUST NOT produce a header");
    }

    [Test]
    public async Task WriteAsync_NotModified_ContentSignalNull_HeaderNotEmitted()
    {
        // Sanity: 304 path with no configured signal stays clean — no
        // accidental empty Content-Signal header leaks onto the response.
        var (writer, ctx, _) = NewHarness();
        await writer.WriteAsync(BuildFound("# x\n"), "/home", "en-GB", contentSignal: null, ctx);
        var etag = ctx.Response.Headers[Constants.HttpHeaders.ETag].ToString();

        var (writer2, ctx2, _) = NewHarness();
        ctx2.Request.Headers[Constants.HttpHeaders.IfNoneMatch] = etag;

        await writer2.WriteAsync(BuildFound("# x\n"), "/home", "en-GB", contentSignal: null, ctx2);

        Assert.That(ctx2.Response.StatusCode, Is.EqualTo(StatusCodes.Status304NotModified));
        Assert.That(ctx2.Response.Headers.ContainsKey(Constants.HttpHeaders.ContentSignal), Is.False);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private async Task<string> CaptureETag(
        string canonicalPath = "/home",
        string? culture = "en-GB",
        DateTime? updatedUtc = null)
    {
        var (writer, ctx, _) = NewHarness();
        await writer.WriteAsync(
            BuildFound("# x\n", culture: culture, updatedUtc: updatedUtc),
            canonicalPath,
            culture,
            contentSignal: null,
            ctx);
        return ctx.Response.Headers["ETag"].ToString();
    }

    private static (MarkdownResponseWriter Writer, HttpContext Ctx, AiVisibilitySettings Settings)
        NewHarness(AiVisibilitySettings? settings = null, MemoryStream? body = null)
    {
        var resolvedSettings = settings ?? new AiVisibilitySettings();
        var optionsMonitor = Substitute.For<IOptionsMonitor<AiVisibilitySettings>>();
        optionsMonitor.CurrentValue.Returns(resolvedSettings);

        var ctx = new DefaultHttpContext();
        if (body is not null)
        {
            ctx.Response.Body = body;
        }

        return (new MarkdownResponseWriter(optionsMonitor), ctx, resolvedSettings);
    }

    private static MarkdownExtractionResult BuildFound(
        string body,
        string? culture = "en-GB",
        DateTime? updatedUtc = null)
        => MarkdownExtractionResult.Found(
            markdown: "---\ntitle: Home\nurl: https://example.test/home\nupdated: 2026-04-29T00:00:00Z\n---\n\n" + body,
            contentKey: HomeKey,
            culture: culture ?? string.Empty,
            updatedUtc: updatedUtc ?? HomeUpdated,
            sourceUrl: "https://example.test/home");
}
