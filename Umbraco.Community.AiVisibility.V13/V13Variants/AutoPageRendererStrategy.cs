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
/// <b>Narrow fallback trigger.</b> Only failures classified as view-model
/// hijacks by <see cref="IsViewModelHijack"/> trigger fallback to Loopback
/// — every other Razor failure propagates as the original
/// <c>PageRenderResult.Failed</c>. The predicate matches two unwrapped
/// shapes (model-binding-layer + view-activation-layer); see the helper's
/// own xmldoc for the canonical phrases / stack-frame tokens. The trigger
/// list is intentionally narrow and documented; widen via filed issue +
/// observed evidence, never speculatively. A <c>NullReferenceException</c>
/// in a misconfigured Razor template should surface, NOT trigger Loopback
/// which would hide the root cause.
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
    // v13 variant (Story 9.2): ITemplateService is v14+ — v13 uses IFileService
    // for template lookup (see Spec Drift SDN-1).
    private readonly IFileService _fileService;
    private readonly ILogger<AutoPageRendererStrategy> _logger;

    public AutoPageRendererStrategy(
        IServiceProvider serviceProvider,
        IRendererStrategyCache cache,
        IFileService fileService,
        ILogger<AutoPageRendererStrategy> logger)
    {
        _serviceProvider = serviceProvider;
        _cache = cache;
        _fileService = fileService;
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

        // 5. Razor failed → IsViewModelHijack decides whether this is a
        //    view-model hijack we can route around via Loopback, or a
        //    non-hijack failure cached as permanently-failed (step 6b) so
        //    the same tuple doesn't re-attempt Razor on every render — a
        //    structural doctype with no renderable template, for example,
        //    would otherwise log a Razor failure for every page sharing
        //    its (doctype, template) tuple every time the manifest builder
        //    iterated them.
        //
        //    The predicate matches two unwrapped shapes — verified against
        //    real agency-site hijacks: the model-binding-layer hijack
        //    (Umbraco.Cms.Web.Common.ModelBinders.ModelBindingException)
        //    and the view-activation-layer hijack (an
        //    InvalidOperationException raised by ASP.NET Core's
        //    RazorPagePropertyActivator when a RenderController subclass
        //    returns CurrentTemplate(customViewModel) against a view
        //    declared @model CustomViewModel). See IsViewModelHijack
        //    xmldoc for the canonical Message + StackTrace tokens. If a
        //    future Razor pipeline change wraps either exception in
        //    TargetInvocationException / AggregateException / etc.,
        //    adopters using `Mode = Auto` would observe the wrapped page
        //    falling into the permanently-failed cache path (6b) instead
        //    of the Loopback fallback. Pin `Mode = Razor` to revert and
        //    file an issue with the exception type from logs so the
        //    trigger can be widened with evidence.
        if (IsViewModelHijack(razorResult.Error))
        {
            // 6a. View-model hijack → cache as hijacked + try Loopback.
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

        // 6b. Non-hijack Razor failure → cache as permanently-failed and
        //     return the original failure. Hijack failures (handled at
        //     step 6a) ARE retried via Loopback because the loopback HTTP
        //     request traverses the agency's hijack controller, which
        //     constructs whatever shape the view expects; non-hijack
        //     failures (missing view, Razor compile error, structural
        //     doctype with no renderable template) are the opposite — the
        //     HTTP path would re-enter the same view engine and hit the
        //     same failure, wasted work that would compound the log noise.
        //     Subsequent renders of this tuple short-circuit at step 2b
        //     and return a fresh "cached as permanently-failed" failure
        //     without re-attempting Razor or Loopback. The warning fires
        //     exactly-once per tuple (gated on
        //     MarkRazorPermanentlyFailed's return) so the operator-visible
        //     signal stays bounded under bulk-export iteration. Widening
        //     the hijack trigger (step 6a / IsViewModelHijack) requires
        //     filed issue + observed evidence, never speculation — a
        //     NullReferenceException in a misconfigured Razor template
        //     must surface, NOT silently fall through to Loopback which
        //     would hide the root cause.
        //
        //     Null-Error guard: a Failed result with no wrapped exception
        //     would otherwise reach this branch and cache the tuple as
        //     permanently-failed against a null exception — better to skip
        //     the cache write and return the result unchanged. PageRenderResult
        //     contract requires Error on Failed, but the guard is defensive
        //     against alternate IPageRendererStrategy implementations.
        if (razorResult.Error is null)
        {
            return razorResult;
        }

        var newlyMarkedFailed = _cache.MarkRazorPermanentlyFailed(contentTypeAlias, templateAlias);
        if (newlyMarkedFailed)
        {
            _logger.LogWarning(
                razorResult.Error,
                "PageRenderer: caching Razor render as permanently-failed for {Alias} {ContentKey} {Path}. Subsequent renders of this tuple will skip both Razor and Loopback until host restart.",
                contentTypeAlias,
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
            var template = _fileService.GetTemplate(templateId);
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

    /// <summary>
    /// Classifies a Razor render failure as a view-model hijack we can
    /// route around via Loopback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Shape A — model-binding-layer hijack:</b>
    /// <see cref="ModelBindingException"/>. Surfaced when Umbraco's model
    /// binders refuse to bind a non-<see cref="IPublishedContent"/> view
    /// model to the template. The original <c>Mode = Auto</c> trigger,
    /// verified against real agency-built sites.
    /// </para>
    /// <para>
    /// <b>Shape B — view-activation-layer hijack:</b>
    /// <see cref="InvalidOperationException"/> whose
    /// <see cref="Exception.Message"/> contains the canonical phrase
    /// <c>"ViewDataDictionary instance requires a model item of type"</c>
    /// AND whose <see cref="Exception.StackTrace"/> contains the literal
    /// token <c>"RazorPagePropertyActivator"</c>. Surfaced when an agency
    /// <c>RenderController</c> subclass returns
    /// <c>CurrentTemplate(customViewModel)</c> against a view declared
    /// <c>@model CustomViewModel</c> — the model binders accept the bound
    /// content but ASP.NET Core's view activator rejects the
    /// <c>ViewDataDictionary</c> type mismatch.
    /// </para>
    /// <para>
    /// <b>Brittleness contract.</b> The stack-frame literal-token match
    /// against <c>"RazorPagePropertyActivator"</c> is the canonical
    /// predicate for shape B. A future ASP.NET Core rename of that
    /// internal type will surface as fall-through to the
    /// <c>RazorPermanentlyFailed</c> path (fail-loud, NOT silent
    /// fall-through to Loopback). Widen via filed issue + observed
    /// evidence, never speculatively. A <c>NullReferenceException</c> in
    /// a misconfigured Razor template must surface, NOT trigger Loopback
    /// which would hide the root cause.
    /// </para>
    /// <para>
    /// <b>Belt-and-braces rationale.</b> The message-fragment match alone
    /// risks false positives on a future ASP.NET Core release that reuses
    /// the same phrasing in an unrelated diagnostic; the stack-frame
    /// check alone risks brittleness if Microsoft renames the activator
    /// class. Combined, the only thing satisfying both predicates is a
    /// genuine view-activation type mismatch.
    /// </para>
    /// <para>
    /// <b>String comparisons.</b> Both <c>Contains</c> calls use
    /// <see cref="StringComparison.Ordinal"/> — the canonical ASP.NET
    /// Core diagnostic text + the stack-frame type-name token are both
    /// case-sensitive in the real runtime output; ordinal comparison is
    /// also allocation-cheaper than the culture-sensitive overloads.
    /// </para>
    /// <para>
    /// <b>Null-safe.</b> Accepts <see cref="Exception"/>? so a
    /// <c>PageRenderResult.Failed</c> with a null <c>Error</c> composes
    /// safely: <c>IsViewModelHijack(null)</c> returns <c>false</c> and
    /// the existing null-Error guard in <see cref="RenderAsync"/>
    /// continues to short-circuit before any cache-write path is
    /// reached.
    /// </para>
    /// <para>
    /// <b>Cache implications across widening.</b>
    /// <see cref="IRendererStrategyCache"/> is per-process in-memory.
    /// Hosts that observed the view-activation-layer hijack BEFORE this
    /// predicate widened (and therefore cached it as
    /// <c>RazorPermanentlyFailed</c>) retain that classification until
    /// the host process restarts. A host restart is the canonical
    /// recovery path after a version upgrade that widens the trigger.
    /// </para>
    /// <para>
    /// <b>Cannot distinguish package-internal binder misconfiguration
    /// from genuine hijack.</b> Both shape A and shape B exception
    /// signatures can also surface when Umbraco's
    /// <c>ContentModelBinder</c> is unregistered or misconfigured (e.g.
    /// the host removed <c>AddUmbracoCms()</c>) — the package's own
    /// direct-view-render path then triggers the exact same exception
    /// shapes the predicate is widened to catch, and Loopback fallback
    /// silently masks the root cause. Adopters who see persistent
    /// <see cref="IRendererStrategyCache.MarkHijacked"/> writes without
    /// any actual <c>RenderController</c> hijack controller in their
    /// codebase should verify <c>AddUmbracoCms()</c> + Umbraco's
    /// content-model-binder registrations are present. Cheap diagnostic:
    /// pin <c>AiVisibility:RenderStrategy:Mode = Razor</c> temporarily
    /// to surface the underlying failure without Loopback masking.
    /// </para>
    /// </remarks>
    private static bool IsViewModelHijack(Exception? ex) =>
        ex is ModelBindingException
        || (ex is InvalidOperationException ioe
            && ioe.Message.Contains(
                "ViewDataDictionary instance requires a model item of type",
                StringComparison.Ordinal)
            && ioe.StackTrace?.Contains(
                "RazorPagePropertyActivator",
                StringComparison.Ordinal) == true);
}
