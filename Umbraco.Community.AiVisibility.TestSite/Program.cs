using Umbraco.Community.AiVisibility.TestSite.Spikes;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.CreateUmbracoBuilder()
    .AddBackOffice()
    .AddWebsite()
    .AddComposers()
    .Build();

// SPIKE 0.A — TestSite-only services for the in-process Razor rendering harness.
// These registrations exist for the duration of Story 0.A only; they will be
// removed once the spike outcome locks the technique and Story 1.1 ships the
// real PageRenderer in the package project.
builder.Services.AddHttpClient("Spike")
    // SPIKE 0.A only: accept the TestSite's dev self-signed cert so the
    // HTTP-fetch baseline used by AC1 can hit https://localhost:44314.
    // This trust override applies to the named "Spike" client ONLY.
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    });
builder.Services.AddControllers();
builder.Services.AddTransient<InProcessPageRenderer>();
builder.Services.AddTransient<HttpFetchComparator>();
builder.Services.AddTransient<ConcurrentRenderProbe>();

WebApplication app = builder.Build();


await app.BootUmbracoAsync();


app.UseUmbraco()
    .WithMiddleware(u =>
    {
        u.UseBackOffice();
        u.UseWebsite();
    })
    .WithEndpoints(u =>
    {
        u.UseBackOfficeEndpoints();
        u.UseWebsiteEndpoints();
    });

// SPIKE 0.A — map our spike controller AFTER Umbraco's endpoint registration so
// the attribute-routed `/spikes/*` routes resolve before Umbraco's content
// fallback returns a 404. Removed when the spike harness is decommissioned.
app.MapControllers();

await app.RunAsync();

// Story 6.0b Task 4 — `WebApplicationFactory<TEntryPoint>` requires `TEntryPoint`
// to be a public type. Top-level statements generate an internal partial class
// `Program`; this declaration upgrades it to public so
// `WebApplicationFactory<Program>` in `Umbraco.Community.AiVisibility.Tests` can
// resolve the entry point. Pure shape upgrade — no runtime behaviour change.
public partial class Program { }
