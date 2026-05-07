using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using ForgeTrust.Runnable.Core;
using Microsoft.AspNetCore.Builder;
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

public sealed class ConventionalExceptionPageTests
{
    [Fact]
    public async Task EnabledMode_InProduction_RendersFrameworkFallback500_WithRequestId()
    {
        await using var runningApp = await StartHostAsync(
            new ThrowingEndpointWebModule(),
            options => options.Errors.UseConventionalExceptionPage());

        using var request = CreateHtmlRequest(HttpMethod.Get, "/throws-with-request-id");

        using var response = await runningApp.Client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Something went wrong", html);
        Assert.Contains("Request id:", html);
        Assert.DoesNotContain("Runnable default 500", html);
        Assert.DoesNotContain("App owners", html);
        Assert.DoesNotContain("~/Views/Shared/500.cshtml", html);
    }

    [Fact]
    public async Task EnabledMode_InDevelopment_DoesNotReplaceDeveloperExceptionBehavior()
    {
        await using var runningApp = await StartHostAsync(
            new ThrowingEndpointWebModule(),
            options => options.Errors.UseConventionalExceptionPage(),
            environmentName: Environments.Development);

        using var request = CreateHtmlRequest(HttpMethod.Get, "/throws-with-request-id");
        string? body = null;

        var exception = await Record.ExceptionAsync(async () =>
        {
            using var response = await runningApp.Client.SendAsync(request);
            body = await response.Content.ReadAsStringAsync();
        });

        if (exception is null)
        {
            Assert.DoesNotContain("Something went wrong", body);
        }
    }

    [Fact]
    public async Task Empty500StatusResponses_DoNotRenderConventionalExceptionPage()
    {
        await using var runningApp = await StartHostAsync(
            new StatusCodeEndpointWebModule(),
            options => options.Errors.UseConventionalExceptionPage());

        using var request = CreateHtmlRequest(HttpMethod.Get, "/empty-500");

        using var response = await runningApp.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(string.Empty, body);
    }

    [Fact]
    public async Task ModuleMiddlewareExceptions_AreCaughtBeforeRouting()
    {
        await using var runningApp = await StartHostAsync(
            new ThrowingMiddlewareWebModule(),
            options => options.Errors.UseConventionalExceptionPage());

        using var request = CreateHtmlRequest(HttpMethod.Get, "/middleware-throws");

        using var response = await runningApp.Client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("Something went wrong", html);
    }

    [Fact]
    public async Task HtmlPostExceptions_DoNotLeakExceptionOrRequestDetails()
    {
        await using var runningApp = await StartHostAsync(
            new ThrowingEndpointWebModule(),
            options => options.Errors.UseConventionalExceptionPage());

        using var request = CreateHtmlRequest(HttpMethod.Post, "/leak-route/secret-route-value");
        request.Headers.Add("X-Leak-Header", "secret-header-value");
        request.Headers.Add("Cookie", "leak-cookie=secret-cookie-value");
        request.Content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("leak-form-field", "secret-form-value")
        ]);

        using var response = await runningApp.Client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("Something went wrong", html);
        Assert.DoesNotContain(ThrowingEndpointWebModule.SecretExceptionMessage, html);
        Assert.DoesNotContain("secret-header-value", html);
        Assert.DoesNotContain("secret-cookie-value", html);
        Assert.DoesNotContain("secret-route-value", html);
        Assert.DoesNotContain("secret-form-value", html);
        Assert.DoesNotContain("StackTrace", html);
    }

    [Fact]
    public async Task JsonRequests_DoNotRenderConventionalExceptionPage()
    {
        await using var runningApp = await StartHostAsync(
            new ThrowingEndpointWebModule(),
            options => options.Errors.UseConventionalExceptionPage());

        using var request = new HttpRequestMessage(HttpMethod.Get, "/throws-with-request-id");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await runningApp.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("Something went wrong", body);
    }

    [Fact]
    public async Task ResponseStartedExceptions_DoNotRenderConventionalExceptionPage()
    {
        await using var runningApp = await StartHostAsync(
            new ResponseStartedWebModule(),
            options => options.Errors.UseConventionalExceptionPage());

        using var request = CreateHtmlRequest(HttpMethod.Get, "/throws-after-start");
        string? body = null;

        var exception = await Record.ExceptionAsync(async () =>
        {
            using var response = await runningApp.Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead);

            body = await response.Content.ReadAsStringAsync();
        });

        if (exception is null)
        {
            Assert.DoesNotContain("Something went wrong", body);
            Assert.Contains(ResponseStartedWebModule.StartedBody, body);
        }
    }

    [Fact]
    public void ShouldApplyConventionalExceptionPage_ReturnsFalse_WhenRequestDoesNotPreferHtml()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Accept = "application/json";

        var shouldApply = WebStartup<ThrowingEndpointWebModule>.ShouldApplyConventionalExceptionPage(httpContext);

        Assert.False(shouldApply);
    }

    [Fact]
    public void ShouldApplyConventionalExceptionPage_ReturnsTrue_WhenRequestPrefersXhtml()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Accept = "application/xhtml+xml";

        var shouldApply = WebStartup<ThrowingEndpointWebModule>.ShouldApplyConventionalExceptionPage(httpContext);

        Assert.True(shouldApply);
    }

    [Fact]
    public void DisableConventionalExceptionPage_ClearsEnabledFlag()
    {
        var options = new ErrorPagesOptions();
        options.UseConventionalExceptionPage();

        options.DisableConventionalExceptionPage();

        Assert.False(options.ConventionalExceptionPageEnabled);
    }

    [Fact]
    public void ValidateConfiguredViews_ThrowsWithSearchedLocations_WhenNoViewsResolve()
    {
        var renderer = CreateRenderer(
            new StubCompositeViewEngine(
                appResult: ViewEngineResult.NotFound(ConventionalExceptionPageDefaults.AppViewPath, ["/Views/Shared/500.cshtml"]),
                frameworkResult: ViewEngineResult.NotFound(ConventionalExceptionPageDefaults.FrameworkFallbackViewPath, ["/Views/_Runnable/Errors/500.cshtml"])));

        var exception = Assert.Throws<InvalidOperationException>(() => renderer.ValidateConfiguredViews());

        Assert.Contains("/Views/Shared/500.cshtml", exception.Message);
        Assert.Contains("/Views/_Runnable/Errors/500.cshtml", exception.Message);
    }

    [Fact]
    public void ValidateConfiguredViews_ThrowsFallbackMessage_WhenViewEngineReportsNoLocations()
    {
        var renderer = CreateRenderer(
            new StubCompositeViewEngine(
                appResult: ViewEngineResult.NotFound(ConventionalExceptionPageDefaults.AppViewPath, []),
                frameworkResult: ViewEngineResult.NotFound(ConventionalExceptionPageDefaults.FrameworkFallbackViewPath, [])));

        var exception = Assert.Throws<InvalidOperationException>(() => renderer.ValidateConfiguredViews());

        Assert.Contains("No Razor view locations were reported.", exception.Message);
    }

    [Fact]
    public void ValidateConfiguredViews_CachesResolvedViewPath()
    {
        var viewEngine = new StubCompositeViewEngine(
            appResult: ViewEngineResult.Found(ConventionalExceptionPageDefaults.AppViewPath, new StubView(ConventionalExceptionPageDefaults.AppViewPath)));
        var renderer = CreateRenderer(viewEngine);

        renderer.ValidateConfiguredViews();
        renderer.ValidateConfiguredViews();

        Assert.Equal(1, viewEngine.GetViewCallCount(ConventionalExceptionPageDefaults.AppViewPath));
        Assert.Equal(0, viewEngine.GetViewCallCount(ConventionalExceptionPageDefaults.FrameworkFallbackViewPath));
    }

    [Fact]
    public async Task RenderAsync_UsesSafeModel_With500StatusAndRequestId()
    {
        var executor = new CapturingViewResultExecutor();
        var renderer = CreateRenderer(
            new StubCompositeViewEngine(
                appResult: ViewEngineResult.Found(ConventionalExceptionPageDefaults.AppViewPath, new StubView(ConventionalExceptionPageDefaults.AppViewPath))),
            executor);
        var previousCurrent = Activity.Current;
        Activity.Current = null;

        try
        {
            var httpContext = new DefaultHttpContext
            {
                TraceIdentifier = "renderer-request-id"
            };

            await renderer.RenderAsync(httpContext);

            Assert.Equal(StatusCodes.Status500InternalServerError, httpContext.Response.StatusCode);
            Assert.Equal(StatusCodes.Status500InternalServerError, executor.Model?.StatusCode);
            Assert.Equal("renderer-request-id", executor.Model?.RequestId);
        }
        finally
        {
            Activity.Current = previousCurrent;
        }
    }

    [Fact]
    public async Task RenderAsync_UsesActivityId_WhenAvailable()
    {
        var executor = new CapturingViewResultExecutor();
        var renderer = CreateRenderer(
            new StubCompositeViewEngine(
                appResult: ViewEngineResult.Found(ConventionalExceptionPageDefaults.AppViewPath, new StubView(ConventionalExceptionPageDefaults.AppViewPath))),
            executor);
        using var activity = new Activity("conventional-exception-page-test");
        var previousCurrent = Activity.Current;
        activity.Start();
        var httpContext = new DefaultHttpContext
        {
            TraceIdentifier = "renderer-request-id"
        };

        try
        {
            await renderer.RenderAsync(httpContext);
        }
        finally
        {
            activity.Stop();
            Activity.Current = previousCurrent;
        }

        Assert.Equal(activity.Id, executor.Model?.RequestId);
    }

    private static HttpRequestMessage CreateHtmlRequest(HttpMethod method, string requestUri)
    {
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        return request;
    }

    private static async Task<RunningAppHandle> StartHostAsync<TModule>(
        TModule module,
        Action<WebOptions> configureOptions,
        string environmentName = "Production")
        where TModule : class, IRunnableWebModule, new()
    {
        var startup = new TestWebStartup<TModule>(module);
        startup.WithOptions(configureOptions);

        var context = new StartupContext(
            [],
            module,
            EnvironmentProvider: new TestEnvironmentProvider(environmentName));
        context.OverrideEntryPointAssembly = typeof(WebApplication).Assembly;

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

    private static ConventionalExceptionPageRenderer CreateRenderer(
        ICompositeViewEngine viewEngine,
        IActionResultExecutor<ViewResult>? executor = null)
    {
        return new ConventionalExceptionPageRenderer(
            executor ?? new CapturingViewResultExecutor(),
            viewEngine,
            new EmptyModelMetadataProvider(),
            NullLogger<ConventionalExceptionPageRenderer>.Instance);
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

    private sealed class ThrowingEndpointWebModule : BaseTestWebModule
    {
        public const string ExpectedRequestId = "request-id-224";
        public const string SecretExceptionMessage = "secret-exception-message";

        public override void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet(
                "/throws-with-request-id",
                httpContext =>
                {
                    httpContext.TraceIdentifier = ExpectedRequestId;
                    throw new InvalidOperationException(SecretExceptionMessage);
                });

            endpoints.MapPost(
                "/leak-route/{leakRouteValue}",
                httpContext =>
                {
                    httpContext.TraceIdentifier = ExpectedRequestId;
                    throw new InvalidOperationException(SecretExceptionMessage);
                });
        }
    }

    private sealed class StatusCodeEndpointWebModule : BaseTestWebModule
    {
        public override void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet(
                "/empty-500",
                httpContext =>
                {
                    httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    return Task.CompletedTask;
                });
        }
    }

    private sealed class ThrowingMiddlewareWebModule : BaseTestWebModule
    {
        public override void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
        {
            app.Use(
                async (httpContext, next) =>
                {
                    if (httpContext.Request.Path == "/middleware-throws")
                    {
                        throw new InvalidOperationException("middleware-secret-exception");
                    }

                    await next();
                });
        }
    }

    private sealed class ResponseStartedWebModule : BaseTestWebModule
    {
        public const string StartedBody = "started response";

        public override void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet(
                "/throws-after-start",
                async httpContext =>
                {
                    httpContext.Response.ContentType = "text/plain";
                    await httpContext.Response.WriteAsync(StartedBody);
                    await httpContext.Response.Body.FlushAsync();
                    throw new InvalidOperationException("response-started-secret-exception");
                });
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
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(30)
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
        public ExceptionPageModel? Model { get; private set; }

        public Task ExecuteAsync(ActionContext context, ViewResult result)
        {
            Model = Assert.IsType<ExceptionPageModel>(result.ViewData?.Model);
            return Task.CompletedTask;
        }
    }

    private sealed class StubCompositeViewEngine : ICompositeViewEngine
    {
        private readonly ViewEngineResult _appResult;
        private readonly ViewEngineResult _frameworkResult;
        private readonly Dictionary<string, int> _getViewCallCounts = [];

        public StubCompositeViewEngine(ViewEngineResult appResult, ViewEngineResult? frameworkResult = null)
        {
            _appResult = appResult;
            _frameworkResult = frameworkResult ?? ViewEngineResult.NotFound(
                ConventionalExceptionPageDefaults.FrameworkFallbackViewPath,
                []);
        }

        public IReadOnlyList<IViewEngine> ViewEngines => [];

        public ViewEngineResult FindView(ActionContext context, string viewName, bool isMainPage)
        {
            return ViewEngineResult.NotFound(viewName, []);
        }

        public ViewEngineResult GetView(string? executingFilePath, string viewPath, bool isMainPage)
        {
            _getViewCallCounts[viewPath] = GetViewCallCount(viewPath) + 1;

            return viewPath switch
            {
                var path when path == ConventionalExceptionPageDefaults.AppViewPath => _appResult,
                var path when path == ConventionalExceptionPageDefaults.FrameworkFallbackViewPath => _frameworkResult,
                _ => ViewEngineResult.NotFound(viewPath, [])
            };
        }

        public int GetViewCallCount(string viewPath)
        {
            return _getViewCallCounts.GetValueOrDefault(viewPath);
        }
    }

    private sealed class StubView(string path) : IView
    {
        public string Path { get; } = path;

        public Task RenderAsync(ViewContext context) => Task.CompletedTask;
    }

    private sealed class TestEnvironmentProvider(string environmentName) : IEnvironmentProvider
    {
        public string Environment { get; } = environmentName;

        public bool IsDevelopment => string.Equals(Environment, Environments.Development, StringComparison.OrdinalIgnoreCase);

        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
    }
}
