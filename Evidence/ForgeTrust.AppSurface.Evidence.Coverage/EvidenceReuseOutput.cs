using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Claims an exclusive reuse output root and defines the per-producer directories within it.</summary>
internal static class EvidenceReuseOutput
{
    /// <summary>Gets a safe, stable directory name for an opaque producer identifier.</summary>
    /// <param name="producerId">Producer identifier to hash, potentially containing path characters.</param>
    /// <returns>The lowercase SHA-256 of the UTF-8 identifier.</returns>
    internal static string GetProducerDirectoryName(string producerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(producerId);
        return EvidenceDigest.Sha256(Encoding.UTF8.GetBytes(producerId));
    }

    /// <summary>Gets the custody directories relative to an invocation output root.</summary>
    /// <param name="producerId">Opaque producer identifier.</param>
    /// <param name="useInvocationArtifactRoot">Whether to keep a single CLI producer's artifacts at the invocation root.</param>
    /// <returns>Safe components suitable for joining or verifying one component at a time.</returns>
    internal static string[] GetCustodyComponents(string producerId, bool useInvocationArtifactRoot = false) =>
        useInvocationArtifactRoot ? [] : ["producers", GetProducerDirectoryName(producerId)];

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
            ?? throw new ArgumentException("reuse.output_invalid: Output must name a new child directory, not a filesystem root.", nameof(explicitPath));
        var name = Path.GetFileName(path);
        CoverageRunOutputLease.ValidateArtifactComponent(name);
        using var parent = CoverageRunOutputLease.AcquireArtifactDirectory(parentPath, true, false)
            ?? throw new IOException("reuse.output_unavailable: The output parent could not be acquired safely.");
        using var output = parent.CreateArtifactDirectory(name);
        return output.ArtifactDirectoryPath;
    }
}
