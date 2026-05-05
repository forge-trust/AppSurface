using System.Net;
using System.Net.Http.Headers;
using ForgeTrust.Runnable.Core;
using ForgeTrust.Runnable.Web.Tests.SharedErrorPagesFixture;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.Runnable.Web.Tests;

public sealed class BrowserStatusPageTests
{
    public static TheoryData<int, string, string> SupportedStatusPages => new()
    {
        { StatusCodes.Status401Unauthorized, BrowserStatusPageDefaults.ReservedUnauthorizedRoute, "Runnable default 401" },
        { StatusCodes.Status403Forbidden, BrowserStatusPageDefaults.ReservedForbiddenRoute, "Runnable default 403" },
        { StatusCodes.Status404NotFound, BrowserStatusPageDefaults.ReservedNotFoundRoute, "Runnable default 404" }
    };

    public static TheoryData<int, HttpStatusCode, string> SupportedStatusResponses => new()
    {
        { StatusCodes.Status401Unauthorized, HttpStatusCode.Unauthorized, "Runnable default 401" },
        { StatusCodes.Status403Forbidden, HttpStatusCode.Forbidden, "Runnable default 403" },
        { StatusCodes.Status404NotFound, HttpStatusCode.NotFound, "Runnable default 404" }
    };

    [Theory]
    [MemberData(nameof(SupportedStatusPages))]
    public async Task AutoMode_WithControllersWithViews_UsesFrameworkFallback(
        int statusCode,
        string reservedRoute,
        string expectedMarker)
    {
        await using var runningApp = await StartHostAsync(
            new PlainWebModule(),
            options => { options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.ControllersWithViews }; },
            typeof(WebApplication).Assembly);

        using var response = await runningApp.Client.GetAsync(reservedRoute);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(expectedMarker, html);
        Assert.Contains(BrowserStatusPageDefaults.GetAppViewPath(statusCode), html);
    }

    [Fact]
    public async Task AutoMode_WithControllers_DoesNotEnableBrowserStatusPages()
    {
        await using var runningApp = await StartHostAsync(
            new PlainWebModule(),
            options => { options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.Controllers }; },
            typeof(WebApplication).Assembly);

        using var response = await runningApp.Client.GetAsync(BrowserStatusPageDefaults.ReservedNotFoundRoute);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EnabledMode_UpgradesControllersOnlyApps_AndMapsReservedRoutes()
    {
        await using var runningApp = await StartHostAsync(
            new PlainWebModule(),
            options =>
            {
                options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.Controllers };
                options.Errors.UseConventionalBrowserStatusPages();
            },
            typeof(WebApplication).Assembly);

        using var response = await runningApp.Client.GetAsync(BrowserStatusPageDefaults.ReservedUnauthorizedRoute);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Runnable default 401", html);
    }

    [Fact]
    public async Task DisabledMode_WithViews_DoesNotMapReservedRoute()
    {
        await using var runningApp = await StartHostAsync(
            new PlainWebModule(),
            options =>
            {
                options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.ControllersWithViews };
                options.Errors.DisableBrowserStatusPages();
            },
            typeof(WebApplication).Assembly);

        using var response = await runningApp.Client.GetAsync(BrowserStatusPageDefaults.ReservedNotFoundRoute);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(SupportedStatusPages))]
    public async Task DirectRequests_ToSupportedReservedRoutes_RenderStatusPageWithOk(
        int statusCode,
        string reservedRoute,
        string expectedMarker)
    {
        await using var runningApp = await StartHostAsync(
            new PlainWebModule(),
            options => { options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.ControllersWithViews }; },
            typeof(WebApplication).Assembly);

        using var response = await runningApp.Client.GetAsync(reservedRoute);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(expectedMarker, html);
        Assert.Contains(BrowserStatusPageDefaults.GetAppViewPath(statusCode), html);
    }

    [Fact]
    public async Task DirectRequests_ToReserved500_Return404WithoutRenderingFallback()
    {
        await using var runningApp = await StartHostAsync(
            new PlainWebModule(),
            options => { options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.ControllersWithViews }; },
            typeof(WebApplication).Assembly);

        using var response = await runningApp.Client.GetAsync("/_runnable/errors/500");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(string.Empty, body);
    }

    [Theory]
    [MemberData(nameof(SupportedStatusResponses))]
    public async Task HtmlRequests_ToEmptySupportedStatuses_RenderBrowserStatusPages_AndPreserveStatus(
        int statusCode,
        HttpStatusCode expectedStatus,
        string expectedMarker)
    {
        await using var runningApp = await StartHostAsync(
            new StatusResponseWebModule(),
            options => { options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.ControllersWithViews }; },
            typeof(WebApplication).Assembly);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/empty-{statusCode}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using var response = await runningApp.Client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(expectedMarker, html);
        Assert.Contains($"/empty-{statusCode}", html);
        Assert.True(response.Headers.Contains($"X-Runnable-Reexecuted-{statusCode}"));
    }

    [Fact]
    public async Task HtmlHeadRequests_ToEmptySupportedStatuses_ReExecuteAndPreserveStatus()
    {
        await using var runningApp = await StartHostAsync(
            new StatusResponseWebModule(),
            options => { options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.ControllersWithViews }; },
            typeof(WebApplication).Assembly);

        using var request = new HttpRequestMessage(HttpMethod.Head, "/empty-403");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using var response = await runningApp.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Runnable-Reexecuted-403"));
    }

    [Theory]
    [MemberData(nameof(SupportedStatusResponses))]
    public async Task HtmlRequests_ToNonEmptySupportedStatuses_DoNotRenderBrowserStatusPage(
        int statusCode,
        HttpStatusCode expectedStatus,
        string expectedMarker)
    {
        await using var runningApp = await StartHostAsync(
            new StatusResponseWebModule(),
            options => { options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.ControllersWithViews }; },
            typeof(WebApplication).Assembly);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/html-{statusCode}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using var response = await runningApp.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"<p>original {statusCode} body</p>", body);
        Assert.DoesNotContain(expectedMarker, body);
        Assert.False(response.Headers.Contains($"X-Runnable-Reexecuted-{statusCode}"));
    }

    [Theory]
    [MemberData(nameof(SupportedStatusResponses))]
    public async Task JsonRequests_ToSupportedStatuses_DoNotRenderBrowserStatusPage(
        int statusCode,
        HttpStatusCode expectedStatus,
        string expectedMarker)
    {
        await using var runningApp = await StartHostAsync(
            new StatusResponseWebModule(),
            options => { options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.ControllersWithViews }; },
            typeof(WebApplication).Assembly);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/json-{statusCode}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await runningApp.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"{{\"status\":{statusCode}}}", body);
        Assert.DoesNotContain(expectedMarker, body);
        Assert.False(response.Headers.Contains($"X-Runnable-Reexecuted-{statusCode}"));
    }

    [Fact]
    public async Task NonGetOrHeadRequests_ToSupportedStatuses_DoNotRenderBrowserStatusPage()
    {
        await using var runningApp = await StartHostAsync(
            new StatusResponseWebModule(),
            options => { options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.ControllersWithViews }; },
            typeof(WebApplication).Assembly);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/empty-post-401");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using var response = await runningApp.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(string.Empty, body);
        Assert.False(response.Headers.Contains("X-Runnable-Reexecuted-401"));
    }

    [Fact]
    public async Task HtmlRequests_ToMissingDocsRoutes_IncludeDocsSearchRecovery()
    {
        await using var runningApp = await StartHostAsync(
            new PlainWebModule(),
            options => { options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.ControllersWithViews }; },
            typeof(WebApplication).Assembly);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/docs/missing-page");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using var response = await runningApp.Client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Search documentation", html);
        Assert.Contains("/docs/search", html);
    }

    [Theory]
    [InlineData("/docsetc")]
    [InlineData("/docs-old")]
    public async Task HtmlRequests_ToNonDocsPrefixRoutes_DoNotIncludeDocsSearchRecovery(string path)
    {
        await using var runningApp = await StartHostAsync(
            new PlainWebModule(),
            options => { options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.ControllersWithViews }; },
            typeof(WebApplication).Assembly);

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using var response = await runningApp.Client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Search documentation", html);
        Assert.DoesNotContain("/docs/search", html);
    }

    [Fact]
    public void ShouldApplyConventionalBrowserStatusPages_ReturnsFalse_ForNonGetRequests()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Headers.Accept = "text/html";

        var shouldApply = WebStartup<PlainWebModule>.ShouldApplyConventionalBrowserStatusPages(httpContext);

        Assert.False(shouldApply);
    }

    [Fact]
    public void ShouldApplyConventionalBrowserStatusPages_ReturnsTrue_ForHeadRequestsAcceptingHtml()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Head;
        httpContext.Request.Headers.Accept = "text/html";

        var shouldApply = WebStartup<PlainWebModule>.ShouldApplyConventionalBrowserStatusPages(httpContext);

        Assert.True(shouldApply);
    }

    [Fact]
    public void ShouldApplyConventionalBrowserStatusPages_ReturnsFalse_ForReservedRoutes()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Request.Path = BrowserStatusPageDefaults.ReservedUnauthorizedRoute;
        httpContext.Request.Headers.Accept = "text/html";

        var shouldApply = WebStartup<PlainWebModule>.ShouldApplyConventionalBrowserStatusPages(httpContext);

        Assert.False(shouldApply);
    }

    [Fact]
    public void ShouldApplyConventionalBrowserStatusPages_ReturnsFalse_WhenRequestDoesNotPreferHtml()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Request.Headers.Accept = "application/json";

        var shouldApply = WebStartup<PlainWebModule>.ShouldApplyConventionalBrowserStatusPages(httpContext);

        Assert.False(shouldApply);
    }

    [Fact]
    public void GetReservedRouteStatusCode_ReturnsNull_WhenRouteValueMissing()
    {
        var httpContext = new DefaultHttpContext();

        var statusCode = WebStartup<PlainWebModule>.GetReservedRouteStatusCode(httpContext);

        Assert.Null(statusCode);
    }

    [Fact]
    public void GetReservedRouteStatusCode_ReturnsInt_WhenRouteValueIsAlreadyAnInt()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["statusCode"] = 403;

        var statusCode = WebStartup<PlainWebModule>.GetReservedRouteStatusCode(httpContext);

        Assert.Equal(StatusCodes.Status403Forbidden, statusCode);
    }

    [Fact]
    public void GetReservedRouteStatusCode_ReturnsNull_ForUnsupportedRouteValueTypes()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["statusCode"] = new object();

        var statusCode = WebStartup<PlainWebModule>.GetReservedRouteStatusCode(httpContext);

        Assert.Null(statusCode);
    }

    [Fact]
    public void GetReservedRouteStatusCode_ReturnsParsedInt_WhenRouteValueIsAString()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["statusCode"] = "401";

        var statusCode = WebStartup<PlainWebModule>.GetReservedRouteStatusCode(httpContext);

        Assert.Equal(StatusCodes.Status401Unauthorized, statusCode);
    }

    [Theory]
    [InlineData(StatusCodes.Status401Unauthorized, "Shared fixture 401")]
    [InlineData(StatusCodes.Status403Forbidden, "Shared fixture 403")]
    [InlineData(StatusCodes.Status404NotFound, "Shared fixture 404")]
    public async Task SharedRclView_IsUsed_WhenAppOverrideIsMissing(int statusCode, string expectedMarker)
    {
        await using var runningApp = await StartHostAsync(
            new SharedConsumerWebModule(),
            options => { options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.ControllersWithViews }; },
            typeof(WebApplication).Assembly);

        using var response = await runningApp.Client.GetAsync(BrowserStatusPageDefaults.GetReservedRoute(statusCode));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(expectedMarker, html);
        Assert.DoesNotContain($"Runnable default {statusCode}", html);
    }

    [Theory]
    [InlineData(StatusCodes.Status401Unauthorized, "Local test assembly 401", "Shared fixture 401")]
    [InlineData(StatusCodes.Status403Forbidden, "Local test assembly 403", "Shared fixture 403")]
    [InlineData(StatusCodes.Status404NotFound, "Local test assembly 404", "Shared fixture 404")]
    public async Task LocalAppView_Wins_OverSharedRclView(
        int statusCode,
        string expectedLocalMarker,
        string sharedMarker)
    {
        await using var runningApp = await StartHostAsync(
            new LocalOverrideWebModule(),
            options => { options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.ControllersWithViews }; });

        using var response = await runningApp.Client.GetAsync(BrowserStatusPageDefaults.GetReservedRoute(statusCode));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(expectedLocalMarker, html);
        Assert.DoesNotContain(sharedMarker, html);
    }

    [Fact]
    public void ValidateConfiguredViews_ThrowsWithStatusAndSearchedLocations_WhenNoViewsResolve()
    {
        var viewEngine = new StubCompositeViewEngine(
            new Dictionary<string, ViewEngineResult>
            {
                [BrowserStatusPageDefaults.GetAppViewPath(StatusCodes.Status401Unauthorized)] =
                    ViewEngineResult.Found(
                        BrowserStatusPageDefaults.GetAppViewPath(StatusCodes.Status401Unauthorized),
                        new StubView(BrowserStatusPageDefaults.GetAppViewPath(StatusCodes.Status401Unauthorized)))
            },
            defaultFrameworkResult: ViewEngineResult.NotFound(
                BrowserStatusPageDefaults.FrameworkFallbackViewPath,
                ["/Views/_Runnable/Errors/StatusPage.cshtml"]));
        var renderer = CreateRenderer(viewEngine);

        var exception = Assert.Throws<InvalidOperationException>(() => renderer.ValidateConfiguredViews());

        Assert.Contains("enabled for 403", exception.Message);
        Assert.Contains(BrowserStatusPageDefaults.GetAppViewPath(StatusCodes.Status403Forbidden), exception.Message);
        Assert.Contains(BrowserStatusPageDefaults.FrameworkFallbackViewPath, exception.Message);
        Assert.Contains("/Views/_Runnable/Errors/StatusPage.cshtml", exception.Message);
        Assert.Contains("Add", exception.Message);
    }

    [Fact]
    public void ValidateConfiguredViews_ThrowsFallbackMessage_WhenViewEngineReportsNoLocations()
    {
        var renderer = CreateRenderer(
            new StubCompositeViewEngine(
                new Dictionary<string, ViewEngineResult>(),
                reportDefaultSearchedLocations: false));

        var exception = Assert.Throws<InvalidOperationException>(() => renderer.ValidateConfiguredViews());

        Assert.Contains("No Razor view locations were reported.", exception.Message);
    }

    [Fact]
    public async Task RenderAsync_UsesDefault404Status_WhenNoRouteStatusExists()
    {
        var executor = new CapturingViewResultExecutor();
        var renderer = CreateRenderer(CreateViewEngineWithAppViews(), executor);
        var httpContext = new DefaultHttpContext();

        await renderer.RenderAsync(httpContext);

        Assert.Equal(StatusCodes.Status404NotFound, executor.Model?.StatusCode);
    }

    [Fact]
    public async Task RenderAsync_UsesIntRouteStatusCode_WhenPresent()
    {
        var executor = new CapturingViewResultExecutor();
        var renderer = CreateRenderer(CreateViewEngineWithAppViews(), executor);
        var httpContext = new DefaultHttpContext();
        var routeData = new RouteData();
        routeData.Values["statusCode"] = StatusCodes.Status401Unauthorized;
        httpContext.Features.Set<IRoutingFeature>(new StubRoutingFeature(routeData));

        await renderer.RenderAsync(httpContext);

        Assert.Equal(StatusCodes.Status401Unauthorized, executor.Model?.StatusCode);
    }

    [Fact]
    public async Task RenderAsync_FallsBackTo404_WhenRouteStatusTypeIsUnsupported()
    {
        var executor = new CapturingViewResultExecutor();
        var renderer = CreateRenderer(CreateViewEngineWithAppViews(), executor);
        var httpContext = new DefaultHttpContext();
        var routeData = new RouteData();
        routeData.Values["statusCode"] = new object();
        httpContext.Features.Set<IRoutingFeature>(new StubRoutingFeature(routeData));

        await renderer.RenderAsync(httpContext);

        Assert.Equal(StatusCodes.Status404NotFound, executor.Model?.StatusCode);
    }

    [Fact]
    public async Task RenderAsync_CachesResolvedViewPaths_PerStatusCode()
    {
        var viewEngine = CreateViewEngineWithAppViews();
        var renderer = CreateRenderer(viewEngine);
        var unauthorizedContext = CreateRoutedStatusContext(StatusCodes.Status401Unauthorized);
        var forbiddenContext = CreateRoutedStatusContext(StatusCodes.Status403Forbidden);

        await renderer.RenderAsync(unauthorizedContext);
        await renderer.RenderAsync(unauthorizedContext);
        await renderer.RenderAsync(forbiddenContext);

        Assert.Equal(1, viewEngine.GetViewCalls[BrowserStatusPageDefaults.GetAppViewPath(StatusCodes.Status401Unauthorized)]);
        Assert.Equal(1, viewEngine.GetViewCalls[BrowserStatusPageDefaults.GetAppViewPath(StatusCodes.Status403Forbidden)]);
    }

    private static StubCompositeViewEngine CreateViewEngineWithAppViews()
    {
        return new StubCompositeViewEngine(
            BrowserStatusPageDescriptor.Supported.ToDictionary(
                descriptor => descriptor.AppViewPath,
                descriptor => ViewEngineResult.Found(descriptor.AppViewPath, new StubView(descriptor.AppViewPath))));
    }

    private static DefaultHttpContext CreateRoutedStatusContext(int statusCode)
    {
        var httpContext = new DefaultHttpContext();
        var routeData = new RouteData();
        routeData.Values["statusCode"] = statusCode;
        httpContext.Features.Set<IRoutingFeature>(new StubRoutingFeature(routeData));
        return httpContext;
    }

    private static async Task<RunningAppHandle> StartHostAsync<TModule>(
        TModule module,
        Action<WebOptions> configureOptions,
        System.Reflection.Assembly? entryPointAssembly = null)
        where TModule : class, IRunnableWebModule, new()
    {
        var startup = new TestWebStartup<TModule>(module);
        startup.WithOptions(configureOptions);

        var context = new StartupContext([], module);
        if (entryPointAssembly is not null)
        {
            context.OverrideEntryPointAssembly = entryPointAssembly;
        }

        var builder = ((IRunnableStartup)startup).CreateHostBuilder(context);
        builder.ConfigureWebHost(webHost => webHost.UseUrls("http://127.0.0.1:0"));

        var host = builder.Build();
        await host.StartAsync();

        var addresses = host.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses
            ?? [];

        var baseUrl = Assert.Single(addresses);
        return new RunningAppHandle(host, baseUrl);
    }

    private static BrowserStatusPageRenderer CreateRenderer(
        ICompositeViewEngine viewEngine,
        IActionResultExecutor<ViewResult>? executor = null)
    {
        return new BrowserStatusPageRenderer(
            executor ?? new CapturingViewResultExecutor(),
            viewEngine,
            new EmptyModelMetadataProvider(),
            NullLogger<BrowserStatusPageRenderer>.Instance);
    }

    private sealed class TestWebStartup<TModule> : WebStartup<TModule>
        where TModule : class, IRunnableWebModule, new()
    {
        private readonly TModule _module;

        public TestWebStartup(TModule module)
        {
            _module = module;
        }

        protected override TModule CreateRootModule() => _module;
    }

    private abstract class BaseTestWebModule : IRunnableWebModule
    {
        public virtual bool IncludeAsApplicationPart => false;

        public virtual void ConfigureWebOptions(StartupContext context, WebOptions options)
        {
        }

        public virtual void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public virtual void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }

        public virtual void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public virtual void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }

        public virtual void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
        {
        }

        public virtual void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
        {
        }
    }

    private sealed class PlainWebModule : BaseTestWebModule;

    private sealed class SharedConsumerWebModule : BaseTestWebModule
    {
        public override void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
            builder.AddModule<SharedErrorPagesFixtureModule>();
        }
    }

    private sealed class StatusResponseWebModule : BaseTestWebModule
    {
        public override void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
        {
            app.Use(
                async (httpContext, next) =>
                {
                    var reExecuteFeature = httpContext.Features.Get<IStatusCodeReExecuteFeature>();
                    if (reExecuteFeature is not null)
                    {
                        httpContext.Response.Headers[$"X-Runnable-Reexecuted-{reExecuteFeature.OriginalStatusCode}"] = "true";
                    }

                    await next();
                });
        }

        public override void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
        {
            foreach (var descriptor in BrowserStatusPageDescriptor.Supported)
            {
                var statusCode = descriptor.StatusCode;
                endpoints.MapMethods(
                    $"/empty-{statusCode}",
                    [HttpMethods.Get, HttpMethods.Head],
                    httpContext =>
                    {
                        httpContext.Response.StatusCode = statusCode;
                        return Task.CompletedTask;
                    });

                endpoints.MapGet(
                    $"/html-{statusCode}",
                    httpContext =>
                    {
                        httpContext.Response.StatusCode = statusCode;
                        httpContext.Response.ContentType = "text/html";
                        return httpContext.Response.WriteAsync($"<p>original {statusCode} body</p>");
                    });

                endpoints.MapGet(
                    $"/json-{statusCode}",
                    httpContext =>
                    {
                        httpContext.Response.StatusCode = statusCode;
                        httpContext.Response.ContentType = "application/json";
                        return httpContext.Response.WriteAsync($"{{\"status\":{statusCode}}}");
                    });
            }

            endpoints.MapPost(
                "/empty-post-401",
                httpContext =>
                {
                    httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                });
        }
    }

    private sealed class LocalOverrideWebModule : BaseTestWebModule
    {
        public override bool IncludeAsApplicationPart => true;

        public override void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
            builder.AddModule<SharedErrorPagesFixtureModule>();
        }
    }

    private sealed class RunningAppHandle : IAsyncDisposable
    {
        public RunningAppHandle(IHost host, string baseUrl)
        {
            Host = host;
            BaseUrl = baseUrl;
            Client = new HttpClient
            {
                BaseAddress = new Uri(baseUrl)
            };
        }

        public IHost Host { get; }

        public string BaseUrl { get; }

        public HttpClient Client { get; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Host.StopAsync();
            Host.Dispose();
        }
    }

    private sealed class CapturingViewResultExecutor : IActionResultExecutor<ViewResult>
    {
        public BrowserStatusPageModel? Model { get; private set; }

        public Task ExecuteAsync(ActionContext context, ViewResult result)
        {
            Model = Assert.IsType<BrowserStatusPageModel>(result.ViewData?.Model);
            return Task.CompletedTask;
        }
    }

    private sealed class StubCompositeViewEngine : ICompositeViewEngine
    {
        private readonly IReadOnlyDictionary<string, ViewEngineResult> _viewResults;
        private readonly ViewEngineResult _defaultFrameworkResult;
        private readonly bool _reportDefaultSearchedLocations;

        public StubCompositeViewEngine(
            IReadOnlyDictionary<string, ViewEngineResult> viewResults,
            ViewEngineResult? defaultFrameworkResult = null,
            bool reportDefaultSearchedLocations = true)
        {
            _viewResults = viewResults;
            _defaultFrameworkResult = defaultFrameworkResult ?? ViewEngineResult.NotFound(
                BrowserStatusPageDefaults.FrameworkFallbackViewPath,
                []);
            _reportDefaultSearchedLocations = reportDefaultSearchedLocations;
        }

        public Dictionary<string, int> GetViewCalls { get; } = [];

        public IReadOnlyList<IViewEngine> ViewEngines => [];

        public ViewEngineResult FindView(ActionContext context, string viewName, bool isMainPage)
        {
            return ViewEngineResult.NotFound(viewName, []);
        }

        public ViewEngineResult GetView(string? executingFilePath, string viewPath, bool isMainPage)
        {
            GetViewCalls[viewPath] = GetViewCalls.GetValueOrDefault(viewPath) + 1;

            if (_viewResults.TryGetValue(viewPath, out var result))
            {
                return result;
            }

            if (viewPath == BrowserStatusPageDefaults.FrameworkFallbackViewPath)
            {
                return _defaultFrameworkResult;
            }

            return ViewEngineResult.NotFound(
                viewPath,
                _reportDefaultSearchedLocations ? [$"/Views/Shared/{Path.GetFileName(viewPath)}"] : []);
        }
    }

    private sealed class StubView(string path) : IView
    {
        public string Path { get; } = path;

        public Task RenderAsync(ViewContext context) => Task.CompletedTask;
    }

    private sealed class StubRoutingFeature(RouteData routeData) : IRoutingFeature
    {
        public RouteData? RouteData { get; set; } = routeData;
    }
}
