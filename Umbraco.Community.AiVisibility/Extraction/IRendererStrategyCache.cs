namespace Umbraco.Community.AiVisibility.Extraction;

/// <summary>
/// Story 7.3 — per-<c>(ContentTypeAlias, TemplateAlias)</c> hijack-decision
/// store consumed by <see cref="AutoPageRendererStrategy"/>. A
/// <c>true</c> entry means the prior render of that
/// <c>(doctype, template)</c> tuple failed with
/// <c>Umbraco.Cms.Web.Common.ModelBinders.ModelBindingException</c> under the
/// Razor strategy, so subsequent renders skip Razor and go straight to the
/// Loopback strategy. Absence-of-entry implicitly means "try Razor" — the
/// cache stores decisions, not lookup-cache entries.
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
/// <b>Idempotent <see cref="MarkHijacked"/>.</b> A second call with the same
/// tuple is a state no-op (the underlying <c>ConcurrentDictionary.TryAdd</c>
/// returns <c>false</c>). The method's <c>bool</c> return distinguishes
/// "first insert" from "already marked" so callers can gate exactly-once
/// side effects (e.g. a one-time warning log) on the result. Callers MAY
/// call <see cref="MarkHijacked"/> without checking <see cref="IsHijacked"/>
/// first.
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
    /// when the tuple was already marked. Callers can use this to gate
    /// exactly-once side effects (such as a one-time warning log) so the
    /// observable signal stays bounded even under concurrent first-encounter
    /// bursts.</returns>
    bool MarkHijacked(string contentTypeAlias, string templateAlias);
}
