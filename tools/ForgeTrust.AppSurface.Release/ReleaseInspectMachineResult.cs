namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Versioned, bounded machine-readable result emitted by successful V2 release inspection.
/// </summary>
/// <param name="Schema">Versioned machine contract identifier.</param>
/// <param name="Version">Validated SemVer release identity.</param>
/// <param name="Tag">Validated annotated tag name.</param>
/// <param name="BaseRef">Normalized protected branch name used for reachability validation.</param>
/// <param name="TagObjectId">Full annotated tag object ID captured before reading the tag.</param>
/// <param name="PeeledCommit">Full commit ID peeled from the captured annotated tag object.</param>
/// <param name="ComparisonBaseCommit">V2 preparation-base commit validated against the release manifest and evidence subject.</param>
/// <param name="EvidenceBundleSha256">SHA-256 of the validated V2 evidence bundle's UTF-8 bytes.</param>
/// <param name="EvidenceSubjectSha256">Validated V2 subject digest bound by the annotated tag trailer.</param>
/// <param name="ReleaseArtifactDigests">SHA-256 values for the fixed V2 release note, sidecar, manifest, and frozen current-pointer artifacts.</param>
internal sealed record ReleaseInspectMachineResult(
    string Schema,
    string Version,
    string Tag,
    string BaseRef,
    string TagObjectId,
    string PeeledCommit,
    string ComparisonBaseCommit,
    string EvidenceBundleSha256,
    string EvidenceSubjectSha256,
    IReadOnlyList<ReleaseInspectArtifactDigest> ReleaseArtifactDigests)
{
    internal const string CurrentSchema = "appsurface-release-inspect-v1";
    internal const int MaximumSerializedUtf8Bytes = 16 * 1024;

    private const int ExpectedReleaseArtifactCount = 5;

    /// <summary>
    /// Creates a result only from a fully validated V2 tagged projection.
    /// </summary>
    /// <param name="options">Validated inspect options.</param>
    /// <param name="projection">Projection returned by the tagged resolver.</param>
    /// <returns>The versioned machine result.</returns>
    /// <exception cref="ReleaseToolException">The projection is not backed by validated V2 evidence.</exception>
    internal static ReleaseInspectMachineResult FromProjection(ReleaseOptions options, ReleaseTaggedProjection projection)
    {
        if (!string.Equals(projection.EvidenceSchema, ReleaseEvidenceV2.Schema, StringComparison.Ordinal)
            || !IsCanonicalGitObjectId(projection.TagObjectId)
            || !IsCanonicalGitObjectId(projection.TagCommit)
            || projection.TagObjectId!.Length != projection.TagCommit.Length
            || !IsCanonicalPreparationBaseCommit(projection.PreparationBaseCommit)
            || projection.PreparationBaseCommit!.Length != projection.TagCommit.Length
            || !IsCanonicalSha256(projection.EvidenceBundleSha256)
            || !IsCanonicalSha256(projection.EvidenceSubjectSha256)
            || !AreValidReleaseArtifactDigests(options.Version, projection.ReleaseArtifactDigests))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-inspect-machine-json-v2-required",
                "Machine-readable release inspection requires validated V2 release evidence.",
                "The tagged projection does not contain the complete bounded V2 comparison-base and artifact-digest identity required by this result schema.",
                "Prepare a V2 release, create its bound annotated tag, and rerun inspect with --machine-json.",
                "tools/ForgeTrust.AppSurface.Release/README.md#prepared-to-tagged-state"));
        }

        return new ReleaseInspectMachineResult(
            CurrentSchema,
            options.Version.ToString(),
            projection.Tag,
            options.BaseRef,
            projection.TagObjectId!,
            projection.TagCommit,
            projection.PreparationBaseCommit!,
            projection.EvidenceBundleSha256,
            projection.EvidenceSubjectSha256,
            projection.ReleaseArtifactDigests
                .Select(artifact => new ReleaseInspectArtifactDigest(artifact.Path, artifact.Value))
                .OrderBy(artifact => artifact.Path, StringComparer.Ordinal)
                .ToArray());
    }

    /// <summary>
    /// Serializes the fixed-shape machine result and rejects output above its documented byte limit.
    /// </summary>
    /// <returns>One JSON object without a trailing newline.</returns>
    /// <exception cref="ReleaseToolException">The serialized result exceeds the machine-output limit.</exception>
    internal string SerializeBounded()
    {
        var json = JsonSerializer.Serialize(this, ReleaseJson.Options);
        if (Encoding.UTF8.GetByteCount(json) <= MaximumSerializedUtf8Bytes)
        {
            return json;
        }

        throw new ReleaseToolException(ReleaseDiagnostic.Error(
            "release-inspect-machine-json-too-large",
            "Machine-readable inspect result exceeds its output limit.",
            $"The serialized result is larger than {MaximumSerializedUtf8Bytes} UTF-8 bytes.",
            "Use a supported bounded release identity and artifact set, then rerun inspect.",
            "tools/ForgeTrust.AppSurface.Release/README.md#prepared-to-tagged-state"));
    }

    private static bool AreValidReleaseArtifactDigests(SemVer version, IReadOnlyList<ReleaseEvidenceArtifactDigest>? artifacts)
    {
        if (artifacts is null || artifacts.Count != ExpectedReleaseArtifactCount)
        {
            return false;
        }

        var expectedPaths = new HashSet<string>(StringComparer.Ordinal)
        {
            $"releases/v{version}.md",
            $"releases/v{version}.md.yml",
            $"releases/v{version}.release.json",
            "releases/current.md",
            "releases/current.md.yml"
        };
        return artifacts.All(artifact =>
            artifact is not null
            && expectedPaths.Remove(artifact.Path)
            && string.Equals(artifact.Algorithm, "sha256", StringComparison.Ordinal)
            && IsCanonicalSha256(artifact.Value))
            && expectedPaths.Count == 0;
    }

    private static bool IsCanonicalGitObjectId(string? value) => value is not null
        && (value.Length is 40 or 64)
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsCanonicalSha256(string? value) => value is not null
        && value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsCanonicalPreparationBaseCommit(string? value) => value is not null
        && value.Length == 40
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>
/// SHA-256 identity for one fixed V2 release artifact validated from the captured peeled commit.
/// </summary>
internal sealed record ReleaseInspectArtifactDigest(string Path, string Sha256);
