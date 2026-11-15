using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>A versioned, complete set of writer-owned coverage outputs, relative to the Evidence run root.</summary>
/// <remarks>
/// Capture consumes the managed session's declared publications and invocation-owned trees, never an arbitrary
/// output-directory walk. Original report bytes, including provenance, are retained under <c>coverage/</c>.
/// The complete set is bounded to 128 files, 256 MiB of original bytes, and 2 MiB of serialized metadata.
/// Portable paths are limited to 4096 characters and 32 components. Explicit slots may impose smaller aggregate limits.
/// Missing, unsafe, changed or oversized sets are optimization failures; they never change a fresh gate outcome.
/// </remarks>
internal sealed record CoverageEvidenceInventory(int Version, IReadOnlyList<CoverageEvidenceInventoryFile> Files)
{
    /// <summary>The supported closed-inventory format.</summary>
    internal const int CurrentVersion = 1;
    /// <summary>The existing producer-wide uncompressed artifact limit, shared with the bounded writer.</summary>
    internal const long MaximumBytes = EvidenceArtifactWriter.MaximumTotalArtifactBytes;
    /// <summary>The maximum count supported by an explicit multi-file slot.</summary>
    internal const int MaximumFiles = 128;
    /// <summary>The maximum serialized inventory metadata, independent of report byte size.</summary>
    internal const int MaximumMetadataBytes = 2 * 1024 * 1024;
    /// <summary>The adapter-owned slot name; callers explicitly declare it to opt into report custody.</summary>
    internal const string LogicalName = "coverage-reports";

    /// <summary>Creates the optional fresh-manifest slot used for complete reusable report custody.</summary>
    /// <remarks>
    /// Required is false so an oversized or unavailable optional cache inventory cannot invalidate an otherwise
    /// successful fresh coverage manifest. Collection, gate thresholds and required assertions remain independently
    /// mandatory. Receipt admission still requires the complete closed inventory and a null adapter ineligibility
    /// reason; an empty optional slot is never proof of a reusable coverage result. Consumers explicitly declaring
    /// Required=true retain that stronger artifact contract, including invalidation when required custody is missing.
    /// Legacy declarations are never modified implicitly. See the
    /// <see href="../../guides/evidence-reuse.md#coverage-report-custody">coverage report custody contract</see>.
    /// </remarks>
    internal static EvidenceArtifactSlot CreateSlot() => new(LogicalName, "coverage", "application/octet-stream", false, MaximumBytes)
    { MaximumFiles = MaximumFiles };

    /// <summary>Captures required run/gate reports and every retained publication in the managed session.</summary>
    /// <remarks>Includes the current-run history sidecar when the managed session published one. That sidecar is
    /// optional in the existing retention workflow; receipt eligibility does not enable or require history storage.</remarks>
    internal static CoverageEvidenceInventory Capture(string outputRoot, CoverageEvidenceExecutionResult result,
        IReadOnlyList<CoverageArtifactInventoryEntry> publications, CancellationToken token)
    {
        if (!result.Run.Success || result.Gate is null || !result.Gate.Passed)
            throw new CoverageEvidenceInventoryException("inventory-result-not-passed");
        var root = Path.GetFullPath(outputRoot);
        var paths = publications.Select(entry => new CoverageEvidenceInventoryFile("coverage/" + entry.Path, entry.Length, entry.Sha256)).ToList();
        var manifestPath = "coverage/" + CoverageArtifactSession.SidecarName;
        if (File.Exists(Path.Join(root, manifestPath)))
            using (var manifest = Open(root, manifestPath))
            {
                if (manifest.Length > MaximumMetadataBytes) throw new CoverageEvidenceInventoryException("inventory-metadata-limit");
                var run = CoverageArtifactSession.ReadCurrent(manifest);
                if (run.ManifestBase.Outcome != "success" || !run.Inventory.SequenceEqual(publications))
                    throw new CoverageEvidenceInventoryException("inventory-run-manifest-mismatch");
                manifest.Position = 0;
                paths.Add(new(manifestPath, manifest.Length, Hash(manifest, token)));
            }
        var inventory = new CoverageEvidenceInventory(CurrentVersion, paths.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray());
        inventory.Validate(root, token);
        var names = inventory.Files.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
        void Require(string relative)
        {
            ValidatePath(relative);
            if (!names.Contains(relative)) throw new CoverageEvidenceInventoryException("inventory-required-file-missing");
        }
        foreach (var path in new[] { result.Run.CoveragePath, result.Gate.JsonReportPath, result.Gate.MarkdownReportPath,
            result.Gate.PatchTargetsJsonPath, result.Gate.PatchTargetsMarkdownPath }.OfType<string>())
        {
            // These are trusted workflow destinations, not inventory input paths. Prove their exact mapping.
            var relative = Path.GetRelativePath(root, Path.GetFullPath(path)).Replace(Path.DirectorySeparatorChar, '/');
            Require(relative);
        }
        Require("coverage/coverage.cobertura.xml");
        Require("coverage/coverage-gate.json");
        Require("coverage/coverage-gate.md");
        Require("coverage/timings.json");
        Require("coverage/summary.txt");
        using var timings = Open(root, "coverage/timings.json");
        if (timings.Length > MaximumMetadataBytes) throw new CoverageEvidenceInventoryException("inventory-metadata-limit");
        using var document = JsonDocument.Parse(timings);
        foreach (var project in document.RootElement.GetProperty("projects").EnumerateArray())
        {
            var slug = project.GetProperty("slug").GetString();
            Require($"coverage/projects/{slug}/{CoverageProjectManifest.FileName}");
            Require($"coverage/projects/{slug}/coverage.cobertura.xml");
            using var projectManifest = Open(root, $"coverage/projects/{slug}/{CoverageProjectManifest.FileName}");
            if (projectManifest.Length > MaximumMetadataBytes) throw new CoverageEvidenceInventoryException("inventory-metadata-limit");
            using var projectDocument = JsonDocument.Parse(projectManifest);
            if (projectDocument.RootElement.GetProperty("schemaVersion").GetInt32() != 1
                || projectDocument.RootElement.GetProperty("slug").GetString() != slug)
                throw new CoverageEvidenceInventoryException("inventory-project-manifest-mismatch");
            foreach (var report in project.GetProperty("testResults").EnumerateArray())
                Require("coverage/" + report.GetProperty("path").GetString());
        }
        return inventory;
    }

    /// <summary>Validates the entire closed set and its original bytes before any destination is written.</summary>
    /// <remarks>Never trim or normalize receipt paths. Case collisions are rejected on every platform.</remarks>
    internal void Validate(string outputRoot, CancellationToken token = default)
    {
        if (Version != CurrentVersion) throw new CoverageEvidenceInventoryException("inventory-version-unsupported");
        if (Files.Count is 0 or > MaximumFiles) throw new CoverageEvidenceInventoryException("inventory-file-limit");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var components = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        string? previous = null;
        foreach (var file in Files)
        {
            token.ThrowIfCancellationRequested();
            ValidatePath(file.RelativePath);
            if (!seen.Add(file.RelativePath)) throw new CoverageEvidenceInventoryException("inventory-path-collision");
            var prefix = string.Empty;
            foreach (var component in file.RelativePath.Split('/'))
            {
                prefix = prefix.Length == 0 ? component : prefix + "/" + component;
                if (components.TryGetValue(prefix, out var existing) && existing != prefix)
                    throw new CoverageEvidenceInventoryException("inventory-path-collision");
                components[prefix] = prefix;
            }
            if (previous is not null && StringComparer.Ordinal.Compare(previous, file.RelativePath) >= 0)
                throw new CoverageEvidenceInventoryException("inventory-order-invalid");
            previous = file.RelativePath;
            if (file.LengthBytes < 0 || file.LengthBytes > MaximumBytes - total)
                throw new CoverageEvidenceInventoryException("inventory-byte-limit");
            total += file.LengthBytes;
            if (file.Sha256.Length != 64 || file.Sha256.Any(c => !char.IsAsciiHexDigitLower(c)))
                throw new CoverageEvidenceInventoryException("inventory-digest-invalid");
        }
        if (JsonSerializer.SerializeToUtf8Bytes(this).Length > MaximumMetadataBytes)
            throw new CoverageEvidenceInventoryException("inventory-metadata-limit");
        foreach (var file in Files)
        {
            using var input = Open(outputRoot, file.RelativePath);
            if (input.Length != file.LengthBytes || Hash(input, token) != file.Sha256)
                throw new CoverageEvidenceInventoryException("inventory-file-changed");
        }
    }

    /// <summary>Preflights the complete declared slot, then streams original bytes into an exclusive custody root.</summary>
    /// <param name="writer">A fresh writer rooted in separate coordinator-owned staging, preserving run-relative paths.</param>
    /// <param name="slot">The explicitly declared coverage-reports multi-file slot.</param>
    /// <param name="outputRoot">Original Evidence run root containing coverage reports; never overwritten.</param>
    /// <param name="cancellationToken">Invocation cancellation; propagated rather than converted into eligibility.</param>
    /// <returns>The complete custody metadata. The coordinator must discard staging on any failure.</returns>
    internal async Task<IReadOnlyList<EvidenceArtifactResult>> WriteAsync(EvidenceArtifactWriter writer,
        EvidenceArtifactSlot slot, string outputRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Validate(outputRoot, cancellationToken);
        if (slot.LogicalName != LogicalName || slot.RelativeRoot != "coverage" || slot.MaximumFiles is null
            || slot.MaximumFiles > MaximumFiles || slot.MaximumFiles < Files.Count || slot.MaximumBytes < Files.Sum(file => file.LengthBytes)
            || writer.WrittenArtifacts.Count != 0)
            throw new CoverageEvidenceInventoryException("inventory-slot-ineligible");
        var results = new List<EvidenceArtifactResult>(Files.Count);
        foreach (var file in Files)
        {
            using var input = Open(outputRoot, file.RelativePath);
            var artifact = await writer.WriteAsync(slot.LogicalName, file.RelativePath, input, cancellationToken).ConfigureAwait(false);
            if (artifact.LengthBytes != file.LengthBytes || artifact.Sha256 != file.Sha256)
                throw new CoverageEvidenceInventoryException("inventory-file-changed");
            results.Add(artifact);
        }
        return results;
    }

    /// <summary>Rejects unsafe or noncanonical portable receipt paths without changing their identity.</summary>
    internal static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 4096 || !path.StartsWith("coverage/", StringComparison.Ordinal)
            || !path.IsNormalized(NormalizationForm.FormC) || Path.IsPathRooted(path) || path.Contains('\\') || path.Any(char.IsControl))
            throw new CoverageEvidenceInventoryException("inventory-path-invalid");
        var parts = path.Split('/');
        if (parts.Length > 32) throw new CoverageEvidenceInventoryException("inventory-path-invalid");
        foreach (var part in parts)
        {
            var stem = part.Split('.')[0];
            if (part.Length == 0 || part is "." or ".." || part != part.Trim() || part.EndsWith('.')
                || part.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0
                || stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && stem[3] is (>= '1' and <= '9') or '¹' or '²' or '³'))
                throw new CoverageEvidenceInventoryException("inventory-path-noncanonical");
        }
    }

    private static FileStream Open(string root, string relative)
    {
        ValidatePath(relative);
        var fullRoot = Path.GetFullPath(root);
        return CoverageRunArtifactReader.OpenRegularFile(fullRoot, Path.Join(fullRoot, "coverage"),
            Path.Join(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string Hash(Stream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

/// <summary>Original length and digest for one exact, portable run-relative path.</summary>
internal sealed record CoverageEvidenceInventoryFile(string RelativePath, long LengthBytes, string Sha256);

/// <summary>A bounded reason code for optional custody failure; contains no source paths or exception details.</summary>
internal sealed class CoverageEvidenceInventoryException(string reason) : Exception(reason)
{
    /// <summary>Gets the stable reason suitable for coordinator diagnostics.</summary>
    internal string Reason { get; } = reason;
}
