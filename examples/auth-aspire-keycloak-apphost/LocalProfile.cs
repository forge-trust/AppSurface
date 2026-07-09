using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Aspire;
using Microsoft.Extensions.Logging;

namespace AuthAspireKeycloakAppHost;

/// <summary>
/// Runs the local real-OIDC proof graph for manual browser sign-in.
/// </summary>
[Command("local", Description = "Run the AppSurface Auth Aspire Keycloak local proof.")]
public sealed partial class LocalProfile : AspireProfile
{
    private readonly AuthAspireKeycloakWebComponent _web;

    /// <summary>
    /// Creates the local profile.
    /// </summary>
    /// <param name="web">Configured web component.</param>
    /// <param name="logger">Logger for the profile.</param>
    public LocalProfile(AuthAspireKeycloakWebComponent web, ILogger<LocalProfile> logger)
        : base(logger)
    {
        _web = web;
    }

    /// <inheritdoc />
    public override IEnumerable<IAspireComponent> GetComponents()
    {
        yield return _web;
    }
}

/// <summary>
/// Runs the local graph, waits for Keycloak proof readiness, and executes the verifier project.
/// </summary>
[Command("verify", Description = "Start local Keycloak, run the web proof, verify readiness, then stop.")]
public sealed partial class VerifyProfile : ICommand
{
    private const string VerifierResourceName = "auth-aspire-keycloak-verifier";

    private readonly AuthAspireKeycloakComponent _keycloak;
    private readonly AuthAspireKeycloakWebComponent _web;
    private readonly AuthAspireKeycloakVerifierComponent _verifier;
    private readonly ILogger<VerifyProfile> _logger;

    /// <summary>
    /// Creates the verify profile.
    /// </summary>
    /// <param name="keycloak">Keycloak component that supplies readiness metadata.</param>
    /// <param name="web">Configured web component.</param>
    /// <param name="verifier">Verifier component that probes the web proof.</param>
    /// <param name="logger">Logger for the profile.</param>
    public VerifyProfile(
        AuthAspireKeycloakComponent keycloak,
        AuthAspireKeycloakWebComponent web,
        AuthAspireKeycloakVerifierComponent verifier,
        ILogger<VerifyProfile> logger)
    {
        _keycloak = keycloak;
        _web = web;
        _verifier = verifier;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IConsole console)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var appBuilder = DistributedApplication.CreateBuilder([]);
        var context = new AspireStartupContext(appBuilder);
        context.Resolve(_web);
        context.Resolve(_verifier);

        await using var app = appBuilder.Build();
        try
        {
            await console.Output.WriteLineAsync("Starting AppSurface Auth Aspire Keycloak verification...");
            await app.StartAsync(timeout.Token);
            await _keycloak.Resolved.Readiness.CheckOnceAsync(timeout.Token);

            await app.ResourceNotifications.WaitForResourceAsync(
                VerifierResourceName,
                [KnownResourceStates.Finished, KnownResourceStates.Exited, KnownResourceStates.FailedToStart],
                timeout.Token);

            var exitCode = 1;
            if (app.ResourceNotifications.TryGetCurrentState(VerifierResourceName, out var resourceEvent))
            {
                exitCode = resourceEvent.Snapshot.ExitCode ?? 1;
            }

            Environment.ExitCode = exitCode;
            if (exitCode == 0)
            {
                await console.Output.WriteLineAsync("AppSurface Auth Aspire Keycloak verification completed successfully.");
            }
            else
            {
                await console.Error.WriteLineAsync($"AppSurface Auth Aspire Keycloak verification failed with exit code {exitCode}.");
            }
        }
        catch (OperationCanceledException)
        {
            Environment.ExitCode = 124;
            await console.Error.WriteLineAsync("AppSurface Auth Aspire Keycloak verification timed out after 5 minutes.");
        }
        catch (Exception exception) when (IsNonFatalVerificationException(exception))
        {
            _logger.LogCritical(exception, "Error running AppSurface Auth Aspire Keycloak verification");
            Environment.ExitCode = -536;
        }
        finally
        {
            try
            {
                await app.StopAsync(CancellationToken.None);
            }
            catch (Exception exception) when (IsNonFatalVerificationException(exception))
            {
                _logger.LogWarning(exception, "Error stopping AppSurface Auth Aspire Keycloak verification resources");
                if (Environment.ExitCode == 0)
                {
                    Environment.ExitCode = -537;
                }
            }
        }
    }

    private static bool IsNonFatalVerificationException(Exception exception)
    {
        return exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException
            and not AppDomainUnloadedException;
    }
}
