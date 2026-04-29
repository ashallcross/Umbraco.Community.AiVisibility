using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using LlmsTxt.Umbraco.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartReader;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;

namespace LlmsTxt.Umbraco.Extraction;

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
    private readonly IOptionsSnapshot<LlmsTxtSettings> _settings;
    private readonly ILogger<DefaultMarkdownContentExtractor> _logger;

    public DefaultMarkdownContentExtractor(
        PageRenderer pageRenderer,
        IContentRegionSelector regionSelector,
        MarkdownConverter converter,
        IPublishedUrlProvider publishedUrlProvider,
        IOptionsSnapshot<LlmsTxtSettings> settings,
        ILogger<DefaultMarkdownContentExtractor> logger)
    {
        _pageRenderer = pageRenderer;
        _regionSelector = regionSelector;
        _converter = converter;
        _publishedUrlProvider = publishedUrlProvider;
        _settings = settings;
        _logger = logger;
    }

    public async Task<MarkdownExtractionResult> ExtractAsync(Uri absoluteUri, CancellationToken cancellationToken)
    {
        var renderResult = await _pageRenderer.RenderAsync(absoluteUri, culture: null, cancellationToken);

        switch (renderResult.Status)
        {
            case PageRenderStatus.NotFound:
                return MarkdownExtractionResult.NotFound(absoluteUri.AbsolutePath);

            case PageRenderStatus.Error:
                return MarkdownExtractionResult.Failed(
                    renderResult.Error
                        ?? new InvalidOperationException("PageRenderer reported failure with no exception."),
                    absoluteUri.ToString(),
                    renderResult.Content?.Key);
        }

        var content = renderResult.Content
            ?? throw new InvalidOperationException(
                "PageRenderer returned Ok but no IPublishedContent — invariant violated.");

        var sourceUrl = ResolveAbsoluteContentUrl(content, absoluteUri) ?? absoluteUri.ToString();
        var metadata = new ContentMetadata(
            Title: content.Name ?? string.Empty,
            AbsoluteUrl: sourceUrl,
            UpdatedUtc: ToUtc(content.UpdateDate),
            ContentKey: content.Key,
            Culture: renderResult.ResolvedCulture ?? string.Empty);

        return await ExtractFromHtmlAsync(renderResult.Html!, absoluteUri, metadata, cancellationToken);
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

        AbsolutifyUrls(region, sourceUri);
        DropImagesWithEmptyAltOrSrc(region);

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

    private string? ResolveAbsoluteContentUrl(IPublishedContent content, Uri requestUri)
    {
        try
        {
            var url = _publishedUrlProvider.GetUrl(content, UrlMode.Absolute);
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
