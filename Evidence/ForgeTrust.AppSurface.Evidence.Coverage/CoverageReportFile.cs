using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Identifies the original bytes of a report at a canonical path relative to the run root.</summary>
/// <param name="RelativePath">Portable run-relative path below the coverage directory.</param>
/// <param name="LengthBytes">Exact length of the original report.</param>
/// <param name="Sha256">Lowercase SHA-256 digest of the original report.</param>
/// <remarks>Delivery and receipt inventory validation check these values before adopting or publishing the report.</remarks>
internal sealed record CoverageReportFile(string RelativePath, long LengthBytes, string Sha256)
{
    private const string CoverageDirectoryName = "coverage";

    /// <summary>Gets whether the digest is exactly 64 lowercase hexadecimal digits.</summary>
    internal bool HasCanonicalDigest => Sha256.Length == 64 && Sha256.All(static character => char.IsAsciiHexDigitLower(character));

    /// <summary>Qualifies a managed publication's coverage-relative path beneath the run root.</summary>
    /// <param name="publication">Original managed publication with a path relative to coverage.</param>
    /// <param name="normalizeSeparators">Whether to translate native separators for ordinary delivery.
    /// Receipt capture preserves the exact spelling for canonical-path validation.</param>
    /// <returns>The run-relative report metadata, ready for validation by the caller.</returns>
    internal static CoverageReportFile FromPublication(CoverageArtifactInventoryEntry publication, bool normalizeSeparators = false)
        => new(CoverageDirectoryName + "/" + (normalizeSeparators ? publication.Path.Replace('\\', '/') : publication.Path),
            publication.Length, publication.Sha256);

    /// <summary>Rejects unsafe or noncanonical portable report paths without changing their identity.</summary>
    /// <param name="path">A run-relative path in the coverage report directory.</param>
    /// <exception cref="CoverageEvidenceInventoryException">The path is outside the portable report layout.</exception>
    internal static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 4096 || !path.StartsWith(CoverageDirectoryName + "/", StringComparison.Ordinal)
            || !path.IsNormalized(NormalizationForm.FormC) || Path.IsPathRooted(path) || path.Contains('\\') || path.Any(char.IsControl))
            throw new CoverageEvidenceInventoryException("inventory-path-invalid");
        var parts = path.Split('/');
        if (parts.Length > 32) throw new CoverageEvidenceInventoryException("inventory-path-invalid");
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part != part.Trim()
                || part.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0
                || EvidenceArtifactValidation.IsReservedSegment(part))
                throw new CoverageEvidenceInventoryException("inventory-path-noncanonical");
        }
    }

    /// <summary>Opens a run-relative report as a regular file without following links.</summary>
    /// <param name="fullRunRoot">The fully qualified run root containing the coverage directory.</param>
    /// <param name="relativePath">A report path already established by the caller's report inventory or layout.</param>
    /// <returns>The open report stream, owned by the caller.</returns>
    internal static FileStream OpenRead(string fullRunRoot, string relativePath) =>
        CoverageRunArtifactReader.OpenRegularFile(fullRunRoot, Path.Join(fullRunRoot, CoverageDirectoryName),
            Path.Join(fullRunRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
