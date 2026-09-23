using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Config;

namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>Describes the durable state of one exact stored-key migration.</summary>
public enum AppSurfaceLocalSecretMigrationState
{
    /// <summary>The operation journal was durably prepared.</summary>
    Prepared = 0,
    /// <summary>The destination was written.</summary>
    DestinationWritten = 1,
    /// <summary>The destination and source rereads matched.</summary>
    DestinationVerified = 2,
    /// <summary>The destination and index are durable and source deletion is pending.</summary>
    SourceDeletePending = 3,
    /// <summary>The source was deleted and the operation completed.</summary>
    Complete = 4,
    /// <summary>The operation cannot safely proceed and requires operator recovery.</summary>
    Unrecoverable = 5,
}

/// <summary>Describes a value-safe result for an exact stored-key migration.</summary>
/// <param name="Status">The value-safe outcome status for the migration request.</param>
/// <param name="MigrationId">The durable journal's operation identifier when known, or a fixed sentinel when no identifier was available.</param>
/// <param name="State">The durable migration state, or the reported state at which a failed request stopped.</param>
/// <param name="SourceStoredKey">The exact stored identifier requested as the migration source.</param>
/// <param name="DestinationKey">The logical destination key value.</param>
/// <param name="Diagnostic">A value-safe diagnostic when the request did not complete normally; otherwise <see langword="null"/>.</param>
/// <param name="Source">The backend or store name that handled the request.</param>
/// <remarks>
/// A real identifier is returned when a journal was read or a new operation identifier was generated, including when
/// the subsequent journal commit reports failure. Fixed sentinels include
/// <c>invalid</c>, <c>invalid-source</c>, <c>unsupported</c>, <c>same-key</c>, <c>unavailable</c>, and
/// <c>io-failure</c>; they are not operation identifiers and do not prove that a failed journal write left no durable
/// journal. Retry a failed operation with the same exact source and destination so any persisted state can be resumed.
/// </remarks>
public sealed record AppSurfaceLocalSecretKeyMigrationResult(
    LocalSecretResultStatus Status,
    string MigrationId,
    AppSurfaceLocalSecretMigrationState State,
    string SourceStoredKey,
    string DestinationKey,
    AppSurfaceLocalSecretDiagnostic? Diagnostic,
    string Source)
{
    /// <summary>Creates a failed result without including secret data.</summary>
    /// <param name="status">The value-safe outcome status.</param>
    /// <param name="migrationId">The durable journal identifier when known, or a fixed sentinel when no identifier was available.</param>
    /// <param name="state">The migration state associated with the failure.</param>
    /// <param name="sourceStoredKey">The exact requested source identifier.</param>
    /// <param name="destinationKey">The logical destination key.</param>
    /// <param name="diagnostic">The value-safe failure diagnostic.</param>
    /// <param name="source">The backend or store name.</param>
    public static AppSurfaceLocalSecretKeyMigrationResult Failed(
        LocalSecretResultStatus status,
        string migrationId,
        AppSurfaceLocalSecretMigrationState state,
        string sourceStoredKey,
        AppSurfaceConfigKey destinationKey,
        AppSurfaceLocalSecretDiagnostic diagnostic,
        string source) => new(status, migrationId, state, sourceStoredKey, destinationKey.Value, diagnostic, source);
}

/// <summary>Durable journal data used by the exact-key migration state machine.</summary>
internal sealed record AppSurfaceLocalSecretMigrationJournal(
    string MigrationId,
    string ApplicationName,
    string Environment,
    string? KeyPrefix,
    string SourceStoredKey,
    string DestinationStoredKey,
    AppSurfaceLocalSecretMigrationState State);

/// <summary>Fault-injectable seam for lease, journal, exact I/O, and index ordering.</summary>
internal interface IAppSurfaceLocalSecretMigrationBackend
{
    /// <summary>True only when shared writer exclusion, durable journals and confirmed exact operations are implemented.</summary>
    bool SupportsDurableMigration => false;
    /// <summary>Excludes every package writer until disposed; timeout and cancellation must stop acquisition.</summary>
    IDisposable AcquireMaintenanceLease(TimeSpan timeout, CancellationToken cancellationToken);
    /// <summary>Reads durable metadata; malformed or unreadable state must throw, never appear absent.</summary>
    AppSurfaceLocalSecretMigrationJournal? ReadJournal();
    /// <summary>Atomically replaces and flushes metadata before acknowledging the transition.</summary>
    void CommitJournal(AppSurfaceLocalSecretMigrationJournal journal);
    /// <summary>Returns the current exact record; only confirmed absence returns null.</summary>
    string? ReadExact(string storedKey);
    /// <summary>Durably copies to an absent destination under the caller's lease; never remaps an identifier.</summary>
    void WriteExact(string storedKey, string value);
    /// <summary>Deletes the exact source after current equality proof; failures throw and remain resumable.</summary>
    void DeleteExact(string storedKey);
    /// <summary>Publishes destination membership before deletion and removes source membership after confirmed absence.</summary>
    void PublishIndex();
    /// <summary>Rejects an existing logical destination collision while retaining the exact source as a valid rename input.</summary>
    void ValidateDestination() { }
}

/// <summary>Signals a logical destination collision discovered before migration mutation.</summary>
internal sealed class AppSurfaceLocalSecretMigrationCollisionException : Exception;

/// <summary>Runs the roll-forward-only migration protocol.</summary>
internal static class AppSurfaceLocalSecretMigrationCoordinator
{
    internal static readonly TimeSpan DefaultLeaseTimeout = TimeSpan.FromSeconds(10);

    internal static AppSurfaceLocalSecretKeyMigrationResult Run(
        IAppSurfaceLocalSecretMigrationBackend backend,
        string applicationName,
        string environment,
        string? keyPrefix,
        string sourceStoredKey,
        string destinationStoredKey,
        AppSurfaceConfigKey destinationKey,
        string sourceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStoredKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationStoredKey);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.Equals(sourceStoredKey, destinationStoredKey, StringComparison.Ordinal))
        {
            return AppSurfaceLocalSecretKeyMigrationResult.Failed(
                LocalSecretResultStatus.ProviderFailed,
                "same-key",
                AppSurfaceLocalSecretMigrationState.Prepared,
                sourceStoredKey,
                destinationKey,
                new AppSurfaceLocalSecretDiagnostic(
                    "local-secret-migration-same-key",
                    "The migration source and destination are the same exact identifier.",
                    "Deleting the source would delete the destination too.",
                    "Choose a different strict destination key and retry.",
                    "local-secrets-migration"),
                sourceName);
        }

        AppSurfaceLocalSecretMigrationJournal? journal = null;
        try
        {
            if (!backend.SupportsDurableMigration)
            {
                return AppSurfaceLocalSecretKeyMigrationResult.Failed(
                    LocalSecretResultStatus.UnsupportedPlatform, "unsupported", AppSurfaceLocalSecretMigrationState.Prepared,
                    sourceStoredKey, destinationKey,
                    new AppSurfaceLocalSecretDiagnostic("local-secret-migration-unsupported",
                        "The backend cannot safely migrate exact identifiers.",
                        "Shared writer exclusion, durable journals and confirmed exact operations are required.",
                        "Use a backend that implements the complete migration contract.", "local-secrets-migration"), sourceName);
            }
            using var lease = backend.AcquireMaintenanceLease(DefaultLeaseTimeout, cancellationToken);
            journal = backend.ReadJournal();
            if (journal is not null)
            {
                if (!MatchesRequest(journal, applicationName, environment, keyPrefix, sourceStoredKey, destinationStoredKey))
                {
                    if (journal.State == AppSurfaceLocalSecretMigrationState.Complete)
                    {
                        journal = null;
                    }
                    else
                    {
                        return AppSurfaceLocalSecretKeyMigrationResult.Failed(
                            LocalSecretResultStatus.ProviderFailed,
                            journal.MigrationId,
                            AppSurfaceLocalSecretMigrationState.Unrecoverable,
                            sourceStoredKey,
                            destinationKey,
                            new AppSurfaceLocalSecretDiagnostic(
                                "local-secret-migration-journal-conflict",
                                "A different exact-key migration is recorded for this store.",
                                "Reusing its journal could delete an unrelated source identifier.",
                                "Complete or remove the recorded migration through the store recovery procedure before retrying.",
                                "local-secrets-migration"),
                            sourceName);
                    }
                }

                if (journal?.State == AppSurfaceLocalSecretMigrationState.Complete)
                {
                    return new(LocalSecretResultStatus.Found, journal.MigrationId, journal.State, sourceStoredKey, destinationKey.Value, null, sourceName);
                }

                if (journal is not null && (!Enum.IsDefined(journal.State) || journal.State == AppSurfaceLocalSecretMigrationState.Unrecoverable))
                {
                    return Failure(LocalSecretResultStatus.ProviderFailed, journal with { State = AppSurfaceLocalSecretMigrationState.Unrecoverable }, destinationKey,
                        "local-secret-migration-unrecoverable", "The exact-key migration requires operator recovery.",
                        "The durable journal recorded an unrecoverable state.",
                        "Inspect the exact source and destination records before retrying.", sourceName);
                }
            }
            if (journal is null)
            {
                journal = new AppSurfaceLocalSecretMigrationJournal(
                    Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                    applicationName, environment, keyPrefix, sourceStoredKey, destinationStoredKey,
                    AppSurfaceLocalSecretMigrationState.Prepared);
                backend.CommitJournal(journal);
            }

            string? source = null;
            if (journal.State is AppSurfaceLocalSecretMigrationState.Prepared or AppSurfaceLocalSecretMigrationState.DestinationWritten)
            {
                source = backend.ReadExact(sourceStoredKey);
                if (source is null)
                {
                    return Failure(LocalSecretResultStatus.ProviderFailed, journal with { State = AppSurfaceLocalSecretMigrationState.Unrecoverable }, destinationKey,
                        "local-secret-migration-source-missing", "The exact source identifier was not found.",
                        "The source disappeared before migration verification.", "Restore the source or choose another exact source identifier.", sourceName);
                }
            }

            if (journal.State == AppSurfaceLocalSecretMigrationState.Prepared)
            {
                backend.ValidateDestination();
                var destination = backend.ReadExact(destinationStoredKey);
                if (destination is null)
                {
                    backend.WriteExact(destinationStoredKey, source!);
                    journal = Commit(backend, journal, AppSurfaceLocalSecretMigrationState.DestinationWritten);
                }
                else if (!FixedEquals(source!, destination))
                {
                    return Failure(LocalSecretResultStatus.ProviderFailed, journal, destinationKey,
                        "local-secret-migration-destination-mismatch", "The destination already contains a different value.",
                        "Migration refuses to overwrite an existing destination.", "Resolve the destination conflict, then retry.", sourceName);
                }

                if (journal.State == AppSurfaceLocalSecretMigrationState.Prepared)
                {
                    journal = Commit(backend, journal, AppSurfaceLocalSecretMigrationState.DestinationWritten);
                }
            }

            if (journal.State == AppSurfaceLocalSecretMigrationState.DestinationWritten)
            {
                backend.ValidateDestination();
                var rereadDestination = backend.ReadExact(destinationStoredKey);
                var rereadSource = backend.ReadExact(sourceStoredKey);
                if (rereadDestination is null || rereadSource is null || !FixedEquals(source!, rereadDestination) || !FixedEquals(source!, rereadSource))
                {
                    return Failure(LocalSecretResultStatus.ProviderFailed, journal, destinationKey,
                        "local-secret-migration-verification-failed", "Migration verification failed.",
                        "A reread did not match the copied value or the source disappeared.",
                        "Do not delete the source; inspect both exact identifiers and retry.", sourceName);
                }

                journal = Commit(backend, journal, AppSurfaceLocalSecretMigrationState.DestinationVerified);
            }

            if (journal.State == AppSurfaceLocalSecretMigrationState.DestinationVerified)
            {
                backend.ValidateDestination();
                backend.PublishIndex();
                journal = Commit(backend, journal, AppSurfaceLocalSecretMigrationState.SourceDeletePending);
            }

            if (journal.State == AppSurfaceLocalSecretMigrationState.SourceDeletePending)
            {
                backend.ValidateDestination();
                var currentDestination = backend.ReadExact(destinationStoredKey);
                var currentSource = backend.ReadExact(sourceStoredKey);
                if (currentDestination is null)
                {
                    return Failure(LocalSecretResultStatus.ProviderFailed, journal, destinationKey,
                        "local-secret-migration-destination-missing", "The verified destination is no longer present.",
                        "The source is retained because deletion cannot be made safe.",
                        "Restore the exact destination and retry the migration.", sourceName);
                }

                if (currentSource is not null && !FixedEquals(currentSource, currentDestination))
                {
                    return Failure(LocalSecretResultStatus.ProviderFailed, journal, destinationKey,
                        "local-secret-migration-source-destination-changed", "The source and destination changed before deletion.",
                        "The current exact records no longer match the value verified by the migration.",
                        "Resolve both exact records and retry the migration.", sourceName);
                }

                // Absence after durable verification is a successful prior delete, including a crash before Complete.
                if (currentSource is not null)
                {
                    backend.DeleteExact(sourceStoredKey);
                }

                if (backend.ReadExact(sourceStoredKey) is not null)
                {
                    return Failure(LocalSecretResultStatus.ProviderFailed, journal, destinationKey,
                        "local-secret-migration-source-delete-unconfirmed", "Source deletion could not be confirmed.",
                        "The source remains readable after the delete attempt.",
                        "Keep the source and inspect the backend before retrying.", sourceName);
                }

                backend.PublishIndex();
                journal = Commit(backend, journal, AppSurfaceLocalSecretMigrationState.Complete);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AppSurfaceLocalSecretMigrationCollisionException)
        {
            journal ??= new AppSurfaceLocalSecretMigrationJournal(
                "unavailable", applicationName, environment, keyPrefix, sourceStoredKey, destinationStoredKey,
                AppSurfaceLocalSecretMigrationState.Prepared);
            return Failure(LocalSecretResultStatus.ProviderFailed, journal, destinationKey,
                "config-key-collision", "The migration destination collides with another stored logical key.",
                "A case-variant destination already exists in the same LocalSecrets namespace.",
                "Remove or explicitly reconcile the existing case variant, then retry the same migration.", sourceName);
        }
        catch (Exception)
        {
            journal ??= new AppSurfaceLocalSecretMigrationJournal(
                "unavailable", applicationName, environment, keyPrefix, sourceStoredKey, destinationStoredKey,
                AppSurfaceLocalSecretMigrationState.Prepared);
            return Failure(LocalSecretResultStatus.Unavailable, journal, destinationKey,
                "local-secret-migration-io-failure", "The exact-key migration stopped before its next durable transition.",
                "The backend operation failed; the durable journal state identifies the safe resume point.",
                "Retry with the same exact source and destination identifiers.", sourceName);
        }
        return new(LocalSecretResultStatus.Found, journal.MigrationId, journal.State, sourceStoredKey, destinationKey.Value, null, sourceName);
    }

    /// <summary>Advances the reported state only after the backend acknowledges the durable commit.</summary>
    private static AppSurfaceLocalSecretMigrationJournal Commit(IAppSurfaceLocalSecretMigrationBackend backend,
        AppSurfaceLocalSecretMigrationJournal journal, AppSurfaceLocalSecretMigrationState state)
    {
        var next = journal with { State = state };
        backend.CommitJournal(next);
        return next;
    }

    private static bool MatchesRequest(
        AppSurfaceLocalSecretMigrationJournal journal,
        string applicationName,
        string environment,
        string? keyPrefix,
        string sourceStoredKey,
        string destinationStoredKey) =>
        string.Equals(journal.ApplicationName, applicationName, StringComparison.Ordinal) &&
        string.Equals(journal.Environment, environment, StringComparison.Ordinal) &&
        string.Equals(journal.KeyPrefix, keyPrefix, StringComparison.Ordinal) &&
        string.Equals(journal.SourceStoredKey, sourceStoredKey, StringComparison.Ordinal) &&
        string.Equals(journal.DestinationStoredKey, destinationStoredKey, StringComparison.Ordinal);

    private static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private static AppSurfaceLocalSecretKeyMigrationResult Failure(
        LocalSecretResultStatus status,
        AppSurfaceLocalSecretMigrationJournal journal,
        AppSurfaceConfigKey destinationKey,
        string code,
        string problem,
        string cause,
        string fix,
        string sourceName) =>
        AppSurfaceLocalSecretKeyMigrationResult.Failed(
            status, journal.MigrationId, journal.State, journal.SourceStoredKey, destinationKey,
            new AppSurfaceLocalSecretDiagnostic(code, problem, cause, fix, "local-secrets-migration"), sourceName);
}
