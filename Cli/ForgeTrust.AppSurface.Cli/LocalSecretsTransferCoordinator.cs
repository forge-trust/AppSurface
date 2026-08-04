using System.Security.Cryptography;
using System.Text.Json;
using ForgeTrust.AppSurface.Config.LocalSecrets;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Coordinates AppSurface-owned remote-to-local transfers without persisting secret values.
/// </summary>
/// <remarks>
/// The coordinator is deliberately internal to the CLI. Local secret stores do not expose cross-store transactions, so
/// this type serializes only cooperating AppSurface commands and records a value-free <c>prepared</c>/<c>committed</c>
/// attestation. Direct platform-store edits remain outside that guarantee.
/// </remarks>
internal sealed class LocalSecretsTransferCoordinator
{
    private const int JournalVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _stateRoot;
    private readonly LocalSecretsTransferCoordinatorTestHooks? _testHooks;

    /// <summary>Initializes a coordinator using the default per-user AppSurface state root.</summary>
    public LocalSecretsTransferCoordinator()
        : this(GetDefaultStateRoot())
    {
    }

    /// <summary>Initializes a coordinator with an explicit state root for deterministic tests.</summary>
    internal LocalSecretsTransferCoordinator(string stateRoot)
        : this(stateRoot, null)
    {
    }

    /// <summary>Initializes a coordinator with deterministic failure hooks for tests.</summary>
    internal LocalSecretsTransferCoordinator(string stateRoot, LocalSecretsTransferCoordinatorTestHooks? testHooks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        _stateRoot = Path.GetFullPath(stateRoot);
        _testHooks = testHooks;
    }

    /// <summary>Gets whether the CLI can coordinate the supplied built-in store in v2.</summary>
    internal static bool Supports(IAppSurfaceLocalSecretStore store) =>
        store is InMemoryAppSurfaceLocalSecretStore
            or FileAppSurfaceLocalSecretStore
            or PlatformAppSurfaceLocalSecretStore;

    /// <summary>Captures the value-free local destination precondition for a v2 plan row.</summary>
    internal LocalCoordinatorPrecondition CapturePrecondition(
        AppSurfaceLocalSecretIdentity identity,
        IAppSurfaceLocalSecretStore store,
        bool replace)
    {
        if (!Supports(store))
        {
            return LocalCoordinatorPrecondition.Unsupported();
        }

        if (!TryAcquire(identity, out var lease, out var failure))
        {
            return LocalCoordinatorPrecondition.Failed(failure!);
        }

        using (lease)
        {
            if (!TryEnsureStoreReady(store, identity, out failure))
            {
                return LocalCoordinatorPrecondition.Failed(failure!);
            }

            if (!TryReadJournal(lease!, out var journal, out failure))
            {
                return LocalCoordinatorPrecondition.Failed(failure!);
            }

            var probe = Probe(store, identity);
            if (probe.Status == LocalSecretResultStatus.Missing)
            {
                return journal is null
                    ? LocalCoordinatorPrecondition.Missing()
                    : LocalCoordinatorPrecondition.Conflict(
                        new LocalCoordinatorFailure(
                            "local-secret-transfer-attestation-stale",
                            "The local transfer attestation does not match the current missing target.",
                            false));
            }

            if (probe.Status != LocalSecretResultStatus.Found)
            {
                return LocalCoordinatorPrecondition.Failed(FromStoreFailure(probe, "local-secret-transfer-probe-failed"));
            }

            if (!replace)
            {
                return LocalCoordinatorPrecondition.Conflict(
                    new LocalCoordinatorFailure(
                        "local-secret-transfer-destination-exists",
                        "The local target already exists. Create a new plan with --replace and confirm the exact job name, or delete the local clone explicitly.",
                        false));
            }

            if (journal is null || journal.State != LocalTransferJournalState.Committed)
            {
                return LocalCoordinatorPrecondition.Conflict(
                    new LocalCoordinatorFailure(
                        "local-secret-transfer-unattested-destination",
                        "The local target is not a committed AppSurface transfer. Delete it explicitly before creating a guarded local transfer plan.",
                        false));
            }

            return LocalCoordinatorPrecondition.Replace(journal.OperationId);
        }
    }

    /// <summary>Rechecks a plan-bound local precondition before source payload access.</summary>
    internal LocalCoordinatorCheck Recheck(
        SecretPromotionPlanArtifact plan,
        SecretPromotionPlanRow row,
        AppSurfaceLocalSecretIdentity identity,
        IAppSurfaceLocalSecretStore store,
        bool allowPreparedRecovery)
    {
        if (!Supports(store))
        {
            return LocalCoordinatorCheck.Unsupported();
        }

        if (!TryAcquire(identity, out var lease, out var failure))
        {
            return LocalCoordinatorCheck.Failed(failure!);
        }

        using (lease)
        {
            if (!TryEnsureStoreReady(store, identity, out failure))
            {
                return LocalCoordinatorCheck.Failed(failure!);
            }

            if (!TryReadJournal(lease!, out var journal, out failure))
            {
                return LocalCoordinatorCheck.Failed(failure!);
            }

            var probe = Probe(store, identity);
            if (probe.Status is not LocalSecretResultStatus.Found and not LocalSecretResultStatus.Missing)
            {
                return LocalCoordinatorCheck.Failed(FromStoreFailure(probe, "local-secret-transfer-probe-failed"));
            }

            if (journal?.State == LocalTransferJournalState.Prepared)
            {
                return allowPreparedRecovery && JournalMatchesPlan(journal, plan, row)
                    ? probe.Status == LocalSecretResultStatus.Found
                        ? LocalCoordinatorCheck.PreparedRecovery()
                        : LocalCoordinatorCheck.Conflict()
                    : LocalCoordinatorCheck.Indeterminate();
            }

            if (row.DestinationExists == true)
            {
                if (probe.Status != LocalSecretResultStatus.Found ||
                    journal?.State != LocalTransferJournalState.Committed ||
                    !JournalMatchesPlanPrecondition(journal, row))
                {
                    return LocalCoordinatorCheck.Conflict();
                }

                return LocalCoordinatorCheck.Ready();
            }

            return probe.Status == LocalSecretResultStatus.Missing && journal is null
                ? LocalCoordinatorCheck.Ready()
                : LocalCoordinatorCheck.Conflict();
        }
    }

    /// <summary>Writes a local value after rechecking its plan-bound precondition under the coordinator lock.</summary>
    internal LocalCoordinatorWriteResult WriteOrRecover(
        SecretPromotionPlanArtifact plan,
        SecretPromotionPlanRow row,
        AppSurfaceLocalSecretIdentity identity,
        IAppSurfaceLocalSecretStore store,
        string value,
        bool allowPreparedRecovery)
    {
        if (!Supports(store))
        {
            return LocalCoordinatorWriteResult.Unsupported();
        }

        if (!TryAcquire(identity, out var lease, out var failure))
        {
            return LocalCoordinatorWriteResult.Failed(failure!);
        }

        using (lease)
        {
            if (!TryEnsureStoreReady(store, identity, out failure))
            {
                return LocalCoordinatorWriteResult.Failed(failure!);
            }

            if (!TryReadJournal(lease!, out var journal, out failure))
            {
                return LocalCoordinatorWriteResult.Failed(failure!);
            }

            if (journal?.State == LocalTransferJournalState.Prepared)
            {
                if (!allowPreparedRecovery || !JournalMatchesPlan(journal, plan, row))
                {
                    return LocalCoordinatorWriteResult.Indeterminate();
                }

                var local = Get(store, identity);
                if (local.Status != LocalSecretResultStatus.Found)
                {
                    return local.Status == LocalSecretResultStatus.Missing
                        ? LocalCoordinatorWriteResult.Conflict()
                        : LocalCoordinatorWriteResult.Failed(FromStoreFailure(local, "local-secret-transfer-recovery-read-failed"));
                }

                if (!CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(local.Value ?? string.Empty),
                        System.Text.Encoding.UTF8.GetBytes(value)))
                {
                    return LocalCoordinatorWriteResult.Conflict();
                }

                if (!TryWriteJournal(lease!, journal with { State = LocalTransferJournalState.Committed }, out failure))
                {
                    return LocalCoordinatorWriteResult.Indeterminate();
                }

                return LocalCoordinatorWriteResult.Recovered();
            }

            if (!MatchesCurrentPrecondition(row, identity, store, journal, out failure))
            {
                return failure is null
                    ? LocalCoordinatorWriteResult.Conflict()
                    : LocalCoordinatorWriteResult.Failed(failure);
            }

            var prepared = new LocalTransferJournal(
                JournalVersion,
                LocalTransferJournalState.Prepared,
                CreateOperationId(),
                plan.PlanIdentity,
                row.SourceResource!,
                identity.StorageName,
                journal?.OperationId);
            if (!TryWriteJournal(lease!, prepared, out failure))
            {
                return LocalCoordinatorWriteResult.Failed(failure!);
            }

            var write = Set(store, identity, value);
            if (write.Status != LocalSecretResultStatus.Found)
            {
                return LocalCoordinatorWriteResult.Indeterminate(FromStoreFailure(write, "local-secret-transfer-write-indeterminate"));
            }

            if (!TryWriteJournal(lease!, prepared with { State = LocalTransferJournalState.Committed }, out failure))
            {
                return LocalCoordinatorWriteResult.Indeterminate(failure);
            }

            return row.DestinationExists == true
                ? LocalCoordinatorWriteResult.Replaced()
                : LocalCoordinatorWriteResult.Created();
        }
    }

    /// <summary>Confirms that a receipt's local row still has matching coordinator evidence.</summary>
    internal LocalCoordinatorCheck VerifyCommitted(
        SecretPromotionPlanArtifact plan,
        SecretPromotionPlanRow row,
        AppSurfaceLocalSecretIdentity identity,
        IAppSurfaceLocalSecretStore store)
    {
        if (!Supports(store))
        {
            return LocalCoordinatorCheck.Unsupported();
        }

        if (!TryAcquire(identity, out var lease, out var failure))
        {
            return LocalCoordinatorCheck.Failed(failure!);
        }

        using (lease)
        {
            if (!TryReadJournal(lease!, out var journal, out failure))
            {
                return LocalCoordinatorCheck.Failed(failure!);
            }

            var probe = Probe(store, identity);
            if (probe.Status != LocalSecretResultStatus.Found)
            {
                return probe.Status == LocalSecretResultStatus.Missing
                    ? LocalCoordinatorCheck.Conflict()
                    : LocalCoordinatorCheck.Failed(FromStoreFailure(probe, "local-secret-transfer-resume-probe-failed"));
            }

            return journal?.State == LocalTransferJournalState.Committed && JournalMatchesPlan(journal, plan, row)
                ? LocalCoordinatorCheck.Ready()
                : LocalCoordinatorCheck.Conflict();
        }
    }

    /// <summary>Invalidates any committed transfer attestation before a normal local CLI mutation.</summary>
    internal AppSurfaceLocalSecretResult InvalidateBeforeMutation(
        IAppSurfaceLocalSecretStore store,
        AppSurfaceLocalSecretIdentity identity,
        Func<AppSurfaceLocalSecretResult> mutation)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(mutation);

        if (!Supports(store))
        {
            return mutation();
        }

        if (!TryAcquire(identity, out var lease, out var failure))
        {
            return ToStoreFailure(store, failure!);
        }

        using (lease)
        {
            if (!TryReadJournal(lease!, out var journal, out failure))
            {
                return ToStoreFailure(store, failure!);
            }

            if (journal is not null)
            {
                try
                {
                    _testHooks?.BeforeDeleteJournal?.Invoke(lease!.JournalPath);
                    File.Delete(lease!.JournalPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return ToStoreFailure(
                        store,
                        new LocalCoordinatorFailure(
                            "local-secret-transfer-attestation-clear-failed",
                            "The local transfer attestation could not be cleared before the local mutation.",
                            true));
                }
            }

            return mutation();
        }
    }

    private bool MatchesCurrentPrecondition(
        SecretPromotionPlanRow row,
        AppSurfaceLocalSecretIdentity identity,
        IAppSurfaceLocalSecretStore store,
        LocalTransferJournal? journal,
        out LocalCoordinatorFailure? failure)
    {
        failure = null;
        var probe = Probe(store, identity);
        if (probe.Status is not LocalSecretResultStatus.Found and not LocalSecretResultStatus.Missing)
        {
            failure = FromStoreFailure(probe, "local-secret-transfer-probe-failed");
            return false;
        }

        if (row.DestinationExists == true)
        {
            return probe.Status == LocalSecretResultStatus.Found &&
                   journal?.State == LocalTransferJournalState.Committed &&
                   JournalMatchesPlanPrecondition(journal, row);
        }

        return probe.Status == LocalSecretResultStatus.Missing && journal is null;
    }

    private static bool JournalMatchesPlan(LocalTransferJournal journal, SecretPromotionPlanArtifact plan, SecretPromotionPlanRow row) =>
        string.Equals(journal.PlanIdentity, plan.PlanIdentity, StringComparison.Ordinal) &&
        string.Equals(journal.SourceVersionResource, row.SourceResource, StringComparison.Ordinal) &&
        string.Equals(journal.LocalStorageName, row.LocalStorageName, StringComparison.Ordinal);

    private static bool JournalMatchesPlanPrecondition(LocalTransferJournal journal, SecretPromotionPlanRow row) =>
        string.Equals(journal.OperationId, row.LocalAttestationOperationId, StringComparison.Ordinal) &&
        string.Equals(journal.LocalStorageName, row.LocalStorageName, StringComparison.Ordinal);

    private AppSurfaceLocalSecretResult Probe(IAppSurfaceLocalSecretStore store, AppSurfaceLocalSecretIdentity identity) =>
        _testHooks?.StoreProbe?.Invoke(store, identity) ??
        (store is IAppSurfaceLocalSecretMetadataStore metadataStore
            ? metadataStore.Probe(identity)
            : AppSurfaceLocalSecretResult.NotFound(
                LocalSecretResultStatus.ProviderFailed,
                new AppSurfaceLocalSecretDiagnostic(
                    "local-secret-transfer-metadata-unsupported",
                    "Local transfer metadata probes are unavailable.",
                    "The selected local secret store cannot prove target presence without reading a value.",
                    "Use a built-in LocalSecrets store for remote-to-local transfer.",
                    "local-secrets-without-a-remote-vault"),
                store.Name));

    private AppSurfaceLocalSecretResult Get(IAppSurfaceLocalSecretStore store, AppSurfaceLocalSecretIdentity identity)
    {
        try
        {
            return _testHooks?.StoreGet?.Invoke(store, identity) ?? store.Get(identity);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return AppSurfaceLocalSecretResult.NotFound(
                LocalSecretResultStatus.ProviderFailed,
                new AppSurfaceLocalSecretDiagnostic(
                    "local-secret-transfer-recovery-read-failed",
                    "The local target could not be read for recovery.",
                    $"The local secret store threw {exception.GetType().Name} while recovery was proving a prepared transfer.",
                    "Reconcile the local clone explicitly; do not retry the transfer write.",
                    "local-secrets-without-a-remote-vault",
                    retryable: true),
                store.Name);
        }
    }

    private AppSurfaceLocalSecretResult Set(IAppSurfaceLocalSecretStore store, AppSurfaceLocalSecretIdentity identity, string value)
    {
        try
        {
            return _testHooks?.StoreSet?.Invoke(store, identity, value) ?? store.Set(identity, value);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return AppSurfaceLocalSecretResult.NotFound(
                LocalSecretResultStatus.ProviderFailed,
                new AppSurfaceLocalSecretDiagnostic(
                    "local-secret-transfer-write-indeterminate",
                    "The local transfer write did not return a confirmed result.",
                    $"The local secret store threw {exception.GetType().Name} after transfer preparation.",
                    "Do not retry the transfer write. Resume only to reconcile the prepared local transfer.",
                    "local-secrets-without-a-remote-vault",
                    retryable: false),
                store.Name);
        }
    }

    private static LocalCoordinatorFailure FromStoreFailure(AppSurfaceLocalSecretResult result, string fallbackCode) =>
        new(
            result.Diagnostic?.Code ?? fallbackCode,
            result.Diagnostic?.Problem ?? "The LocalSecrets store could not complete the transfer operation.",
            result.Diagnostic?.Retryable ?? false);

    private static AppSurfaceLocalSecretResult ToStoreFailure(IAppSurfaceLocalSecretStore store, LocalCoordinatorFailure failure) =>
        AppSurfaceLocalSecretResult.NotFound(
            LocalSecretResultStatus.ProviderFailed,
            new AppSurfaceLocalSecretDiagnostic(
                failure.Code,
                failure.Problem,
                "The AppSurface local transfer coordinator could not safely complete the requested operation.",
                "Resolve the local transfer coordinator state before changing this local secret.",
                "local-secrets-without-a-remote-vault",
                failure.Retryable),
            store.Name);

    private bool TryEnsureStoreReady(IAppSurfaceLocalSecretStore store, AppSurfaceLocalSecretIdentity identity, out LocalCoordinatorFailure? failure)
    {
        var doctor = _testHooks?.StoreDoctor?.Invoke(store, identity) ??
            store.Doctor(identity.ApplicationName, identity.Environment, identity.KeyPrefix);
        if (doctor.Status is LocalSecretResultStatus.Found or LocalSecretResultStatus.Missing)
        {
            failure = null;
            return true;
        }

        failure = FromStoreFailure(doctor, "local-secret-transfer-store-unavailable");
        return false;
    }

    private bool TryAcquire(AppSurfaceLocalSecretIdentity identity, out LocalCoordinatorLease? lease, out LocalCoordinatorFailure? failure)
    {
        lease = null;
        if (!TryEnsureStateRoot(out failure))
        {
            return false;
        }

        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity.StorageName))).ToLowerInvariant();
        var lockPath = Path.Join(_stateRoot, $"{hash}.lock");
        var journalPath = Path.Join(_stateRoot, $"{hash}.json");
        FileStream stream;
        try
        {
            _testHooks?.BeforeAcquireLock?.Invoke(lockPath);
            stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            failure = new LocalCoordinatorFailure(
                "local-secret-transfer-locked",
                "Another AppSurface transfer is using this local target.",
                true);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            failure = new LocalCoordinatorFailure(
                "local-secret-transfer-state-root-unavailable",
                "The AppSurface transfer state root cannot be opened for the current user.",
                false);
            return false;
        }

        try
        {
            _testHooks?.BeforeSecureLockFile?.Invoke(lockPath);
            TrySetPrivateFileMode(lockPath);
            lease = new LocalCoordinatorLease(stream, journalPath, _stateRoot);
            failure = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            stream.Dispose();
            failure = new LocalCoordinatorFailure(
                "local-secret-transfer-state-root-unavailable",
                "The AppSurface transfer lock file could not be secured for the current user.",
                false);
            return false;
        }
    }

    private bool TryEnsureStateRoot(out LocalCoordinatorFailure? failure)
    {
        try
        {
            _testHooks?.BeforeEnsureStateRoot?.Invoke(_stateRoot);
            var existed = Directory.Exists(_stateRoot);
            Directory.CreateDirectory(_stateRoot);
            var attributes = File.GetAttributes(_stateRoot);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                failure = new LocalCoordinatorFailure(
                    "local-secret-transfer-state-root-unsafe",
                    "The AppSurface transfer state root must not be a symbolic link or reparse point.",
                    false);
                return false;
            }

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(_stateRoot);
                var privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                {
                    if (!existed)
                    {
                        File.SetUnixFileMode(_stateRoot, privateMode);
                        mode = File.GetUnixFileMode(_stateRoot);
                    }

                    if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0)
                    {
                        failure = null;
                        return true;
                    }

                    failure = new LocalCoordinatorFailure(
                        "local-secret-transfer-state-root-unsafe",
                        "The AppSurface transfer state root is accessible by group or other users.",
                        false);
                    return false;
                }

                if (mode != privateMode)
                {
                    File.SetUnixFileMode(_stateRoot, privateMode);
                }
            }

            failure = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            failure = new LocalCoordinatorFailure(
                "local-secret-transfer-state-root-unavailable",
                "The AppSurface transfer state root could not be created or verified.",
                false);
            return false;
        }
    }

    private bool TryReadJournal(LocalCoordinatorLease lease, out LocalTransferJournal? journal, out LocalCoordinatorFailure? failure)
    {
        journal = null;
        if (!File.Exists(lease.JournalPath))
        {
            failure = null;
            return true;
        }

        try
        {
            _testHooks?.BeforeReadJournal?.Invoke(lease.JournalPath);
            if ((File.GetAttributes(lease.JournalPath) & FileAttributes.ReparsePoint) != 0)
            {
                failure = new LocalCoordinatorFailure(
                    "local-secret-transfer-journal-unsafe",
                    "The local transfer journal must not be a symbolic link or reparse point.",
                    false);
                return false;
            }

            journal = JsonSerializer.Deserialize<LocalTransferJournal>(File.ReadAllBytes(lease.JournalPath), JsonOptions);
            if (!IsValidJournal(journal))
            {
                failure = new LocalCoordinatorFailure(
                    "local-secret-transfer-journal-corrupt",
                    "The local transfer journal is corrupt or has an unsupported shape.",
                    false);
                return false;
            }

            failure = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            failure = new LocalCoordinatorFailure(
                "local-secret-transfer-journal-corrupt",
                "The local transfer journal could not be read safely.",
                false);
            return false;
        }
    }

    private bool TryWriteJournal(LocalCoordinatorLease lease, LocalTransferJournal journal, out LocalCoordinatorFailure? failure)
    {
        var temporaryPath = Path.Join(lease.StateRoot, $".{Path.GetFileNameWithoutExtension(lease.JournalPath)}-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(journal, JsonOptions));
            _testHooks?.AfterWriteTemporaryJournal?.Invoke(temporaryPath);
            _testHooks?.BeforeSecureTemporaryJournal?.Invoke(temporaryPath);
            TrySetPrivateFileMode(temporaryPath);
            File.Move(temporaryPath, lease.JournalPath, overwrite: true);
            failure = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            try
            {
                _testHooks?.BeforeDeleteTemporaryJournal?.Invoke(temporaryPath);
                File.Delete(temporaryPath);
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                // The journal remains authoritative when cleanup cannot remove an uncommitted temporary file.
            }

            failure = new LocalCoordinatorFailure(
                "local-secret-transfer-journal-write-failed",
                "The local transfer journal could not be updated safely.",
                false);
            return false;
        }
    }

    private static bool IsValidJournal(LocalTransferJournal? journal) =>
        journal is not null &&
        journal.Version == JournalVersion &&
        journal.State is LocalTransferJournalState.Prepared or LocalTransferJournalState.Committed &&
        LocalTransferFormat.IsLowerHex(journal.OperationId, 32) &&
        LocalTransferFormat.IsLowerHex(journal.PlanIdentity, 64) &&
        !string.IsNullOrWhiteSpace(journal.SourceVersionResource) &&
        !string.IsNullOrWhiteSpace(journal.LocalStorageName) &&
        (journal.PreviousOperationId is null || LocalTransferFormat.IsLowerHex(journal.PreviousOperationId, 32));

    private static string CreateOperationId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static string GetDefaultStateRoot() =>
        GetDefaultStateRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Builds the default per-user transfer state directory from platform folder values.</summary>
    /// <param name="localApplicationData">Platform local-application-data root, if available.</param>
    /// <param name="userProfile">Platform user-profile root used when local application data is unavailable.</param>
    /// <returns>The absolute AppSurface secret-transfer state directory.</returns>
    internal static string GetDefaultStateRoot(string? localApplicationData, string userProfile)
    {
        var root = localApplicationData;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Join(userProfile, ".appsurface");
        }

        return Path.Join(root, "AppSurface", "secret-transfer");
    }

    private static void TrySetPrivateFileMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private sealed class LocalCoordinatorLease(FileStream stream, string journalPath, string stateRoot) : IDisposable
    {
        public string JournalPath { get; } = journalPath;

        public string StateRoot { get; } = stateRoot;

        public void Dispose() => stream.Dispose();
    }
}

/// <summary>Validates fixed-length lowercase hexadecimal transfer identities.</summary>
internal static class LocalTransferFormat
{
    /// <summary>Gets whether a value is lowercase hexadecimal with the required length.</summary>
    /// <param name="value">Candidate value to validate.</param>
    /// <param name="length">Required character length.</param>
    /// <returns><see langword="true"/> when the value has the required lowercase hexadecimal shape.</returns>
    internal static bool IsLowerHex(string? value, int length) =>
        value?.Length == length && value.All(static character => character is >= '0' and <= '9' || character is >= 'a' and <= 'f');
}

/// <summary>Provides deterministic coordinator failure injection for internal tests.</summary>
internal sealed class LocalSecretsTransferCoordinatorTestHooks
{
    /// <summary>Runs immediately before the coordinator verifies or creates its state directory.</summary>
    public Action<string>? BeforeEnsureStateRoot { get; init; }

    /// <summary>Runs immediately before the coordinator opens a per-secret transfer lock.</summary>
    public Action<string>? BeforeAcquireLock { get; init; }

    /// <summary>Runs after the coordinator opens a lock file and before it restricts the file to the current user.</summary>
    public Action<string>? BeforeSecureLockFile { get; init; }

    /// <summary>Runs immediately before the coordinator reads an existing transfer journal.</summary>
    public Action<string>? BeforeReadJournal { get; init; }

    /// <summary>Runs after a temporary journal has been written and before it is secured and committed.</summary>
    public Action<string>? AfterWriteTemporaryJournal { get; init; }

    /// <summary>Runs immediately before the coordinator restricts a temporary journal to the current user.</summary>
    public Action<string>? BeforeSecureTemporaryJournal { get; init; }

    /// <summary>Runs immediately before cleanup deletes an uncommitted temporary journal.</summary>
    public Action<string>? BeforeDeleteTemporaryJournal { get; init; }

    /// <summary>Runs immediately before an attestation journal is removed for an ordinary mutation.</summary>
    public Action<string>? BeforeDeleteJournal { get; init; }

    /// <summary>Supplies the LocalSecrets doctor result instead of calling the store.</summary>
    public Func<IAppSurfaceLocalSecretStore, AppSurfaceLocalSecretIdentity, AppSurfaceLocalSecretResult>? StoreDoctor { get; init; }

    /// <summary>Supplies the LocalSecrets metadata probe result instead of calling the store.</summary>
    public Func<IAppSurfaceLocalSecretStore, AppSurfaceLocalSecretIdentity, AppSurfaceLocalSecretResult>? StoreProbe { get; init; }

    /// <summary>Supplies the LocalSecrets recovery read result instead of calling the store.</summary>
    public Func<IAppSurfaceLocalSecretStore, AppSurfaceLocalSecretIdentity, AppSurfaceLocalSecretResult>? StoreGet { get; init; }

    /// <summary>Supplies the LocalSecrets write result instead of calling the store.</summary>
    public Func<IAppSurfaceLocalSecretStore, AppSurfaceLocalSecretIdentity, string, AppSurfaceLocalSecretResult>? StoreSet { get; init; }
}

/// <summary>Describes the captured local precondition without exposing local values.</summary>
internal sealed record LocalCoordinatorPrecondition(
    LocalCoordinatorPreconditionKind Kind,
    string? PreviousOperationId,
    LocalCoordinatorFailure? Failure)
{
    /// <summary>Creates a precondition for a local target that is absent.</summary>
    /// <returns>A missing-target precondition with no attestation or failure.</returns>
    public static LocalCoordinatorPrecondition Missing() => new(LocalCoordinatorPreconditionKind.Missing, null, null);

    /// <summary>Creates a precondition that permits a guarded replacement.</summary>
    /// <param name="previousOperationId">Committed attestation operation identifier that authorizes replacement.</param>
    /// <returns>A replacement precondition bound to the supplied prior operation.</returns>
    public static LocalCoordinatorPrecondition Replace(string previousOperationId) => new(LocalCoordinatorPreconditionKind.Replace, previousOperationId, null);

    /// <summary>Creates a precondition that blocks transfer because the local target conflicts with the plan.</summary>
    /// <param name="failure">Value-safe explanation of the conflict.</param>
    /// <returns>A conflict precondition carrying the supplied failure.</returns>
    public static LocalCoordinatorPrecondition Conflict(LocalCoordinatorFailure failure) => new(LocalCoordinatorPreconditionKind.Conflict, null, failure);

    /// <summary>Creates a precondition for a local store that cannot participate in coordinated transfer.</summary>
    /// <returns>An unsupported-store precondition with its stable failure diagnostic.</returns>
    public static LocalCoordinatorPrecondition Unsupported() => new(
        LocalCoordinatorPreconditionKind.Unsupported,
        null,
        new LocalCoordinatorFailure("local-secret-transfer-unsupported-store", "The selected LocalSecrets store is not supported for coordinated remote-to-local transfer.", false));

    /// <summary>Creates a precondition that failed before a safe local state could be established.</summary>
    /// <param name="failure">Value-safe failure that prevented precondition capture.</param>
    /// <returns>A failed precondition carrying the supplied failure.</returns>
    public static LocalCoordinatorPrecondition Failed(LocalCoordinatorFailure failure) => new(LocalCoordinatorPreconditionKind.Failed, null, failure);
}

/// <summary>Classifies the value-free precondition captured for a local transfer target.</summary>
internal enum LocalCoordinatorPreconditionKind
{
    /// <summary>The target is absent and may be created.</summary>
    Missing,
    /// <summary>The target has a matching committed attestation and may be replaced with explicit authorization.</summary>
    Replace,
    /// <summary>The target or attestation conflicts with the planned transfer.</summary>
    Conflict,
    /// <summary>The selected local store cannot participate in coordinated transfer.</summary>
    Unsupported,
    /// <summary>The coordinator could not safely capture a local precondition.</summary>
    Failed
}

/// <summary>Describes a local destination check without exposing local values.</summary>
internal sealed record LocalCoordinatorCheck(LocalCoordinatorCheckKind Kind, LocalCoordinatorFailure? Failure)
{
    /// <summary>Creates a check result that permits the planned operation.</summary>
    /// <returns>A ready result without a failure.</returns>
    public static LocalCoordinatorCheck Ready() => new(LocalCoordinatorCheckKind.Ready, null);

    /// <summary>Creates a check result that requires safe reconciliation of a prepared transfer.</summary>
    /// <returns>A prepared-recovery result without a failure.</returns>
    public static LocalCoordinatorCheck PreparedRecovery() => new(LocalCoordinatorCheckKind.PreparedRecovery, null);

    /// <summary>Creates a check result that blocks the operation because current state conflicts with the plan.</summary>
    /// <returns>A conflict result without a failure because the plan mismatch is the diagnostic.</returns>
    public static LocalCoordinatorCheck Conflict() => new(LocalCoordinatorCheckKind.Conflict, null);

    /// <summary>Creates a check result whose prior write state cannot be safely determined.</summary>
    /// <returns>An indeterminate result without a failure because reconciliation is required.</returns>
    public static LocalCoordinatorCheck Indeterminate() => new(LocalCoordinatorCheckKind.Indeterminate, null);

    /// <summary>Creates a check result for a local store that cannot participate in coordinated transfer.</summary>
    /// <returns>An unsupported-store result with its stable failure diagnostic.</returns>
    public static LocalCoordinatorCheck Unsupported() => new(
        LocalCoordinatorCheckKind.Unsupported,
        new LocalCoordinatorFailure("local-secret-transfer-unsupported-store", "The selected LocalSecrets store is not supported for coordinated remote-to-local transfer.", false));

    /// <summary>Creates a check result that failed before the local state could be safely rechecked.</summary>
    /// <param name="failure">Value-safe failure that prevented the recheck.</param>
    /// <returns>A failed result carrying the supplied failure.</returns>
    public static LocalCoordinatorCheck Failed(LocalCoordinatorFailure failure) => new(LocalCoordinatorCheckKind.Failed, failure);
}

/// <summary>Classifies a value-free recheck of a plan-bound local destination.</summary>
internal enum LocalCoordinatorCheckKind
{
    /// <summary>The local destination still satisfies the plan precondition.</summary>
    Ready,
    /// <summary>A matching prepared transfer may be reconciled only through the resume workflow.</summary>
    PreparedRecovery,
    /// <summary>The local destination no longer satisfies the plan precondition.</summary>
    Conflict,
    /// <summary>The coordinator cannot establish whether a prior write completed safely.</summary>
    Indeterminate,
    /// <summary>The selected local store cannot participate in coordinated transfer.</summary>
    Unsupported,
    /// <summary>The coordinator could not safely recheck local state.</summary>
    Failed
}

/// <summary>Describes a guarded local write or recovery outcome.</summary>
internal sealed record LocalCoordinatorWriteResult(LocalCoordinatorWriteKind Kind, LocalCoordinatorFailure? Failure)
{
    /// <summary>Creates a successful create outcome.</summary>
    /// <returns>A created result without a failure.</returns>
    public static LocalCoordinatorWriteResult Created() => new(LocalCoordinatorWriteKind.Created, null);

    /// <summary>Creates a successful guarded replacement outcome.</summary>
    /// <returns>A replaced result without a failure.</returns>
    public static LocalCoordinatorWriteResult Replaced() => new(LocalCoordinatorWriteKind.Replaced, null);

    /// <summary>Creates a successful recovery outcome for a matching prepared write.</summary>
    /// <returns>A recovered result without a failure.</returns>
    public static LocalCoordinatorWriteResult Recovered() => new(LocalCoordinatorWriteKind.Recovered, null);

    /// <summary>Creates a write outcome blocked by a changed local target or attestation.</summary>
    /// <returns>A conflict result without a failure because the plan mismatch is the diagnostic.</returns>
    public static LocalCoordinatorWriteResult Conflict() => new(LocalCoordinatorWriteKind.Conflict, null);

    /// <summary>Creates an outcome whose write state requires reconciliation before another write.</summary>
    /// <param name="failure">Optional value-safe detail; omitted when the coordinator cannot determine a more specific cause.</param>
    /// <returns>An indeterminate result; a null <paramref name="failure"/> means callers must use the generic reconciliation diagnostic.</returns>
    public static LocalCoordinatorWriteResult Indeterminate(LocalCoordinatorFailure? failure = null) => new(LocalCoordinatorWriteKind.Indeterminate, failure);

    /// <summary>Creates a write outcome for a local store that cannot participate in coordinated transfer.</summary>
    /// <returns>An unsupported-store result with its stable failure diagnostic.</returns>
    public static LocalCoordinatorWriteResult Unsupported() => new(
        LocalCoordinatorWriteKind.Unsupported,
        new LocalCoordinatorFailure("local-secret-transfer-unsupported-store", "The selected LocalSecrets store is not supported for coordinated remote-to-local transfer.", false));

    /// <summary>Creates a write outcome that failed before a safe completion could be established.</summary>
    /// <param name="failure">Value-safe failure that prevented a confirmed write result.</param>
    /// <returns>A failed result carrying the supplied failure.</returns>
    public static LocalCoordinatorWriteResult Failed(LocalCoordinatorFailure failure) => new(LocalCoordinatorWriteKind.Failed, failure);
}

/// <summary>Classifies a guarded local write or recovery outcome.</summary>
internal enum LocalCoordinatorWriteKind
{
    /// <summary>A previously absent local secret was created and attested.</summary>
    Created,
    /// <summary>An attested local secret was replaced with explicit authorization.</summary>
    Replaced,
    /// <summary>A prepared local write was reconciled and committed.</summary>
    Recovered,
    /// <summary>The local target or attestation conflicts with the plan.</summary>
    Conflict,
    /// <summary>The write may have changed local state and requires reconciliation.</summary>
    Indeterminate,
    /// <summary>The selected local store cannot participate in coordinated transfer.</summary>
    Unsupported,
    /// <summary>The coordinator could not safely complete the write operation.</summary>
    Failed
}

/// <summary>Contains a stable, value-safe local coordinator failure.</summary>
internal sealed record LocalCoordinatorFailure(string Code, string Problem, bool Retryable);

/// <summary>Persists only transfer identity and state, never a secret value or a value-derived hash.</summary>
internal sealed record LocalTransferJournal(
    int Version,
    LocalTransferJournalState State,
    string OperationId,
    string PlanIdentity,
    string SourceVersionResource,
    string LocalStorageName,
    string? PreviousOperationId);

/// <summary>Tracks the value-free lifecycle of a local transfer journal.</summary>
internal enum LocalTransferJournalState
{
    /// <summary>A local write is prepared and must be reconciled before another write.</summary>
    Prepared,
    /// <summary>A local write is confirmed and may authorize a later guarded replacement.</summary>
    Committed
}
