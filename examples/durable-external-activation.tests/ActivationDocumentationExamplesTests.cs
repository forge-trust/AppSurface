using System.Diagnostics;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Examples.DurableExternalActivation.Tests;

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
