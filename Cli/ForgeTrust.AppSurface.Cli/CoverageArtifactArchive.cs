using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using System.Globalization;
using System.Text;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Describes the versioned metadata and integrity facts for one archived coverage run.</summary>
/// <remarks>
/// This is bounded local storage metadata. It describes the bytes observed during the original run and
/// is not an attestation that those bytes match any later source tree.
/// </remarks>
internal sealed record CoverageArtifactRunManifest
{
    /// <summary>Gets the manifest schema version. Version 1 is the only supported version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Gets the run identifier encoded as 32 hexadecimal GUID N-format characters.</summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>Gets the native identity of the output directory that produced the run.</summary>
    public string OutputIdentity { get; init; } = string.Empty;

    /// <summary>Gets the native identity of the repository or worktree containing the run.</summary>
    public string RepositoryIdentity { get; init; } = string.Empty;

    /// <summary>Gets the producing AppSurface version.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Gets the producing run outcome.</summary>
    public string Outcome { get; init; } = string.Empty;

    /// <summary>Gets the time the original coverage run was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>Gets the time this archive was completed.</summary>
    public DateTimeOffset ArchivedAtUtc { get; init; }

    /// <summary>Gets optional bounded Git and configuration fingerprints.</summary>
    public CoverageArtifactProvenance? Provenance { get; init; }

    /// <summary>Gets the ordered file inventory and its verified compressed payload facts.</summary>
    public IReadOnlyList<CoverageArtifactFileEntry> Files { get; init; } = Array.Empty<CoverageArtifactFileEntry>();

    /// <summary>Gets whether every payload and the manifest passed integrity verification.</summary>
    public bool Complete { get; init; }
}

/// <summary>Describes one archived file and the opaque payload that stores its gzip representation.</summary>
/// <param name="Path">The portable path relative to the original output root.</param>
/// <param name="PayloadId">An opaque eight-digit identifier used as the stored payload filename stem.</param>
/// <param name="OriginalLength">The verified original file length in bytes.</param>
/// <param name="OriginalSha256">The lowercase SHA-256 hash of the original bytes.</param>
/// <param name="StoredLength">The verified compressed payload length in bytes.</param>
/// <param name="StoredSha256">The lowercase SHA-256 hash of the compressed payload bytes.</param>
/// <param name="Complete">Whether this payload was fully written and round-trip verified.</param>
internal sealed record CoverageArtifactFileEntry(
    string Path,
    string PayloadId,
    long OriginalLength,
    string OriginalSha256,
    long StoredLength,
    string StoredSha256,
    bool Complete);

/// <summary>Captures the expected path, length, and content hash observed by the archive inventory writer.</summary>
/// <param name="Path">The portable path relative to the source output root.</param>
/// <param name="Length">The expected source length in bytes.</param>
/// <param name="Sha256">The expected lowercase or uppercase SHA-256 hash of the source bytes.</param>
internal sealed record CoverageArtifactInventoryEntry(string Path, long Length, string Sha256);

/// <summary>Contains optional bounded fingerprints that describe original-run provenance.</summary>
/// <param name="GitRevisionFingerprint">An optional fingerprint for the observed Git revision.</param>
/// <param name="GitStateFingerprint">An optional fingerprint for the observed clean, dirty, or unknown state.</param>
/// <param name="ConfigurationFingerprint">An optional fingerprint for effective coverage configuration.</param>
internal sealed record CoverageArtifactProvenance(
    string? GitRevisionFingerprint,
    string? GitStateFingerprint,
    string? ConfigurationFingerprint);

/// <summary>Streams, verifies, and restores private gzip payloads for coverage-run archives.</summary>
/// <remarks>
/// Staging writes only inside a caller-owned private staging directory and never removes source files.
/// The manager remains responsible for atomic directory publication, locking, quotas, and cleanup.
/// </remarks>
internal static class CoverageArtifactArchive
{
    /// <summary>Creates and verifies a complete archive in an existing private staging directory.</summary>
    /// <param name="sourceRoot">The output root containing the inventoried source files.</param>
    /// <param name="stageRoot">An existing, empty, private directory owned by the artifact manager.</param>
    /// <param name="manifestBase">Run metadata captured before archive staging.</param>
    /// <param name="inventory">The explicit writer-owned source inventory with expected lengths and hashes.</param>
    /// <param name="maxOriginalBytes">The strict aggregate uncompressed byte limit.</param>
    /// <param name="maxStoredBytes">The strict aggregate compressed byte limit.</param>
    /// <param name="clock">The clock used to enforce the cooperative time budget and archive timestamp.</param>
    /// <param name="budget">An optional cooperative deadline measured from operation start.</param>
    /// <param name="cancellationToken">A token whose cancellation is preserved.</param>
    /// <returns>The complete manifest after every stored payload has been verified by decompression.</returns>
    internal static async Task<CoverageArtifactRunManifest> StageAsync(
        string sourceRoot,
        string stageRoot,
        CoverageArtifactRunManifest manifestBase,
        IReadOnlyList<CoverageArtifactInventoryEntry> inventory,
        long maxOriginalBytes,
        long maxStoredBytes,
        TimeProvider clock,
        TimeSpan? budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        if (inventory.Count > CoverageArtifactLimits.MaximumFiles) throw new InvalidDataException("Archive inventory has too many files.");
        var work = new CoverageArtifactWorkBudget(clock, budget);
        work.Check(cancellationToken);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long planned = 0;
        foreach (var entry in inventory)
        {
            ValidatePath(entry.Path);
            ValidateHash(entry.Sha256);
            if (!paths.Add(entry.Path.Normalize(NormalizationForm.FormC))) throw new InvalidDataException("Archive inventory has colliding paths.");
            if (entry.Length < 0 || (planned = checked(planned + entry.Length)) > maxOriginalBytes) throw new InvalidDataException("Archive original byte limit reached.");
        }
        using var stage = CoverageRunOutputLease.AcquireArtifactDirectory(stageRoot, false, true)
            ?? throw new IOException("Private archive stage is missing.");
        if (stage.EnumerateArtifactEntries(2).Any(entry => entry.Name != ".appsurface-coverage-operation.lock" || entry.IsDirectory))
            throw new IOException("Archive stage must be empty apart from its cooperative lock.");
        using var source = CoverageRunOutputLease.AcquireArtifactDirectory(sourceRoot, false, false)
            ?? throw new IOException("Archive source is missing.");
        var files = new List<CoverageArtifactFileEntry>(inventory.Count);
        var buffer = new byte[CoverageArtifactLimits.OperationBufferMaxBytes];
        long stored = 0;
        foreach (var entry in inventory)
        {
            work.Check(cancellationToken);
            var payloadId = (files.Count + 1).ToString("D8", CultureInfo.InvariantCulture);
            var path = Path.Join(source.ArtifactDirectoryPath, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            using var parent = CoverageRunOutputLease.AcquireArtifactDirectory(Path.GetDirectoryName(path)!, false, false)
                ?? throw new IOException("Archive input parent is missing.");
            using var input = parent.OpenArtifactFile(Path.GetFileName(path), false, requirePrivate: false);
            var identity = CoverageRunOutputLease.GetArtifactObjectIdentity(input.SafeFileHandle);
            var sourceState = CoverageRunOutputLease.GetArtifactObjectState(input.SafeFileHandle);
            if (input.Length != entry.Length) throw new IOException("Archive input length changed.");
            using var originalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var output = stage.OpenArtifactFile(payloadId + ".gz", true))
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
                {
                    long observed = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        work.Check(cancellationToken);
                        observed = checked(observed + read);
                        if (observed > entry.Length) throw new IOException("Archive input expanded while reading.");
                        originalHash.AppendData(buffer, 0, read);
                        await gzip.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        if (checked(stored + output.Position) > maxStoredBytes) throw new InvalidDataException("Archive staging byte limit reached.");
                    }
                    if (observed != entry.Length) throw new IOException("Archive input was truncated.");
                }
                work.Check(cancellationToken);
                output.Flush(flushToDisk: true);
                if (checked(stored + output.Length) > maxStoredBytes) throw new InvalidDataException("Archive staging byte limit reached.");
            }
            var actualOriginalHash = Convert.ToHexString(originalHash.GetHashAndReset()).ToLowerInvariant();
            if (!actualOriginalHash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Archive input hash changed.");
            using (var rebound = parent.OpenArtifactFile(Path.GetFileName(path), false, requirePrivate: false))
            {
                if (CoverageRunOutputLease.GetArtifactObjectIdentity(rebound.SafeFileHandle) != identity || rebound.Length != entry.Length
                    || CoverageRunOutputLease.GetArtifactObjectState(input.SafeFileHandle) != sourceState
                    || CoverageRunOutputLease.GetArtifactObjectState(rebound.SafeFileHandle) != sourceState)
                    throw new IOException("Archive source binding changed.");
            }
            using var payload = stage.OpenArtifactFile(payloadId + ".gz", false);
            var storedHash = await HashAsync(payload, buffer, work, cancellationToken);
            var storedLength = payload.Length;
            stored = checked(stored + storedLength);
            payload.Position = 0;
            await VerifyExpandedAsync(payload, Stream.Null, entry.Length, entry.Sha256, buffer, work, cancellationToken);
            files.Add(new(entry.Path, payloadId, entry.Length, actualOriginalHash, storedLength, storedHash, true));
        }
        source.VerifyArtifactBinding();
        stage.VerifyArtifactBinding();
        work.Check(cancellationToken);
        var completed = manifestBase with { Files = files, ArchivedAtUtc = clock.GetUtcNow(), Complete = true };
        ValidateManifest(completed);
        return completed;
    }

    /// <summary>Writes one complete version 1 manifest to a caller-owned stream under the JSON size limit.</summary>
    /// <param name="destination">A writable stream positioned at the start of a new manifest.</param>
    /// <param name="manifest">The complete run manifest to serialize.</param>
    internal static void WriteManifest(Stream destination, CoverageArtifactRunManifest manifest)
    {
        ValidateManifest(manifest);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (bytes.LongLength > CoverageArtifactLimits.RunInventoryMaxBytes) throw new InvalidDataException("Archive manifest exceeds its metadata limit.");
        destination.Write(bytes);
        destination.Flush();
    }

    /// <summary>Reads and validates one bounded version 1 manifest without disposing the input stream.</summary>
    /// <param name="source">A readable stream positioned at the start of the manifest.</param>
    /// <returns>The validated complete archive manifest.</returns>
    internal static CoverageArtifactRunManifest ReadManifest(Stream source)
    {
        var result = CoverageArtifactJson.Read<CoverageArtifactRunManifest>(source, CoverageArtifactLimits.RunInventoryMaxBytes, JsonOptions);
        ValidateManifest(result);
        return result;
    }

    /// <summary>Restores all verified payloads into an existing, empty, private staging directory.</summary>
    /// <param name="archiveRoot">A private archive directory containing payload files named by opaque IDs.</param>
    /// <param name="freshStageRoot">A fresh, private destination stage that must remain unpublished.</param>
    /// <param name="manifest">A complete validated version 1 manifest.</param>
    /// <param name="expandedLimit">The strict aggregate expanded-byte limit.</param>
    /// <param name="clock">The clock used to enforce the cooperative time budget.</param>
    /// <param name="budget">An optional cooperative deadline measured from operation start.</param>
    /// <param name="cancellationToken">A token whose cancellation is preserved.</param>
    /// <returns>A task that completes after all restored bytes pass length and SHA-256 verification.</returns>
    internal static async Task RestorePayloadsAsync(
        string archiveRoot,
        string freshStageRoot,
        CoverageArtifactRunManifest manifest,
        long expandedLimit,
        TimeProvider clock,
        TimeSpan? budget,
        CancellationToken cancellationToken)
    {
        ValidateManifest(manifest);
        var work = new CoverageArtifactWorkBudget(clock, budget);
        work.Check(cancellationToken);
        var total = manifest.Files.Aggregate(0L, (sum, entry) => checked(sum + entry.OriginalLength));
        if (total > expandedLimit) throw new InvalidDataException("Archive expanded byte limit reached.");
        using var archive = CoverageRunOutputLease.AcquireArtifactDirectory(archiveRoot, false, true)
            ?? throw new IOException("Archive root is missing.");
        using var stage = CoverageRunOutputLease.AcquireArtifactDirectory(freshStageRoot, false, true)
            ?? throw new IOException("Restore stage is missing.");
        if (stage.EnumerateArtifactEntries(1).Count != 0) throw new IOException("Restore stage must be empty.");
        var buffer = new byte[CoverageArtifactLimits.OperationBufferMaxBytes];
        foreach (var entry in manifest.Files)
        {
            work.Check(cancellationToken);
            using var payload = archive.OpenArtifactFile(entry.PayloadId + ".gz", false);
            if (payload.Length != entry.StoredLength) throw new InvalidDataException("Stored archive length mismatch.");
            var hash = await HashAsync(payload, buffer, work, cancellationToken);
            if (!hash.Equals(entry.StoredSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Stored archive hash mismatch.");
            payload.Position = 0;
            var components = entry.Path.Split('/');
            var parents = new List<CoverageRunOutputLease>();
            try
            {
                var current = stage;
                foreach (var component in components[..^1])
                {
                    work.Check(cancellationToken);
                    var next = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(current.ArtifactDirectoryPath, component), false, true)
                        ?? current.CreateArtifactDirectory(component);
                    parents.Add(next);
                    current = next;
                }
                using var output = current.OpenArtifactFile(components[^1], true);
                await VerifyExpandedAsync(payload, output, entry.OriginalLength, entry.OriginalSha256, buffer, work, cancellationToken);
                output.Flush(flushToDisk: true);
                stage.VerifyArtifactBinding();
            }
            finally { foreach (var parent in parents.AsEnumerable().Reverse()) parent.Dispose(); }
        }
        work.Check(cancellationToken);
        archive.VerifyArtifactBinding();
        stage.VerifyArtifactBinding();
    }

    /// <summary>Verifies every stored and original payload fact without publishing extracted files.</summary>
    /// <param name="archiveRoot">Private archive whose shared or exclusive candidate lease the caller holds.</param>
    /// <param name="manifest">Completed bounded manifest.</param>
    /// <param name="token">Caller cancellation token.</param>
    /// <param name="sharedBudget">Optional cumulative maintenance allowance.</param>
    internal static async Task VerifyPayloadsAsync(string archiveRoot, CoverageArtifactRunManifest manifest, CancellationToken token, CoverageArtifactWorkBudget? sharedBudget = null)
    {
        ValidateManifest(manifest);
        using var archive = CoverageRunOutputLease.AcquireArtifactDirectory(archiveRoot, false, true)
            ?? throw new IOException("The archive is unavailable.");
        var buffer = new byte[CoverageArtifactLimits.OperationBufferMaxBytes];
        var work = sharedBudget ?? new CoverageArtifactWorkBudget(TimeProvider.System, null);
        foreach (var entry in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            using var payload = archive.OpenArtifactFile(entry.PayloadId + ".gz", false);
            if (payload.Length != entry.StoredLength) throw new InvalidDataException("Stored archive length mismatch.");
            var hash = await HashAsync(payload, buffer, work, token).ConfigureAwait(false);
            if (!hash.Equals(entry.StoredSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Stored archive hash mismatch.");
            payload.Position = 0;
            await VerifyExpandedAsync(payload, Stream.Null, entry.OriginalLength, entry.OriginalSha256, buffer, work, token).ConfigureAwait(false);
        }
        archive.VerifyArtifactBinding();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 32,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    private static async Task<string> HashAsync(Stream input, byte[] buffer, CoverageArtifactWorkBudget work, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            work.Check(token);
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task VerifyExpandedAsync(Stream payload, Stream output, long expectedLength, string expectedHash,
        byte[] buffer, CoverageArtifactWorkBudget work, CancellationToken token)
    {
        using var expanded = new GZipStream(payload, CompressionMode.Decompress, leaveOpen: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        int read;
        while ((read = await expanded.ReadAsync(buffer, token)) > 0)
        {
            work.Check(token);
            length = checked(length + read);
            if (length > expectedLength) throw new InvalidDataException("Archive expands beyond its recorded length.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        var actualHash = Convert.ToHexString(hash.GetHashAndReset());
        if (length != expectedLength || !actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Archive expanded length or hash mismatch.");
    }

    private static void ValidateManifest(CoverageArtifactRunManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != 1 || !manifest.Complete || !Guid.TryParseExact(manifest.RunId, "N", out _)
            || manifest.Files is null || manifest.Files.Count > CoverageArtifactLimits.MaximumFiles
            || manifest.Outcome is not ("success" or "failed" or "interrupted")
            || string.IsNullOrEmpty(manifest.OutputIdentity) || manifest.OutputIdentity.Length > 256
            || string.IsNullOrEmpty(manifest.RepositoryIdentity) || manifest.RepositoryIdentity.Length > 256
            || manifest.Version is null || manifest.Version.Length > 256) throw new InvalidDataException("Archive manifest is unsupported or incomplete.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var payloads = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in manifest.Files)
        {
            if (entry is null || entry.PayloadId is null) throw new InvalidDataException("Archive entry is invalid.");
            ValidatePath(entry.Path);
            ValidateHash(entry.OriginalSha256);
            ValidateHash(entry.StoredSha256);
            if (!paths.Add(entry.Path.Normalize(NormalizationForm.FormC)) || !payloads.Add(entry.PayloadId)
                || entry.PayloadId.Length != 8 || entry.PayloadId.Any(c => !char.IsAsciiDigit(c)) || !entry.Complete
                || entry.OriginalLength < 0 || entry.StoredLength < 0)
                throw new InvalidDataException("Archive entries collide or contain invalid integrity facts.");
            total = checked(total + entry.OriginalLength);
            if (total > CoverageArtifactLimits.ExplicitExpandedMaxBytes) throw new InvalidDataException("Archive expanded bound exceeded.");
        }
        foreach (var path in paths)
        {
            var prefix = path;
            while (prefix.LastIndexOf('/') is var separator && separator >= 0)
            {
                prefix = prefix[..separator];
                if (paths.Contains(prefix)) throw new InvalidDataException("An archive file collides with a parent directory.");
            }
        }
        if (manifest.Provenance is { } provenance && new[] { provenance.GitRevisionFingerprint, provenance.GitStateFingerprint,
            provenance.ConfigurationFingerprint }.Any(value => value is { Length: > 128 }))
            throw new InvalidDataException("Archive provenance is oversized.");
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 4096 || Path.IsPathRooted(path) || path.Contains('\\'))
            throw new InvalidDataException("Archive path must be a bounded portable relative path.");
        var components = path.Split('/');
        if (components.Length > 32) throw new InvalidDataException("Archive path is too deep.");
        foreach (var component in components) CoverageRunOutputLease.ValidateArtifactComponent(component);
    }

    private static void ValidateHash(string hash)
    {
        if (hash is null || hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("Archive hash must be a SHA-256 hexadecimal value.");
    }
}
