namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Validates release readiness without mutating repository files.
/// </summary>
[Command("check", Description = "Validate release inputs, generated file targets, package policy, and release warnings.")]
internal sealed partial class ReleaseCheckCommand : ReleaseCommandBase, ICommand
{
    /// <summary>
    /// Creates the check command.
    /// </summary>
    /// <param name="executionContext">Execution context supplied by the entry point.</param>
    /// <param name="commandRunner">Runner used for Git calls.</param>
    /// <param name="clock">Clock used by shared command construction.</param>
    public ReleaseCheckCommand(ReleaseExecutionContext executionContext, ICommandRunner commandRunner, IReleaseClock clock)
        : base(executionContext, commandRunner, clock)
    {
    }

    /// <inheritdoc />
    protected override string CommandName => "check";

    /// <summary>
    /// Gets a value indicating whether check should fail on warning diagnostics.
    /// </summary>
    [CommandOption("fail-on-warnings", Description = "Return a failing exit code when check finds warning diagnostics.")]
    public bool FailOnWarningsOption { get; set; }

    /// <summary>
    /// Gets a value indicating whether check may review already-generated release artifacts.
    /// </summary>
    [CommandOption("allow-existing-targets", Description = "Allow check to review already-generated release artifacts.")]
    public bool AllowExistingTargetsOption { get; set; }

    /// <summary>
    /// Gets the AppSurface Docs version catalog used to verify stable release evidence.
    /// </summary>
    /// <remarks>
    /// <c>check</c> may omit this value to use <c>dist/docs/versions.json</c> when that local review fallback exists. Stable checks that
    /// review prepared artifacts should pass the staged catalog explicitly when possible. Relative paths are resolved from the repository
    /// root, and invalid or missing catalogs surface release diagnostics rather than mutating release files.
    /// </remarks>
    [CommandOption("docs-catalog", Description = "AppSurface Docs versions.json used to verify stable release evidence. Check falls back to dist/docs/versions.json when omitted and present.")]
    public string? DocsCatalogPath { get; set; }

    /// <summary>
    /// Gets the trusted release root used to resolve catalog exactTreePath values.
    /// </summary>
    /// <remarks>
    /// When omitted, the verifier uses the catalog directory as the trusted release root. Pass this option when the catalog is staged outside
    /// the directory that contains the exact tree paths. The root must be an ordinary directory, and catalog <c>exactTreePath</c> values must
    /// stay relative to it without hidden or parent segments.
    /// </remarks>
    [CommandOption("docs-trusted-release-root", Description = "Trusted release root that contains docs exact trees. Defaults to the docs catalog directory.")]
    public string? DocsTrustedReleaseRootPath { get; set; }

    /// <inheritdoc />
    protected override bool FailOnWarnings => FailOnWarningsOption;

    /// <inheritdoc />
    protected override bool AllowExistingTargets => AllowExistingTargetsOption;

    /// <inheritdoc />
    protected override string? ResolveDocsCatalogPath(string repoRoot)
    {
        return string.IsNullOrWhiteSpace(DocsCatalogPath)
            ? null
            : Path.GetFullPath(DocsCatalogPath, repoRoot);
    }

    /// <inheritdoc />
    protected override string? ResolveDocsTrustedReleaseRootPath(string repoRoot)
    {
        return string.IsNullOrWhiteSpace(DocsTrustedReleaseRootPath)
            ? null
            : Path.GetFullPath(DocsTrustedReleaseRootPath, repoRoot);
    }

    /// <inheritdoc />
    public ValueTask ExecuteAsync(IConsole console)
    {
        return ExecuteWithDiagnosticsAsync(console, async (options, cancellationToken) =>
        {
            var services = CreateServices(options);
            var report = await services.Checker.CheckAsync(options, cancellationToken);
            var rendered = ReleaseReportRenderer.RenderCheck(report);
            await WriteReportAsync(options, rendered, console.Output, cancellationToken);
            return report.HasErrors || (options.FailOnWarnings && report.Warnings.Count > 0) ? 1 : 0;
        });
    }
}

/// <summary>
/// Generates the coordinated release pull request payload.
/// </summary>
[Command("prepare", Description = "Generate release notes, sidecar metadata, changelog rollover, package note paths, and manifest.")]
internal sealed partial class ReleasePrepareCommand : ReleaseCommandBase, ICommand
{
    /// <summary>
    /// Creates the prepare command.
    /// </summary>
    /// <param name="executionContext">Execution context supplied by the entry point.</param>
    /// <param name="commandRunner">Runner used for Git calls.</param>
    /// <param name="clock">Clock used for default release dates.</param>
    public ReleasePrepareCommand(ReleaseExecutionContext executionContext, ICommandRunner commandRunner, IReleaseClock clock)
        : base(executionContext, commandRunner, clock)
    {
    }

    /// <inheritdoc />
    protected override string CommandName => "prepare";

    /// <inheritdoc />
    public ValueTask ExecuteAsync(IConsole console)
    {
        return ExecuteWithDiagnosticsAsync(console, async (options, cancellationToken) =>
        {
            var services = CreateServices(options);
            var result = await services.Preparation.PrepareAsync(options, cancellationToken);
            var rendered = ReleaseReportRenderer.RenderPreparation(result);
            await WriteReportAsync(options, rendered, console.Output, cancellationToken);
            return result.Check.HasErrors ? 1 : 0;
        });
    }
}

/// <summary>
/// Validates tag state and emits GitHub Release workflow outputs.
/// </summary>
[Command("publish", Description = "Validate an annotated tag and emit structured GitHub Release workflow outputs.")]
internal sealed partial class ReleasePublishCommand : ReleaseCommandBase, ICommand
{
    /// <summary>
    /// Creates the publish command.
    /// </summary>
    /// <param name="executionContext">Execution context supplied by the entry point.</param>
    /// <param name="commandRunner">Runner used for Git and GitHub CLI calls.</param>
    /// <param name="clock">Clock used by shared command construction.</param>
    public ReleasePublishCommand(ReleaseExecutionContext executionContext, ICommandRunner commandRunner, IReleaseClock clock)
        : base(executionContext, commandRunner, clock)
    {
    }

    /// <summary>
    /// Gets the annotated release tag to publish.
    /// </summary>
    [CommandOption("tag", Description = "Annotated tag to publish. Must match --version with a leading v.")]
    public string? Tag { get; set; }

    /// <summary>
    /// Gets an optional GitHub Actions output file.
    /// </summary>
    [CommandOption("github-output", Description = "Optional GITHUB_OUTPUT file for publish workflow outputs.")]
    public string? GitHubOutputPath { get; set; }

    /// <summary>
    /// Gets the branch that must contain the annotated tag commit.
    /// </summary>
    /// <remarks>
    /// Publish defaults to <c>main</c>. Use this option when a maintained release branch, such as
    /// <c>release/0.1.0</c>, owns the tag provenance for a release. The command accepts branch names and branch refs
    /// shaped as <c>origin/&lt;branch&gt;</c>, <c>refs/heads/&lt;branch&gt;</c>, or
    /// <c>refs/remotes/origin/&lt;branch&gt;</c>, then normalizes them before validation fetches and checks
    /// <c>origin/&lt;branch&gt;</c>. Tags, SHAs, empty branch names, and unsupported refs such as <c>refs/tags/v1.2.3</c>
    /// are invalid because publish validation must prove protected branch reachability.
    /// </remarks>
    [CommandOption("base-ref", Description = "Branch name or supported branch ref (origin/<branch>, refs/heads/<branch>, refs/remotes/origin/<branch>) that must contain the annotated tag commit. Defaults to main; tags and SHAs are invalid.")]
    public string? BaseRef { get; set; }

    /// <summary>
    /// Gets the staged AppSurface Docs version catalog used to verify stable release evidence.
    /// </summary>
    /// <remarks>
    /// Stable publish accepts this path for local diagnostics, but the public release workflow creates its staged docs artifact through
    /// <c>docs-publication</c>. Relative paths are resolved from the repository root. Prerelease publish accepts the option but does not
    /// require docs archive proof.
    /// </remarks>
    [CommandOption("docs-catalog", Description = "Staged AppSurface Docs versions.json used to verify stable release evidence.")]
    public string? DocsCatalogPath { get; set; }

    /// <summary>
    /// Gets the trusted release root used to resolve catalog exactTreePath values.
    /// </summary>
    /// <remarks>
    /// When omitted, stable publish resolves exact trees relative to the staged catalog directory. Supply this option when the artifact layout
    /// stores <c>versions.json</c> separately from the exact release trees; the path must resolve under the repository root when relative and
    /// must point at the ordinary directory that owns the catalog exact-tree paths.
    /// </remarks>
    [CommandOption("docs-trusted-release-root", Description = "Trusted release root that contains docs exact trees. Defaults to the docs catalog directory.")]
    public string? DocsTrustedReleaseRootPath { get; set; }

    /// <inheritdoc />
    protected override string CommandName => "publish";

    /// <inheritdoc />
    protected override string? ResolveTag(SemVer version)
    {
        if (string.IsNullOrWhiteSpace(Tag))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-tag-required",
                "Publishing requires an explicit tag.",
                "The release-publish workflow must bind GitHub Release content to one annotated tag.",
                "Pass `--tag v<version>` and create the annotated tag outside this tool.",
                "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
        }

        if (!string.Equals(Tag, version.TagName, StringComparison.Ordinal))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-tag-version-mismatch",
                "The requested tag does not match the requested version.",
                $"`--version {version}` maps to tag `{version.TagName}`, but the command received `{Tag}`.",
                "Use matching `--version` and `--tag` values so package artifacts, notes, and GitHub Release identity cannot split.",
                "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
        }

        return Tag;
    }

    /// <inheritdoc />
    protected override string? ResolveGitHubOutputPath(string repoRoot)
    {
        return string.IsNullOrWhiteSpace(GitHubOutputPath)
            ? null
            : Path.GetFullPath(GitHubOutputPath, repoRoot);
    }

    /// <inheritdoc />
    protected override string ResolveBaseRef()
    {
        return NormalizeBaseRef(BaseRef);
    }

    /// <inheritdoc />
    protected override string? ResolveDocsCatalogPath(string repoRoot)
    {
        return string.IsNullOrWhiteSpace(DocsCatalogPath)
            ? null
            : Path.GetFullPath(DocsCatalogPath, repoRoot);
    }

    /// <inheritdoc />
    protected override string? ResolveDocsTrustedReleaseRootPath(string repoRoot)
    {
        return string.IsNullOrWhiteSpace(DocsTrustedReleaseRootPath)
            ? null
            : Path.GetFullPath(DocsTrustedReleaseRootPath, repoRoot);
    }

    private static string NormalizeBaseRef(string? baseRef)
    {
        if (string.IsNullOrWhiteSpace(baseRef))
        {
            return "main";
        }

        var normalized = baseRef.Trim();
        const string remoteBranchPrefix = "refs/remotes/origin/";
        const string branchPrefix = "refs/heads/";
        const string originPrefix = "origin/";

        if (normalized.StartsWith(remoteBranchPrefix, StringComparison.Ordinal))
        {
            return ValidateNormalizedBaseRef(normalized[remoteBranchPrefix.Length..], baseRef);
        }

        if (normalized.StartsWith(branchPrefix, StringComparison.Ordinal))
        {
            return ValidateNormalizedBaseRef(normalized[branchPrefix.Length..], baseRef);
        }

        if (normalized.StartsWith(originPrefix, StringComparison.Ordinal))
        {
            return ValidateNormalizedBaseRef(normalized[originPrefix.Length..], baseRef);
        }

        return ValidateNormalizedBaseRef(normalized, baseRef);
    }

    private static string ValidateNormalizedBaseRef(string normalizedBaseRef, string originalBaseRef)
    {
        if (string.IsNullOrWhiteSpace(normalizedBaseRef)
            || normalizedBaseRef.StartsWith("refs/", StringComparison.Ordinal)
            || IsFullObjectId(normalizedBaseRef)
            || !IsValidBranchRef(normalizedBaseRef))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-base-ref-invalid",
                "The requested base ref is not a supported branch ref.",
                $"`--base-ref {originalBaseRef}` normalizes to `{normalizedBaseRef}`.",
                "Pass a branch name, `origin/<branch>`, `refs/heads/<branch>`, or `refs/remotes/origin/<branch>`. Do not pass tags, full object IDs, empty refs, or unsupported refs.",
                "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
        }

        return normalizedBaseRef;
    }

    private static bool IsFullObjectId(string value)
    {
        if (value.Length is not (40 or 64))
        {
            return false;
        }

        return value.All(IsHexObjectIdCharacter);
    }

    private static bool IsHexObjectIdCharacter(char character)
    {
        return (character >= '0' && character <= '9')
            || (character >= 'a' && character <= 'f')
            || (character >= 'A' && character <= 'F');
    }

    private static bool IsValidBranchRef(string value)
    {
        if (value is "@"
            || value.StartsWith("/", StringComparison.Ordinal)
            || value.StartsWith(".", StringComparison.Ordinal)
            || value.StartsWith("-", StringComparison.Ordinal)
            || value.EndsWith("/", StringComparison.Ordinal)
            || value.EndsWith(".", StringComparison.Ordinal)
            || value.Contains("//", StringComparison.Ordinal)
            || value.Contains("..", StringComparison.Ordinal)
            || value.Contains("@{", StringComparison.Ordinal)
            || !value.All(IsValidBranchRefCharacter))
        {
            return false;
        }

        return value
            .Split('/')
            .All(component => !component.StartsWith(".", StringComparison.Ordinal)
                && !component.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsValidBranchRefCharacter(char character)
    {
        return character > ' '
            && character != '\u007f'
            && character is not ('~' or '^' or ':' or '?' or '*' or '[' or '\\');
    }

    /// <inheritdoc />
    public ValueTask ExecuteAsync(IConsole console)
    {
        return ExecuteWithDiagnosticsAsync(console, async (options, cancellationToken) =>
        {
            var services = CreateServices(options);
            var publish = await services.Publishing.PublishAsync(options, cancellationToken);
            await services.Publishing.WriteOutputsAsync(publish, options, cancellationToken);
            await console.Output.WriteLineAsync(JsonSerializer.Serialize(publish, ReleaseJson.Options));
            return 0;
        });
    }
}

/// <summary>
/// Creates AppSurface Docs publication artifacts for the release publish workflow.
/// </summary>
/// <remarks>
/// This command is the maintainer-facing CLI seam for the public release docs trust path. It expects docs to have already been
/// exported for the annotated tag commit and produces the deterministic archive, digest ledger, Pages staging payload, catalog entry,
/// publication plan, and recovery summary consumed by <c>release-publish.yml</c>. Use it when the release workflow needs a durable
/// GitHub Release asset and a Pages catalog update from the same tag-bound exact tree. Do not use it to publish arbitrary local docs:
/// the tag must match <c>--version</c>, <c>--docs-exact-tree</c> must be an ordinary exported tree with the release manifest, and
/// <c>--pages-staging-root</c> is deleted before the merged payload is written. Disable <c>--promote-recommended</c> only for a
/// documented recovery or prerelease-style staging run where the current stable recommendation must remain unchanged.
/// </remarks>
[Command("docs-publication", Description = "Create release docs archive, catalog, Pages staging, digest ledger, and recovery summary.")]
internal sealed partial class ReleaseDocsPublicationCommand : ReleaseCommandBase, ICommand
{
    /// <summary>
    /// Creates the docs publication command.
    /// </summary>
    /// <param name="executionContext">Execution context supplied by the entry point.</param>
    /// <param name="commandRunner">Unused command runner kept for shared command construction.</param>
    /// <param name="clock">Clock used by shared command construction.</param>
    public ReleaseDocsPublicationCommand(ReleaseExecutionContext executionContext, ICommandRunner commandRunner, IReleaseClock clock)
        : base(executionContext, commandRunner, clock)
    {
    }

    /// <summary>
    /// Gets the annotated release tag that owns the docs publication.
    /// </summary>
    /// <remarks>
    /// This option is required and must be the canonical <c>v{version}</c> tag. A mismatch fails before any staging directory is reset.
    /// </remarks>
    [CommandOption("tag", Description = "Annotated tag being published. Must match --version with a leading v.")]
    public string? Tag { get; set; }

    /// <summary>
    /// Gets the exported docs exact tree for the tag.
    /// </summary>
    /// <remarks>
    /// The exact tree must be a completed AppSurface Docs export for the tag commit and must contain
    /// <c>.appsurface-docs-release-manifest.json</c>. The planner rejects hidden repository-relative paths, generated output paths under
    /// this tree, and reparse-point entries so archive bytes come only from ordinary exported files.
    /// </remarks>
    [CommandOption("docs-exact-tree", Description = "Exported AppSurface Docs exact tree for the tag.")]
    public string? DocsExactTreePath { get; set; }

    /// <summary>
    /// Gets the optional current Pages payload to copy before adding the immutable release tree.
    /// </summary>
    /// <remarks>
    /// When supplied, this directory must exist. Use it to preserve existing <c>versions.json</c>, <c>/docs</c>, and prior
    /// <c>releases/*</c> content before the new release exact tree is copied into the staging root.
    /// </remarks>
    [CommandOption("existing-pages-root", Description = "Optional existing Pages payload to preserve before adding releases/{version}.")]
    public string? ExistingPagesRootPath { get; set; }

    /// <summary>
    /// Gets the tar.gz archive output path.
    /// </summary>
    /// <remarks>
    /// The command writes this file and a sibling <c>.sha256</c> file. The path and its temporary <c>.tar</c> sibling must be outside
    /// the exact tree so the archive cannot include its own generated bytes.
    /// </remarks>
    [CommandOption("archive-output", Description = "Output path for appsurface-docs-v{version}.tar.gz.")]
    public string? ArchiveOutputPath { get; set; }

    /// <summary>
    /// Gets the Pages staging root output path.
    /// </summary>
    /// <remarks>
    /// This directory is destructive scratch space: it is deleted and recreated before existing Pages content and the new
    /// <c>releases/{version}/</c> tree are copied. It must not overlap the repository, exact tree, existing Pages root, archive,
    /// publication plan, or recovery summary paths.
    /// </remarks>
    [CommandOption("pages-staging-root", Description = "Output directory for the verified Pages payload.")]
    public string? PagesStagingRootPath { get; set; }

    /// <summary>
    /// Gets the publication plan JSON output path.
    /// </summary>
    /// <remarks>
    /// The plan is the machine-readable artifact handoff between docs archive creation, Pages deployment, public verification, and
    /// release promotion. Store it outside the exact tree and staging root.
    /// </remarks>
    [CommandOption("plan-output", Description = "Output path for the docs publication plan JSON.")]
    public string? PlanOutputPath { get; set; }

    /// <summary>
    /// Gets the optional recovery summary output path.
    /// </summary>
    /// <remarks>
    /// The summary contains exact resume, publish, and abort commands for partial failures. Store it outside the exact tree and staging
    /// root so it cannot be served as release docs content.
    /// </remarks>
    [CommandOption("summary-output", Description = "Output path for the maintainer recovery summary.")]
    public string? SummaryOutputPath { get; set; }

    /// <summary>
    /// Gets the optional release evidence docs manifest digest that the exact tree must match.
    /// </summary>
    /// <remarks>
    /// Stable release workflows pass this from tag-bound release evidence. A mismatch means the exported docs tree does not match the
    /// reviewed evidence and must be regenerated from the annotated tag commit.
    /// </remarks>
    [CommandOption("expected-release-manifest-sha256", Description = "Expected exact-tree release manifest SHA-256 from release evidence.")]
    public string? ExpectedReleaseManifestSha256 { get; set; }

    /// <summary>
    /// Gets whether stable docs publication should promote the version to recommendedVersion.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>true</c>. The command rejects values other than <c>true</c> or <c>false</c>. Passing <c>false</c> keeps the
    /// existing recommendation and is intended for prereleases or explicit recovery where maintainers do not want to change the stable
    /// docs pointer.
    /// </remarks>
    [CommandOption("promote-recommended", Description = "Promote a stable release to recommendedVersion. Defaults to true.")]
    public string PromoteRecommendedText { get; set; } = "true";

    /// <summary>
    /// Gets an optional GitHub Actions output file.
    /// </summary>
    /// <remarks>
    /// When supplied, the command appends scalar outputs such as archive name, digest, catalog path, exact tree path, and recovery
    /// summary path using GitHub Actions output-file syntax. The option must name a file, not a root directory.
    /// </remarks>
    [CommandOption("github-output", Description = "Optional GITHUB_OUTPUT file for docs publication outputs.")]
    public string? GitHubOutputPath { get; set; }

    /// <inheritdoc />
    protected override string CommandName => "docs-publication";

    /// <inheritdoc />
    protected override string? ResolveTag(SemVer version)
    {
        if (string.IsNullOrWhiteSpace(Tag))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-docs-publication-tag-required",
                "Docs publication requires an explicit tag.",
                "The publication plan must bind the docs archive and catalog to one annotated release tag.",
                "Pass `--tag v<version>` using the same tag validated by release publish.",
                "tools/ForgeTrust.AppSurface.Release/README.md#docs-publication"));
        }

        return Tag;
    }

    /// <inheritdoc />
    protected override string? ResolveGitHubOutputPath(string repoRoot)
    {
        return string.IsNullOrWhiteSpace(GitHubOutputPath)
            ? null
            : Path.GetFullPath(GitHubOutputPath, repoRoot);
    }

    /// <inheritdoc />
    public ValueTask ExecuteAsync(IConsole console)
    {
        return ExecuteWithDiagnosticsAsync(console, async (options, cancellationToken) =>
        {
            var request = new DocsPublicationRequest(
                options.Version,
                options.Tag!,
                ResolveRequiredPath(options.RepositoryRoot, DocsExactTreePath, "release-docs-publication-exact-tree-required", "--docs-exact-tree"),
                ResolveOptionalPath(options.RepositoryRoot, ExistingPagesRootPath),
                ResolveRequiredPath(options.RepositoryRoot, ArchiveOutputPath, "release-docs-publication-archive-output-required", "--archive-output"),
                ResolveRequiredPath(options.RepositoryRoot, PagesStagingRootPath, "release-docs-publication-pages-staging-required", "--pages-staging-root"),
                ResolveRequiredPath(options.RepositoryRoot, PlanOutputPath, "release-docs-publication-plan-output-required", "--plan-output"),
                ResolveOptionalPath(options.RepositoryRoot, SummaryOutputPath),
                ExpectedReleaseManifestSha256,
                ResolvePromoteRecommended());
            var publication = new ReleaseDocsPublication(new ReleaseWorkspace(options.RepositoryRoot));
            var plan = await publication.CreateAsync(request, cancellationToken);
            await ReleaseDocsPublication.WriteOutputsAsync(plan, options.GitHubOutputPath, cancellationToken);
            await console.Output.WriteLineAsync(JsonSerializer.Serialize(plan, ReleaseJson.Options));
            return 0;
        });
    }

    private static string ResolveRequiredPath(string repoRoot, string? value, string code, string option)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                code,
                $"Docs publication requires `{option}`.",
                "The release workflow did not provide every artifact path needed to build the public docs trust path.",
                $"Pass `{option} <path>` from the release workflow staging directory.",
                "tools/ForgeTrust.AppSurface.Release/README.md#docs-publication"));
        }

        return Path.GetFullPath(value, repoRoot);
    }

    private static string? ResolveOptionalPath(string repoRoot, string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : Path.GetFullPath(value, repoRoot);
    }

    private bool ResolvePromoteRecommended()
    {
        if (bool.TryParse(PromoteRecommendedText, out var promoteRecommended))
        {
            return promoteRecommended;
        }

        throw new ReleaseToolException(ReleaseDiagnostic.Error(
            "release-docs-publication-promote-recommended-invalid",
            "Docs publication received an invalid recommended-version promotion value.",
            $"`--promote-recommended {PromoteRecommendedText}` is not `true` or `false`.",
            "Pass `--promote-recommended true` for stable releases unless performing a documented manual recovery.",
            "tools/ForgeTrust.AppSurface.Release/README.md#docs-publication"));
    }
}
