using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Web.Common.ModelBinders;
using Umbraco.Community.AiVisibility.Configuration;

namespace Umbraco.Community.AiVisibility.Extraction;

/// <summary>
/// Story 7.3 — Auto page-rendering strategy. Tries
/// <see cref="RazorPageRendererStrategy"/> first; on
/// <see cref="ModelBindingException"/> falls back to
/// <see cref="LoopbackPageRendererStrategy"/> and caches the per-tuple
/// decision so subsequent renders of the same
/// <c>(ContentTypeAlias, TemplateAlias)</c> combination skip Razor entirely.
/// The fallback warning log fires exactly once per tuple (gated on
/// <see cref="IRendererStrategyCache.MarkHijacked"/>'s insert-or-already-present
/// return); concurrent first-encounter bursts on the same tuple may fire the
/// Razor attempt more than once before any of them populates the cache, but
/// each wasted attempt is bounded by <see cref="ModelBindingException"/> cost
/// and the operator-visible log signal stays exactly-once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composition via captured <see cref="IServiceProvider"/>.</b> Resolves
/// the two sibling strategies through keyed-service lookups on the captured
/// provider — the same primitive <c>PageRenderer</c>'s orchestrator uses.
/// Both <c>RazorPageRendererStrategy</c> and
/// <c>LoopbackPageRendererStrategy</c> are registered with
/// <c>Transient</c> lifetime and have no captive scoped dependencies (their
/// <c>Compose_StartupValidation_*_NoCaptiveDependency</c> tests pin the
/// invariant), so root-provider resolution is safe.
/// </para>
/// <para>
/// <b>Narrow fallback trigger.</b> Only <see cref="ModelBindingException"/>
/// triggers fallback to Loopback — every other Razor failure propagates as
/// the original <c>PageRenderResult.Failed</c>. The trigger list is
/// intentionally narrow and documented; widen via filed issue + observed
/// evidence, never speculatively. A <c>NullReferenceException</c> in a
/// misconfigured Razor template should surface, NOT trigger Loopback which
/// would hide the root cause.
/// </para>
/// <para>
/// <b>Cache key tuple <c>(ContentTypeAlias, TemplateAlias)</c>.</b>
/// Distinguishing by template alias (not just by doctype alias) prevents
/// over-caching mixed-template doctypes — a doctype with two templates where
/// only the default is hijacked must NOT mark the alternate template's
/// render as hijacked. The template alias is resolved via
/// <c>ITemplateService.GetAsync(content.TemplateId)</c>; when the alias
/// cannot be resolved (no template selected, deleted template, etc.) the
/// fallback is <see cref="string.Empty"/> + a Debug log line so the
/// missing-template path stays observable.
/// </para>
/// <para>
/// <b>Cache-write-on-failure-only.</b> Successful Razor renders do NOT
/// write to the cache; the absence-of-entry semantic is "try Razor". A
/// <c>true</c> entry means "skip Razor, go to Loopback". This keeps the
/// cache size bounded to the count of <c>(doctype × hijacked template)</c>
/// combinations on the site.
/// </para>
/// <para>
/// <b>Lifetime: Transient.</b> Matches the
/// <see cref="RazorPageRendererStrategy"/> /
/// <see cref="LoopbackPageRendererStrategy"/> precedent. The strategy holds
/// no per-render mutable state, but the captive-IServiceProvider safety
/// constraint pinned by <see cref="IPageRendererStrategy"/> remarks means
/// Singleton would require per-class justification + a
/// <c>NoCaptiveDependency</c> validation test. Transient is the safest
/// default.
/// </para>
/// </remarks>
internal sealed class AutoPageRendererStrategy : IPageRendererStrategy
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IRendererStrategyCache _cache;
    private readonly ITemplateService _templateService;
    private readonly ILogger<AutoPageRendererStrategy> _logger;

    public AutoPageRendererStrategy(
        IServiceProvider serviceProvider,
        IRendererStrategyCache cache,
        ITemplateService templateService,
        ILogger<AutoPageRendererStrategy> logger)
    {
        _serviceProvider = serviceProvider;
        _cache = cache;
        _templateService = templateService;
        _logger = logger;
    }

    public async Task<PageRenderResult> RenderAsync(
        IPublishedContent content,
        Uri absoluteUri,
        string? culture,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Derive the (ContentTypeAlias, TemplateAlias) cache key. Template
        //    alias mirrors the Razor strategy's lookup (TemplateId > 0 →
        //    ITemplateService.GetAsync(id).Alias); the architecture-mandated
        //    fallback for the no-template case is string.Empty plus a Debug
        //    log line so the case stays observable. The empty-string slot is
        //    a distinct cache key from any real template alias, which means
        //    a doctype's "no template" renders share a single cache entry
        //    that is independent of any of its templated-render entries.
        var contentTypeAlias = content.ContentType.Alias;
        var templateAlias = await ResolveTemplateAliasAsync(content);

        // 2a. Cache hit (hijacked) → skip Razor, go straight to Loopback.
        if (_cache.IsHijacked(contentTypeAlias, templateAlias))
        {
            var loopbackStrategy = ResolveLoopbackStrategy();
            return await loopbackStrategy.RenderAsync(content, absoluteUri, culture, cancellationToken);
        }

        // 2b. Cache hit (Razor permanently failed) → skip BOTH Razor and
        //     Loopback. A prior render of this tuple failed with a non-MBE
        //     error (no template, view not found, structural doctype with no
        //     renderable view, Razor compilation issue). Loopback would hit
        //     the same template/view through HTTP and fail identically, so
        //     trying it would be wasted work. The cached failure shape is a
        //     fresh PageRenderResult.Failed wrapping the per-process cache
        //     decision — callers (the manifest builder, the .md controller)
        //     handle this exactly the same way as a live render failure
        //     because the absent body and Failed status are what they react
        //     to, not the specific exception instance.
        if (_cache.IsRazorPermanentlyFailed(contentTypeAlias, templateAlias))
        {
            return PageRenderResult.Failed(
                new InvalidOperationException(
                    $"PageRenderer: Razor render for ({contentTypeAlias}, {templateAlias}) is cached as permanently-failed; skipping both Razor and Loopback. Restart the host to clear the cache after fixing the underlying view / template issue."),
                content,
                templateAlias,
                culture);
        }

        // 3. Cache miss → try Razor first.
        var razorStrategy = ResolveRazorStrategy();
        var razorResult = await razorStrategy.RenderAsync(content, absoluteUri, culture, cancellationToken);

        // 4. Razor succeeded → return verbatim. NO cache write on success
        //    (absence-of-entry implicitly means "try Razor"; writing "false"
        //    would change semantics from "decision made" to "checked it").
        if (razorResult.Status == PageRenderStatus.Ok)
        {
            return razorResult;
        }

        // 5. Razor failed → ModelBindingException is the hijack trigger.
        //    Other exception types are cached as permanently-failed (step 6b)
        //    so the same tuple doesn't re-attempt Razor on every render — a
        //    structural doctype with no renderable template, for example,
        //    would otherwise log a Razor failure for every page sharing its
        //    (doctype, template) tuple every time the manifest builder
        //    iterated them.
        //
        //    The pattern below matches the unwrapped form — verified against
        //    a real custom-view-model hijack on an agency-built site (a
        //    RenderController subclass binding a non-IPublishedContent view
        //    model to the template). The surfaced exception is the bare
        //    Umbraco.Cms.Web.Common.ModelBinders.ModelBindingException; no
        //    wrapping observed. If a future Razor pipeline change wraps the
        //    exception in TargetInvocationException / AggregateException /
        //    etc., adopters using `Mode = Auto` would observe the wrapped
        //    page falling into the permanently-failed cache path (6b)
        //    instead of the Loopback fallback. Pin `Mode = Razor` to revert
        //    and file an issue with the exception type from logs so the
        //    trigger can be widened with evidence.
        if (razorResult.Error is ModelBindingException)
        {
            // 6a. ModelBindingException → cache as hijacked + try Loopback.
            //     Cache is marked BEFORE the Loopback call, so a subsequent
            //     Loopback failure (or cancellation) still leaves the cache
            //     populated for future renders. The warning log is gated on
            //     MarkHijacked's return so it fires exactly once per tuple
            //     even when concurrent first-encounter bursts race past the
            //     cache-hit check at step 2a.
            var newlyMarked = _cache.MarkHijacked(contentTypeAlias, templateAlias);
            if (newlyMarked)
            {
                _logger.LogWarning(
                    razorResult.Error,
                    "PageRenderer: Razor → Loopback fallback fired for {Alias} {ContentKey} {Path}",
                    contentTypeAlias,
                    content.Key,
                    absoluteUri.AbsolutePath);
            }

            var loopbackOnFallback = ResolveLoopbackStrategy();
            return await loopbackOnFallback.RenderAsync(content, absoluteUri, culture, cancellationToken);
        }

        // 6b. Non-MBE Razor failure → cache as permanently-failed and return
        //     the original failure. Subsequent renders of this tuple
        //     short-circuit at step 2b and return a fresh "cached as
        //     permanently-failed" failure without re-attempting Razor or
        //     Loopback. Loopback isn't tried because it would HTTP-fetch the
        //     same content through the same Razor view engine and hit the
        //     same failure — wasted work that would compound the log noise.
        //     The warning fires exactly-once per tuple (gated on
        //     MarkRazorPermanentlyFailed's return) so the operator-visible
        //     signal stays bounded under bulk-export iteration.
        var newlyMarkedFailed = _cache.MarkRazorPermanentlyFailed(contentTypeAlias, templateAlias);
        if (newlyMarkedFailed)
        {
            _logger.LogWarning(
                razorResult.Error,
                "PageRenderer: caching Razor render as permanently-failed for {Alias}/{Template} {ContentKey} {Path}. Subsequent renders of this tuple will skip both Razor and Loopback until host restart.",
                contentTypeAlias,
                templateAlias,
                content.Key,
                absoluteUri.AbsolutePath);
        }

        return razorResult;
    }

    private async Task<string> ResolveTemplateAliasAsync(IPublishedContent content)
    {
        if (content.TemplateId is int templateId && templateId > 0)
        {
            // ITemplateService.GetAsync(int) is the parameterless-token API
            // (no CancellationToken overload exists in v17.3.2); same
            // overload RazorPageRendererStrategy uses. Umbraco hot-caches
            // templates internally so the call is effectively free.
            var template = await _templateService.GetAsync(templateId);
            if (template is not null)
            {
                // Defensive ?? string.Empty: ITemplate.Alias is interface-
                // annotated non-null but Umbraco interface contracts are
                // routinely lax at runtime; a null alias here would propagate
                // as a (doctype, null) tuple into the cache against the
                // IRendererStrategyCache non-null contract.
                return template.Alias ?? string.Empty;
            }
        }

        _logger.LogDebug(
            "PageRenderer: Auto strategy cache key uses empty TemplateAlias for {ContentKey} (TemplateId={TemplateId})",
            content.Key,
            content.TemplateId);
        return string.Empty;
    }

    private IPageRendererStrategy ResolveRazorStrategy()
        => _serviceProvider.GetRequiredKeyedService<IPageRendererStrategy>(RenderStrategyMode.Razor);

    private IPageRendererStrategy ResolveLoopbackStrategy()
        => _serviceProvider.GetRequiredKeyedService<IPageRendererStrategy>(RenderStrategyMode.Loopback);
}
