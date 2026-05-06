using Asp.Versioning;
using Umbraco.Community.AiVisibility.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Api.Common.Attributes;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Web.Common.Authorization;

namespace LlmsTxt.Umbraco.Controllers.Backoffice;

/// <summary>
/// Story 3.2 — Backoffice Settings dashboard's Management API surface.
/// Routes to <c>/umbraco/management/api/v1/llmstxt/settings/...</c> via Spike 0.B's
/// canonical pattern (locked decision #5):
/// <c>[VersionedApiBackOfficeRoute("llmstxt/settings")]</c> from
/// <c>Umbraco.Cms.Api.Management.Routing</c> — the framework prepends
/// <c>/umbraco/management/api/v{version}/</c> so the resolved prefix is
/// <c>/umbraco/management/api/v1/llmstxt/settings/</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Auth pattern (Spike 0.B locked decision #11):</b> the Management API
/// enforces bearer-token auth via OpenIddict, NOT cookies. Cookie-only
/// <c>fetch(..., { credentials: "include" })</c> calls return HTTP 401. The
/// dashboard at <c>llms-settings-dashboard.element.ts</c> uses
/// <c>UMB_AUTH_CONTEXT.getOpenApiConfiguration()</c> to obtain a bearer token
/// per call.
/// </para>
/// <para>
/// <b>Authorization:</b> all actions are gated by
/// <see cref="AuthorizationPolicies.SectionAccessSettings"/>. Editors without
/// Settings-section access receive HTTP 403; users without any Backoffice auth
/// receive HTTP 401. The dashboard's manifest condition
/// (<c>Umb.Condition.SectionAlias</c> matching <c>Umb.Section.Settings</c>)
/// hides the tile from non-Settings users at the UI layer; this attribute is
/// the API-layer enforcement (UX-DR4).
/// </para>
/// <para>
/// <b>Resolver-throw graceful degradation:</b> <see cref="ISettingsResolver"/>
/// throwing on <see cref="GetAsync"/> falls back to the appsettings snapshot +
/// logs <c>Warning</c>. Same shape as Story 2.3 hreflang resolver-throw
/// (<c>LlmsTxtController.cs</c>) and Story 3.1 <see cref="MarkdownController"/>.
/// </para>
/// <para>
/// <b>Cache invalidation:</b> <see cref="PutAsync"/> writes through
/// <see cref="IContentService.Save"/> + <see cref="IContentService.Publish"/>;
/// Umbraco's normal pipeline fires <c>ContentCacheRefresherNotification</c>,
/// which Story 3.1's <c>ContentCacheRefresherHandler</c> handles by clearing
/// the <c>llms:settings:</c> namespace. No manual cache-bust here — the
/// notification path is the single source of truth.
/// </para>
/// </remarks>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("llmstxt/settings")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessSettings)]
[MapToApi(Constants.ApiName)]
public sealed class LlmsSettingsManagementApiController : ManagementApiControllerBase
{
    /// <summary>
    /// Server-side cap on <see cref="LlmsSettingsUpdateRequest.SiteSummary"/>.
    /// Surfaced to the client via <see cref="LlmsSettingsViewModel.SummaryMaxChars"/>
    /// so the form counter reads from one source of truth (no client/server drift).
    /// Matches the resolver's <c>SiteSummaryMaxChars</c> truncation cap at
    /// <see cref="DefaultSettingsResolver"/>.
    /// </summary>
    internal const int SiteSummaryMaxChars = 500;

    /// <summary>
    /// Server-side cap on <see cref="LlmsSettingsUpdateRequest.SiteName"/>.
    /// Symmetric with <see cref="SiteSummaryMaxChars"/> — the H1 of every
    /// <c>/llms.txt</c> response. 255 mirrors the SQL Server <c>nvarchar(255)</c>
    /// convention used by Umbraco's name columns.
    /// </summary>
    internal const int SiteNameMaxChars = 255;

    /// <summary>
    /// Server-side cap on the number of excluded-doctype-alias entries. A
    /// hostile or buggy client cannot inflate the persisted Settings node by
    /// submitting an unbounded array; the resolver re-parses the joined string
    /// on every cache miss, so this also bounds resolver work.
    /// </summary>
    internal const int ExcludedAliasesMaxCount = 1024;

    /// <summary>
    /// Settings doctype alias matched against root content nodes when the
    /// resolver / controller walks roots. Same constant as
    /// <c>DefaultSettingsResolver.SettingsDoctypeAlias</c> (kept private
    /// there; duplicated here so the controller doesn't take a dependency on
    /// the resolver's internals).
    /// </summary>
    internal const string SettingsDoctypeAlias = "llmsSettings";

    internal const string SiteNameAlias = "siteName";
    internal const string SiteSummaryAlias = "siteSummary";
    internal const string ExcludedAliasesAlias = "excludedDoctypeAliases";
    internal const string ExcludeFromLlmExportsAlias = "excludeFromLlmExports";
    internal const string DefaultSettingsNodeName = "LlmsTxt Settings";

    /// <summary>
    /// Characters that <see cref="DefaultSettingsResolver"/> uses as alias
    /// separators when parsing the persisted textarea property. Aliases
    /// containing any of these would round-trip as multiple entries — reject
    /// them at the validation boundary so persisted data stays well-formed.
    /// </summary>
    private static readonly char[] AliasSeparatorChars = { '\n', '\r', ',', ';' };

    private readonly ISettingsResolver _resolver;
    private readonly IContentService _contentService;
    private readonly IContentTypeService _contentTypeService;
    private readonly IUmbracoContextAccessor _umbracoContextAccessor;
    private readonly IDocumentNavigationQueryService _navigation;
    private readonly IPublishedUrlProvider _publishedUrlProvider;
    private readonly ILogger<LlmsSettingsManagementApiController> _logger;

    public LlmsSettingsManagementApiController(
        ISettingsResolver resolver,
        IContentService contentService,
        IContentTypeService contentTypeService,
        IUmbracoContextAccessor umbracoContextAccessor,
        IDocumentNavigationQueryService navigation,
        IPublishedUrlProvider publishedUrlProvider,
        ILogger<LlmsSettingsManagementApiController> logger)
    {
        _resolver = resolver;
        _contentService = contentService;
        _contentTypeService = contentTypeService;
        _umbracoContextAccessor = umbracoContextAccessor;
        _navigation = navigation;
        _publishedUrlProvider = publishedUrlProvider;
        _logger = logger;
    }

    /// <summary>
    /// Returns the resolver's effective settings overlay plus the live
    /// <c>llmsSettings</c> content-node key (or <c>null</c> when no Settings
    /// node exists yet). Resolver throws are caught and surfaced as the
    /// appsettings-only fallback (see <see cref="ResolveSafelyAsync"/>).
    /// </summary>
    [HttpGet("")]
    [ProducesResponseType<LlmsSettingsViewModel>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(CancellationToken cancellationToken)
    {
        var (host, culture) = ReadHostAndCulture();
        var resolved = await ResolveSafelyAsync(host, culture, cancellationToken).ConfigureAwait(false);
        var settingsNodeKey = TryFindSettingsNodeKey();

        return Ok(BuildViewModel(resolved, settingsNodeKey));
    }

    /// <summary>
    /// Validates the payload, upserts the Settings content node, and publishes.
    /// The publish triggers Umbraco's <c>ContentCacheRefresherNotification</c>
    /// pipeline → Story 3.1's handler clears <c>llms:settings:</c> per the
    /// existing notification flow. No explicit cache-bust here.
    /// </summary>
    [HttpPut("")]
    [Consumes("application/json")]
    [ProducesResponseType<LlmsSettingsViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<IActionResult> PutAsync(
        [FromBody] LlmsSettingsUpdateRequest? request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var validation = ValidateUpdateRequest(request);
        if (validation.IsInvalid)
        {
            return Task.FromResult<IActionResult>(Problem(
                title: "Invalid LlmsTxt settings payload",
                detail: validation.Detail,
                statusCode: StatusCodes.Status400BadRequest));
        }

        var sanitised = validation.Sanitised!;

        var content = ResolveOrCreateSettingsContent();
        if (content is null)
        {
            // Doctype not present in the host (uSync coexistence path with
            // SkipSettingsDoctype + uSync hasn't imported the schema yet).
            return Task.FromResult<IActionResult>(Problem(
                title: "LlmsTxt Settings doctype not installed",
                detail: $"Content type '{SettingsDoctypeAlias}' was not found. "
                    + "Install via the package migration or import the doctype via uSync.",
                statusCode: StatusCodes.Status400BadRequest));
        }

        content.SetValue(SiteNameAlias, sanitised.SiteName);
        content.SetValue(SiteSummaryAlias, sanitised.SiteSummary);
        content.SetValue(
            ExcludedAliasesAlias,
            string.Join('\n', sanitised.ExcludedDoctypeAliases));

        _contentService.Save(content);
        cancellationToken.ThrowIfCancellationRequested();
        var publishResult = _contentService.Publish(content, new[] { "*" });

        if (!publishResult.Success)
        {
            _logger.LogWarning(
                "LlmsSettings PUT — IContentService.Publish returned non-success {Status} for {ContentKey}",
                publishResult.Result.ToString(),
                content.Key);
            return Task.FromResult<IActionResult>(Problem(
                title: "Failed to publish LlmsTxt Settings node",
                detail: "Check user permissions on the Settings node and the host's publish pipeline.",
                statusCode: StatusCodes.Status400BadRequest));
        }

        // Round-trip from the sanitised request directly rather than calling
        // ResolveAsync. The cache refresher handler runs asynchronously off the
        // ContentCacheRefresherNotification, so the resolver may still see
        // pre-edit values on this thread. Building from the just-saved values
        // avoids a stale-cache flicker in the dashboard's _initialFormState.
        return Task.FromResult<IActionResult>(Ok(new LlmsSettingsViewModel(
            SiteName: sanitised.SiteName,
            SiteSummary: sanitised.SiteSummary,
            ExcludedDoctypeAliases: sanitised.ExcludedDoctypeAliases
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            SummaryMaxChars: SiteSummaryMaxChars,
            SettingsNodeKey: content.Key)));
    }

    /// <summary>
    /// Returns the host's content-type alias list filtered to publish-eligible
    /// doctypes — used by the dashboard's <c>excludedDoctypeAliases</c>
    /// multi-select. Element types (compositions like
    /// <c>llmsTxtSettingsComposition</c>) and the <c>llmsSettings</c> doctype
    /// itself are excluded — the form is for choosing pages to omit, not for
    /// listing the settings doctype.
    /// </summary>
    [HttpGet("doctypes")]
    [ProducesResponseType<IReadOnlyList<LlmsDoctypeViewModel>>(StatusCodes.Status200OK)]
    public IActionResult GetDoctypes(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var doctypes = _contentTypeService.GetAll() ?? Array.Empty<IContentType>();
        var view = doctypes
            .Where(ct => ct is not null
                && !ct.IsElement
                && !string.Equals(ct.Alias, SettingsDoctypeAlias, StringComparison.OrdinalIgnoreCase))
            .OrderBy(ct => ct.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(ct => new LlmsDoctypeViewModel(
                Alias: ct.Alias,
                Name: ct.Name ?? ct.Alias,
                IconCss: ct.Icon))
            .ToList();

        return Ok((IReadOnlyList<LlmsDoctypeViewModel>)view);
    }

    /// <summary>
    /// Returns the (paginated) list of published pages whose
    /// <c>excludeFromLlmExports</c> composition property is <c>true</c>.
    /// Walking all descendants is O(n); the controller clamps <c>take</c> to
    /// <c>[1, 200]</c>. Cross-instance caching deferred to v1.1+
    /// (<c>deferred-work.md</c> D-3.2-1).
    /// </summary>
    [HttpGet("excluded-pages")]
    [ProducesResponseType<LlmsExcludedPagesPageViewModel>(StatusCodes.Status200OK)]
    public IActionResult GetExcludedPages(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var clampedTake = Math.Clamp(take, 1, 200);
        var clampedSkip = Math.Max(skip, 0);

        if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx?.Content is null)
        {
            return Ok(new LlmsExcludedPagesPageViewModel(
                Items: Array.Empty<LlmsExcludedPageViewModel>(),
                Total: 0,
                Skip: clampedSkip,
                Take: clampedTake));
        }

        if (!_navigation.TryGetRootKeys(out var rootKeys))
        {
            return Ok(new LlmsExcludedPagesPageViewModel(
                Items: Array.Empty<LlmsExcludedPageViewModel>(),
                Total: 0,
                Skip: clampedSkip,
                Take: clampedTake));
        }

        var content = ctx.Content;
        var matches = new List<IPublishedContent>();

        foreach (var rootKey in rootKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CollectExcludedDescendants(rootKey, content, matches, cancellationToken);
        }

        // Project to (page, url) tuples once — IPublishedUrlProvider.GetUrl
        // walks every URL provider on each call, so re-evaluating during sort
        // and projection is N + N work. One projection then sort + skip + take.
        var resolved = matches
            .Select(p => (Page: p, Url: _publishedUrlProvider.GetUrl(p) ?? string.Empty))
            .OrderBy(x => x.Url, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Page.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var total = resolved.Count;
        // Build alias → human-readable name map once per request — IPublishedContentType
        // only carries the alias; the human-readable Name lives on IContentType.
        var contentTypeNameMap = (_contentTypeService.GetAll() ?? Array.Empty<IContentType>())
            .Where(ct => ct is not null)
            .GroupBy(ct => ct.Alias, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name ?? g.Key, StringComparer.OrdinalIgnoreCase);

        var pageItems = resolved
            .Skip(clampedSkip)
            .Take(clampedTake)
            .Select(x =>
            {
                var alias = x.Page.ContentType?.Alias ?? string.Empty;
                var name = contentTypeNameMap.TryGetValue(alias, out var displayName) ? displayName : alias;
                return new LlmsExcludedPageViewModel(
                    Key: x.Page.Key,
                    Name: x.Page.Name ?? string.Empty,
                    Path: x.Url,
                    Culture: FormatCultureSet(x.Page.Cultures),
                    ContentTypeAlias: alias,
                    ContentTypeName: name);
            })
            .ToList();

        return Ok(new LlmsExcludedPagesPageViewModel(
            Items: pageItems,
            Total: total,
            Skip: clampedSkip,
            Take: clampedTake));
    }

    /// <summary>
    /// Joins the page's culture keys in deterministic OrdinalIgnoreCase order.
    /// Invariant pages return <c>null</c>; multi-culture variants render as
    /// <c>"en-GB, fr-FR"</c> so the dashboard row reflects ALL cultures the
    /// page is published under (the <c>excludeFromLlmExports</c> flag is
    /// invariant, so the entry applies across cultures regardless).
    /// </summary>
    private static string? FormatCultureSet(IReadOnlyDictionary<string, PublishedCultureInfo>? cultures)
    {
        if (cultures is null || cultures.Count == 0)
        {
            return null;
        }

        var ordered = cultures.Keys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", ordered);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private (string? Host, string? Culture) ReadHostAndCulture()
    {
        var host = HttpContext.Request.Host.HasValue ? HttpContext.Request.Host.Host : null;

        // Use GetTypedHeaders so q= quality preferences are honoured. Headers
        // like `en;q=0.1, fr-FR;q=0.9` resolve to "fr-FR" — the raw split on
        // "," would have grabbed "en" because of token order.
        string? culture = null;
        var typedHeaders = HttpContext.Request.GetTypedHeaders();
        var acceptLanguages = typedHeaders.AcceptLanguage;
        if (acceptLanguages is { Count: > 0 })
        {
            culture = acceptLanguages
                .OrderByDescending(l => l.Quality ?? 1.0)
                .Select(l => l.Value.Value)
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && v != "*");
        }

        return (host, culture);
    }

    private async Task<ResolvedLlmsSettings> ResolveSafelyAsync(string? host, string? culture, CancellationToken cancellationToken)
    {
        try
        {
            return await _resolver.ResolveAsync(host, culture, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug(
                "ISettingsResolver cancelled mid-call for {Host} {Culture}",
                host,
                culture);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "ISettingsResolver threw on Settings dashboard call for {Host} {Culture}; falling back to appsettings",
                host,
                culture);
            // Fall back to an empty resolved record — the dashboard will still
            // render with whatever it last knew. Using new AiVisibilitySettings()
            // mirrors the BaseSettings shape DefaultSettingsResolver
            // produces on the no-context path.
            return new ResolvedLlmsSettings(
                SiteName: null,
                SiteSummary: null,
                ExcludedDoctypeAliases: Array.Empty<string>(),
                BaseSettings: new AiVisibilitySettings());
        }
    }

    private LlmsSettingsViewModel BuildViewModel(ResolvedLlmsSettings resolved, Guid? settingsNodeKey)
    {
        var aliases = (resolved.ExcludedDoctypeAliases ?? Array.Empty<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new LlmsSettingsViewModel(
            SiteName: resolved.SiteName,
            SiteSummary: resolved.SiteSummary,
            ExcludedDoctypeAliases: aliases,
            SummaryMaxChars: SiteSummaryMaxChars,
            SettingsNodeKey: settingsNodeKey);
    }

    private Guid? TryFindSettingsNodeKey()
    {
        if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx?.Content is null)
        {
            return null;
        }

        if (!_navigation.TryGetRootKeys(out var rootKeys))
        {
            return null;
        }

        foreach (var rootKey in rootKeys)
        {
            var node = ctx.Content.GetById(rootKey);
            if (node is not null
                && string.Equals(node.ContentType?.Alias, SettingsDoctypeAlias, StringComparison.OrdinalIgnoreCase))
            {
                return node.Key;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the existing Settings content node, or creates one if the
    /// site genuinely has none. Three branches in order:
    /// <list type="number">
    ///   <item>Published cache walk — cheap; matches the resolver's path.</item>
    ///   <item>Draft scan over root children — catches Save-then-Publish-failed orphans
    ///         and uSync-imported-but-never-published drafts (the published
    ///         cache misses both, and creating without checking would pile up
    ///         duplicate root nodes on every PUT).</item>
    ///   <item>Create at root — only when neither cache nor draft scan
    ///         finds an existing node. Parent <c>-1</c> is intentional: the
    ///         <c>llmsSettings</c> doctype is by-design global per Umbraco
    ///         install, gated by <see cref="AuthorizationPolicies.SectionAccessSettings"/>;
    ///         editor content-tree start-node restrictions are not the right
    ///         gate for a section-level configuration node.</item>
    /// </list>
    /// </summary>
    private IContent? ResolveOrCreateSettingsContent()
    {
        // 1. Try the published-cache walk (cheap, identical to resolver's path).
        var existingKey = TryFindSettingsNodeKey();
        if (existingKey is Guid publishedKey)
        {
            var fromPublished = _contentService.GetById(publishedKey);
            if (fromPublished is not null)
            {
                return fromPublished;
            }
            // Published cache pointed at a deleted/recycled node — fall through
            // to the draft scan rather than 400ing with a misleading "doctype
            // not installed" error.
        }

        // 2. Scan root drafts directly. The published navigation index excludes
        // unpublished drafts (Save-then-Publish-failed orphans, uSync-imported-
        // but-never-published nodes). Without this, every PUT would create a
        // sibling root node and pile up duplicates.
        var rootDraft = FindDraftSettingsNodeAtRoot();
        if (rootDraft is not null)
        {
            return rootDraft;
        }

        // 3. No existing node anywhere — create one. Doctype must exist.
        var contentType = _contentTypeService.Get(SettingsDoctypeAlias);
        if (contentType is null)
        {
            return null;
        }

        return _contentService.Create(DefaultSettingsNodeName, parentId: -1, SettingsDoctypeAlias);
    }

    /// <summary>
    /// Pages root-level content (parent <c>-1</c>) and returns the first node
    /// whose content-type alias is <c>llmsSettings</c>. Catches drafts that
    /// the published cache cannot see. Bounded by site root-fan-out
    /// (<c>RootChildPageSize</c> per page; most installs have &lt; 200 root
    /// children).
    /// </summary>
    private IContent? FindDraftSettingsNodeAtRoot()
    {
        const int rootChildPageSize = 200;
        var pageIndex = 0L;
        long total;
        do
        {
            var batch = _contentService.GetPagedChildren(
                id: -1,
                pageIndex: pageIndex,
                pageSize: rootChildPageSize,
                totalRecords: out total);

            foreach (var child in batch)
            {
                if (child is not null
                    && string.Equals(
                        child.ContentType?.Alias,
                        SettingsDoctypeAlias,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return child;
                }
            }
            pageIndex++;
        } while (pageIndex * rootChildPageSize < total);

        return null;
    }

    private void CollectExcludedDescendants(
        Guid rootKey,
        IPublishedContentCache content,
        List<IPublishedContent> matches,
        CancellationToken cancellationToken)
    {
        var root = content.GetById(rootKey);
        if (root is not null && PageIsExcluded(root))
        {
            matches.Add(root);
        }

        if (!_navigation.TryGetDescendantsKeys(rootKey, out var descendantKeys))
        {
            return;
        }

        foreach (var descendantKey in descendantKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = content.GetById(descendantKey);
            if (node is not null && PageIsExcluded(node))
            {
                matches.Add(node);
            }
        }
    }

    private static bool PageIsExcluded(IPublishedContent page)
    {
        var prop = page.GetProperty(ExcludeFromLlmExportsAlias);
        if (prop is null)
        {
            return false;
        }

        var value = prop.GetValue();
        return value is bool b && b;
    }

    private static UpdateRequestValidation ValidateUpdateRequest(LlmsSettingsUpdateRequest? request)
    {
        if (request is null)
        {
            return UpdateRequestValidation.Failed("Request body is required.");
        }

        var siteName = request.SiteName;
        if (siteName is not null && siteName.Length > SiteNameMaxChars)
        {
            return UpdateRequestValidation.Failed(
                $"siteName cannot exceed {SiteNameMaxChars} characters (received {siteName.Length}).");
        }

        var summary = request.SiteSummary;
        if (summary is not null && summary.Length > SiteSummaryMaxChars)
        {
            return UpdateRequestValidation.Failed(
                $"siteSummary cannot exceed {SiteSummaryMaxChars} characters (received {summary.Length}).");
        }

        var aliases = request.ExcludedDoctypeAliases ?? Array.Empty<string>();
        if (aliases.Count > ExcludedAliasesMaxCount)
        {
            return UpdateRequestValidation.Failed(
                $"excludedDoctypeAliases cannot exceed {ExcludedAliasesMaxCount} entries (received {aliases.Count}).");
        }

        var sanitised = new List<string>(aliases.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in aliases)
        {
            if (alias is null || string.IsNullOrWhiteSpace(alias))
            {
                return UpdateRequestValidation.Failed(
                    "excludedDoctypeAliases cannot contain empty or whitespace-only entries.");
            }
            // Reject characters the resolver uses as separators
            // (DefaultSettingsResolver splits on \n / \r / , / ;) — an
            // unescaped separator inside an alias would round-trip as multiple
            // entries (or zero, when the entry is the separator itself).
            if (alias.IndexOfAny(AliasSeparatorChars) >= 0)
            {
                return UpdateRequestValidation.Failed(
                    "excludedDoctypeAliases entries cannot contain newline, carriage-return, comma, or semicolon characters.");
            }
            var trimmed = alias.Trim();
            if (!seen.Add(trimmed))
            {
                return UpdateRequestValidation.Failed(
                    $"excludedDoctypeAliases cannot contain duplicate entries (case-insensitive). Duplicate: '{trimmed}'.");
            }
            sanitised.Add(trimmed);
        }

        return UpdateRequestValidation.Ok(new LlmsSettingsUpdateRequest(
            SiteName: string.IsNullOrWhiteSpace(siteName) ? null : siteName,
            SiteSummary: string.IsNullOrWhiteSpace(summary) ? null : summary,
            ExcludedDoctypeAliases: sanitised));
    }

    private readonly struct UpdateRequestValidation
    {
        public bool IsInvalid { get; }
        public string? Detail { get; }
        public LlmsSettingsUpdateRequest? Sanitised { get; }

        private UpdateRequestValidation(bool isInvalid, string? detail, LlmsSettingsUpdateRequest? sanitised)
        {
            IsInvalid = isInvalid;
            Detail = detail;
            Sanitised = sanitised;
        }

        public static UpdateRequestValidation Ok(LlmsSettingsUpdateRequest sanitised)
            => new(false, null, sanitised);

        public static UpdateRequestValidation Failed(string detail)
            => new(true, detail, null);
    }
}
