using System.Net;
using System.Threading.Channels;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Web;
using ForgeTrust.RazorWire.Bridge;
using ForgeTrust.RazorWire.Forms;
using ForgeTrust.RazorWire.Streams;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.RazorWire.Tests;

public class RazorWireWebModuleTests
{
    [Fact]
    public void IncludeAsApplicationPart_IsTrue()
    {
        var module = new RazorWireWebModule();
        Assert.True(module.IncludeAsApplicationPart);
    }

    [Fact]
    public void ConfigureWebOptions_InDevelopment_UpgradesMvcAndAddsConfigureMvc()
    {
        // Arrange
        var module = new RazorWireWebModule();
        var context = CreateContext(isDevelopment: true);
        var options = new WebOptions
        {
            Mvc = new MvcOptions { MvcSupportLevel = MvcSupport.Controllers }
        };

        // Act
        module.ConfigureWebOptions(context, options);

        // Assert
        Assert.Equal(MvcSupport.ControllersWithViews, options.Mvc.MvcSupportLevel);
        Assert.NotNull(options.Mvc.ConfigureMvc);
        AssertConfigureMvcAddsAntiforgeryFilter(options.Mvc.ConfigureMvc);
    }

    [Fact]
    public void ConfigureWebOptions_InProduction_WithSufficientMvc_PreservesSupportAndAddsConfigureMvc()
    {
        // Arrange
        var module = new RazorWireWebModule();
        var context = CreateContext(isDevelopment: false);
        Action<IMvcBuilder>? configure = _ => { };
        var options = new WebOptions
        {
            Mvc = new MvcOptions
            {
                MvcSupportLevel = MvcSupport.Full,
                ConfigureMvc = configure
            }
        };

        // Act
        module.ConfigureWebOptions(context, options);

        // Assert
        Assert.Equal(MvcSupport.Full, options.Mvc.MvcSupportLevel);
        Assert.NotNull(options.Mvc.ConfigureMvc);
        Assert.NotSame(configure, options.Mvc.ConfigureMvc);
        Assert.Contains(options.Mvc.ConfigureMvc!.GetInvocationList(), callback => ReferenceEquals(callback, configure));
        AssertConfigureMvcAddsAntiforgeryFilter(options.Mvc.ConfigureMvc);
    }

    [Fact]
    public void ConfigureWebOptions_InProduction_WithSufficientMvcAndNoExistingConfigure_AddsConfigureMvc()
    {
        // Arrange
        var module = new RazorWireWebModule();
        var context = CreateContext(isDevelopment: false);
        var options = new WebOptions
        {
            Mvc = new MvcOptions
            {
                MvcSupportLevel = MvcSupport.Full
            }
        };

        // Act
        module.ConfigureWebOptions(context, options);

        // Assert
        Assert.Equal(MvcSupport.Full, options.Mvc.MvcSupportLevel);
        Assert.NotNull(options.Mvc.ConfigureMvc);
        AssertConfigureMvcAddsAntiforgeryFilter(options.Mvc.ConfigureMvc);
    }

    [Fact]
    public void ConfigureServices_RegistersRazorWireAndOutputCacheServices()
    {
        // Arrange
        var module = new RazorWireWebModule();
        var services = new ServiceCollection();

        // Act
        module.ConfigureServices(CreateContext(isDevelopment: false), services);
        using var provider = services.BuildServiceProvider();

        // Assert
        Assert.NotNull(provider.GetService<IRazorWireStreamHub>());
        Assert.NotNull(provider.GetService<IRazorWireChannelAuthorizer>());
        Assert.NotNull(provider.GetService<IAntiforgery>());
        Assert.NotNull(provider.GetService<IOptions<RazorWireOptions>>());
        Assert.NotNull(provider.GetService<IOptions<OutputCacheOptions>>());
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IRazorPartialRenderer));
    }

    [Fact]
    public async Task ConfigureEndpoints_MapsRazorWireStreamEndpoint()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(new RazorWireOptions());
        builder.Services.AddSingleton<IRazorWireChannelAuthorizer, AllowAllRazorWireChannelAuthorizer>();
        builder.Services.AddSingleton<IRazorWireStreamHub, InMemoryRazorWireStreamHub>();
        await using var app = builder.Build();
        var module = new RazorWireWebModule();

        // Act
        module.ConfigureEndpoints(CreateContext(isDevelopment: false), app);

        // Assert
        var routeBuilder = (IEndpointRouteBuilder)app;
        var routeEndpoint = Assert.Single(
            routeBuilder.DataSources
                .SelectMany(ds => ds.Endpoints)
                .OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText == "/_rw/streams/{channel}");

        Assert.Equal("/_rw/streams/{channel}", routeEndpoint.RoutePattern.RawText);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("?replay=1", true)]
    [InlineData("?replay=true", true)]
    [InlineData("?replay=TRUE", true)]
    [InlineData("?replay=0&replay=true", true)]
    [InlineData("?replay=1&replay=false", true)]
    [InlineData("?replay=false", false)]
    public async Task ConfigureEndpoints_PassesReplayQueryToStreamHub(string queryString, bool expectedReplay)
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        var hub = new RecordingRazorWireStreamHub();
        builder.Services.AddSingleton(new RazorWireOptions());
        builder.Services.AddSingleton<IRazorWireChannelAuthorizer, AllowAllRazorWireChannelAuthorizer>();
        builder.Services.AddSingleton<IRazorWireStreamAuthorizer, RazorWireBoolChannelAuthorizerAdapter>();
        builder.Services.AddSingleton<IRazorWireStreamHub>(hub);
        await using var app = builder.Build();
        var module = new RazorWireWebModule();

        module.ConfigureEndpoints(CreateContext(isDevelopment: false), app);

        var routeEndpoint = GetRazorWireStreamsEndpoint(app);
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services
        };
        context.Request.RouteValues["channel"] = "orders";
        context.Request.QueryString = new QueryString(queryString);
        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        // Act
        await routeEndpoint.RequestDelegate!(context);

        // Assert
        Assert.Equal("orders", hub.SubscribedChannel);
        Assert.Equal(expectedReplay, hub.SubscribedOptions?.Replay ?? false);
        Assert.Equal("orders", hub.UnsubscribedChannel);
        Assert.Same(hub.Reader, hub.UnsubscribedReader);
    }

    [Fact]
    public async Task ConfigureEndpoints_ServesEmbeddedRuntimeAssets_WhenStaticWebAssetsAreUnavailable()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new RazorWireOptions());
        builder.Services.AddSingleton<IRazorWireChannelAuthorizer, AllowAllRazorWireChannelAuthorizer>();
        builder.Services.AddSingleton<IRazorWireStreamAuthorizer, RazorWireBoolChannelAuthorizerAdapter>();
        builder.Services.AddSingleton<IRazorWireStreamHub, InMemoryRazorWireStreamHub>();

        await using var app = builder.Build();
        var module = new RazorWireWebModule();

        module.ConfigureEndpoints(CreateContext(isDevelopment: false), app);

        await app.StartAsync();

        try
        {
            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>();
            var baseAddress = Assert.Single(addresses!.Addresses);

            using var client = new HttpClient
            {
                BaseAddress = new Uri(baseAddress)
            };

            using var runtimeResponse = await client.GetAsync("/_content/ForgeTrust.RazorWire/razorwire/razorwire.js");
            Assert.Equal(HttpStatusCode.OK, runtimeResponse.StatusCode);
            Assert.Equal("text/javascript", runtimeResponse.Content.Headers.ContentType?.MediaType);
            Assert.Contains(
                "Generated from assets/src/razorwire.ts",
                await runtimeResponse.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);

            using var islandsRequest = new HttpRequestMessage(HttpMethod.Head, "/_content/ForgeTrust.RazorWire/razorwire/razorwire.islands.js");
            using var islandsResponse = await client.SendAsync(islandsRequest);
            Assert.Equal(HttpStatusCode.OK, islandsResponse.StatusCode);
            Assert.Equal("text/javascript", islandsResponse.Content.Headers.ContentType?.MediaType);
            Assert.True(islandsResponse.Content.Headers.ContentLength > 0);

            using var behaviorKitResponse = await client.GetAsync("/_content/ForgeTrust.RazorWire/razorwire/behavior-kit.js");
            Assert.Equal(HttpStatusCode.OK, behaviorKitResponse.StatusCode);
            Assert.Equal("text/javascript", behaviorKitResponse.Content.Headers.ContentType?.MediaType);
            Assert.Contains(
                "Generated from assets/src/behavior-kit.ts",
                await behaviorKitResponse.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);

            using var pageNavigationResponse = await client.GetAsync("/_content/ForgeTrust.RazorWire/razorwire/page-navigation.js");
            Assert.Equal(HttpStatusCode.OK, pageNavigationResponse.StatusCode);
            Assert.Equal("text/javascript", pageNavigationResponse.Content.Headers.ContentType?.MediaType);
            Assert.Contains(
                "Generated from assets/src/page-navigation.ts",
                await pageNavigationResponse.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);

            using var sectionCopyResponse = await client.GetAsync("/_content/ForgeTrust.RazorWire/razorwire/section-copy.js");
            Assert.Equal(HttpStatusCode.OK, sectionCopyResponse.StatusCode);
            Assert.Equal("text/javascript", sectionCopyResponse.Content.Headers.ContentType?.MediaType);
            Assert.Contains(
                "Generated from assets/src/section-copy.ts",
                await sectionCopyResponse.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);

            using var formInteractionsResponse = await client.GetAsync("/_content/ForgeTrust.RazorWire/razorwire/form-interactions.js");
            Assert.Equal(HttpStatusCode.OK, formInteractionsResponse.StatusCode);
            Assert.Equal("text/javascript", formInteractionsResponse.Content.Headers.ContentType?.MediaType);
            Assert.Contains(
                "Generated from assets/src/form-interactions.ts",
                await formInteractionsResponse.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);

            using var turboResponse = await client.GetAsync("/_content/ForgeTrust.RazorWire/razorwire/turbo.es2017-umd.js");
            Assert.Equal(HttpStatusCode.OK, turboResponse.StatusCode);
            Assert.Equal("text/javascript", turboResponse.Content.Headers.ContentType?.MediaType);
            Assert.Contains(
                "Turbo 8.0.23",
                await turboResponse.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);

            using var turboHeadRequest = new HttpRequestMessage(HttpMethod.Head, "/_content/ForgeTrust.RazorWire/razorwire/turbo.es2017-umd.js");
            using var turboHeadResponse = await client.SendAsync(turboHeadRequest);
            Assert.Equal(HttpStatusCode.OK, turboHeadResponse.StatusCode);
            Assert.True(turboHeadResponse.Content.Headers.ContentLength > 0);

            using var imageResponse = await client.GetAsync("/_content/ForgeTrust.RazorWire/background.png");
            Assert.Equal(HttpStatusCode.OK, imageResponse.StatusCode);
            Assert.Equal("image/png", imageResponse.Content.Headers.ContentType?.MediaType);

            using var interopResponse = await client.GetAsync("/_content/ForgeTrust.RazorWire/exampleJsInterop.js");
            Assert.Equal(HttpStatusCode.OK, interopResponse.StatusCode);
            Assert.Equal("text/javascript", interopResponse.Content.Headers.ContentType?.MediaType);
            Assert.Contains(
                "showPrompt",
                await interopResponse.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static RouteEndpoint GetRazorWireStreamsEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        return Assert.Single(
            routeBuilder.DataSources
                .SelectMany(ds => ds.Endpoints)
                .OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText == "/_rw/streams/{channel}");
    }

    [Fact]
    public void NoOpMethods_DoNotThrow()
    {
        var module = new RazorWireWebModule();
        var context = CreateContext(isDevelopment: false);
        var hostBuilder = Host.CreateDefaultBuilder();

        module.RegisterDependentModules(new ModuleDependencyBuilder());
        module.ConfigureHostBeforeServices(context, hostBuilder);
        module.ConfigureHostAfterServices(context, hostBuilder);
    }

    private static StartupContext CreateContext(bool isDevelopment)
    {
        return new StartupContext(
            [],
            new DummyRootModule(),
            EnvironmentProvider: new TestEnvironmentProvider(isDevelopment));
    }

    private static void AssertConfigureMvcAddsAntiforgeryFilter(Action<IMvcBuilder>? configureMvc)
    {
        Assert.NotNull(configureMvc);
        var services = new ServiceCollection();
        var builder = services.AddControllersWithViews();

        configureMvc(builder);

        using var provider = services.BuildServiceProvider();
        var mvcOptions = provider.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.MvcOptions>>().Value;
        Assert.Contains(
            mvcOptions.Filters,
            filter => filter is Microsoft.AspNetCore.Mvc.ServiceFilterAttribute serviceFilter
                      && serviceFilter.ServiceType == typeof(RazorWireAntiforgeryFailureFilter));
    }

    private sealed class DummyRootModule : IAppSurfaceHostModule
    {
        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }
    }

    private sealed class RecordingRazorWireStreamHub : IRazorWireStreamHub
    {
        private readonly Channel<string> _streamChannel = Channel.CreateUnbounded<string>();

        public RecordingRazorWireStreamHub()
        {
            _streamChannel.Writer.Complete();
        }

        public ChannelReader<string> Reader => _streamChannel.Reader;

        public string? SubscribedChannel { get; private set; }

        public RazorWireStreamSubscribeOptions? SubscribedOptions { get; private set; }

        public string? UnsubscribedChannel { get; private set; }

        public ChannelReader<string>? UnsubscribedReader { get; private set; }

        public ValueTask PublishAsync(string channelName, string message) => ValueTask.CompletedTask;

        public ChannelReader<string> Subscribe(string channelName)
        {
            return Subscribe(channelName, options: null);
        }

        public ChannelReader<string> Subscribe(string channelName, RazorWireStreamSubscribeOptions? options)
        {
            SubscribedChannel = channelName;
            SubscribedOptions = options;

            return Reader;
        }

        public void Unsubscribe(string channelName, ChannelReader<string> reader)
        {
            UnsubscribedChannel = channelName;
            UnsubscribedReader = reader;
        }
    }

    private sealed class TestEnvironmentProvider : IEnvironmentProvider
    {
        public TestEnvironmentProvider(bool isDevelopment)
        {
            IsDevelopment = isDevelopment;
            Environment = isDevelopment ? "Development" : "Production";
        }

        public string Environment { get; }

        public bool IsDevelopment { get; }

        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
    }
}
