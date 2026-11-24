using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Defines the required run reports and project report references in a closed coverage publication.</summary>
internal static class CoverageReportSet
{
    private const string TimingsPath = "coverage/timings.json";
    private static readonly string[] RequiredReports =
    [
        "coverage/coverage.cobertura.xml",
        "coverage/coverage-gate.json",
        "coverage/coverage-gate.md",
        TimingsPath,
        "coverage/summary.txt",
    ];

    /// <summary>Checks the delivered report inventory against the live collection and gate outputs.</summary>
    /// <param name="root">The current run root containing the coverage directory.</param>
    /// <param name="paths">The exact report paths in the delivery inventory.</param>
    /// <param name="execution">The current collection and gate results, including their actual report destinations.</param>
    /// <param name="token">Cancellation observed while validating report references.</param>
    /// <remarks>Delivery validates project manifests independently of optional receipt metadata limits.</remarks>
    internal static void ValidateOrdinaryDelivery(string root, IReadOnlySet<string> paths,
        CoverageEvidenceExecutionResult execution, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(execution);
        Validate(root, paths, ReportPurpose.OrdinaryDelivery, execution, token);
    }

    /// <summary>Checks optional receipt capture against the live collection and gate outputs.</summary>
    /// <param name="root">The current run root containing the coverage directory.</param>
    /// <param name="paths">The exact report paths in the captured inventory.</param>
    /// <param name="execution">The current collection and gate results, including their actual report destinations.</param>
    /// <param name="token">Cancellation observed while reading metadata and validating report references.</param>
    /// <remarks>Capture validates project manifests and applies the receipt metadata byte limit.</remarks>
    internal static void ValidateReceiptCapture(string root, IReadOnlySet<string> paths,
        CoverageEvidenceExecutionResult execution, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(execution);
        Validate(root, paths, ReportPurpose.ReceiptCapture, execution, token);
    }

    /// <summary>Checks that the separately verified receipt inventory contains the required reports and timing references.</summary>
    /// <param name="root">The receipt artifact root containing the coverage directory.</param>
    /// <param name="paths">The exact report paths declared by the receipt.</param>
    /// <param name="token">Cancellation observed while reading the bounded timings and validating report references.</param>
    internal static void ValidateStoredReceipt(string root, IReadOnlySet<string> paths, CancellationToken token)
        => Validate(root, paths, ReportPurpose.ReceiptStorage, null, token);

    private static void Validate(string root, IReadOnlySet<string> paths, ReportPurpose purpose,
        CoverageEvidenceExecutionResult? execution, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var ordinary = purpose == ReportPurpose.OrdinaryDelivery;
        var stored = purpose == ReportPurpose.ReceiptStorage;

        void Require(string relative)
        {
            if (!stored) CoverageEvidenceInventory.ValidatePath(relative);
            if (paths.Contains(relative)) return;
            if (ordinary)
                throw new InvalidDataException("A required ordinary coverage report is missing from the closed publication set.");
            throw new CoverageEvidenceInventoryException("inventory-required-file-missing");
        }

        if (execution is not null)
        {
            var gate = execution.Gate ?? throw new InvalidDataException("A successful coverage run must produce a gate result.");
            foreach (var path in new[] { execution.Run.CoveragePath, gate.JsonReportPath, gate.MarkdownReportPath,
                gate.PatchTargetsJsonPath, gate.PatchTargetsMarkdownPath }.OfType<string>())
                Require(Path.GetRelativePath(root, Path.GetFullPath(path)).Replace(Path.DirectorySeparatorChar, '/'));
        }
        foreach (var path in RequiredReports) Require(path);

        using var timings = ReadDocument(root, TimingsPath, purpose, token);
        foreach (var project in timings.RootElement.GetProperty("projects").EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var slug = project.GetProperty("slug").GetString();
            if (ordinary && slug is null)
                throw new InvalidDataException("Coverage timings contain a project without a slug.");
            if (stored && string.IsNullOrWhiteSpace(slug))
                throw new CoverageEvidenceInventoryException("inventory-project-manifest-mismatch");
            var projectManifest = $"coverage/projects/{slug}/{CoverageProjectManifest.FileName}";
            Require(projectManifest);
            Require($"coverage/projects/{slug}/coverage.cobertura.xml");
            if (!stored) ValidateProject(root, projectManifest, slug, purpose, token);

            foreach (var report in project.GetProperty("testResults").EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                var path = report.GetProperty("path").GetString();
                if (ordinary && path is null)
                    throw new InvalidDataException("Coverage timings contain a test result without a path.");
                Require("coverage/" + path);
            }
        }
    }

    private static void ValidateProject(string root, string path, string? slug, ReportPurpose purpose, CancellationToken token)
    {
        using var manifest = ReadDocument(root, path, purpose, token);
        var project = manifest.RootElement;
        var version = project.GetProperty("schemaVersion");
        var versionMatches = purpose == ReportPurpose.OrdinaryDelivery
            ? version.GetInt32() == 1
            : version.TryGetInt32(out var schemaVersion) && schemaVersion == 1;
        if (versionMatches && project.GetProperty("slug").GetString() == slug) return;
        if (purpose == ReportPurpose.OrdinaryDelivery)
            throw new InvalidDataException("A managed coverage project manifest does not match its timing entry.");
        throw new CoverageEvidenceInventoryException("inventory-project-manifest-mismatch");
    }

    private static JsonDocument ReadDocument(string root, string relative, ReportPurpose purpose, CancellationToken token)
    {
        using var stream = CoverageRunArtifactReader.OpenRegularFile(root, Path.Join(root, "coverage"),
            Path.Join(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (purpose == ReportPurpose.OrdinaryDelivery) return JsonDocument.Parse(stream);
        // Keep stream parsing's UTF-8 BOM handling while limiting the bytes supplied to the parser.
        using var snapshot = new MemoryStream(ReadReceiptMetadata(stream, token), writable: false);
        return JsonDocument.Parse(snapshot);
    }

    /// <summary>Captures one receipt metadata file for parsing or hashing within the receipt byte cap.</summary>
    /// <param name="stream">A caller-owned stream for one metadata document.</param>
    /// <param name="token">Cancellation observed at the read boundary.</param>
    /// <returns>The bounded snapshot to parse or hash.</returns>
    /// <exception cref="CoverageEvidenceInventoryException">The document exceeded the receipt metadata limit.</exception>
    internal static byte[] ReadReceiptMetadata(Stream stream, CancellationToken token)
        => BoundedEvidenceDocument.Read(stream, CoverageEvidenceInventory.MaximumMetadataBytes, token)
            ?? throw new CoverageEvidenceInventoryException("inventory-metadata-limit");

    private enum ReportPurpose
    {
        OrdinaryDelivery,
        ReceiptCapture,
        ReceiptStorage,
    }
}
