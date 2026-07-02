namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Allows external providers to expose effective keys for configuration audit inventory.
/// </summary>
/// <remarks>
/// Enumeration should describe the provider's effective view for the requested environment. Providers that cannot
/// enumerate safely, such as remote secret stores where inventory access is broader than value access, should not
/// implement this interface.
/// </remarks>
public interface IConfigProviderAuditKeyEnumerator
{
    /// <summary>
    /// Enumerates effective provider keys for <paramref name="environment"/>.
    /// </summary>
    /// <param name="environment">The environment being audited.</param>
    /// <returns>Discovered keys with redaction-ready values and source metadata.</returns>
    IReadOnlyList<ConfigProviderAuditDiscoveredKey> EnumerateKeys(string environment);
}
