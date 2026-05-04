# Extension points

Story 5.1 introduces three public notifications and two new extension-point interfaces alongside the seams Stories 1–4 already shipped. This page is the canonical adopter reference for **subscribing to events**, **overriding the default writer**, and **overriding UA classification**.

> **PII discipline (NFR11).** Notifications and the request-log table capture **path, content key, culture, UA classification, referrer host** ONLY. Never query strings, cookies, tokens, session IDs, or full referrer paths. Adopter handlers MUST honour the same discipline if they forward data to external sinks.

---

## Notifications

The package publishes one notification per successful (200) response across the three routes:

| Notification | Fires from | Payload |
|---|---|---|
| `MarkdownPageRequestedNotification` | `MarkdownController.Render` (`.md` route) + `AcceptHeaderNegotiationMiddleware` (Accept-negotiated path) | `Path`, `ContentKey`, `Culture`, `UserAgentClassification`, `ReferrerHost` |
| `LlmsTxtRequestedNotification` | `LlmsTxtController.Render` | `Hostname`, `Culture`, `UserAgentClassification`, `ReferrerHost` |
| `LlmsFullTxtRequestedNotification` | `LlmsFullTxtController.Render` | `Hostname`, `Culture`, `UserAgentClassification`, `ReferrerHost`, `BytesServed` |

All three classes are sealed POCO records (get-only properties, all-args ctor). All three implement `Umbraco.Cms.Core.Notifications.INotification` and are dispatched via Umbraco's `IEventAggregator` in fire-and-forget mode (`PublishAsync`). **Skipped on 304 / 404 / 500.**

### Subscribing — `INotificationAsyncHandler<T>` (recommended)

```csharp
using LlmsTxt.Umbraco.Notifications;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Events;

public sealed class AnalyticsComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder) =>
        builder.AddNotificationAsyncHandler<MarkdownPageRequestedNotification, MarkdownAnalyticsHandler>();
}

public sealed class MarkdownAnalyticsHandler : INotificationAsyncHandler<MarkdownPageRequestedNotification>
{
    private readonly ILogger<MarkdownAnalyticsHandler> _log;
    public MarkdownAnalyticsHandler(ILogger<MarkdownAnalyticsHandler> log) => _log = log;

    public Task HandleAsync(MarkdownPageRequestedNotification n, CancellationToken ct)
    {
        _log.LogInformation(
            "AI traffic: {Path} {Class} {ContentKey}",
            n.Path,
            n.UserAgentClassification,
            n.ContentKey);
        return Task.CompletedTask;
    }
}
```

### Subscribing — sync `INotificationHandler<T>`

Same pattern via `builder.AddNotificationHandler<TNotification, THandler>()`. Sync handlers are dispatched on the request thread; **keep them fast** or queue work to your own background channel.

### Adopter-handler exception isolation

Adopter handler exceptions are caught at Umbraco's dispatcher layer and logged at `Warning`. **They never break our routes** (AC2). The package's own default handler (`DefaultLlmsRequestLogHandler`) adopts the same try/catch defence-in-depth around `ILlmsRequestLog.EnqueueAsync` so a custom writer fault can't degrade publication for sibling handlers.

---

## `ILlmsRequestLog` — request-log writer

The package's default writer (`DefaultLlmsRequestLog`) enqueues entries into a process-wide bounded `Channel<T>` and a background drainer batch-writes to the host DB's `llmsTxtRequestLog` table. Adopters override the interface to redirect logging to App Insights, Serilog, a custom table, or a no-op.

### Lifetime contract — Singleton only

```csharp
// ✅ ACCEPTED — Singleton override wins; the package's TryAddSingleton no-ops
services.AddSingleton<ILlmsRequestLog, MyAppInsightsLog>();

// ❌ REJECTED at composer-time — InvalidOperationException
services.AddScoped<ILlmsRequestLog, MyScopedLog>();
services.AddTransient<ILlmsRequestLog, MyTransientLog>();
```

`NotificationsComposer.Compose` throws `InvalidOperationException` if a non-Singleton lifetime is registered (architecture.md § Configuration & DI; Story 4.2 chunk-3 D2 pattern).

> **Register before `NotificationsComposer` runs.** The composer-time hard-validation only inspects registrations present when the composer executes. If your `IComposer` runs AFTER `NotificationsComposer` (via `[ComposeAfter(typeof(NotificationsComposer))]`, alphabetical ordering by type name, or a `services.AddScoped<ILlmsRequestLog, ...>` after `IUmbracoBuilder.Build()`) the validation is bypassed at composer-time. Microsoft DI's runtime `ValidateScopes` would still catch a captive Scoped → Singleton mismatch at boot — but the soonest, clearest error message comes from registering before `NotificationsComposer`.

Adopters needing a Scoped impl wrap it in a Singleton facade:

```csharp
public sealed class ScopedLogFacade : ILlmsRequestLog
{
    private readonly IServiceScopeFactory _scopeFactory;
    public ScopedLogFacade(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task EnqueueAsync(LlmsTxtRequestLogEntry entry, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var inner = scope.ServiceProvider.GetRequiredService<MyScopedLog>();
        await inner.EnqueueAsync(entry, ct);
    }
}
```

### Best-effort logging by design

The default writer's bounded channel uses `BoundedChannelFullMode.DropOldest` — under sustained crawl load, oldest entries are shed so the most recent traffic is preserved. The drainer runs **only on the scheduling instance** (`IServerRoleAccessor.CurrentServerRole ∈ { SchedulingPublisher, Single }`) — multi-instance front-end servers each hold their own per-process channel and drop entries on overflow. **Adopters needing durable cross-instance analytics override `ILlmsRequestLog`** with a sink that writes synchronously / per-instance.

### Examples

**No-op (kill-switch)** — equivalent to `LlmsTxt:RequestLog:Enabled: false`:

```csharp
public sealed class NoOpRequestLog : ILlmsRequestLog
{
    public Task EnqueueAsync(LlmsTxtRequestLogEntry entry, CancellationToken ct) => Task.CompletedTask;
}

services.AddSingleton<ILlmsRequestLog, NoOpRequestLog>();
```

**App Insights forwarder**:

```csharp
public sealed class AppInsightsRequestLog : ILlmsRequestLog
{
    private readonly TelemetryClient _telemetry;
    public AppInsightsRequestLog(TelemetryClient telemetry) => _telemetry = telemetry;

    public Task EnqueueAsync(LlmsTxtRequestLogEntry entry, CancellationToken ct)
    {
        _telemetry.TrackEvent("LlmsTxtRequest", new Dictionary<string, string>
        {
            ["Path"] = entry.Path,
            ["UserAgentClass"] = entry.UserAgentClass,
            ["Culture"] = entry.Culture ?? "",
            ["ReferrerHost"] = entry.ReferrerHost ?? "",
            ["ContentKey"] = entry.ContentKey?.ToString() ?? "",
        });
        return Task.CompletedTask;
    }
}

services.AddSingleton<ILlmsRequestLog, AppInsightsRequestLog>();
```

---

## `IUserAgentClassifier` — UA classification

The default classifier (`DefaultUserAgentClassifier`) projects Story 4.2's `AiBotList` into a token-to-class map, returning one of the seven `UserAgentClass` values: `AiTraining`, `AiSearchRetrieval`, `AiUserTriggered`, `AiDeprecated`, `HumanBrowser`, `CrawlerOther`, `Unknown`.

### `BotCategory` → `UserAgentClass` mapping

| `AiBotEntry` source | `UserAgentClass` |
|---|---|
| `IsDeprecated == true` (any category) | `AiDeprecated` |
| `BotCategory.Training` | `AiTraining` |
| `BotCategory.SearchRetrieval` | `AiSearchRetrieval` |
| `BotCategory.UserTriggered` | `AiUserTriggered` |
| `BotCategory.OptOut` (e.g. `Google-Extended`) | `AiTraining` |
| `BotCategory.Unknown` | `AiTraining` (conservative — still in the AI-bot list) |
| (no AI match) → `Googlebot`/`bingbot`/etc. | `CrawlerOther` |
| (no AI / crawler match) → `Mozilla`/`AppleWebKit`/etc. | `HumanBrowser` |
| (no match anywhere; null/empty) | `Unknown` |

Match priority: **AI tokens first (longest substring), then non-AI crawlers, then browser tells.**

### Adopter override

```csharp
public sealed class CustomClassifier : IUserAgentClassifier
{
    public UserAgentClass Classify(string? userAgent) =>
        string.IsNullOrEmpty(userAgent) ? UserAgentClass.Unknown
        : userAgent.Contains("MyCustomBot", StringComparison.OrdinalIgnoreCase) ? UserAgentClass.AiTraining
        : UserAgentClass.Unknown;
}

services.AddSingleton<IUserAgentClassifier, CustomClassifier>();
```

> The `IUserAgentClassifier` Singleton requirement is **not** composer-time enforced (unlike `ILlmsRequestLog`). The DI gate's `ValidateScopes = true` catches captive-dep misuse at boot.

---

## Other Story 5.1 surfaces (no public extension)

- **`LogRetentionJob` (`IDistributedBackgroundJob`)** — sealed; not an extension point. Adjust cadence via `LlmsTxt:LogRetention:DurationDays` + `RunIntervalHours`. Set `DurationDays: 0` to disable retention entirely.
- **`LlmsRequestLogDrainHostedService` (`IHostedService`)** — internal to the default `DefaultLlmsRequestLog` shape; bypassed when an adopter overrides `ILlmsRequestLog`.

---

## Other extension points (cross-references)

- **`IRobotsAuditor`** (Story 4.2) — runs the build-time AI-bot list against the host site's `robots.txt` and surfaces blocking advice via the Backoffice Health Check view. See [`robots-audit.md`](robots-audit.md). Lifetime: Singleton (composer-time hard-validation enforced by `HealthChecksComposer`).
- **`ILlmsExclusionEvaluator`** (Story 4.1) — shared shape for the per-doctype + per-page `excludeFromLlmExports` boolean used by `MarkdownController` + `AcceptHeaderNegotiationMiddleware` + the Razor TagHelpers. See [`data-attributes.md`](data-attributes.md).
- **`ILlmsSettingsResolver`** (Story 3.1) — overlays the appsettings + Settings-doctype configuration into a `ResolvedLlmsSettings` per-request. See [`getting-started.md` § Settings doctype](getting-started.md#settings-doctype--backoffice-story-31). Adopter overrides go via `services.AddScoped<ILlmsSettingsResolver, MyResolver>()`; resolver throws degrade gracefully to appsettings-only.
- **`IMarkdownContentExtractor`** + **`IContentRegionSelector`** (Story 1.x) — the seam between `IPublishedContent` and the Markdown body. Adopters who need a different HTML→Markdown pipeline (e.g. inject custom Markdig pipelines, transform images, etc.) override these. See [`getting-started.md` § Customising extraction](getting-started.md#customising-extraction). The package's `Caching/` decorator wraps any registration; bypass-extractor adopters get a one-shot `Information` log line at boot.
- **`ILlmsTxtBuilder`** + **`ILlmsFullBuilder`** (Story 2.x) — the per-route manifest builders for `/llms.txt` and `/llms-full.txt`. Pure functions over `(host, culture, root content, pages, settings)` — no HTTP / scope dependencies. See the package's `Builders/` source for the public contracts; default behaviour is the canonical `llms.txt` shape from llmstxt.org.
