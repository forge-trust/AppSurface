using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Holds the durable filesystem lease for one Docker daemon and physical volume.
/// </summary>
/// <remarks>
/// The active marker is deliberately opaque here: its JSON contract belongs to the supervised owner.
/// Marker bytes must be nonsecret and between 2 bytes and 64 KiB. Acquisition permits an existing
/// marker so callers can inspect or recover it; a normal begin operation must check
/// <see cref="HasActive"/> and refuse to start while one exists. Disposing this lease never removes
/// or changes the marker.
/// </remarks>
internal sealed class SupervisedVolumeLease : IDisposable
{
    private const int MaximumDaemonIdLength = 1024;
    private const string ActiveMarkerName = "active.json";
    private static readonly ConcurrentDictionary<string, byte> Held = new(StringComparer.Ordinal);

    private readonly QualifiedLeaseDirectory _directory;
    private readonly string _registryKey;
    private MarkerSnapshot? _expectedActive;
    private bool _disposed;

    private sealed record MarkerSnapshot(QualifiedLeaseDirectory.FileId Id, byte[] Bytes);

    /// <summary>Gets the private directory containing this lease's durable records.</summary>
    internal string Path => _directory.Path;

    private SupervisedVolumeLease(QualifiedLeaseDirectory directory, string registryKey)
    {
        _directory = directory;
        _registryKey = registryKey;
        if (_directory.Exists(ActiveMarkerName)) CaptureActive();
    }

    /// <summary>
    /// Acquires the private native lease for a daemon and volume without rejecting an existing
    /// active marker, allowing the caller to perform recovery under the same lease.
    /// </summary>
    /// <param name="leaseRoot">Private root directory used to store lease records.</param>
    /// <param name="daemonId">Nonempty bounded daemon identity metadata.</param>
    /// <param name="volumeName">Docker volume name using bounded ASCII Docker name characters.</param>
    /// <returns>The held lease. Awaiting this task does not perform Docker inventory.</returns>
    /// <exception cref="QualifiedOwnershipFailure">The inputs are invalid, locking is disabled, the platform is unsupported, or another lease is held.</exception>
    internal static Task<SupervisedVolumeLease> Acquire(string leaseRoot, string daemonId, string volumeName)
    {
        ValidateDaemonId(daemonId);
        if (!SupervisedDocker.IsSupportedVolumeName(volumeName)) throw new QualifiedOwnershipFailure("InvalidVolumeName");
        if (IsFileLockingDisabled()) throw new QualifiedOwnershipFailure("LockingDisabled");

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(daemonId + "\n" + volumeName)))
            .ToLowerInvariant();
        var registryKey = System.IO.Path.GetFullPath(leaseRoot) + System.IO.Path.DirectorySeparatorChar + key;
        if (!Held.TryAdd(registryKey, 0)) throw new QualifiedOwnershipFailure("Contention");

        QualifiedLeaseDirectory? directory = null;
        try
        {
            directory = new QualifiedLeaseDirectory(leaseRoot, key);
            var lease = new SupervisedVolumeLease(directory, registryKey);
            return Task.FromResult(lease);
        }
        catch
        {
            directory?.Dispose();
            Held.TryRemove(registryKey, out _);
            throw;
        }
    }

    /// <summary>Returns whether a durable active marker exists under this lease.</summary>
    internal bool HasActive
    {
        get
        {
            ThrowIfDisposed();
            _directory.VerifyAnchor();
            if (_expectedActive is not null)
            {
                VerifyActiveUnchanged();
                return true;
            }
            return _directory.Exists(ActiveMarkerName);
        }
    }

    /// <summary>Reports marker retention for a run that attempted its own reservation under this lease.</summary>
    /// <remarks>Unverifiable absence is reported as retained. Cleanup and recovery still require the full marker comparison.</remarks>
    internal bool HasRetainedActiveMarker()
    {
        ThrowIfDisposed();
        try
        {
            _directory.VerifyAnchor();
            return _directory.Exists(ActiveMarkerName);
        }
        catch (QualifiedOwnershipFailure)
        {
            return true;
        }
    }

    /// <summary>
    /// Reads the opaque active marker, capturing its file identity and bytes as the baseline for
    /// subsequent replacement or removal. Returned bytes are an independent bounded copy.
    /// </summary>
    /// <exception cref="QualifiedOwnershipFailure">The marker is missing, unsafe, oversized, or changes while being read.</exception>
    internal byte[] ReadActive()
    {
        ThrowIfDisposed();
        var snapshot = _expectedActive;
        if (snapshot is null) snapshot = CaptureActive();
        else VerifyActiveUnchanged();
        return snapshot.Bytes.ToArray();
    }

    /// <summary>Creates a new active marker only when no marker currently exists.</summary>
    /// <param name="bytes">Opaque nonsecret marker bytes, from 2 bytes through 64 KiB.</param>
    internal void WriteNewActive(byte[] bytes)
    {
        ThrowIfDisposed();
        ValidateMarkerBytes(bytes);
        _directory.VerifyAnchor();
        if (_directory.Exists(ActiveMarkerName)) throw new QualifiedOwnershipFailure("QualifiedActiveOwner");

        _directory.WriteNew(ActiveMarkerName, bytes.ToArray());
        CaptureActive();
    }

    /// <summary>
    /// Atomically replaces the marker only after confirming that its current opened file identity
    /// and bytes still match the snapshot last acquired or read by this lease.
    /// </summary>
    /// <param name="bytes">Opaque nonsecret replacement bytes, from 2 bytes through 64 KiB.</param>
    internal void ReplaceActive(byte[] bytes)
    {
        ThrowIfDisposed();
        ValidateMarkerBytes(bytes);
        VerifyActiveUnchanged();

        _directory.Replace(ActiveMarkerName, bytes.ToArray());
        CaptureActive();
    }

    /// <summary>Removes the active marker only when identity and bytes still match the captured marker.</summary>
    internal void ClearActive()
    {
        ThrowIfDisposed();
        VerifyActiveUnchanged();
        _directory.Delete(ActiveMarkerName);
        _expectedActive = null;
    }

    /// <summary>
    /// Restores a marker with a no-clobber create, but only if the marker is missing while this
    /// lease is held. Intended for recovery after a deadline failure; it never overwrites a marker.
    /// </summary>
    /// <param name="bytes">Opaque nonsecret marker bytes, from 2 bytes through 64 KiB.</param>
    internal void RestoreActive(byte[] bytes) => WriteNewActive(bytes);

    /// <summary>Releases in-process and native locks without cleaning durable marker state.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _directory.Dispose();
        }
        finally
        {
            Held.TryRemove(_registryKey, out _);
        }
    }

    private MarkerSnapshot CaptureActive()
    {
        _directory.VerifyAnchor();
        if (!_directory.Exists(ActiveMarkerName)) throw new QualifiedOwnershipFailure("MarkerChanged");

        return _expectedActive = ReadActiveSnapshot();
    }

    private void VerifyActiveUnchanged()
    {
        _directory.VerifyAnchor();
        if (_expectedActive is not { } expected)
            throw new QualifiedOwnershipFailure("MarkerChanged");

        var actual = ReadActiveSnapshot();
        if (actual.Id != expected.Id || !actual.Bytes.AsSpan().SequenceEqual(expected.Bytes))
            throw new QualifiedOwnershipFailure("MarkerChanged");
    }

    /// <summary>Reads the bounded marker between two name-based identity observations.</summary>
    private MarkerSnapshot ReadActiveSnapshot()
    {
        var initialId = _directory.Identity(ActiveMarkerName);
        var bytes = _directory.Read(ActiveMarkerName);
        var finalId = _directory.Identity(ActiveMarkerName);
        if (initialId != finalId) throw new QualifiedOwnershipFailure("MarkerChanged");
        return new(finalId, bytes);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SupervisedVolumeLease));
    }

    private static void ValidateDaemonId(string daemonId)
    {
        if (string.IsNullOrWhiteSpace(daemonId) || daemonId.Length > MaximumDaemonIdLength)
            throw new QualifiedOwnershipFailure("DaemonIdentityUnavailable");
    }

    private static bool IsFileLockingDisabled()
    {
        return IsFileLockingDisabled(Environment.GetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING"));
    }

    internal static bool IsFileLockingDisabled(string? value)
    {
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
    }

    internal static void ValidateMarkerBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        QualifiedLeaseDirectory.ValidateMarkerLength(bytes.LongLength);
    }
}
