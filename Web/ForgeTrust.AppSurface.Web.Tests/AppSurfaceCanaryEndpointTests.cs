using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
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
        Assert.Equal($"{{\"name\":\"{Name}\",\"status\":\"{expectedWireStatus}\"}}", body);
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
            $"{{\"name\":\"{Name}\",\"status\":\"{expectedWireStatus}\"}}",
            await response.Content.ReadAsStringAsync());
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
        await using var host = await StartHostAsync();
        using var request = AuthorizedRequest(Route("other"));

        using var response = await host.Client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("ASCAN203", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Name, json, StringComparison.Ordinal);
        Assert.Equal(0, host.State.InvocationCount);
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

        await using var canceled = await StartHostAsync(cancelOnEvaluate: true);
        using var cancelRequest = AuthorizedRequest(Route(Name));
        using var cancelResponse = await canceled.Client.SendAsync(cancelRequest);
        Assert.Equal(HttpStatusCode.InternalServerError, cancelResponse.StatusCode);
        Assert.Contains("ASCAN301", await cancelResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await using var nullResult = await StartHostAsync(returnNull: true);
        using var nullRequest = AuthorizedRequest(Route(Name));
        using var nullResponse = await nullResult.Client.SendAsync(nullRequest);
        Assert.Equal(HttpStatusCode.InternalServerError, nullResponse.StatusCode);
        Assert.Contains("ASCAN301", await nullResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluationFailureFilter_DoesNotCatchFatalExceptions()
    {
        Assert.True(AppSurfaceCanaryEndpointRouteBuilderExtensions.IsNonFatalEvaluationFailure(new InvalidOperationException()));
        Assert.False(AppSurfaceCanaryEndpointRouteBuilderExtensions.IsNonFatalEvaluationFailure(new OutOfMemoryException()));
        Assert.False(AppSurfaceCanaryEndpointRouteBuilderExtensions.IsNonFatalEvaluationFailure(new StackOverflowException()));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AppSurfaceCanaryEndpointRouteBuilderExtensions.ToWireStatus((AppSurfaceCanaryStatus)99));
    }

    [Fact]
    public async Task Endpoint_PropagatesRequestAbortCancellation()
    {
        await using var host = await StartHostAsync(waitForCancellation: true);
        using var request = AuthorizedRequest(Route(Name));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.Client.SendAsync(request, cancellation.Token));

        Assert.Equal(1, host.State.InvocationCount);
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
    public async Task StartupValidator_IsInactiveUntilCanaryRouteIsMapped()
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
        AppSurfaceCanaryStatus status = AppSurfaceCanaryStatus.Pass)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
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
            });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        if (mapEndpoint)
        {
            var endpoint = app.MapAppSurfaceCanaries(
                PolicyName,
                options => options.CompletedResponseMode = alwaysOk
                    ? AppSurfaceCanaryCompletedResponseMode.AlwaysOk
                    : AppSurfaceCanaryCompletedResponseMode.StatusCode);
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

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
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
            entries.Enqueue(new RecordedLog(eventId, formatter(state, exception), exception, structuredState));
        }
    }

    private sealed record RecordedLog(
        EventId EventId,
        string Message,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> State);

    private sealed class EvaluationState(AppSurfaceCanaryStatus status)
    {
        internal int InvocationCount { get; set; }

        internal AppSurfaceCanaryEvaluationContext? Context { get; set; }

        internal AppSurfaceCanaryStatus Status { get; } = status;

        internal bool ThrowOnEvaluate { get; init; }

        internal bool CancelOnEvaluate { get; init; }

        internal bool WaitForCancellation { get; init; }

        internal bool ReturnNull { get; init; }
    }

    private sealed class TestEvaluator(EvaluationState state) : IAppSurfaceCanaryEvaluator
    {
        public async ValueTask<AppSurfaceCanaryResult> EvaluateAsync(
            AppSurfaceCanaryEvaluationContext context,
            CancellationToken cancellationToken)
        {
            state.InvocationCount++;
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

            return state.ReturnNull ? null! : new AppSurfaceCanaryResult(state.Status);
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
}

[ApiController]
[Route("/_appsurface/canaries/controller-conflict")]
public sealed class CanaryConflictController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok();
}
