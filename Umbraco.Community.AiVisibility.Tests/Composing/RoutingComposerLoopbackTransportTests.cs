using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Microsoft.AspNetCore.Hosting.Server;
using Umbraco.Community.AiVisibility.Composing;
using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Extraction;
using Umbraco.Community.AiVisibility.Notifications;

namespace Umbraco.Community.AiVisibility.Tests.Composing;

/// <summary>
/// <c>LoopbackTransport</c> picks the loopback client's handler and resolver.
/// </summary>
[TestFixture]
public class RoutingComposerLoopbackTransportTests
{
    [Test]
    public async Task Compose_DefaultSettings_LoopbackClientRunsRequestsThroughTheCapturedPipeline()
    {
        using var services = BuildServices(transport: null);
        var app = new ApplicationBuilder(services);
        services.GetServices<IStartupFilter>().Single()
            .Configure(site => site.Run(ctx => ctx.Response.WriteAsync($"rendered {ctx.Request.Path}")))(app);
        app.Build();
        var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(Constants.Http.LoopbackHttpClientName);

        var body = await client.GetStringAsync("http://localhost/people/");

        Assert.That(body, Is.EqualTo("rendered /people/"));
    }

    [Test]
    public void Compose_NetworkTransport_LoopbackClientUsesHttpClientHandlerWithoutAutoRedirect()
    {
        using var services = BuildServices(transport: "Network");

        var primary = PrimaryHandler(services);

        Assert.That(primary, Is.InstanceOf<HttpClientHandler>());
        Assert.That(((HttpClientHandler)primary).AllowAutoRedirect, Is.False);
        Assert.That(((HttpClientHandler)primary).ServerCertificateCustomValidationCallback, Is.Not.Null);
    }

    [TestCase(null, typeof(InMemoryLoopbackUrlResolver))]
    [TestCase("Network", typeof(LoopbackUrlResolver))]
    public void Compose_LoopbackTransport_PicksTheMatchingResolver(string? transport, Type expected)
    {
        using var services = BuildServices(transport);

        Assert.That(services.GetRequiredService<ILoopbackUrlResolver>(), Is.InstanceOf(expected));
    }

    [TestCase(null)]
    [TestCase("Network")]
    public void Compose_LoopbackTransport_ResolvesUnderScopeValidation(string? transport)
    {
        using var services = BuildServices(transport);

        Assert.DoesNotThrow(() =>
        {
            services.GetRequiredService<LoopbackTransportSelection>();
            services.GetRequiredService<InMemoryPipelineHolder>();
            services.GetServices<IStartupFilter>().Single();
            services.GetRequiredService<ILoopbackUrlResolver>();
            services.GetRequiredService<IHttpClientFactory>().CreateClient(Constants.Http.LoopbackHttpClientName);
        });
    }

    [Test]
    public void Compose_LoopbackTransport_LiveEditIsIgnoredUntilRestart()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        using var services = BuildServices(transport: null, config);
        Assert.That(services.GetRequiredService<LoopbackTransportSelection>().IsInMemory, Is.True);

        config["AiVisibility:RenderStrategy:LoopbackTransport"] = "Network";
        config.Reload();

        Assert.Multiple(() =>
        {
            Assert.That(
                services.GetRequiredService<IOptionsMonitor<AiVisibilitySettings>>().CurrentValue.RenderStrategy.LoopbackTransport,
                Is.EqualTo(LoopbackTransport.Network),
                "precondition: the options monitor saw the edit");
            Assert.That(services.GetRequiredService<LoopbackTransportSelection>().IsInMemory, Is.True);
            Assert.That(services.GetRequiredService<ILoopbackUrlResolver>(), Is.InstanceOf<InMemoryLoopbackUrlResolver>());
            Assert.That(PrimaryHandler(services), Is.InstanceOf<InMemoryLoopbackHandler>());
        });
    }

    private static ServiceProvider BuildServices(string? transport, IConfigurationRoot? config = null)
    {
        var services = new ServiceCollection();
        var builder = Substitute.For<IUmbracoBuilder>();
        builder.Services.Returns(services);
        var settings = new Dictionary<string, string?>();
        if (transport is not null)
        {
            settings["AiVisibility:RenderStrategy:LoopbackTransport"] = transport;
        }

        builder.Config.Returns(config ?? new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

        new RoutingComposer().Compose(builder);

        services.AddLogging();
        services.AddSingleton(Substitute.For<IServer>());
        services.AddSingleton<IHttpContextFactory>(sp => new DefaultHttpContextFactory(sp));

        // Umbraco services the rest of RoutingComposer's graph consumes; stubbed so
        // ValidateOnBuild can walk every registration, not just the loopback ones.
        services.AddSingleton(Substitute.For<IPublishedRouter>());
        services.AddSingleton(Substitute.For<IPublishedUrlProvider>());
        services.AddSingleton(Substitute.For<IPublicAccessService>());
        services.AddScoped(_ => Substitute.For<ISettingsResolver>());
        services.AddSingleton(Substitute.For<IUmbracoContextFactory>());
        services.AddSingleton(Substitute.For<IHttpContextAccessor>());
        services.AddSingleton(Substitute.For<IFileService>());
        services.AddSingleton(Substitute.For<ITemplateService>());
        services.AddSingleton(Substitute.For<IRazorViewEngine>());
        services.AddSingleton(Substitute.For<ITempDataProvider>());
        services.AddSingleton(Substitute.For<IVariationContextAccessor>());
        services.AddSingleton(Substitute.For<INotificationPublisher>());
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
    }

    private static HttpMessageHandler PrimaryHandler(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(Constants.Http.LoopbackHttpClientName);
        var builder = new RecordingHandlerBuilder(services);
        foreach (var action in options.HttpMessageHandlerBuilderActions)
        {
            action(builder);
        }

        return builder.PrimaryHandler;
    }

    private sealed class RecordingHandlerBuilder : HttpMessageHandlerBuilder
    {
        public RecordingHandlerBuilder(IServiceProvider services) => Services = services;

        public override string? Name { get; set; } = Constants.Http.LoopbackHttpClientName;

        public override HttpMessageHandler PrimaryHandler { get; set; } = new UnconfiguredHandler();

        public override IList<DelegatingHandler> AdditionalHandlers { get; } = new List<DelegatingHandler>();

        public override IServiceProvider Services { get; }

        public override HttpMessageHandler Build() => PrimaryHandler;
    }

    private sealed class UnconfiguredHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("primary handler was not configured");
    }
}
