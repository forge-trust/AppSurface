namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Internal in-process bridge to the same captured-tag validation used by <c>release inspect --machine-json</c>.
/// </summary>
/// <remarks>
/// This bridge deliberately returns the versioned machine result from the existing tagged projection resolver. It
/// does not add a second parser or weaken V2 release-evidence validation. The result describes local inspection;
/// callers still need independent protected-event, remote-tag freshness, and output-artifact checks before making
/// a release-eligible claim.
/// </remarks>
internal static class ReleaseInspectMachineAuthority
{
    /// <summary>
    /// Inspects one annotated release tag through the machine-inspect validation path.
    /// </summary>
    /// <param name="repositoryRoot">Trusted repository checkout containing the release tag and objects.</param>
    /// <param name="versionText">SemVer release version without a leading <c>v</c>.</param>
    /// <param name="tag">Annotated release tag matching the version.</param>
    /// <param name="baseRef">Protected branch name that must contain the tag commit.</param>
    /// <param name="cancellationToken">Cancellation for the bounded Git inspection.</param>
    /// <returns>The same bounded V2 machine result emitted by <c>inspect --machine-json</c>.</returns>
    /// <exception cref="ArgumentException">A required request value is missing.</exception>
    /// <exception cref="ReleaseToolException">The tag or its V2 release evidence is invalid.</exception>
    internal static async Task<ReleaseInspectMachineResult> InspectAsync(
        string repositoryRoot,
        string versionText,
        string tag,
        string baseRef,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionText);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);

        var version = SemVer.Parse(versionText);
        if (!string.Equals(tag, version.TagName, StringComparison.Ordinal))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-tag-version-mismatch",
                "The requested tag does not match the requested version.",
                $"Version {version} maps to tag {version.TagName}, but inspection received {tag}.",
                "Use matching version and tag values so the release identity cannot split.",
                "tools/ForgeTrust.AppSurface.Release/README.md#prepared-to-tagged-state"));
        }

        var options = new ReleaseOptions(
            Command: "inspect",
            RepositoryRoot: Path.GetFullPath(repositoryRoot),
            Version: version,
            Tag: tag,
            Date: null,
            DryRun: false,
            ReportPath: null,
            GitHubOutputPath: null,
            FailOnWarnings: false,
            AllowExistingTargets: false,
            BaseRef: ReleasePublishCommand.NormalizeBaseRef(baseRef));
        var workspace = new ReleaseWorkspace(options.RepositoryRoot);
        var resolver = new ReleaseTaggedProjectionResolver(workspace, new ProcessCommandRunner());
        var projection = await resolver.ResolveMachineInspectAsync(options, cancellationToken).ConfigureAwait(false);
        var result = ReleaseInspectMachineResult.FromProjection(options, projection);

        // Keep this bridge's serialized shape and byte limit identical to the CLI contract.
        _ = result.SerializeBounded();
        return result;
    }
}
