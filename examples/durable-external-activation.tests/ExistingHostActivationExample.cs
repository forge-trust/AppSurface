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
