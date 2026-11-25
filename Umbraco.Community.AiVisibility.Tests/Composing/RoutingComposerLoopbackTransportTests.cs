using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Community.AiVisibility.Composing;

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
    }

    private static ServiceProvider BuildServices(string? transport)
    {
        var services = new ServiceCollection();
        var builder = Substitute.For<IUmbracoBuilder>();
        builder.Services.Returns(services);
        var settings = new Dictionary<string, string?>();
        if (transport is not null)
        {
            settings["AiVisibility:RenderStrategy:LoopbackTransport"] = transport;
        }

        builder.Config.Returns(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

        new RoutingComposer().Compose(builder);

        services.AddLogging();
        services.AddSingleton<IHttpContextFactory>(sp => new DefaultHttpContextFactory(sp));
        return services.BuildServiceProvider();
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
