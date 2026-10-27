namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Publishes bounded observation state through one fixed, private staging slot.</summary>
/// <remarks>
/// The caller must hold the directory's exclusive operation lease. A fixed slot bounds crash remnants;
/// only a validated private regular slot with its retained identity may be reconciled. Unknown names
/// and oversized, linked, or nonprivate slots remain untouched. This grants no current-output authority.
/// </remarks>
internal static class CoverageArtifactMetadataPublication
{
    /// <summary>Gets the one recognized observation-state publication slot.</summary>
    internal const string StagingName = ".state-write.json";

    /// <summary>Writes and atomically replaces one bounded state file without accumulating unique remnants.</summary>
    /// <param name="directory">Private observation directory under an exclusive operation lease.</param>
    /// <param name="destinationName">Immediate metadata name authorized by the observation writer.</param>
    /// <param name="bytes">Complete, validated encoded state.</param>
    /// <param name="maximumBytes">Category-specific encoded-byte bound, also applied to abandoned staging.</param>
    /// <param name="beforePublish">Optional per-call fault/scheduling seam after durable staging and before rename.</param>
    /// <remarks>The hook verifies cleanup decisions; production omits it. It must not be used as ownership authority.</remarks>
    internal static void Write(CoverageRunOutputLease directory, string destinationName, byte[] bytes,
        long maximumBytes, Action? beforePublish = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        CoverageRunOutputLease.ValidateArtifactComponent(destinationName);
        if (destinationName == StagingName) throw new ArgumentException("The destination must differ from the publication slot.", nameof(destinationName));
        if (bytes.LongLength > maximumBytes) throw new InvalidDataException("Observation state exceeds its encoded-byte limit.");
        directory.VerifyArtifactBinding();
        string? abandonedIdentity = null;
        try
        {
            using var abandoned = directory.OpenArtifactFile(StagingName, createNew: false);
            if (abandoned.Length > maximumBytes)
                throw new InvalidDataException("Observation staging exceeds its bounded publication slot; preserve it for inspection.");
            abandonedIdentity = CoverageRunOutputLease.GetArtifactObjectIdentity(abandoned.SafeFileHandle);
        }
        catch (FileNotFoundException) { }
        if (abandonedIdentity is not null)
            directory.DeleteArtifactEntry(StagingName, 1, abandonedIdentity);

        string? stagedIdentity = null;
        try
        {
            using (var stream = directory.OpenArtifactFile(StagingName, createNew: true))
            {
                stagedIdentity = CoverageRunOutputLease.GetArtifactObjectIdentity(stream.SafeFileHandle);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            beforePublish?.Invoke();
            directory.MoveArtifactEntry(StagingName, directory, destinationName,
                replaceFile: true, expectedSourceIdentity: stagedIdentity);
            stagedIdentity = null;
        }
        finally
        {
            if (stagedIdentity is not null)
            {
                try { directory.DeleteArtifactEntry(StagingName, 1, stagedIdentity); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException) { }
            }
        }
    }
}
