// docs:snippet durable-external-activation-adoption-imports:start
using System;
using System.Threading;
using System.Threading.Tasks;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
// docs:snippet durable-external-activation-adoption-imports:end

// docs:snippet durable-external-activation-export-imports:start
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using ForgeTrust.AppSurface.Core;
using OpenTelemetry;
using OpenTelemetry.Trace;
// docs:snippet durable-external-activation-export-imports:end

namespace ForgeTrust.AppSurface.Examples.DurableExternalActivation.Tests;

// docs:snippet durable-external-activation-direct-admission:start
/// <summary>Shows the lower-level calls retained by hosts that intentionally own direct-admission orchestration.</summary>
internal static class ExistingHostDirectAdmissionExample
{
    /// <summary>Obtains advisory health and asks the provider for an authoritative attempt.</summary>
    /// <param name="provider">The existing host's actual service provider.</param>
    /// <param name="pumpRequest">Existing host-configured provider discovery limits.</param>
    /// <param name="callerCancellation">The existing caller token.</param>
    /// <returns>Observed health and the authoritative attempt for the existing host's own outcome policy.</returns>
    /// <remarks>These two calls alone do not supply the service's phase, deadline, or exception policy.</remarks>
    internal static async ValueTask<(DurableRuntimeHealthSnapshot Health, DurableRuntimePumpAttempt Attempt)> RunAsync(
        IServiceProvider provider,
        DurableRuntimePumpRequest pumpRequest,
        CancellationToken callerCancellation)
    {
        var health = await provider.GetRequiredService<IDurableRuntimeHealth>()
            .GetAsync(callerCancellation).ConfigureAwait(false);
        var attempt = await provider.GetRequiredService<IDurableRuntimePumpAdmission>()
            .TryRunOnceAsync(pumpRequest, callerCancellation).ConfigureAwait(false);
        return (health, attempt);
    }
}
// docs:snippet durable-external-activation-direct-admission:end

// docs:snippet durable-external-activation-adoption:start
/// <summary>Demonstrates adopting the service inside an existing host without adding a package API or route.</summary>
internal static class ExistingHostActivationExample
{
    /// <summary>Builds and eagerly resolves the actual host, disposing it if a dependency factory fails.</summary>
    /// <param name="builder">Existing host builder with its provider and host policy already configured.</param>
    /// <returns>The built application and activation service, without starting a listener.</returns>
    internal static async ValueTask<(WebApplication App, IDurableExternalActivationService Service)> BuildBeforeListeningAsync(
        WebApplicationBuilder builder)
    {
        Register(builder);
        var app = builder.Build();
        try
        {
            return (app, ResolveBeforeListening(app));
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Adds passive activation registration and validates the host's actual container in every environment.</summary>
    /// <param name="builder">Existing host builder, retaining its provider, authentication, and authorization setup.</param>
    internal static void Register(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddDurableExternalActivation();
        builder.Host.UseDefaultServiceProvider((_, options) =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });
    }

    /// <summary>Resolves the service before listening so opaque dependency factories are checked as well.</summary>
    /// <param name="app">Existing application built from the same host builder.</param>
    /// <returns>The host's registered activation service.</returns>
    internal static IDurableExternalActivationService ResolveBeforeListening(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Services.GetRequiredService<IDurableExternalActivationService>();
    }

    /// <summary>Invokes the service after the existing endpoint has authorized the caller and validated an empty body.</summary>
    /// <param name="service">Eagerly resolved service from the existing host.</param>
    /// <param name="callerCancellation">Caller or transport token, separate from both configured budgets.</param>
    /// <returns>The example's HTTP status and unchanged result for the host's existing response projection.</returns>
    internal static async ValueTask<(int StatusCode, DurableExternalActivationResult Result)> ActivateAsync(
        IDurableExternalActivationService service,
        CancellationToken callerCancellation)
    {
        ArgumentNullException.ThrowIfNull(service);
        var pumpRequest = new DurableRuntimePumpRequest(
            maximumItems: 32,
            timeBudget: TimeSpan.FromSeconds(2),
            surfaces: DurableRuntimeSurface.Work);
        var request = new DurableExternalActivationRequest(
            pumpRequest,
            requestBudget: TimeSpan.FromSeconds(10));

        var result = await service.ActivateAsync(request, callerCancellation).ConfigureAwait(false);
        return (GetStatusCode(result), result);
    }

    /// <summary>Handles every v1 outcome explicitly, preserving the reference host's chosen status policy.</summary>
    /// <param name="result">Validated service result, including its observed state, code, and aggregate.</param>
    /// <returns>The reference host's status without rewriting a returned result after late caller cancellation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An outcome outside the handled v1 contract is supplied.</exception>
    internal static int GetStatusCode(DurableExternalActivationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Kind switch
        {
            DurableExternalActivationOutcomeKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
            DurableExternalActivationOutcomeKind.Incompatible => StatusCodes.Status503ServiceUnavailable,
            DurableExternalActivationOutcomeKind.Draining => StatusCodes.Status503ServiceUnavailable,
            DurableExternalActivationOutcomeKind.Busy => StatusCodes.Status409Conflict,
            DurableExternalActivationOutcomeKind.CanceledBeforeAdmission => StatusCodes.Status408RequestTimeout,
            DurableExternalActivationOutcomeKind.RequestBudgetExceeded => StatusCodes.Status504GatewayTimeout,
            DurableExternalActivationOutcomeKind.Completed => StatusCodes.Status200OK,
            DurableExternalActivationOutcomeKind.ActivationFailed => StatusCodes.Status500InternalServerError,
            DurableExternalActivationOutcomeKind.PumpCanceled => StatusCodes.Status408RequestTimeout,
            DurableExternalActivationOutcomeKind.PumpFailed => result.ProblemCode is
                DurableProblemCodes.StoreUnavailable
                or DurableProblemCodes.RecoveryEpochRequired
                or DurableProblemCodes.SchemaMissing
                or DurableProblemCodes.SchemaUpgradeRequired
                or DurableProblemCodes.SchemaVersionUnsupported
                or DurableProblemCodes.SchemaInconsistent
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status500InternalServerError,
            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Kind, "Unsupported activation outcome."),
        };
    }
}
// docs:snippet durable-external-activation-adoption:end

// docs:snippet durable-external-activation-in-process-export:start
/// <summary>Proves export from the source-owned test host running in the same process as the OpenTelemetry SDK.</summary>
internal static class InProcessActivationExportExample
{
    /// <summary>Starts the in-process test host and sends an authorized empty wake through its real service.</summary>
    /// <param name="callerCancellation">Cancellation for the test-owned HTTP request.</param>
    /// <returns>The activation received by the attached exporter after a successful SDK flush.</returns>
    /// <remarks>The fixture uses controlled health/admission dependencies; this recipe alone proves no persistence.</remarks>
    internal static async Task<Activity> RequireExportAsync(CancellationToken callerCancellation)
    {
        var exported = new ConcurrentQueue<Activity>();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(AppSurfaceActivitySources.ActivitySourceName)
            .SetSampler(new AlwaysOnSampler())
            .AddProcessor(new SimpleActivityExportProcessor(new RecordingActivityExporter(exported)))
            .Build();
        await using var host = await ActivationTestHost.StartAsync(new ActivationHostOptions
        {
            UseProductionActivationService = true,
        }).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/private/durable/activate")
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ActivationTestHost.ValidToken);
        using var response = await host.Client.SendAsync(request, callerCancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        _ = await response.Content.ReadAsStringAsync(callerCancellation).ConfigureAwait(false);

        if (!tracerProvider.ForceFlush(30_000))
        {
            throw new InvalidOperationException("The activation exporter did not flush.");
        }

        return exported.Single(activity =>
            activity.Source.Name == AppSurfaceActivitySources.ActivitySourceName
            && activity.OperationName == "appsurface.durable.runtime.activation");
    }

    /// <summary>Collects activities delivered through the SDK's export processor for this test only.</summary>
    /// <param name="exported">Test-owned receipt collection.</param>
    private sealed class RecordingActivityExporter(ConcurrentQueue<Activity> exported) : BaseExporter<Activity>
    {
        /// <inheritdoc />
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                exported.Enqueue(activity);
            }

            return ExportResult.Success;
        }
    }
}
// docs:snippet durable-external-activation-in-process-export:end

/// <summary>Compiles and exercises the canonical guide's adoption and same-process exporter recipes.</summary>
[Collection(ActivationExporterCollection.Name)]
public sealed class ActivationDocumentationExamplesTests
{
    /// <summary>Checks all ten outcomes with constructor-validated examples and a distinct caller token.</summary>
    /// <param name="kind">The v1 outcome represented by this public-constructor example.</param>
    /// <param name="expectedStatus">The reference host's chosen status for that outcome.</param>
    [Theory]
    [InlineData(DurableExternalActivationOutcomeKind.Unavailable, 503)]
    [InlineData(DurableExternalActivationOutcomeKind.Incompatible, 503)]
    [InlineData(DurableExternalActivationOutcomeKind.Draining, 503)]
    [InlineData(DurableExternalActivationOutcomeKind.Busy, 409)]
    [InlineData(DurableExternalActivationOutcomeKind.CanceledBeforeAdmission, 408)]
    [InlineData(DurableExternalActivationOutcomeKind.RequestBudgetExceeded, 504)]
    [InlineData(DurableExternalActivationOutcomeKind.Completed, 200)]
    [InlineData(DurableExternalActivationOutcomeKind.ActivationFailed, 500)]
    [InlineData(DurableExternalActivationOutcomeKind.PumpCanceled, 408)]
    [InlineData(DurableExternalActivationOutcomeKind.PumpFailed, 500)]
    public async Task Adoption_handles_each_validated_outcome_without_losing_result_facts(
        DurableExternalActivationOutcomeKind kind,
        int expectedStatus)
    {
        var expected = ValidatedExample(kind);
        using var caller = new CancellationTokenSource();
        var service = new ExampleActivationService(expected, caller.Cancel);

        var (statusCode, result) = await ExistingHostActivationExample.ActivateAsync(service, caller.Token);

        Assert.Equal(expectedStatus, statusCode);
        Assert.Same(expected, result);
        Assert.Equal(1, service.CallCount);
        Assert.Equal(caller.Token, service.CallerCancellation);
        Assert.True(caller.IsCancellationRequested);
        var request = Assert.IsType<DurableExternalActivationRequest>(service.Request);
        Assert.Equal(TimeSpan.FromSeconds(10), request.RequestBudget);
        Assert.Equal(TimeSpan.FromSeconds(2), request.PumpRequest.TimeBudget);
        Assert.Equal(32, request.PumpRequest.MaximumItems);
        Assert.Equal(DurableRuntimeSurface.Work, request.PumpRequest.Surfaces);
        Assert.NotEqual(request.RequestBudget, request.PumpRequest.TimeBudget);
        Assert.Same(expected.PumpResult, result.PumpResult);
        if (kind == DurableExternalActivationOutcomeKind.Completed)
        {
            var aggregate = Assert.IsType<DurableRuntimePumpResult>(result.PumpResult);
            Assert.True(aggregate.Failed > 0);
            Assert.True(aggregate.HasMore);
        }
    }

    /// <summary>Verifies every permitted pump-failure diagnostic selects the documented dependency or failure status.</summary>
    /// <param name="problemCode">A permitted bounded provider or orchestration diagnostic.</param>
    /// <param name="expectedStatus">Dependency failure 503 or unexpected orchestration failure 500.</param>
    [Theory]
    [InlineData(DurableProblemCodes.StoreUnavailable, 503)]
    [InlineData(DurableProblemCodes.RecoveryEpochRequired, 503)]
    [InlineData(DurableProblemCodes.SchemaMissing, 503)]
    [InlineData(DurableProblemCodes.SchemaUpgradeRequired, 503)]
    [InlineData(DurableProblemCodes.SchemaVersionUnsupported, 503)]
    [InlineData(DurableProblemCodes.SchemaInconsistent, 503)]
    [InlineData(DurableProblemCodes.ExternalActivationFailed, 500)]
    public void Adoption_maps_each_permitted_pump_failure_code(string problemCode, int expectedStatus)
    {
        var result = new DurableExternalActivationResult(
            DurableExternalActivationOutcomeKind.PumpFailed,
            DurableRuntimeHealthState.Healthy,
            problemCode,
            pumpResult: null);

        Assert.Equal(expectedStatus, ExistingHostActivationExample.GetStatusCode(result));
    }

    /// <summary>Checks passive registration and eager resolution on the existing application's actual provider.</summary>
    [Fact]
    public async Task Adoption_keeps_an_existing_service_and_resolves_it_before_listening()
    {
        var service = new ExampleActivationService(ValidatedExample(DurableExternalActivationOutcomeKind.Completed));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Services.AddSingleton<IDurableExternalActivationService>(service);

        var (builtApp, resolved) = await ExistingHostActivationExample.BuildBeforeListeningAsync(builder);
        await using var app = builtApp;

        Assert.Same(service, resolved);
        Assert.Equal(0, service.CallCount);
        Assert.False(app.Lifetime.ApplicationStarted.IsCancellationRequested);
    }

    /// <summary>Checks an opaque factory failure is caught by the source recipe before the listener starts.</summary>
    [Fact]
    public async Task Adoption_eager_resolution_exposes_a_factory_failure_before_listening()
    {
        var failure = new InvalidOperationException("controlled dependency failure");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Services.AddSingleton<IDurableExternalActivationService>(_ => throw failure);

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExistingHostActivationExample.BuildBeforeListeningAsync(builder).AsTask());

        Assert.Same(failure, observed);
    }

    /// <summary>Executes the guide's in-process export recipe against the authorized endpoint and production service.</summary>
    [Fact]
    public async Task Export_recipe_receives_the_real_in_process_activation()
    {
        var activity = await InProcessActivationExportExample.RequireExportAsync(CancellationToken.None);

        Assert.Equal("appsurface.durable.runtime.activation", activity.OperationName);
        Assert.Equal(AppSurfaceActivitySources.ActivitySourceName, activity.Source.Name);
        Assert.Equal(ActivityKind.Internal, activity.Kind);
        Assert.Equal(1, activity.GetTagItem("appsurface.durable.activation.contract_version"));
        Assert.Equal("completed", activity.GetTagItem("appsurface.durable.activation.phase"));
        Assert.Equal("Completed", activity.GetTagItem("appsurface.durable.activation.outcome"));
        Assert.Empty(activity.Events);
    }

    /// <summary>Creates only publicly validated result examples, including retained stale and failed-item evidence.</summary>
    /// <param name="kind">The closed outcome to illustrate.</param>
    /// <returns>A result whose state, diagnostic, and aggregate are validated by the public constructor.</returns>
    private static DurableExternalActivationResult ValidatedExample(DurableExternalActivationOutcomeKind kind) => kind switch
    {
        DurableExternalActivationOutcomeKind.Unavailable => new(
            kind, DurableRuntimeHealthState.Unavailable, DurableProblemCodes.StoreUnavailable, null),
        DurableExternalActivationOutcomeKind.Incompatible => new(
            kind, DurableRuntimeHealthState.Incompatible, DurableProblemCodes.SchemaUpgradeRequired, null),
        DurableExternalActivationOutcomeKind.Draining => new(kind, DurableRuntimeHealthState.Draining, null, null),
        DurableExternalActivationOutcomeKind.Busy => new(
            kind, DurableRuntimeHealthState.Stale, DurableProblemCodes.WorkerIdentityConflict, null),
        DurableExternalActivationOutcomeKind.CanceledBeforeAdmission => new(kind, null, null, null),
        DurableExternalActivationOutcomeKind.RequestBudgetExceeded => new(
            kind, DurableRuntimeHealthState.Stale, DurableProblemCodes.ActivatorStale, null),
        DurableExternalActivationOutcomeKind.Completed => new(
            kind,
            DurableRuntimeHealthState.Healthy,
            null,
            new DurableRuntimePumpResult(6, 5, 4, 1, 2, true, DateTimeOffset.UnixEpoch, TimeSpan.FromTicks(123))),
        DurableExternalActivationOutcomeKind.ActivationFailed => new(
            kind, DurableRuntimeHealthState.Stale, DurableProblemCodes.ExternalActivationFailed, null),
        DurableExternalActivationOutcomeKind.PumpCanceled => new(kind, DurableRuntimeHealthState.NotStarted, null, null),
        DurableExternalActivationOutcomeKind.PumpFailed => new(
            kind, DurableRuntimeHealthState.Healthy, DurableProblemCodes.ExternalActivationFailed, null),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Returns a validated authoritative result while recording the exact invocation inputs.</summary>
    /// <param name="result">Result selected by the test before invocation.</param>
    /// <param name="beforeReturn">Optional controlled late-cancellation callback.</param>
    private sealed class ExampleActivationService(DurableExternalActivationResult result, Action? beforeReturn = null)
        : IDurableExternalActivationService
    {
        /// <summary>Gets the number of service invocations made by the recipe.</summary>
        internal int CallCount { get; private set; }

        /// <summary>Gets the exact explicitly budgeted request received from the recipe.</summary>
        internal DurableExternalActivationRequest? Request { get; private set; }

        /// <summary>Gets the unmodified caller token received from the recipe.</summary>
        internal CancellationToken CallerCancellation { get; private set; }

        /// <inheritdoc />
        public ValueTask<DurableExternalActivationResult> ActivateAsync(
            DurableExternalActivationRequest request,
            CancellationToken callerCancellation = default)
        {
            CallCount++;
            Request = request;
            CallerCancellation = callerCancellation;
            beforeReturn?.Invoke();
            return ValueTask.FromResult(result);
        }
    }
}
