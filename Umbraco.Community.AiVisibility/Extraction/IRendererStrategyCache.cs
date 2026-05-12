namespace Umbraco.Community.AiVisibility.Extraction;

/// <summary>
/// Per-<c>(ContentTypeAlias, TemplateAlias)</c> render-decision store consumed
/// by <see cref="AutoPageRendererStrategy"/>. A cached entry signals one of two
/// outcomes from a prior render attempt under the Razor strategy:
/// <list type="bullet">
/// <item><see cref="IsHijacked"/> — Razor failed with
/// <c>Umbraco.Cms.Web.Common.ModelBinders.ModelBindingException</c> (custom
/// view-model hijack). Subsequent renders skip Razor and go straight to the
/// Loopback strategy.</item>
/// <item><see cref="IsRazorPermanentlyFailed"/> — Razor failed with a
/// non-recoverable, non-MBE error (no template, view not found, structural
/// doctype with no renderable view, Razor compilation issue). Subsequent
/// renders skip BOTH Razor and Loopback and return the failure directly —
/// Loopback would hit the same template/view through HTTP and fail identically,
/// so trying it would be wasted work.</item>
/// </list>
/// Absence-of-entry implicitly means "try Razor" — the cache stores decisions,
/// not lookup-cache entries.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime: Singleton.</b> The cache is shared across requests for the
/// life of the host process; a Transient cache would defeat the purpose.
/// Default registration is <c>services.TryAddSingleton&lt;IRendererStrategyCache,
/// RendererStrategyCache&gt;()</c>; adopters who want a custom store register
/// their own implementation via the same <c>TryAddSingleton</c> pattern
/// (consistent with the package's existing extension-point discipline).
/// </para>
/// <para>
/// <b>Cache key tuple <c>(ContentTypeAlias, TemplateAlias)</c>.</b>
/// Distinguishing by template alias (not just by doctype alias) is the
/// load-bearing invariant that prevents over-caching mixed-template
/// doctypes — a doctype with two templates where only the default is
/// hijacked must NOT mark the alternate template's render as hijacked. The
/// caller (<see cref="AutoPageRendererStrategy"/>) resolves the template
/// alias via <c>ITemplateService.GetAsync</c>; when the alias cannot be
/// resolved (no template selected, deleted template, etc.) the caller
/// substitutes <see cref="System.String.Empty"/> for the second slot and
/// emits a Debug log line so the missing-template path stays observable.
/// </para>
/// <para>
/// <b>One-decision-per-tuple-per-process.</b> The first render's outcome
/// determines the cached decision; subsequent first-mark calls are state
/// no-ops. Once a tuple is cached as <see cref="IsHijacked"/>, it stays
/// hijacked; once cached as <see cref="IsRazorPermanentlyFailed"/>, it stays
/// permanently failed. There is no transition between the two states —
/// transient errors that resolve themselves are NOT re-evaluated. Process
/// restart is the natural invalidation event (consistent with the
/// hijack-cache rationale below).
/// </para>
/// <para>
/// <b>Process-lifetime semantics, no invalidation hook.</b> Hijack
/// registration is determined by adopter code (<c>RenderController</c>
/// subclasses + template assignment) — that's a deploy-time concern.
/// Process restart is the natural invalidation event; no
/// <c>ContentCacheRefresherNotification</c> handler is wired because content
/// edits do not change which <c>(doctype, template)</c> combinations are
/// hijacked. Adopters who remove a hijack and want to verify Razor-path
/// performance restart the host to clear the cache. Production deployments
/// restart on each release, so the staleness window is bounded by deploy
/// cadence.
/// </para>
/// <para>
/// <b>Idempotent mark methods.</b> Second calls with the same tuple are
/// state no-ops (the underlying <c>ConcurrentDictionary.TryAdd</c> returns
/// <c>false</c>). The methods' <c>bool</c> return distinguishes
/// "first insert" from "already marked" so callers can gate exactly-once
/// side effects (e.g. a one-time warning log) on the result. Callers MAY
/// call mark methods without checking first.
/// </para>
/// </remarks>
internal interface IRendererStrategyCache
{
    /// <summary>
    /// Returns <c>true</c> when the <c>(contentTypeAlias, templateAlias)</c>
    /// tuple has been marked hijacked by a prior <see cref="MarkHijacked"/>
    /// call.
    /// </summary>
    /// <param name="contentTypeAlias">The published content type alias.
    /// Non-null; pass empty string for the no-content-type case (the
    /// dispatcher does not currently surface a null content-type, but the
    /// signature is defensive).</param>
    /// <param name="templateAlias">The template alias. Non-null; pass
    /// <see cref="System.String.Empty"/> when no template is available per
    /// the no-template fallback rule.</param>
    bool IsHijacked(string contentTypeAlias, string templateAlias);

    /// <summary>
    /// Marks the <c>(contentTypeAlias, templateAlias)</c> tuple as hijacked
    /// so that subsequent <see cref="IsHijacked"/> calls return <c>true</c>.
    /// Idempotent — a second call with the same tuple has no observable
    /// effect on cache state.
    /// </summary>
    /// <param name="contentTypeAlias">The published content type alias.
    /// Non-null.</param>
    /// <param name="templateAlias">The template alias. Non-null; pass
    /// <see cref="System.String.Empty"/> when no template is available.</param>
    /// <returns><c>true</c> when this call inserted the entry; <c>false</c>
    /// when the tuple was already marked (either as hijacked or as
    /// permanently failed). Callers can use this to gate exactly-once side
    /// effects (such as a one-time warning log) so the observable signal stays
    /// bounded even under concurrent first-encounter bursts.</returns>
    bool MarkHijacked(string contentTypeAlias, string templateAlias);

    /// <summary>
    /// Returns <c>true</c> when the <c>(contentTypeAlias, templateAlias)</c>
    /// tuple has been marked permanently failed by a prior
    /// <see cref="MarkRazorPermanentlyFailed"/> call. A cached
    /// "permanently failed" tuple signals that Razor failed with a non-MBE
    /// error and Loopback would hit the same template/view via HTTP and fail
    /// identically — so the Auto strategy skips BOTH paths and returns the
    /// cached failure.
    /// </summary>
    /// <param name="contentTypeAlias">The published content type alias.
    /// Non-null.</param>
    /// <param name="templateAlias">The template alias. Non-null; pass
    /// <see cref="System.String.Empty"/> when no template is available.</param>
    bool IsRazorPermanentlyFailed(string contentTypeAlias, string templateAlias);

    /// <summary>
    /// Marks the <c>(contentTypeAlias, templateAlias)</c> tuple as permanently
    /// failed under the Razor strategy. Subsequent
    /// <see cref="IsRazorPermanentlyFailed"/> calls return <c>true</c>.
    /// Idempotent — a second call with the same tuple has no observable
    /// effect on cache state, and a call with a tuple already marked
    /// <see cref="IsHijacked"/> is also a no-op (the hijacked decision wins).
    /// </summary>
    /// <param name="contentTypeAlias">The published content type alias.
    /// Non-null.</param>
    /// <param name="templateAlias">The template alias. Non-null; pass
    /// <see cref="System.String.Empty"/> when no template is available.</param>
    /// <returns><c>true</c> when this call inserted the entry; <c>false</c>
    /// when the tuple was already marked (either as hijacked or as permanently
    /// failed). Callers can use this to gate exactly-once side effects (such
    /// as a one-time warning log).</returns>
    bool MarkRazorPermanentlyFailed(string contentTypeAlias, string templateAlias);
}
