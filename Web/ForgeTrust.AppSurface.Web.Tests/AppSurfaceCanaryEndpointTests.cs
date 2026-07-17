using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ForgeTrust.AppSurface.Web.Tests.CanaryConsumerFixture;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Web.Tests;

public sealed class AppSurfaceCanaryEndpointTests
{
    private const string Name = "forwarding.alpha-evidence";
    private const string PolicyName = "DeployOperators";
    private const string WrongPolicyName = "SupportOperators";

    [Fact]
    public void MapAppSurfaceCanaries_ValidatesMapTimeContract()
    {
        IEndpointRouteBuilder nullEndpoints = null!;
        Assert.Throws<ArgumentNullException>(() => nullEndpoints.MapAppSurfaceCanaries(PolicyName));

        using var noRegistrations = BuildUnstartedApp(addCanary: false, addAuthorization: true);
        var blankPolicy = Assert.Throws<ArgumentException>(() => noRegistrations.MapAppSurfaceCanaries(" "));
        Assert.Equal("authorizationPolicyName", blankPolicy.ParamName);
        Assert.StartsWith("ASCAN111", blankPolicy.Message, StringComparison.Ordinal);
        Assert.StartsWith(
            "ASCAN112",
            Assert.Throws<InvalidOperationException>(() => noRegistrations.MapAppSurfaceCanaries(PolicyName)).Message,
            StringComparison.Ordinal);

        using var noAuthorization = BuildUnstartedApp(addCanary: true, addAuthorization: false);
        Assert.StartsWith(
            "ASCAN113",
            Assert.Throws<InvalidOperationException>(() => noAuthorization.MapAppSurfaceCanaries(PolicyName)).Message,
            StringComparison.Ordinal);

        using var invalidMode = BuildUnstartedApp(addCanary: true, addAuthorization: true);
        var modeException = Assert.Throws<ArgumentException>(() =>
            invalidMode.MapAppSurfaceCanaries(
                PolicyName,
                options => options.CompletedResponseMode = (AppSurfaceCanaryCompletedResponseMode)42));
        Assert.Equal("configure", modeException.ParamName);
        Assert.StartsWith("ASCAN116", modeException.Message, StringComparison.Ordinal);
        Assert.NotNull(invalidMode.MapAppSurfaceCanaries(PolicyName));
    }

    [Fact]
    public void MapAppSurfaceCanaries_FallsBackWhenContainerDoesNotExposeServiceProbe()
    {
        using var app = BuildUnstartedApp(addCanary: true, addAuthorization: true);
        var services = new FilteringServiceProvider(
            app.Services,
            typeof(IServiceProviderIsService));
        var endpoints = new TestEndpointRouteBuilder(
            services,
            ((IEndpointRouteBuilder)app).DataSources);

        Assert.Null(services.GetService(typeof(IServiceProviderIsService)));

        var builder = endpoints.MapAppSurfaceCanaries(PolicyName);

        Assert.NotNull(builder);
        Assert.Single(GetRouteEndpoints(endpoints));
    }

    [Fact]
    public void MapAppSurfaceCanaries_FallbackRejectsIncompleteAuthorizationServices()
    {
        var services = new ServiceCollection();
        services.AddRouting();
        ConfigureAuthorization(services);
        var policyProvider = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IAuthorizationPolicyProvider));
        Assert.True(services.Remove(policyProvider));
        services.AddSingleton(new EvaluationState(AppSurfaceCanaryStatus.Pass));
        services.AddAppSurfaceCanary<TestEvaluator>(Name);
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = false,
                ValidateScopes = true,
            });
        var filteredProvider = new FilteringServiceProvider(
            provider,
            typeof(IServiceProviderIsService));
        var endpoints = new TestEndpointRouteBuilder(
            filteredProvider,
            []);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            endpoints.MapAppSurfaceCanaries(PolicyName));

        Assert.StartsWith("ASCAN113", exception.Message, StringComparison.Ordinal);
        Assert.Empty(GetRouteEndpoints(endpoints));
    }

    [Fact]
    public void MapAppSurfaceCanaries_FallbackFailsClosedWithoutScopeFactoryOrAuthorization()
    {
        using var completeApp = BuildUnstartedApp(addCanary: true, addAuthorization: true);
        var noScopeEndpoints = new TestEndpointRouteBuilder(
            new FilteringServiceProvider(
                completeApp.Services,
                typeof(IServiceProviderIsService),
                typeof(IServiceScopeFactory)),
            ((IEndpointRouteBuilder)completeApp).DataSources);

        var noScopeException = Assert.Throws<InvalidOperationException>(() =>
            noScopeEndpoints.MapAppSurfaceCanaries(PolicyName));

        Assert.StartsWith("ASCAN113", noScopeException.Message, StringComparison.Ordinal);
        Assert.Empty(GetRouteEndpoints(noScopeEndpoints));

        using var noAuthorizationApp = BuildUnstartedApp(addCanary: true, addAuthorization: false);
        var noAuthorizationEndpoints = new TestEndpointRouteBuilder(
            new FilteringServiceProvider(
                noAuthorizationApp.Services,
                typeof(IServiceProviderIsService)),
            ((IEndpointRouteBuilder)noAuthorizationApp).DataSources);

        var noAuthorizationException = Assert.Throws<InvalidOperationException>(() =>
            noAuthorizationEndpoints.MapAppSurfaceCanaries(PolicyName));

        Assert.StartsWith("ASCAN113", noAuthorizationException.Message, StringComparison.Ordinal);
        Assert.Empty(GetRouteEndpoints(noAuthorizationEndpoints));
    }

    [Fact]
    public void MapAppSurfaceCanaries_FallbackResolvesAuthorizationInsideScope()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Host.UseDefaultServiceProvider(options => options.ValidateScopes = true);
        ConfigureAuthorization(builder.Services);
        builder.Services.AddScoped<IAuthorizationHandler, ScopedAuthorizationHandler>();
        builder.Services.AddSingleton(new EvaluationState(AppSurfaceCanaryStatus.Pass));
        builder.Services.AddAppSurfaceCanary<TestEvaluator>(Name);
        using var app = builder.Build();
        var services = new FilteringServiceProvider(
            app.Services,
            typeof(IServiceProviderIsService));
        var endpoints = new TestEndpointRouteBuilder(
            services,
            ((IEndpointRouteBuilder)app).DataSources);

        var endpoint = endpoints.MapAppSurfaceCanaries(PolicyName);

        Assert.NotNull(endpoint);
        Assert.Single(GetRouteEndpoints(endpoints));
    }

    [Fact]
    public void MapAppSurfaceCanaries_MapsOneProtectedHiddenGetRouteAndRejectsSecondMapping()
    {
        using var app = BuildUnstartedApp(addCanary: true, addAuthorization: true);

        var builder = app.MapAppSurfaceCanaries(PolicyName);

        Assert.NotNull(builder);
        var endpoint = Assert.Single(GetRouteEndpoints(app));
        Assert.Equal(AppSurfaceCanaryEndpointDefaults.RoutePattern, endpoint.RoutePattern.RawText);
        Assert.Contains(endpoint.Metadata.OfType<IAuthorizeData>(), item => item.Policy == PolicyName);
        Assert.Contains(endpoint.Metadata.OfType<IExcludeFromDescriptionMetadata>(), item => item.ExcludeFromDescription);
        Assert.NotNull(endpoint.Metadata.GetMetadata<AppSurfaceCanaryRouteMetadata>());
        Assert.Equal([HttpMethods.Get], endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods);

        var repeated = Assert.Throws<InvalidOperationException>(() => app.MapAppSurfaceCanaries(PolicyName));
        Assert.StartsWith("ASCAN114", repeated.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapAppSurfaceCanaries_AtomicallyRejectsConcurrentRepeatedMapping()
    {
        using var app = BuildUnstartedApp(addCanary: true, addAuthorization: true);

        var attempts = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                try
                {
                    app.MapAppSurfaceCanaries(PolicyName);
                    return "mapped";
                }
                catch (InvalidOperationException exception)
                {
                    return exception.Message;
                }
            })));

        Assert.Single(attempts, result => result == "mapped");
        Assert.Equal(7, attempts.Count(result => result.StartsWith("ASCAN114", StringComparison.Ordinal)));
        Assert.Single(GetRouteEndpoints(app));
    }

    [Fact]
    public void MapAppSurfaceCanaries_RejectsDuplicateNamesWhenRegistryIsBuilt()
    {
        var builder = WebApplication.CreateBuilder();
        ConfigureAuthorization(builder.Services);
        builder.Services.AddSingleton(new EvaluationState(AppSurfaceCanaryStatus.Pass));
        builder.Services.AddAppSurfaceCanary<TestEvaluator>(Name);
        builder.Services.AddAppSurfaceCanary<TestEvaluator>(Name);
        using var app = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(() => app.MapAppSurfaceCanaries(PolicyName));

        Assert.StartsWith("ASCAN102", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegistrationAlone_DoesNotExposeAnEndpoint()
    {
        await using var host = await StartHostAsync(mapEndpoint: false);

        using var response = await host.Client.GetAsync(Route(Name));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.State.InvocationCount);
    }

    [Fact]
    public async Task Endpoint_AuthorizesBeforeLookupOrEvaluation()
    {
        await using var host = await StartHostAsync();

        using var unauthorized = await host.Client.GetAsync(Route("unknown"));
        using var forbiddenRequest = AuthorizedRequest(Route(Name), "reader");
        using var forbidden = await host.Client.SendAsync(forbiddenRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(0, host.State.InvocationCount);
    }

    [Fact]
    public async Task Endpoint_IsGetOnly()
    {
        await using var host = await StartHostAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, Route(Name));
        request.Headers.Add(HeaderAuthenticationHandler.UserHeaderName, "operator");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(0, host.State.InvocationCount);
    }

    [Fact]
    public async Task Endpoint_FailsClosedWhenAllowAnonymousIsAppended()
    {
        await using var host = await StartHostAsync(allowAnonymous: true);

        using var response = await host.Client.GetAsync(Route(Name));
        var json = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("ASCAN113", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "https://github.com/forge-trust/AppSurface/blob/main/Web/ForgeTrust.AppSurface.Web/README.md#named-canary-endpoints",
            problem.RootElement.GetProperty("docsLink").GetString());
        Assert.Equal(0, host.State.InvocationCount);
        AssertNoStore(response);
    }

    [Fact]
    public async Task Endpoint_FailsClosedWhenRequiredAuthorizationMetadataIsRemoved()
    {
        await using var host = await StartHostAsync(removeAuthorizationMetadata: true);

        using var response = await host.Client.GetAsync(Route(Name));
        var json = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("ASCAN113", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, host.State.InvocationCount);
        AssertNoStore(response);
    }

    [Fact]
    public async Task Endpoint_FailsClosedWhenRequiredAuthorizationMetadataIsReplaced()
    {
        await using var host = await StartHostAsync(replaceAuthorizationMetadata: true);
        using var request = AuthorizedRequest(Route(Name));

        using var response = await host.Client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("ASCAN113", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, host.State.InvocationCount);
        AssertNoStore(response);
    }

    [Fact]
    public async Task Endpoint_FailsClosedWhenPolicyOrAuthorizationMiddlewareIsMissing()
    {
        await using var missingPolicy = await StartHostAsync(registerPolicy: false);
        using var policyRequest = AuthorizedRequest(Route(Name));
        using var policyResponse = await missingPolicy.Client.SendAsync(policyRequest);
        Assert.Equal(HttpStatusCode.InternalServerError, policyResponse.StatusCode);
        Assert.Equal(0, missingPolicy.State.InvocationCount);

        await using var missingMiddleware = await StartHostWithoutAuthorizationMiddlewareAsync();
        using var middlewareRequest = AuthorizedRequest(Route(Name));
        using var middlewareResponse = await missingMiddleware.Client.SendAsync(middlewareRequest);
        Assert.Equal(HttpStatusCode.InternalServerError, middlewareResponse.StatusCode);
        Assert.Equal(0, missingMiddleware.State.InvocationCount);
    }

    [Fact]
    public async Task Endpoint_PreservesDynamicAuthorizationPolicyProviders()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        ConfigureAuthorization(builder.Services, registerPolicy: false);
        builder.Services.AddSingleton<IAuthorizationPolicyProvider, DynamicPolicyProvider>();
        var state = new EvaluationState(AppSurfaceCanaryStatus.Pass);
        builder.Services.AddSingleton(state);
        builder.Services.AddAppSurfaceCanary<TestEvaluator>(Name);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAppSurfaceCanaries(PolicyName);
        await app.StartAsync();
        using var client = CreateClient(app);
        using var request = AuthorizedRequest(Route(Name));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, state.InvocationCount);
        await app.StopAsync();
    }

    [Theory]
    [InlineData(AppSurfaceCanaryStatus.Pass, HttpStatusCode.OK, "pass")]
    [InlineData(AppSurfaceCanaryStatus.Pending, HttpStatusCode.ServiceUnavailable, "pending")]
    [InlineData(AppSurfaceCanaryStatus.Fail, HttpStatusCode.ServiceUnavailable, "fail")]
    [InlineData(AppSurfaceCanaryStatus.Stale, HttpStatusCode.ServiceUnavailable, "stale")]
    [InlineData(AppSurfaceCanaryStatus.NotConfigured, HttpStatusCode.ServiceUnavailable, "not-configured")]
    public async Task Endpoint_DefaultMode_MapsCompletedStatuses(
        AppSurfaceCanaryStatus status,
        HttpStatusCode expectedStatusCode,
        string expectedWireStatus)
    {
        await using var host = await StartHostAsync(status: status);

        using var request = AuthorizedRequest(Route(Name));
        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.Equal(
            $"{{\"name\":\"{Name}\",\"ready\":{(status == AppSurfaceCanaryStatus.Pass ? "true" : "false")},\"status\":\"{expectedWireStatus}\"}}",
            body);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        AssertNoStore(response);
        Assert.Equal(1, host.State.InvocationCount);
    }

    [Theory]
    [InlineData(AppSurfaceCanaryStatus.Pass, "pass")]
    [InlineData(AppSurfaceCanaryStatus.Pending, "pending")]
    [InlineData(AppSurfaceCanaryStatus.Fail, "fail")]
    [InlineData(AppSurfaceCanaryStatus.Stale, "stale")]
    [InlineData(AppSurfaceCanaryStatus.NotConfigured, "not-configured")]
    public async Task Endpoint_AlwaysOkMode_PreservesJsonAndReturnsOk(
        AppSurfaceCanaryStatus status,
        string expectedWireStatus)
    {
        await using var host = await StartHostAsync(status: status, alwaysOk: true);

        using var request = AuthorizedRequest(Route(Name));
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            $"{{\"name\":\"{Name}\",\"ready\":{(status == AppSurfaceCanaryStatus.Pass ? "true" : "false")},\"status\":\"{expectedWireStatus}\"}}",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Endpoint_EmitsFullyPopulatedEnvelopeWithCanonicalTimestampsAndExactMarkerFingerprint()
    {
        using var loggerProvider = new RecordingLoggerProvider();
        var result = new AppSurfaceCanaryResult(
            AppSurfaceCanaryStatus.Pending,
            options =>
            {
                options.ObservedAt = new DateTimeOffset(2026, 7, 16, 0, 31, 2, 123, TimeSpan.FromHours(-4));
                options.MatchedCount = 0;
                options.ReasonCode = "proof-not-observed";
                options.Summary = "No fresh matching proof observed yet.";
                options.CorrelationId = "deploy-20260716-004006";
                options.AddDetail("provider.region", "us-east");
                options.AddDetail("proof.kind", "bounded-proof-kind");
            });
        await using var host = await StartHostAsync(
            resultFactory: () => result,
            allowedDetailKeys: ["proof.kind", "provider.region"],
            loggerProvider: loggerProvider);
        using var request = AuthorizedRequest(Route(Name));
        request.Headers.Add(AppSurfaceCanaryHeaderNames.Marker, " marker-secret ");
        request.Headers.Add(AppSurfaceCanaryHeaderNames.FreshSince, "2026-07-16T00:30:00-04:00");

        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(Name, root.GetProperty("name").GetString());
        Assert.False(root.GetProperty("ready").GetBoolean());
        Assert.Equal("pending", root.GetProperty("status").GetString());
        Assert.Equal(
            "sha256:6bd4a0d2fca0465d45b13c75065d55ad129f825d18b57d9cb56e20a259728315",
            root.GetProperty("markerFingerprint").GetString());
        Assert.Equal("2026-07-16T04:30:00.0000000Z", root.GetProperty("freshSince").GetString());
        Assert.Equal("2026-07-16T04:31:02.1230000Z", root.GetProperty("observedAt").GetString());
        Assert.Equal(0, root.GetProperty("matchedCount").GetInt32());
        Assert.Equal("proof-not-observed", root.GetProperty("reasonCode").GetString());
        Assert.Equal("No fresh matching proof observed yet.", root.GetProperty("summary").GetString());
        Assert.Equal("deploy-20260716-004006", root.GetProperty("correlationId").GetString());
        Assert.Equal(
            ["proof.kind", "provider.region"],
            root.GetProperty("details").EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("bounded-proof-kind", root.GetProperty("details").GetProperty("proof.kind").GetString());
        Assert.Equal("us-east", root.GetProperty("details").GetProperty("provider.region").GetString());
        Assert.Equal("marker-secret", host.State.Context?.Marker);
        Assert.DoesNotContain("marker-secret", body, StringComparison.Ordinal);
        var completionLog = Assert.Single(loggerProvider.Entries, entry => entry.EventId.Id == 62401);
        Assert.Equal(result.ObservedAt, completionLog.State["ObservedAt"]);
        Assert.Equal(0, completionLog.State["MatchedCount"]);
        foreach (var responseOnlyValue in new[]
        {
            "marker-secret",
            "sha256:",
            "proof-not-observed",
            "No fresh matching proof observed yet.",
            "deploy-20260716-004006",
            "provider.region",
            "us-east",
            "proof.kind",
            "bounded-proof-kind",
        })
        {
            Assert.DoesNotContain(responseOnlyValue, completionLog.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                completionLog.State.Values,
                value => value?.ToString()?.Contains(responseOnlyValue, StringComparison.Ordinal) == true);
        }
    }

    [Theory]
    [InlineData(AppSurfaceCanaryStatus.Pass, "pass", true)]
    [InlineData(AppSurfaceCanaryStatus.Pending, "pending", false)]
    [InlineData(AppSurfaceCanaryStatus.Fail, "fail", false)]
    [InlineData(AppSurfaceCanaryStatus.Stale, "stale", false)]
    [InlineData(AppSurfaceCanaryStatus.NotConfigured, "not-configured", false)]
    public async Task Endpoint_CompletedEvaluationEmitsOneExactPrivacyBoundedEvent(
        AppSurfaceCanaryStatus status,
        string wireStatus,
        bool ready)
    {
        using var loggerProvider = new RecordingLoggerProvider();
        await using var host = await StartHostAsync(
            status: status,
            loggerProvider: loggerProvider,
            applicationName: "canary-host",
            environmentName: "Production");
        using var request = AuthorizedRequest(Route(Name));
        request.Headers.Add(AppSurfaceCanaryHeaderNames.Marker, "private-marker");
        request.Headers.Add(AppSurfaceCanaryHeaderNames.FreshSince, "2026-07-16T04:30:00Z");

        using var response = await host.Client.SendAsync(request);

        var entry = Assert.Single(loggerProvider.Entries, candidate => candidate.EventId.Id == 62401);
        Assert.Equal("AppSurfaceCanaryEvaluationCompleted", entry.EventId.Name);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Equal(Name, entry.State["CanaryName"]);
        Assert.Equal(wireStatus, entry.State["CanaryStatus"]);
        Assert.Equal(ready, entry.State["Ready"]);
        Assert.Null(entry.State["ObservedAt"]);
        Assert.Equal(DateTimeOffset.Parse("2026-07-16T04:30:00Z"), entry.State["FreshSince"]);
        Assert.Null(entry.State["MatchedCount"]);
        Assert.IsType<double>(entry.State["ElapsedMilliseconds"]);
        Assert.True((double)entry.State["ElapsedMilliseconds"]! >= 0);
        Assert.Equal("canary-host", entry.State["ApplicationName"]);
        Assert.Equal("Production", entry.State["EnvironmentName"]);
        Assert.Equal(
            [
                "ApplicationName",
                "CanaryName",
                "CanaryStatus",
                "ElapsedMilliseconds",
                "EnvironmentName",
                "FreshSince",
                "MatchedCount",
                "ObservedAt",
                "Ready",
                "{OriginalFormat}",
            ],
            entry.State.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("private-marker", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sha256:", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Endpoint_BlankHostNamesNormalizeToUnknownInCompletionEvent()
    {
        using var loggerProvider = new RecordingLoggerProvider();
        await using var host = await StartHostAsync(
            loggerProvider: loggerProvider,
            applicationName: " ",
            environmentName: "\t");
        using var request = AuthorizedRequest(Route(Name));

        using var response = await host.Client.SendAsync(request);

        var entry = Assert.Single(loggerProvider.Entries, candidate => candidate.EventId.Id == 62401);
        Assert.Equal("unknown", entry.State["ApplicationName"]);
        Assert.Equal("unknown", entry.State["EnvironmentName"]);
    }

    [Fact]
    public async Task Endpoint_MissingHostEnvironmentNormalizesToUnknownAtMapping()
    {
        using var loggerProvider = new RecordingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(loggerProvider);
        ConfigureAuthorization(builder.Services);
        var state = new EvaluationState(AppSurfaceCanaryStatus.Pass);
        builder.Services.AddSingleton(state);
        builder.Services.AddAppSurfaceCanary<TestEvaluator>(Name);
        await using var app = builder.Build();
        var endpoints = new TestEndpointRouteBuilder(
            new FilteringServiceProvider(app.Services, typeof(IHostEnvironment)),
            ((IEndpointRouteBuilder)app).DataSources);
        endpoints.MapAppSurfaceCanaries(PolicyName);
        var endpoint = Assert.Single(GetRouteEndpoints(endpoints));
        await using var scope = app.Services.CreateAsyncScope();
        var context = CreateHttpContext(scope.ServiceProvider);
        context.SetEndpoint(endpoint);
        context.Request.RouteValues["name"] = Name;

        await endpoint.RequestDelegate!(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var entry = Assert.Single(loggerProvider.Entries, candidate => candidate.EventId.Id == 62401);
        Assert.Equal("unknown", entry.State["ApplicationName"]);
        Assert.Equal("unknown", entry.State["EnvironmentName"]);
    }

    [Fact]
    public async Task Endpoint_AlwaysOkMode_DoesNotMaskRequestLookupOrEvaluationFailures()
    {
        await using (var unknownNameHost = await StartHostAsync(alwaysOk: true))
        {
            using var request = AuthorizedRequest(Route("unknown"));
            using var response = await unknownNameHost.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        await using (var invalidRequestHost = await StartHostAsync(alwaysOk: true, requireHeaders: true))
        {
            using var request = AuthorizedRequest(Route(Name));
            using var response = await invalidRequestHost.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        await using (var evaluationFailureHost = await StartHostAsync(alwaysOk: true, throwOnEvaluate: true))
        {
            using var request = AuthorizedRequest(Route(Name));
            using var response = await evaluationFailureHost.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }
    }

    [Fact]
    public async Task Endpoint_SnapshotsResponseModeAfterMappingCallback()
    {
        AppSurfaceCanaryEndpointOptions? captured = null;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        ConfigureAuthorization(builder.Services);
        var state = new EvaluationState(AppSurfaceCanaryStatus.Pending);
        builder.Services.AddSingleton(state);
        builder.Services.AddAppSurfaceCanary<TestEvaluator>(Name);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAppSurfaceCanaries(
            PolicyName,
            options =>
            {
                captured = options;
                options.CompletedResponseMode = AppSurfaceCanaryCompletedResponseMode.StatusCode;
            });
        captured!.CompletedResponseMode = AppSurfaceCanaryCompletedResponseMode.AlwaysOk;
        await app.StartAsync();
        using var client = CreateClient(app);
        using var request = AuthorizedRequest(Route(Name));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public async Task Endpoint_UnknownNameReturnsSafe404WithoutInventoryOrInvocation()
    {
        using var loggerProvider = new RecordingLoggerProvider();
        await using var host = await StartHostAsync(loggerProvider: loggerProvider);
        using var request = AuthorizedRequest(Route("other"));

        using var response = await host.Client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("ASCAN203", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Name, json, StringComparison.Ordinal);
        Assert.Equal(0, host.State.InvocationCount);
        Assert.DoesNotContain(loggerProvider.Entries, entry => entry.EventId.Id == 62401);
        AssertNoStore(response);
    }

    [Fact]
    public async Task Endpoint_ProblemResponsesIgnoreHostProblemDetailsCustomization()
    {
        await using var host = await StartHostAsync(customizeProblemDetails: true);
        using var request = AuthorizedRequest(Route("other"));

        using var response = await host.Client.SendAsync(request);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            ["cause", "code", "docsLink", "fix", "problem", "status", "title"],
            json.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.False(json.RootElement.TryGetProperty("traceId", out _));
        Assert.DoesNotContain("host-secret-trace", json.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Endpoint_ValidatesRequiredHeadersAndPassesNormalizedInputs()
    {
        await using var host = await StartHostAsync(requireHeaders: true);

        using var missingRequest = AuthorizedRequest(Route(Name));
        using var missingResponse = await host.Client.SendAsync(missingRequest);
        Assert.Equal(HttpStatusCode.BadRequest, missingResponse.StatusCode);
        Assert.Contains("ASCAN201", await missingResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var invalidFreshnessRequest = AuthorizedRequest(Route(Name));
        invalidFreshnessRequest.Headers.Add(AppSurfaceCanaryHeaderNames.Marker, "deploy-42");
        invalidFreshnessRequest.Headers.Add(AppSurfaceCanaryHeaderNames.FreshSince, "not-a-timestamp");
        using var invalidFreshnessResponse = await host.Client.SendAsync(invalidFreshnessRequest);
        Assert.Equal(HttpStatusCode.BadRequest, invalidFreshnessResponse.StatusCode);
        Assert.Contains("ASCAN202", await invalidFreshnessResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var validRequest = AuthorizedRequest(Route(Name));
        validRequest.Headers.Add(AppSurfaceCanaryHeaderNames.Marker, " deploy-42 ");
        validRequest.Headers.Add(AppSurfaceCanaryHeaderNames.FreshSince, "2026-07-12T08:09:10.1234567-04:00");
        using var validResponse = await host.Client.SendAsync(validRequest);

        Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);
        Assert.Equal("deploy-42", host.State.Context?.Marker);
        Assert.Equal(DateTimeOffset.Parse("2026-07-12T12:09:10.1234567Z"), host.State.Context?.FreshSince);
        Assert.Equal(1, host.State.InvocationCount);
    }

    [Theory]
    [InlineData("2026-07-12T08:09:10Z")]
    [InlineData("2026-07-12T08:09:10.1Z")]
    [InlineData("2026-07-12T08:09:10.1234567+04:30")]
    [InlineData("2026-07-12T08:09:10-04:00")]
    public void FreshSinceParser_AcceptsStrictProfile(string value)
    {
        var context = HeaderContext(AppSurfaceCanaryHeaderNames.FreshSince, value);

        var accepted = AppSurfaceCanaryEndpointRouteBuilderExtensions.TryReadFreshSince(
            context,
            required: true,
            out var parsed,
            out var problem);

        Assert.True(accepted);
        Assert.NotNull(parsed);
        var parsedValue = parsed!.Value;
        Assert.Equal(TimeSpan.Zero, parsedValue.Offset);
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("2026-07-12T08:09:10")]
    [InlineData(" 2026-07-12T08:09:10Z")]
    [InlineData("2026-07-12T08:09:10Z ")]
    [InlineData("2026-07-12T08:09:10.12345678Z")]
    [InlineData("2026-02-30T08:09:10Z")]
    [InlineData("2026-07-12T08:09:10+24:00")]
    public async Task FreshSinceParser_RejectsInvalidProfileWithoutEchoingValue(string value)
    {
        var context = HeaderContext(AppSurfaceCanaryHeaderNames.FreshSince, value);

        var accepted = AppSurfaceCanaryEndpointRouteBuilderExtensions.TryReadFreshSince(
            context,
            required: false,
            out var parsed,
            out var problem);
        await problem!.ExecuteAsync(context);
        var body = await ReadBodyAsync(context);

        Assert.False(accepted);
        Assert.Null(parsed);
        Assert.Contains("ASCAN202", body, StringComparison.Ordinal);
        Assert.DoesNotContain(value, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeaderParsers_HandleOptionalBlankRequiredMultipleAndMarkerLimits()
    {
        var optional = CreateHttpContext();
        optional.Request.Headers[AppSurfaceCanaryHeaderNames.Marker] = " ";
        optional.Request.Headers[AppSurfaceCanaryHeaderNames.FreshSince] = " ";
        Assert.True(AppSurfaceCanaryEndpointRouteBuilderExtensions.TryReadMarker(optional, false, out var marker, out _));
        Assert.Null(marker);
        Assert.True(AppSurfaceCanaryEndpointRouteBuilderExtensions.TryReadFreshSince(optional, false, out var fresh, out _));
        Assert.Null(fresh);

        var opaque = HeaderContext(AppSurfaceCanaryHeaderNames.Marker, " marker ");
        Assert.True(AppSurfaceCanaryEndpointRouteBuilderExtensions.TryReadMarker(opaque, false, out marker, out _));
        Assert.Equal(" marker ", marker);

        var required = CreateHttpContext();
        Assert.False(AppSurfaceCanaryEndpointRouteBuilderExtensions.TryReadMarker(required, true, out _, out var requiredProblem));
        await requiredProblem!.ExecuteAsync(required);
        Assert.Contains("ASCAN201", await ReadBodyAsync(required), StringComparison.Ordinal);
        Assert.False(AppSurfaceCanaryEndpointRouteBuilderExtensions.TryReadFreshSince(required, true, out _, out var requiredFreshnessProblem));
        Assert.NotNull(requiredFreshnessProblem);

        var multiple = HeaderContext(AppSurfaceCanaryHeaderNames.Marker, ["one", "two"]);
        Assert.False(AppSurfaceCanaryEndpointRouteBuilderExtensions.TryReadMarker(multiple, false, out _, out _));

        var control = HeaderContext(AppSurfaceCanaryHeaderNames.Marker, "bad\u0001marker");
        Assert.False(AppSurfaceCanaryEndpointRouteBuilderExtensions.TryReadMarker(control, false, out _, out _));

        var oversized = HeaderContext(AppSurfaceCanaryHeaderNames.Marker, new string('é', 129));
        Assert.False(AppSurfaceCanaryEndpointRouteBuilderExtensions.TryReadMarker(oversized, false, out _, out _));

        var atLimit = HeaderContext(AppSurfaceCanaryHeaderNames.Marker, new string('é', 128));
        Assert.True(AppSurfaceCanaryEndpointRouteBuilderExtensions.TryReadMarker(
            atLimit,
            false,
            out var atLimitMarker,
            out var atLimitProblem));
        Assert.Equal(new string('é', 128), atLimitMarker);
        Assert.Equal(256, Encoding.UTF8.GetByteCount(atLimitMarker!));
        Assert.Null(atLimitProblem);

        foreach (var malformed in new[] { new string((char)0xD800, 1), new string((char)0xDC00, 1) })
        {
            var malformedContext = HeaderContext(AppSurfaceCanaryHeaderNames.Marker, malformed);
            Assert.False(AppSurfaceCanaryEndpointRouteBuilderExtensions.TryReadMarker(
                malformedContext,
                false,
                out var malformedMarker,
                out var malformedProblem));
            Assert.Equal(malformed, malformedMarker);
            Assert.NotNull(malformedProblem);
            await malformedProblem.ExecuteAsync(malformedContext);
            Assert.Contains("ASCAN202", await ReadBodyAsync(malformedContext), StringComparison.Ordinal);
        }

        var repeatedFreshness = HeaderContext(
            AppSurfaceCanaryHeaderNames.FreshSince,
            ["2026-07-12T08:09:10Z", "2026-07-12T08:09:11Z"]);
        Assert.False(AppSurfaceCanaryEndpointRouteBuilderExtensions.TryReadFreshSince(repeatedFreshness, false, out _, out _));
    }

    [Fact]
    public async Task Endpoint_RedactsEvaluatorFailuresAndMapsIndependentCancellationToASCAN301()
    {
        var loggerProvider = new RecordingLoggerProvider();
        await using var throwing = await StartHostAsync(
            throwOnEvaluate: true,
            loggerProvider: loggerProvider);
        using var throwRequest = AuthorizedRequest(Route(Name));
        throwRequest.Headers.Add(AppSurfaceCanaryHeaderNames.Marker, "marker-secret");
        throwRequest.Headers.Add(AppSurfaceCanaryHeaderNames.FreshSince, "2026-07-12T08:09:10Z");
        using var throwResponse = await throwing.Client.SendAsync(throwRequest);
        var throwBody = await throwResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, throwResponse.StatusCode);
        Assert.Contains("ASCAN301", throwBody, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-secret", throwBody, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", throwBody, StringComparison.Ordinal);

        var failureLog = Assert.Single(loggerProvider.Entries, entry => entry.EventId.Id == 62301);
        Assert.Null(failureLog.Exception);
        Assert.Equal(Name, failureLog.State["CanaryName"]);
        Assert.Equal("ASCAN301", failureLog.State["DiagnosticCode"]);
        Assert.Equal("System.InvalidOperationException", failureLog.State["ExceptionType"]);
        Assert.Equal(
            ["CanaryName", "DiagnosticCode", "ExceptionType"],
            failureLog.State.Keys
                .Where(key => key != "{OriginalFormat}")
                .Order(StringComparer.Ordinal));
        Assert.DoesNotContain("raw-secret", failureLog.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("marker-secret", failureLog.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-07-12", failureLog.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(loggerProvider.Entries, entry => entry.EventId.Id == 62401);

        using var canceledLoggerProvider = new RecordingLoggerProvider();
        await using var canceled = await StartHostAsync(
            cancelOnEvaluate: true,
            loggerProvider: canceledLoggerProvider);
        using var cancelRequest = AuthorizedRequest(Route(Name));
        using var cancelResponse = await canceled.Client.SendAsync(cancelRequest);
        Assert.Equal(HttpStatusCode.InternalServerError, cancelResponse.StatusCode);
        Assert.Contains("ASCAN301", await cancelResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(canceledLoggerProvider.Entries, entry => entry.EventId.Id == 62401);

        using var nullLoggerProvider = new RecordingLoggerProvider();
        await using var nullResult = await StartHostAsync(
            returnNull: true,
            loggerProvider: nullLoggerProvider);
        using var nullRequest = AuthorizedRequest(Route(Name));
        using var nullResponse = await nullResult.Client.SendAsync(nullRequest);
        Assert.Equal(HttpStatusCode.InternalServerError, nullResponse.StatusCode);
        Assert.Contains("ASCAN301", await nullResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(nullLoggerProvider.Entries, entry => entry.EventId.Id == 62401);
    }

    [Fact]
    public void EvaluationFailureFilter_DoesNotCatchFatalExceptions()
    {
        Assert.True(AppSurfaceCanaryEndpointRouteBuilderExtensions.IsNonFatalEvaluationFailure(new InvalidOperationException()));
        Assert.False(AppSurfaceCanaryEndpointRouteBuilderExtensions.IsNonFatalEvaluationFailure(new OutOfMemoryException()));
        Assert.False(AppSurfaceCanaryEndpointRouteBuilderExtensions.IsNonFatalEvaluationFailure(new StackOverflowException()));
        Assert.False(AppSurfaceCanaryEndpointRouteBuilderExtensions.IsNonFatalEvaluationFailure(new AccessViolationException()));
        Assert.False(AppSurfaceCanaryEndpointRouteBuilderExtensions.IsNonFatalEvaluationFailure(new AppDomainUnloadedException()));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AppSurfaceCanaryEndpointRouteBuilderExtensions.ToWireStatus((AppSurfaceCanaryStatus)99));
    }

    [Fact]
    public async Task Endpoint_PropagatesRequestAbortCancellation()
    {
        using var loggerProvider = new RecordingLoggerProvider();
        await using var host = await StartHostAsync(
            waitForCancellation: true,
            loggerProvider: loggerProvider);
        using var request = AuthorizedRequest(Route(Name));
        using var cancellation = new CancellationTokenSource();
        var sendTask = host.Client.SendAsync(request, cancellation.Token);

        await host.State.EvaluationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);

        Assert.Equal(1, host.State.InvocationCount);
        Assert.DoesNotContain(loggerProvider.Entries, entry => entry.EventId.Id is 62301 or 62401);
    }

    [Fact]
    public async Task Endpoint_DoesNotMisclassifyResponseWriteFailureAsEvaluationFailure()
    {
        using var loggerProvider = new RecordingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(loggerProvider);
        ConfigureAuthorization(builder.Services);
        var state = new EvaluationState(AppSurfaceCanaryStatus.Pass);
        builder.Services.AddSingleton(state);
        builder.Services.AddAppSurfaceCanary<TestEvaluator>(Name);
        await using var app = builder.Build();
        app.MapAppSurfaceCanaries(PolicyName);
        var endpoint = Assert.Single(GetRouteEndpoints(app));
        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        context.SetEndpoint(endpoint);
        context.Request.RouteValues["name"] = Name;
        context.Response.Body = new ThrowingWriteStream();

        await Assert.ThrowsAsync<IOException>(() => endpoint.RequestDelegate!(context));

        Assert.Equal(1, state.InvocationCount);
        Assert.Single(loggerProvider.Entries, entry => entry.EventId.Id == 62401);
        Assert.DoesNotContain(loggerProvider.Entries, entry => entry.EventId.Id == 62301);
    }

    [Fact]
    public async Task Endpoint_FailsClosedWithoutSelectedEndpointOrRouteName()
    {
        var builder = WebApplication.CreateBuilder();
        ConfigureAuthorization(builder.Services);
        var state = new EvaluationState(AppSurfaceCanaryStatus.Pass);
        builder.Services.AddSingleton(state);
        builder.Services.AddAppSurfaceCanary<TestEvaluator>(Name);
        await using var app = builder.Build();
        app.MapAppSurfaceCanaries(PolicyName);
        var endpoint = Assert.Single(GetRouteEndpoints(app));
        await using var scope = app.Services.CreateAsyncScope();

        var missingEndpoint = CreateHttpContext(scope.ServiceProvider);
        await endpoint.RequestDelegate!(missingEndpoint);
        Assert.Equal(StatusCodes.Status500InternalServerError, missingEndpoint.Response.StatusCode);
        Assert.Contains("ASCAN113", await ReadBodyAsync(missingEndpoint), StringComparison.Ordinal);

        var missingRouteName = CreateHttpContext(scope.ServiceProvider);
        missingRouteName.SetEndpoint(endpoint);
        await endpoint.RequestDelegate(missingRouteName);
        Assert.Equal(StatusCodes.Status404NotFound, missingRouteName.Response.StatusCode);
        Assert.Contains("ASCAN203", await ReadBodyAsync(missingRouteName), StringComparison.Ordinal);

        Assert.Equal(0, state.InvocationCount);
    }

    [Fact]
    public void PublicConsumer_AcceptsReorderedCoreAbsentOptionalsAndUnknownFields()
    {
        const string json = """
            {
              "futureEnvelopeField": { "nested": true },
              "status": "pending",
              "name": "migration.schema-v4",
              "ready": false
            }
            """;

        var parsed = CanaryEnvelopeConsumer.Parse(json);

        Assert.Equal("migration.schema-v4", parsed.Name);
        Assert.Equal("pending", parsed.Status);
        Assert.False(parsed.Ready);
        Assert.Null(parsed.ReasonCode);
        Assert.Equal(CanaryOperatorAction.Wait, parsed.Action);
    }

    [Theory]
    [InlineData("pass", true, "proof-observed", CanaryOperatorAction.Continue)]
    [InlineData("pending", false, "proof-not-observed", CanaryOperatorAction.Wait)]
    [InlineData("stale", false, "proof-stale", CanaryOperatorAction.Refresh)]
    [InlineData("not-configured", false, "proof-not-configured", CanaryOperatorAction.Configure)]
    [InlineData("fail", false, "ambiguous-matches", CanaryOperatorAction.Investigate)]
    [InlineData("fail", false, "checksum-mismatch", CanaryOperatorAction.RollBack)]
    public void PublicConsumer_MapsEveryCompletedStatusAndAmbiguityToOperatorAction(
        string status,
        bool ready,
        string reasonCode,
        CanaryOperatorAction expectedAction)
    {
        var json = JsonSerializer.Serialize(new
        {
            reasonCode,
            status,
            futureField = "ignored",
            ready,
            name = "consumer.proof",
        });

        Assert.Equal(expectedAction, CanaryEnvelopeConsumer.Parse(json).Action);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":\"proof\",\"ready\":false}")]
    [InlineData("{\"name\":\"proof\",\"status\":\"pending\"}")]
    [InlineData("{\"ready\":false,\"status\":\"pending\"}")]
    [InlineData("{\"name\":null,\"ready\":false,\"status\":\"pending\"}")]
    [InlineData("{\"name\":\"proof\",\"ready\":\"false\",\"status\":\"pending\"}")]
    [InlineData("{\"name\":\"proof\",\"ready\":false,\"status\":null}")]
    [InlineData("{\"name\":\"proof\",\"ready\":false,\"status\":\"future\"}")]
    [InlineData("{\"name\":\"proof\",\"ready\":true,\"status\":\"pending\"}")]
    public void PublicConsumer_RejectsMissingInvalidOrInconsistentRequiredCore(string json)
    {
        Assert.Throws<JsonException>(() => CanaryEnvelopeConsumer.Parse(json));
    }

    [Fact]
    public async Task PublicForwardingFixture_ProducesActionableAmbiguousEnvelopeThroughEndpoint()
    {
        await using var host = await StartConsumerHostAsync<ForwardingProofCanaryEvaluator>(
            "forwarding.consumer-proof",
            new CanaryFixtureScenario(AppSurfaceCanaryStatus.Fail, Ambiguous: true),
            ForwardingProofCanaryEvaluator.ProofKindDetailKey);
        using var request = AuthorizedRequest(Route("forwarding.consumer-proof"));

        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var parsed = CanaryEnvelopeConsumer.Parse(body);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("ambiguous-matches", parsed.ReasonCode);
        Assert.Equal(CanaryOperatorAction.Investigate, parsed.Action);
        Assert.DoesNotContain("email", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider payload", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublicMigrationFixture_ProducesContrastingRollbackEnvelopeThroughEndpoint()
    {
        await using var host = await StartConsumerHostAsync<MigrationCompletionCanaryEvaluator>(
            "migration.consumer-proof",
            new CanaryFixtureScenario(AppSurfaceCanaryStatus.Fail),
            MigrationCompletionCanaryEvaluator.MigrationKindDetailKey);
        using var request = AuthorizedRequest(Route("migration.consumer-proof"));

        using var response = await host.Client.SendAsync(request);
        var parsed = CanaryEnvelopeConsumer.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("checksum-mismatch", parsed.ReasonCode);
        Assert.Equal(CanaryOperatorAction.RollBack, parsed.Action);
    }

    [Theory]
    [InlineData(AppSurfaceCanaryStatus.Pass, HttpStatusCode.OK, CanaryOperatorAction.Continue)]
    [InlineData(AppSurfaceCanaryStatus.Pending, HttpStatusCode.ServiceUnavailable, CanaryOperatorAction.Wait)]
    [InlineData(AppSurfaceCanaryStatus.Fail, HttpStatusCode.ServiceUnavailable, CanaryOperatorAction.Investigate)]
    [InlineData(AppSurfaceCanaryStatus.Stale, HttpStatusCode.ServiceUnavailable, CanaryOperatorAction.Refresh)]
    [InlineData(AppSurfaceCanaryStatus.NotConfigured, HttpStatusCode.ServiceUnavailable, CanaryOperatorAction.Configure)]
    public async Task PublicForwardingFixture_MapsEveryStatusThroughEndpoint(
        AppSurfaceCanaryStatus status,
        HttpStatusCode expectedStatusCode,
        CanaryOperatorAction expectedAction)
    {
        await using var host = await StartConsumerHostAsync<ForwardingProofCanaryEvaluator>(
            "forwarding.status-matrix",
            new CanaryFixtureScenario(status),
            ForwardingProofCanaryEvaluator.ProofKindDetailKey);
        using var request = AuthorizedRequest(Route("forwarding.status-matrix"));

        using var response = await host.Client.SendAsync(request);
        var parsed = CanaryEnvelopeConsumer.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.Equal(expectedAction, parsed.Action);
    }

    [Theory]
    [InlineData(AppSurfaceCanaryStatus.Pass, HttpStatusCode.OK, CanaryOperatorAction.Continue)]
    [InlineData(AppSurfaceCanaryStatus.Pending, HttpStatusCode.ServiceUnavailable, CanaryOperatorAction.Wait)]
    [InlineData(AppSurfaceCanaryStatus.Fail, HttpStatusCode.ServiceUnavailable, CanaryOperatorAction.RollBack)]
    [InlineData(AppSurfaceCanaryStatus.Stale, HttpStatusCode.ServiceUnavailable, CanaryOperatorAction.Refresh)]
    [InlineData(AppSurfaceCanaryStatus.NotConfigured, HttpStatusCode.ServiceUnavailable, CanaryOperatorAction.Configure)]
    public async Task PublicMigrationFixture_MapsEveryStatusThroughEndpoint(
        AppSurfaceCanaryStatus status,
        HttpStatusCode expectedStatusCode,
        CanaryOperatorAction expectedAction)
    {
        await using var host = await StartConsumerHostAsync<MigrationCompletionCanaryEvaluator>(
            "migration.status-matrix",
            new CanaryFixtureScenario(status),
            MigrationCompletionCanaryEvaluator.MigrationKindDetailKey);
        using var request = AuthorizedRequest(Route("migration.status-matrix"));

        using var response = await host.Client.SendAsync(request);
        var parsed = CanaryEnvelopeConsumer.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.Equal(expectedAction, parsed.Action);
    }

    [Fact]
    public async Task PublicStatusOnlyFixture_ProducesCompatibilityEnvelopeThroughEndpoint()
    {
        await using var host = await StartConsumerHostAsync<StatusOnlyCanaryEvaluator>(
            "status-only.consumer-proof",
            new CanaryFixtureScenario(AppSurfaceCanaryStatus.Pass),
            "unused.detail");
        using var request = AuthorizedRequest(Route("status-only.consumer-proof"));

        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var parsed = CanaryEnvelopeConsumer.Parse(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(CanaryOperatorAction.Continue, parsed.Action);
        Assert.DoesNotContain("reasonCode", body, StringComparison.Ordinal);
        Assert.DoesNotContain("details", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/_appsurface/canaries")]
    [InlineData("/_appsurface/canaries/special")]
    [InlineData("/_APPSURFACE/CANARIES/{other}")]
    [InlineData("/_appsurface/canaries/{other:int}")]
    public async Task StartupValidator_RejectsReservedNamespaceConflicts(string conflictRoute)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        ConfigureAuthorization(builder.Services);
        builder.Services.AddSingleton(new EvaluationState(AppSurfaceCanaryStatus.Pass));
        builder.Services.AddAppSurfaceCanary<TestEvaluator>(Name);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAppSurfaceCanaries(PolicyName);
        app.MapGet(conflictRoute, () => "shadow");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

        Assert.StartsWith("ASCAN115", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupValidator_AllowsUnrelatedRoutes()
    {
        await using var host = await StartHostAsync(addUnrelatedRoute: true);
        using var request = AuthorizedRequest(Route(Name));

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void MapAppSurfaceCanaries_RejectsRouteGroupRelocation()
    {
        var builder = WebApplication.CreateBuilder();
        ConfigureAuthorization(builder.Services);
        builder.Services.AddSingleton(new EvaluationState(AppSurfaceCanaryStatus.Pass));
        builder.Services.AddAppSurfaceCanary<TestEvaluator>(Name);
        using var app = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            app.MapGroup("/ops").MapAppSurfaceCanaries(PolicyName));

        Assert.StartsWith("ASCAN115", exception.Message, StringComparison.Ordinal);
        Assert.Contains("application root", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(app.MapAppSurfaceCanaries(PolicyName));
    }

    [Fact]
    public void StartupValidator_RejectsRelocatedMarkedRouteDefensively()
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();
        app.MapGet("/ops/_appsurface/canaries/{name}", () => "relocated")
            .WithMetadata(AppSurfaceCanaryRouteMetadata.Instance);
        var mappingState = new AppSurfaceCanaryMappingState();
        Assert.True(mappingState.TryClaim(((IEndpointRouteBuilder)app).DataSources));
        var validator = new AppSurfaceCanaryStartupValidator(mappingState);

        var exception = Assert.Throws<InvalidOperationException>(validator.Validate);

        Assert.StartsWith("ASCAN115", exception.Message, StringComparison.Ordinal);
        Assert.Contains(AppSurfaceCanaryEndpointDefaults.RoutePattern, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData(" ", "/")]
    [InlineData("_appsurface/canaries/", "/_appsurface/canaries")]
    [InlineData("/", "/")]
    public void StartupValidator_NormalizesDefensiveRouteShapes(string? route, string expected)
    {
        Assert.Equal(expected, AppSurfaceCanaryStartupValidator.Normalize(route));
    }

    [Fact]
    public void StartupValidator_IsInactiveUntilCanaryRouteIsMapped()
    {
        var validator = new AppSurfaceCanaryStartupValidator(new AppSurfaceCanaryMappingState());
        var nextInvoked = false;
        var applicationBuilder = new ApplicationBuilder(
            new ServiceCollection().BuildServiceProvider());

        validator.Configure(_ => nextInvoked = true)(applicationBuilder);

        Assert.True(nextInvoked);
        Assert.Throws<ArgumentNullException>(() => validator.Configure(null!));
    }

    [Fact]
    public async Task StartupValidator_RejectsConflictMappedBeforeCanaryRoute()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        ConfigureAuthorization(builder.Services);
        builder.Services.AddSingleton(new EvaluationState(AppSurfaceCanaryStatus.Pass));
        builder.Services.AddAppSurfaceCanary<TestEvaluator>(Name);
        await using var app = builder.Build();
        app.MapGet("/_appsurface/canaries/before", () => "shadow");
        app.MapAppSurfaceCanaries(PolicyName);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

        Assert.StartsWith("ASCAN115", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupValidator_RejectsControllerRouteInReservedNamespace()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        ConfigureAuthorization(builder.Services);
        builder.Services.AddControllers().AddApplicationPart(typeof(CanaryConflictController).Assembly);
        builder.Services.AddSingleton(new EvaluationState(AppSurfaceCanaryStatus.Pass));
        builder.Services.AddAppSurfaceCanary<TestEvaluator>(Name);
        await using var app = builder.Build();
        app.MapAppSurfaceCanaries(PolicyName);
        app.MapControllers();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

        Assert.StartsWith("ASCAN115", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupValidator_RejectsConflictMappedDuringGenericHostPipelineConstruction()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseKestrel();
                webBuilder.UseUrls("http://127.0.0.1:0");
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    ConfigureAuthorization(services);
                    services.AddSingleton(new EvaluationState(AppSurfaceCanaryStatus.Pass));
                    services.AddAppSurfaceCanary<TestEvaluator>(Name);
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapAppSurfaceCanaries(PolicyName);
                        endpoints.MapGet("/_appsurface/canaries/conflict", () => "shadow");
                    });
                });
            })
            .Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.StartsWith("ASCAN115", exception.Message, StringComparison.Ordinal);
    }

    private static WebApplication BuildUnstartedApp(bool addCanary, bool addAuthorization)
    {
        var builder = WebApplication.CreateBuilder();
        if (addAuthorization)
        {
            ConfigureAuthorization(builder.Services);
        }

        if (addCanary)
        {
            builder.Services.AddSingleton(new EvaluationState(AppSurfaceCanaryStatus.Pass));
            builder.Services.AddAppSurfaceCanary<TestEvaluator>(Name);
        }

        return builder.Build();
    }

    private static IReadOnlyList<RouteEndpoint> GetRouteEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToList();

    private static async Task<StartedHost> StartHostAsync(
        bool mapEndpoint = true,
        bool registerPolicy = true,
        bool allowAnonymous = false,
        bool removeAuthorizationMetadata = false,
        bool replaceAuthorizationMetadata = false,
        bool alwaysOk = false,
        bool requireHeaders = false,
        bool throwOnEvaluate = false,
        bool cancelOnEvaluate = false,
        bool returnNull = false,
        bool waitForCancellation = false,
        bool addUnrelatedRoute = false,
        bool customizeProblemDetails = false,
        RecordingLoggerProvider? loggerProvider = null,
        Func<AppSurfaceCanaryResult>? resultFactory = null,
        string[]? allowedDetailKeys = null,
        string? applicationName = null,
        string? environmentName = null,
        AppSurfaceCanaryStatus status = AppSurfaceCanaryStatus.Pass)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        if (applicationName is not null || environmentName is not null)
        {
            builder.Services.AddSingleton<IHostEnvironment>(
                new TestHostEnvironment(
                    builder.Environment,
                    applicationName ?? builder.Environment.ApplicationName,
                    environmentName ?? builder.Environment.EnvironmentName));
        }

        ConfigureAuthorization(builder.Services, registerPolicy);
        if (customizeProblemDetails)
        {
            builder.Services.AddProblemDetails(options =>
                options.CustomizeProblemDetails = context =>
                    context.ProblemDetails.Extensions["traceId"] = "host-secret-trace");
        }

        if (loggerProvider is not null)
        {
            builder.Logging.AddProvider(loggerProvider);
        }

        var state = new EvaluationState(status)
        {
            ThrowOnEvaluate = throwOnEvaluate,
            CancelOnEvaluate = cancelOnEvaluate,
            ReturnNull = returnNull,
            WaitForCancellation = waitForCancellation,
            ResultFactory = resultFactory,
        };
        builder.Services.AddSingleton(state);
        builder.Services.AddAppSurfaceCanary<TestEvaluator>(
            Name,
            options =>
            {
                if (requireHeaders)
                {
                    options.RequireMarker();
                    options.RequireFreshSince();
                }

                foreach (var key in allowedDetailKeys ?? [])
                {
                    options.AllowedDetailKeys.Add(key);
                }
            });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        if (mapEndpoint)
        {
            var endpoint = alwaysOk
                ? app.MapAppSurfaceCanaries(
                    PolicyName,
                    options => options.CompletedResponseMode = AppSurfaceCanaryCompletedResponseMode.AlwaysOk)
                : app.MapAppSurfaceCanaries(PolicyName);
            if (allowAnonymous)
            {
                endpoint.AllowAnonymous();
            }

            if (removeAuthorizationMetadata || replaceAuthorizationMetadata)
            {
                endpoint.Add(endpointBuilder =>
                {
                    for (var index = endpointBuilder.Metadata.Count - 1; index >= 0; index--)
                    {
                        if (endpointBuilder.Metadata[index] is IAuthorizeData)
                        {
                            endpointBuilder.Metadata.RemoveAt(index);
                        }
                    }

                    if (replaceAuthorizationMetadata)
                    {
                        endpointBuilder.Metadata.Add(new AuthorizeAttribute(WrongPolicyName));
                    }
                });
            }
        }

        if (addUnrelatedRoute)
        {
            app.MapGet("/_appsurface/other", () => "ok");
            app.MapGet("/_appsurface/canaries-extra", () => "ok");
        }

        await app.StartAsync();
        return new StartedHost(app, CreateClient(app), state);
    }

    private static async Task<StartedConsumerHost> StartConsumerHostAsync<TEvaluator>(
        string name,
        CanaryFixtureScenario scenario,
        string allowedDetailKey)
        where TEvaluator : class, IAppSurfaceCanaryEvaluator
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        ConfigureAuthorization(builder.Services);
        builder.Services.AddSingleton(scenario);
        builder.Services.AddAppSurfaceCanary<TEvaluator>(
            name,
            options => options.AllowedDetailKeys.Add(allowedDetailKey));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAppSurfaceCanaries(PolicyName);
        await app.StartAsync();
        return new StartedConsumerHost(app, CreateClient(app));
    }

    private static async Task<StartedHost> StartHostWithoutAuthorizationMiddlewareAsync()
    {
        var state = new EvaluationState(AppSurfaceCanaryStatus.Pass);
        var host = Host.CreateDefaultBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseKestrel();
                webBuilder.UseUrls("http://127.0.0.1:0");
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    ConfigureAuthorization(services);
                    services.AddSingleton(state);
                    services.AddAppSurfaceCanary<TestEvaluator>(Name);
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseEndpoints(endpoints => endpoints.MapAppSurfaceCanaries(PolicyName));
                });
            })
            .Build();

        await host.StartAsync();
        return new StartedHost(host, CreateClient(host), state);
    }

    private static void ConfigureAuthorization(IServiceCollection services, bool registerPolicy = true)
    {
        services.AddLogging();
        services.AddAuthentication(HeaderAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                HeaderAuthenticationHandler.SchemeName,
                _ => { });
        services.AddAuthorization(options =>
        {
            options.AddPolicy(
                WrongPolicyName,
                policy => policy.RequireClaim("role", "deploy-operator"));
            if (registerPolicy)
            {
                options.AddPolicy(
                    PolicyName,
                    policy => policy.RequireClaim("role", "deploy-operator"));
            }
        });
    }

    private static string Route(string name) => $"/_appsurface/canaries/{name}";

    private static HttpRequestMessage AuthorizedRequest(string route, string user = "operator")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Add(HeaderAuthenticationHandler.UserHeaderName, user);
        return request;
    }

    private static DefaultHttpContext HeaderContext(string name, string value) => HeaderContext(name, [value]);

    private static DefaultHttpContext HeaderContext(string name, string[] values)
    {
        var context = CreateHttpContext();
        context.Request.Headers[name] = values;
        return context;
    }

    private static DefaultHttpContext CreateHttpContext() =>
        CreateHttpContext(new ServiceCollection().AddLogging().BuildServiceProvider());

    private static DefaultHttpContext CreateHttpContext(IServiceProvider requestServices)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = requestServices,
        };
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static HttpClient CreateClient(IHost host) => new()
    {
        BaseAddress = new Uri(GetBaseAddress(host)),
    };

    private static string GetBaseAddress(IHost host) => Assert.Single(
        host.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses ?? []);

    private static void AssertNoStore(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("no-cache", response.Headers.Pragma.Select(value => value.Name));
    }

    private sealed class StartedHost(IHost host, HttpClient client, EvaluationState state) : IAsyncDisposable
    {
        internal HttpClient Client => client;

        internal EvaluationState State => state;

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await host.StopAsync();
            host.Dispose();
        }
    }

    private sealed class StartedConsumerHost(IHost host, HttpClient client) : IAsyncDisposable
    {
        internal HttpClient Client => client;

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await host.StopAsync();
            host.Dispose();
        }
    }

    private sealed class FilteringServiceProvider(
        IServiceProvider inner,
        params Type[] hiddenServices) : IServiceProvider
    {
        private readonly HashSet<Type> _hiddenServices = hiddenServices.ToHashSet();

        public object? GetService(Type serviceType) =>
            _hiddenServices.Contains(serviceType) ? null : inner.GetService(serviceType);
    }

    private sealed class TestHostEnvironment(
        IHostEnvironment inner,
        string applicationName,
        string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = applicationName;

        public string ContentRootPath
        {
            get => inner.ContentRootPath;
            set => inner.ContentRootPath = value;
        }

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider
        {
            get => inner.ContentRootFileProvider;
            set => inner.ContentRootFileProvider = value;
        }
    }

    private sealed class TestEndpointRouteBuilder(
        IServiceProvider serviceProvider,
        ICollection<EndpointDataSource> dataSources) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;

        public ICollection<EndpointDataSource> DataSources { get; } = dataSources;

        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<RecordedLog> _entries = new();

        internal IReadOnlyCollection<RecordedLog> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(_entries);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger(ConcurrentQueue<RecordedLog> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var structuredState = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            entries.Enqueue(new RecordedLog(logLevel, eventId, formatter(state, exception), exception, structuredState));
        }
    }

    private sealed record RecordedLog(
        LogLevel Level,
        EventId EventId,
        string Message,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> State);

    private sealed class ThrowingWriteStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("response write failed");

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            Task.FromException(new IOException("response write failed"));

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("response write failed"));
    }

    private sealed class EvaluationState(AppSurfaceCanaryStatus status)
    {
        internal int InvocationCount { get; set; }

        internal TaskCompletionSource<bool> EvaluationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal AppSurfaceCanaryEvaluationContext? Context { get; set; }

        internal AppSurfaceCanaryStatus Status { get; } = status;

        internal bool ThrowOnEvaluate { get; init; }

        internal bool CancelOnEvaluate { get; init; }

        internal bool WaitForCancellation { get; init; }

        internal bool ReturnNull { get; init; }

        internal Func<AppSurfaceCanaryResult>? ResultFactory { get; init; }
    }

    private sealed class TestEvaluator(EvaluationState state) : IAppSurfaceCanaryEvaluator
    {
        public async ValueTask<AppSurfaceCanaryResult> EvaluateAsync(
            AppSurfaceCanaryEvaluationContext context,
            CancellationToken cancellationToken)
        {
            state.InvocationCount++;
            state.EvaluationStarted.TrySetResult(true);
            state.Context = context;
            if (state.ThrowOnEvaluate)
            {
                throw new InvalidOperationException("raw-secret evaluator failure");
            }

            if (state.CancelOnEvaluate)
            {
                throw new OperationCanceledException("independent evaluator cancellation");
            }

            if (state.WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return state.ReturnNull
                ? null!
                : state.ResultFactory?.Invoke() ?? new AppSurfaceCanaryResult(state.Status);
        }
    }

    private sealed class HeaderAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        internal const string SchemeName = "CanaryHeaderTest";
        internal const string UserHeaderName = "X-Test-User";

        public HeaderAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeaderName, out var values)
                || string.IsNullOrWhiteSpace(values[0]))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, values[0]!) };
            if (string.Equals(values[0], "operator", StringComparison.Ordinal))
            {
                claims.Add(new Claim("role", "deploy-operator"));
            }

            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(
                AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }

    private sealed class DynamicPolicyProvider : IAuthorizationPolicyProvider
    {
        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) =>
            Task.FromResult<AuthorizationPolicy?>(
                string.Equals(policyName, PolicyName, StringComparison.Ordinal)
                    ? new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()
                    : null);

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() =>
            Task.FromResult(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() =>
            Task.FromResult<AuthorizationPolicy?>(null);
    }

    private sealed class ScopedAuthorizationHandler : IAuthorizationHandler
    {
        public Task HandleAsync(AuthorizationHandlerContext context) => Task.CompletedTask;
    }
}

[ApiController]
[Route("/_appsurface/canaries/controller-conflict")]
public sealed class CanaryConflictController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok();
}
