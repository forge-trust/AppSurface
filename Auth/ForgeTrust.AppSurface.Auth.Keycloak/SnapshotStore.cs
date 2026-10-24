using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Auth.Keycloak;

/// <summary>A source binding used only at capture; its local path is excluded from canonical identity.</summary>
internal sealed record CaptureFile(string Id, string SourcePath, string Sha256, long Length, int Mode);

/// <summary>Captured relative identity and metadata, independent of the source workspace.</summary>
internal sealed record SnapshotFile(string Id, string Sha256, long Length, int Mode);

/// <summary>Optional per-invocation resource limits shared by private infrastructure operations.</summary>
internal sealed record InvocationLimits(
    long MaxFileBytes = 1024L * 1024 * 1024,
    long MaxAggregateBytes = 2L * 1024 * 1024 * 1024,
    int MaxFileCount = 65536,
    TimeSpan? Deadline = null,
    int MaxProcessOutputBytes = 1024 * 1024,
    int MaxRedirects = 5,
    int MaxArgumentCount = 256,
    int MaxArgumentBytes = 64 * 1024,
    TimeSpan? CleanupDeadline = null)
{
    internal TimeSpan EffectiveDeadline => Deadline ?? TimeSpan.FromSeconds(300);
    internal TimeSpan EffectiveCleanupDeadline => CleanupDeadline ?? TimeSpan.FromSeconds(5);
}

/// <summary>Owns entry acceptance only. Disposal never deletes committed caller-owned snapshots.</summary>
internal sealed class SnapshotHandle : IDisposable
{
    private readonly object sync = new();
    private bool disposed;
    private int active;
    internal string DirectoryPath { get; }
    internal string Digest { get; }
    internal ImmutableArray<SnapshotFile> Files { get; }
    internal string RecipeDigest { get; }
    internal string PolicyDigest { get; }

    internal SnapshotHandle(string path, string digest, ImmutableArray<SnapshotFile> files, string recipe, string policy)
    {
        DirectoryPath = path;
        Digest = digest;
        Files = files;
        RecipeDigest = recipe;
        PolicyDigest = policy;
    }

    internal SnapshotLease Enter()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            active++;
            return new SnapshotLease(this);
        }
    }

    internal int ActiveConsumers { get { lock (sync) return active; } }
    private void Leave() { lock (sync) active--; }
    public void Dispose() { lock (sync) disposed = true; }

    internal sealed class SnapshotLease : IDisposable
    {
        private SnapshotHandle? owner;
        internal SnapshotLease(SnapshotHandle value) => owner = value;
        internal void RequireOwner(SnapshotHandle value)
        {
            if (!ReferenceEquals(owner, value)) throw new ObjectDisposedException(nameof(SnapshotLease));
        }
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Leave();
    }
}

/// <summary>Creates and revalidates a focused content-addressed snapshot directory.</summary>
internal static class SnapshotStore
{
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    internal static async Task<SnapshotHandle> CaptureAsync(
        IEnumerable<CaptureFile> sourceFiles,
        string recipeDigest,
        string policyDigest,
        string absentDestination,
        CancellationToken cancellationToken,
        InvocationLimits? limits = null)
    {
        limits ??= new InvocationLimits();
        ValidateLimits(limits);
        using var deadline = CreateDeadline(cancellationToken, limits.EffectiveDeadline);
        var operationToken = deadline.Token;
        RequireDigest(recipeDigest);
        RequireDigest(policyDigest);
        var files = sourceFiles.ToImmutableArray();
        if (files.Length is < 1 || files.Length > limits.MaxFileCount) throw new InvalidDataException("Invalid captured material count.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long aggregate = 0;
        foreach (var file in files)
        {
            RequireId(file.Id);
            RequireDigest(file.Sha256);
            if (!names.Add(file.Id) || file.Length < 0 || file.Length > limits.MaxFileBytes || file.Mode is not (420 or 493))
                throw new InvalidDataException("Invalid captured material identity or budget.");
            aggregate = checked(aggregate + file.Length);
            if (aggregate > limits.MaxAggregateBytes) throw new InvalidDataException("Captured aggregate budget exceeded.");
        }
        var destination = Path.GetFullPath(absentDestination);
        RequireAbsent(destination);
        RequireSafeAncestors(Path.GetDirectoryName(destination)!);
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        // Advisory only: concurrent exhaustion remains an I/O failure handled by owned staging cleanup.
        var drive = new DriveInfo(Path.GetPathRoot(destination)!);
        if (drive.AvailableFreeSpace < aggregate + 1024 * 1024) throw new IOException("Insufficient capture capacity.");
        var stage = Path.Combine(parent, ".capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        SetMode(stage, (int)PrivateDirectory);
        var clock = Stopwatch.StartNew();
        try
        {
            foreach (var file in files.OrderBy(f => f.Id, StringComparer.Ordinal))
            {
                RequireSafeAncestors(Path.GetFullPath(file.SourcePath));
                await CopyVerifiedAsync(file.SourcePath, Path.Combine(stage, file.Id), file.Sha256, file.Length, file.Mode, clock, limits, operationToken).ConfigureAwait(false);
            }
            var inventory = files.OrderBy(f => f.Id, StringComparer.Ordinal)
                .Select(f => new SnapshotFile(f.Id, f.Sha256, f.Length, f.Mode)).ToImmutableArray();
            var manifest = CanonicalManifest(inventory, recipeDigest, policyDigest);
            await WriteManifestAsync(stage, manifest, operationToken).ConfigureAwait(false);
            operationToken.ThrowIfCancellationRequested();
            RequireSafeAncestors(parent);
            Directory.Move(stage, destination);
            return new SnapshotHandle(destination, Hash(manifest), inventory, recipeDigest, policyDigest);
        }
        finally { CleanupOwned(stage); }
    }

    internal static async Task<string> CopyForUseAsync(SnapshotHandle snapshot, string absentPrivateDestination, CancellationToken cancellationToken, InvocationLimits? limits = null)
    {
        using var accepted = snapshot.Enter();
        return await CopyForAcceptedUseAsync(snapshot, accepted, absentPrivateDestination, cancellationToken, limits).ConfigureAwait(false);
    }

    // The public boundary already accepted this invocation. Disposal may prevent future consumers,
    // but cannot revoke this consumer while it obtains its verified private copies.
    internal static async Task<string> CopyForAcceptedUseAsync(SnapshotHandle snapshot, SnapshotHandle.SnapshotLease accepted,
        string absentPrivateDestination, CancellationToken cancellationToken, InvocationLimits? limits = null)
    {
        accepted.RequireOwner(snapshot);
        limits ??= new InvocationLimits();
        ValidateLimits(limits);
        var started = Stopwatch.StartNew();
        using var deadline = CreateDeadline(cancellationToken, limits.EffectiveDeadline);
        var operationToken = deadline.Token;
        operationToken.ThrowIfCancellationRequested();
        var expectedManifest = CanonicalManifest(snapshot.Files, snapshot.RecipeDigest, snapshot.PolicyDigest);
        RequireSafeAncestors(snapshot.DirectoryPath);
        var manifestPath = Path.Combine(snapshot.DirectoryPath, "snapshot.json");
        if (new FileInfo(manifestPath).Length != expectedManifest.Length) throw new InvalidDataException("Captured manifest changed.");
        using (var opened = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var bytes = new byte[expectedManifest.Length];
            await opened.ReadExactlyAsync(bytes, operationToken).ConfigureAwait(false);
            if (opened.ReadByte() != -1 || !bytes.AsSpan().SequenceEqual(expectedManifest) || Hash(bytes) != snapshot.Digest)
                throw new InvalidDataException("Captured manifest changed.");
        }
        var actual = Directory.EnumerateFileSystemEntries(snapshot.DirectoryPath).Select(Path.GetFileName)
            .Order(StringComparer.Ordinal).ToArray();
        var allowed = snapshot.Files.Select(f => f.Id).Append("snapshot.json").Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(allowed)) throw new InvalidDataException("Captured inventory changed.");
        // This fresh private directory contains only opened, hash-verified copies; later snapshot path mutation cannot affect it.
        var copies = snapshot.Files.Select(f => new CaptureFile(f.Id, Path.Combine(snapshot.DirectoryPath, f.Id), f.Sha256, f.Length, f.Mode));
        var remaining = limits.EffectiveDeadline - started.Elapsed;
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("Snapshot use deadline exceeded.");
        var copied = await CaptureAsync(copies, snapshot.RecipeDigest, snapshot.PolicyDigest, absentPrivateDestination, operationToken, limits with { Deadline = remaining }).ConfigureAwait(false);
        copied.Dispose();
        return copied.DirectoryPath;
    }

    internal static byte[] CanonicalManifest(ImmutableArray<SnapshotFile> files, string recipe, string policy)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            writer.WriteString("recipe", recipe);
            writer.WriteString("policy", policy);
            writer.WriteStartArray("files");
            foreach (var file in files.OrderBy(f => f.Id, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", file.Id);
                writer.WriteString("sha256", file.Sha256);
                writer.WriteNumber("length", file.Length);
                writer.WriteNumber("mode", file.Mode);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static async Task CopyVerifiedAsync(string source, string destination, string digest, long length, int mode, Stopwatch clock, InvocationLimits limits, CancellationToken cancellationToken)
    {
        using var opened = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        if (opened.Length != length) throw new InvalidDataException("Captured material length differs.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        var buffer = new byte[65536];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > limits.EffectiveDeadline) throw new TimeoutException("Capture deadline exceeded.");
            var read = await opened.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total = checked(total + read);
            if (total > length) throw new InvalidDataException("Captured material grew beyond its identity.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        if (total != length || Convert.ToHexStringLower(hash.GetHashAndReset()) != digest)
            throw new InvalidDataException("Captured material digest differs.");
        output.Flush(flushToDisk: true);
        SetMode(destination, mode & ~(int)(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
    }

    private static async Task WriteManifestAsync(string stage, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(Path.Combine(stage, "snapshot.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        file.Flush(flushToDisk: true);
        SetMode(file.Name, (int)UnixFileMode.UserRead);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void RequireDigest(string digest)
    {
        if (digest.Length != 64 || digest.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException("Noncanonical digest.");
    }
    private static void RequireId(string id)
    {
        if (id.Length is < 1 or > 128 || id is "." or ".." || id == "snapshot.json" || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new InvalidDataException("Unsafe captured material ID.");
    }
    private static void RequireAbsent(string path)
    {
        if (Path.Exists(path) || new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
            throw new IOException("Capture destination must be absent.");
    }
    private static void RequireSafeAncestors(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var segment in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var attributes = File.Exists(current) || Directory.Exists(current) ? File.GetAttributes(current) : (FileAttributes)0;
            if ((attributes & FileAttributes.ReparsePoint) != 0 || new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null)
                throw new InvalidDataException("A capture path contains a link.");
        }
    }
    private static void SetMode(string path, int mode) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, (UnixFileMode)mode); }
    private static CancellationTokenSource CreateDeadline(CancellationToken cancellationToken, TimeSpan deadline)
    { var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); source.CancelAfter(deadline); return source; }
    private static void ValidateLimits(InvocationLimits limits)
    { if (limits.MaxFileBytes is <= 0 or > 1024L * 1024 * 1024 || limits.MaxAggregateBytes < limits.MaxFileBytes || limits.MaxFileCount is < 1 or > 65536 || limits.EffectiveDeadline <= TimeSpan.Zero || limits.EffectiveDeadline > TimeSpan.FromSeconds(300) || limits.MaxProcessOutputBytes is < 1 or > 1024 * 1024 || limits.MaxRedirects is < 0 or > 32 || limits.MaxArgumentCount is < 1 or > 4096 || limits.MaxArgumentBytes is < 1 or > 1024 * 1024 || limits.EffectiveCleanupDeadline <= TimeSpan.Zero || limits.EffectiveCleanupDeadline > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(limits)); }
    private static void CleanupOwned(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
