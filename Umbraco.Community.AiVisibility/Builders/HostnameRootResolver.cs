using Umbraco.Community.AiVisibility.Caching;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Cms.Core.Web;

namespace LlmsTxt.Umbraco.Builders;

/// <summary>
/// Public-but-package-stable seam for controller-side hostname resolution. Same
/// shape rationale as <see cref="LlmsTxt.Umbraco.Routing.IMarkdownRouteResolver"/>:
/// a public controller's ctor must take a public dependency, but the seam isn't
/// a general-purpose adopter extension point.
/// </summary>
public interface IHostnameRootResolver
{
    HostnameRootResolution Resolve(string requestHost, IUmbracoContext umbracoContext);
}

/// <summary>
/// Resolves a request's hostname to an Umbraco root content node + culture by
/// walking <c>IDomainService.GetAll(includeWildcards: true)</c> and matching
/// <c>IDomain.DomainName</c> against the request host (case-insensitive,
/// port-stripped, wildcard-aware).
/// <para>
/// Architecture anchor: <c>architecture.md</c> § Multi-Site &amp; Multi-Language
/// (lines 383–388) — <c>IDomainService.GetAll(true)</c> is the only resolution
/// mechanism for <c>/llms.txt</c> and <c>/llms-full.txt</c>. No
/// <c>appsettings</c>-based site mapping per AR10.
/// </para>
/// </summary>
public sealed class HostnameRootResolver : IHostnameRootResolver
{
    private const string WildcardPrefix = "*.";

    private readonly IDomainService _domainService;
    private readonly ILocalizationService _localizationService;
    private readonly IDocumentNavigationQueryService _navigation;
    private readonly ILogger<HostnameRootResolver> _logger;

    public HostnameRootResolver(
        IDomainService domainService,
        ILocalizationService localizationService,
        IDocumentNavigationQueryService navigation,
        ILogger<HostnameRootResolver> logger)
    {
        _domainService = domainService;
        _localizationService = localizationService;
        _navigation = navigation;
        _logger = logger;
    }

    public HostnameRootResolution Resolve(string requestHost, IUmbracoContext umbracoContext)
    {
        ArgumentNullException.ThrowIfNull(umbracoContext);

        var publishedSnapshot = umbracoContext.Content;
        if (publishedSnapshot is null)
        {
            _logger.LogWarning(
                "UmbracoContext.Content is null; cannot resolve hostname {Host}",
                requestHost);
            return HostnameRootResolution.NotFound();
        }

        var normalisedHost = AiVisibilityCacheKeys.NormaliseHost(requestHost);
        var domains = _domainService.GetAll(includeWildcards: true).ToList();

        foreach (var domain in domains)
        {
            if (!IsMatch(domain, normalisedHost))
            {
                continue;
            }

            if (domain.RootContentId is not int rootId)
            {
                continue;
            }

            var root = publishedSnapshot.GetById(rootId);
            if (root is null)
            {
                _logger.LogWarning(
                    "IDomain {DomainName} matches host {Host} but root content {RootId} is not in published cache",
                    domain.DomainName,
                    normalisedHost,
                    rootId);
                continue;
            }

            var culture = NormaliseCulture(domain.LanguageIsoCode) ?? GetDefaultCulture();
            return HostnameRootResolution.Found(root, culture);
        }

        // No domain match. Fall back to default culture's first root content node
        // via IDocumentNavigationQueryService.TryGetRootKeys — the v17 canonical
        // path (IPublishedContentCache.GetAtRoot is marked [Obsolete] and not on
        // the interface).
        var defaultCulture = GetDefaultCulture();
        if (!_navigation.TryGetRootKeys(out var rootKeys))
        {
            _logger.LogWarning(
                "No IDomain match for {Host} and IDocumentNavigationQueryService.TryGetRootKeys returned false",
                normalisedHost);
            return HostnameRootResolution.NotFound();
        }

        IPublishedContent? fallbackRoot = null;
        foreach (var rootKey in rootKeys)
        {
            fallbackRoot = publishedSnapshot.GetById(rootKey);
            if (fallbackRoot is not null)
            {
                break;
            }
        }

        if (fallbackRoot is null)
        {
            _logger.LogWarning(
                "No IDomain match for {Host} and no root content available in default culture {Culture}",
                normalisedHost,
                defaultCulture);
            return HostnameRootResolution.NotFound();
        }

        _logger.LogWarning(
            "No IDomain match for {Host}; falling back to default culture's root {RootName} ({Culture})",
            normalisedHost,
            fallbackRoot.Name,
            defaultCulture);
        return HostnameRootResolution.Found(fallbackRoot, defaultCulture);
    }

    private static bool IsMatch(IDomain domain, string normalisedHost)
    {
        var raw = domain.DomainName;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        // IDomain.DomainName can carry "culture/path" prefixes (e.g. "/en/")
        // when the domain is a culture-only binding without a hostname; those
        // never match a hostname request.
        if (raw.StartsWith('/'))
        {
            return false;
        }

        // Wildcard form: "*.example.com" matches any *subdomain* of example.com.
        // Per standard wildcard DNS/TLS semantics (RFC 6125 § 6.4.3), the wildcard
        // does NOT match the apex domain — `*.example.com` matches `foo.example.com`
        // but not `example.com` itself. Adopters who want apex traffic too must
        // bind the apex explicitly as a separate IDomain.
        if (domain.IsWildcard || raw.StartsWith(WildcardPrefix, StringComparison.Ordinal))
        {
            var suffix = AiVisibilityCacheKeys.NormaliseHost(
                raw.StartsWith(WildcardPrefix, StringComparison.Ordinal) ? raw[WildcardPrefix.Length..] : raw);
            return !string.IsNullOrWhiteSpace(suffix)
                && suffix != "_"
                && normalisedHost.EndsWith("." + suffix, StringComparison.Ordinal);
        }

        // Exact host match (case-insensitive via NormaliseHost on both sides).
        var domainHost = AiVisibilityCacheKeys.NormaliseHost(StripScheme(raw));
        return string.Equals(domainHost, normalisedHost, StringComparison.Ordinal)
               && domainHost != "_";
    }

    /// <summary>
    /// IDomain.DomainName may be stored with a scheme (<c>https://siteA.example</c>)
    /// — strip it so the host-only comparison wins.
    /// </summary>
    private static string StripScheme(string raw)
    {
        var schemeIdx = raw.IndexOf("://", StringComparison.Ordinal);
        return schemeIdx < 0 ? raw : raw[(schemeIdx + 3)..];
    }

    private static string? NormaliseCulture(string? iso)
        => string.IsNullOrWhiteSpace(iso) ? null : iso.ToLowerInvariant();

    private string GetDefaultCulture()
    {
        try
        {
            var iso = _localizationService.GetDefaultLanguageIsoCode();
            return NormaliseCulture(iso) ?? "en-us";
        }
        catch
        {
            return "en-us";
        }
    }
}

/// <summary>
/// Result of <see cref="HostnameRootResolver.Resolve"/>. <see cref="Root"/> is
/// non-null on a successful match (including the default-culture fallback);
/// callers should treat null as 404.
/// </summary>
public sealed record HostnameRootResolution(IPublishedContent? Root, string? Culture)
{
    public static HostnameRootResolution Found(IPublishedContent root, string culture)
        => new(root, culture);

    public static HostnameRootResolution NotFound() => new(null, null);
}
