using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Reads, verifies, restores and stages immutable receipt payload files.</summary>
/// <remarks>The caller holds the key lease and decides how to update authority and quota. These operations
/// dispose their child handles on failure so the caller can retire a payload after they unwind.</remarks>
internal static class EvidenceReceiptPayload
{
    private const int MaximumPayloadFiles = 256;
    private const int MaximumPayloadBytes = (int)EvidenceArtifactWriter.MaximumTotalArtifactBytes;

    /// <summary>The entry bound for a native scan or deletion, including room for payload metadata.</summary>
    internal const int MaximumScannedEntries = MaximumPayloadFiles * 4 + 8;

    private static class ReceiptDocuments
    {
        internal const int OriginPlanMaximumBytes = 4 * 1024 * 1024;
        internal const int OriginManifestMaximumBytes = 4 * 1024 * 1024;
        internal const int PassedProducerMaximumBytes = 64 * 1024;
        internal const int InputIdentityMaximumBytes = EvidenceProducerIdentityBuilder.MaximumMetadataBytes;
        internal const string OriginPlanName = "origin.plan.json";
        internal const string OriginManifestName = "origin.manifest.json";
        internal const string PassedProducerName = "passed-producer.json";
        internal const string InputIdentityName = "identity.json";
        internal const string ArtifactsDirectoryName = "artifacts";

        internal static HashSet<string> GetPayloadRootEntries(bool includesIdentity)
        {
            var entries = new HashSet<string>([OriginPlanName, OriginManifestName, PassedProducerName, ArtifactsDirectoryName], StringComparer.Ordinal);
            if (includesIdentity) entries.Add(InputIdentityName);
            return entries;
        }
    }

    /// <summary>Reads and binds the canonical origin metadata without changing receipt state or touching artifacts.</summary>
    internal static ReceiptOriginMetadata ReadOriginMetadata(CoverageRunOutputLease payload, EvidenceReceiptState state)
    {
        var resultBytes = ReadBoundedFile(payload, ReceiptDocuments.PassedProducerName, ReceiptDocuments.PassedProducerMaximumBytes);
        if (!DecodePassedProducerRecord(state.PassedProducerRecord).AsSpan().SequenceEqual(resultBytes))
            throw new ReceiptMetadataException("passed-producer-record-invalid");

        var manifestBytes = ReadBoundedFile(payload, ReceiptDocuments.OriginManifestName, ReceiptDocuments.OriginManifestMaximumBytes);
        var result = EvidenceCanonicalJson.Deserialize<EvidenceProducerResult>(resultBytes);
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(manifestBytes);
        var planBytes = ReadBoundedFile(payload, ReceiptDocuments.OriginPlanName, ReceiptDocuments.OriginPlanMaximumBytes);
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(planBytes);

        EvidenceReceiptInputIdentity? inputIdentity = null;
        if (state.InputIdentityDigest is not null)
        {
            var identityBytes = ReadBoundedFile(payload, ReceiptDocuments.InputIdentityName, ReceiptDocuments.InputIdentityMaximumBytes);
            if (!StringComparer.Ordinal.Equals(EvidenceDigest.Sha256(identityBytes), state.InputIdentityDigest))
                throw new ReceiptMetadataException("input-identity-digest-invalid");
            inputIdentity = EvidenceCanonicalJson.Deserialize<EvidenceReceiptInputIdentity>(identityBytes);
            var canonicalIdentityBytes = inputIdentity.ValidateAndEncode();
            if (!identityBytes.AsSpan().SequenceEqual(canonicalIdentityBytes))
                throw new ReceiptMetadataException("input-identity-noncanonical");
        }

        if (!resultBytes.AsSpan().SequenceEqual(EvidenceCanonicalJson.Serialize(result))
            || !manifestBytes.AsSpan().SequenceEqual(EvidenceCanonicalJson.Serialize(manifest))
            || !planBytes.AsSpan().SequenceEqual(EvidenceCanonicalJson.Serialize(plan))
            || !EvidenceManifestBuilder.Verify(plan, manifest)
            || !StringComparer.Ordinal.Equals(manifest.ManifestDigest, state.OriginManifestDigest))
            throw new ReceiptMetadataException("origin-binding-invalid");

        if (result.Outcome != EvidenceProducerOutcome.Passed
            || !manifest.ProducerResults.Any(item => item.ProducerId == result.ProducerId
                && resultBytes.AsSpan().SequenceEqual(EvidenceCanonicalJson.Serialize(item))))
            throw new ReceiptMetadataException("passed-producer-invalid");

        return new ReceiptOriginMetadata(plan, manifest, result, inputIdentity);
    }

    /// <summary>The verified origin data shared by inspection and restoration.</summary>
    internal sealed record ReceiptOriginMetadata(EvidencePlan Plan, EvidenceManifest Manifest,
        EvidenceProducerResult Result, EvidenceReceiptInputIdentity? InputIdentity);

    /// <summary>A metadata refusal that inspection and leased restoration handle according to their ownership rules.</summary>
    internal sealed class ReceiptMetadataException(string reason) : Exception
    {
        internal string Reason { get; } = reason;
    }

    private static byte[] ReadBoundedFile(CoverageRunOutputLease root, string name, int maximumBytes)
    {
        using var stream = root.OpenArtifactFile(name, false);
        return BoundedEvidenceDocument.Read(stream, maximumBytes)
            ?? throw new InvalidDataException("Receipt document exceeds its bound.");
    }

    private static byte[] DecodePassedProducerRecord(string? encoded)
    {
        if (encoded is null || encoded.Length > EvidenceReceiptState.MaximumPassedProducerBase64Characters)
            throw new InvalidDataException("Passed producer authority record is missing or over its encoded bound.");
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length > ReceiptDocuments.PassedProducerMaximumBytes)
            throw new InvalidDataException("Passed producer authority record exceeds its byte bound.");
        return bytes;
    }

    /// <summary>Carries a typed validation miss outside open payload handles before invalidation.</summary>
    internal sealed class InvalidOwnedReceiptException(string reason) : Exception
    {
        internal string Reason { get; } = reason;
    }

    /// <summary>Validates a retained payload and its report inventory before restoring any output.</summary>
    /// <remarks>The caller holds the payload lease and handles typed refusals after that lease closes.</remarks>
    internal static async Task<(ReceiptOriginMetadata Origin, DateTimeOffset CompletedAt)> VerifyForRestoreAsync(
        CoverageRunOutputLease payload, EvidenceReceiptState state, EvidenceProducerDeclaration producer,
        CancellationToken cancellationToken)
    {
        var rootEntries = payload.EnumerateArtifactEntries(8);
        var names = rootEntries.Select(static entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        var requiredNames = ReceiptDocuments.GetPayloadRootEntries(state.InputIdentityDigest is not null);
        if (!names.SetEquals(requiredNames)
            || rootEntries.Single(static entry => entry.Name == ReceiptDocuments.ArtifactsDirectoryName).IsDirectory is false
            || rootEntries.Where(static entry => entry.Name != ReceiptDocuments.ArtifactsDirectoryName).Any(static entry => entry.IsDirectory))
            throw new InvalidOwnedReceiptException("payload-container-invalid");
        var origin = ReadOriginMetadata(payload, state);
        if (state.OriginCompletedAt is not { } completedAt)
            throw new InvalidOwnedReceiptException("origin-binding-invalid");

        var declaration = origin.Plan.Profile.Producers.SingleOrDefault(item => item.Id == producer.Id);
        var originResult = origin.Manifest.ProducerResults.SingleOrDefault(item => item.ProducerId == producer.Id);
        if (declaration is null || originResult is null || origin.Result.ProducerId != producer.Id
            || !StringComparer.Ordinal.Equals(
                EvidenceProducerIdentityBuilder.CanonicalDeclarationHash(declaration),
                EvidenceProducerIdentityBuilder.CanonicalDeclarationHash(producer))
            || !EvidenceArtifactValidation.AreValid(producer, originResult.Artifacts))
            throw new InvalidOwnedReceiptException("passed-producer-invalid");

        var artifacts = originResult.Artifacts ?? [];
        await VerifyArtifactsAsync(payload, artifacts, cancellationToken).ConfigureAwait(false);
        if (producer.Kind.Equals("coverage", StringComparison.OrdinalIgnoreCase))
            ValidateCoverageReceiptInventory(producer, artifacts,
                Path.Combine(payload.ArtifactDirectoryPath, ReceiptDocuments.ArtifactsDirectoryName), cancellationToken);
        return (origin with { Result = originResult }, completedAt);
    }

    /// <summary>Opens the artifacts container while the caller holds the payload lease.</summary>
    /// <returns>The caller-owned artifact root lease.</returns>
    internal static CoverageRunOutputLease OpenArtifacts(CoverageRunOutputLease payload)
        => OpenSubdirectory(payload, ReceiptDocuments.ArtifactsDirectoryName);

    /// <summary>Copies verified artifacts through the invocation writer from the caller-owned artifact root.</summary>
    /// <returns>True when the written bytes verify against the writer inventory.</returns>
    internal static async Task<bool> RestoreArtifactsAsync(CoverageRunOutputLease artifactRoot,
        IReadOnlyList<EvidenceArtifactResult> artifacts, EvidenceArtifactWriter writer, CancellationToken cancellationToken)
    {
        foreach (var artifact in artifacts.OrderBy(static item => item.LogicalName, StringComparer.Ordinal)
                     .ThenBy(static item => item.RelativePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var opened = OpenRelativeFile(artifactRoot, artifact.RelativePath, requirePrivate: true);
            var restored = await writer.WriteAsync(artifact.LogicalName, artifact.RelativePath,
                opened.Stream, cancellationToken).ConfigureAwait(false);
            if (!ArtifactMetadataEquals(restored, artifact))
                throw new InvalidOwnedReceiptException("restore-artifact-changed-during-copy");
        }
        return await writer.VerifyWrittenArtifactsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes canonical documents and copies and verifies the exact inventory in an owned stage.</summary>
    /// <remarks>Reserve quota and create the stage before calling. The caller owns renaming and cleanup.
    /// The optional test callback runs immediately before each artifact copy.</remarks>
    internal static async Task WriteStageAsync(CoverageRunOutputLease stage, string sourceRoot,
        ReceiptDocumentBytes documents, IReadOnlyList<EvidenceArtifactResult> artifacts,
        Action<EvidenceArtifactResult>? beforeArtifactCopy, CancellationToken cancellationToken)
    {
        WriteImmediate(stage, ReceiptDocuments.OriginPlanName, documents.Plan, ReceiptDocuments.OriginPlanMaximumBytes);
        WriteImmediate(stage, ReceiptDocuments.OriginManifestName, documents.Manifest, ReceiptDocuments.OriginManifestMaximumBytes);
        WriteImmediate(stage, ReceiptDocuments.PassedProducerName, documents.Producer, ReceiptDocuments.PassedProducerMaximumBytes);
        if (documents.Identity.Length != 0)
            WriteImmediate(stage, ReceiptDocuments.InputIdentityName, documents.Identity, ReceiptDocuments.InputIdentityMaximumBytes);
        using var artifactsRoot = stage.CreateArtifactDirectory(ReceiptDocuments.ArtifactsDirectoryName);
        foreach (var artifact in artifacts.OrderBy(static item => item.RelativePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            beforeArtifactCopy?.Invoke(artifact);
            await CopySourceArtifactToStageAsync(sourceRoot, artifactsRoot, artifact, cancellationToken).ConfigureAwait(false);
        }
        await VerifyArtifactsAsync(stage, artifacts, cancellationToken).ConfigureAwait(false);
        VerifyStagedPayload(stage, artifacts);
    }

    /// <summary>Holds the serialized documents used to size and populate a staging directory.</summary>
    internal readonly record struct ReceiptDocumentBytes(byte[] Plan, byte[] Manifest, byte[] Producer, byte[] Identity);

    /// <summary>Opens an existing private payload below the held key directory.</summary>
    /// <returns>The caller-owned payload directory lease.</returns>
    internal static CoverageRunOutputLease Open(string keyDirectory, string payloadName)
    {
        CoverageRunOutputLease.ValidateArtifactComponent(payloadName);
        if (!EvidenceReceiptPayloadNames.HasPayloadPrefix(payloadName))
            throw new InvalidDataException("Receipt payload name is not owned.");
        var path = Path.Combine(keyDirectory, payloadName);
        return CoverageRunOutputLease.AcquireArtifactDirectory(path, false, true)
            ?? throw new DirectoryNotFoundException("Receipt payload is missing.");
    }

    private static void WriteImmediate(CoverageRunOutputLease root, string name, byte[] bytes, int maximumBytes)
    {
        if (bytes.Length > maximumBytes) throw new InvalidDataException("Receipt document exceeds its bound.");
        using var output = root.OpenArtifactFile(name, true);
        output.Write(bytes);
        output.Flush(flushToDisk: true);
    }

    private static async Task VerifyArtifactsAsync(CoverageRunOutputLease payload,
        IReadOnlyList<EvidenceArtifactResult> artifacts, CancellationToken cancellationToken)
    {
        ValidateInventoryBounds(artifacts);
        using var artifactsRoot = OpenSubdirectory(payload, ReceiptDocuments.ArtifactsDirectoryName);
        long total = 0;
        foreach (var artifact in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (length, digest) = await HashRelativeAsync(artifactsRoot, artifact.RelativePath, artifact.LengthBytes,
                requirePrivate: true, cancellationToken).ConfigureAwait(false);
            total = checked(total + length);
            if (total > MaximumPayloadBytes || !StringComparer.OrdinalIgnoreCase.Equals(digest, artifact.Sha256))
                throw new InvalidDataException("Receipt artifact inventory digest or size differs.");
        }
        VerifyExactTree(artifactsRoot, artifacts.Select(static item => item.RelativePath).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>Verifies every declared artifact before optional receipt custody is staged.</summary>
    /// <param name="sourceRoot">Execution report root; it need not exist when the complete inventory is empty.</param>
    /// <param name="producer">Producer declaration, including the coverage inventory slot when applicable.</param>
    /// <param name="artifacts">Complete result inventory, already checked against the producer's slot declarations.</param>
    /// <param name="cancellationToken">Cancels validation before filesystem access and between streaming reads.</param>
    /// <remarks>Empty generic inventories require no source files or directory. The caller independently checks
    /// source/store separation, passing origin claims, and exact retained custody.</remarks>
    internal static async Task VerifySourceArtifactsAsync(string sourceRoot, EvidenceProducerDeclaration producer,
        IReadOnlyList<EvidenceArtifactResult> artifacts, CancellationToken cancellationToken)
    {
        ValidateInventoryBounds(artifacts);
        cancellationToken.ThrowIfCancellationRequested();
        if (artifacts.Count != 0)
        {
            using var root = CoverageRunOutputLease.AcquireArtifactDirectory(Path.GetFullPath(sourceRoot), false, false)
                ?? throw new DirectoryNotFoundException("Producer artifact root is unavailable.");
            long total = 0;
            foreach (var artifact in artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (length, digest) = await HashRelativeAsync(root, artifact.RelativePath, artifact.LengthBytes,
                    requirePrivate: false, cancellationToken).ConfigureAwait(false);
                total = checked(total + length);
                if (total > MaximumPayloadBytes || !StringComparer.OrdinalIgnoreCase.Equals(digest, artifact.Sha256))
                    throw new InvalidDataException("Source artifact inventory digest or size differs.");
            }
        }
        if (producer.Kind.Equals("coverage", StringComparison.OrdinalIgnoreCase))
            ValidateCoverageReceiptInventory(producer, artifacts, sourceRoot, cancellationToken);
        // Source roots may also contain invocation plans and original execution output. Only the
        // declared writer inventory is copied; the staged/retained receipt tree must be exact.
    }

    private static async Task<(long Length, string Digest)> HashRelativeAsync(CoverageRunOutputLease root,
        string relativePath, long expectedLength, bool requirePrivate, CancellationToken cancellationToken)
    {
        using var opened = OpenRelativeFile(root, relativePath, requirePrivate);
        return await HashStreamAsync(opened.Stream, expectedLength, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Hashes an exact-length receipt stream using bounded reads without taking stream ownership.</summary>
    /// <param name="input">Caller-owned readable stream exposing its length, at the beginning of its declared artifact bytes.</param>
    /// <param name="expectedLength">Manifest length, from zero through the 256-MiB payload bound.</param>
    /// <param name="cancellationToken">Checked before each read, including the final end-of-stream observation.</param>
    /// <param name="destination">Optional caller-owned stream that receives each observed block as it is hashed.</param>
    /// <returns>The observed length and lower-case SHA-256 after exact-length end-of-stream verification.</returns>
    /// <remarks>
    /// This internal validation boundary is intentionally exposed for contract tests without reflection.
    /// It checks the initial length and refuses either growth or truncation while streaming in at most 64-KiB reads.
    /// The caller retains the stream and responsibility for disposal on success and failure.
    /// </remarks>
    /// <exception cref="InvalidDataException">The manifest length is outside its bound or the stream differs from it.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled a read boundary.</exception>
    internal static async Task<(long Length, string Digest)> HashStreamAsync(Stream input, long expectedLength,
        CancellationToken cancellationToken, Stream? destination = null)
    {
        if (expectedLength > MaximumPayloadBytes)
            throw new InvalidDataException("Artifact length differs from its manifest.");
        var (status, digest) = await EvidenceArtifactStream.HashAsync(input, expectedLength, cancellationToken, destination).ConfigureAwait(false);
        return status switch
        {
            EvidenceArtifactLengthStatus.Exact => (expectedLength, digest!),
            EvidenceArtifactLengthStatus.InitialMismatch => throw new InvalidDataException("Artifact length differs from its manifest."),
            EvidenceArtifactLengthStatus.Long => throw new InvalidDataException("Artifact grew during read."),
            _ => throw new InvalidDataException("Artifact changed during read."),
        };
    }

    private static async Task CopySourceArtifactToStageAsync(string sourceRoot, CoverageRunOutputLease stageRoot,
        EvidenceArtifactResult expected, CancellationToken cancellationToken)
    {
        using var source = CoverageRunOutputLease.AcquireArtifactDirectory(Path.GetFullPath(sourceRoot), false, false)
            ?? throw new DirectoryNotFoundException("Producer artifact root is unavailable.");
        using var opened = OpenRelativeFile(source, expected.RelativePath, requirePrivate: false);
        using var destination = OpenRelativeOutput(stageRoot, expected.RelativePath);
        var (length, digest) = await HashStreamAsync(opened.Stream, expected.LengthBytes,
            cancellationToken, destination.Stream).ConfigureAwait(false);
        if (length != expected.LengthBytes || !StringComparer.OrdinalIgnoreCase.Equals(digest, expected.Sha256))
            throw new InvalidDataException("Source artifact changed during staging.");
        await destination.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Stream.Flush(flushToDisk: true);
    }

    private static bool ArtifactMetadataEquals(EvidenceArtifactResult actual, EvidenceArtifactResult expected)
        => actual.LogicalName == expected.LogicalName && actual.RelativePath == expected.RelativePath
            && actual.MediaType == expected.MediaType && actual.LengthBytes == expected.LengthBytes
            && StringComparer.OrdinalIgnoreCase.Equals(actual.Sha256, expected.Sha256);

    private static void ValidateInventoryBounds(IReadOnlyList<EvidenceArtifactResult> artifacts)
    {
        if (artifacts.Count > MaximumPayloadFiles || !artifacts.Select(static item => item.RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count().Equals(artifacts.Count))
            throw new InvalidDataException("Receipt artifact inventory exceeds its bound or contains duplicate paths.");
        var prefixes = new EvidencePathPrefixes();
        foreach (var artifact in artifacts)
        {
            try { _ = EvidenceArtifactValidation.NormalizePortableRelativePath(artifact.RelativePath); }
            catch (ArgumentException exception) { throw new InvalidDataException("Receipt artifact path is not portable.", exception); }
            // The payload's artifacts container adds one directory to the exact-tree depth.
            if (artifact.RelativePath.Count(static character => character == '/') >= 32)
                throw new InvalidDataException("Receipt artifact path exceeds its depth bound.");
            if (!prefixes.TryAdd(artifact.RelativePath))
                throw new InvalidDataException("Receipt artifact paths have colliding component spellings.");
        }
        // Four metadata files and the artifacts container also consume exact-tree entries.
        if (prefixes.Count + 5 > MaximumPayloadFiles * 4)
            throw new InvalidDataException("Receipt artifact tree exceeds its bounded entry count.");
    }

    private static void ValidateCoverageReceiptInventory(EvidenceProducerDeclaration producer,
        IReadOnlyList<EvidenceArtifactResult> artifacts, string artifactRoot, CancellationToken cancellationToken)
    {
        var slot = producer.ArtifactSlots.SingleOrDefault(item => item.LogicalName == CoverageEvidenceInventory.LogicalName);
        if (slot is null || slot.RelativeRoot != "coverage" || slot.MaximumFiles is null
            || slot.MaximumFiles > CoverageEvidenceInventory.MaximumFiles)
            throw new CoverageEvidenceInventoryException("inventory-slot-ineligible");

        var files = artifacts.Where(item => item.LogicalName == CoverageEvidenceInventory.LogicalName)
            .Select(item => new CoverageEvidenceInventoryFile(item.RelativePath, item.LengthBytes, item.Sha256))
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray();
        if (files.Length == 0 || files.Length > slot.MaximumFiles || files.Length > CoverageEvidenceInventory.MaximumFiles
            || files.Sum(file => file.LengthBytes) > slot.MaximumBytes)
            throw new CoverageEvidenceInventoryException("inventory-required-file-missing");

        var inventory = new CoverageEvidenceInventory(CoverageEvidenceInventory.CurrentVersion, files);
        inventory.Validate(artifactRoot, cancellationToken);
        var names = files.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
        CoverageReportSet.ValidateStoredReceipt(Path.GetFullPath(artifactRoot), names, cancellationToken);
    }

    private static OpenedArtifactFile OpenRelativeFile(CoverageRunOutputLease root, string relativePath, bool requirePrivate)
    {
        var normalized = EvidenceArtifactValidation.NormalizePortableRelativePath(relativePath);
        if (!StringComparer.Ordinal.Equals(normalized, relativePath)) throw new InvalidDataException("Artifact path is not canonical.");
        return OpenRelativeArtifact(root, normalized, createNew: false, requirePrivate: requirePrivate);
    }

    private static OpenedArtifactFile OpenRelativeOutput(CoverageRunOutputLease root, string relativePath)
        => OpenRelativeArtifact(root, EvidenceArtifactValidation.NormalizePortableRelativePath(relativePath),
            createNew: true, requirePrivate: true);

    // Retain the opened directories until the file closes, including on Windows.
    private static OpenedArtifactFile OpenRelativeArtifact(CoverageRunOutputLease root, string normalizedPath,
        bool createNew, bool requirePrivate)
    {
        var components = normalizedPath.Split('/');
        var current = root;
        var owned = new List<CoverageRunOutputLease>();
        try
        {
            for (var index = 0; index < components.Length - 1; index++)
            {
                current = createNew
                    ? GetOrCreateSubdirectory(current, components[index])
                    : OpenSubdirectory(current, components[index], requirePrivate);
                owned.Add(current);
            }
            return new OpenedArtifactFile(current.OpenArtifactFile(components[^1], createNew, requirePrivate), owned);
        }
        catch
        {
            DisposeArtifactDirectories(owned);
            throw;
        }
    }

    private static void VerifyStagedPayload(CoverageRunOutputLease stage, IReadOnlyList<EvidenceArtifactResult> artifacts)
    {
        var expected = artifacts.Select(static item => item.RelativePath).ToHashSet(StringComparer.Ordinal);
        using var artifactRoot = OpenSubdirectory(stage, ReceiptDocuments.ArtifactsDirectoryName);
        VerifyExactTree(artifactRoot, expected);
    }

    /// <summary>Verifies an exact, bounded artifact-path inventory without taking ownership of the root.</summary>
    /// <param name="root">Caller-owned native directory lease, held exclusively through verification.</param>
    /// <param name="expectedFiles">Immutable canonical portable root-relative file paths expected in the tree.</param>
    /// <param name="requirePrivate">Whether every opened child must retain current-user-only access.</param>
    /// <param name="maximumTreeEntries">A stricter entry budget from one through 1,024; defaults to the production limit of 1,024.</param>
    /// <remarks>Callers separately verify artifact hashes and lengths. Verification owns and disposes only its child leases.
    /// A smaller per-call budget supports bounded native traversal tests without changing process file-descriptor limits.</remarks>
    internal static void VerifyExactTree(CoverageRunOutputLease root, HashSet<string> expectedFiles, bool requirePrivate = true,
        int maximumTreeEntries = MaximumPayloadFiles * 4)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(expectedFiles);
        if (maximumTreeEntries is < 1 or > MaximumPayloadFiles * 4)
            throw new ArgumentOutOfRangeException(nameof(maximumTreeEntries));
        var actualFiles = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(CoverageRunOutputLease Root, string Prefix)>();
        pending.Push((root, string.Empty));
        var owned = new List<CoverageRunOutputLease>();
        try
        {
            var entryCount = 0;
            while (pending.TryPop(out var item))
            {
                if (item.Prefix.Count(static character => character == '/') >= 32)
                    throw new InvalidDataException("Receipt payload nesting exceeds its bound.");
                foreach (var entry in item.Root.EnumerateArtifactEntries(MaximumScannedEntries))
                {
                    if (++entryCount > maximumTreeEntries) throw new InvalidDataException("Receipt payload tree exceeds its bound.");
                    var path = item.Prefix.Length == 0 ? entry.Name : item.Prefix + "/" + entry.Name;
                    if (entry.IsDirectory)
                    {
                        if (!expectedFiles.Any(file => file.StartsWith(path + "/", StringComparison.Ordinal)))
                            throw new InvalidDataException("Receipt payload contains an undeclared directory.");
                        var child = OpenSubdirectory(item.Root, entry.Name, requirePrivate);
                        owned.Add(child);
                        pending.Push((child, path));
                    }
                    else if (!actualFiles.Add(path)) throw new InvalidDataException("Receipt payload contains duplicate paths.");
                }
            }
            if (!actualFiles.SetEquals(expectedFiles)) throw new InvalidDataException("Receipt payload inventory differs from its manifest.");
        }
        finally { foreach (var item in owned.AsEnumerable().Reverse()) item.Dispose(); }
    }

    private static CoverageRunOutputLease OpenSubdirectory(CoverageRunOutputLease parent, string name, bool requirePrivate = true)
    {
        CoverageRunOutputLease.ValidateArtifactComponent(name);
        return CoverageRunOutputLease.AcquireArtifactDirectory(Path.Combine(parent.ArtifactDirectoryPath, name), false, requirePrivate)
            ?? throw new DirectoryNotFoundException("Receipt payload directory is missing.");
    }

    private static CoverageRunOutputLease GetOrCreateSubdirectory(CoverageRunOutputLease parent, string name)
    {
        try { return OpenSubdirectory(parent, name); }
        catch (DirectoryNotFoundException) { return parent.CreateArtifactDirectory(name); }
    }

    private static void DisposeArtifactDirectories(IReadOnlyList<CoverageRunOutputLease> roots)
    {
        for (var index = roots.Count - 1; index >= 0; index--)
            roots[index].Dispose();
    }

    private sealed class OpenedArtifactFile(FileStream stream, IReadOnlyList<CoverageRunOutputLease> roots) : IDisposable
    {
        internal FileStream Stream { get; } = stream;
        public void Dispose()
        {
            try { Stream.Dispose(); }
            finally { DisposeArtifactDirectories(roots); }
        }
    }
}
