using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Config;

/// <summary>Validates registrations and known opted-in plans before readiness without resolving secret payloads.</summary>
/// <remarks>
/// This is an <see cref="IHostedLifecycleService"/> so local validation runs in <see cref="StartingAsync"/>, before
/// any ordinary <see cref="IHostedService.StartAsync"/>
/// method can stop a fast host. Validation remains local and creates no wrappers or remote-resolution state.
/// </remarks>
internal sealed class ConfigCompositionStartupValidator(ConfigCompositionEngine engine,
    IEnumerable<ConfigAuditKnownEntry> entries, IEnvironmentProvider environment) : IHostedLifecycleService
{
    /// <summary>Compiles known roots locally before ordinary hosted-service startup begins.</summary>
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        if (engine.Registry.Errors.Count > 0)
            throw new ConfigurationCompositionException(environment.Environment, "configuration",
                engine.Registry.Errors.Select(c => new ConfigCompositionFailure("configuration", c)).ToArray());
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            engine.ValidatePlan(environment.Environment, entry.LogicalKey, entry.ValueType);
        }
        return Task.CompletedTask;
    }

    /// <summary>Completes the ordinary hosted-service phase because validation ran in <see cref="StartingAsync"/>.</summary>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Completes the post-start lifecycle phase without activating wrappers or resolving payloads.</summary>
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Completes the post-stop lifecycle phase because validation owns no shutdown work.</summary>
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Completes the pre-stop lifecycle phase because validation owns no shutdown work.</summary>
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>No background work or payload state survives startup validation.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
