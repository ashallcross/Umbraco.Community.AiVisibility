using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Community.AiVisibility.Configuration;

namespace Umbraco.Community.AiVisibility.Extraction;

/// <summary>
/// Story 7.1 — thin orchestrator that dispatches the per-page render to the
/// configured <see cref="IPageRendererStrategy"/>. Reads
/// <c>AiVisibility:RenderStrategy:Mode</c> at the top of every render via
/// <see cref="IOptionsMonitor{TOptions}.CurrentValue"/> (config-hot-reload
/// semantics for free) and resolves the keyed strategy from the DI
/// container.
///
/// <para>
/// <b>No <c>if (mode == X)</c> switches inside the orchestrator.</b> The
/// mode-to-strategy mapping is a keyed-DI lookup; new strategies are added
/// by the composer registering them with <c>TryAddKeyedTransient</c> against
/// a <see cref="RenderStrategyMode"/> key.
/// </para>
///
/// <para>
/// Each strategy owns its own contract:
/// <list type="bullet">
/// <item><see cref="RazorPageRendererStrategy"/> (Story 7.1) — in-process
/// Razor render. Holds the locked decisions from Spike 0.A
/// (LD#7 non-generic <c>ViewDataDictionary</c>; LD#9 single-render-per-request
/// AsyncLocal scope constraint).</item>
/// <item><c>LoopbackPageRendererStrategy</c> (Story 7.2) — HTTP self-call.
/// Reverses Spike 0.A LD#8 for the loopback path only; Razor strategy still
/// honours LD#8.</item>
/// <item><c>AutoPageRendererStrategy</c> (Story 7.3) — Razor first, Loopback
/// fallback on <c>ModelBindingException</c>, with a per-(doctype, template)
/// decision cache. Becomes the default Mode when shipped.</item>
/// </list>
/// </para>
///
/// <para>
/// Lifetime is <c>Transient</c> (matches v1.0; matches every strategy
/// registered through the composer). The orchestrator holds no per-render
/// state.
/// </para>
/// </summary>
internal sealed class PageRenderer
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptionsMonitor<AiVisibilitySettings> _settings;
    private readonly ILogger<PageRenderer> _logger;

    public PageRenderer(
        IServiceProvider serviceProvider,
        IOptionsMonitor<AiVisibilitySettings> settings,
        ILogger<PageRenderer> logger)
    {
        _serviceProvider = serviceProvider;
        _settings = settings;
        _logger = logger;
    }

    public Task<PageRenderResult> RenderAsync(
        IPublishedContent content,
        Uri absoluteUri,
        string? culture,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mode = _settings.CurrentValue.RenderStrategy.Mode;
        var strategy = ResolveStrategy(mode);
        return strategy.RenderAsync(content, absoluteUri, culture, cancellationToken);
    }

    private IPageRendererStrategy ResolveStrategy(RenderStrategyMode mode)
    {
        // Use GetKeyedService (nullable) rather than GetRequiredKeyedService so a
        // genuine "no registration for this key" returns null and we can throw with
        // a project-context-aware diagnostic — while ctor-level InvalidOperationException
        // shapes (captive deps, ambiguous registration, custom-strategy ctor throws)
        // bubble unchanged with their original stack trace, instead of being re-wrapped
        // as "...is not registered" which would mislead the operator about root cause.
        var strategy = _serviceProvider.GetKeyedService<IPageRendererStrategy>(mode);
        if (strategy is not null)
        {
            return strategy;
        }

        var hint = mode switch
        {
            RenderStrategyMode.Loopback =>
                "An upcoming release ships LoopbackPageRendererStrategy. Pin Mode=Razor in appsettings.json until then.",
            RenderStrategyMode.Auto =>
                "An upcoming release ships AutoPageRendererStrategy. Pin Mode=Razor in appsettings.json until then.",
            _ =>
                "Register a custom IPageRendererStrategy keyed by this RenderStrategyMode value via services.TryAddKeyedTransient.",
        };
        var message =
            $"AiVisibility:RenderStrategy:Mode={mode} requires an IPageRendererStrategy keyed by RenderStrategyMode.{mode} which is not registered. {hint}";
        _logger.LogError("PageRenderer: render strategy {Mode} not registered. {Hint}", mode, hint);
        throw new InvalidOperationException(message);
    }
}
