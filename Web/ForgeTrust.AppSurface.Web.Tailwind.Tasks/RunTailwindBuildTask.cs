using ForgeTrust.AppSurface.Web.Tailwind.Internal;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace ForgeTrust.AppSurface.Web.Tailwind.Tasks;

/// <summary>
/// Runs the Tailwind CLI during an MSBuild build and reports stable <c>ASTW###</c> diagnostics.
/// </summary>
/// <remarks>
/// The task is loaded by <c>ForgeTrust.AppSurface.Web.Tailwind.targets</c> for build-time CSS generation.
/// It prefers an explicit <see cref="TailwindCliPath"/> when supplied; otherwise it resolves the package-pinned
/// standalone CLI into the verified cache for the current build host RID. Build mode intentionally does not search <c>PATH</c>, because
/// command-line shells and CI agents often expose different paths than MSBuild nodes. Developer watch mode
/// may use <c>PATH</c>, but reproducible builds should use the verified host cache or an explicit local binary.
/// </remarks>
public sealed class RunTailwindBuildTask : Microsoft.Build.Utilities.Task, ICancelableTask
{
    private const int BuildOutputCaptureLimit = 8192;
    private CancellationTokenSource? _cancellationTokenSource;

    /// <summary>
    /// Gets or sets the project directory used as the Tailwind working directory.
    /// </summary>
    /// <remarks>
    /// Required. Relative <see cref="InputPath"/>, <see cref="OutputPath"/>, and <see cref="TailwindCliPath"/>
    /// values are resolved from this directory. MSBuild passes <c>$(MSBuildProjectDirectory)</c>.
    /// </remarks>
    [Required]
    public string ProjectDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Tailwind input CSS path.
    /// </summary>
    /// <remarks>
    /// Required. The value is passed to Tailwind as <c>-i</c> and is interpreted relative to
    /// <see cref="ProjectDirectory"/> unless it is rooted. The imported targets validate that input and output
    /// paths do not resolve to the same file before this task runs.
    /// </remarks>
    [Required]
    public string InputPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the generated Tailwind output CSS path.
    /// </summary>
    /// <remarks>
    /// Required. The value is passed to Tailwind as <c>-o</c> and is interpreted relative to
    /// <see cref="ProjectDirectory"/> unless it is rooted. Outputs under <c>wwwroot</c> are registered by the
    /// targets file as static web assets.
    /// </remarks>
    [Required]
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional explicit Tailwind CLI path used instead of verified host-cache resolution.
    /// </summary>
    /// <remarks>
    /// Optional. Use this escape hatch when a project must pin a custom standalone Tailwind binary or a local
    /// shim. Relative values resolve from <see cref="ProjectDirectory"/>. When set, the file must exist or the
    /// task emits <c>ASTW003</c>; when unset, <see cref="TailwindVersion"/> and the release manifest identify
    /// the single verified cache entry for the build host.
    /// </remarks>
    public string? TailwindCliPath { get; set; }

    /// <summary>
    /// Gets or sets the resolved Tailwind version from <c>tailwind.version</c>.
    /// </summary>
    /// <remarks>
    /// Required when <see cref="TailwindCliPath"/> is not supplied. The targets file normally reads this value
    /// from the package <c>tailwind.version</c> file. Missing values emit <c>ASTW002</c> because the verified
    /// cache identity includes the Tailwind version.
    /// </remarks>
    public string? TailwindVersion { get; set; }

    /// <summary>
    /// Gets or sets the packed or source-tree <c>tailwind.release.json</c> path.
    /// </summary>
    /// <remarks>
    /// Required when no explicit <see cref="TailwindCliPath"/> is supplied. The manifest
    /// provides the package-pinned version, release URL, host binary names, and expected
    /// digests used to verify the one acquired host executable.
    /// </remarks>
    public string? TailwindReleaseManifestPath { get; set; }

    /// <summary>
    /// Gets or sets the directory containing <c>ForgeTrust.AppSurface.Web.Tailwind.targets</c>.
    /// </summary>
    /// <remarks>
    /// Retained for MSBuild target compatibility. The host-scoped resolver does not probe package-local, sibling,
    /// or source-tree native binaries; MSBuild continues to pass <c>$(MSBuildThisFileDirectory)</c>.
    /// </remarks>
    [Required]
    public string TargetsDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current build configuration.
    /// </summary>
    /// <remarks>
    /// Retained for target compatibility. Host-scoped CLI resolution does not use this value.
    /// </remarks>
    public string? Configuration { get; set; }

    /// <summary>
    /// Gets or sets the current project target framework.
    /// </summary>
    /// <remarks>
    /// Retained for target compatibility. Host-scoped CLI resolution does not use this value.
    /// </remarks>
    public string? TargetFramework { get; set; }

    /// <summary>
    /// Gets or sets the shared source-tree Tailwind download cache root.
    /// </summary>
    /// <remarks>
    /// Optional. The imported targets default this to a user-level cache such as
    /// <c>$XDG_CACHE_HOME/forgetrust/appsurface/tailwind</c> or
    /// <c>$HOME/.cache/forgetrust/appsurface/tailwind</c>. The task probes this cache so source-tree builds can
    /// reuse verified host CLI downloads across Git worktrees instead of copying every executable under each
    /// worktree's <c>obj</c> directory.
    /// </remarks>
    public string? TailwindDownloadCacheRoot { get; set; }

    /// <summary>
    /// Gets or sets an optional Tailwind RID override for tests.
    /// </summary>
    /// <remarks>
    /// Optional. Production builds should leave this blank so <see cref="TailwindRuntimeMap.GetCurrentRid"/> can
    /// resolve the host RID. Tests set this value to exercise unsupported-RID branches.
    /// </remarks>
    public string? TailwindTargetRid { get; set; }

    /// <summary>
    /// Requests cancellation of the running Tailwind child process.
    /// </summary>
    /// <remarks>
    /// MSBuild calls this method when the build is canceled. <see cref="Execute"/> observes the cancellation token,
    /// terminates the child process through the process runner, emits <c>ASTW007</c>, and returns <c>false</c>.
    /// Calling this method before <see cref="Execute"/> starts is a no-op.
    /// </remarks>
    public void Cancel()
    {
        _cancellationTokenSource?.Cancel();
    }

    /// <summary>
    /// Resolves the Tailwind executable, runs <c>tailwindcss -i ... -o ... --minify</c>, and reports success to MSBuild.
    /// </summary>
    /// <returns>
    /// <c>true</c> when Tailwind exits with code <c>0</c>; otherwise <c>false</c> after logging an <c>ASTW###</c>
    /// diagnostic. Non-zero exits include the last <c>8192</c> characters of stdout and stderr to avoid unbounded
    /// MSBuild memory growth while preserving the useful tail of the failure output.
    /// </returns>
    public override bool Execute()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        _cancellationTokenSource = cancellationTokenSource;

        try
        {
            return ExecuteAsync(cancellationTokenSource.Token).GetAwaiter().GetResult();
        }
        finally
        {
            _cancellationTokenSource = null;
        }
    }

    private async Task<bool> ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tailwindPath = await ResolveTailwindPathAsync(cancellationToken);
            if (string.IsNullOrEmpty(tailwindPath))
            {
                return false;
            }

            var args = new[] { "-i", InputPath, "-o", OutputPath, "--minify" };
            var invocation = TailwindInvocationBuilder.Build(tailwindPath, args);

            Log.LogMessage(
                MessageImportance.High,
                "Tailwind CSS: Running build for {0} -> {1}",
                InputPath,
                OutputPath);

            var result = await TailwindProcessRunner.ExecuteAsync(
                invocation.FileName,
                invocation.Arguments,
                ProjectDirectory,
                line => Log.LogMessage(MessageImportance.Normal, "{0}: {1}", invocation.FileName, line),
                LogStandardErrorLine,
                BuildOutputCaptureLimit,
                cancellationToken);

            if (result.ExitCode == 0)
            {
                return true;
            }

            Log.LogError(
                TailwindDiagnostics.Format(
                    TailwindDiagnostics.NonZeroExit,
                    $"Tailwind CLI exited with code {result.ExitCode}.",
                    "The Tailwind process completed but reported a failed build.",
                    "Review the Tailwind output above, fix the CSS/configuration error, and run the build again.")
                + FormatCapturedOutput(result));
            return false;
        }
        catch (OperationCanceledException)
        {
            Log.LogError(
                TailwindDiagnostics.Format(
                    TailwindDiagnostics.Canceled,
                    "Tailwind build was canceled.",
                    "MSBuild canceled the task before the Tailwind process completed.",
                    "Run the build again when cancellation was unintentional."));
            return false;
        }
        catch (TailwindProcessStartException ex)
        {
            Log.LogError(
                TailwindDiagnostics.Format(
                    TailwindDiagnostics.ProcessStartFailed,
                    $"Tailwind CLI process could not be started from '{ex.FileName}'.",
                    ex.InnerException?.Message ?? "The operating system rejected the process start request.",
                    "Verify TailwindCliPath or the verified cached binary is executable and accessible."));
            return false;
        }
    }

    private async Task<string?> ResolveTailwindPathAsync(CancellationToken cancellationToken)
    {
        try
        {
            TailwindResolvedCli resolved;
            if (!string.IsNullOrWhiteSpace(TailwindCliPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                resolved = TailwindCliResolver.ResolveExplicitPath(TailwindCliPath, ProjectDirectory);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(TailwindReleaseManifestPath))
                {
                    throw new TailwindCliResolutionException(
                        TailwindCliResolutionFailure.MissingManifest,
                        "The package Tailwind release manifest is missing.");
                }

                var manifest = TailwindReleaseManifest.LoadFromFile(TailwindReleaseManifestPath);
                var resolver = new TailwindCliResolver(manifest);
                resolved = await resolver.ResolveAsync(
                    new TailwindCliResolverOptions(
                        TailwindCliPath,
                        ProjectDirectory,
                        TailwindDownloadCacheRoot,
                        TailwindVersion,
                        TailwindTargetRid),
                    cancellationToken);
            }

            Log.LogMessage(MessageImportance.High, "Tailwind CSS: {0} verified CLI for {1} ({2}).", resolved.CacheState, resolved.Rid, resolved.Version);
            return resolved.Path;
        }
        catch (TailwindCliResolutionException ex)
        {
            LogResolverFailure(ex);
            return null;
        }
        catch (InvalidDataException ex)
        {
            Log.LogError(TailwindDiagnostics.Format(
                TailwindDiagnostics.AcquisitionFailed,
                "Tailwind release manifest is invalid.",
                ex.Message,
                "Restore the package or set TailwindCliPath to an existing compatible CLI."));
            return null;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            Log.LogError(TailwindDiagnostics.Format(
                TailwindDiagnostics.AcquisitionFailed,
                "Tailwind release manifest could not be opened.",
                ex.Message,
                "Restore the package or set TailwindCliPath to an existing compatible CLI."));
            return null;
        }
    }

    private void LogResolverFailure(TailwindCliResolutionException ex)
    {
        if (ex.Failure == TailwindCliResolutionFailure.InvalidCliPath)
        {
            Log.LogError(TailwindDiagnostics.Format(
                TailwindDiagnostics.InvalidCliPath,
                "TailwindCliPath was set but does not resolve to an existing file.",
                ex.Message,
                "Set TailwindCliPath to an existing Tailwind standalone binary or remove the property to use verified host resolution."));
            return;
        }

        if (ex.Failure == TailwindCliResolutionFailure.MissingManifest)
        {
            Log.LogError(TailwindDiagnostics.Format(
                TailwindDiagnostics.AcquisitionFailed,
                "The package Tailwind release manifest is missing.",
                ex.Message,
                "Restore the package or set TailwindCliPath to an existing compatible CLI."));
            return;
        }

        if (ex.Failure == TailwindCliResolutionFailure.UnsupportedRid)
        {
            Log.LogError(TailwindDiagnostics.Format(
                TailwindDiagnostics.UnsupportedRid,
                ex.Message,
                "The current operating system and process architecture are not mapped to a Tailwind release asset.",
                "Use a supported host or set TailwindCliPath to a compatible local Tailwind CLI binary."));
            return;
        }

        if (ex.Failure == TailwindCliResolutionFailure.MissingVersion)
        {
            Log.LogError(TailwindDiagnostics.Format(
                TailwindDiagnostics.MissingVersion,
                ex.Message,
                "The package targets could not read tailwind.version.",
                "Restore the package or set TailwindCliPath to an existing compatible CLI."));
            return;
        }

        var identity = ex.Rid is null || ex.Version is null
            ? "unavailable"
            : $"tailwind-{ex.Version}/{ex.Rid}";
        Log.LogError(TailwindDiagnostics.Format(
            TailwindDiagnostics.AcquisitionFailed,
            $"Tailwind CLI acquisition failed ({ex.Failure}).",
            $"{ex.Message} Safe cache identity: {identity}.",
            "Set TailwindCliPath, prewarm TailwindDownloadCacheRoot, or consult the Tailwind package diagnostics."));
    }

    private void LogStandardErrorLine(string line, TailwindOutputLevel level)
    {
        switch (level)
        {
            case TailwindOutputLevel.Debug:
                Log.LogMessage(MessageImportance.Low, "{0}", line);
                break;
            case TailwindOutputLevel.Information:
                Log.LogMessage(MessageImportance.Normal, "{0}", line);
                break;
            default:
                Log.LogWarning("{0}", line);
                break;
        }
    }

    private static string FormatCapturedOutput(TailwindCommandResult result)
    {
        if (string.IsNullOrEmpty(result.Stdout) && string.IsNullOrEmpty(result.Stderr))
        {
            return string.Empty;
        }

        return $"{Environment.NewLine}Captured stdout tail:{Environment.NewLine}{result.Stdout}{Environment.NewLine}Captured stderr tail:{Environment.NewLine}{result.Stderr}";
    }

}
