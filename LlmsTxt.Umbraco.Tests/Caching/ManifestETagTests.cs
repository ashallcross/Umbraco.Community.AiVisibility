using LlmsTxt.Umbraco.Caching;

namespace LlmsTxt.Umbraco.Tests.Caching;

[TestFixture]
public class ManifestETagTests
{
    [Test]
    public void Compute_KnownInput_ReturnsExpectedShape()
    {
        // Strong validator shape: quoted, 16 chars between quotes (12 bytes
        // base64-url-encoded). Underlying SHA-256 is deterministic so the value
        // for a fixed input is reproducible across runs.
        var etag = ManifestETag.Compute("# Acme\n> \n");

        Assert.Multiple(() =>
        {
            Assert.That(etag, Does.StartWith("\""));
            Assert.That(etag, Does.EndWith("\""));
            // 16 chars body + 2 quotes = 18 total.
            Assert.That(etag.Length, Is.EqualTo(18));
            // Determinism: re-computing yields the same value.
            Assert.That(ManifestETag.Compute("# Acme\n> \n"), Is.EqualTo(etag));
        });
    }

    [Test]
    public void Compute_Empty_ReturnsHashOfZeroBytes()
    {
        // Empty body is the Story 2.2 "scope rejects everything → 200 + empty
        // body" path. SHA-256("") is well-defined and the resulting ETag is a
        // stable non-null quoted strong validator.
        var etag = ManifestETag.Compute(string.Empty);

        Assert.Multiple(() =>
        {
            Assert.That(etag, Is.Not.Null);
            Assert.That(etag, Is.Not.Empty);
            Assert.That(etag.Length, Is.EqualTo(18));
        });
    }

    [Test]
    public void Compute_DifferentBodies_ReturnDifferentETags()
    {
        var a = ManifestETag.Compute("body-a");
        var b = ManifestETag.Compute("body-b");

        Assert.That(a, Is.Not.EqualTo(b));
    }

    [Test]
    public void Compute_BodyByteEqual_ReturnsByteEqualETag()
    {
        const string body = "manifest body content with some realistic shape\n## Section\n- [Page](/page.md)";
        var first = ManifestETag.Compute(body);
        var second = ManifestETag.Compute(body);

        Assert.That(first, Is.EqualTo(second), "deterministic — same input → same ETag");
    }

    [Test]
    public void Compute_NullBody_Throws()
    {
        Assert.That(() => ManifestETag.Compute(null!), Throws.ArgumentNullException);
    }

    [Test]
    public void Compute_HreflangEnabledOutput_DiffersFromHreflangDisabledOutput()
    {
        // AC6 closing clause: hreflang flips the body, so the ETag must flip too.
        // (Pin via the natural property — different inputs produce different
        // outputs — without simulating the controller path.)
        var disabled = ManifestETag.Compute("- [About](/about.md)\n");
        var enabled = ManifestETag.Compute("- [About](/about.md) (fr-fr: /fr/about.md)\n");

        Assert.That(enabled, Is.Not.EqualTo(disabled));
    }
}
