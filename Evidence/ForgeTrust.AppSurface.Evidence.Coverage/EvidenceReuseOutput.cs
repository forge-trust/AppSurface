namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Claims one private exclusive output root for a reuse-aware invocation without merging prior artifacts.</summary>
internal static class EvidenceReuseOutput
{
    /// <summary>Atomically creates a new explicit root or random child below the ordinary Evidence output parent.</summary>
    /// <param name="explicitPath">Explicit output path, which must not exist; null selects a random default child.</param>
    /// <param name="defaultParent">Ordinary Evidence output parent, containing a runs child for random invocation roots.</param>
    /// <returns>The absolute retained-parent-bound new directory path.</returns>
    /// <remarks>
    /// Existing output, including an empty directory, is never adopted. Native creation refuses collisions and
    /// linked ancestors and applies current-user-only permissions. This creates no latest alias or receipt state.
    /// Callers must keep the root outside declared inputs and retain all output ownership through actual cleanup.
    /// </remarks>
    internal static string CreateFreshRoot(string? explicitPath, string defaultParent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultParent);
        var path = explicitPath is null
            ? Path.GetFullPath(Path.Join(defaultParent, "runs", Guid.NewGuid().ToString("N")))
            : Path.GetFullPath(explicitPath);
        var parentPath = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("reuse.output_invalid: Output must name a new child directory, not a filesystem root.");
        var name = Path.GetFileName(path);
        CoverageRunOutputLease.ValidateArtifactComponent(name);
        using var parent = CoverageRunOutputLease.AcquireArtifactDirectory(parentPath, true, false)
            ?? throw new IOException("reuse.output_unavailable: The output parent could not be acquired safely.");
        using var output = parent.CreateArtifactDirectory(name);
        return output.ArtifactDirectoryPath;
    }
}
