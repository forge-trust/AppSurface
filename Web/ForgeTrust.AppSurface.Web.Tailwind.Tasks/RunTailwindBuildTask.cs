using ForgeTrust.AppSurface.Web.Tailwind.Internal;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace ForgeTrust.AppSurface.Web.Tailwind.Tasks;

/// <summary>
/// Runs the Tailwind CLI during an MSBuild build.
/// </summary>
public sealed class RunTailwindBuildTask : Microsoft.Build.Utilities.Task, ICancelableTask
{
    private const int BuildOutputCaptureLimit = 8192;
    private CancellationTokenSource? _cancellationTokenSource;

    /// <summary>
    /// Gets or sets the project directory used as the Tailwind working directory and base for relative paths.
    /// </summary>
    [Required]
    public string ProjectDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Tailwind input CSS path, relative to <see cref="ProjectDirectory"/> unless rooted.
    /// </summary>
    [Required]
    public string InputPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Tailwind output CSS path, relative to <see cref="ProjectDirectory"/> unless rooted.
    /// </summary>
    [Required]
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional explicit Tailwind CLI path.
    /// </summary>
    public string? TailwindCliPath { get; set; }

    /// <summary>
    /// Gets or sets the resolved Tailwind version from <c>tailwind.version</c>.
    /// </summary>
    public string? TailwindVersion { get; set; }

    /// <summary>
    /// Gets or sets the directory containing <c>ForgeTrust.AppSurface.Web.Tailwind.targets</c>.
    /// </summary>
    [Required]
    public string TargetsDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current build configuration.
    /// </summary>
    public string? Configuration { get; set; }

    /// <summary>
    /// Gets or sets the current target framework.
    /// </summary>
    public string? TargetFramework { get; set; }

    /// <summary>
    /// Gets or sets an optional Tailwind RID override for tests.
    /// </summary>
    public string? TailwindTargetRid { get; set; }

    /// <inheritdoc />
    public void Cancel()
    {
        _cancellationTokenSource?.Cancel();
    }

    /// <inheritdoc />
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
        var tailwindPath = ResolveTailwindPath();
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

        try
        {
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
                    "Verify TailwindCliPath or the packaged runtime binary is executable and accessible."));
            return false;
        }
    }

    private string? ResolveTailwindPath()
    {
        if (!string.IsNullOrWhiteSpace(TailwindCliPath))
        {
            var explicitPath = Path.GetFullPath(TailwindCliPath, ProjectDirectory);
            if (File.Exists(explicitPath))
            {
                return explicitPath;
            }

            Log.LogError(
                TailwindDiagnostics.Format(
                    TailwindDiagnostics.InvalidCliPath,
                    $"TailwindCliPath was set to '{TailwindCliPath}', but the resolved file '{explicitPath}' does not exist.",
                    "The explicit CLI override points at a missing file.",
                    "Set TailwindCliPath to an existing Tailwind standalone binary or remove the property to use the packaged runtime."));
            return null;
        }

        var rid = string.IsNullOrWhiteSpace(TailwindTargetRid)
            ? TailwindRuntimeMap.GetCurrentRid()
            : TailwindTargetRid;
        if (string.Equals(rid, "unknown", StringComparison.Ordinal))
        {
            Log.LogError(
                TailwindDiagnostics.Format(
                    TailwindDiagnostics.UnsupportedRid,
                    "Tailwind CSS could not determine a supported runtime identifier for the current build host.",
                    "The current operating system and process architecture do not match a packaged Tailwind runtime.",
                    "Use a supported host or set TailwindCliPath to a compatible local Tailwind CLI binary."));
            return null;
        }

        var runtimeBinaryName = TailwindRuntimeMap.GetRuntimeBinaryName(rid);
        if (string.IsNullOrEmpty(runtimeBinaryName))
        {
            Log.LogError(
                TailwindDiagnostics.Format(
                    TailwindDiagnostics.UnsupportedRid,
                    $"Tailwind RID '{rid}' is not supported by this package.",
                    "No Tailwind runtime binary mapping exists for the resolved RID.",
                    "Use a supported host or set TailwindCliPath to a compatible local Tailwind CLI binary."));
            return null;
        }

        if (string.IsNullOrWhiteSpace(TailwindVersion))
        {
            Log.LogError(
                TailwindDiagnostics.Format(
                    TailwindDiagnostics.MissingVersion,
                    "Tailwind CSS version could not be resolved.",
                    "The targets file could not read build/tailwind.version and TailwindVersion was not set.",
                    "Set TailwindVersion explicitly or ensure tailwind.version is packaged next to the targets file."));
            return null;
        }

        foreach (var candidate in EnumerateRuntimeCandidates(rid, runtimeBinaryName))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        Log.LogError(
            TailwindDiagnostics.Format(
                TailwindDiagnostics.MissingCli,
                "Tailwind CLI binary not found.",
                "No packaged, project-local, source-tree, or explicit Tailwind CLI binary exists for the current build host. Build mode does not search PATH.",
                "Install the matching ForgeTrust.AppSurface.Web.Tailwind.Runtime package or set TailwindCliPath to a local Tailwind CLI binary."));
        return null;
    }

    private IEnumerable<string> EnumerateRuntimeCandidates(string rid, string runtimeBinaryName)
    {
        var targetsDirectory = EnsureTrailingSeparator(TargetsDirectory);
        yield return Path.GetFullPath(Path.Combine(targetsDirectory, "..", "runtimes", rid, "native", runtimeBinaryName));
        foreach (var packageRuntimePath in EnumerateSiblingRuntimePackageCandidates(targetsDirectory, rid, runtimeBinaryName))
        {
            yield return packageRuntimePath;
        }

        yield return Path.GetFullPath(Path.Combine(ProjectDirectory, "runtimes", rid, "native", runtimeBinaryName));

        var localBinaryName = TailwindRuntimeMap.GetLocalBinaryName();
        yield return Path.GetFullPath(Path.Combine(targetsDirectory, "..", localBinaryName));

        var configuration = string.IsNullOrWhiteSpace(Configuration) ? "Debug" : Configuration;
        var targetFramework = string.IsNullOrWhiteSpace(TargetFramework) ? "net10.0" : TargetFramework;
        yield return Path.GetFullPath(Path.Combine(
            targetsDirectory,
            "..",
            "runtimes",
            "obj",
            $"ForgeTrust.AppSurface.Web.Tailwind.Runtime.{rid}",
            configuration,
            targetFramework,
            $"tailwind-{TailwindVersion}",
            runtimeBinaryName));
        yield return Path.GetFullPath(Path.Combine(
            targetsDirectory,
            "..",
            "runtimes",
            "obj",
            $"ForgeTrust.AppSurface.Web.Tailwind.Runtime.{rid}",
            configuration,
            targetFramework,
            runtimeBinaryName));
    }

    private static IEnumerable<string> EnumerateSiblingRuntimePackageCandidates(
        string targetsDirectory,
        string rid,
        string runtimeBinaryName)
    {
        var buildDirectory = Directory.GetParent(targetsDirectory);
        var mainPackageVersionDirectory = buildDirectory?.Parent;
        var packagesRoot = mainPackageVersionDirectory?.Parent?.Parent;
        var packageVersion = mainPackageVersionDirectory?.Name;

        if (packagesRoot == null || string.IsNullOrWhiteSpace(packageVersion))
        {
            yield break;
        }

        yield return Path.Combine(
            packagesRoot.FullName,
            $"forgetrust.appsurface.web.tailwind.runtime.{rid}",
            packageVersion,
            "runtimes",
            rid,
            "native",
            runtimeBinaryName);
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

    private static string EnsureTrailingSeparator(string path)
    {
        if (string.IsNullOrEmpty(path) || Path.EndsInDirectorySeparator(path))
        {
            return path;
        }

        return path + Path.DirectorySeparatorChar;
    }
}
