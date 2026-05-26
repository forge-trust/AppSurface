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
/// Validates release inputs and computes release readiness diagnostics.
/// </summary>
internal sealed class ReleaseChecker
{
    private readonly ReleaseWorkspace _workspace;
    private readonly ICommandRunner _commandRunner;

    /// <summary>
    /// Creates a release checker.
    /// </summary>
    /// <param name="workspace">Repository workspace paths.</param>
    /// <param name="commandRunner">Process runner for optional git checks.</param>
    internal ReleaseChecker(ReleaseWorkspace workspace, ICommandRunner commandRunner)
    {
        _workspace = workspace;
        _commandRunner = commandRunner;
    }

    /// <summary>
    /// Runs local release readiness checks.
    /// </summary>
    /// <param name="options">Release command options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Readiness result with errors, warnings, and generated paths.</returns>
    internal async Task<ReleaseCheckResult> CheckAsync(ReleaseOptions options, CancellationToken cancellationToken)
    {
        var errors = new List<ReleaseDiagnostic>();
        var warnings = new List<ReleaseDiagnostic>();
        var generatedFiles = GeneratedFiles(options.Version);

        foreach (var requiredPath in RequiredPaths().Where(requiredPath => !File.Exists(requiredPath)))
        {
            errors.Add(ReleaseDiagnostic.Error(
                "release-required-file-missing",
                $"Required release input '{_workspace.DisplayPath(requiredPath)}' is missing.",
                "Release preparation needs the living note, sidecar metadata, changelog, tagged template, and package manifest.",
                "Restore the file or run from the repository root before retrying.",
                "releases/release-authoring-checklist.md"));
        }

        if (options.AllowExistingTargets && string.Equals(options.Command, "check", StringComparison.Ordinal))
        {
            foreach (var target in generatedFiles.Where(target => !File.Exists(target)))
            {
                errors.Add(ReleaseDiagnostic.Error(
                    "release-generated-target-missing",
                    $"Generated target '{_workspace.DisplayPath(target)}' is missing.",
                    "`--allow-existing-targets` is only valid when reviewing the complete prepared release artifact set.",
                    "Run `./eng/release prepare` for this version and include the release note, sidecar metadata, and release manifest in the release preparation pull request.",
                    "tools/ForgeTrust.AppSurface.Release/README.md#check"));
            }
        }

        foreach (var target in generatedFiles.Where(File.Exists))
        {
            if (options.AllowExistingTargets && string.Equals(options.Command, "check", StringComparison.Ordinal))
            {
                continue;
            }

            var diagnostic = ReleaseDiagnostic.Error(
                    "release-target-exists",
                    $"Generated target '{_workspace.DisplayPath(target)}' already exists.",
                    "Release preparation is intentionally create-only for versioned artifacts.",
                    "Choose a new version or remove the stale generated artifact after confirming it is safe.",
                    "tools/ForgeTrust.AppSurface.Release/README.md#prepare");
            if (string.Equals(options.Command, "prepare", StringComparison.Ordinal))
            {
                errors.Add(diagnostic);
            }
            else
            {
                warnings.Add(diagnostic with { Severity = "warning" });
            }
        }

        if (!File.Exists(_workspace.PathFor(".github/workflows/nuget-prerelease-publish.yml")))
        {
            errors.Add(ReleaseDiagnostic.Error(
                "release-prerelease-package-path-missing",
                "The protected NuGet prerelease publish workflow is missing.",
                "GitHub Releases must not ship without a package publish path for public packages.",
                "Restore `.github/workflows/nuget-prerelease-publish.yml` before preparing or publishing a release.",
                "tools/ForgeTrust.AppSurface.Release/README.md#stable-release-policy"));
        }

        if (options.Version.IsStable)
        {
            warnings.Add(ReleaseDiagnostic.Warning(
                "release-stable-package-policy-missing",
                "Stable GitHub Release publishing is currently blocked.",
                "The repository has a protected prerelease NuGet path, but no protected stable NuGet publish workflow yet.",
                "Ship a prerelease tag such as `0.1.0-preview.1`, or add a reviewed stable package publish path before publishing `v0.1.0`.",
                "tools/ForgeTrust.AppSurface.Release/README.md#stable-release-policy"));
        }

        if (File.Exists(_workspace.UnreleasedPath))
        {
            var unreleased = await File.ReadAllTextAsync(_workspace.UnreleasedPath, cancellationToken);
            Markdown.Parse(unreleased);
            AddNarrativeWarnings(unreleased, warnings);
        }

        if (File.Exists(_workspace.PackageIndexPath))
        {
            var packageSummary = await PackageIndexSummary.LoadAsync(_workspace.PackageIndexPath, cancellationToken);
            if (packageSummary.PublicPublishedPackages.Count == 0)
            {
                errors.Add(ReleaseDiagnostic.Error(
                    "release-no-public-packages",
                    "No public publishable packages were found in the package manifest.",
                    "`classification: public` plus `publish_decision: publish` defines the release package surface.",
                    "Fix `packages/package-index.yml` before preparing a coordinated release.",
                    "packages/README.md"));
            }
        }

        var sourceCommit = await TryGetSourceCommitAsync(cancellationToken);
        return new ReleaseCheckResult(
            options.Version.ToString(),
            options.Version.IsStable ? "stable" : "prerelease",
            sourceCommit,
            generatedFiles.Select(_workspace.DisplayPath).ToArray(),
            errors,
            warnings);
    }

    private IReadOnlyList<string> RequiredPaths()
    {
        return
        [
            _workspace.ChangelogPath,
            _workspace.UnreleasedPath,
            _workspace.UnreleasedSidecarPath,
            _workspace.PackageIndexPath,
            _workspace.TemplatePath
        ];
    }

    private IReadOnlyList<string> GeneratedFiles(SemVer version)
    {
        return
        [
            _workspace.ReleaseNotePath(version),
            _workspace.ReleaseSidecarPath(version),
            _workspace.ReleaseManifestPath(version)
        ];
    }

    private async Task<string?> TryGetSourceCommitAsync(CancellationToken cancellationToken)
    {
        var result = await _commandRunner.RunAsync(
            new CommandInvocation("git", ["rev-parse", "HEAD"], _workspace.RepositoryRoot),
            cancellationToken);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    private static void AddNarrativeWarnings(string unreleased, List<ReleaseDiagnostic> warnings)
    {
        if (!unreleased.Contains("## Migration watch", StringComparison.OrdinalIgnoreCase)
            && !unreleased.Contains("## Migration guidance", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(ReleaseDiagnostic.Warning(
                "release-migration-guidance-missing",
                "The unreleased note does not include migration guidance.",
                "Tagged releases need a reader-visible place for breaking changes and upgrade steps.",
                "Add or preserve a migration section before preparing the final note.",
                "releases/release-authoring-checklist.md"));
        }

        if (unreleased.Contains("TODO", StringComparison.OrdinalIgnoreCase)
            || unreleased.Contains("placeholder", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(ReleaseDiagnostic.Warning(
                "release-placeholder-copy",
                "The unreleased note still contains placeholder copy.",
                "Placeholder language can leak into the public tagged release note.",
                "Replace TODO or placeholder text before publishing.",
                "releases/release-authoring-checklist.md"));
        }
    }
}
