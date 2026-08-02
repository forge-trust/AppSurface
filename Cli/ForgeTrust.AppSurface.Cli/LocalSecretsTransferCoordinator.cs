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

    /// <summary>Initializes a coordinator using the default per-user AppSurface state root.</summary>
    public LocalSecretsTransferCoordinator()
        : this(GetDefaultStateRoot())
    {
    }

    /// <summary>Initializes a coordinator with an explicit state root for deterministic tests.</summary>
    internal LocalSecretsTransferCoordinator(string stateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        _stateRoot = Path.GetFullPath(stateRoot);
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

    private static bool MatchesCurrentPrecondition(
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

    private static AppSurfaceLocalSecretResult Probe(IAppSurfaceLocalSecretStore store, AppSurfaceLocalSecretIdentity identity) =>
        store is IAppSurfaceLocalSecretMetadataStore metadataStore
            ? metadataStore.Probe(identity)
            : AppSurfaceLocalSecretResult.NotFound(
                LocalSecretResultStatus.ProviderFailed,
                new AppSurfaceLocalSecretDiagnostic(
                    "local-secret-transfer-metadata-unsupported",
                    "Local transfer metadata probes are unavailable.",
                    "The selected local secret store cannot prove target presence without reading a value.",
                    "Use a built-in LocalSecrets store for remote-to-local transfer.",
                    "local-secrets-without-a-remote-vault"),
                store.Name);

    private static AppSurfaceLocalSecretResult Get(IAppSurfaceLocalSecretStore store, AppSurfaceLocalSecretIdentity identity)
    {
        try
        {
            return store.Get(identity);
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

    private static AppSurfaceLocalSecretResult Set(IAppSurfaceLocalSecretStore store, AppSurfaceLocalSecretIdentity identity, string value)
    {
        try
        {
            return store.Set(identity, value);
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
                failure.Problem,
                "Resolve the local transfer coordinator state before changing this local secret.",
                "local-secrets-without-a-remote-vault",
                failure.Retryable),
            store.Name);

    private bool TryEnsureStoreReady(IAppSurfaceLocalSecretStore store, AppSurfaceLocalSecretIdentity identity, out LocalCoordinatorFailure? failure)
    {
        var doctor = store.Doctor(identity.ApplicationName, identity.Environment, identity.KeyPrefix);
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
        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            TrySetPrivateFileMode(lockPath);
            lease = new LocalCoordinatorLease(stream, journalPath, _stateRoot);
            failure = null;
            return true;
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
    }

    private bool TryEnsureStateRoot(out LocalCoordinatorFailure? failure)
    {
        try
        {
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
            TrySetPrivateFileMode(temporaryPath);
            File.Move(temporaryPath, lease.JournalPath, overwrite: true);
            failure = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
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
        IsLowerHex(journal.OperationId, 32) &&
        IsLowerHex(journal.PlanIdentity, 64) &&
        !string.IsNullOrWhiteSpace(journal.SourceVersionResource) &&
        !string.IsNullOrWhiteSpace(journal.LocalStorageName) &&
        (journal.PreviousOperationId is null || IsLowerHex(journal.PreviousOperationId, 32));

    private static bool IsLowerHex(string? value, int length) =>
        value?.Length == length && value.All(static character => character is >= '0' and <= '9' || character is >= 'a' and <= 'f');

    private static string CreateOperationId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static string GetDefaultStateRoot()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".appsurface");
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

/// <summary>Describes the captured local precondition without exposing local values.</summary>
internal sealed record LocalCoordinatorPrecondition(
    LocalCoordinatorPreconditionKind Kind,
    string? PreviousOperationId,
    LocalCoordinatorFailure? Failure)
{
    public static LocalCoordinatorPrecondition Missing() => new(LocalCoordinatorPreconditionKind.Missing, null, null);

    public static LocalCoordinatorPrecondition Replace(string previousOperationId) => new(LocalCoordinatorPreconditionKind.Replace, previousOperationId, null);

    public static LocalCoordinatorPrecondition Conflict(LocalCoordinatorFailure failure) => new(LocalCoordinatorPreconditionKind.Conflict, null, failure);

    public static LocalCoordinatorPrecondition Unsupported() => new(
        LocalCoordinatorPreconditionKind.Unsupported,
        null,
        new LocalCoordinatorFailure("local-secret-transfer-unsupported-store", "The selected LocalSecrets store is not supported for coordinated remote-to-local transfer.", false));

    public static LocalCoordinatorPrecondition Failed(LocalCoordinatorFailure failure) => new(LocalCoordinatorPreconditionKind.Failed, null, failure);
}

internal enum LocalCoordinatorPreconditionKind
{
    Missing,
    Replace,
    Conflict,
    Unsupported,
    Failed
}

/// <summary>Describes a local destination check without exposing local values.</summary>
internal sealed record LocalCoordinatorCheck(LocalCoordinatorCheckKind Kind, LocalCoordinatorFailure? Failure)
{
    public static LocalCoordinatorCheck Ready() => new(LocalCoordinatorCheckKind.Ready, null);

    public static LocalCoordinatorCheck PreparedRecovery() => new(LocalCoordinatorCheckKind.PreparedRecovery, null);

    public static LocalCoordinatorCheck Conflict() => new(LocalCoordinatorCheckKind.Conflict, null);

    public static LocalCoordinatorCheck Indeterminate() => new(LocalCoordinatorCheckKind.Indeterminate, null);

    public static LocalCoordinatorCheck Unsupported() => new(
        LocalCoordinatorCheckKind.Unsupported,
        new LocalCoordinatorFailure("local-secret-transfer-unsupported-store", "The selected LocalSecrets store is not supported for coordinated remote-to-local transfer.", false));

    public static LocalCoordinatorCheck Failed(LocalCoordinatorFailure failure) => new(LocalCoordinatorCheckKind.Failed, failure);
}

internal enum LocalCoordinatorCheckKind
{
    Ready,
    PreparedRecovery,
    Conflict,
    Indeterminate,
    Unsupported,
    Failed
}

/// <summary>Describes a guarded local write or recovery outcome.</summary>
internal sealed record LocalCoordinatorWriteResult(LocalCoordinatorWriteKind Kind, LocalCoordinatorFailure? Failure)
{
    public static LocalCoordinatorWriteResult Created() => new(LocalCoordinatorWriteKind.Created, null);

    public static LocalCoordinatorWriteResult Replaced() => new(LocalCoordinatorWriteKind.Replaced, null);

    public static LocalCoordinatorWriteResult Recovered() => new(LocalCoordinatorWriteKind.Recovered, null);

    public static LocalCoordinatorWriteResult Conflict() => new(LocalCoordinatorWriteKind.Conflict, null);

    public static LocalCoordinatorWriteResult Indeterminate(LocalCoordinatorFailure? failure = null) => new(LocalCoordinatorWriteKind.Indeterminate, failure);

    public static LocalCoordinatorWriteResult Unsupported() => new(
        LocalCoordinatorWriteKind.Unsupported,
        new LocalCoordinatorFailure("local-secret-transfer-unsupported-store", "The selected LocalSecrets store is not supported for coordinated remote-to-local transfer.", false));

    public static LocalCoordinatorWriteResult Failed(LocalCoordinatorFailure failure) => new(LocalCoordinatorWriteKind.Failed, failure);
}

internal enum LocalCoordinatorWriteKind
{
    Created,
    Replaced,
    Recovered,
    Conflict,
    Indeterminate,
    Unsupported,
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

internal enum LocalTransferJournalState
{
    Prepared,
    Committed
}
