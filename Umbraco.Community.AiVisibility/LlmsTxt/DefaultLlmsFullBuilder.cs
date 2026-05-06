using System.Text;
using Umbraco.Community.AiVisibility.Caching;
using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Extraction;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;

namespace Umbraco.Community.AiVisibility.LlmsTxt;

/// <summary>
/// Built-in <see cref="ILlmsFullBuilder"/> (Story 2.2). Iterates the pre-collected
/// <see cref="LlmsFullBuilderContext.Pages"/> in the configured
/// <see cref="LlmsFullBuilderSettings.Order"/>, calls
/// <see cref="IMarkdownContentExtractor.ExtractAsync"/> per page, strips per-page
/// YAML frontmatter, prefixes each section with
/// <c># {Title}\n\n_Source: {absolute URL}_\n\n</c>, joins with
/// <c>\n\n---\n\n</c> separators, and enforces
/// <see cref="AiVisibilitySettings.MaxLlmsFullSizeKb"/> with a stable truncation footer
/// when the cap is hit.
/// <para>
/// Logically stateless across calls (per-request state lives in
/// <see cref="LlmsFullBuilderContext"/>) but registered as <c>TryAddTransient</c>
/// because the dependency graph pulls scoped state via
/// <see cref="IMarkdownContentExtractor"/>'s caching decorator. See
/// <see cref="Composers.BuildersComposer"/> for the captive-dependency rationale —
/// same shape Story 2.1's <see cref="DefaultLlmsTxtBuilder"/> hit.
/// </para>
/// </summary>
internal sealed class DefaultLlmsFullBuilder : ILlmsFullBuilder
{
    private const string PageSeparator = "\n\n---\n\n";

    private readonly IPublishedUrlProvider _publishedUrlProvider;
    private readonly IMarkdownContentExtractor _extractor;
    private readonly ILogger<DefaultLlmsFullBuilder> _logger;

    public DefaultLlmsFullBuilder(
        IPublishedUrlProvider publishedUrlProvider,
        IMarkdownContentExtractor extractor,
        ILogger<DefaultLlmsFullBuilder> logger)
    {
        _publishedUrlProvider = publishedUrlProvider;
        _extractor = extractor;
        _logger = logger;
    }

    public async Task<string> BuildAsync(LlmsFullBuilderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var pages = ApplyOrdering(context.Pages, context.Settings.BaseSettings.LlmsFullBuilder.Order);
        var totalPages = pages.Count;
        if (totalPages == 0)
        {
            return string.Empty;
        }

        // Cap = 0 / negative → defensive fallback to "no cap" + Warning.
        var capKb = context.Settings.BaseSettings.MaxLlmsFullSizeKb;
        long capBytes;
        if (capKb <= 0)
        {
            _logger.LogWarning(
                "MaxLlmsFullSizeKb {Cap} is non-positive; treating as unlimited (defensive)",
                capKb);
            capBytes = long.MaxValue;
        }
        else
        {
            capBytes = (long)capKb * 1024L;
        }

        var manifest = new StringBuilder();
        long runningBytes = 0;
        var pagesEmitted = 0;
        var truncated = false;
        var singlePageOverflow = false;
        var firstSectionWritten = false;

        for (var i = 0; i < pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = pages[i];

            string? section;
            try
            {
                section = await BuildPageSectionAsync(page, context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "/llms-full.txt — page extraction failed for {ContentKey} {Path}",
                    page.Key,
                    page.Name);
                section = BuildSkippedPlaceholder(page);
            }

            if (section is null)
            {
                // Extractor returned Error status without throwing — same outcome as
                // an exception: emit the placeholder so the manifest stays valid.
                section = BuildSkippedPlaceholder(page);
            }

            // Compose with the inter-section separator (only between sections, not
            // before the first one).
            var sectionWithSeparator = firstSectionWritten ? PageSeparator + section : section;
            var sectionBytes = Encoding.UTF8.GetByteCount(sectionWithSeparator);

            if (runningBytes + sectionBytes > capBytes)
            {
                // Cap hit — refuse this page and stop. Special case: this is the FIRST
                // page and on its own it busts the cap → emit it truncated mid-content
                // with an inline marker per AC5 single-page-overflow rule. The inline
                // marker is the truncation signal in this branch — the standard
                // site-level footer is suppressed below to avoid two consecutive
                // _Truncated:_ blocks with conflicting "Showing 0 of N" semantics.
                if (!firstSectionWritten)
                {
                    EmitOversizedFirstPage(manifest, section, capBytes, capKb);
                    singlePageOverflow = true;
                }

                truncated = true;
                break;
            }

            manifest.Append(sectionWithSeparator);
            runningBytes += sectionBytes;
            firstSectionWritten = true;
            pagesEmitted++;
        }

        if (truncated && !singlePageOverflow)
        {
            // Standard truncation footer — does NOT count toward the cap (footer is
            // metadata, not page content per AC5). Suppressed on the
            // single-page-overflow path: the inline marker emitted by
            // EmitOversizedFirstPage is the page-level truncation signal there.
            manifest.Append(PageSeparator)
                .Append("_Truncated: site exceeds the configured ")
                .Append(capKb)
                .Append(" KB cap. Showing ")
                .Append(pagesEmitted)
                .Append(" of ")
                .Append(totalPages)
                .Append(" pages._\n");
        }

        return manifest.ToString();
    }

    /// <summary>
    /// Apply the configured ordering policy to the controller's pre-collected page
    /// list. Tree-order accepts the input verbatim. Alphabetical / RecentFirst use
    /// LINQ's <c>OrderBy</c> for stable sorts; RecentFirst breaks ties by tree-index
    /// so two pages with the same <c>UpdateDate</c> stay deterministic.
    /// </summary>
    private static List<IPublishedContent> ApplyOrdering(
        IReadOnlyList<IPublishedContent> pages,
        LlmsFullOrder order)
    {
        return order switch
        {
            LlmsFullOrder.Alphabetical => pages
                .OrderBy(p => p.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            LlmsFullOrder.RecentFirst => pages
                .Select((p, treeIndex) => (Page: p, TreeIndex: treeIndex))
                .OrderByDescending(x => x.Page.UpdateDate)
                .ThenBy(x => x.TreeIndex)
                .Select(x => x.Page)
                .ToList(),
            _ => pages.ToList(), // TreeOrder
        };
    }

    private async Task<string?> BuildPageSectionAsync(
        IPublishedContent page,
        LlmsFullBuilderContext context,
        CancellationToken cancellationToken)
    {
        var extraction = await _extractor
            .ExtractAsync(page, context.Culture, cancellationToken)
            .ConfigureAwait(false);

        if (extraction.Status != MarkdownExtractionStatus.Found || string.IsNullOrEmpty(extraction.Markdown))
        {
            return null;
        }

        var stripped = MarkdownEscaping.StripFrontmatter(extraction.Markdown!);
        var title = MarkdownEscaping.EscapeMarkdownLinkText(page.Name ?? string.Empty);
        var sourceUrl = ResolveAbsoluteSourceUrl(page, context);

        var sb = new StringBuilder(stripped.Length + 64);
        sb.Append("# ").Append(title).Append("\n\n")
            .Append("_Source: ").Append(sourceUrl).Append("_\n\n")
            .Append(stripped);
        return sb.ToString();
    }

    /// <summary>
    /// Resolve the page's absolute URL for the <c>_Source:</c> line. Per architecture
    /// § Caching &amp; HTTP, <c>/llms-full.txt</c> is consumed as a self-contained
    /// off-site dump — relative URLs would lose meaning, so this builder uses
    /// <see cref="UrlMode.Absolute"/> (opposite to <c>/llms.txt</c>'s root-relative
    /// links). When <see cref="IPublishedUrlProvider"/> returns null/empty (e.g.
    /// multi-site misconfiguration where the provider has no domain mapping at hand),
    /// fall back to building <c>https://{Hostname}{relativeUrl}</c> defensively.
    /// If the context carries no hostname either, emit <c>about:blank</c> rather than
    /// poisoning the off-site dump with an unreachable <c>https://localhost/...</c>
    /// link — <c>about:blank</c> is RFC-defined as "no content available", which
    /// signals the broken provenance to AI consumers without sending them to the
    /// wrong machine.
    /// </summary>
    private string ResolveAbsoluteSourceUrl(IPublishedContent page, LlmsFullBuilderContext context)
    {
        try
        {
            var absolute = _publishedUrlProvider.GetUrl(page, UrlMode.Absolute, context.Culture);
            if (!string.IsNullOrWhiteSpace(absolute))
            {
                return absolute;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "IPublishedUrlProvider.GetUrl(Absolute) threw for {ContentKey}; falling back to hostname prefix",
                page.Key);
        }

        // Fall back to building the absolute URL from the hostname + the relative URL.
        var relative = string.Empty;
        try
        {
            relative = _publishedUrlProvider.GetUrl(page, UrlMode.Relative, context.Culture) ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "IPublishedUrlProvider.GetUrl(Relative) threw for {ContentKey}; using empty relative URL",
                page.Key);
        }

        var hasHost = !string.IsNullOrWhiteSpace(context.Hostname)
            && context.Hostname != AiVisibilityCacheKeys.NormaliseHost(null);

        if (!hasHost)
        {
            _logger.LogWarning(
                "/llms-full.txt — IPublishedUrlProvider returned no absolute URL for {ContentKey} AND no hostname in context; emitting about:blank placeholder",
                page.Key);
            return "about:blank";
        }

        var path = relative.StartsWith('/') ? relative : "/" + relative;
        _logger.LogWarning(
            "/llms-full.txt — IPublishedUrlProvider absolute URL empty for {ContentKey}; building https://{Host}{Path} fallback",
            page.Key,
            context.Hostname,
            path);
        return $"https://{context.Hostname}{path}";
    }

    private static string BuildSkippedPlaceholder(IPublishedContent page)
    {
        var safeTitle = MarkdownEscaping.EscapeMarkdownLinkText(page.Name ?? string.Empty);
        return $"<!-- LlmsTxt: skipped {safeTitle} due to extraction error -->";
    }

    /// <summary>
    /// AC5 single-page-overflow path: when the very first emitted page on its own
    /// exceeds the entire cap, emit it truncated mid-content with an inline marker
    /// after the cut. The byte budget is the cap minus the inline marker length so
    /// the final manifest stays under cap. Followed by the standard truncation
    /// footer ("Showing 0 of M pages.") emitted by the caller.
    /// </summary>
    private void EmitOversizedFirstPage(StringBuilder manifest, string fullSection, long capBytes, int capKb)
    {
        const string InlineMarker = "\n\n_Truncated: page content exceeds the {0} KB cap._\n";
        var marker = string.Format(System.Globalization.CultureInfo.InvariantCulture, InlineMarker, capKb);
        var markerBytes = Encoding.UTF8.GetByteCount(marker);

        // Allocate as many bytes of page content as the cap minus the marker. If
        // the cap is so small even the marker doesn't fit, emit just the marker so
        // the body is at least observable — defensive against pathological caps.
        var bodyBudget = capBytes - markerBytes;
        var truncated = bodyBudget > 0
            ? TruncateUtf8(fullSection, bodyBudget)
            : string.Empty;

        manifest.Append(truncated).Append(marker);
        _logger.LogWarning(
            "/llms-full.txt — page content exceeds entire cap {Cap} KB; emitting truncated",
            capKb);
    }

    /// <summary>
    /// Truncate <paramref name="value"/> at the largest UTF-8 prefix whose byte
    /// length is &lt;= <paramref name="byteBudget"/>. Encodes one char at a time so
    /// we never split a multi-byte UTF-8 sequence (which would emit invalid bytes).
    /// </summary>
    private static string TruncateUtf8(string value, long byteBudget)
    {
        if (byteBudget <= 0)
        {
            return string.Empty;
        }

        var encoding = Encoding.UTF8;
        var consumed = 0L;
        var sb = new StringBuilder();
        for (var i = 0; i < value.Length;)
        {
            // Handle surrogate pairs as a single unit so we never split them.
            var charCount = char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])
                ? 2
                : 1;
            var slice = value.AsSpan(i, charCount);
            var sliceBytes = encoding.GetByteCount(slice);
            if (consumed + sliceBytes > byteBudget)
            {
                break;
            }

            sb.Append(slice);
            consumed += sliceBytes;
            i += charCount;
        }
        return sb.ToString();
    }
}
