using System.Security.Claims;
using ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Tests;

public sealed partial class AppSurfaceDevAuthEndpointTests
{
    private const string PersonaCookieProtectorPurpose = "ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Persona.v1";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectPersona_InvokesOptionalHandlerOnceWithConfiguredPersonaAndRequest(bool taskBackedCompletion)
    {
        var probe = new PersonaSelectionProbe
        {
            Activation = taskBackedCompletion
                ? static (_, _, _) => new ValueTask(Task.CompletedTask)
                : static (_, _, _) => ValueTask.CompletedTask,
        };
        await using var app = BuildHostedAppWithProbe(probe, options =>
        {
            AddPersonasWithViewerLandingUrl(options);
            options.UseAsDefaultSchemeForLocalProof = true;
        });
        Assert.Equal(0, probe.ResolutionCount);
        using var client = await StartLoopbackHttpClientAsync(app);
        var incomingAdminCookie = $"{AppSurfaceDevAuthDefaults.CookieName}={ProtectPersonaId(app.Services, "admin")}";
        using var response = await PostSelectionAsync(client, "viewer", "?returnUrl=%2Fselected", incomingAdminCookie);

        var activation = Assert.Single(probe.Activations);
        var configuredPersona = app.Services.GetRequiredService<IOptions<AppSurfaceDevAuthOptions>>()
            .Value.Users.Personas["viewer"];
        Assert.Same(configuredPersona, activation.Persona);
        Assert.Same(probe.UserBeforeRequest, probe.UserAfterRequest);
        Assert.Same(probe.UserBeforeRequest, activation.UserAtInvocation);
        Assert.Equal("admin-1", activation.UserAtInvocation.FindFirst("sub")?.Value);
        Assert.Equal("operator", activation.UserAtInvocation.FindFirst("role")?.Value);
        Assert.Equal(incomingAdminCookie, activation.RequestCookieHeader);
        Assert.Equal(probe.RequestAbortedBeforeRequest, activation.CancellationToken);
        Assert.Equal(probe.RequestAbortedBeforeRequest, probe.RequestAbortedAfterRequest);
        Assert.NotSame(app.Services, activation.RequestServices);
        Assert.Equal(1, probe.ResolutionCount);
        Assert.Equal(1, probe.InvocationCount);

        Assert.Contains(AppSurfaceDevAuthDefaults.CookieName, activation.SetCookieHeader, StringComparison.Ordinal);
        var cookieValue = activation.SetCookieHeader.Split(';', 2)[0].Split('=', 2)[1];
        var protectedPersonaId = app.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(PersonaCookieProtectorPurpose)
            .Unprotect(cookieValue);
        Assert.Equal("viewer", protectedPersonaId);
        Assert.Equal(StatusCodes.Status302Found, (int)response.StatusCode);
        Assert.Equal("/selected", response.Headers.Location?.ToString());
        Assert.Equal(activation.SetCookieHeader, Assert.Single(response.Headers.GetValues("Set-Cookie")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SelectPersona_AwaitsHandlerBeforeRedirectOrControlPage(bool hasRedirectTarget)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new PersonaSelectionProbe
        {
            Activation = async (_, _, _) =>
            {
                entered.TrySetResult();
                await release.Task;
            },
        };
        await using var app = BuildHostedAppWithProbe(probe, options =>
        {
            AddDefaultPersonas(options);
            options.UseAsDefaultSchemeForLocalProof = true;
        });
        using var client = await StartLoopbackHttpClientAsync(app);
        var responseTask = PostSelectionAsync(
            client,
            "admin",
            hasRedirectTarget ? "?returnUrl=%2Fready" : "");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(responseTask.IsCompleted);
        var activation = Assert.Single(probe.Activations);
        Assert.Contains(AppSurfaceDevAuthDefaults.CookieName, activation.SetCookieHeader, StringComparison.Ordinal);
        Assert.Equal(1, probe.InvocationCount);

        release.TrySetResult();
        using var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));

        if (hasRedirectTarget)
        {
            Assert.Equal(StatusCodes.Status302Found, (int)response.StatusCode);
            Assert.Equal("/ready", response.Headers.Location?.ToString());
            Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        }
        else
        {
            Assert.Equal(StatusCodes.Status200OK, (int)response.StatusCode);
            Assert.Null(response.Headers.Location);
            Assert.Contains("Local Admin", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SelectPersona_UsesOneRequestScopeAndDisposesScopedDependenciesAfterEachRequest()
    {
        var probe = new PersonaSelectionProbe();
        await using var app = BuildHostedAppWithProbe(probe, options =>
        {
            AddPersonasWithViewerLandingUrl(options);
            options.UseAsDefaultSchemeForLocalProof = true;
        });

        Assert.Equal(0, probe.ResolutionCount);
        using var client = await StartLoopbackHttpClientAsync(app);
        Assert.Equal(0, probe.ResolutionCount);
        using var firstResponse = await PostSelectionAsync(client, "admin");
        Assert.Equal(StatusCodes.Status200OK, (int)firstResponse.StatusCode);
        var first = Assert.Single(probe.Activations);
        Assert.Same(first.InjectedDependency, first.ContextDependency);
        Assert.True(first.InjectedDependency.IsDisposed);

        using var secondResponse = await PostSelectionAsync(client, "viewer");
        Assert.Equal(StatusCodes.Status302Found, (int)secondResponse.StatusCode);
        var second = Assert.Single(probe.Activations, activation => !ReferenceEquals(activation, first));
        Assert.Same(second.InjectedDependency, second.ContextDependency);
        Assert.True(second.InjectedDependency.IsDisposed);
        Assert.NotSame(first.Handler, second.Handler);
        Assert.NotSame(first.InjectedDependency, second.InjectedDependency);
        Assert.NotSame(first.RequestServices, second.RequestServices);
        Assert.Equal(2, probe.ResolutionCount);
        Assert.Equal(2, probe.InvocationCount);
        Assert.Equal(2, probe.Dependencies.Count);
    }

    [Fact]
    public async Task SelectPersona_WhenHandlerResolutionFails_PropagatesOriginalExceptionAfterQueuingCookie()
    {
        var expected = new InvalidOperationException("test resolution failure");
        var resolutionAttempts = 0;
        await using var app = BuildApp(
            AddDefaultPersonas,
            configureServices: services => services.AddScoped<IAppSurfaceDevAuthPersonaSelectionHandler>(_ =>
            {
                Interlocked.Increment(ref resolutionAttempts);
                throw expected;
            }));
        using var scope = app.Services.CreateScope();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/select/{personaId}", HttpMethods.Post);
        var context = CreateContext(scope.ServiceProvider);
        context.Request.RouteValues["personaId"] = "admin";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => endpoint.RequestDelegate!(context));

        Assert.Same(expected, exception);
        Assert.Equal(1, resolutionAttempts);
        Assert.Contains(AppSurfaceDevAuthDefaults.CookieName, context.Response.Headers.SetCookie.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, context.Response.Headers.Location.Count);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectPersona_WhenHandlerThrowsOrFaults_PropagatesSameExceptionWithoutRetry(bool asynchronousFault)
    {
        var expected = new InvalidOperationException("test activation failure");
        var probe = new PersonaSelectionProbe
        {
            Activation = asynchronousFault
                ? (_, _, _) => ValueTask.FromException(expected)
                : (_, _, _) => throw expected,
        };
        await using var app = BuildAppWithProbe(probe);
        using var scope = app.Services.CreateScope();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/select/{personaId}", HttpMethods.Post);
        var context = CreateContext(scope.ServiceProvider);
        context.Request.RouteValues["personaId"] = "admin";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => endpoint.RequestDelegate!(context));

        Assert.Same(expected, exception);
        Assert.Equal(1, probe.ResolutionCount);
        Assert.Equal(1, probe.InvocationCount);
        Assert.Single(probe.Activations);
        AssertSelectionFailureResponse(context);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SelectPersona_WhenRequestIsAlreadyAborted_DoesNotInvokeHandler(bool hasRedirectTarget)
    {
        var probe = new PersonaSelectionProbe();
        await using var app = BuildAppWithProbe(probe);
        using var scope = app.Services.CreateScope();
        using var requestAborted = new CancellationTokenSource();
        requestAborted.Cancel();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/select/{personaId}", HttpMethods.Post);
        var context = CreateContext(scope.ServiceProvider);
        context.RequestAborted = requestAborted.Token;
        context.Request.RouteValues["personaId"] = "admin";
        SetRedirectTarget(context, hasRedirectTarget);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => endpoint.RequestDelegate!(context));

        Assert.Equal(requestAborted.Token, exception.CancellationToken);
        Assert.Equal(1, probe.ResolutionCount);
        Assert.Equal(0, probe.InvocationCount);
        Assert.Empty(probe.Activations);
        AssertSelectionFailureResponse(context);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SelectPersona_WhenHandlerCooperatesWithRequestCancellation_PropagatesCancellation(bool hasRedirectTarget)
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new PersonaSelectionProbe
        {
            Activation = async (_, _, cancellationToken) =>
            {
                entered.TrySetResult(cancellationToken);
                using var registration = cancellationToken.Register(() => cancellationObserved.TrySetResult(cancellationToken));
                await release.Task;
                cancellationToken.ThrowIfCancellationRequested();
            },
        };
        await using var app = BuildAppWithProbe(probe);
        using var scope = app.Services.CreateScope();
        using var requestAborted = new CancellationTokenSource();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/select/{personaId}", HttpMethods.Post);
        var context = CreateContext(scope.ServiceProvider);
        context.RequestAborted = requestAborted.Token;
        context.Request.RouteValues["personaId"] = "admin";
        SetRedirectTarget(context, hasRedirectTarget);

        var responseTask = endpoint.RequestDelegate!(context);
        Assert.Equal(requestAborted.Token, await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        requestAborted.Cancel();
        Assert.Equal(requestAborted.Token, await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        release.TrySetResult();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => responseTask);

        Assert.Equal(requestAborted.Token, exception.CancellationToken);
        Assert.Equal(1, probe.InvocationCount);
        Assert.Equal(requestAborted.Token, Assert.Single(probe.Activations).CancellationToken);
        AssertSelectionFailureResponse(context);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    // Value: protects=captured cancellation prevents normal selection response; fails_when=post-await check rereads a replaced token;
    // why_new=existing ignored-cancellation assertion leaves RequestAborted unchanged; seam=none
    public async Task SelectPersona_WhenHandlerIgnoresCancellation_DoesNotNavigateAfterNormalReturn(bool hasRedirectTarget)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new PersonaSelectionProbe
        {
            Activation = async (_, activationContext, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                activationContext.RequestAborted = CancellationToken.None;
            },
        };
        await using var app = BuildAppWithProbe(probe);
        using var scope = app.Services.CreateScope();
        using var requestAborted = new CancellationTokenSource();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/select/{personaId}", HttpMethods.Post);
        var context = CreateContext(scope.ServiceProvider);
        context.RequestAborted = requestAborted.Token;
        context.Request.RouteValues["personaId"] = "admin";
        SetRedirectTarget(context, hasRedirectTarget);

        var responseTask = endpoint.RequestDelegate!(context);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        requestAborted.Cancel();
        release.TrySetResult();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => responseTask);

        Assert.Equal(requestAborted.Token, exception.CancellationToken);
        Assert.Equal(1, probe.InvocationCount);
        Assert.Equal(requestAborted.Token, Assert.Single(probe.Activations).CancellationToken);
        Assert.Equal(CancellationToken.None, context.RequestAborted);
        AssertSelectionFailureResponse(context);
    }

    [Theory]
    [InlineData("?returnUrl=%2Fsafe", StatusCodes.Status302Found, "/safe")]
    [InlineData("?returnUrl=https%3A%2F%2Fexample.com%2F", StatusCodes.Status200OK, "")]
    [InlineData("", StatusCodes.Status200OK, "")]
    public async Task SelectPersona_WithoutHandler_PreservesNavigationCookieAndCancellationBehavior(
        string queryString,
        int expectedStatusCode,
        string expectedLocation)
    {
        await using var app = BuildApp();
        using var scope = app.Services.CreateScope();
        using var requestAborted = new CancellationTokenSource();
        requestAborted.Cancel();
        var endpoint = FindEndpoint(app, "/_appsurface/dev-auth/select/{personaId}", HttpMethods.Post);
        var context = CreateContext(scope.ServiceProvider);
        context.RequestAborted = requestAborted.Token;
        context.Request.RouteValues["personaId"] = "admin";
        context.Request.QueryString = new QueryString(queryString);

        await endpoint.RequestDelegate!(context);

        var setCookie = context.Response.Headers.SetCookie.ToString();
        Assert.Equal(expectedStatusCode, context.Response.StatusCode);
        Assert.Equal(expectedLocation, context.Response.Headers.Location.ToString());
        Assert.Contains(AppSurfaceDevAuthDefaults.CookieName, setCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-store, no-cache", context.Response.Headers.CacheControl);
        if (expectedStatusCode == StatusCodes.Status200OK)
        {
            Assert.Contains("Local Admin", await ReadBodyAsync(context), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task NonSelectionRequests_DoNotResolveOrConstructPersonaSelectionHandler()
    {
        var probe = new PersonaSelectionProbe();
        await using var app = BuildHostedAppWithProbe(probe, options =>
        {
            AddDefaultPersonas(options);
            options.UseAsDefaultSchemeForLocalProof = true;
        });
        Assert.Equal(0, probe.ResolutionCount);

        using (var scope = app.Services.CreateScope())
        {
            var controlEndpoint = FindEndpoint(app, "/_appsurface/dev-auth/", HttpMethods.Get);
            await controlEndpoint.RequestDelegate!(CreateContext(scope.ServiceProvider));
        }
        AssertNoPersonaSelectionActivation(probe);

        using (var scope = app.Services.CreateScope())
        {
            var statusEndpoint = FindEndpoint(app, "/_appsurface/dev-auth/status", HttpMethods.Get);
            await statusEndpoint.RequestDelegate!(CreateContext(scope.ServiceProvider));
        }
        AssertNoPersonaSelectionActivation(probe);

        var markerContext = CreateContext(app.Services);
        _ = AppSurfaceDevAuthMarker.Render(
            markerContext,
            app.Services.GetRequiredService<IHostEnvironment>(),
            app.Services.GetRequiredService<IOptions<AppSurfaceDevAuthOptions>>(),
            app.Services.GetRequiredService<IDataProtectionProvider>());
        AssertNoPersonaSelectionActivation(probe);

        using (var scope = app.Services.CreateScope())
        {
            var authContext = CreateContext(scope.ServiceProvider);
            authContext.Request.Headers.Cookie = $"{AppSurfaceDevAuthDefaults.CookieName}={ProtectPersonaId(app.Services, "admin")}";
            var result = await scope.ServiceProvider.GetRequiredService<IAuthenticationService>()
                .AuthenticateAsync(authContext, AppSurfaceDevAuthDefaults.AuthenticationScheme);
            Assert.True(result.Succeeded);
        }
        AssertNoPersonaSelectionActivation(probe);

        using (var scope = app.Services.CreateScope())
        {
            var clearEndpoint = FindEndpoint(app, "/_appsurface/dev-auth/clear", HttpMethods.Post);
            await clearEndpoint.RequestDelegate!(CreateContext(scope.ServiceProvider));
        }
        AssertNoPersonaSelectionActivation(probe);

        var selectEndpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => string.Equals(
                endpoint.RoutePattern.RawText,
                "/_appsurface/dev-auth/select/{personaId}",
                StringComparison.Ordinal))
            .ToArray();
        Assert.Single(selectEndpoints);
        Assert.Equal([HttpMethods.Post], selectEndpoints[0].Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods);
        AssertNoPersonaSelectionActivation(probe);

        using var client = await StartLoopbackHttpClientAsync(app);
        using var getSelectionResponse = await client.GetAsync("/_appsurface/dev-auth/select/admin");
        Assert.Equal(StatusCodes.Status405MethodNotAllowed, (int)getSelectionResponse.StatusCode);
        AssertNoPersonaSelectionActivation(probe);
    }

    private static WebApplication BuildAppWithProbe(
        PersonaSelectionProbe probe,
        Action<AppSurfaceDevAuthOptions>? configureDevAuth = null,
        bool singletonHandler = false)
    {
        return BuildApp(
            configureDevAuth ?? AddDefaultPersonas,
            configureServices: services =>
            {
                if (singletonHandler)
                {
                    AddPersonaSelectionResolutionProbe(services, probe);
                }
                else
                {
                    AddPersonaSelectionProbe(services, probe);
                }
            });
    }

    private static WebApplication BuildHostedAppWithProbe(
        PersonaSelectionProbe probe,
        Action<AppSurfaceDevAuthOptions> configureDevAuth)
    {
        return BuildApp(
            configureDevAuth,
            configureServices: services =>
            {
                services.AddAuthentication(AppSurfaceDevAuthDefaults.AuthenticationScheme);
                AddPersonaSelectionProbe(services, probe);
            },
            configureWebHost: webHost => webHost.UseUrls("http://127.0.0.1:0"),
            configurePipeline: app =>
            {
                app.UseAuthentication();
                app.Use(async (context, next) =>
                {
                    probe.UserBeforeRequest = context.User;
                    probe.RequestAbortedBeforeRequest = context.RequestAborted;
                    await next();
                    probe.UserAfterRequest = context.User;
                    probe.RequestAbortedAfterRequest = context.RequestAborted;
                });
            });
    }

    private static void AddPersonaSelectionProbe(IServiceCollection services, PersonaSelectionProbe probe)
    {
        services.AddSingleton(probe);
        services.AddScoped<ScopedActivationDependency>(_ => probe.CreateDependency());
        services.AddScoped<IAppSurfaceDevAuthPersonaSelectionHandler>(serviceProvider =>
        {
            Interlocked.Increment(ref probe.ResolutionCount);
            return new ProbePersonaSelectionHandler(
                probe,
                serviceProvider.GetRequiredService<ScopedActivationDependency>());
        });
    }

    private static void AddPersonaSelectionResolutionProbe(IServiceCollection services, PersonaSelectionProbe probe)
    {
        services.AddSingleton(probe);
        services.AddSingleton<IAppSurfaceDevAuthPersonaSelectionHandler>(_ =>
        {
            Interlocked.Increment(ref probe.ResolutionCount);
            return new ResolutionOnlyPersonaSelectionHandler(probe);
        });
    }

    private static void AssertNoPersonaSelectionActivation(PersonaSelectionProbe probe)
    {
        Assert.Equal(0, probe.ResolutionCount);
        Assert.Equal(0, probe.InvocationCount);
        Assert.Empty(probe.Activations);
        Assert.Empty(probe.Dependencies);
    }

    private static async Task<HttpClient> StartLoopbackHttpClientAsync(WebApplication app)
    {
        await app.StartAsync();
        var address = Assert.Single(app.Urls);
        return new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        })
        {
            BaseAddress = new Uri(address),
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    private static async Task<HttpResponseMessage> PostSelectionAsync(
        HttpClient client,
        string personaId,
        string queryString = "",
        string? cookieHeader = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/_appsurface/dev-auth/select/{Uri.EscapeDataString(personaId)}{queryString}");
        if (cookieHeader is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        return await client.SendAsync(request, HttpCompletionOption.ResponseContentRead);
    }

    private static void SetRedirectTarget(HttpContext context, bool hasRedirectTarget)
    {
        if (hasRedirectTarget)
        {
            context.Request.QueryString = new QueryString("?returnUrl=%2Fafter-activation");
        }
    }

    private static void AssertSelectionFailureResponse(HttpContext context)
    {
        Assert.Contains(AppSurfaceDevAuthDefaults.CookieName, context.Response.Headers.SetCookie.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, context.Response.Headers.Location.Count);
        Assert.Equal(0, context.Response.Body.Length);
    }

    private sealed class PersonaSelectionProbe
    {
        public int ResolutionCount;

        public int InvocationCount;

        public ClaimsPrincipal? UserBeforeRequest { get; set; }

        public ClaimsPrincipal? UserAfterRequest { get; set; }

        public CancellationToken RequestAbortedBeforeRequest { get; set; }

        public CancellationToken RequestAbortedAfterRequest { get; set; }

        public List<ActivationObservation> Activations { get; } = [];

        public List<ScopedActivationDependency> Dependencies { get; } = [];

        public Func<AppSurfaceDevAuthPersona, HttpContext, CancellationToken, ValueTask> Activation { get; set; }
            = static (_, _, _) => ValueTask.CompletedTask;

        public ScopedActivationDependency CreateDependency()
        {
            var dependency = new ScopedActivationDependency();
            Dependencies.Add(dependency);
            return dependency;
        }

        public ValueTask InvokeAsync(
            ProbePersonaSelectionHandler handler,
            ScopedActivationDependency dependency,
            AppSurfaceDevAuthPersona persona,
            HttpContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref InvocationCount);
            var observation = new ActivationObservation(
                handler,
                dependency,
                persona,
                context,
                context.RequestServices.GetRequiredService<ScopedActivationDependency>(),
                context.RequestServices,
                context.User,
                cancellationToken,
                context.Response.Headers.SetCookie.ToString(),
                context.Request.Headers.Cookie.ToString());
            Activations.Add(observation);
            return Activation(persona, context, cancellationToken);
        }
    }

    private sealed class ProbePersonaSelectionHandler(
        PersonaSelectionProbe probe,
        ScopedActivationDependency dependency) : IAppSurfaceDevAuthPersonaSelectionHandler
    {
        public ValueTask ActivateAsync(
            AppSurfaceDevAuthPersona persona,
            HttpContext httpContext,
            CancellationToken cancellationToken)
        {
            return probe.InvokeAsync(this, dependency, persona, httpContext, cancellationToken);
        }
    }

    private sealed class ResolutionOnlyPersonaSelectionHandler(PersonaSelectionProbe probe)
        : IAppSurfaceDevAuthPersonaSelectionHandler
    {
        public ValueTask ActivateAsync(
            AppSurfaceDevAuthPersona persona,
            HttpContext httpContext,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref probe.InvocationCount);
            return probe.Activation(persona, httpContext, cancellationToken);
        }
    }

    private sealed class ScopedActivationDependency : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private sealed record ActivationObservation(
        ProbePersonaSelectionHandler Handler,
        ScopedActivationDependency InjectedDependency,
        AppSurfaceDevAuthPersona Persona,
        HttpContext Context,
        ScopedActivationDependency ContextDependency,
        IServiceProvider RequestServices,
        ClaimsPrincipal UserAtInvocation,
        CancellationToken CancellationToken,
        string SetCookieHeader,
        string RequestCookieHeader);
}
