using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>Stable per-user state location shared by every platform package mutation.</summary>
internal static class PlatformLocalSecretStatePaths
{
    // Only deterministic tests override this path; process TMPDIR and application working directories never select it.
    internal static string? TestDirectory { get; set; }
    internal static string DefaultDirectory => TestDirectory ?? ForUserProfile(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static string ForUserProfile(string profile)
    {
        if (string.IsNullOrWhiteSpace(profile) || !Path.IsPathFullyQualified(profile))
            throw new IOException("A stable current-user state directory is unavailable.");
        return Path.Combine(profile, ".appsurface", "local-secrets-state");
    }
}

/// <summary>Provides bounded cross-process writer exclusion. Nested synchronous package calls share one lease.</summary>
internal static partial class PlatformLocalSecretMaintenanceLease
{
    [ThreadStatic] private static Dictionary<string, LeaseState>? _held;

    internal static IDisposable Acquire(string directory, string applicationName, string environment, string? keyPrefix, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // The whole application's environment is one lock: prefixes share native indexes and must not split exclusion.
        // Fold casing because Windows native names and logical keys are case insensitive.
        var identity = string.Join("\n", applicationName.ToUpperInvariant(), environment.ToUpperInvariant());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return AcquireFile(Path.Combine(directory, $"local-secrets-{hash}.maintenance.lock"), timeout, cancellationToken);
    }

    internal static IDisposable AcquireFile(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        var held = _held ??= new Dictionary<string, LeaseState>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (held.TryGetValue(path, out var existing))
        {
            existing.References++;
            return new Lease(path, existing);
        }
        var posture = DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance.PrepareWrite(path);
        if (posture.Kind == FileSecretPostureKind.Unsupported) throw new IOException("The maintenance lease path is unsafe.");
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream? stream = null;
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.ReadWrite };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                stream = new FileStream(path, options);
                if (OperatingSystem.IsWindows()) stream.Lock(0, 1);
                else if (flock(stream.SafeFileHandle.DangerousGetHandle(), 2 | 4) != 0)
                    throw new IOException("The platform maintenance lease is held by another process.");
                var state = new LeaseState(stream);
                held.Add(path, state);
                return new Lease(path, state);
            }
            catch (IOException) when (Stopwatch.GetElapsedTime(started) < timeout)
            {
                stream?.Dispose();
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(25));
            }
            catch { stream?.Dispose(); throw; }
        }
    }

    /// <summary>Converts lease/path failures to the store's structured result before any backend mutation.</summary>
    internal static T Run<T>(Func<IDisposable> acquire, Func<T> operation, Func<AppSurfaceLocalSecretDiagnostic, T> failed)
    {
        try { using var lease = acquire(); return operation(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return failed(new AppSurfaceLocalSecretDiagnostic("local-secret-maintenance-unavailable",
                "The local secret maintenance operation could not complete.",
                "The shared lease or protected backend state is unavailable.",
                "Check user state permissions and retry after competing operations finish.", "local-secrets-migration", true));
        }
    }

    private sealed class LeaseState(FileStream stream)
    {
        internal FileStream Stream { get; } = stream;
        internal int References { get; set; } = 1;
    }

    private sealed class Lease(string path, LeaseState state) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (--state.References != 0) return;
            _held!.Remove(path);
            // Closing releases the operating-system lock even after an interrupted mutation.
            state.Stream.Dispose();
        }
    }

    [System.Runtime.InteropServices.LibraryImport("libc", SetLastError = true)]
    private static partial int flock(IntPtr fileDescriptor, int operation);
}

/// <summary>Durable journal backend for indexed Secret Service and Credential Manager stores.</summary>
internal sealed class PlatformLocalSecretMigrationBackend(
    PlatformAppSurfaceLocalSecretStore.IndexedLocalSecretStore owner,
    AppSurfaceLocalSecretIdentity source,
    AppSurfaceLocalSecretIdentity destination) : IAppSurfaceLocalSecretMigrationBackend
{
    private LocalSecretMigrationJournalFile? _journal;
    private LocalSecretMigrationJournalFile Journal => _journal ??= new(Path.Combine(owner.MigrationStateDirectory, JournalFileName(owner, source, destination)));

    public bool SupportsDurableMigration => true;

    public IDisposable AcquireMaintenanceLease(TimeSpan timeout, CancellationToken cancellationToken) =>
        PlatformLocalSecretMaintenanceLease.Acquire(owner.MigrationStateDirectory, source.ApplicationName, source.Environment, source.KeyPrefix, timeout, cancellationToken);

    public AppSurfaceLocalSecretMigrationJournal? ReadJournal() => Journal.Read();

    public void CommitJournal(AppSurfaceLocalSecretMigrationJournal journal) => Journal.Commit(journal);

    public void ValidateDestination()
    {
        var index = owner.ReadIndexForMigration(destination.ApplicationName, destination.Environment, destination.KeyPrefix);
        if (index.Status != LocalSecretResultStatus.Found)
        {
            throw new IOException(index.Diagnostic?.Problem ?? "The platform index could not be read.");
        }

        if (index.Keys.Any(key => !StringComparer.Ordinal.Equals(key, source.StoredKey)
                                  && !StringComparer.Ordinal.Equals(key, destination.Key.Value)
                                  && StringComparer.OrdinalIgnoreCase.Equals(key, destination.Key.Value)))
        {
            throw new AppSurfaceLocalSecretMigrationCollisionException();
        }
    }

    public string? ReadExact(string storedKey)
    {
        var identity = string.Equals(storedKey, source.StorageName, StringComparison.Ordinal) ? source : destination;
        var result = owner.ReadRaw(identity);
        return result.Status == LocalSecretResultStatus.Found ? result.Value ?? throw new IOException("The platform returned no value for a found record.") : result.Status == LocalSecretResultStatus.Missing ? null : throw new IOException(result.Diagnostic?.Problem ?? "The platform store could not read the exact identifier.");
    }

    public void WriteExact(string storedKey, string value)
    {
        var result = owner.WriteRaw(destination, value);
        if (result.Status != LocalSecretResultStatus.Found) throw new IOException(result.Diagnostic?.Problem ?? "The platform store could not write the destination.");
    }

    public void DeleteExact(string storedKey)
    {
        var result = owner.DeleteRaw(source);
        if (result.Status is not (LocalSecretResultStatus.Found or LocalSecretResultStatus.Missing)) throw new IOException(result.Diagnostic?.Problem ?? "The platform store could not delete the source.");
    }

    public void PublishIndex()
    {
        var journal = ReadJournal();
        if (journal is null) throw new IOException("The platform migration journal is missing before index publication.");
        var index = owner.ReadIndexForMigration(destination.ApplicationName, destination.Environment, destination.KeyPrefix);
        if (index.Status != LocalSecretResultStatus.Found) throw new IOException(index.Diagnostic?.Problem ?? "The platform index could not be read.");
        var keys = index.Keys.ToHashSet(StringComparer.Ordinal);
        keys.Add(destination.Key.Value);
        if (journal.State == AppSurfaceLocalSecretMigrationState.SourceDeletePending)
            keys.Remove(source.StoredKey);
        var result = owner.WriteIndexForMigration(destination.ApplicationName, destination.Environment, destination.KeyPrefix, keys);
        if (result.Status != LocalSecretResultStatus.Found) throw new IOException(result.Diagnostic?.Problem ?? "The platform index could not be published.");
    }

    private static string JournalFileName(PlatformAppSurfaceLocalSecretStore.IndexedLocalSecretStore owner, AppSurfaceLocalSecretIdentity source, AppSurfaceLocalSecretIdentity destination)
    {
        var value = string.Join("\n", owner.Name, source.StorageName, destination.StorageName);
        return $"local-secrets-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()}.migration.json";
    }
}
