namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Provides shared state predicates for configuration audit entries.
/// </summary>
internal static class ConfigAuditEntryStateHelpers
{
    /// <summary>
    /// Determines whether an entry or any descendant represents a partial provider patch.
    /// </summary>
    /// <param name="entry">The entry to inspect.</param>
    /// <returns><see langword="true"/> when the entry is explicitly partial, has a patch source, or contains a partial child.</returns>
    internal static bool IsPartiallyResolved(ConfigAuditEntry entry) =>
        entry.State == ConfigAuditEntryState.PartiallyResolved
        || entry.Sources.Any(source => source.Role == ConfigAuditSourceRole.Patch)
        || entry.Children.Any(IsPartiallyResolved);
}
