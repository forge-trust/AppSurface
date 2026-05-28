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
/// Validates tag state and produces GitHub Release workflow outputs.
/// </summary>
internal sealed class ReleasePublishing
{
    private readonly ReleaseWorkspace _workspace;
    private readonly ICommandRunner _commandRunner;

    /// <summary>
    /// Creates release publishing validation workflow.
    /// </summary>
    /// <param name="workspace">Repository workspace paths.</param>
    /// <param name="commandRunner">Process runner.</param>
    internal ReleasePublishing(ReleaseWorkspace workspace, ICommandRunner commandRunner)
    {
        _workspace = workspace;
        _commandRunner = commandRunner;
    }

    /// <summary>
    /// Validates an existing annotated tag and extracts release notes from the tag commit.
    /// </summary>
    /// <param name="options">Publish command options. The version and tag must match, and stable versions are blocked until stable package publishing is protected.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Structured workflow outputs for GitHub Release creation.</returns>
    /// <remarks>
    /// The publish path is create-only: it verifies annotated tag shape, reachability from <c>origin/main</c>, prerelease package publication,
    /// absence of an existing GitHub Release, and presence of <c>releases/v{version}.md</c> in the tag commit. The method writes the tag's
    /// release note to a temporary file so workflows can pass a stable notes path to GitHub's release action.
    /// </remarks>
    internal async Task<PublishOutputs> PublishAsync(ReleaseOptions options, CancellationToken cancellationToken)
    {
        if (options.Version.IsStable)
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-stable-package-policy-missing",
                "Stable GitHub Release publishing is blocked.",
                "The release cockpit has no verifiable protected stable NuGet publish gate yet.",
                "Ship a prerelease, or add and protect a stable package publish path plus release-cockpit validation before publishing a stable tag.",
                "tools/ForgeTrust.AppSurface.Release/README.md#stable-release-policy"));
        }

        var tag = options.Tag ?? options.Version.TagName;
        await ValidateAnnotatedTagAsync(tag, cancellationToken);
        var tagCommit = await RequireCommandOutputAsync("git", ["rev-parse", $"refs/tags/{tag}^{{commit}}"], "release-tag-commit-missing", cancellationToken);
        await ValidateReachableFromMainAsync(tag, tagCommit, cancellationToken);
        await ValidatePackagePublishingSucceededAsync(options.Version, tag, tagCommit, cancellationToken);
        await ValidateGitHubReleaseDoesNotExistAsync(tag, cancellationToken);

        var notePathInTag = $"releases/v{options.Version}.md";
        var note = await RequireCommandOutputAsync("git", ["show", $"{tag}:{notePathInTag}"], "release-note-missing-from-tag", cancellationToken);
        var safeTagSegment = Path.GetFileName(tag);
        if (string.IsNullOrWhiteSpace(safeTagSegment))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-tag-invalid-temp-path",
                $"Tag '{tag}' cannot be used for release output paths.",
                "The tag does not contain a file-name-safe segment for the temporary release notes path.",
                "Use the canonical release tag form `v{version}` and retry.",
                "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
        }

        var tempDirectory = Path.Join(Path.GetTempPath(), "appsurface-release", safeTagSegment);
        Directory.CreateDirectory(tempDirectory);
        var notesFile = Path.Join(tempDirectory, "release-notes.md");
        await File.WriteAllTextAsync(notesFile, note, cancellationToken);

        return new PublishOutputs(
            options.Version.ToString(),
            tag,
            tagCommit,
            notePathInTag,
            notesFile,
            options.Version.IsStable ? "stable" : "prerelease",
            !options.Version.IsStable,
            options.DryRun);
    }

    /// <summary>
    /// Writes publish outputs to a GitHub Actions output file when requested.
    /// </summary>
    /// <param name="outputs">Publish outputs.</param>
    /// <param name="options">Release command options. <see cref="ReleaseOptions.GitHubOutputPath"/> must be a file path, not a root directory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Scalar outputs use <c>name=value</c>. Multiline outputs use GitHub's delimiter form. Existing files are appended to match
    /// <c>GITHUB_OUTPUT</c> behavior.
    /// </remarks>
    internal async Task WriteOutputsAsync(PublishOutputs outputs, ReleaseOptions options, CancellationToken cancellationToken)
    {
        if (options.GitHubOutputPath is null)
        {
            return;
        }

        var outputDirectory = Path.GetDirectoryName(options.GitHubOutputPath);
        if (string.IsNullOrEmpty(outputDirectory))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-github-output-path-invalid",
                "The GitHub output path must be a file path, not a root directory.",
                $"`--github-output {options.GitHubOutputPath}` does not include a parent directory.",
                "Pass a file path such as `$GITHUB_OUTPUT` or `artifacts/release-output.txt`.",
                "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
        }

        Directory.CreateDirectory(outputDirectory);
        var builder = new StringBuilder();
        AppendOutput(builder, "version", outputs.Version);
        AppendOutput(builder, "tag", outputs.Tag);
        AppendOutput(builder, "tag_commit", outputs.TagCommit);
        AppendOutput(builder, "note_path", outputs.NotePath);
        AppendOutput(builder, "notes_file", outputs.NotesFile);
        AppendOutput(builder, "release_classification", outputs.ReleaseClassification);
        AppendOutput(builder, "prerelease", outputs.Prerelease ? "true" : "false");
        await File.AppendAllTextAsync(options.GitHubOutputPath, builder.ToString(), cancellationToken);
    }

    private async Task ValidateAnnotatedTagAsync(string tag, CancellationToken cancellationToken)
    {
        var tagType = await RequireCommandOutputAsync("git", ["cat-file", "-t", $"refs/tags/{tag}"], "release-tag-missing", cancellationToken);
        if (!string.Equals(tagType.Trim(), "tag", StringComparison.Ordinal))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-tag-lightweight",
                $"Tag '{tag}' is not annotated.",
                $"`git cat-file -t refs/tags/{tag}` returned `{tagType.Trim()}`.",
                "Create an annotated tag with `git tag -a` so the release has stable provenance.",
                "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
        }
    }

    private async Task ValidateReachableFromMainAsync(string tag, string tagCommit, CancellationToken cancellationToken)
    {
        var result = await _commandRunner.RunAsync(
            new CommandInvocation("git", ["merge-base", "--is-ancestor", tagCommit.Trim(), "origin/main"], _workspace.RepositoryRoot),
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-tag-unreachable-from-main",
                $"Tag '{tag}' does not point at a commit reachable from origin/main.",
                result.StandardError.Length == 0 ? $"Commit {tagCommit.Trim()} is not an ancestor of origin/main." : result.StandardError.Trim(),
                "Fetch `origin/main`, move the tag only through the normal protected process, and retry.",
                "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
        }
    }

    private async Task ValidateGitHubReleaseDoesNotExistAsync(string tag, CancellationToken cancellationToken)
    {
        var result = await _commandRunner.RunAsync(
            new CommandInvocation("gh", ["release", "view", tag, "--json", "url"], _workspace.RepositoryRoot),
            cancellationToken);
        if (result.ExitCode == 0)
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-github-release-exists",
                $"GitHub Release '{tag}' already exists.",
                "Release publishing is create-only in v1 to avoid changing already-public notes by accident.",
                "Delete the incorrect draft/release manually or cut a new tag; this tool does not update existing releases.",
                "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
        }
    }

    private async Task ValidatePackagePublishingSucceededAsync(SemVer version, string tag, string tagCommit, CancellationToken cancellationToken)
    {
        if (version.IsStable)
        {
            return;
        }

        var result = await _commandRunner.RunAsync(
            new CommandInvocation(
                "gh",
                [
                    "run",
                    "list",
                    "--workflow",
                    "nuget-prerelease-publish.yml",
                    "--commit",
                    tagCommit.Trim(),
                    "--json",
                    "conclusion,headBranch,status,url",
                    "--jq",
                    $"[.[] | select(.headBranch == \"{tag}\" and .status == \"completed\" and .conclusion == \"success\")][0].url // \"\""
                ],
                _workspace.RepositoryRoot),
            cancellationToken);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-prerelease-packages-not-published",
                $"Prerelease packages have not been published for tag '{tag}'.",
                result.ExitCode == 0
                    ? $"No successful `nuget-prerelease-publish.yml` run was found for {tag} at {tagCommit.Trim()}."
                    : (string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput.Trim() : result.StandardError.Trim()),
                "Wait for the protected NuGet prerelease publish workflow for this tag to complete successfully, then retry GitHub Release publishing.",
                "tools/ForgeTrust.AppSurface.Release/README.md#stable-release-policy"));
        }
    }

    private async Task<string> RequireCommandOutputAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string code,
        CancellationToken cancellationToken)
    {
        var result = await _commandRunner.RunAsync(new CommandInvocation(executable, arguments, _workspace.RepositoryRoot), cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                code,
                $"Command `{executable} {string.Join(' ', arguments)}` failed.",
                string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput.Trim() : result.StandardError.Trim(),
                "Fetch tags and main, verify the release artifact exists at the tag commit, then retry.",
                "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
        }

        return result.StandardOutput.TrimEnd('\r', '\n');
    }

    private static void AppendOutput(StringBuilder builder, string name, string value)
    {
        if (value.Contains('\n', StringComparison.Ordinal))
        {
            var delimiter = $"EOF_{Guid.NewGuid():N}";
            builder.AppendLine($"{name}<<{delimiter}");
            builder.AppendLine(value);
            builder.AppendLine(delimiter);
            return;
        }

        builder.AppendLine($"{name}={value}");
    }
}
