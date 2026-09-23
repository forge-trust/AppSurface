using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeTrust.AppSurface.Durable;

/// <summary>Materializes and validates local Work values before any codec enumeration can mask duplicate Work.</summary>
internal sealed class DurableWorkRegistrationCatalog
{
    /// <summary>Closes the local registration set; custom public registries need not enumerate private state.</summary>
    internal DurableWorkRegistrationCatalog(IEnumerable<DurableWorkRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var map = new Dictionary<(string Name, string Version), DurableWorkRegistration>();
        foreach (var registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration);
            if (!map.TryAdd((registration.WorkName, registration.WorkVersion), registration))
            {
                throw new InvalidOperationException(
                    $"Durable work '{registration.WorkName}' version '{registration.WorkVersion}' is registered more than once.");
            }
        }

        Registrations = new System.Collections.ObjectModel.ReadOnlyDictionary<(string Name, string Version), DurableWorkRegistration>(map);
        Contracts = Array.AsReadOnly(map.Keys.Select(static key => new DurableWorkContractIdentity(key.Name, key.Version))
            .OrderBy(static identity => identity.WorkName, StringComparer.Ordinal)
            .ThenBy(static identity => identity.WorkVersion, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Gets exact immutable registration values indexed by Work identity.</summary>
    internal IReadOnlyDictionary<(string Name, string Version), DurableWorkRegistration> Registrations { get; }
    /// <summary>Gets sorted public discovery identities.</summary>
    internal IReadOnlyList<DurableWorkContractIdentity> Contracts { get; }
}

/// <summary>Installs acyclic passive defaults without replacing or reordering consumer descriptors.</summary>
internal static class DurableRegistryInstallation
{
    /// <summary>Uses TryAdd for each default so module/Work/Flow ordering preserves consumer overrides.</summary>
    internal static void AddDefaults(IServiceCollection services)
    {
        services.TryAddSingleton(static provider => new DurableWorkRegistrationCatalog(provider.GetServices<DurableWorkRegistration>()));
        services.TryAddSingleton<IDurableWorkRegistry>(static provider =>
            new DurableWorkRegistry(provider.GetRequiredService<DurableWorkRegistrationCatalog>()));
        services.TryAddSingleton<IDurablePayloadCodecRegistry>(static provider =>
        {
            // This must precede resolving either enumerable, including user-owned descriptor factories.
            var catalog = provider.GetRequiredService<DurableWorkRegistrationCatalog>();
            var contributions = catalog.Registrations.Values.SelectMany(static registration => new[]
            {
                new DurableCodecContribution(registration.Snapshot.Work, registration.RequiresFrozenView),
                new DurableCodecContribution(registration.Snapshot.Result, registration.RequiresFrozenView)
            }).Concat(provider.GetServices<DurableCodecContribution>());
            return new DurablePayloadCodecRegistry(contributions, provider.GetServices<IDurablePayloadCodec>());
        });
    }
}
