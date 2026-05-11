using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Community.AiVisibility.Configuration;

namespace Umbraco.Community.AiVisibility.Extraction;

/// <summary>
/// Story 7.2 — default <see cref="ILoopbackUrlResolver"/> implementation.
/// Resolution algorithm runs lazily on the first <see cref="Resolve"/> call:
/// (1) configured <c>AiVisibility:RenderStrategy:LoopbackBaseUrl</c> override
/// when present, else (2) <c>IServer.Features.Get&lt;IServerAddressesFeature&gt;()</c>
/// with wildcard-binding normalisation, else (3) throw with a diagnostic
/// listing the bound addresses + the configured-override workaround.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime:</b> Singleton. Result is cached after the first successful
/// resolution; <c>IServer.Features</c> is stable post-startup so re-walking
/// on every render adds cost without value. Concurrent first-call races are
/// race-tolerant — two threads both walking the bindings produce the same
/// answer; last write wins, no correctness impact.
/// </para>
/// <para>
/// <b>Restart-required for <c>LoopbackBaseUrl</c> changes.</b> The cache
/// holds the first-call result for the process lifetime. Changes to
/// <c>AiVisibility:RenderStrategy:LoopbackBaseUrl</c> via
/// <see cref="IOptionsMonitor{TOptions}"/> hot-reload are intentionally NOT
/// propagated to the cached target — the resolver does not subscribe to
/// <c>OnChange</c>. Two reasons: (1) the alternative resolution path
/// (<c>IServer.Features</c>) is itself fundamentally not reloadable
/// (Kestrel binds at startup), so hot-reload would be asymmetric across
/// the two branches; (2) <c>LoopbackBaseUrl</c> is a deployment-shape
/// setting tied to the Kestrel binding, not a runtime tuning knob. Operators
/// who change it must restart the host. The configured-override parse
/// failure (Step 1) DOES re-run on every call because it throws and never
/// caches — adopters who misconfigure on first deploy can fix and reload
/// without restart; only the successful-cache case is sticky.
/// </para>
/// <para>
/// <b>Wildcard normalisation:</b> Kestrel binding strings like
/// <c>http://+:8080</c>, <c>http://*:8080</c>, <c>http://0.0.0.0:8080</c>
/// and <c>http://[::]:8080</c> are common — particularly in containers — but
/// not directly usable as loopback target hosts (the wildcard tokens are not
/// valid RFC 3986 host characters; <c>Uri.TryCreate</c> rejects them). The
/// resolver string-substitutes the wildcard token to a loopback address
/// (<c>127.0.0.1</c> for IPv4, <c>[::1]</c> for IPv6) BEFORE constructing
/// the <see cref="Uri"/>. The substitution accepts the wildcard token
/// followed by <c>:port</c>, <c>/path</c>, or end-of-string — Kestrel
/// accepts all three shapes in <c>ASPNETCORE_URLS</c>.
/// </para>
/// <para>
/// <b>Sub-path / path-base hosting not supported.</b> The loopback strategy
/// constructs the internal request URI by replacing the transport target's
/// path with the published-URL path; a path-base prefix on the transport
/// target would be silently dropped. The resolver rejects both shapes:
/// <c>LoopbackBaseUrl</c> values containing a non-root path throw with an
/// actionable diagnostic; <c>IServer.Features</c> bindings with a path-base
/// (e.g. <c>http://+:8080/myapp</c>) are skipped at <c>Debug</c> log level
/// alongside the other "unusable binding" skip paths. Adopters mounting
/// Umbraco at a non-root path base need a follow-up that propagates the
/// path-base through the request construction; tracked as deferred work.
/// </para>
/// <para>
/// <b>Scheme preference:</b> when both HTTP and HTTPS bindings are present,
/// the resolver prefers the HTTP binding. Loopback inside the same kernel
/// adds zero security value over HTTPS; the HTTP path also avoids the cert
/// chain entirely on the loopback transport.
/// </para>
/// </remarks>
internal sealed class LoopbackUrlResolver : ILoopbackUrlResolver
{
    private readonly IServer _server;
    private readonly IOptionsMonitor<AiVisibilitySettings> _settings;
    private readonly ILogger<LoopbackUrlResolver> _logger;

    // Race-tolerant single-write cache. Volatile reads/writes prevent torn
    // observations on concurrent first-call races; resolution is idempotent
    // (IServer.Features is stable post-startup) so last write wins is safe.
    // Wrapped in a reference-type holder because Volatile.Read/Write require
    // a reference type and LoopbackTarget is a record struct.
    private sealed record TargetHolder(LoopbackTarget Target);
    private TargetHolder? _cachedTarget;

    public LoopbackUrlResolver(
        IServer server,
        IOptionsMonitor<AiVisibilitySettings> settings,
        ILogger<LoopbackUrlResolver> logger)
    {
        _server = server;
        _settings = settings;
        _logger = logger;
    }

    public LoopbackTarget Resolve()
    {
        var cached = Volatile.Read(ref _cachedTarget);
        if (cached is not null)
        {
            return cached.Target;
        }

        // Step 1 — configured override wins.
        var configuredOverride = _settings.CurrentValue.RenderStrategy.LoopbackBaseUrl;
        if (!string.IsNullOrWhiteSpace(configuredOverride))
        {
            // Scheme + Authority guard: Uri.TryCreate(UriKind.Absolute) accepts
            // ftp://, file://, javascript:, etc. — and on macOS/Linux a bare
            // leading-slash path like "/foo" parses as file:///foo. None of
            // these are usable HTTP loopback targets. Mirrors the same guard
            // applied to publishedUri in LoopbackPageRendererStrategy.
            if (!Uri.TryCreate(configuredOverride, UriKind.Absolute, out var overrideUri)
                || (overrideUri.Scheme != Uri.UriSchemeHttp && overrideUri.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrEmpty(overrideUri.Authority))
            {
                throw new InvalidOperationException(
                    $"AiVisibility:RenderStrategy:LoopbackBaseUrl is set to '{configuredOverride}' which is not a valid absolute http(s) URL with a non-empty host. Set the value to an absolute http(s) URL pointing at the local Kestrel binding (e.g. \"http://127.0.0.1:5000\").");
            }

            // Sub-path / path-base hosting not supported in this release.
            // The strategy builds the loopback URI by REPLACING the transport
            // target's path with the published-URL path — a path-base prefix
            // on the override (e.g. "http://127.0.0.1:5000/myapp") would be
            // silently dropped, and the internal request would land on the
            // wrong route. Fail loud with an actionable diagnostic. Real
            // path-base support is a separate routing concern; track as
            // deferred work if an adopter needs it.
            if (!HasRootPath(overrideUri))
            {
                throw new InvalidOperationException(
                    $"AiVisibility:RenderStrategy:LoopbackBaseUrl is set to '{configuredOverride}' which includes a path component ('{overrideUri.AbsolutePath}'). Sub-path / path-base hosting is not supported by the loopback strategy in this release — the path would be silently dropped when the loopback request is constructed. Set the value to scheme + host + optional port only (e.g. \"http://127.0.0.1:5000\" rather than \"http://127.0.0.1:5000/myapp\").");
            }

            var overrideTarget = new LoopbackTarget(overrideUri, IsLoopbackHost(overrideUri.Host));
            Volatile.Write(ref _cachedTarget, new TargetHolder(overrideTarget));
            return overrideTarget;
        }

        // Step 2 — IServerAddressesFeature. Snapshot to array before iterating:
        // the feature's Addresses collection is live, and a concurrent mutation
        // by another hosted service would otherwise surface as a confusing
        // "Collection was modified" exception instead of the resolver's
        // actionable diagnostic.
        var addressesFeature = _server.Features.Get<IServerAddressesFeature>();
        var snapshot = addressesFeature?.Addresses?.ToArray() ?? Array.Empty<string>();
        if (snapshot.Length > 0)
        {
            // Single-pass collect: track the first usable HTTP and the first
            // usable HTTPS binding. Decide at the end so HTTP wins regardless
            // of order in the list.
            Uri? firstHttp = null;
            Uri? firstHttps = null;

            foreach (var binding in snapshot)
            {
                if (string.IsNullOrWhiteSpace(binding))
                {
                    continue;
                }

                if (!TryNormaliseBinding(binding, out var uri))
                {
                    _logger.LogDebug(
                        "PageRenderer: Loopback resolver skipped unparseable binding '{Binding}'",
                        binding);
                    continue;
                }

                // Sub-path / path-base bindings (e.g. "http://+:8080/myapp")
                // are skipped — same rationale as the LoopbackBaseUrl path-base
                // rejection above. The strategy would silently drop the path
                // prefix when constructing the loopback URI, sending the
                // internal request to the wrong route. If all bindings are
                // path-base, the resolver falls through to the "no usable
                // binding" diagnostic which already prompts the adopter to
                // set LoopbackBaseUrl explicitly.
                if (!HasRootPath(uri))
                {
                    _logger.LogDebug(
                        "PageRenderer: Loopback resolver skipped path-base binding '{Binding}' (path-base hosting is not supported in this release; set AiVisibility:RenderStrategy:LoopbackBaseUrl explicitly if needed)",
                        binding);
                    continue;
                }

                if (uri.Scheme == Uri.UriSchemeHttp && firstHttp is null)
                {
                    firstHttp = uri;
                }
                else if (uri.Scheme == Uri.UriSchemeHttps && firstHttps is null)
                {
                    firstHttps = uri;
                }
            }

            var chosen = firstHttp ?? firstHttps;
            if (chosen is not null)
            {
                var target = new LoopbackTarget(chosen, IsLoopbackHost(chosen.Host));
                Volatile.Write(ref _cachedTarget, new TargetHolder(target));
                return target;
            }
        }

        // Step 3 — no usable binding. Fail-loud at the call site.
        var observed = snapshot.Length > 0
            ? string.Join(", ", snapshot)
            : "(none)";
        _logger.LogError(
            "PageRenderer: Loopback URL resolution failed; no usable HTTP/HTTPS binding. Observed: {Addresses}. Workaround: set AiVisibility:RenderStrategy:LoopbackBaseUrl in appsettings.json.",
            observed);
        throw new InvalidOperationException(
            $"PageRenderer: Loopback URL resolution failed; no usable HTTP/HTTPS binding. Observed bindings: {observed}. Set AiVisibility:RenderStrategy:LoopbackBaseUrl in appsettings.json to point at the local Kestrel binding (e.g. \"http://127.0.0.1:5000\"), or pin AiVisibility:RenderStrategy:Mode=Razor to skip the loopback strategy.");
    }

    /// <summary>
    /// Substitutes wildcard host tokens (<c>+</c>, <c>*</c>, <c>0.0.0.0</c>,
    /// <c>[::]</c>) with concrete loopback addresses before constructing a
    /// <see cref="Uri"/>. Accepts the wildcard token followed by <c>:port</c>,
    /// <c>/path</c>, or end-of-string (Kestrel <c>ASPNETCORE_URLS</c> accepts
    /// all three shapes). Returns <c>false</c> when the binding cannot be
    /// parsed even after substitution; the caller skips and continues.
    /// </summary>
    internal static bool TryNormaliseBinding(string binding, out Uri uri)
    {
        // Order matters: check IPv6 wildcard "[::]" before the bare "*" — the
        // IPv6 form contains characters that could otherwise be misclassified
        // by the "*" replacement.
        string normalised = binding;
        _ = TryReplaceWildcard(binding, "http://", "[::]", "[::1]", out normalised)
            || TryReplaceWildcard(binding, "https://", "[::]", "[::1]", out normalised)
            || TryReplaceWildcard(binding, "http://", "+", "127.0.0.1", out normalised)
            || TryReplaceWildcard(binding, "https://", "+", "127.0.0.1", out normalised)
            || TryReplaceWildcard(binding, "http://", "*", "127.0.0.1", out normalised)
            || TryReplaceWildcard(binding, "https://", "*", "127.0.0.1", out normalised)
            || TryReplaceWildcard(binding, "http://", "0.0.0.0", "127.0.0.1", out normalised)
            || TryReplaceWildcard(binding, "https://", "0.0.0.0", "127.0.0.1", out normalised);

        return Uri.TryCreate(normalised, UriKind.Absolute, out uri!);
    }

    /// <summary>
    /// Replaces <paramref name="wildcardHost"/> with <paramref name="replacementHost"/>
    /// in <paramref name="binding"/> when the binding starts with
    /// <paramref name="scheme"/>+<paramref name="wildcardHost"/> followed by
    /// either <c>:</c>, <c>/</c>, or end-of-string. On no match the original
    /// binding is returned unchanged via <paramref name="normalised"/>.
    /// </summary>
    private static bool TryReplaceWildcard(
        string binding,
        string scheme,
        string wildcardHost,
        string replacementHost,
        out string normalised)
    {
        if (!binding.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            normalised = binding;
            return false;
        }

        var afterScheme = binding[scheme.Length..];
        if (!afterScheme.StartsWith(wildcardHost, StringComparison.OrdinalIgnoreCase))
        {
            normalised = binding;
            return false;
        }

        var afterHost = afterScheme[wildcardHost.Length..];
        if (afterHost.Length == 0 || afterHost[0] == ':' || afterHost[0] == '/')
        {
            normalised = scheme + replacementHost + afterHost;
            return true;
        }

        normalised = binding;
        return false;
    }

    /// <summary>
    /// Returns <c>true</c> when the URI's path is the root (<c>/</c>) — i.e.
    /// the URI has no path-base / sub-path prefix that the loopback strategy
    /// would silently drop when constructing the internal request. .NET's
    /// <see cref="Uri"/> normalises an empty path to <c>/</c>, so a single
    /// equality check covers both forms.
    /// </summary>
    private static bool HasRootPath(Uri uri) => uri.AbsolutePath == "/";

    private static bool IsLoopbackHost(string host)
    {
        // RFC 6761 reserves the "localhost" name to resolve only to loopback
        // addresses; Kestrel dev installations conventionally bind
        // "https://localhost:5001" with a dev cert, and adopters who set
        // LoopbackBaseUrl typically use "localhost" rather than "127.0.0.1".
        // Treat the literal hostname as a loopback alias so the cert-bypass
        // eligibility flag stays accurate for the typical dev shape.
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Strip IPv6 brackets so IPAddress.TryParse can read the address.
        var bare = host.StartsWith('[') && host.EndsWith(']')
            ? host[1..^1]
            : host;

        return IPAddress.TryParse(bare, out var ip) && IPAddress.IsLoopback(ip);
    }
}
