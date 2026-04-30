namespace LlmsTxt.Umbraco.Builders;

/// <summary>
/// Public extension point for the <c>/llms-full.txt</c> manifest body (Story 2.2).
/// Adopters who want to override the manifest shape register their own
/// implementation via
/// <c>services.TryAddTransient&lt;ILlmsFullBuilder, MyBuilder&gt;()</c> BEFORE the
/// package's <see cref="Composers.BuildersComposer"/> runs, OR via
/// <c>services.AddTransient&lt;ILlmsFullBuilder, MyBuilder&gt;()</c> in a composer
/// annotated with <c>[ComposeAfter(typeof(BuildersComposer))]</c> — DI's
/// last-registration-wins semantics handle either.
/// <para>
/// The default implementation (<see cref="DefaultLlmsFullBuilder"/>) iterates the
/// pre-collected <see cref="LlmsFullBuilderContext.Pages"/> in the configured
/// <see cref="Configuration.LlmsFullBuilderSettings.Order"/>, calls
/// <see cref="Extraction.IMarkdownContentExtractor.ExtractAsync"/> per page, strips
/// per-page YAML frontmatter, prefixes each section with
/// <c># {Title}\n\n_Source: {absolute URL}_\n\n</c>, joins with
/// <c>\n\n---\n\n</c> separators, and enforces
/// <see cref="Configuration.LlmsTxtSettings.MaxLlmsFullSizeKb"/> with a stable
/// truncation footer when the cap is hit.
/// </para>
/// <para>
/// Adopter handler exceptions are caught by
/// <see cref="Controllers.LlmsFullTxtController"/> and surfaced as <c>500
/// ProblemDetails</c> — the controller never lets adopter exceptions break the
/// response pipeline (per project-context.md § Critical Don't-Miss Rules).
/// </para>
/// <para>
/// Lifetime: <b>transient</b>. The implementation is logically stateless across
/// calls (per-request state flows through <see cref="LlmsFullBuilderContext"/>),
/// but the default builder pulls <see cref="Extraction.IMarkdownContentExtractor"/>
/// (transient) whose decorator factory pulls scoped
/// <c>IOptionsSnapshot&lt;LlmsTxtSettings&gt;</c>. A Singleton builder would form
/// a captive dependency on the scoped options snapshot — registering as Transient
/// matches the extractor's lifetime. See <see cref="Composers.BuildersComposer"/>
/// for the full rationale (and Story 2.1 Spec Drift Note #7 — this is a deliberate
/// deviation from the architecture's "Singleton when stateless" rule because the
/// dependency graph carries scoped state). The same trade-off applies to
/// <see cref="ILlmsTxtBuilder"/>; both registrations are Transient.
/// </para>
/// </summary>
public interface ILlmsFullBuilder
{
    /// <summary>
    /// Build the <c>/llms-full.txt</c> body for the given context.
    /// </summary>
    /// <param name="context">
    /// Resolved hostname, culture, root content, scope-filtered page list, and
    /// settings snapshot. Never null. The controller has already applied
    /// <see cref="Configuration.LlmsFullScopeSettings"/> filtering before handing
    /// the context to the builder; default builders consume
    /// <see cref="LlmsFullBuilderContext.Pages"/> verbatim.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token the controller propagates from
    /// <c>HttpContext.RequestAborted</c>. Implementations should pass this through
    /// to any awaited operations (e.g.
    /// <c>IMarkdownContentExtractor.ExtractAsync</c> for each page in the loop).
    /// </param>
    /// <returns>
    /// The full manifest body as a string. The caller writes this verbatim to the
    /// HTTP response with <c>Content-Type: text/markdown; charset=utf-8</c>.
    /// </returns>
    Task<string> BuildAsync(LlmsFullBuilderContext context, CancellationToken cancellationToken);
}
