namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>
/// Maintains production RazorWire source paths used by the #697 coverage regression proof.
/// </summary>
/// <remarks>
/// The list deliberately contains more than one path so an intentional production-source rename
/// updates maintained test data instead of making the proof depend on an incidental file name.
/// Values use repository-relative forward-slash paths; <see cref="SelectCoveredSource"/> accepts
/// Cobertura's platform-specific separators before the selected path is passed to the patch gate.
/// </remarks>
internal static class RazorWireCoverageProofSources
{
    private static readonly string[] Sources =
    [
        "Web/ForgeTrust.RazorWire.Cli/ExportSourceResolver.cs",
        "Web/ForgeTrust.RazorWire.Cli/ExportSourceRequestFactory.cs",
    ];

    /// <summary>
    /// Gets the maintained production source candidates in deterministic preference order.
    /// </summary>
    public static IReadOnlyList<string> All => Sources;

    /// <summary>
    /// Selects the first maintained source path present in a Cobertura file-name sequence.
    /// </summary>
    /// <param name="coverageFileNames">File names read from a merged Cobertura artifact.</param>
    /// <returns>The normalized maintained source path present in coverage.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no maintained RazorWire source is covered.</exception>
    public static string SelectCoveredSource(IEnumerable<string> coverageFileNames)
    {
        ArgumentNullException.ThrowIfNull(coverageFileNames);
        var covered = coverageFileNames
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Normalize)
            .ToHashSet(StringComparer.Ordinal);

        return Sources.FirstOrDefault(covered.Contains)
            ?? throw new InvalidOperationException(
                $"Merged Cobertura did not contain a maintained RazorWire proof source. Expected one of: {string.Join(", ", Sources)}.");
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
