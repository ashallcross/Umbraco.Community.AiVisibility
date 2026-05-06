using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Umbraco.Community.AiVisibility.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartReader;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;

namespace Umbraco.Community.AiVisibility.Extraction;

/// <summary>
/// Built-in <see cref="IMarkdownContentExtractor"/> orchestrating the full pipeline:
/// in-process Razor render → AngleSharp parse → region select → strip-inside-region →
/// SmartReader fallback (against stripped HTML) → URL absolutify → empty-alt image
/// drop → ReverseMarkdown convert → YAML frontmatter prepend.
/// </summary>
internal sealed class DefaultMarkdownContentExtractor : IMarkdownContentExtractor
{
    private static readonly string[] StripSelectors = new[]
    {
        "script",
        "style",
        "svg",
        "iframe",
        "noscript",
        "[hidden]",
        "[aria-hidden=\"true\"]",
        "[data-llms-ignore]",
    };

    /// <summary>
    /// YAML 1.2 reserved tokens that must be quoted to avoid being parsed as
    /// scalar values (null, boolean, etc.) by strict YAML parsers.
    /// </summary>
    private static readonly HashSet<string> YamlReservedTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "null", "Null", "NULL", "~",
        "true", "True", "TRUE",
        "false", "False", "FALSE",
        "yes", "Yes", "YES",
        "no", "No", "NO",
        "on", "On", "ON",
        "off", "Off", "OFF",
    };

    private readonly PageRenderer _pageRenderer;
    private readonly IContentRegionSelector _regionSelector;
    private readonly MarkdownConverter _converter;
    private readonly IPublishedUrlProvider _publishedUrlProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IOptionsSnapshot<AiVisibilitySettings> _settings;
    private readonly ILogger<DefaultMarkdownContentExtractor> _logger;

    public DefaultMarkdownContentExtractor(
        PageRenderer pageRenderer,
        IContentRegionSelector regionSelector,
        MarkdownConverter converter,
        IPublishedUrlProvider publishedUrlProvider,
        IHttpContextAccessor httpContextAccessor,
        IOptionsSnapshot<AiVisibilitySettings> settings,
        ILogger<DefaultMarkdownContentExtractor> logger)
    {
        _pageRenderer = pageRenderer;
        _regionSelector = regionSelector;
        _converter = converter;
        _publishedUrlProvider = publishedUrlProvider;
        _httpContextAccessor = httpContextAccessor;
        _settings = settings;
        _logger = logger;
    }

    public async Task<MarkdownExtractionResult> ExtractAsync(
        IPublishedContent content,
        string? culture,
        CancellationToken cancellationToken)
    {
        var absoluteUri = ResolveAbsoluteUri(content, culture);

        var renderResult = await _pageRenderer.RenderAsync(content, absoluteUri, culture, cancellationToken);

        if (renderResult.Status == PageRenderStatus.Error)
        {
            return MarkdownExtractionResult.Failed(
                renderResult.Error
                    ?? new InvalidOperationException("PageRenderer reported failure with no exception."),
                absoluteUri.ToString(),
                content.Key);
        }

        var resolvedCulture = renderResult.ResolvedCulture ?? culture;
        var sourceUrl = ResolveAbsoluteContentUrl(content, absoluteUri, resolvedCulture) ?? absoluteUri.ToString();
        var metadata = new ContentMetadata(
            Title: content.Name ?? string.Empty,
            AbsoluteUrl: sourceUrl,
            UpdatedUtc: ToUtc(content.UpdateDate),
            ContentKey: content.Key,
            Culture: resolvedCulture ?? string.Empty);

        return await ExtractFromHtmlAsync(renderResult.Html!, absoluteUri, metadata, cancellationToken);
    }

    /// <summary>
    /// Derive the absolute URI for the content under the active request. Prefer
    /// <see cref="IPublishedUrlProvider"/> (host-aware via current
    /// <see cref="HttpContext"/>) and fall back to building from the request scheme +
    /// host + content URL when the URL provider returns a relative or missing value.
    /// </summary>
    private Uri ResolveAbsoluteUri(IPublishedContent content, string? culture)
    {
        try
        {
            var resolved = _publishedUrlProvider.GetUrl(content, UrlMode.Absolute, culture);
            if (!string.IsNullOrWhiteSpace(resolved)
                && Uri.TryCreate(resolved, UriKind.Absolute, out var asAbsolute))
            {
                return asAbsolute;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "IPublishedUrlProvider.GetUrl threw for {ContentKey}; falling back to request-derived URI",
                content.Key);
        }

        var request = _httpContextAccessor.HttpContext?.Request
            ?? throw new InvalidOperationException(
                "DefaultMarkdownContentExtractor requires an active HttpContext to derive an absolute URI.");

        var scheme = request.Scheme;
        var host = request.Host.HasValue ? request.Host.Value! : "localhost";

        // Use the content's relative URL under the host to compose an absolute URI.
        // GetUrl(...) without UrlMode.Absolute returns the host-relative path.
        var relative = _publishedUrlProvider.GetUrl(content, UrlMode.Relative, culture);
        if (string.IsNullOrWhiteSpace(relative))
        {
            relative = "/";
        }

        return new Uri($"{scheme}://{host}{relative}");
    }

    /// <summary>
    /// Internal seam — drives the pure HTML→Markdown pipeline against captured input
    /// without booting Umbraco. The benchmark test
    /// <c>ExtractionQualityBenchmarkTests</c> exercises this entry point against
    /// <c>Fixtures/Extraction/&lt;scenario&gt;/{input.html,expected.md}</c> pairs.
    /// </summary>
    /// <remarks>
    /// Pipeline ordering (locked by code review 2026-04-29):
    /// 1. Parse → 2. Pre-strip <c>html</c>/<c>body</c>-marked-ignore guard → 3. Select
    /// region → 4. Strip selectors inside chosen region (so <c>aria-hidden</c> on a
    /// wrapping container can't nuke a region before selection runs).
    /// </remarks>
    internal async Task<MarkdownExtractionResult> ExtractFromHtmlAsync(
        string html,
        Uri sourceUri,
        ContentMetadata metadata,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Force UTC at the seam so callers passing a Local/Unspecified DateTime
        // can't poison the "Z" suffix in frontmatter formatting.
        metadata = metadata with { UpdatedUtc = ToUtc(metadata.UpdatedUtc) };

        var parser = new HtmlParser();
        using var document = await parser.ParseDocumentAsync(html, cancellationToken);

        // Whole-document ignore: the page told us not to extract anything.
        if (HasDocumentLevelIgnore(document))
        {
            _logger.LogInformation(
                "data-llms-ignore on html/body — extraction suppressed {ContentKey} {Path}",
                metadata.ContentKey,
                sourceUri.AbsolutePath);
            return BuildFrontmatterOnlyResult(metadata, reason: "document-level data-llms-ignore");
        }

        IElement? region = _regionSelector.SelectRegion(
            document,
            _settings.Value.MainContentSelectors ?? Array.Empty<string>());

        if (region is null)
        {
            // Strip first so `[data-llms-ignore]` is honoured by the SmartReader fallback path too.
            StripBoilerplate(document);
            region = await TrySmartReaderFallbackAsync(document, sourceUri, metadata, cancellationToken);
            if (region is null)
            {
                return MarkdownExtractionResult.Failed(
                    new InvalidOperationException("Region selectors and SmartReader fallback both failed."),
                    metadata.AbsoluteUrl,
                    metadata.ContentKey);
            }
        }
        else
        {
            // Strip selectors INSIDE the chosen region — the region itself is the
            // boundary the adopter declared, so we never remove ancestors of it.
            StripWithinRegion(region);
        }

        LiftHeadingsOutOfAnchors(region);
        AbsolutifyUrls(region, sourceUri);
        DropImagesWithEmptyAltOrSrc(region);
        // Removing images can leave anchors empty (e.g. <a><img alt=""/></a>) which
        // would render as link-shaped noise [](url). Sweep them up.
        RemoveEmptyAnchors(region);

        // Trim trailing whitespace on each line — ReverseMarkdown emits stray
        // trailing spaces on blockquote-paragraph wrappers (`> `) that editors with
        // "trim on save" would silently rewrite, breaking the fixture byte-equality
        // benchmark in `ExtractionQualityBenchmarkTests`.
        var bodyMarkdown = TrimLineEndings(_converter.Convert(region.InnerHtml)).TrimEnd();

        if (string.IsNullOrWhiteSpace(bodyMarkdown))
        {
            _logger.LogWarning(
                "Empty render body for {ContentKey} {Path}",
                metadata.ContentKey,
                sourceUri.AbsolutePath);
            return BuildFrontmatterOnlyResult(metadata, reason: "empty body");
        }

        var frontmatter = BuildFrontmatter(metadata);
        var markdown = frontmatter + "\n" + bodyMarkdown + "\n";

        return MarkdownExtractionResult.Found(
            markdown: markdown,
            contentKey: metadata.ContentKey,
            culture: metadata.Culture,
            updatedUtc: metadata.UpdatedUtc,
            sourceUrl: metadata.AbsoluteUrl);
    }

    private static MarkdownExtractionResult BuildFrontmatterOnlyResult(ContentMetadata metadata, string reason)
    {
        var frontmatter = BuildFrontmatter(metadata);
        var markdown = frontmatter + "\n\n";
        _ = reason; // captured by the Warning log at the call site
        return MarkdownExtractionResult.Found(
            markdown: markdown,
            contentKey: metadata.ContentKey,
            culture: metadata.Culture,
            updatedUtc: metadata.UpdatedUtc,
            sourceUrl: metadata.AbsoluteUrl);
    }

    internal string? ResolveAbsoluteContentUrl(IPublishedContent content, Uri requestUri, string? culture)
    {
        try
        {
            // Story 6.0a (Codex finding #4) — pass the resolved culture so
            // multilingual sites emit a culture-correct `url:` in YAML
            // frontmatter. Pre-6.0a path called the 1-arg overload which
            // defaulted to the site's default culture, causing non-default
            // culture pages to advertise the default-culture URL. Canonical
            // 4-arg overload `IPublishedUrlProvider.GetUrl(IPublishedContent,
            // UrlMode, string culture, Uri current)` pinned at
            // Umbraco.Core.xml:63210; trailing `Uri` is unused for
            // UrlMode.Absolute.
            var url = _publishedUrlProvider.GetUrl(content, UrlMode.Absolute, culture);
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            // Validate Umbraco actually returned an absolute URL — under multi-site
            // misconfiguration `IPublishedUrlProvider` silently degrades to relative,
            // and we'd otherwise emit `url: /about` in frontmatter.
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                _logger.LogWarning(
                    "IPublishedUrlProvider returned relative URL {Url} for {ContentKey} — falling back to request URI",
                    url,
                    content.Key);
                return null;
            }

            return url;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "IPublishedUrlProvider.GetUrl threw for {ContentKey}; falling back to request URL",
                content.Key);
            return null;
        }
    }

    private static bool HasDocumentLevelIgnore(IDocument document)
    {
        var html = document.DocumentElement;
        if (html is not null && html.HasAttribute("data-llms-ignore"))
        {
            return true;
        }
        var body = document.Body;
        return body is not null && body.HasAttribute("data-llms-ignore");
    }

    private static void StripBoilerplate(IDocument document)
    {
        foreach (var selector in StripSelectors)
        {
            foreach (var el in document.QuerySelectorAll(selector).ToArray())
            {
                // Never destroy the document root; HasDocumentLevelIgnore handles those.
                if (ReferenceEquals(el, document.DocumentElement) || ReferenceEquals(el, document.Body))
                {
                    continue;
                }
                el.Remove();
            }
        }
    }

    private static void StripWithinRegion(IElement region)
    {
        foreach (var selector in StripSelectors)
        {
            foreach (var el in region.QuerySelectorAll(selector).ToArray())
            {
                el.Remove();
            }
        }
    }

    private async Task<IElement?> TrySmartReaderFallbackAsync(
        IDocument strippedDocument,
        Uri sourceUri,
        ContentMetadata metadata,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // SmartReader takes the post-strip HTML so [data-llms-ignore] / scripts /
            // iframes can't sneak content back in via the readability heuristic.
            var strippedHtml = strippedDocument.DocumentElement?.OuterHtml ?? string.Empty;
            var reader = new Reader(sourceUri.ToString(), strippedHtml);

            // GetArticle is CPU-bound; calling synchronously on the request thread
            // avoids Task.Run thread-pool churn and respects cancellation honestly
            // (the call doesn't accept a CancellationToken — wrap it ourselves at the
            // boundary instead of pretending to honour it).
            cancellationToken.ThrowIfCancellationRequested();
            var article = reader.GetArticle();
            cancellationToken.ThrowIfCancellationRequested();

            if (article is null || !article.IsReadable || string.IsNullOrWhiteSpace(article.Content))
            {
                return null;
            }

            // Re-parse the SmartReader-extracted HTML so the rest of the pipeline
            // (URL absolutify, empty-alt drop, ReverseMarkdown) runs uniformly.
            // Keep the IDocument alive — disposing it invalidates the returned IElement.
            var parser = new HtmlParser();
            var doc = await parser.ParseDocumentAsync(article.Content, cancellationToken);
            var element = doc.Body ?? doc.DocumentElement;

            if (element is not null)
            {
                _logger.LogWarning(
                    "SmartReader fallback fired {ContentKey} {Path} {Culture}",
                    metadata.ContentKey,
                    sourceUri.AbsolutePath,
                    metadata.Culture);
            }

            return element;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "SmartReader fallback threw for {ContentKey}",
                metadata.ContentKey);
            return null;
        }
    }

    /// <summary>
    /// Hoist <c>&lt;h1&gt;</c>–<c>&lt;h6&gt;</c> elements out of any wrapping
    /// <c>&lt;a&gt;</c> ancestor — Clean.Core BlockList card markup wraps each card
    /// heading in an anchor (<c>&lt;a&gt;&lt;h2&gt;Title&lt;/h2&gt;…&lt;/a&gt;</c>),
    /// which ReverseMarkdown faithfully encodes as a Markdown link whose visible text
    /// contains heading markers (<c>[## Heading](url)</c>) — technically correct, but
    /// unreadable for AI crawlers that look for top-level headings.
    ///
    /// <para>
    /// Transformation: each affected heading is moved to be a sibling immediately
    /// before its wrapping anchor; the anchor's remaining content stays in place.
    /// Headings nested at any depth inside the anchor are still lifted (intermediate
    /// wrappers like <c>&lt;div class="card"&gt;</c> are preserved). Heading order is
    /// preserved by walking matches in document order.
    /// </para>
    /// <para>
    /// If the lift empties the anchor entirely (the heading was the anchor's only
    /// content), the now-empty anchor is removed — preserving <c>[]</c>-shaped
    /// orphan links in Markdown is worse than dropping them.
    /// </para>
    /// </summary>
    private static void LiftHeadingsOutOfAnchors(IElement region)
    {
        var headings = region.QuerySelectorAll("a h1, a h2, a h3, a h4, a h5, a h6").ToArray();
        var emptiedAnchors = new HashSet<IElement>();

        foreach (var heading in headings)
        {
            // Walk to the OUTERMOST <a> ancestor that is still within the region.
            // Two reasons to bound the walk:
            //  - the descendant combinator in the selector matches against the document
            //    tree, not the region — so a region nested inside an <a> ancestor
            //    (pathological adopter selector, or SmartReader fallback wrapping the
            //    extracted body) would otherwise let `Closest("a")` lift the heading
            //    OUT of the region and silently drop it from the converted output.
            //  - nested anchors (`<a outer><a inner><h2/></a></a>` — invalid HTML but
            //    parseable) need the heading to escape the OUTER anchor, not just the
            //    inner one. Using the outermost-in-region anchor handles both.
            var anchor = FindOutermostAnchorInRegion(heading, region);
            if (anchor is null)
            {
                continue;
            }

            var anchorParent = anchor.ParentElement;
            if (anchorParent is null)
            {
                continue;
            }

            // Detach the heading from its current location and insert it immediately
            // before the wrapping anchor as a sibling. AngleSharp's InsertBefore moves
            // the node when it's already in the DOM (per the spec), so an explicit
            // remove-from-old-parent step isn't required.
            anchorParent.InsertBefore(heading, anchor);

            if (HasNoMeaningfulContent(anchor))
            {
                emptiedAnchors.Add(anchor);
            }
        }

        foreach (var anchor in emptiedAnchors)
        {
            anchor.Remove();
        }
    }

    private static IElement? FindOutermostAnchorInRegion(IElement element, IElement region)
    {
        IElement? outermost = null;
        IElement? current = element.ParentElement;
        while (current is not null)
        {
            if (ReferenceEquals(current, region))
            {
                return outermost;
            }
            if (string.Equals(current.LocalName, "a", StringComparison.OrdinalIgnoreCase))
            {
                outermost = current;
            }
            current = current.ParentElement;
        }
        // Walked off the document tree without hitting region — heading is not inside
        // region (selector matched via document-tree descendant combinator). Skip.
        return null;
    }

    private static void RemoveEmptyAnchors(IElement region)
    {
        foreach (var anchor in region.QuerySelectorAll("a").ToArray())
        {
            if (HasNoMeaningfulContent(anchor))
            {
                anchor.Remove();
            }
        }
    }

    /// <summary>
    /// True when the element has no element children and no non-whitespace text —
    /// after a heading lift, an anchor whose only content was the heading is left
    /// either empty or with whitespace nodes. ReverseMarkdown would emit
    /// <c>[](url)</c> for that, which is link-shaped noise; drop instead.
    /// </summary>
    private static bool HasNoMeaningfulContent(IElement element)
    {
        if (element.ChildElementCount > 0)
        {
            return false;
        }
        return string.IsNullOrWhiteSpace(element.TextContent);
    }

    private void AbsolutifyUrls(IElement region, Uri sourceUri)
    {
        AbsolutifyAttribute(region, "a", "href", sourceUri);
        AbsolutifyAttribute(region, "img", "src", sourceUri);
        AbsolutifyAttribute(region, "link", "href", sourceUri);
        AbsolutifyAttribute(region, "source", "src", sourceUri);
        AbsolutifySrcset(region, sourceUri);
    }

    private void AbsolutifyAttribute(IElement region, string tag, string attr, Uri sourceUri)
    {
        foreach (var el in region.QuerySelectorAll(tag))
        {
            var raw = el.GetAttribute(attr);
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            // Skip fragment-only links.
            if (raw.StartsWith('#'))
            {
                continue;
            }

            if (IsAlreadyAbsolute(raw))
            {
                continue;
            }

            try
            {
                var absolute = new Uri(sourceUri, raw);
                el.SetAttribute(attr, absolute.ToString());
            }
            catch (UriFormatException ex)
            {
                _logger.LogWarning(
                    ex,
                    "URL absolutification failed {RelativeUrl}",
                    raw);
            }
        }
    }

    /// <summary>
    /// Treat as already-absolute when the URL parses with a non-<c>file</c> scheme.
    /// On Unix, <c>Uri.TryCreate("/path", UriKind.Absolute, ...)</c> succeeds with
    /// <c>file:///path</c> — a false positive that would skip absolutification of
    /// every relative URL on macOS/Linux. Excluding the <c>file:</c> scheme makes
    /// the check platform-stable.
    /// </summary>
    private static bool IsAlreadyAbsolute(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        return !string.Equals(parsed.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase);
    }

    private void AbsolutifySrcset(IElement region, Uri sourceUri)
    {
        foreach (var el in region.QuerySelectorAll("source[srcset], img[srcset]"))
        {
            var raw = el.GetAttribute("srcset");
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var rebuilt = string.Join(", ", ParseSrcset(raw)
                .Select(entry =>
                {
                    var (urlPart, descriptor) = entry;
                    if (string.IsNullOrWhiteSpace(urlPart))
                    {
                        return null;
                    }
                    if (IsAlreadyAbsolute(urlPart))
                    {
                        return string.IsNullOrEmpty(descriptor) ? urlPart : $"{urlPart} {descriptor}";
                    }
                    try
                    {
                        var absolute = new Uri(sourceUri, urlPart);
                        return string.IsNullOrEmpty(descriptor)
                            ? absolute.ToString()
                            : $"{absolute} {descriptor}";
                    }
                    catch (UriFormatException)
                    {
                        return null;
                    }
                })
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Cast<string>());

            el.SetAttribute("srcset", rebuilt);
        }
    }

    /// <summary>
    /// Split a srcset attribute into <c>(url, descriptor)</c> pairs while honouring
    /// the WHATWG quirk that <c>data:</c> URIs contain commas inside their payload.
    /// Comma-as-separator fires only when the next token starts with a candidate
    /// width/density descriptor (digit / capital W or X / nothing). Whitespace
    /// between url and descriptor is any ASCII whitespace (space, tab, LF, FF, CR).
    /// </summary>
    private static IEnumerable<(string Url, string Descriptor)> ParseSrcset(string raw)
    {
        var i = 0;
        while (i < raw.Length)
        {
            // Skip leading whitespace and commas
            while (i < raw.Length && (char.IsWhiteSpace(raw[i]) || raw[i] == ','))
            {
                i++;
            }
            if (i >= raw.Length) yield break;

            var urlStart = i;
            // URL is anything up to the next ASCII-whitespace, except commas
            // immediately followed by a digit/W/X count as separators (so
            // `data:image/png;base64,XYZ` stays whole because the char after the
            // comma is alpha-numeric content of the URI body, not a descriptor).
            while (i < raw.Length && !char.IsWhiteSpace(raw[i]))
            {
                if (raw[i] == ',')
                {
                    // Look ahead — bare comma between entries (no descriptor) is fine
                    // when the URL itself doesn't contain unescaped commas, which is
                    // exactly the rule for non-data URLs. For data URIs we expect a
                    // descriptor to follow whitespace, not a bare comma. Treat bare
                    // comma as a safe separator only when not inside a `data:` URI.
                    var url = raw[urlStart..i];
                    if (!url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        // separator
                        yield return (url, string.Empty);
                        i++;
                        goto next;
                    }
                }
                i++;
            }

            var urlEnd = i;
            var urlPart = raw[urlStart..urlEnd];

            // Descriptor: skip whitespace, take everything up to next comma at depth 0.
            while (i < raw.Length && char.IsWhiteSpace(raw[i]))
            {
                i++;
            }
            var descStart = i;
            while (i < raw.Length && raw[i] != ',')
            {
                i++;
            }
            var descriptor = raw[descStart..i].Trim();

            yield return (urlPart, descriptor);

            // Skip the separating comma
            if (i < raw.Length && raw[i] == ',')
            {
                i++;
            }
            next:;
        }
    }

    /// <summary>
    /// Drop images that contribute nothing: empty/missing <c>alt</c> (decorative —
    /// per AC1) or empty <c>src</c> (would emit broken <c>![alt]()</c> Markdown).
    /// </summary>
    private static void DropImagesWithEmptyAltOrSrc(IElement region)
    {
        foreach (var img in region.QuerySelectorAll("img").ToArray())
        {
            var alt = img.GetAttribute("alt");
            var src = img.GetAttribute("src");
            if (string.IsNullOrWhiteSpace(alt) || string.IsNullOrWhiteSpace(src))
            {
                img.Remove();
            }
        }
    }

    private static string TrimLineEndings(string markdown)
    {
        var lines = markdown.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].TrimEnd();
        }
        return string.Join('\n', lines);
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        // Umbraco's NPoco-loaded dates default to Unspecified — assume UTC for the
        // backing store rather than letting the host TZ shift the timestamp.
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static string BuildFrontmatter(ContentMetadata metadata)
    {
        var title = EscapeYamlScalar(metadata.Title);
        var url = EscapeYamlScalar(metadata.AbsoluteUrl);
        var updated = metadata.UpdatedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

        return string.Join("\n", new[]
        {
            "---",
            $"title: {title}",
            $"url: {url}",
            $"updated: {updated}",
            "---",
        });
    }

    private static string EscapeYamlScalar(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "''";
        }

        // YAML 1.2 plain-scalar rules: a colon followed by whitespace ends a mapping
        // key, so only ": " (colon-space) forces quoting — bare colons inside URLs
        // (e.g. https://...) are safe as plain scalars. Flow indicators (,[]{})
        // anywhere force quoting because they delimit flow collections.
        var needsQuoting =
            value.Contains(": ", StringComparison.Ordinal)
            || value.EndsWith(':')
            || value.IndexOfAny(new[] { '\'', '"', '\n', '\r', '\t', ',', '[', ']', '{', '}' }) >= 0
            || value.StartsWith(' ')
            || value.EndsWith(' ')
            || HasLeadingYamlIndicator(value)
            || YamlReservedTokens.Contains(value);

        if (!needsQuoting)
        {
            return value;
        }

        // Single-quoted YAML — escape single quotes by doubling.
        var escaped = value.Replace("'", "''");
        return $"'{escaped}'";
    }

    private static bool HasLeadingYamlIndicator(string value)
    {
        if (value.Length == 0) return false;
        var first = value[0];
        // Indicators that force quoting only when at the START of a scalar.
        return first is '-' or '?' or '#' or '&' or '*' or '|' or '>' or '!' or '%' or '@' or '`';
    }
}
