using System.Text;
using Umbraco.Community.AiVisibility.Configuration;
using LlmsTxt.Umbraco.Extraction;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;

namespace LlmsTxt.Umbraco.Builders;

/// <summary>
/// Built-in <see cref="ILlmsTxtBuilder"/>. Walks the published content cache under
/// <see cref="LlmsTxtBuilderContext.RootContent"/>, groups pages by configured
/// doctype, resolves per-page summaries (from the configured property alias OR
/// a 160-char body fallback), and emits an llmstxt.org-spec-compliant body.
/// <para>
/// Logically stateless across calls (per-request state lives in
/// <see cref="LlmsTxtBuilderContext"/>) but registered as <c>TryAddTransient</c>
/// because the dependency graph pulls scoped state via
/// <see cref="Extraction.IMarkdownContentExtractor"/>'s caching decorator. See
/// <see cref="Composers.BuildersComposer"/> for the full captive-dependency
/// rationale.
/// </para>
/// </summary>
internal sealed class DefaultLlmsTxtBuilder : ILlmsTxtBuilder
{
    private const int SummaryFallbackMaxChars = 160;
    private const string DefaultSectionTitle = "Pages";
    private const string DefaultSiteName = "Site";

    private readonly IPublishedUrlProvider _publishedUrlProvider;
    private readonly IPublishedValueFallback _publishedValueFallback;
    private readonly IMarkdownContentExtractor _extractor;
    private readonly ILogger<DefaultLlmsTxtBuilder> _logger;

    public DefaultLlmsTxtBuilder(
        IPublishedUrlProvider publishedUrlProvider,
        IPublishedValueFallback publishedValueFallback,
        IMarkdownContentExtractor extractor,
        ILogger<DefaultLlmsTxtBuilder> logger)
    {
        _publishedUrlProvider = publishedUrlProvider;
        _publishedValueFallback = publishedValueFallback;
        _extractor = extractor;
        _logger = logger;
    }

    public async Task<string> BuildAsync(LlmsTxtBuilderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var pages = context.Pages?.ToList() ?? new List<IPublishedContent>();
        var sections = GroupBySection(pages, context.Settings.BaseSettings.LlmsTxtBuilder, context.Hostname);

        var manifest = new StringBuilder();
        manifest.Append("# ").Append(SanitiseHeaderLine(ResolveSiteName(context))).Append('\n');
        // Story 3.1 — resolved overlay (doctype value wins per-field; falls back
        // to appsettings via the resolver's per-field overlay before the body
        // ever sees it). Skip the blockquote line entirely when the summary is
        // null/whitespace at both layers — emitting `> \n` would produce an
        // empty blockquote that violates the llms.txt manifest shape.
        var sanitisedSummary = SanitiseHeaderLine(context.Settings.SiteSummary);
        if (!string.IsNullOrWhiteSpace(sanitisedSummary))
        {
            manifest.Append("> ").Append(sanitisedSummary).Append('\n');
        }

        if (sections.Count == 0)
        {
            // Empty site (no opted-in pages) — header + blockquote only, valid llms.txt.
            return manifest.ToString();
        }

        foreach (var section in sections)
        {
            manifest.Append('\n').Append("## ").Append(section.Title).Append('\n');
            foreach (var page in section.Pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var summary = await ResolveSummaryAsync(page, context, cancellationToken).ConfigureAwait(false);
                AppendLink(manifest, page, summary, context.Culture, ResolveVariants(context, page.Key));
            }
        }

        return manifest.ToString();
    }

    /// <summary>
    /// Look up sibling-culture variants for the given page key from the
    /// per-request <see cref="LlmsTxtBuilderContext.HreflangVariants"/> map.
    /// Returns <c>null</c> when hreflang is disabled (the map itself is null) or
    /// the page has no published sibling cultures (the map has no entry).
    /// Builder treats null and empty list identically — neither emits a suffix.
    /// </summary>
    private static IReadOnlyList<HreflangVariant>? ResolveVariants(LlmsTxtBuilderContext context, Guid pageKey)
    {
        if (context.HreflangVariants is null)
        {
            return null;
        }
        return context.HreflangVariants.TryGetValue(pageKey, out var variants) ? variants : null;
    }

    private string ResolveSiteName(LlmsTxtBuilderContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.Settings.SiteName))
        {
            return context.Settings.SiteName!;
        }
        var rootName = context.RootContent?.Name;
        return string.IsNullOrWhiteSpace(rootName) ? DefaultSiteName : rootName!;
    }

    /// <summary>
    /// Groups pages into ordered sections per the configured
    /// <see cref="LlmsTxtBuilderSettings.SectionGrouping"/>. Pages whose doctype
    /// alias isn't matched by any configured section land in a final default
    /// <c>"Pages"</c> section. Sections whose configured doctype aliases match no
    /// pages are omitted from the output (and a <c>Warning</c> is logged).
    /// </summary>
    private List<SectionBuild> GroupBySection(
        List<IPublishedContent> pages,
        LlmsTxtBuilderSettings settings,
        string hostname)
    {
        var sections = new List<SectionBuild>();
        var assigned = new HashSet<Guid>();

        foreach (var entry in settings.SectionGrouping)
        {
            if (string.IsNullOrWhiteSpace(entry.Title))
            {
                _logger.LogWarning(
                    "Section grouping entry has empty Title; skipping (Host={Host})",
                    hostname);
                continue;
            }

            var aliasSet = new HashSet<string>(
                (entry.DocTypeAliases ?? Array.Empty<string>())
                    .Where(a => !string.IsNullOrWhiteSpace(a)),
                StringComparer.OrdinalIgnoreCase);
            if (aliasSet.Count == 0)
            {
                // Configuration error (section declared with no aliases) — skip
                // silently. Logging Warning per-request would flood the log every
                // cache rebuild after TTL expiry; this belongs in startup
                // validation, not the hot path.
                continue;
            }

            var sectionPages = pages
                .Where(p => !assigned.Contains(p.Key) && aliasSet.Contains(p.ContentType.Alias))
                .ToList();

            if (sectionPages.Count == 0)
            {
                _logger.LogWarning(
                    "Section group {SectionTitle} references unknown doctype {DocTypes}",
                    entry.Title,
                    string.Join(", ", aliasSet));
                continue;
            }

            foreach (var p in sectionPages)
            {
                assigned.Add(p.Key);
            }
            sections.Add(new SectionBuild(entry.Title, sectionPages));
        }

        var leftovers = pages.Where(p => !assigned.Contains(p.Key)).ToList();
        if (leftovers.Count > 0)
        {
            sections.Add(new SectionBuild(DefaultSectionTitle, leftovers));
        }

        return sections;
    }

    private async Task<string?> ResolveSummaryAsync(
        IPublishedContent page,
        LlmsTxtBuilderContext context,
        CancellationToken cancellationToken)
    {
        var alias = context.Settings.BaseSettings.LlmsTxtBuilder.PageSummaryPropertyAlias;
        if (!string.IsNullOrWhiteSpace(alias))
        {
            try
            {
                // Use the explicit overload taking IPublishedValueFallback so unit
                // tests don't depend on Umbraco's StaticServiceProvider — the
                // ambient overload (Umbraco.Web.Common.PublishedContentExtensions)
                // resolves the fallback via service-locator and NPEs in tests.
                // Resolve property at the IPublishedProperty layer so unit tests
                // don't depend on Umbraco's StaticServiceProvider (the ambient
                // `page.Value<string>(alias, culture)` overload service-locates
                // an IPublishedValueFallback at static-init time and NPEs in
                // tests). Two `Umbraco.Extensions.PublishedContentExtensions`
                // types — one in Umbraco.Core, one in Umbraco.Web.Common — share
                // the same FQN, so calling extension methods on IPublishedContent
                // also triggers a CS0433 ambiguity. Going via GetProperty/GetValue
                // sidesteps both. The DI-injected _publishedValueFallback is held
                // for future fall-through-fallback semantics if Story 3.1's
                // doctype overlay needs them.
                _ = _publishedValueFallback;
                string? raw = null;
                var prop = page.GetProperty(alias);
                if (prop is not null && prop.HasValue(context.Culture))
                {
                    raw = prop.GetValue(context.Culture)?.ToString();
                }
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    return CollapseWhitespace(raw!);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Page summary property {Alias} threw for {ContentKey}; trying body fallback",
                    alias,
                    page.Key);
            }
        }

        // Body-Markdown fallback. Per-page extraction failures must NOT 500 the
        // manifest — log + emit the link with no summary.
        try
        {
            var result = await _extractor
                .ExtractAsync(page, context.Culture, cancellationToken)
                .ConfigureAwait(false);
            if (result.Status != MarkdownExtractionStatus.Found || string.IsNullOrEmpty(result.Markdown))
            {
                return null;
            }
            return TruncateBody(MarkdownEscaping.StripFrontmatter(result.Markdown!));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Manifest summary fallback failed for {ContentKey} {Path}",
                page.Key,
                _publishedUrlProvider.GetUrl(page, UrlMode.Relative, context.Culture));
            return null;
        }
    }

    /// <summary>
    /// Emit one bulleted link line. Shape:
    /// <c>- [{Title}](/{path}.md){summary?}{hreflang-variants?}\n</c>.
    /// <para>
    /// When <paramref name="hreflangVariants"/> is non-null and non-empty (Story 2.3,
    /// AC3), each variant is appended after the optional summary as
    /// <c> ({culture}: {relativeMarkdownUrl})</c> in BCP-47-lexicographic order
    /// (sort by <see cref="HreflangVariant.Culture"/>). Variants are emitted
    /// verbatim from the controller-supplied list — the builder does NOT
    /// re-suffix `.md` or escape culture codes (BCP-47 is `[a-z]{2,3}-[A-Z]{2,3}`
    /// lowercased; no Markdown specials). When hreflang is disabled or no
    /// variants exist, the line shape is byte-identical to Story 2.1's output.
    /// </para>
    /// </summary>
    private void AppendLink(
        StringBuilder manifest,
        IPublishedContent page,
        string? summary,
        string? culture,
        IReadOnlyList<HreflangVariant>? hreflangVariants)
    {
        var title = MarkdownEscaping.EscapeMarkdownLinkText(page.Name ?? string.Empty);
        var url = ResolveManifestLink(page, culture);
        if (url is null)
        {
            _logger.LogWarning(
                "Skipping {ContentKey} from manifest — IPublishedUrlProvider returned no URL",
                page.Key);
            return;
        }

        manifest.Append("- [").Append(title).Append("](").Append(url).Append(')');
        if (!string.IsNullOrWhiteSpace(summary))
        {
            manifest.Append(": ").Append(summary);
        }

        if (hreflangVariants is { Count: > 0 })
        {
            // Lexicographic order by culture (BCP-47 is already lowercased on the
            // way in via HreflangVariantsResolver). Stable sort: LINQ OrderBy is
            // documented stable, so two variants with the same culture (which the
            // resolver should never emit, but defensively...) keep insertion
            // order.
            foreach (var variant in hreflangVariants.OrderBy(v => v.Culture, StringComparer.Ordinal))
            {
                manifest.Append(' ').Append('(')
                    .Append(variant.Culture)
                    .Append(": ")
                    .Append(variant.RelativeMarkdownUrl)
                    .Append(')');
            }
        }

        manifest.Append('\n');
    }

    private string? ResolveManifestLink(IPublishedContent page, string? culture)
    {
        try
        {
            var relative = _publishedUrlProvider.GetUrl(page, UrlMode.Relative, culture);
            if (string.IsNullOrWhiteSpace(relative))
            {
                return null;
            }

            // Already-absolute URL (multi-site misconfiguration). The llms.txt spec
            // uses root-relative links, so an absolute URL would leak the build-time
            // host into the cached manifest body and break multi-host serving.
            // Exclude the `file:` scheme — on Unix `Uri.TryCreate("/path", Absolute)`
            // succeeds with `file:///path` (a false positive that would skip every
            // root-relative URL on macOS/Linux). Same fix as
            // DefaultMarkdownContentExtractor.IsAlreadyAbsolute.
            if (Uri.TryCreate(relative, UriKind.Absolute, out var parsed)
                && !string.Equals(parsed.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Append the .md suffix to point at the per-page Markdown route.
            // Trailing slash → prefer index.html.md form per architecture line 251.
            var trimmed = relative.TrimEnd();
            return trimmed.EndsWith('/')
                ? trimmed + "index.html.md"
                : trimmed + Constants.Routes.MarkdownSuffix;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "IPublishedUrlProvider.GetUrl threw for {ContentKey}",
                page.Key);
            return null;
        }
    }

    /// <summary>
    /// Replace CR/LF with single spaces so a multi-line <c>SiteName</c> or
    /// <c>SiteSummary</c> setting doesn't drop out of the H1/blockquote line and
    /// produce invalid llms.txt body shape. Returns <see cref="string.Empty"/>
    /// for null/whitespace input.
    /// </summary>
    internal static string SanitiseHeaderLine(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.IndexOfAny(new[] { '\r', '\n' }) < 0) return value;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(c is '\r' or '\n' ? ' ' : c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Collapse all whitespace runs (including CR/LF) to a single space. Ensures
    /// the summary fits on the bulleted-list line per the llms.txt spec.
    /// </summary>
    internal static string CollapseWhitespace(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        var lastWasWhitespace = false;
        foreach (var c in raw)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasWhitespace && sb.Length > 0)
                {
                    sb.Append(' ');
                }
                lastWasWhitespace = true;
            }
            else
            {
                sb.Append(c);
                lastWasWhitespace = false;
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Collapses whitespace, then truncates at the nearest word boundary at-or-before
    /// 160 chars. Appends an ellipsis (U+2026) on truncation. Word-boundary trim
    /// avoids mid-word cuts like <c>"…the quick brown fo"</c>.
    /// </summary>
    internal static string TruncateBody(string body)
    {
        var collapsed = CollapseWhitespace(body);
        if (collapsed.Length <= SummaryFallbackMaxChars)
        {
            return collapsed;
        }

        // Walk back from the limit until the previous char is whitespace OR we hit
        // start of string. Guarantees we don't break a word in half.
        var cutoff = SummaryFallbackMaxChars;
        while (cutoff > 0 && !char.IsWhiteSpace(collapsed[cutoff]))
        {
            cutoff--;
        }
        if (cutoff == 0)
        {
            // First 160 chars are a single word. Hard-cut and append ellipsis.
            cutoff = SummaryFallbackMaxChars;
        }
        return collapsed[..cutoff].TrimEnd() + "…";
    }

    private sealed record SectionBuild(string Title, IReadOnlyList<IPublishedContent> Pages);
}
