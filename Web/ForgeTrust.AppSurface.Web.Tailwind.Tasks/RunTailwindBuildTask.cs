using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Web.Tailwind.Internal;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace ForgeTrust.AppSurface.Web.Tailwind.Tasks;

/// <summary>
/// Runs the Tailwind CLI during an MSBuild build and reports stable <c>ASTW###</c> diagnostics.
/// </summary>
/// <remarks>
/// The task is loaded by <c>ForgeTrust.AppSurface.Web.Tailwind.targets</c> for build-time CSS generation.
/// It prefers an explicit <see cref="TailwindCliPath"/> when supplied; otherwise it resolves the runtime
/// package for the current build host RID. Build mode intentionally does not search <c>PATH</c>, because
/// command-line shells and CI agents often expose different paths than MSBuild nodes. Developer watch mode
/// may use <c>PATH</c>, but reproducible builds should use the packaged runtime or an explicit local binary.
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
    /// Gets or sets an optional explicit Tailwind CLI path used instead of the packaged runtime.
    /// </summary>
    /// <remarks>
    /// Optional. Use this escape hatch when a project must pin a custom standalone Tailwind binary or a local
    /// shim. Relative values resolve from <see cref="ProjectDirectory"/>. When set, the file must exist or the
    /// task emits <c>ASTW003</c>; when unset, <see cref="TailwindVersion"/> is required so the runtime package
    /// candidate paths can be constructed.
    /// </remarks>
    public string? TailwindCliPath { get; set; }

    /// <summary>
    /// Gets or sets the resolved Tailwind version from <c>tailwind.version</c>.
    /// </summary>
    /// <remarks>
    /// Required when <see cref="TailwindCliPath"/> is not supplied. The targets file normally reads this value
    /// from the package <c>tailwind.version</c> file. Missing values emit <c>ASTW002</c> because packaged runtime
    /// lookup includes the Tailwind version in source-tree candidate paths.
    /// </remarks>
    public string? TailwindVersion { get; set; }

    /// <summary>
    /// Gets or sets the directory containing <c>ForgeTrust.AppSurface.Web.Tailwind.targets</c>.
    /// </summary>
    /// <remarks>
    /// Required. The task uses this directory to find package-local runtimes, sibling runtime packages in the
    /// NuGet global-packages layout, and source-tree runtime outputs. MSBuild passes
    /// <c>$(MSBuildThisFileDirectory)</c>, which includes a trailing separator.
    /// </remarks>
    [Required]
    public string TargetsDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current build configuration used for source-tree runtime probing.
    /// </summary>
    /// <remarks>
    /// Optional. Defaults to <c>Debug</c> when blank. Package consumers normally do not depend on this property;
    /// it exists so repository/source-tree imports can locate runtime outputs under <c>runtimes/obj</c>.
    /// </remarks>
    public string? Configuration { get; set; }

    /// <summary>
    /// Gets or sets the current project target framework used for source-tree runtime probing.
    /// </summary>
    /// <remarks>
    /// Optional. Defaults to <c>net10.0</c> when blank. This is the consuming project's framework, not the
    /// framework used to load the MSBuild task assembly.
    /// </remarks>
    public string? TargetFramework { get; set; }

    /// <summary>
    /// Gets or sets the shared source-tree Tailwind download cache root.
    /// </summary>
    /// <remarks>
    /// Optional. The imported targets default this to a user-level cache such as
    /// <c>$XDG_CACHE_HOME/forgetrust/appsurface/tailwind</c> or
    /// <c>$HOME/.cache/forgetrust/appsurface/tailwind</c>. The task probes this cache so source-tree builds can
    /// reuse runtime-project downloads across Git worktrees instead of copying every Tailwind executable under
    /// each worktree's <c>obj</c> directory.
    /// </remarks>
    public string? TailwindDownloadCacheRoot { get; set; }

    /// <summary>
    /// Gets or sets an optional Tailwind RID override for tests.
    /// </summary>
    /// <remarks>
    /// Optional. Production builds should leave this blank so <see cref="TailwindRuntimeMap.GetCurrentRid"/> can
    /// resolve the host RID. Tests set this value to exercise unsupported RIDs and package probing branches.
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

        if (!IsRelativePathComponent(rid))
        {
            Log.LogError(
                TailwindDiagnostics.Format(
                    TailwindDiagnostics.UnsupportedRid,
                    $"Tailwind RID '{rid}' is not supported by this package.",
                    "The resolved RID is not a single relative path component.",
                    "Use a supported host or set TailwindCliPath to a compatible local Tailwind CLI binary."));
            return null;
        }

        var runtimeBinaryName = TailwindRuntimeMap.GetRuntimeBinaryName(rid);
        if (string.IsNullOrEmpty(runtimeBinaryName) || !IsFileName(runtimeBinaryName))
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

        foreach (var candidate in EnumerateRuntimeCandidates(rid, runtimeBinaryName).Where(File.Exists))
        {
            return candidate;
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
        yield return Path.GetFullPath(Path.Join(targetsDirectory, "..", "runtimes", rid, "native", runtimeBinaryName));
        foreach (var packageRuntimePath in EnumerateSiblingRuntimePackageCandidates(targetsDirectory, rid, runtimeBinaryName))
        {
            yield return packageRuntimePath;
        }

        yield return Path.GetFullPath(Path.Join(ProjectDirectory, "runtimes", rid, "native", runtimeBinaryName));

        if (!string.IsNullOrWhiteSpace(TailwindDownloadCacheRoot))
        {
            var sharedCacheCandidate = Path.GetFullPath(TailwindDownloadCache.GetRuntimeBinaryPath(
                TailwindDownloadCacheRoot,
                TailwindVersion!,
                rid,
                runtimeBinaryName));
            if (IsVerifiedSharedDownloadCacheCandidate(sharedCacheCandidate, runtimeBinaryName))
            {
                yield return sharedCacheCandidate;
            }
        }

        var localBinaryName = TailwindRuntimeMap.GetLocalBinaryName();
        if (IsFileName(localBinaryName))
        {
            yield return Path.GetFullPath(Path.Join(targetsDirectory, "..", localBinaryName));
        }

        var configuration = string.IsNullOrWhiteSpace(Configuration) ? "Debug" : Configuration;
        var targetFramework = string.IsNullOrWhiteSpace(TargetFramework) ? "net10.0" : TargetFramework;
        yield return Path.GetFullPath(Path.Join(
            targetsDirectory,
            "..",
            "runtimes",
            "obj",
            $"ForgeTrust.AppSurface.Web.Tailwind.Runtime.{rid}",
            configuration,
            targetFramework,
            $"tailwind-{TailwindVersion}",
            runtimeBinaryName));
        yield return Path.GetFullPath(Path.Join(
            targetsDirectory,
            "..",
            "runtimes",
            "obj",
            $"ForgeTrust.AppSurface.Web.Tailwind.Runtime.{rid}",
            configuration,
            targetFramework,
            runtimeBinaryName));
    }

    private bool IsVerifiedSharedDownloadCacheCandidate(string candidatePath, string runtimeBinaryName)
    {
        if (!File.Exists(candidatePath))
        {
            return false;
        }

        var checksumFile = Path.Join(Path.GetDirectoryName(candidatePath), "sha256sums.txt");
        var expectedHash = TryReadExpectedChecksum(checksumFile, runtimeBinaryName);
        if (expectedHash == null)
        {
            Log.LogWarning(
                "Skipping Tailwind shared-cache candidate '{0}' because '{1}' does not contain a checksum for '{2}'.",
                candidatePath,
                checksumFile,
                runtimeBinaryName);
            return false;
        }

        var computedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(candidatePath))).ToLowerInvariant();
        if (!string.Equals(expectedHash, computedHash, StringComparison.OrdinalIgnoreCase))
        {
            Log.LogWarning(
                "Skipping Tailwind shared-cache candidate '{0}' because its checksum does not match '{1}'.",
                candidatePath,
                checksumFile);
            return false;
        }

        return true;
    }

    private static string? TryReadExpectedChecksum(string checksumFile, string runtimeBinaryName)
    {
        if (!File.Exists(checksumFile))
        {
            return null;
        }

        foreach (var line in File.ReadLines(checksumFile, Encoding.UTF8))
        {
            var tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
            {
                continue;
            }

            var fileToken = tokens[1].TrimStart('*');
            if (fileToken.StartsWith("./", StringComparison.Ordinal))
            {
                fileToken = fileToken.Substring(2);
            }

            if (string.Equals(fileToken, runtimeBinaryName, StringComparison.Ordinal))
            {
                return tokens[0].ToLowerInvariant();
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSiblingRuntimePackageCandidates(
        string targetsDirectory,
        string rid,
        string runtimeBinaryName)
    {
        var normalizedTargetsDirectory = Path.TrimEndingDirectorySeparator(targetsDirectory);
        if (string.IsNullOrWhiteSpace(normalizedTargetsDirectory))
        {
            yield break;
        }

        var buildDirectory = new DirectoryInfo(normalizedTargetsDirectory);
        var mainPackageVersionDirectory = buildDirectory.Parent;
        var mainPackageIdDirectory = mainPackageVersionDirectory?.Parent;
        var packagesRoot = mainPackageIdDirectory?.Parent;
        var packageVersion = mainPackageVersionDirectory?.Name;

        if (packagesRoot == null || packageVersion == null || !IsRelativePathComponent(packageVersion))
        {
            yield break;
        }

        yield return Path.Join(
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
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("TargetsDirectory cannot be empty.", nameof(path));
        }

        if (Path.EndsInDirectorySeparator(path))
        {
            return path;
        }

        return path + Path.DirectorySeparatorChar;
    }

    private static bool IsRelativePathComponent(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !Path.IsPathRooted(value)
            && value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;
    }

    private static bool IsFileName(string value)
    {
        return IsRelativePathComponent(value) && Path.GetFileName(value) == value;
    }
}
