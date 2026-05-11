using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Extraction;

namespace Umbraco.Community.AiVisibility.Tests.Extraction;

/// <summary>
/// Story 7.2 AC1 + AC2 + AC11 — unit tests for the loopback URL resolver.
/// Pins the lazy-resolution rule (no construction-time throws), wildcard
/// binding normalisation, HTTP-over-HTTPS preference, configured-override
/// path, malformed-override fail-loud shape, and first-call caching.
/// </summary>
[TestFixture]
public class LoopbackUrlResolverTests
{
    /// <summary>
    /// AC1 Step 1 — when <c>LoopbackBaseUrl</c> is set in config, the
    /// resolver short-circuits the <see cref="IServer"/> walk and uses the
    /// configured URL directly. <see cref="IServer.Features"/> is NOT
    /// consulted.
    /// </summary>
    [Test]
    public void Resolve_ConfigOverride_UsesLoopbackBaseUrl()
    {
        var server = Substitute.For<IServer>();
        var resolver = BuildResolver(server, loopbackBaseUrl: "http://localhost:5005");

        var target = resolver.Resolve();

        Assert.Multiple(() =>
        {
            Assert.That(target.TransportUri, Is.EqualTo(new Uri("http://localhost:5005/")));
            Assert.That(target.CertBypassEligible, Is.True,
                "localhost is a loopback IP — cert bypass eligible (no-op for HTTP, "
                + "would apply if the loopback target spoke HTTPS)");
            // IServer.Features must NOT have been consulted at all.
            _ = server.DidNotReceiveWithAnyArgs().Features;
        });
    }

    /// <summary>
    /// AC1 Step 1 — when <c>LoopbackBaseUrl</c> is malformed, the resolver
    /// throws <see cref="InvalidOperationException"/> at the first
    /// <see cref="ILoopbackUrlResolver.Resolve"/> call (NOT at construction
    /// time per AC1 lazy rule). The diagnostic must name the offending value
    /// AND the config key.
    /// </summary>
    [Test]
    public void Resolve_ConfigOverride_MalformedUrl_Throws()
    {
        var server = Substitute.For<IServer>();
        var resolver = BuildResolver(server, loopbackBaseUrl: "not-a-uri");

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Resolve());

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("not-a-uri"),
                "diagnostic must name the offending value");
            Assert.That(ex.Message, Does.Contain("AiVisibility:RenderStrategy:LoopbackBaseUrl"),
                "diagnostic must name the config key path");
        });
    }

    /// <summary>
    /// AC1 Step 1 — scheme + Authority guard. <c>Uri.TryCreate</c> with
    /// <see cref="UriKind.Absolute"/> accepts non-http(s) schemes (ftp://,
    /// file://, javascript:) and, on macOS/Linux, parses bare leading-slash
    /// paths like "/foo" as <c>file:///foo</c>. None of these are usable
    /// loopback targets. The resolver must reject all of them with the same
    /// actionable diagnostic the genuinely-unparseable case produces.
    /// Mirrors SDN #3's published-URL scheme guard on
    /// <c>LoopbackPageRendererStrategy</c>.
    /// </summary>
    [TestCase("ftp://example.com/", TestName = "ftp scheme rejected")]
    [TestCase("file:///etc/passwd", TestName = "file scheme rejected")]
    [TestCase("javascript:alert(1)", TestName = "javascript scheme rejected")]
    [TestCase("/foo", TestName = "bare-path (parses to file:// on Unix) rejected")]
    [TestCase("http://", TestName = "empty authority rejected")]
    public void Resolve_ConfigOverride_NonHttpScheme_Throws(string configuredOverride)
    {
        var server = Substitute.For<IServer>();
        var resolver = BuildResolver(server, loopbackBaseUrl: configuredOverride);

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Resolve());

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain(configuredOverride),
                "diagnostic must name the offending value");
            Assert.That(ex.Message, Does.Contain("AiVisibility:RenderStrategy:LoopbackBaseUrl"),
                "diagnostic must name the config key path");
            Assert.That(ex.Message, Does.Contain("http(s)"),
                "diagnostic must name the required scheme constraint");
        });
    }

    /// <summary>
    /// CR7.2 patch — <see cref="IServerAddressesFeature.Addresses"/> is a
    /// live collection; concurrent mutation by another hosted service mid-
    /// iteration would otherwise surface as a confusing "Collection was
    /// modified" exception instead of the resolver's actionable diagnostic.
    /// The resolver must snapshot the addresses (via
    /// <see cref="Enumerable.ToArray{TSource}(IEnumerable{TSource})"/>)
    /// before walking them.
    /// </summary>
    /// <remarks>
    /// The fixture below pins the snapshot contract: a collection whose
    /// <see cref="IEnumerable{T}.GetEnumerator"/> throws but whose
    /// <see cref="ICollection{T}.CopyTo"/> succeeds. LINQ's
    /// <c>ToArray&lt;T&gt;</c> for an <see cref="ICollection{T}"/> goes
    /// through <c>CopyTo</c> (size-known fast path) — never the enumerator
    /// — so the resolver succeeds. A pre-patch direct <c>foreach</c> over
    /// the live <c>Addresses</c> would hit <c>GetEnumerator</c> and throw.
    /// </remarks>
    [Test]
    public void Resolve_SnapshotsAddressesBeforeIteration()
    {
        var addresses = new EnumerationHostileCollection("http://localhost:5000");
        var addressesFeature = Substitute.For<IServerAddressesFeature>();
        addressesFeature.Addresses.Returns(addresses);

        var features = Substitute.For<IFeatureCollection>();
        features.Get<IServerAddressesFeature>().Returns(addressesFeature);

        var server = Substitute.For<IServer>();
        server.Features.Returns(features);

        var resolver = BuildResolver(server);

        var target = resolver.Resolve();

        Assert.That(target.TransportUri, Is.EqualTo(new Uri("http://localhost:5000/")),
            "resolver must consume Addresses through ToArray() (CopyTo fast path) — direct enumeration would have thrown");
    }

    /// <summary>
    /// <see cref="ICollection{T}"/> whose enumerator throws but whose
    /// <see cref="ICollection{T}.CopyTo"/> succeeds. Proves the resolver
    /// snapshots via <c>ToArray()</c> (which uses CopyTo for an
    /// <see cref="ICollection{T}"/>) rather than enumerating directly.
    /// </summary>
    private sealed class EnumerationHostileCollection : ICollection<string>
    {
        private readonly List<string> _inner;

        public EnumerationHostileCollection(params string[] items)
        {
            _inner = new List<string>(items);
        }

        public int Count => _inner.Count;
        public bool IsReadOnly => true;
        public void Add(string item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(string item) => _inner.Contains(item);
        public void CopyTo(string[] array, int arrayIndex) => _inner.CopyTo(array, arrayIndex);
        public bool Remove(string item) => throw new NotSupportedException();

        public IEnumerator<string> GetEnumerator() =>
            throw new InvalidOperationException("Collection was modified during iteration — resolver must snapshot first");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// AC1 Step 2 + AC2 — when both HTTP and HTTPS bindings are present in
    /// <see cref="IServerAddressesFeature.Addresses"/>, HTTP wins regardless
    /// of order in the list. Loopback inside the same kernel adds zero
    /// security value over HTTPS, and the HTTP path avoids the cert chain.
    /// </summary>
    [Test]
    public void Resolve_NoConfigOverride_PrefersHttpBinding()
    {
        var server = BuildServerWithAddresses("https://localhost:5001", "http://localhost:5000");
        var resolver = BuildResolver(server);

        var target = resolver.Resolve();

        Assert.That(target.TransportUri, Is.EqualTo(new Uri("http://localhost:5000/")),
            "HTTP wins over HTTPS even when HTTPS appears first in the list");
    }

    /// <summary>
    /// AC1 Step 2 — when only HTTPS bindings are present (HTTPS-only
    /// deployments), the resolver falls back to the first HTTPS binding.
    /// Cert bypass eligibility = true when the host is a loopback IP.
    /// </summary>
    [Test]
    public void Resolve_NoConfigOverride_FallsBackToHttpsWhenOnlyHttpsBound()
    {
        var server = BuildServerWithAddresses("https://localhost:5001");
        var resolver = BuildResolver(server);

        var target = resolver.Resolve();

        Assert.Multiple(() =>
        {
            Assert.That(target.TransportUri, Is.EqualTo(new Uri("https://localhost:5001/")));
            Assert.That(target.CertBypassEligible, Is.True,
                "loopback IP HTTPS — cert bypass eligible");
        });
    }

    /// <summary>
    /// AC1 Step 2 — HTTPS binding to an external host (exotic adopter
    /// topology — e.g. <c>LoopbackBaseUrl</c> is unset and Kestrel happens
    /// to be bound to a public hostname). Cert bypass eligibility = false;
    /// default cert chain runs.
    /// </summary>
    [Test]
    public void Resolve_NoConfigOverride_HttpsExternalHost_CertBypassFalse()
    {
        var server = BuildServerWithAddresses("https://contoso.example:443");
        var resolver = BuildResolver(server);

        var target = resolver.Resolve();

        Assert.Multiple(() =>
        {
            Assert.That(target.TransportUri, Is.EqualTo(new Uri("https://contoso.example/")),
                "external-host HTTPS resolves but is not loopback");
            Assert.That(target.CertBypassEligible, Is.False,
                "external host — default cert validation chain runs (NEVER blanket bypass)");
        });
    }

    /// <summary>
    /// AC2 — wildcard binding normalisation: <c>+</c>, <c>*</c>,
    /// <c>0.0.0.0</c>, <c>[::]</c> rewrite to a loopback host before
    /// <c>Uri.TryCreate</c> (the wildcard tokens are not valid RFC 3986
    /// host characters; <c>Uri.TryCreate</c> rejects them directly).
    /// Parameterised — collapses four AC11-listed normalisation cases into
    /// one test per the AC11 ceiling-discipline note.
    /// </summary>
    [TestCase("http://+:8080", "http://127.0.0.1:8080/")]
    [TestCase("http://*:8080", "http://127.0.0.1:8080/")]
    [TestCase("http://0.0.0.0:8080", "http://127.0.0.1:8080/")]
    [TestCase("http://[::]:8080", "http://[::1]:8080/")]
    [TestCase("https://+:8443", "https://127.0.0.1:8443/")]
    [TestCase("https://[::]:8443", "https://[::1]:8443/")]
    // Wildcard tokens without an explicit ":port" — Kestrel accepts these in
    // ASPNETCORE_URLS (default port 80/443 is implied). The resolver must
    // also accept them so adopters who bind to "http://+" don't surface a
    // confusing "no usable binding" diagnostic.
    [TestCase("http://+", "http://127.0.0.1/")]
    [TestCase("http://*", "http://127.0.0.1/")]
    [TestCase("http://0.0.0.0", "http://127.0.0.1/")]
    [TestCase("http://[::]", "http://[::1]/")]
    // Wildcard token followed by a path (no port). Kestrel accepts this shape
    // for path-base-bound hosts.
    [TestCase("http://+/myapp", "http://127.0.0.1/myapp")]
    [TestCase("http://*/myapp", "http://127.0.0.1/myapp")]
    public void Resolve_NormalisesWildcardBindings(string binding, string expectedTransportUri)
    {
        var server = BuildServerWithAddresses(binding);
        var resolver = BuildResolver(server);

        var target = resolver.Resolve();

        Assert.Multiple(() =>
        {
            Assert.That(target.TransportUri, Is.EqualTo(new Uri(expectedTransportUri)));
            Assert.That(target.CertBypassEligible, Is.True,
                "wildcards normalise to a loopback IP — cert bypass eligible");
        });
    }

    /// <summary>
    /// AC1 Step 3 — when no usable binding resolves (no addresses bound,
    /// or every binding fails normalisation + <c>Uri.TryCreate</c>), the
    /// resolver throws with a diagnostic naming the workaround config key
    /// AND the observed addresses (or "(none)" when empty).
    /// </summary>
    [Test]
    public void Resolve_NoBindings_ThrowsWithDiagnostic()
    {
        var server = BuildServerWithAddresses(); // empty
        var resolver = BuildResolver(server);

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Resolve());

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("AiVisibility:RenderStrategy:LoopbackBaseUrl"),
                "diagnostic must name the workaround config key");
            Assert.That(ex.Message, Does.Contain("(none)"),
                "diagnostic must report the observed bindings — empty case literal");
        });
    }

    /// <summary>
    /// AC1 lazy-resolution rule — <see cref="LoopbackUrlResolver"/>'s
    /// constructor must NOT walk <see cref="IServer.Features"/> or parse
    /// the configured override. Adopters pinned to
    /// <see cref="RenderStrategyMode.Razor"/> never call <c>Resolve()</c>
    /// and therefore must not be bricked at startup by an environment
    /// without a usable binding.
    /// </summary>
    [Test]
    public void Resolve_LazyBehaviour_DoesNotThrowAtConstruction()
    {
        var server = Substitute.For<IServer>();
        // Configure Features to throw if accessed — proves lazy contract:
        // construction must not access Features.
        server.Features.Returns(_ => throw new InvalidOperationException(
            "Features must NOT be read from the resolver's constructor"));

        Assert.DoesNotThrow(() => BuildResolver(server),
            "LoopbackUrlResolver constructor must NOT access IServer.Features");
    }

    /// <summary>
    /// AC1 caching rule — first <c>Resolve()</c> walks
    /// <see cref="IServer.Features"/>; subsequent calls return the cached
    /// result without re-consulting <c>Features</c>.
    /// </summary>
    [Test]
    public void Resolve_CachesFirstSuccessfulResult()
    {
        var server = BuildServerWithAddresses("http://localhost:5000");
        var resolver = BuildResolver(server);

        var first = resolver.Resolve();
        var second = resolver.Resolve();

        Assert.Multiple(() =>
        {
            Assert.That(second, Is.EqualTo(first),
                "second Resolve() returns the cached LoopbackTarget");
            // Features touched exactly once over both Resolve() calls.
            _ = server.Received(1).Features;
        });
    }

    private static LoopbackUrlResolver BuildResolver(
        IServer server,
        string? loopbackBaseUrl = null)
    {
        var settings = new AiVisibilitySettings
        {
            RenderStrategy = new RenderStrategySettings
            {
                LoopbackBaseUrl = loopbackBaseUrl,
            },
        };
        var monitor = Substitute.For<IOptionsMonitor<AiVisibilitySettings>>();
        monitor.CurrentValue.Returns(settings);

        return new LoopbackUrlResolver(
            server,
            monitor,
            NullLogger<LoopbackUrlResolver>.Instance);
    }

    private static IServer BuildServerWithAddresses(params string[] addresses)
    {
        var addressesFeature = Substitute.For<IServerAddressesFeature>();
        addressesFeature.Addresses.Returns(addresses.ToList());

        var features = Substitute.For<IFeatureCollection>();
        features.Get<IServerAddressesFeature>().Returns(addressesFeature);

        var server = Substitute.For<IServer>();
        server.Features.Returns(features);
        return server;
    }
}
