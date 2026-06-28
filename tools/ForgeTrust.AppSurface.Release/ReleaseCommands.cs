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
    [CommandOption("docs-catalog", Description = "AppSurface Docs versions.json used to verify stable release evidence. Check falls back to dist/docs/versions.json when omitted and present.")]
    public string? DocsCatalogPath { get; set; }

    /// <summary>
    /// Gets the trusted release root used to resolve catalog exactTreePath values.
    /// </summary>
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
    [CommandOption("docs-catalog", Description = "Staged AppSurface Docs versions.json used to verify stable release evidence.")]
    public string? DocsCatalogPath { get; set; }

    /// <summary>
    /// Gets the trusted release root used to resolve catalog exactTreePath values.
    /// </summary>
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
