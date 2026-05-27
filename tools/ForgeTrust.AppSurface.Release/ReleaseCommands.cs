using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Console;
using ForgeTrust.AppSurface.Core;
using Markdig;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

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

    /// <inheritdoc />
    protected override bool FailOnWarnings => FailOnWarningsOption;

    /// <inheritdoc />
    protected override bool AllowExistingTargets => AllowExistingTargetsOption;

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
