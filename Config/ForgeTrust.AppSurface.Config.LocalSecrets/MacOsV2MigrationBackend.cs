using System.Security.Cryptography;
using System.Text;
using static ForgeTrust.AppSurface.Config.LocalSecrets.PlatformAppSurfaceLocalSecretStore;

namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>Runs exact Keychain I/O under the shared package lease with a durable, value-free journal.</summary>
internal sealed class MacOsV2MigrationBackend(
    MacOsV2CompatibilityLocalSecretStore owner,
    AppSurfaceLocalSecretIdentity source,
    AppSurfaceLocalSecretIdentity destination,
    bool sourceIsV2) : IAppSurfaceLocalSecretMigrationBackend
{
    private LocalSecretMigrationJournalFile? _journal;
    private LocalSecretMigrationJournalFile Journal => _journal ??= new(Path.Combine(
        PlatformLocalSecretStatePaths.DefaultDirectory,
        $"local-secrets-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", owner.Name, source.StorageName, destination.StorageName)))).ToLowerInvariant()}.migration.json"));

    public bool SupportsDurableMigration => sourceIsV2 || owner.SupportsExactLegacyMigration;

    public IDisposable AcquireMaintenanceLease(TimeSpan timeout, CancellationToken cancellationToken) =>
        PlatformLocalSecretMaintenanceLease.Acquire(PlatformLocalSecretStatePaths.DefaultDirectory,
            source.ApplicationName, source.Environment, source.KeyPrefix, timeout, cancellationToken);

    public AppSurfaceLocalSecretMigrationJournal? ReadJournal() => Journal.Read();
    public void CommitJournal(AppSurfaceLocalSecretMigrationJournal journal) => Journal.Commit(journal);

    public string? ReadExact(string storedKey)
    {
        var isDestination = string.Equals(storedKey, destination.StorageName, StringComparison.Ordinal);
        var result = owner.ReadMigrationValue(isDestination ? destination : source, isDestination || sourceIsV2);
        return result.Status switch
        {
            LocalSecretResultStatus.Found => result.Value ?? throw new IOException("The Keychain returned no value for a found record."),
            LocalSecretResultStatus.Missing => null,
            _ => throw new IOException("The Keychain could not confirm the exact identifier.")
        };
    }

    public void WriteExact(string storedKey, string value)
    {
        var result = owner.WriteMigrationValue(destination, value);
        if (result.Status != LocalSecretResultStatus.Found) throw new IOException("The Keychain could not write the destination.");
    }

    public void DeleteExact(string storedKey)
    {
        var result = owner.DeleteMigrationValue(source, sourceIsV2);
        if (result.Status is not (LocalSecretResultStatus.Found or LocalSecretResultStatus.Missing)) throw new IOException("The Keychain could not delete the source.");
    }

    public void PublishIndex()
    {
        var journal = ReadJournal() ?? throw new IOException("The migration journal is missing before index publication.");
        var index = owner.ReadMigrationIndex(destination.ApplicationName, destination.Environment, destination.KeyPrefix);
        if (index.Status != LocalSecretResultStatus.Found) throw new IOException("The v2 index could not be read.");
        var keys = index.Keys.ToHashSet(StringComparer.Ordinal);
        keys.Add(destination.Key.Value);
        if (journal.State == AppSurfaceLocalSecretMigrationState.SourceDeletePending && sourceIsV2) keys.Remove(source.StoredKey);
        var result = owner.WriteMigrationIndex(destination.ApplicationName, destination.Environment, destination.KeyPrefix, keys);
        if (result.Status != LocalSecretResultStatus.Found) throw new IOException("The v2 index could not be published.");
        if (journal.State == AppSurfaceLocalSecretMigrationState.SourceDeletePending && !sourceIsV2)
            owner.PublishLegacyMigrationIndex(source);
    }
}
