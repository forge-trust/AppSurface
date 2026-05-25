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
/// Creates release artifacts from the living unreleased note.
/// </summary>
internal sealed class ReleasePreparation
{
    private readonly ReleaseWorkspace _workspace;
    private readonly ReleaseChecker _checker;
    private readonly IReleaseClock _clock;

    /// <summary>
    /// Creates release preparation workflow.
    /// </summary>
    /// <param name="workspace">Repository workspace paths.</param>
    /// <param name="checker">Release readiness checker.</param>
    /// <param name="clock">Clock for default dates.</param>
    internal ReleasePreparation(ReleaseWorkspace workspace, ReleaseChecker checker, IReleaseClock clock)
    {
        _workspace = workspace;
        _checker = checker;
        _clock = clock;
    }

    /// <summary>
    /// Generates release files or, in dry-run mode, returns the planned edits.
    /// </summary>
    /// <param name="options">Release command options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Preparation result.</returns>
    internal async Task<ReleasePreparationResult> PrepareAsync(ReleaseOptions options, CancellationToken cancellationToken)
    {
        var check = await _checker.CheckAsync(options, cancellationToken);
        if (check.HasErrors)
        {
            return new ReleasePreparationResult(check, [], options.DryRun);
        }

        var date = options.Date ?? _clock.TodayUtc();
        var unreleased = await File.ReadAllTextAsync(_workspace.UnreleasedPath, cancellationToken);
        var sidecar = await ReleaseSidecar.LoadAsync(_workspace.UnreleasedSidecarPath, cancellationToken);
        var packageSummary = await PackageIndexSummary.LoadAsync(_workspace.PackageIndexPath, cancellationToken);
        var generatedPaths = new List<string>();
        var releaseNotePath = _workspace.ReleaseNotePath(options.Version);
        var releaseSidecarPath = _workspace.ReleaseSidecarPath(options.Version);
        var releaseManifestPath = _workspace.ReleaseManifestPath(options.Version);
        var releasePath = $"releases/v{options.Version}.md";

        var releaseNote = ReleaseNoteBuilder.Build(options.Version, date, unreleased);
        var releaseSidecar = sidecar.ToTaggedRelease(options.Version, date);
        var manifest = new ReleaseManifest(
            options.Version.ToString(),
            options.Version.TagName,
            date.ToString("yyyy-MM-dd"),
            check.SourceCommit,
            check.ReleaseClassification,
            check.GeneratedFiles,
            packageSummary.PublicPublishedPackages.Select(package => package.Project).ToArray(),
            packageSummary.PublicPublishedPackages.Select(package => new PackagePathUpdate(package.Project, package.ReleaseNotesPath, releasePath)).ToArray(),
            check.Errors.Concat(check.Warnings).Select(ReleaseDiagnosticRecord.FromDiagnostic).ToArray(),
            check.Warnings.Select(warning => warning.Code).ToArray());

        var changelog = await File.ReadAllTextAsync(_workspace.ChangelogPath, cancellationToken);
        var packageIndex = await File.ReadAllTextAsync(_workspace.PackageIndexPath, cancellationToken);
        var nextUnreleased = ReleaseNoteBuilder.ResetUnreleased(options.Version);

        var writes = new Dictionary<string, string>
        {
            [releaseNotePath] = releaseNote,
            [releaseSidecarPath] = releaseSidecar,
            [releaseManifestPath] = JsonSerializer.Serialize(manifest, ReleaseJson.Options) + Environment.NewLine,
            [_workspace.ChangelogPath] = ChangelogEditor.RollForward(changelog, options.Version, date, releasePath),
            [_workspace.PackageIndexPath] = PackageIndexEditor.UpdatePublicPublishedReleaseNotes(packageIndex, releasePath),
            [_workspace.UnreleasedPath] = nextUnreleased,
            [_workspace.UnreleasedSidecarPath] = ReleaseSidecar.UnreleasedTemplate()
        };

        if (!options.DryRun)
        {
            foreach (var (path, content) in writes)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, content, cancellationToken);
                generatedPaths.Add(_workspace.DisplayPath(path));
            }
        }
        else
        {
            generatedPaths.AddRange(writes.Keys.Select(_workspace.DisplayPath));
        }

        return new ReleasePreparationResult(check, generatedPaths, options.DryRun);
    }
}
