using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>
/// Renders the public-safe, versioned evidence companion for the packaged coverage proof.
/// </summary>
/// <remarks>
/// The Markdown proof remains a private maintainer diagnostic with paths, command details, and bounded logs. This
/// renderer intentionally emits only retained semantic outcomes and relative artifact references, so it is safe to
/// publish beside package validation artifacts without exposing NuGet configuration, command arguments, credentials,
/// raw XML, or host filesystem paths.
/// </remarks>
internal static class CoverageCliConsumerProofEvidenceRenderer
{
    private const int SchemaVersion = 1;

    /// <summary>
    /// Renders one public-safe evidence document.
    /// </summary>
    /// <param name="report">Complete private proof report from which safe semantic facts are selected.</param>
    /// <returns>Indented JSON with a terminating newline.</returns>
    internal static string RenderJson(CoverageCliConsumerProofReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var semanticProof = report.SemanticProof ?? CoverageCliConsumerProofSemanticProof.NotRun;
        var semanticSucceeded = semanticProof.Succeeded;
        var failures = semanticProof.Failures.Count > 0
            ? semanticProof.Failures.Select(failure => new EvidenceFailure(
                failure.Code,
                failure.Scope,
                failure.Cause,
                failure.NextAction,
                NormalizeEvidencePath(failure.EvidenceRelativePath))).ToArray()
            : report.Succeeded && semanticSucceeded
                ? []
                :
                [
                    new EvidenceFailure(
                        "CPV000",
                        "workflow",
                        "The packaged coverage proof did not complete.",
                        "Open the private coverage-cli-consumer-proof.md report and rerun verify-packages after remediation.",
                        "coverage-cli-consumer-proof.md")
                ];
        var evidence = new CoverageCliConsumerProofEvidence(
            SchemaVersion,
            report.Succeeded && semanticSucceeded ? "passed" : "failed",
            report.PackageVersion,
            report.SelectedArtifact is null ? null : $"sha512:{report.SelectedArtifact.Sha512}",
            new EvidenceDriverBoundary(
                "appsurface coverage run",
                "VSTest default collector",
                ["coverlet.collector"],
                "semantic-retention"),
            ToEvidenceOutcome(semanticProof.Raw, report.WorkDirectory, includeSha256: true),
            ToEvidenceOutcome(semanticProof.Merged, report.WorkDirectory, includeSha256: false),
            failures);
        return JsonSerializer.Serialize(
                evidence,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                })
            + "\n";
    }

    private static EvidenceOutcome ToEvidenceOutcome(
        CoverageCliConsumerProofSemanticOutcome outcome,
        string workDirectory,
        bool includeSha256)
        => new(
            outcome.Outcome,
            ToRelativePath(workDirectory, outcome.ArtifactPath),
            includeSha256 ? outcome.Sha256 : null,
            outcome.Invariants);

    private static string? ToRelativePath(string workDirectory, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(workDirectory), Path.GetFullPath(path))
                .Replace('\\', '/');
            return IsSafeRelativePath(relative) ? relative : "unavailable";
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return "unavailable";
        }
    }

    private static string NormalizeEvidencePath(string path)
        => IsSafeRelativePath(path.Replace('\\', '/')) ? path.Replace('\\', '/') : "unavailable";

    private static bool IsSafeRelativePath(string path)
        => !string.IsNullOrWhiteSpace(path)
            && !Path.IsPathRooted(path)
            && !path.StartsWith("../", StringComparison.Ordinal)
            && path != "..";

    private sealed record CoverageCliConsumerProofEvidence(
        int SchemaVersion,
        string Verdict,
        string PackageVersion,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? PackageArtifactDigest,
        EvidenceDriverBoundary DriverBoundary,
        EvidenceOutcome Raw,
        EvidenceOutcome Merged,
        IReadOnlyList<EvidenceFailure> Failures);

    private sealed record EvidenceDriverBoundary(
        string Runner,
        string Integration,
        IReadOnlyList<string> DirectPackages,
        string AssuranceLevel);

    private sealed record EvidenceOutcome(
        string Outcome,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? ArtifactRelativePath,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Sha256,
        IReadOnlyList<string> Invariants);

    private sealed record EvidenceFailure(
        string Code,
        string Scope,
        string Cause,
        string NextAction,
        string EvidenceRelativePath);
}
