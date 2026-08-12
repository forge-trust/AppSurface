using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Classifies the complete release-preparation pull-request diff and admits generated package documentation only with
/// a matching PackageIndex provenance witness.
/// </summary>
/// <remarks>
/// This is a maintainer-integrity gate for repository-owned release preparation. It is not a hostile-fork security
/// boundary: the evaluator and witness generator run from the checked-out pull-request tree. See
/// <c>tools/ForgeTrust.AppSurface.Release/README.md#verify-prep-diff</c> for the exact local and CI workflow.
/// </remarks>
internal sealed class ReleasePreparationDiffVerifier
{
    private const string WitnessSchema = "forge-trust.appsurface.release-prep-witness/v1";
    private const string ManifestPath = "packages/package-index.yml";
    private const string GuidanceTemplatePath = "tools/ForgeTrust.AppSurface.PackageIndex/release-guidance.template";
    private const string ChooserPath = "packages/README.md";
    private const string ReadinessPath = "packages/readiness.md";
    private const string ManagedBeginMarker = "<!-- appsurface-release-guidance: begin -->";
    private const string ManagedEndMarker = "<!-- appsurface-release-guidance: end -->";
    private const string ChangeLogPath = "CHANGELOG.md";
    private const string CurrentReleasePath = "releases/current.md";
    private const string CurrentReleaseSidecarPath = "releases/current.md.yml";
    private const string UnreleasedPath = "releases/unreleased.md";
    private const string UnreleasedEntriesPathPrefix = "releases/unreleased.entries/";
    private const string Documentation = "tools/ForgeTrust.AppSurface.Release/README.md#verify-prep-diff";
    private static readonly Regex ReleaseManifestPath = new("^releases/v(?<version>[^/]+)\\.release\\.json$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionedReleaseArtifactPath = new("^releases/v[^/]+\\.(?:md|md\\.yml|release\\.json|evidence\\.json)$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex GitCommit = new("^[0-9a-f]{40,64}$", RegexOptions.CultureInvariant);
    private readonly ICommandRunner _commandRunner;

    /// <summary>
    /// Creates the full-diff verifier.
    /// </summary>
    /// <param name="commandRunner">Bounded process runner used for Git and exactly one PackageIndex witness invocation.</param>
    internal ReleasePreparationDiffVerifier(ICommandRunner commandRunner)
    {
        ArgumentNullException.ThrowIfNull(commandRunner);
        _commandRunner = commandRunner;
    }

    /// <summary>
    /// Verifies a release-preparation diff from an explicit base branch or ref to HEAD.
    /// </summary>
    /// <param name="repositoryRoot">Checked-out repository root.</param>
    /// <param name="baseRef">Base branch/ref; when omitted callers should pass <c>main</c>.</param>
    /// <param name="noFetch">Whether the caller explicitly accepts offline base-ref diagnostics instead of fetching.</param>
    /// <param name="witnessPath">Optional advanced seam for a pre-created PackageIndex witness.</param>
    /// <param name="cancellationToken">Cancellation token for Git and witness operations.</param>
    /// <returns>Identity, changed-path, and typed diagnostic report.</returns>
    internal async Task<ReleasePreparationDiffResult> VerifyAsync(
        string repositoryRoot,
        string baseRef,
        bool noFetch,
        string? witnessPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);

        var diagnostics = new List<ReleaseDiagnostic>();
        var normalizedRoot = Path.GetFullPath(repositoryRoot);
        if (!TryNormalizeBaseRef(baseRef, out var normalizedBaseRef, out var baseRefIssue))
        {
            Add(diagnostics, "release-prep-base-ref-invalid", "The release-preparation base ref is unsupported.",
                baseRefIssue, "Use a branch name, origin/<branch>, refs/heads/<branch>, or refs/remotes/origin/<branch>.");
            return Result(baseRef, null, null, null, [], diagnostics);
        }

        if (!noFetch)
        {
            var fetchSource = normalizedBaseRef["origin/".Length..];
            var fetch = await RunAsync(
                ["fetch", "origin", $"{fetchSource}:refs/remotes/{normalizedBaseRef}"],
                normalizedRoot,
                TimeSpan.FromMinutes(1),
                cancellationToken);
            if (fetch.ExitCode != 0)
            {
                Add(diagnostics, "release-prep-base-fetch-failed", "The release-preparation base ref could not be refreshed.",
                    $"git fetch origin {baseRef} failed: {DescribeFailure(fetch)}",
                    "Confirm the base branch exists and network access is available, then rerun. Use --no-fetch only for an intentionally offline, already-current checkout.");
                return Result(normalizedBaseRef, null, null, null, [], diagnostics);
            }
        }

        var baseTip = await RequireGitValueAsync(["rev-parse", "--verify", normalizedBaseRef], normalizedRoot, diagnostics, "release-prep-base-ref-unavailable", cancellationToken);
        var head = await RequireGitValueAsync(["rev-parse", "HEAD"], normalizedRoot, diagnostics, "release-prep-head-unavailable", cancellationToken);
        if (baseTip is null || head is null)
        {
            return Result(normalizedBaseRef, baseTip, null, head, [], diagnostics);
        }

        var mergeBasesResult = await RunAsync(["merge-base", "--all", baseTip, head], normalizedRoot, TimeSpan.FromMinutes(1), cancellationToken);
        var mergeBases = mergeBasesResult.ExitCode == 0
            ? mergeBasesResult.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        if (mergeBases.Length != 1)
        {
            Add(diagnostics, "release-prep-merge-base-invalid", "Release preparation requires exactly one merge base.",
                mergeBasesResult.ExitCode == 0
                    ? $"Git returned {mergeBases.Length} merge-base commits for {normalizedBaseRef} and HEAD."
                    : $"git merge-base failed: {DescribeFailure(mergeBasesResult)}",
                "Update the pull-request branch from its base so Git has one merge base, then rerun verify-prep-diff.");
            return Result(normalizedBaseRef, baseTip, null, head, [], diagnostics);
        }

        var mergeBase = mergeBases[0];
        var diff = await RunAsync(
            ["diff", "--name-status", "-z", "--find-renames", $"{mergeBase}..{head}"],
            normalizedRoot,
            TimeSpan.FromMinutes(1),
            cancellationToken);
        if (diff.ExitCode != 0)
        {
            Add(diagnostics, "release-prep-diff-unavailable", "The complete release-preparation diff could not be inspected.",
                DescribeFailure(diff), "Repair the checkout or base ref, then rerun verify-prep-diff.");
            return Result(normalizedBaseRef, baseTip, mergeBase, head, [], diagnostics);
        }

        if (!TryParseNameStatus(diff.StandardOutput, out var changes, out var parseIssue))
        {
            Add(diagnostics, "release-prep-unsupported-status", "Git returned an unsafe release-preparation status stream.",
                parseIssue, "Use ordinary tracked file additions, modifications, and manifest-declared deletions; do not use rename, copy, type-change, or malformed paths.");
            return Result(normalizedBaseRef, baseTip, mergeBase, head, [], diagnostics);
        }

        await ClassifyReleaseArtifactsAsync(changes, normalizedRoot, diagnostics, cancellationToken);
        var isReleasePreparation = changes.Any(change => ReleaseManifestPath.IsMatch(change.Path));
        var candidateChanges = isReleasePreparation ? changes.Where(IsPackageCandidate).ToArray() : [];
        if (candidateChanges.Length > 0)
        {
            var resolvedWitnessPath = witnessPath;
            var deleteWitness = false;
            if (string.IsNullOrWhiteSpace(resolvedWitnessPath))
            {
                resolvedWitnessPath = Path.Join(Path.GetTempPath(), $"appsurface-release-prep-witness-{Guid.NewGuid():N}.json");
                deleteWitness = true;
                var witnessCommand = await RunProcessAsync(
                    "dotnet",
                    [
                        "run", "--project", "tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj", "--",
                        "release-prep-witness", "--repo-root", normalizedRoot, "--base-ref", baseTip, "--witness", resolvedWitnessPath
                    ],
                    normalizedRoot,
                    TimeSpan.FromMinutes(6),
                    cancellationToken);
                if (witnessCommand.ExitCode != 0)
                {
                    Add(diagnostics, "release-prep-package-witness-invalid", "PackageIndex could not produce a release-preparation witness.",
                        DescribeFailure(witnessCommand), "Run the PackageIndex witness command locally, repair the reported package input or generated output, then rerun verify-prep-diff.");
                }
            }

            try
            {
                if (!diagnostics.Any(diagnostic => string.Equals(diagnostic.Code, "release-prep-package-witness-invalid", StringComparison.Ordinal)))
                {
                    var witnessJson = await File.ReadAllTextAsync(resolvedWitnessPath!, cancellationToken);
                    if (!TryParseWitness(witnessJson, out var witness, out var witnessIssue))
                    {
                        Add(diagnostics, "release-prep-package-witness-invalid", "PackageIndex emitted an invalid release-preparation witness.",
                            witnessIssue, "Regenerate the witness through PackageIndex; do not hand edit witness JSON.");
                    }
                    else
                    {
                        await ValidateWitnessAsync(witness!, changes, normalizedRoot, normalizedBaseRef, baseTip, mergeBase, head, diagnostics, cancellationToken);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Add(diagnostics, "release-prep-package-witness-invalid", "PackageIndex did not provide a readable release-preparation witness.",
                    ex.Message, "Regenerate the witness to an ordinary writable temporary path and rerun verify-prep-diff.");
            }
            finally
            {
                if (deleteWitness && resolvedWitnessPath is not null)
                {
                    TryDelete(resolvedWitnessPath);
                }
            }
        }

        return Result(normalizedBaseRef, baseTip, mergeBase, head, changes, diagnostics);
    }

    /// <summary>
    /// Strictly parses NUL-delimited <c>git diff --name-status -z --find-renames</c> output.
    /// </summary>
    /// <param name="output">Raw Git stdout.</param>
    /// <param name="changes">Parsed changes on success.</param>
    /// <param name="issue">Failure reason on malformed input.</param>
    /// <returns>Whether the stream was unambiguous and safe to classify.</returns>
    internal static bool TryParseNameStatus(string output, out IReadOnlyList<ReleasePreparationChange> changes, out string issue)
    {
        ArgumentNullException.ThrowIfNull(output);

        changes = [];
        issue = string.Empty;
        var fields = output.Split('\0');
        var parsed = new List<ReleasePreparationChange>();
        for (var index = 0; index < fields.Length - 1;)
        {
            var status = fields[index++];
            if (!IsSupportedNameStatus(status))
            {
                issue = $"The NUL-delimited name-status stream contains unsupported status '{status}'.";
                return false;
            }

            var needsOriginalPath = status.StartsWith('R') || status.StartsWith('C');
            if (index >= fields.Length - 1 || (needsOriginalPath && index + 1 >= fields.Length - 1))
            {
                issue = $"Git status '{status}' does not have the required path field(s).";
                return false;
            }

            if (needsOriginalPath)
            {
                var original = fields[index++];
                var path = fields[index++];
                parsed.Add(new ReleasePreparationChange(status, path, original));
            }
            else
            {
                parsed.Add(new ReleasePreparationChange(status, fields[index++]));
            }
        }

        if (fields[^1].Length != 0)
        {
            issue = "Git name-status output was not NUL terminated.";
            return false;
        }

        changes = parsed;
        return true;
    }

    private async Task ClassifyReleaseArtifactsAsync(
        IReadOnlyList<ReleasePreparationChange> changes,
        string repositoryRoot,
        List<ReleaseDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var manifests = changes.Where(change => ReleaseManifestPath.IsMatch(change.Path)).ToArray();
        var allowsCurrentPointerBootstrap = manifests.Length == 0 && IsCurrentPointerBootstrap(changes);
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (!IsSafePath(change.Path) || (change.OriginalPath is not null && !IsSafePath(change.OriginalPath)))
            {
                Add(diagnostics, "release-prep-unexpected-path", "Release preparation contains an unsafe repository path.",
                    $"Git reported '{change.OriginalPath ?? change.Path}' -> '{change.Path}'.", "Use normalized repository-relative paths without traversal, control characters, or symbolic-link indirection.");
                continue;
            }

            if (!seenPaths.Add(change.Path))
            {
                Add(diagnostics, "release-prep-unsupported-status", "Release preparation reports one path more than once.",
                    $"'{change.Path}' appears multiple times in the complete Git diff.", "Resolve the duplicate path operation and rerun verify-prep-diff.");
            }

            if (change.Status.StartsWith('R'))
            {
                Add(diagnostics, "release-prep-rename-forbidden", "Release preparation may not rename files.",
                    $"Git reported {change.Status}: {change.OriginalPath} -> {change.Path}.", "Commit the intended release artifacts as ordinary additions, modifications, or manifest-declared deletions.");
                continue;
            }

            if (change.Status.StartsWith('C'))
            {
                Add(diagnostics, "release-prep-unsupported-status", "Release preparation may not copy files.",
                    $"Git reported {change.Status}: {change.OriginalPath} -> {change.Path}.", "Use an ordinary tracked file change instead of copy detection.");
                continue;
            }

            if (!allowsCurrentPointerBootstrap
                && (string.Equals(change.Path, CurrentReleaseSidecarPath, StringComparison.Ordinal)
                    || string.Equals(change.OriginalPath, CurrentReleaseSidecarPath, StringComparison.Ordinal)))
            {
                Add(diagnostics, "release-prep-permanent-sidecar-changed", "The permanent current-release sidecar must not change.",
                    "releases/current.md.yml is version-independent metadata.", "Remove the sidecar edit from the release-preparation pull request.");
            }
        }

        if (manifests.Length == 0)
        {
            ValidateNonReleaseArtifactChanges(changes, allowsCurrentPointerBootstrap, diagnostics);
            return;
        }

        if (manifests.Length != 1 || manifests[0].Status != "A")
        {
            Add(diagnostics, "release-prep-release-manifest-shape", "Release preparation requires exactly one added versioned release manifest.",
                manifests.Length == 0 ? "No releases/v<version>.release.json path was added." : $"Found {manifests.Length} versioned release manifest change(s); each release manifest must be status A.",
                "Generate one new release manifest with ./eng/release prepare and do not modify historical manifests.");
            return;
        }

        var version = ReleaseManifestPath.Match(manifests[0].Path).Groups["version"].Value;
        var manifestPath = Path.Join(repositoryRoot, manifests[0].Path.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            if (!ReleaseManifestV2Validator.TryDeserialize(json, out var manifest, out var issue)
                || manifest is null
                || !string.Equals(manifest.Version, version, StringComparison.Ordinal))
            {
                Add(diagnostics, "release-prep-release-manifest-shape", "The added release manifest is not a valid release-preparation manifest.",
                    string.IsNullOrWhiteSpace(issue) ? "The manifest version does not match its file name." : issue,
                    "Regenerate the release artifacts with ./eng/release prepare, then commit the complete generated set.");
                return;
            }

            ValidateReleaseArtifactChanges(version, changes, manifest.ConsumedUnreleasedEntryPaths, diagnostics);
        }
        catch (IOException ex)
        {
            Add(diagnostics, "release-prep-release-manifest-shape", "The added release manifest could not be read from HEAD.",
                ex.Message, "Restore the generated manifest as an ordinary tracked file and rerun verify-prep-diff.");
        }
    }

    internal static void ValidateReleaseArtifactChanges(
        string version,
        IReadOnlyList<ReleasePreparationChange> changes,
        IReadOnlyList<string> consumedEntryPaths,
        List<ReleaseDiagnostic> diagnostics)
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"releases/v{version}.md"] = "A",
            [$"releases/v{version}.md.yml"] = "A",
            [$"releases/v{version}.release.json"] = "A",
            [$"releases/v{version}.evidence.json"] = "A",
            ["releases/current.md"] = "M",
            ["CHANGELOG.md"] = "M",
            ["releases/unreleased.md"] = "M",
            ["releases/unreleased.md.yml"] = "M"
        };
        var consumed = new HashSet<string>(consumedEntryPaths, StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (expected.TryGetValue(change.Path, out var requiredStatus))
            {
                if (!string.Equals(change.Status, requiredStatus, StringComparison.Ordinal))
                {
                    Add(diagnostics, "release-prep-unsupported-status", "A release artifact has an invalid Git status.",
                        $"'{change.Path}' must be {requiredStatus} but Git reported {change.Status}.", "Regenerate the release-preparation artifacts from the current base branch.");
                }

                continue;
            }

            if (change.Status == "D" && consumed.Contains(change.Path))
            {
                continue;
            }

            if (IsPackageCandidate(change))
            {
                if (change.Status != "M")
                {
                    Add(diagnostics, "release-prep-unsupported-status", "A package provenance path has an invalid Git status.",
                        $"'{change.Path}' must be M but Git reported {change.Status}.", "Keep package sources and generated documentation as ordinary modifications in a release-preparation pull request.");
                }

                continue;
            }

            Add(diagnostics, "release-prep-unexpected-path", "Release preparation changed a path outside its approved artifact and provenance surfaces.",
                $"'{change.Path}' with status {change.Status} is not part of the release-preparation contract.", "Move unrelated work to a separate pull request, or regenerate the approved release artifacts.");
        }

        foreach (var expectedPath in expected.Where(expectedPath => !changes.Any(change => string.Equals(change.Path, expectedPath.Key, StringComparison.Ordinal))))
        {
            Add(diagnostics, "release-prep-unexpected-path", "A required release-preparation artifact is missing.",
                $"The complete diff does not contain '{expectedPath.Key}'.", "Regenerate the complete release artifact set with ./eng/release prepare.");
        }

        foreach (var consumedPath in consumed.Where(consumedPath => !changes.Any(change => change.Status == "D" && string.Equals(change.Path, consumedPath, StringComparison.Ordinal))))
        {
            Add(diagnostics, "release-prep-release-manifest-shape", "The release manifest records an unreleased entry that was not deleted.",
                $"'{consumedPath}' is listed in consumedUnreleasedEntryPaths but has no D diff entry.", "Regenerate the release artifacts so the manifest and archive handoff agree.");
        }
    }

    private static void ValidateNonReleaseArtifactChanges(
        IReadOnlyList<ReleasePreparationChange> changes,
        bool allowsCurrentPointerBootstrap,
        List<ReleaseDiagnostic> diagnostics)
    {
        foreach (var change in changes.Where(change => IsReleasePreparationOwnedPath(change.Path)))
        {
            if (IsAllowedNonReleaseArtifactChange(change, allowsCurrentPointerBootstrap))
            {
                continue;
            }

            Add(diagnostics, "release-prep-release-manifest-required", "A release-preparation artifact changed without an added versioned release manifest.",
                $"'{change.Path}' with status {change.Status} is not one of the allowed unreleased-entry updates.", "Generate a versioned release manifest with ./eng/release prepare, or limit the change to releases/unreleased.md and added unreleased entry files.");
        }
    }

    private static bool IsCurrentPointerBootstrap(IReadOnlyList<ReleasePreparationChange> changes) =>
        changes.Any(change => change.Status == "A" && string.Equals(change.Path, CurrentReleasePath, StringComparison.Ordinal))
        && changes.Any(change => change.Status == "A" && string.Equals(change.Path, CurrentReleaseSidecarPath, StringComparison.Ordinal));

    private static bool IsAllowedNonReleaseArtifactChange(ReleasePreparationChange change, bool allowsCurrentPointerBootstrap) =>
        (change.Status == "M" && string.Equals(change.Path, UnreleasedPath, StringComparison.Ordinal))
        || (change.Status == "A" && change.Path.StartsWith(UnreleasedEntriesPathPrefix, StringComparison.Ordinal) && change.Path.EndsWith(".md", StringComparison.Ordinal))
        || (allowsCurrentPointerBootstrap
            && change.Status == "A"
            && (string.Equals(change.Path, CurrentReleasePath, StringComparison.Ordinal)
                || string.Equals(change.Path, CurrentReleaseSidecarPath, StringComparison.Ordinal)));

    private static bool IsReleasePreparationOwnedPath(string path) =>
        string.Equals(path, ChangeLogPath, StringComparison.Ordinal)
        || string.Equals(path, CurrentReleasePath, StringComparison.Ordinal)
        || string.Equals(path, CurrentReleaseSidecarPath, StringComparison.Ordinal)
        || string.Equals(path, UnreleasedPath, StringComparison.Ordinal)
        || string.Equals(path, "releases/unreleased.md.yml", StringComparison.Ordinal)
        || path.StartsWith(UnreleasedEntriesPathPrefix, StringComparison.Ordinal)
        || VersionedReleaseArtifactPath.IsMatch(path);

    internal async Task ValidateWitnessAsync(
        ReleasePreparationWitnessDocument witness,
        IReadOnlyList<ReleasePreparationChange> changes,
        string repositoryRoot,
        string baseRef,
        string baseTip,
        string mergeBase,
        string head,
        List<ReleaseDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(witness.Schema, WitnessSchema, StringComparison.Ordinal)
            || !string.Equals(witness.BaseRef, baseTip, StringComparison.Ordinal)
            || !string.Equals(witness.BaseTipCommit, baseTip, StringComparison.Ordinal)
            || !string.Equals(witness.MergeBaseCommit, mergeBase, StringComparison.Ordinal)
            || !string.Equals(witness.HeadCommit, head, StringComparison.Ordinal)
            || !string.Equals(witness.Verification, "verified", StringComparison.Ordinal))
        {
            Add(diagnostics, "release-prep-package-witness-invalid", "The PackageIndex witness identity does not match this diff.",
                $"Witness identity baseRef={witness.BaseRef}, baseTip={witness.BaseTipCommit}, mergeBase={witness.MergeBaseCommit}, head={witness.HeadCommit}; verifier resolved baseRef={baseRef}, baseTip={baseTip}, mergeBase={mergeBase}, head={head}.",
                "Regenerate the witness from this exact checkout and fetched base ref.");
            return;
        }

        var changedSources = changes.Where(change => change.Path is ManifestPath or GuidanceTemplatePath).Select(change => change.Path).ToHashSet(StringComparer.Ordinal);
        var changedOutputs = changes.Where(change => IsGeneratedPackageSurface(change.Path)).Select(change => change.Path).ToHashSet(StringComparer.Ordinal);
        var witnessInputs = witness.ChangedInputs.ToDictionary(input => input.Path, StringComparer.Ordinal);
        if (!changedSources.SetEquals(witnessInputs.Keys))
        {
            Add(diagnostics, "release-prep-package-witness-mismatch", "The witness changed-input set does not match the complete diff.",
                $"Diff sources [{string.Join(", ", changedSources.Order(StringComparer.Ordinal))}] differ from witness sources [{string.Join(", ", witnessInputs.Keys.Order(StringComparer.Ordinal))}].",
                "Regenerate package documentation and the witness from the same release-preparation branch.");
        }

        var surfaces = witness.Surfaces.ToDictionary(surface => surface.Path, StringComparer.Ordinal);
        foreach (var surface in surfaces.Values)
        {
            var authorizingInputs = witness.ChangedInputs.Where(input => input.Surfaces.Contains(surface.Path, StringComparer.Ordinal)).ToArray();
            if (authorizingInputs.Length == 0)
            {
                continue;
            }

            var baseHash = await ReadBaseSurfaceHashAsync(repositoryRoot, mergeBase, surface, cancellationToken);
            if (baseHash is null)
            {
                Add(diagnostics, "release-prep-package-witness-mismatch", "A generated package surface could not be compared with its merge-base version.",
                    $"Could not read a valid base version of '{surface.Path}'.", "Restore the generated package surface from the merge base, regenerate it, and rerun verify-prep-diff.");
                continue;
            }

            if (!string.Equals(baseHash, surface.Sha256, StringComparison.Ordinal) && !changedOutputs.Contains(surface.Path))
            {
                Add(diagnostics, "release-prep-package-surface-missing", "A changed package input did not commit every generated surface it affects.",
                    $"'{surface.Path}' differs from the witness at the merge base but is absent from the complete diff.", "Run PackageIndex generation and commit every generated package document changed by the manifest or release-guidance template.");
            }
        }

        foreach (var output in changedOutputs)
        {
            if (!surfaces.TryGetValue(output, out var surface))
            {
                Add(diagnostics, "release-prep-package-surface-without-source", "A changed package documentation surface is not declared by the witness.",
                    $"'{output}' has no generated surface entry.", "Regenerate package documentation from packages/package-index.yml or the release-guidance template.");
                continue;
            }

            var authorizingInputs = witness.ChangedInputs.Where(input => input.Surfaces.Contains(output, StringComparer.Ordinal)).ToArray();
            if (authorizingInputs.Length == 0)
            {
                Add(diagnostics, "release-prep-package-surface-without-source", "A changed package documentation surface has no changed semantic source.",
                    $"'{output}' is present in the witness but no changed input authorizes it.", "Change the mapped manifest/template input and regenerate the surface, or remove the generated-file edit.");
                continue;
            }

            var fullPath = Path.Join(repositoryRoot, output.Replace('/', Path.DirectorySeparatorChar));
            if (surface.Kind == "managed-readme")
            {
                var baseContent = await ReadGitFileAsync(repositoryRoot, mergeBase, output, cancellationToken);
                var headContent = await File.ReadAllTextAsync(fullPath, cancellationToken);
                string? issue = null;
                var managedReadmeMatches = baseContent is not null
                    && TryValidateManagedReadme(baseContent, headContent, surface.Sha256, out issue);
                if (!managedReadmeMatches)
                {
                    Add(diagnostics, "release-prep-package-witness-mismatch", "A managed package README changed outside its generated release-guidance body or does not match the witness.",
                        issue ?? $"Could not read base README '{output}'.", "Restore every byte outside the marker body and regenerate the managed region with PackageIndex.");
                }
            }
            else if (!File.Exists(fullPath) || !string.Equals(ComputeSha256(await File.ReadAllTextAsync(fullPath, cancellationToken)), surface.Sha256, StringComparison.Ordinal))
            {
                Add(diagnostics, "release-prep-package-witness-mismatch", "A generated package document does not match the witness digest.",
                    $"'{output}' does not hash to the declared SHA-256 value.", "Run PackageIndex generation, inspect the diff, and rerun verify-prep-diff.");
            }
        }

        foreach (var input in witness.ChangedInputs.Where(input => !input.Surfaces.Any(changedOutputs.Contains)))
        {
            Add(diagnostics, "release-prep-package-surface-without-source", "A changed package source did not produce a changed generated surface.",
                $"'{input.Path}' authorizes no changed package output in this diff.", "Remove the unrelated source edit or regenerate and commit each affected package surface.");
        }
    }

    private async Task<string?> ReadBaseSurfaceHashAsync(
        string repositoryRoot,
        string mergeBase,
        ReleasePreparationWitnessSurfaceDocument surface,
        CancellationToken cancellationToken)
    {
        var baseContent = await ReadGitFileAsync(repositoryRoot, mergeBase, surface.Path, cancellationToken);
        if (baseContent is null)
        {
            return null;
        }

        if (surface.Kind != "managed-readme")
        {
            return ComputeSha256(baseContent);
        }

        return TryGetManagedBody(baseContent, out _, out _, out var body)
            ? ComputeSha256(body)
            : null;
    }

    internal static bool TryParseWitness(string json, out ReleasePreparationWitnessDocument? witness, out string issue)
    {
        witness = null;
        issue = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                issue = "Witness JSON root must be an object.";
                return false;
            }

            if (!TryGetObject(document.RootElement, ["schema", "baseRef", "baseTipCommit", "mergeBaseCommit", "headCommit", "verification", "changedInputs", "surfaces"], out var root, out issue)
                || !TryGetRequiredString(root, "schema", out var schema, out issue)
                || !TryGetRequiredString(root, "baseRef", out var baseRef, out issue)
                || !TryGetRequiredString(root, "baseTipCommit", out var baseTip, out issue)
                || !TryGetRequiredString(root, "mergeBaseCommit", out var mergeBase, out issue)
                || !TryGetRequiredString(root, "headCommit", out var head, out issue)
                || !TryGetRequiredString(root, "verification", out var verification, out issue)
                || !TryGetArray(root, "changedInputs", out var changedInputs, out issue)
                || !TryGetArray(root, "surfaces", out var surfaces, out issue))
            {
                return false;
            }

            var inputs = new List<ReleasePreparationWitnessInputDocument>();
            foreach (var element in changedInputs.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !TryGetObject(element, ["kind", "path", "surfaces"], out var input, out issue)
                    || !TryGetRequiredString(input, "kind", out var kind, out issue)
                    || !TryGetRequiredString(input, "path", out var path, out issue)
                    || !TryGetArray(input, "surfaces", out var inputSurfaces, out issue))
                {
                    return false;
                }

                var paths = new List<string>();
                foreach (var surface in inputSurfaces.EnumerateArray())
                {
                    if (surface.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(surface.GetString()))
                    {
                        issue = "Witness changed-input surfaces must be non-empty strings.";
                        return false;
                    }

                    paths.Add(surface.GetString()!);
                }

                inputs.Add(new ReleasePreparationWitnessInputDocument(kind, path, paths));
            }

            var outputSurfaces = new List<ReleasePreparationWitnessSurfaceDocument>();
            foreach (var element in surfaces.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !TryGetObject(element, ["kind", "path", "sha256"], out var surface, out issue)
                    || !TryGetRequiredString(surface, "kind", out var kind, out issue)
                    || !TryGetRequiredString(surface, "path", out var path, out issue)
                    || !TryGetRequiredString(surface, "sha256", out var sha256, out issue))
                {
                    return false;
                }

                outputSurfaces.Add(new ReleasePreparationWitnessSurfaceDocument(kind, path, sha256));
            }

            if (!GitCommit.IsMatch(baseRef)
                || !GitCommit.IsMatch(baseTip)
                || !GitCommit.IsMatch(mergeBase)
                || !GitCommit.IsMatch(head))
            {
                issue = "Witness commit identities must be lowercase full Git object IDs.";
                return false;
            }

            if (!ValidateWitnessOrdering(inputs, outputSurfaces, out issue))
            {
                return false;
            }

            witness = new ReleasePreparationWitnessDocument(schema, baseRef, baseTip, mergeBase, head, verification, inputs, outputSurfaces);
            return true;
        }
        catch (JsonException ex)
        {
            issue = ex.Message;
            return false;
        }
    }

    private static bool ValidateWitnessOrdering(
        IReadOnlyList<ReleasePreparationWitnessInputDocument> inputs,
        IReadOnlyList<ReleasePreparationWitnessSurfaceDocument> surfaces,
        out string issue)
    {
        issue = string.Empty;
        if (!inputs.Select(input => input.Path).SequenceEqual(inputs.Select(input => input.Path).Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || inputs.Select(input => input.Path).Distinct(StringComparer.Ordinal).Count() != inputs.Count
            || inputs.Any(input => !IsSafePath(input.Path)
                || (input.Path == ManifestPath && input.Kind != "package-index-manifest")
                || (input.Path == GuidanceTemplatePath && input.Kind != "release-guidance-template")
                || (input.Path is not ManifestPath and not GuidanceTemplatePath)
                || (input.Path == GuidanceTemplatePath && input.Surfaces.Any(path => path is ChooserPath or ReadinessPath))
                || !input.Surfaces.SequenceEqual(input.Surfaces.Order(StringComparer.Ordinal), StringComparer.Ordinal)
                || input.Surfaces.Distinct(StringComparer.Ordinal).Count() != input.Surfaces.Count
                || input.Surfaces.Any(path => !IsSafePath(path))))
        {
            issue = "Witness changedInputs contain an unsafe, duplicate, unordered, or unsupported entry.";
            return false;
        }

        var orderedSurfaces = surfaces.OrderBy(surface => surface.Path, StringComparer.Ordinal).ThenBy(surface => surface.Kind, StringComparer.Ordinal).Select(surface => (surface.Path, surface.Kind));
        if (!surfaces.Select(surface => (surface.Path, surface.Kind)).SequenceEqual(orderedSurfaces)
            || surfaces.Select(surface => surface.Path).Distinct(StringComparer.Ordinal).Count() != surfaces.Count
            || surfaces.Any(surface => !IsSafePath(surface.Path)
                || !Sha256.IsMatch(surface.Sha256)
                || (surface.Path == ChooserPath && surface.Kind != "chooser")
                || (surface.Path == ReadinessPath && surface.Kind != "readiness")
                || (surface.Path is not ChooserPath and not ReadinessPath && (surface.Kind != "managed-readme" || !surface.Path.EndsWith("/README.md", StringComparison.Ordinal)))))
        {
            issue = "Witness surfaces contain an unsafe, duplicate, unordered, unsupported, or invalid SHA-256 entry.";
            return false;
        }

        var knownSurfaces = surfaces.Select(surface => surface.Path).ToHashSet(StringComparer.Ordinal);
        if (inputs.Any(input => input.Surfaces.Any(path => !knownSurfaces.Contains(path))))
        {
            issue = "Witness changedInputs reference a generated surface that is not declared in surfaces.";
            return false;
        }

        return true;
    }

    private static bool TryGetObject(JsonElement element, IReadOnlyList<string> expectedNames, out JsonElement value, out string issue)
    {
        value = element;
        issue = string.Empty;
        var expected = expectedNames.ToHashSet(StringComparer.Ordinal);
        var names = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != expected.Count || names.Distinct(StringComparer.Ordinal).Count() != names.Length || !names.All(expected.Contains))
        {
            issue = "Witness JSON has missing, duplicate, null, or unknown properties.";
            return false;
        }

        return true;
    }

    private static bool TryGetRequiredString(JsonElement element, string name, out string value, out string issue)
    {
        value = string.Empty;
        issue = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            issue = $"Witness property '{name}' must be a non-empty string.";
            return false;
        }

        value = property.GetString()!;
        return true;
    }

    private static bool TryGetArray(JsonElement element, string name, out JsonElement value, out string issue)
    {
        value = default;
        issue = string.Empty;
        if (!element.TryGetProperty(name, out value) || value.ValueKind != JsonValueKind.Array)
        {
            issue = $"Witness property '{name}' must be an array.";
            return false;
        }

        return true;
    }

    private static bool TryValidateManagedReadme(string baseContent, string headContent, string expectedSha256, out string? issue)
    {
        issue = null;
        if (!TryGetManagedBody(baseContent, out var baseStart, out var baseEnd, out _)
            || !TryGetManagedBody(headContent, out var headStart, out var headEnd, out var headBody))
        {
            issue = "Both base and HEAD README contents must have exactly one ordered release-guidance marker pair.";
            return false;
        }

        if (!string.Equals(baseContent[..baseStart], headContent[..headStart], StringComparison.Ordinal)
            || !string.Equals(baseContent[baseEnd..], headContent[headEnd..], StringComparison.Ordinal))
        {
            issue = "Bytes outside the managed marker body changed between base and HEAD.";
            return false;
        }

        if (!string.Equals(ComputeSha256(headBody), expectedSha256, StringComparison.Ordinal))
        {
            issue = "The HEAD managed marker body does not match the witness SHA-256.";
            return false;
        }

        return true;
    }

    private static bool TryGetManagedBody(string content, out int bodyStart, out int bodyEnd, out string body)
    {
        bodyStart = 0;
        bodyEnd = 0;
        body = string.Empty;
        var begin = FindExactMarkerLines(content, ManagedBeginMarker);
        var end = FindExactMarkerLines(content, ManagedEndMarker);
        if (begin.Count != 1 || end.Count != 1 || begin[0].LineStart >= end[0].LineStart)
        {
            return false;
        }

        bodyStart = begin[0].NextLineStart;
        bodyEnd = end[0].LineStart;
        body = content[bodyStart..bodyEnd];
        return true;
    }

    private static IReadOnlyList<MarkerLine> FindExactMarkerLines(string content, string marker)
    {
        var matches = new List<MarkerLine>();
        var lineStart = 0;
        while (lineStart <= content.Length)
        {
            var lineEnd = content.IndexOf('\n', lineStart);
            var nextLineStart = lineEnd < 0 ? content.Length : lineEnd + 1;
            var lineLength = (lineEnd < 0 ? content.Length : lineEnd) - lineStart;
            if (lineLength > 0 && content[lineStart + lineLength - 1] == '\r')
            {
                lineLength--;
            }

            if (string.Equals(content.Substring(lineStart, lineLength), marker, StringComparison.Ordinal))
            {
                matches.Add(new MarkerLine(lineStart, nextLineStart));
            }

            if (lineEnd < 0)
            {
                break;
            }

            lineStart = nextLineStart;
        }

        return matches;
    }

    private async Task<string?> RequireGitValueAsync(
        IReadOnlyList<string> arguments,
        string repositoryRoot,
        List<ReleaseDiagnostic> diagnostics,
        string code,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(arguments, repositoryRoot, TimeSpan.FromMinutes(1), cancellationToken);
        var value = result.StandardOutput.Trim();
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(value))
        {
            Add(diagnostics, code, "Release preparation could not resolve required Git identity.",
                $"git {string.Join(' ', arguments)} failed: {DescribeFailure(result)}", "Fetch the base branch, ensure HEAD is checked out, and rerun verify-prep-diff.");
            return null;
        }

        return value;
    }

    private async Task<CommandResult> RunAsync(IReadOnlyList<string> arguments, string repositoryRoot, TimeSpan timeout, CancellationToken cancellationToken)
    {
        return await RunProcessAsync("git", arguments, repositoryRoot, timeout, cancellationToken);
    }

    private async Task<CommandResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string repositoryRoot,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return await _commandRunner.RunAsync(new CommandInvocation(executable, arguments, repositoryRoot, timeout), cancellationToken);
    }

    private async Task<string?> ReadGitFileAsync(string repositoryRoot, string commit, string path, CancellationToken cancellationToken)
    {
        var result = await RunAsync(["show", $"{commit}:{path}"], repositoryRoot, TimeSpan.FromMinutes(1), cancellationToken);
        return result.ExitCode == 0 ? result.StandardOutput : null;
    }

    private static bool IsPackageCandidate(ReleasePreparationChange change) =>
        change.Path is ManifestPath or GuidanceTemplatePath || IsGeneratedPackageSurface(change.Path);

    private static bool IsGeneratedPackageSurface(string path) =>
        path is ChooserPath or ReadinessPath || (path.EndsWith("/README.md", StringComparison.Ordinal) && !path.StartsWith("packages/", StringComparison.Ordinal));

    private static bool IsSafePath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !Path.IsPathRooted(path)
        && !path.Contains('\\')
        && !path.Contains('\0')
        && !path.Split('/').Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or "..");

    private static bool IsSupportedNameStatus(string status)
    {
        if (status is "A" or "D" or "M" or "T" or "U")
        {
            return true;
        }

        return status.Length > 1
            && status[0] is 'R' or 'C'
            && status[1..].All(char.IsAsciiDigit);
    }

    internal static bool TryNormalizeBaseRef(string baseRef, out string normalizedBaseRef, out string issue)
    {
        var trimmed = baseRef.Trim();
        var branch = trimmed switch
        {
            var value when value.StartsWith("refs/remotes/origin/", StringComparison.Ordinal) => value["refs/remotes/origin/".Length..],
            var value when value.StartsWith("refs/heads/", StringComparison.Ordinal) => value["refs/heads/".Length..],
            var value when value.StartsWith("origin/", StringComparison.Ordinal) => value["origin/".Length..],
            var value when !value.StartsWith("refs/", StringComparison.Ordinal) => value,
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(branch)
            || branch.StartsWith("/", StringComparison.Ordinal)
            || branch.Contains("..", StringComparison.Ordinal)
            || branch.Contains('~')
            || branch.Contains('^')
            || branch.Contains(':')
            || branch.Contains('\\')
            || branch.Split('/').Any(segment => string.IsNullOrWhiteSpace(segment) || segment == "."))
        {
            normalizedBaseRef = string.Empty;
            issue = $"'{baseRef}' is not a safe origin-tracking branch ref.";
            return false;
        }

        normalizedBaseRef = "origin/" + branch;
        issue = string.Empty;
        return true;
    }

    private static string ComputeSha256(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static string DescribeFailure(CommandResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        return $"exit {result.ExitCode}: {detail.Trim()}";
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Temporary witness cleanup must not hide the classification result.
        }
        catch (UnauthorizedAccessException)
        {
            // Temporary witness cleanup must not hide the classification result.
        }
    }

    private static void Add(List<ReleaseDiagnostic> diagnostics, string code, string problem, string cause, string fix)
    {
        diagnostics.Add(ReleaseDiagnostic.Error(code, problem, cause, fix, Documentation));
    }

    private static ReleasePreparationDiffResult Result(
        string baseRef,
        string? baseTip,
        string? mergeBase,
        string? head,
        IReadOnlyList<ReleasePreparationChange> changes,
        IReadOnlyList<ReleaseDiagnostic> diagnostics) =>
        new(baseRef, baseTip, mergeBase, head, changes, diagnostics, diagnostics.Select(diagnostic => diagnostic.Render()).ToArray());

    private sealed record MarkerLine(int LineStart, int NextLineStart);
}

/// <summary>
/// Typed report produced by <see cref="ReleasePreparationDiffVerifier"/>.
/// </summary>
internal sealed record ReleasePreparationDiffResult(
    string BaseRef,
    string? BaseTipCommit,
    string? MergeBaseCommit,
    string? HeadCommit,
    IReadOnlyList<ReleasePreparationChange> Changes,
    IReadOnlyList<ReleaseDiagnostic> Diagnostics,
    IReadOnlyList<string> Errors)
{
    /// <summary>
    /// Gets whether no blocking diff diagnostic was produced.
    /// </summary>
    internal bool IsValid => Diagnostics.All(diagnostic => !string.Equals(diagnostic.Severity, "error", StringComparison.Ordinal));
}

internal sealed record ReleasePreparationWitnessDocument(
    string Schema,
    string BaseRef,
    string BaseTipCommit,
    string MergeBaseCommit,
    string HeadCommit,
    string Verification,
    IReadOnlyList<ReleasePreparationWitnessInputDocument> ChangedInputs,
    IReadOnlyList<ReleasePreparationWitnessSurfaceDocument> Surfaces);

internal sealed record ReleasePreparationWitnessInputDocument(string Kind, string Path, IReadOnlyList<string> Surfaces);

internal sealed record ReleasePreparationWitnessSurfaceDocument(string Kind, string Path, string Sha256);
