using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Aspire;
using Microsoft.Extensions.Logging;

namespace ProductReadinessLabAppHost;

/// <summary>
/// Runs the product-readiness lab with local Aspire resources.
/// </summary>
[Command("local", Description = "Run the AppSurface product-readiness lab with local Postgres.")]
public sealed partial class LocalProfile : AspireProfile
{
    private readonly ProductReadinessWebComponent _web;

    /// <summary>
    /// Creates the local profile.
    /// </summary>
    /// <param name="web">Configured web component.</param>
    /// <param name="logger">Logger for the profile.</param>
    public LocalProfile(ProductReadinessWebComponent web, ILogger<LocalProfile> logger)
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
/// Runs the product-readiness lab with local Postgres and a bounded report verifier.
/// </summary>
[Command("verify", Description = "Start local Postgres, run the lab, verify the readiness report, then stop.")]
public sealed partial class VerifyProfile : ICommand
{
    private const string VerifierResourceName = "product-readiness-lab-verifier";

    private readonly ProductReadinessWebComponent _web;
    private readonly ProductReadinessVerifierComponent _verifier;
    private readonly ILogger<VerifyProfile> _logger;

    /// <summary>
    /// Creates the verify profile.
    /// </summary>
    /// <param name="web">Configured web component.</param>
    /// <param name="verifier">Verifier component that probes the AppHost-backed report.</param>
    /// <param name="logger">Logger for the profile.</param>
    public VerifyProfile(
        ProductReadinessWebComponent web,
        ProductReadinessVerifierComponent verifier,
        ILogger<VerifyProfile> logger)
    {
        _web = web;
        _verifier = verifier;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IConsole console)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var appBuilder = new DistributedApplicationBuilder([]);
        var context = new AspireStartupContext(appBuilder);
        context.Resolve(_web);
        context.Resolve(_verifier);

        await using var app = appBuilder.Build();
        try
        {
            await console.Output.WriteLineAsync("Starting product-readiness AppHost verification...");
            await app.StartAsync(timeout.Token);

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
                await console.Output.WriteLineAsync("Product-readiness AppHost verification completed successfully.");
            }
            else
            {
                await console.Error.WriteLineAsync($"Product-readiness AppHost verification failed with exit code {exitCode}.");
            }
        }
        catch (OperationCanceledException)
        {
            Environment.ExitCode = 124;
            await console.Error.WriteLineAsync("Product-readiness AppHost verification timed out after 5 minutes.");
        }
        catch (Exception exception)
        {
            _logger.LogCritical(exception, "Error running product-readiness AppHost verification");
            Environment.ExitCode = -150;
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }
}
