namespace LlmsTxt.Umbraco.Builders;

/// <summary>
/// Public extension point for the <c>/llms.txt</c> manifest body. Adopters who want
/// to override the manifest shape register their own implementation via
/// <c>services.TryAddTransient&lt;ILlmsTxtBuilder, MyBuilder&gt;()</c> BEFORE the
/// package's <see cref="Composers.BuildersComposer"/> runs, OR via
/// <c>services.AddTransient&lt;ILlmsTxtBuilder, MyBuilder&gt;()</c> in a composer
/// annotated with <c>[ComposeAfter(typeof(BuildersComposer))]</c> — DI's
/// last-registration-wins semantics handle either.
/// <para>
/// The default implementation (<see cref="DefaultLlmsTxtBuilder"/>) walks the
/// published content cache under <see cref="LlmsTxtBuilderContext.RootContent"/>,
/// applies the configured <see cref="Configuration.LlmsTxtBuilderSettings.SectionGrouping"/>,
/// and emits an llmstxt.org-spec-compliant body. Adopter overrides return whatever
/// string they prefer; the controller does not validate the body shape against
/// the spec (that is the adopter's responsibility once they override).
/// </para>
/// <para>
/// Adopter handler exceptions are caught by <see cref="Controllers.LlmsTxtController"/>
/// and surfaced as <c>500 ProblemDetails</c> — the controller never lets adopter
/// exceptions break the response pipeline (per project-context.md § Critical
/// Don't-Miss Rules).
/// </para>
/// <para>
/// Lifetime: <b>transient</b>. The implementation is logically stateless across
/// calls (per-request state flows through <see cref="LlmsTxtBuilderContext"/>),
/// but the default builder pulls <see cref="Extraction.IMarkdownContentExtractor"/>
/// (transient) whose decorator factory pulls scoped
/// <c>IOptionsSnapshot&lt;AiVisibilitySettings&gt;</c>. A Singleton builder would form
/// a captive dependency on the scoped options snapshot — registering as Transient
/// matches the extractor's lifetime. See <see cref="Composers.BuildersComposer"/>
/// for the full rationale (and Story 2.1 Spec Drift Note #7 — this is a
/// deliberate deviation from the architecture's "Singleton when stateless" rule
/// because the dependency graph carries scoped state).
/// </para>
/// </summary>
public interface ILlmsTxtBuilder
{
    /// <summary>
    /// Build the <c>/llms.txt</c> body for the given context.
    /// </summary>
    /// <param name="context">
    /// Resolved hostname, culture, root content, and settings snapshot. Never null.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token the controller propagates from <c>HttpContext.RequestAborted</c>.
    /// Implementations should pass this through to any awaited operations
    /// (e.g. <c>IMarkdownContentExtractor.ExtractAsync</c> for the per-page summary
    /// fallback).
    /// </param>
    /// <returns>
    /// The full manifest body as a string. The caller writes this verbatim to the
    /// HTTP response with <c>Content-Type: text/markdown; charset=utf-8</c>.
    /// </returns>
    Task<string> BuildAsync(LlmsTxtBuilderContext context, CancellationToken cancellationToken);
}
