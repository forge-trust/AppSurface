using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Examples.DurableExternalActivation.Tests;

/// <summary>Verifies the sample's externally observable HTTP, auth, body, probe, and JSON contracts.</summary>
public sealed class ActivationHttpContractTests
{
    [Fact]
    public async Task Route_surface_is_exactly_the_four_approved_endpoints()
    {
        await using var host = await ActivationTestHost.StartAsync();

        var routes = ((IEndpointRouteBuilder)host.App).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods
                .Select(method => $"{method} {endpoint.RoutePattern.RawText}") ?? Enumerable.Empty<string>())
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[]
        {
            "GET /compatibility",
            "GET /live",
            "GET /ready",
            "POST /private/durable/activate",
        }, routes);
    }

    [Fact]
    public async Task Live_is_fixed_and_does_not_resolve_health_or_activation_dependencies()
    {
        await using var host = await ActivationTestHost.StartAsync(new ActivationHostOptions
        {
            RegisterHealth = false,
            RegisterAdmission = false,
        });

        using var response = await host.Client.GetAsync("/live");
        using var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Live", json.RootElement.GetProperty("status").GetString());
        Assert.Single(json.RootElement.EnumerateObject());
        Assert.Equal(0, host.Dependencies.Health.CallCount);
        Assert.Equal(0, host.Dependencies.Activation.CallCount);
    }

    [Theory]
    [InlineData("/compatibility", DurableRuntimeHealthState.NotStarted, DurableProblemCodes.ActivatorStale, false, HttpStatusCode.OK)]
    [InlineData("/ready", DurableRuntimeHealthState.NotStarted, DurableProblemCodes.ActivatorStale, false, HttpStatusCode.ServiceUnavailable)]
    [InlineData("/compatibility", DurableRuntimeHealthState.Healthy, null, false, HttpStatusCode.OK)]
    [InlineData("/ready", DurableRuntimeHealthState.Healthy, null, false, HttpStatusCode.OK)]
    [InlineData("/compatibility", DurableRuntimeHealthState.Draining, null, true, HttpStatusCode.OK)]
    [InlineData("/ready", DurableRuntimeHealthState.Draining, null, true, HttpStatusCode.ServiceUnavailable)]
    [InlineData("/compatibility", DurableRuntimeHealthState.Stale, DurableProblemCodes.WorkerIdentityConflict, false, HttpStatusCode.OK)]
    [InlineData("/ready", DurableRuntimeHealthState.Stale, DurableProblemCodes.ActivatorStale, false, HttpStatusCode.ServiceUnavailable)]
    [InlineData("/compatibility", DurableRuntimeHealthState.Incompatible, DurableProblemCodes.SchemaUpgradeRequired, false, HttpStatusCode.ServiceUnavailable)]
    [InlineData("/ready", DurableRuntimeHealthState.Incompatible, DurableProblemCodes.RecoveryEpochRequired, false, HttpStatusCode.ServiceUnavailable)]
    [InlineData("/compatibility", DurableRuntimeHealthState.Unavailable, DurableProblemCodes.StoreUnavailable, false, HttpStatusCode.ServiceUnavailable)]
    [InlineData("/ready", DurableRuntimeHealthState.Unavailable, DurableProblemCodes.StoreUnavailable, false, HttpStatusCode.ServiceUnavailable)]
    public async Task Probe_projects_valid_state_and_uses_compatibility_or_readiness_status(
        string path,
        DurableRuntimeHealthState state,
        string? code,
        bool draining,
        HttpStatusCode expectedStatus)
    {
        var dependencies = new ActivationTestDependencies();
        dependencies.Health.Reader = _ => ValueTask.FromResult(ActivationTestData.Snapshot(
            state,
            code,
            schemaCompatible: state is not DurableRuntimeHealthState.Unavailable and not DurableRuntimeHealthState.Incompatible,
            epochCompatible: state != DurableRuntimeHealthState.Unavailable,
            isDraining: draining));
        await using var host = await ActivationTestHost.StartAsync(dependencies: dependencies);

        using var response = await host.Client.GetAsync(path);
        using var json = await ReadJsonAsync(response);
        var root = json.RootElement;

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("Assessment", root.GetProperty("outcome").GetString());
        Assert.Equal(state.ToString(), root.GetProperty("observedHealthState").GetString());
        Assert.Equal(state is not DurableRuntimeHealthState.Unavailable and not DurableRuntimeHealthState.Incompatible,
            root.GetProperty("canEnableActivation").GetBoolean());
        Assert.Equal(state == DurableRuntimeHealthState.Healthy, root.GetProperty("isReady").GetBoolean());
        Assert.Equal(code, GetNullableString(root.GetProperty("problemCode")));
        AssertExactProperties(root, "outcome", "observedHealthState", "canEnableActivation", "isReady", "problemCode");
        Assert.Equal(1, dependencies.Health.CallCount);
        Assert.Equal(0, dependencies.Activation.CallCount);
        Assert.Equal(0, dependencies.Admission.CallCount);
    }

    [Theory]
    [InlineData(DurableRuntimeHealthState.NotStarted, DurableProblemCodes.ActivatorStale, false)]
    [InlineData(DurableRuntimeHealthState.NotStarted, null, false)]
    [InlineData(DurableRuntimeHealthState.Healthy, null, false)]
    [InlineData(DurableRuntimeHealthState.Draining, null, true)]
    [InlineData(DurableRuntimeHealthState.Stale, DurableProblemCodes.ActivatorStale, false)]
    [InlineData(DurableRuntimeHealthState.Stale, DurableProblemCodes.WorkerIdentityConflict, true)]
    [InlineData(DurableRuntimeHealthState.Stale, DurableProblemCodes.WorkerIdentityConflict, false)]
    [InlineData(DurableRuntimeHealthState.Unavailable, DurableProblemCodes.StoreUnavailable, false)]
    [InlineData(DurableRuntimeHealthState.Incompatible, DurableProblemCodes.SchemaMissing, false)]
    public async Task Probe_preserves_allowed_health_observation(
        DurableRuntimeHealthState state,
        string? code,
        bool draining)
    {
        var dependencies = new ActivationTestDependencies();
        dependencies.Health.Reader = _ => ValueTask.FromResult(ActivationTestData.Snapshot(
            state,
            code,
            schemaCompatible: state is not DurableRuntimeHealthState.Unavailable and not DurableRuntimeHealthState.Incompatible,
            epochCompatible: state != DurableRuntimeHealthState.Unavailable,
            isDraining: draining));
        await using var host = await ActivationTestHost.StartAsync(dependencies: dependencies);

        using var response = await host.Client.GetAsync("/compatibility");
        using var json = await ReadJsonAsync(response);

        Assert.Equal(state == DurableRuntimeHealthState.Unavailable || state == DurableRuntimeHealthState.Incompatible
            ? HttpStatusCode.ServiceUnavailable
            : HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(state.ToString(), json.RootElement.GetProperty("observedHealthState").GetString());
        Assert.Equal(code, GetNullableString(json.RootElement.GetProperty("problemCode")));
        Assert.Equal(0, dependencies.Activation.CallCount);
        Assert.Equal(0, dependencies.Admission.CallCount);
    }

    [Theory]
    [InlineData(DurableRuntimeHealthState.NotStarted, DurableProblemCodes.ActivatorStale, false, "Completed", HttpStatusCode.OK, null, 1)]
    [InlineData(DurableRuntimeHealthState.NotStarted, null, false, "Completed", HttpStatusCode.OK, null, 1)]
    [InlineData(DurableRuntimeHealthState.Healthy, null, false, "Completed", HttpStatusCode.OK, null, 1)]
    [InlineData(DurableRuntimeHealthState.Draining, null, true, "Draining", HttpStatusCode.ServiceUnavailable, null, 0)]
    [InlineData(DurableRuntimeHealthState.Stale, DurableProblemCodes.ActivatorStale, false, "Completed", HttpStatusCode.OK, DurableProblemCodes.ActivatorStale, 1)]
    [InlineData(DurableRuntimeHealthState.Stale, DurableProblemCodes.WorkerIdentityConflict, true, "Completed", HttpStatusCode.OK, DurableProblemCodes.WorkerIdentityConflict, 1)]
    [InlineData(DurableRuntimeHealthState.Incompatible, DurableProblemCodes.SchemaUpgradeRequired, false, "Incompatible", HttpStatusCode.ServiceUnavailable, DurableProblemCodes.SchemaUpgradeRequired, 0)]
    [InlineData(DurableRuntimeHealthState.Unavailable, DurableProblemCodes.StoreUnavailable, false, "Unavailable", HttpStatusCode.ServiceUnavailable, DurableProblemCodes.StoreUnavailable, 0)]
    public async Task Production_service_classifies_health_and_controls_one_authoritative_admission(
        DurableRuntimeHealthState state,
        string? healthCode,
        bool draining,
        string expectedOutcome,
        HttpStatusCode expectedStatus,
        string? expectedResultCode,
        int expectedAdmissionCalls)
    {
        var dependencies = new ActivationTestDependencies();
        dependencies.Health.Reader = _ => ValueTask.FromResult(ActivationTestData.Snapshot(
            state,
            healthCode,
            schemaCompatible: state is not DurableRuntimeHealthState.Unavailable and not DurableRuntimeHealthState.Incompatible,
            epochCompatible: state != DurableRuntimeHealthState.Unavailable,
            isDraining: draining));
        await using var host = await ActivationTestHost.StartAsync(
            new ActivationHostOptions { UseProductionActivationService = true },
            dependencies);
        using var request = AuthorizedRequest(new ByteArrayContent([]));

        using var response = await host.Client.SendAsync(request);
        using var json = await ReadJsonAsync(response);
        var root = json.RootElement;

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedOutcome, root.GetProperty("outcome").GetString());
        Assert.Equal(state.ToString(), root.GetProperty("observedHealthState").GetString());
        Assert.Equal(expectedResultCode, GetNullableString(root.GetProperty("problemCode")));
        Assert.Equal(1, dependencies.Health.CallCount);
        Assert.Equal(expectedAdmissionCalls, dependencies.Admission.CallCount);
        Assert.Equal(0, dependencies.Activation.CallCount);
        if (expectedOutcome == "Completed")
        {
            var call = Assert.Single(dependencies.Admission.Calls);
            Assert.Equal(32, call.Request.MaximumItems);
            Assert.Equal(TimeSpan.FromSeconds(2), call.Request.TimeBudget);
            Assert.Equal(DurableRuntimeSurface.Work, call.Request.Surfaces);
            Assert.Equal(JsonValueKind.Object, root.GetProperty("pumpResult").ValueKind);
        }
        else
        {
            Assert.Empty(dependencies.Admission.Calls);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("pumpResult").ValueKind);
        }
    }

    [Fact]
    public async Task Activation_uses_configured_bounds_independently_and_fixed_work_surface()
    {
        var dependencies = new ActivationTestDependencies();
        await using var host = await ActivationTestHost.StartAsync(
            new ActivationHostOptions
            {
                ConfigurationValues = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["DurableActivation:PumpMaximumItems"] = "17",
                    ["DurableActivation:PumpDiscoveryBudgetSeconds"] = "3",
                    ["DurableActivation:RequestBudgetSeconds"] = "9",
                },
            },
            dependencies);
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ActivationTestHost.ValidToken);

        using var response = await host.Client.PostAsync("/private/durable/activate", new ByteArrayContent([]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(dependencies.Activation.Calls);

        Assert.Equal(17, call.Request.PumpRequest.MaximumItems);
        Assert.Equal(TimeSpan.FromSeconds(3), call.Request.PumpRequest.TimeBudget);
        Assert.Equal(DurableRuntimeSurface.Work, call.Request.PumpRequest.Surfaces);
        Assert.Equal(TimeSpan.FromSeconds(9), call.Request.RequestBudget);
    }

    [Theory]
    [InlineData("/compatibility")]
    [InlineData("/ready")]
    public async Task Probe_dependency_failure_is_a_safe_probe_failed_response(string path)
    {
        var dependencies = new ActivationTestDependencies();
        dependencies.Health.Reader = _ => ValueTask.FromException<DurableRuntimeHealthSnapshot>(new InvalidOperationException("secret health detail"));
        await using var host = await ActivationTestHost.StartAsync(dependencies: dependencies);

        using var response = await host.Client.GetAsync(path);
        using var json = await ReadJsonAsync(response);
        var root = json.RootElement;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("ProbeFailed", root.GetProperty("outcome").GetString());
        Assert.Null(root.GetProperty("observedHealthState").GetString());
        Assert.Null(root.GetProperty("canEnableActivation").GetBooleanOrNull());
        Assert.Null(root.GetProperty("isReady").GetBooleanOrNull());
        Assert.Equal(DurableProblemCodes.ExternalActivationFailed, root.GetProperty("problemCode").GetString());
        Assert.DoesNotContain("secret", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, dependencies.Activation.CallCount);
        Assert.Equal(0, dependencies.Admission.CallCount);
    }

    /// <summary>Proves malformed or unrelated-canceled health reads use the exact safe failure envelope and preserve liveness.</summary>
    /// <param name="path">Compatibility or readiness probe route.</param>
    /// <param name="failure">Null snapshot, unknown diagnostic, or unrelated cancellation exception.</param>
    [Theory]
    [InlineData("/compatibility", "null")]
    [InlineData("/ready", "null")]
    [InlineData("/compatibility", "unknown-code")]
    [InlineData("/ready", "unknown-code")]
    [InlineData("/compatibility", "unrelated-cancellation")]
    [InlineData("/ready", "unrelated-cancellation")]
    public async Task Probe_invalid_observation_or_unrelated_cancellation_fails_closed_and_live_stays_fixed(string path, string failure)
    {
        var dependencies = new ActivationTestDependencies();
        using var unrelatedCancellation = new CancellationTokenSource();
        unrelatedCancellation.Cancel();
        dependencies.Health.Reader = _ => failure switch
        {
            "null" => ValueTask.FromResult<DurableRuntimeHealthSnapshot>(null!),
            "unknown-code" => ValueTask.FromResult(ActivationTestData.Snapshot(DurableRuntimeHealthState.Stale, "ASDUR999")),
            "unrelated-cancellation" => ValueTask.FromException<DurableRuntimeHealthSnapshot>(
                new OperationCanceledException("private unrelated health cancellation", unrelatedCancellation.Token)),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        await using var host = await ActivationTestHost.StartAsync(dependencies: dependencies);
        await AssertLiveInvariantAsync(host);

        using var response = await host.Client.GetAsync(path);
        using var json = await ReadJsonAsync(response);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            "{\"outcome\":\"ProbeFailed\",\"observedHealthState\":null,\"canEnableActivation\":null,\"isReady\":null,\"problemCode\":\"ASDUR407\"}",
            await response.Content.ReadAsStringAsync());
        AssertExactProperties(json.RootElement, "outcome", "observedHealthState", "canEnableActivation", "isReady", "problemCode");
        var observedToken = Assert.Single(dependencies.Health.CancellationTokens);
        Assert.False(observedToken.IsCancellationRequested);
        Assert.Equal(1, dependencies.Health.CallCount);
        Assert.Equal(0, dependencies.Activation.CallCount);
        Assert.Equal(0, dependencies.Admission.CallCount);
        await AssertLiveInvariantAsync(host);
        Assert.Equal(1, dependencies.Health.CallCount);
    }

    /// <summary>Uses the intentional handler seam to verify fatal failures propagate without promising an HTTP envelope.</summary>
    /// <param name="readiness">Selects the readiness handler branch.</param>
    /// <param name="failure">Name of one excluded fatal exception type.</param>
    [Theory]
    [InlineData(false, "out-of-memory")]
    [InlineData(true, "out-of-memory")]
    [InlineData(false, "access-violation")]
    [InlineData(true, "access-violation")]
    [InlineData(false, "stack-overflow")]
    [InlineData(true, "stack-overflow")]
    public async Task Probe_fatal_failure_propagates_from_internal_handler_and_live_is_independent(bool readiness, string failure)
    {
        Exception fatal = failure switch
        {
            "out-of-memory" => new OutOfMemoryException("test-owned fatal health failure"),
            "access-violation" => new AccessViolationException("test-owned fatal health failure"),
            "stack-overflow" => new StackOverflowException("test-owned fatal health failure"),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        var dependencies = new ActivationTestDependencies();
        dependencies.Health.Reader = _ => ValueTask.FromException<DurableRuntimeHealthSnapshot>(fatal);
        await using var host = await ActivationTestHost.StartAsync(dependencies: dependencies);
        await AssertLiveInvariantAsync(host);
        var context = new DefaultHttpContext { RequestServices = host.App.Services };
        using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var propagated = await Record.ExceptionAsync(async () =>
            await ForgeTrust.AppSurface.Examples.DurableExternalActivation.ActivationHttpEndpoints.ProbeAsync(context, readiness));

        Assert.Same(fatal, propagated);
        Assert.Equal(0, responseBody.Length);
        Assert.Null(context.Response.ContentType);
        Assert.Equal(1, dependencies.Health.CallCount);
        Assert.Equal(0, dependencies.Activation.CallCount);
        Assert.Equal(0, dependencies.Admission.CallCount);
        await AssertLiveInvariantAsync(host);
        Assert.Equal(1, dependencies.Health.CallCount);
    }

    [Theory]
    [InlineData("/compatibility")]
    [InlineData("/ready")]
    public async Task Probe_request_cancellation_is_distinct_from_failure(string path)
    {
        var dependencies = new ActivationTestDependencies();
        dependencies.Health.Reader = cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ActivationTestData.Snapshot(DurableRuntimeHealthState.Healthy));
        };
        await using var host = await ActivationTestHost.StartAsync(dependencies: dependencies);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = new DefaultHttpContext();
        context.RequestAborted = cancellation.Token;
        context.RequestServices = host.App.Services;

        var result = await ForgeTrust.AppSurface.Examples.DurableExternalActivation.ActivationHttpEndpoints.ProbeAsync(
            context,
            readiness: path == "/ready");
        var (status, body) = await MaterializeResultAsync(result, host.App.Services);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        Assert.Equal(StatusCodes.Status408RequestTimeout, status);
        Assert.Equal("ProbeCanceled", root.GetProperty("outcome").GetString());
        Assert.Null(root.GetProperty("observedHealthState").GetString());
        Assert.Null(root.GetProperty("canEnableActivation").GetBooleanOrNull());
        Assert.Null(root.GetProperty("isReady").GetBooleanOrNull());
        Assert.Null(root.GetProperty("problemCode").GetString());
        Assert.Equal(cancellation.Token, Assert.Single(dependencies.Health.CancellationTokens));
        Assert.Equal(0, dependencies.Activation.CallCount);
        Assert.Equal(0, dependencies.Admission.CallCount);
    }

    [Fact]
    public async Task Probe_rejects_contradictory_snapshot_as_probe_failed()
    {
        var dependencies = new ActivationTestDependencies();
        dependencies.Health.Reader = _ => ValueTask.FromResult(ActivationTestData.Snapshot(
            DurableRuntimeHealthState.Draining,
            isDraining: false));
        await using var host = await ActivationTestHost.StartAsync(dependencies: dependencies);

        using var response = await host.Client.GetAsync("/compatibility");
        using var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("ProbeFailed", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(DurableProblemCodes.ExternalActivationFailed, json.RootElement.GetProperty("problemCode").GetString());
        Assert.Equal(0, dependencies.Activation.CallCount);
        Assert.Equal(0, dependencies.Admission.CallCount);
    }

    [Theory]
    [InlineData(DurableRuntimeHealthState.NotStarted, true)]
    [InlineData(DurableRuntimeHealthState.Healthy, true)]
    [InlineData(DurableRuntimeHealthState.Draining, false)]
    public async Task Probe_rejects_inconsistent_draining_flag_for_enabled_states(
        DurableRuntimeHealthState state,
        bool isDraining)
    {
        var dependencies = new ActivationTestDependencies();
        dependencies.Health.Reader = _ => ValueTask.FromResult(ActivationTestData.Snapshot(state, isDraining: isDraining));
        await using var host = await ActivationTestHost.StartAsync(dependencies: dependencies);

        using var response = await host.Client.GetAsync("/compatibility");
        using var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("ProbeFailed", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(DurableProblemCodes.ExternalActivationFailed, json.RootElement.GetProperty("problemCode").GetString());
        Assert.Equal(0, dependencies.Activation.CallCount);
        Assert.Equal(0, dependencies.Admission.CallCount);
    }

    [Theory]
    [InlineData(null, false, HttpStatusCode.Unauthorized)]
    [InlineData("not-a-token", false, HttpStatusCode.Unauthorized)]
    [InlineData("external-activation-test-token", false, HttpStatusCode.OK)]
    [InlineData(null, true, HttpStatusCode.Unauthorized)]
    [InlineData("ordinary-user", true, HttpStatusCode.Forbidden)]
    [InlineData("durable-activation", true, HttpStatusCode.OK)]
    public async Task Activation_authentication_and_authorization_precede_body_or_service_handling(
        string? credential,
        bool externalPermissionScheme,
        HttpStatusCode expectedStatus)
    {
        var options = externalPermissionScheme
            ? new ActivationHostOptions
            {
                EnvironmentName = "Production",
                ConfigureExternalAuthentication = PermissionTestAuthentication.Configure,
                ConfigureExternalAuthorization = PermissionTestAuthentication.ConfigurePolicy,
            }
            : new ActivationHostOptions();
        await using var host = await ActivationTestHost.StartAsync(options);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/private/durable/activate")
        {
            Content = expectedStatus == HttpStatusCode.OK ? new ByteArrayContent([]) : new OneByteContent(),
        };
        if (externalPermissionScheme)
        {
            if (credential is not null)
            {
                request.Headers.Add("X-Test-Permission", credential);
            }
        }
        else if (credential is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        }

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(expectedStatus, response.StatusCode);
        var bodyRead = Assert.Single(host.Dependencies.BodyReads.Observations);
        Assert.Equal(0, bodyRead.BytesRead);
        Assert.False(host.Dependencies.BodyReads.ReadStarted.IsCompleted);
        Assert.Equal(0, host.Dependencies.Health.CallCount);
        Assert.Equal(expectedStatus == HttpStatusCode.OK ? 1 : 0, host.Dependencies.Activation.CallCount);
        Assert.Equal(0, host.Dependencies.Admission.CallCount);
        if (!externalPermissionScheme && expectedStatus == HttpStatusCode.Unauthorized)
        {
            Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        }
    }

    [Fact]
    public async Task Missing_or_whitespace_development_token_fails_before_startup()
    {
        foreach (var token in new string?[] { null, "", " \t\r\n " })
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await ActivationTestHost.StartAsync(new ActivationHostOptions { DevelopmentToken = token }));

            Assert.Contains("DurableActivation:DevelopmentBearerToken", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(ActivationTestHost.ValidToken, exception.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("DurableActivation:PumpMaximumItems", "10001")]
    [InlineData("DurableActivation:PumpDiscoveryBudgetSeconds", "0")]
    [InlineData("DurableActivation:PumpDiscoveryBudgetSeconds", "301")]
    [InlineData("DurableActivation:RequestBudgetSeconds", "0")]
    [InlineData("DurableActivation:RequestBudgetSeconds", "4294968")]
    public async Task Invalid_activation_limits_fail_before_the_host_starts(string key, string value)
    {
        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await ActivationTestHost.StartAsync(new ActivationHostOptions
            {
                ConfigurationValues = new Dictionary<string, string?>(StringComparer.Ordinal) { [key] = value },
            }));

        Assert.DoesNotContain(ActivationTestHost.ValidToken, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Production_service_dependency_graph_must_be_complete_before_listening(bool registerHealth, bool registerAdmission)
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await ActivationTestHost.StartAsync(
                new ActivationHostOptions
                {
                    UseProductionActivationService = true,
                    RegisterHealth = registerHealth,
                    RegisterAdmission = registerAdmission,
                }));

        Assert.Contains(
            registerHealth ? nameof(IDurableRuntimePumpAdmission) : nameof(IDurableRuntimeHealth),
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nondevelopment_startup_requires_explicit_external_authentication_and_authorization()
    {
        var builder = ActivationTestHost.CreateBuilder(new ActivationHostOptions { EnvironmentName = "Production" });
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ForgeTrust.AppSurface.Examples.DurableExternalActivation.DurableExternalActivationProgram.BuildApplication(builder));

        Assert.Contains("authentication-scheme and authorization-policy", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Proves supplying only one external authentication/policy callback still fails before a listener can start.</summary>
    /// <param name="includeAuthentication">Whether the scheme callback is supplied.</param>
    /// <param name="includeAuthorization">Whether the policy callback is supplied.</param>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Nondevelopment_missing_one_authentication_callback_fails_before_listener_start(
        bool includeAuthentication,
        bool includeAuthorization)
    {
        var startup = new ListenerStartupProbe();
        var dependencies = new ActivationTestDependencies();
        var options = new ActivationHostOptions
        {
            EnvironmentName = Environments.Production,
            UseKestrel = true,
            ConfigureExternalAuthentication = includeAuthentication ? PermissionTestAuthentication.Configure : null,
            ConfigureExternalAuthorization = includeAuthorization ? PermissionTestAuthentication.ConfigurePolicy : null,
            ConfigureServices = services => services.AddSingleton<IHostedService>(startup),
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ActivationTestHost.StartAsync(options, dependencies));

        Assert.Contains("authentication-scheme and authorization-policy", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, startup.StartCount);
        Assert.Equal(0, dependencies.Health.CallCount);
        Assert.Equal(0, dependencies.Activation.CallCount);
        Assert.Equal(0, dependencies.Admission.CallCount);
    }

    /// <summary>Proves absent or wrongly named external policies fail eager composition before Kestrel starts.</summary>
    /// <param name="registerWrongPolicy">Adds a policy under an unrelated name instead of leaving policies absent.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nondevelopment_missing_named_activation_policy_fails_before_listener_start(bool registerWrongPolicy)
    {
        var startup = new ListenerStartupProbe();
        var dependencies = new ActivationTestDependencies();
        var options = new ActivationHostOptions
        {
            EnvironmentName = Environments.Production,
            UseKestrel = true,
            ConfigureExternalAuthentication = PermissionTestAuthentication.Configure,
            ConfigureExternalAuthorization = authorization =>
            {
                if (registerWrongPolicy)
                {
                    authorization.AddPolicy("UnrelatedPermission", policy =>
                        policy.RequireAuthenticatedUser().RequireClaim("permission", "durable-activation"));
                }
            },
            ConfigureServices = services => services.AddSingleton<IHostedService>(startup),
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ActivationTestHost.StartAsync(options, dependencies));

        Assert.Equal("The DurableActivation authorization policy must exist before listening.", exception.Message);
        Assert.Equal(0, startup.StartCount);
        Assert.Equal(0, dependencies.Health.CallCount);
        Assert.Equal(0, dependencies.Activation.CallCount);
        Assert.Equal(0, dependencies.Admission.CallCount);
    }

    /// <summary>Shows a named host policy actually governs permission and cannot be bypassed by an otherwise authenticated wake.</summary>
    [Fact]
    public async Task Nondevelopment_wrong_permission_policy_forbids_before_body_read_or_durable_calls()
    {
        await using var host = await ActivationTestHost.StartAsync(new ActivationHostOptions
        {
            EnvironmentName = Environments.Production,
            UseKestrel = true,
            ConfigureExternalAuthentication = PermissionTestAuthentication.Configure,
            ConfigureExternalAuthorization = authorization => authorization.AddPolicy(
                ForgeTrust.AppSurface.Examples.DurableExternalActivation.ActivationHttpEndpoints.AuthorizationPolicy,
                policy => policy.RequireAuthenticatedUser().RequireClaim("permission", "unrelated-permission")),
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/private/durable/activate") { Content = new OneByteContent() };
        request.Headers.Add("X-Test-Permission", "durable-activation");

        using var response = await host.Client.SendAsync(request);
        await host.Dependencies.BodyReads.ObservationRecorded.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.False(host.Dependencies.BodyReads.ReadStarted.IsCompleted);
        Assert.Empty(host.Dependencies.BodyReads.RequestedReadSizes);
        Assert.Equal(0, Assert.Single(host.Dependencies.BodyReads.Observations).BytesRead);
        Assert.Equal(0, host.Dependencies.Health.CallCount);
        Assert.Equal(0, host.Dependencies.Activation.CallCount);
        Assert.Equal(0, host.Dependencies.Admission.CallCount);
        await AssertLiveInvariantAsync(host);
    }

    /// <summary>Requires authentication even when external composition supplies a permissive named host policy.</summary>
    [Fact]
    public async Task Nondevelopment_permissive_named_policy_cannot_allow_anonymous_wake()
    {
        await using var host = await ActivationTestHost.StartAsync(new ActivationHostOptions
        {
            EnvironmentName = Environments.Production,
            UseKestrel = true,
            ConfigureExternalAuthentication = PermissionTestAuthentication.Configure,
            ConfigureExternalAuthorization = authorization => authorization.AddPolicy(
                ForgeTrust.AppSurface.Examples.DurableExternalActivation.ActivationHttpEndpoints.AuthorizationPolicy,
                policy => policy.RequireAssertion(_ => true)),
        });
        using var anonymousRequest = new HttpRequestMessage(HttpMethod.Post, "/private/durable/activate")
        {
            Content = new ByteArrayContent([]),
        };

        using var anonymousResponse = await host.Client.SendAsync(anonymousRequest);
        await host.Dependencies.BodyReads.ObservationRecorded.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.Empty(await anonymousResponse.Content.ReadAsByteArrayAsync());
        Assert.False(host.Dependencies.BodyReads.ReadStarted.IsCompleted);
        Assert.Empty(host.Dependencies.BodyReads.RequestedReadSizes);
        Assert.Equal(0, Assert.Single(host.Dependencies.BodyReads.Observations).BytesRead);
        Assert.Equal(0, host.Dependencies.Health.CallCount);
        Assert.Equal(0, host.Dependencies.Activation.CallCount);
        Assert.Equal(0, host.Dependencies.Admission.CallCount);

        using var authenticatedRequest = new HttpRequestMessage(HttpMethod.Post, "/private/durable/activate")
        {
            Content = new ByteArrayContent([]),
        };
        authenticatedRequest.Headers.Add("X-Test-Permission", "ordinary-user");
        using var authenticatedResponse = await host.Client.SendAsync(authenticatedRequest);
        using var json = await ReadJsonAsync(authenticatedResponse);

        Assert.Equal(HttpStatusCode.OK, authenticatedResponse.StatusCode);
        Assert.Equal("Completed", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(1, host.Dependencies.Activation.CallCount);
        Assert.Equal(0, host.Dependencies.Health.CallCount);
        Assert.Equal(0, host.Dependencies.Admission.CallCount);
        Assert.False(host.Dependencies.BodyReads.ReadStarted.IsCompleted);
    }

    [Fact]
    public async Task Known_nonempty_body_is_rejected_without_reading_body_or_resolving_durable_dependencies()
    {
        await using var host = await AuthorizedHostAsync();
        using var request = AuthorizedRequest(new ByteArrayContent(Encoding.UTF8.GetBytes("{")));

        using var response = await host.Client.SendAsync(request);
        using var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("WakeBodyMustBeEmpty", json.RootElement.GetProperty("error").GetString());
        Assert.False(host.Dependencies.BodyReads.ReadStarted.IsCompleted);
        Assert.Equal(0, Assert.Single(host.Dependencies.BodyReads.Observations).BytesRead);
        Assert.Equal(0, host.Dependencies.Health.CallCount);
        Assert.Equal(0, host.Dependencies.Activation.CallCount);
        Assert.Equal(0, host.Dependencies.Admission.CallCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Unknown_length_empty_body_is_allowed_but_any_streamed_byte_is_rejected(bool includeOneByte, bool useKestrel)
    {
        await using var host = await ActivationTestHost.StartAsync(new ActivationHostOptions { UseKestrel = useKestrel });
        using var request = AuthorizedRequest(new ChunkedTestContent(includeOneByte ? "x"u8.ToArray() : []));

        using var response = await host.Client.SendAsync(request);
        using var json = await ReadJsonAsync(response);
        var observation = Assert.Single(host.Dependencies.BodyReads.Observations);

        Assert.Equal(includeOneByte ? HttpStatusCode.BadRequest : HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(includeOneByte ? "WakeBodyMustBeEmpty" : "Completed", json.RootElement.GetProperty(includeOneByte ? "error" : "outcome").GetString());
        Assert.Equal(includeOneByte ? 1 : 0, observation.BytesRead);
        Assert.True(host.Dependencies.BodyReads.ReadStarted.IsCompleted);
        Assert.Equal(includeOneByte ? 0 : 1, host.Dependencies.Activation.CallCount);
        Assert.Equal(0, host.Dependencies.Health.CallCount);
        Assert.Equal(0, host.Dependencies.Admission.CallCount);
    }

    [Fact]
    public async Task Known_large_declared_body_is_rejected_before_reading_any_byte()
    {
        await using var host = await AuthorizedHostAsync();
        using var request = AuthorizedRequest(new ChunkedTestContent(Encoding.UTF8.GetBytes(new string('x', 64 * 1024)), knownLength: true));

        using var response = await host.Client.SendAsync(request);
        using var json = await ReadJsonAsync(response);
        var observation = Assert.Single(host.Dependencies.BodyReads.Observations);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("WakeBodyMustBeEmpty", json.RootElement.GetProperty("error").GetString());
        Assert.False(host.Dependencies.BodyReads.ReadStarted.IsCompleted);
        Assert.Equal(0, observation.BytesRead);
        Assert.Equal(0, host.Dependencies.Health.CallCount);
        Assert.Equal(0, host.Dependencies.Activation.CallCount);
        Assert.Equal(0, host.Dependencies.Admission.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_length_large_body_is_rejected_after_exactly_one_byte(bool useKestrel)
    {
        await using var host = await ActivationTestHost.StartAsync(new ActivationHostOptions { UseKestrel = useKestrel });
        using var request = AuthorizedRequest(new ChunkedTestContent(Encoding.UTF8.GetBytes(new string('x', 64 * 1024))));

        using var response = await host.Client.SendAsync(request);
        using var json = await ReadJsonAsync(response);
        var observation = Assert.Single(host.Dependencies.BodyReads.Observations);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("WakeBodyMustBeEmpty", json.RootElement.GetProperty("error").GetString());
        Assert.Equal(1, observation.BytesRead);
        Assert.Equal(0, host.Dependencies.Health.CallCount);
        Assert.Equal(0, host.Dependencies.Activation.CallCount);
        Assert.Equal(0, host.Dependencies.Admission.CallCount);
    }

    /// <summary>Holds a real chunked request open until Kestrel enters its bounded read, then releases one rejecting byte.</summary>
    [Fact]
    public async Task Slow_chunked_loopback_request_waits_for_one_byte_without_durable_calls()
    {
        await using var host = await ActivationTestHost.StartAsync(new ActivationHostOptions { UseKestrel = true });
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var connection = await LoopbackActivationConnection.OpenAsync(host.Client.BaseAddress!, watchdog.Token);
        var responseTask = connection.ReadResponseAsync(watchdog.Token);
        try
        {
            await host.Dependencies.BodyReads.ReadPending.WaitAsync(watchdog.Token);
            Assert.False(responseTask.IsCompleted);
            Assert.Empty(host.Dependencies.BodyReads.Observations);
            Assert.Equal(1, Assert.Single(host.Dependencies.BodyReads.RequestedReadSizes));
            Assert.Equal(0, host.Dependencies.Health.CallCount);
            Assert.Equal(0, host.Dependencies.Activation.CallCount);
            Assert.Equal(0, host.Dependencies.Admission.CallCount);

            await connection.SendAsync("1\r\nx\r\n0\r\n\r\n", watchdog.Token);
            var response = await responseTask;
            await host.Dependencies.BodyReads.ObservationRecorded.WaitAsync(watchdog.Token);

            Assert.StartsWith("HTTP/1.1 400 Bad Request\r\n", response);
            Assert.Contains("Content-Type: application/json", response, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("{\"error\":\"WakeBodyMustBeEmpty\"}", response, StringComparison.Ordinal);
            Assert.Equal(1, Assert.Single(host.Dependencies.BodyReads.Observations).BytesRead);
            Assert.Equal(1, Assert.Single(host.Dependencies.BodyReads.RequestedReadSizes));
            Assert.Equal(0, host.Dependencies.Health.CallCount);
            Assert.Equal(0, host.Dependencies.Activation.CallCount);
            Assert.Equal(0, host.Dependencies.Admission.CallCount);
            await AssertLiveInvariantAsync(host);
        }
        finally
        {
            connection.Abort();
            await Record.ExceptionAsync(async () => await responseTask);
        }
    }

    /// <summary>Cancels a real connection while Kestrel awaits its first body byte and observes server-side transport cancellation.</summary>
    [Fact]
    public async Task Slow_chunked_loopback_disconnect_aborts_bounded_read_without_durable_calls()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var dependencies = new ActivationTestDependencies();
        dependencies.BodyReads.AwaitRequestAbortedOnReadFailure = true;
        dependencies.BodyReads.ReadFailureObservationCancellation = watchdog.Token;
        await using var host = await ActivationTestHost.StartAsync(
            new ActivationHostOptions { UseKestrel = true }, dependencies);
        await using var connection = await LoopbackActivationConnection.OpenAsync(host.Client.BaseAddress!, watchdog.Token);

        await host.Dependencies.BodyReads.ReadPending.WaitAsync(watchdog.Token);
        Assert.Empty(host.Dependencies.BodyReads.Observations);
        Assert.Equal(1, Assert.Single(host.Dependencies.BodyReads.RequestedReadSizes));
        Assert.Equal(0, host.Dependencies.Health.CallCount);
        Assert.Equal(0, host.Dependencies.Activation.CallCount);
        Assert.Equal(0, host.Dependencies.Admission.CallCount);

        connection.Abort();
        var readCancellationToken = await host.Dependencies.BodyReads.ReadFailed.WaitAsync(watchdog.Token);
        await host.Dependencies.BodyReads.RequestAborted.WaitAsync(watchdog.Token);
        await host.Dependencies.BodyReads.ObservationRecorded.WaitAsync(watchdog.Token);

        Assert.True(readCancellationToken.CanBeCanceled);
        Assert.True(readCancellationToken.IsCancellationRequested);
        Assert.NotEqual(watchdog.Token, readCancellationToken);
        Assert.Equal(0, Assert.Single(host.Dependencies.BodyReads.Observations).BytesRead);
        Assert.Equal(1, Assert.Single(host.Dependencies.BodyReads.RequestedReadSizes));
        Assert.Equal(0, host.Dependencies.Health.CallCount);
        Assert.Equal(0, host.Dependencies.Activation.CallCount);
        Assert.Equal(0, host.Dependencies.Admission.CallCount);
        await AssertLiveInvariantAsync(host);
    }

    [Fact]
    public async Task Unknown_length_body_cancellation_returns_safe_host_failure_without_durable_calls()
    {
        await using var host = await ActivationTestHost.StartAsync();
        using var cancellation = new CancellationTokenSource();
        var body = new CancellationAwareReadStream();
        var context = new DefaultHttpContext();
        context.RequestServices = host.App.Services;
        context.RequestAborted = cancellation.Token;
        context.Request.ContentLength = null;
        context.Request.Body = body;

        var invocation = ForgeTrust.AppSurface.Examples.DurableExternalActivation.ActivationHttpEndpoints.ActivateAsync(
            context,
            new ForgeTrust.AppSurface.Examples.DurableExternalActivation.ActivationHostSettings(
                32,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(10)));
        await body.ReadStarted;
        cancellation.Cancel();
        var result = await invocation;
        var (status, responseBody) = await MaterializeResultAsync(result, host.App.Services);
        using var json = JsonDocument.Parse(responseBody);

        Assert.Equal(StatusCodes.Status500InternalServerError, status);
        Assert.Equal("HostFailure", json.RootElement.GetProperty("error").GetString());
        Assert.Equal(cancellation.Token, body.CancellationToken);
        Assert.Equal(0, host.Dependencies.Health.CallCount);
        Assert.Equal(0, host.Dependencies.Activation.CallCount);
        Assert.Equal(0, host.Dependencies.Admission.CallCount);
    }

    [Fact]
    public async Task Unknown_length_body_read_failure_returns_safe_host_failure_without_durable_calls()
    {
        await using var host = await AuthorizedHostAsync();
        using var request = AuthorizedRequest(new ChunkedTestContent("x"u8.ToArray()));
        request.Headers.Add("X-Test-Body-Read-Failure", "1");

        using var response = await host.Client.SendAsync(request);
        using var json = await ReadJsonAsync(response);
        var observation = Assert.Single(host.Dependencies.BodyReads.Observations);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("HostFailure", json.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("Test request-body", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(observation.Failed);
        Assert.True(host.Dependencies.BodyReads.ReadStarted.IsCompleted);
        Assert.Equal(0, host.Dependencies.Health.CallCount);
        Assert.Equal(0, host.Dependencies.Activation.CallCount);
        Assert.Equal(0, host.Dependencies.Admission.CallCount);
    }

    [Fact]
    public async Task Successful_empty_wake_calls_service_once_and_preserves_aggregate_dto()
    {
        var dependencies = new ActivationTestDependencies();
        var nextDue = new DateTimeOffset(2026, 10, 1, 2, 3, 4, TimeSpan.FromHours(-4));
        dependencies.Activation.Activator = (_, _) => ValueTask.FromResult(ActivationTestData.Result(
            DurableExternalActivationOutcomeKind.Completed,
            DurableRuntimeHealthState.NotStarted,
            pumpResult: ActivationTestData.PumpResult(4, 3, 2, 1, 1, true, nextDue, 123456789)));
        await using var host = await ActivationHostAsync(dependencies);
        using var request = AuthorizedRequest(new ByteArrayContent([]));

        using var response = await host.Client.SendAsync(request);
        using var json = await ReadJsonAsync(response);
        var root = json.RootElement;
        var aggregate = root.GetProperty("pumpResult");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Completed", root.GetProperty("outcome").GetString());
        Assert.Equal("NotStarted", root.GetProperty("observedHealthState").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("problemCode").ValueKind);
        Assert.Equal(4, aggregate.GetProperty("discovered").GetInt32());
        Assert.Equal(3, aggregate.GetProperty("claimed").GetInt32());
        Assert.Equal(2, aggregate.GetProperty("processed").GetInt32());
        Assert.Equal(1, aggregate.GetProperty("deferred").GetInt32());
        Assert.Equal(1, aggregate.GetProperty("failed").GetInt32());
        Assert.True(aggregate.GetProperty("hasMore").GetBoolean());
        Assert.Equal("2026-10-01T06:03:04.0000000Z", aggregate.GetProperty("nextDueAtUtc").GetString());
        Assert.Equal(123456789, aggregate.GetProperty("elapsedTicks").GetInt64());
        AssertExactProperties(root, "outcome", "observedHealthState", "problemCode", "pumpResult");
        AssertExactProperties(aggregate, "discovered", "claimed", "processed", "deferred", "failed", "hasMore", "nextDueAtUtc", "elapsedTicks");
        Assert.Equal(0, dependencies.Health.CallCount);
        Assert.Equal(1, dependencies.Activation.CallCount);
        Assert.Equal(0, dependencies.Admission.CallCount);
        Assert.Equal(DurableRuntimeSurface.Work, Assert.Single(dependencies.Activation.Calls).Request.PumpRequest.Surfaces);
    }

    [Fact]
    public async Task Completed_response_keeps_null_next_due_and_zero_aggregate_fields()
    {
        var dependencies = new ActivationTestDependencies();
        dependencies.Activation.Activator = (_, _) => ValueTask.FromResult(ActivationTestData.Result(
            DurableExternalActivationOutcomeKind.Completed,
            DurableRuntimeHealthState.Healthy,
            pumpResult: ActivationTestData.PumpResult()));
        await using var host = await ActivationHostAsync(dependencies);
        using var request = AuthorizedRequest(new ByteArrayContent([]));

        using var response = await host.Client.SendAsync(request);
        using var json = await ReadJsonAsync(response);
        var pump = json.RootElement.GetProperty("pumpResult");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, pump.GetProperty("nextDueAtUtc").ValueKind);
        Assert.Equal(0, pump.GetProperty("discovered").GetInt32());
        Assert.Equal(0, pump.GetProperty("claimed").GetInt32());
        Assert.Equal(0, pump.GetProperty("processed").GetInt32());
        Assert.Equal(0, pump.GetProperty("deferred").GetInt32());
        Assert.Equal(0, pump.GetProperty("failed").GetInt32());
        Assert.False(pump.GetProperty("hasMore").GetBoolean());
        Assert.Equal(0, pump.GetProperty("elapsedTicks").GetInt64());
        AssertExactProperties(pump, "discovered", "claimed", "processed", "deferred", "failed", "hasMore", "nextDueAtUtc", "elapsedTicks");
        Assert.Equal(0, dependencies.Health.CallCount);
        Assert.Equal(1, dependencies.Activation.CallCount);
        Assert.Equal(0, dependencies.Admission.CallCount);
    }

    [Theory]
    [InlineData(DurableExternalActivationOutcomeKind.Unavailable, DurableRuntimeHealthState.Unavailable, DurableProblemCodes.StoreUnavailable, HttpStatusCode.ServiceUnavailable)]
    [InlineData(DurableExternalActivationOutcomeKind.Incompatible, DurableRuntimeHealthState.Incompatible, DurableProblemCodes.SchemaUpgradeRequired, HttpStatusCode.ServiceUnavailable)]
    [InlineData(DurableExternalActivationOutcomeKind.Draining, DurableRuntimeHealthState.Draining, null, HttpStatusCode.ServiceUnavailable)]
    [InlineData(DurableExternalActivationOutcomeKind.Busy, DurableRuntimeHealthState.Stale, DurableProblemCodes.ActivatorStale, HttpStatusCode.Conflict)]
    [InlineData(DurableExternalActivationOutcomeKind.CanceledBeforeAdmission, DurableRuntimeHealthState.NotStarted, null, HttpStatusCode.RequestTimeout)]
    [InlineData(DurableExternalActivationOutcomeKind.CanceledBeforeAdmission, DurableRuntimeHealthState.Stale, DurableProblemCodes.WorkerIdentityConflict, HttpStatusCode.RequestTimeout)]
    [InlineData(DurableExternalActivationOutcomeKind.RequestBudgetExceeded, null, null, HttpStatusCode.GatewayTimeout)]
    [InlineData(DurableExternalActivationOutcomeKind.ActivationFailed, null, DurableProblemCodes.ExternalActivationFailed, HttpStatusCode.InternalServerError)]
    [InlineData(DurableExternalActivationOutcomeKind.PumpCanceled, DurableRuntimeHealthState.Healthy, null, HttpStatusCode.RequestTimeout)]
    [InlineData(DurableExternalActivationOutcomeKind.PumpCanceled, DurableRuntimeHealthState.Stale, DurableProblemCodes.ActivatorStale, HttpStatusCode.RequestTimeout)]
    [InlineData(DurableExternalActivationOutcomeKind.PumpFailed, DurableRuntimeHealthState.Healthy, DurableProblemCodes.StoreUnavailable, HttpStatusCode.ServiceUnavailable)]
    [InlineData(DurableExternalActivationOutcomeKind.PumpFailed, DurableRuntimeHealthState.Healthy, DurableProblemCodes.RecoveryEpochRequired, HttpStatusCode.ServiceUnavailable)]
    [InlineData(DurableExternalActivationOutcomeKind.PumpFailed, DurableRuntimeHealthState.Healthy, DurableProblemCodes.SchemaMissing, HttpStatusCode.ServiceUnavailable)]
    [InlineData(DurableExternalActivationOutcomeKind.PumpFailed, DurableRuntimeHealthState.Healthy, DurableProblemCodes.SchemaUpgradeRequired, HttpStatusCode.ServiceUnavailable)]
    [InlineData(DurableExternalActivationOutcomeKind.PumpFailed, DurableRuntimeHealthState.Healthy, DurableProblemCodes.SchemaVersionUnsupported, HttpStatusCode.ServiceUnavailable)]
    [InlineData(DurableExternalActivationOutcomeKind.PumpFailed, DurableRuntimeHealthState.Healthy, DurableProblemCodes.SchemaInconsistent, HttpStatusCode.ServiceUnavailable)]
    [InlineData(DurableExternalActivationOutcomeKind.PumpFailed, DurableRuntimeHealthState.Healthy, DurableProblemCodes.ExternalActivationFailed, HttpStatusCode.InternalServerError)]
    public async Task Every_noncompleted_activation_outcome_maps_to_closed_http_contract(
        DurableExternalActivationOutcomeKind kind,
        DurableRuntimeHealthState? state,
        string? code,
        HttpStatusCode expectedStatus)
    {
        var dependencies = new ActivationTestDependencies();
        dependencies.Activation.Activator = (_, _) => ValueTask.FromResult(ActivationTestData.Result(kind, state, code));
        await using var host = await ActivationHostAsync(dependencies);
        using var request = AuthorizedRequest(new ByteArrayContent([]));

        using var response = await host.Client.SendAsync(request);
        using var json = await ReadJsonAsync(response);
        var root = json.RootElement;

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(kind.ToString(), root.GetProperty("outcome").GetString());
        Assert.Equal(state?.ToString(), GetNullableString(root.GetProperty("observedHealthState")));
        Assert.Equal(code, GetNullableString(root.GetProperty("problemCode")));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("pumpResult").ValueKind);
        AssertExactProperties(root, "outcome", "observedHealthState", "problemCode", "pumpResult");
        Assert.Equal(1, dependencies.Activation.CallCount);
        Assert.Equal(0, dependencies.Admission.CallCount);
    }

    [Fact]
    public async Task Pump_failed_unknown_code_maps_to_host_failure_status_without_changing_diagnostic()
    {
        var dependencies = new ActivationTestDependencies();
        dependencies.Activation.Activator = (_, _) => ValueTask.FromResult(ActivationTestData.Result(
            DurableExternalActivationOutcomeKind.PumpFailed,
            DurableRuntimeHealthState.Healthy,
            DurableProblemCodes.ExternalActivationFailed));
        await using var host = await ActivationHostAsync(dependencies);
        using var request = AuthorizedRequest(new ByteArrayContent([]));

        using var response = await host.Client.SendAsync(request);
        using var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("PumpFailed", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(DurableProblemCodes.ExternalActivationFailed, json.RootElement.GetProperty("problemCode").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("pumpResult").ValueKind);
    }

    [Fact]
    public async Task Every_service_exception_maps_to_safe_host_failure_without_leaking_detail()
    {
        var dependencies = new ActivationTestDependencies();
        dependencies.Activation.Activator = (_, _) => ValueTask.FromException<DurableExternalActivationResult>(new InvalidOperationException("private failure detail"));
        await using var host = await ActivationHostAsync(dependencies);
        using var request = AuthorizedRequest(new ByteArrayContent([]));

        using var response = await host.Client.SendAsync(request);
        using var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("HostFailure", json.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("private failure detail", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(1, dependencies.Activation.CallCount);
        Assert.Equal(0, dependencies.Admission.CallCount);
    }

    private static async Task<ActivationTestHost> AuthorizedHostAsync() => await ActivationHostAsync(new ActivationTestDependencies());

    /// <summary>Verifies liveness remains the exact fixed envelope without consulting Durable dependencies.</summary>
    /// <param name="host">Running reference host.</param>
    private static async Task AssertLiveInvariantAsync(ActivationTestHost host)
    {
        var healthCalls = host.Dependencies.Health.CallCount;
        var activationCalls = host.Dependencies.Activation.CallCount;
        var admissionCalls = host.Dependencies.Admission.CallCount;
        using var response = await host.Client.GetAsync("/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{\"status\":\"Live\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal(healthCalls, host.Dependencies.Health.CallCount);
        Assert.Equal(activationCalls, host.Dependencies.Activation.CallCount);
        Assert.Equal(admissionCalls, host.Dependencies.Admission.CallCount);
    }

    private static async Task<ActivationTestHost> ActivationHostAsync(ActivationTestDependencies dependencies)
    {
        var host = await ActivationTestHost.StartAsync(dependencies: dependencies);
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ActivationTestHost.ValidToken);
        return host;
    }

    private static HttpRequestMessage AuthorizedRequest(HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/private/durable/activate") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ActivationTestHost.ValidToken);
        return request;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static string? GetNullableString(JsonElement element) => element.ValueKind == JsonValueKind.Null ? null : element.GetString();

    private static void AssertExactProperties(JsonElement element, params string[] expected)
    {
        Assert.Equal(expected.Order(StringComparer.Ordinal), element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    private static async Task<(int StatusCode, byte[] Body)> MaterializeResultAsync(IResult result, IServiceProvider requestServices)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = requestServices;
        using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        await result.ExecuteAsync(context);
        return ((int)context.Response.StatusCode, responseBody.ToArray());
    }

    private sealed class OneByteContent : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 1;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync("x"u8.ToArray()).AsTask();

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            stream.WriteAsync("x"u8.ToArray(), cancellationToken).AsTask();
    }

    private sealed class ChunkedTestContent(byte[] bytes, bool knownLength = false) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = knownLength ? bytes.LongLength : 0;
            return knownLength;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            stream.WriteAsync(bytes, cancellationToken).AsTask();
    }

    private sealed class CancellationAwareReadStream : Stream
    {
        private readonly TaskCompletionSource readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource neverCompletes = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task ReadStarted => readStarted.Task;

        internal CancellationToken CancellationToken { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            CancellationToken = cancellationToken;
            readStarted.TrySetResult();
            return WaitForCancellationAsync(cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private async ValueTask<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await neverCompletes.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }
}

internal static class JsonElementContractAssertions
{
    /// <summary>Reads a nullable Boolean JSON field without conflating JSON null with false.</summary>
    /// <param name="element">JSON value to inspect.</param>
    internal static bool? GetBooleanOrNull(this JsonElement element) => element.ValueKind == JsonValueKind.Null ? null : element.GetBoolean();
}
