using LlmsTxt.Umbraco.Builders;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace LlmsTxt.Umbraco.Composers;

/// <summary>
/// Story 2.1 — registers the <c>/llms.txt</c> builder seam:
/// <list type="bullet">
/// <item><see cref="ILlmsTxtBuilder"/> via <c>TryAddTransient</c> so adopters can
/// override before this composer runs.</item>
/// <item><see cref="ILlmsFullBuilder"/> (Story 2.2) — same registration shape; the
/// captive-dependency rationale on <see cref="ILlmsTxtBuilder"/> applies
/// identically (the default builder pulls
/// <see cref="Extraction.IMarkdownContentExtractor"/> whose decorator factory
/// pulls scoped <c>IOptionsSnapshot&lt;LlmsTxtSettings&gt;</c>).</item>
/// <item><see cref="HostnameRootResolver"/> as a singleton helper consumed by both
/// controllers.</item>
/// </list>
/// <para>
/// <c>[ComposeAfter(typeof(RoutingComposer))]</c> guarantees the
/// <see cref="Extraction.IMarkdownContentExtractor"/> registration is in place
/// before <see cref="DefaultLlmsTxtBuilder"/> resolves it for the per-page
/// summary fallback. The dependency is on the <i>registration</i> being present,
/// not on a particular runtime ordering — DI's last-wins semantics handle either.
/// </para>
/// <para>
/// Adopter override discipline (mirrors Story 1.4's <c>IMarkdownContentExtractor</c>
/// pattern):
/// </para>
/// <list type="bullet">
/// <item><b>Override BEFORE us:</b> register <c>services.TryAddSingleton&lt;ILlmsTxtBuilder, MyBuilder&gt;()</c>
/// in a composer with no ordering constraint (or <c>[ComposeBefore(typeof(BuildersComposer))]</c>).
/// Our <c>TryAdd*</c> notices the existing registration and bows out.</item>
/// <item><b>Override AFTER us:</b> register <c>services.AddSingleton&lt;ILlmsTxtBuilder, MyBuilder&gt;()</c>
/// in a composer with <c>[ComposeAfter(typeof(BuildersComposer))]</c>. DI
/// last-registration-wins ensures the adopter's instance is resolved.</item>
/// </list>
/// </summary>
[ComposeAfter(typeof(RoutingComposer))]
public sealed class BuildersComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        // ILlmsTxtBuilder is logically stateless, but DefaultLlmsTxtBuilder pulls
        // IMarkdownContentExtractor (transient — DefaultMarkdownContentExtractor
        // depends on scoped IOptionsSnapshot<LlmsTxtSettings>). Registering the
        // builder as Singleton would capture a transient dependency whose root-
        // scope resolution throws "Cannot resolve … from root provider because
        // it requires scoped service IOptionsSnapshot<LlmsTxtSettings>". Transient
        // matches the extractor's lifetime; the builder holds no per-request state
        // of its own. Architecture's "Singleton when stateless and thread-safe"
        // (line 374) doesn't apply when the dependency graph carries scoped state.
        builder.Services.TryAddTransient<ILlmsTxtBuilder, DefaultLlmsTxtBuilder>();

        // Story 2.2 — same captive-dependency reasoning as ILlmsTxtBuilder.
        // DefaultLlmsFullBuilder pulls IMarkdownContentExtractor (transient) whose
        // decorator factory pulls scoped IOptionsSnapshot<LlmsTxtSettings>.
        // Singleton would form a captive dependency on the scoped options
        // snapshot; Transient matches the extractor's lifetime.
        builder.Services.TryAddTransient<ILlmsFullBuilder, DefaultLlmsFullBuilder>();

        builder.Services.TryAddSingleton<IHostnameRootResolver, HostnameRootResolver>();
    }
}
