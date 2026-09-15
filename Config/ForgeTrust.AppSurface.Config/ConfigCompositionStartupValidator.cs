using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Config;

/// <summary>Validates registrations and known opted-in plans before readiness without resolving secret payloads.</summary>
internal sealed class ConfigCompositionStartupValidator(ConfigCompositionEngine engine,
    IEnumerable<ConfigAuditKnownEntry> entries, IEnvironmentProvider environment) : IHostedService
{
    /// <summary>Compiles known roots locally. Wrapper construction and enabled remote resolution remain lazy.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (engine.Registry.Errors.Count > 0)
            throw new ConfigurationCompositionException(environment.Environment, "configuration",
                engine.Registry.Errors.Select(c => new ConfigCompositionFailure("configuration", c)).ToArray());
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            engine.ValidatePlan(environment.Environment, entry.Key, entry.ValueType);
        }
        return Task.CompletedTask;
    }

    /// <summary>No background work or payload state survives startup validation.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
