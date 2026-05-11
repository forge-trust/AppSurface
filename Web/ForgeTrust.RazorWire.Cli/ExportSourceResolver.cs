using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using CliFx.Exceptions;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Structured command execution result used by the export resolver pipeline.
/// </summary>
/// <param name="ExitCode">
/// The process exit code, or a synthetic negative value when process start
/// failed before an operating-system exit code was available.
/// </param>
/// <param name="Stdout">The captured standard output for the command.</param>
/// <param name="Stderr">The captured standard error or a synthetic start-failure message.</param>
/// <remarks>
/// The resolver prefers this explicit result shape over exceptions for ordinary
/// command failures so it can preserve stdout/stderr in logs and decide whether
/// to throw, fall back to XML parsing, or continue probing.
/// </remarks>
internal record ProcessResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Resolves export sources and, when needed, orchestrates launching a target application for crawling.
/// </summary>
public sealed class ExportSourceResolver
{
    private readonly ILogger<ExportSourceResolver> _logger;
    private readonly ITargetAppProcessFactory _processFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICommandExecutor _razorWireExecutor;

    private const string EphemeralUrlsValue = "http://127.0.0.1:0";
    private const int MaxLogLines = 200;
    private const string ExportPublishFolder = "razorwire-export";

    private static readonly Regex ListeningUrlRegex = new(
        @"Now listening on:\s*(https?://\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FrameworkSegmentRegex = new(
        @"^(net(?<major>\d+)(\.(?<minor>\d+))?|netcoreapp(?<major>\d+)(\.(?<minor>\d+))?|netstandard(?<major>\d+)(\.(?<minor>\d+))?)(?:-[A-Za-z0-9][A-Za-z0-9\.-]*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Gets or sets the maximum time to wait for the launched target app to emit a listening URL.
    /// </summary>
    public TimeSpan ListeningUrlTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets or sets the maximum time to wait for the launched target app to respond as ready.
    /// </summary>
    public TimeSpan AppReadyTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets or sets the polling interval used while probing target app readiness.
    /// </summary>
    public TimeSpan AppReadyPollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Initializes a new instance of <see cref="ExportSourceResolver"/>.
    /// </summary>
    /// <param name="loggerFactory">The logger factory for creating loggers.</param>
    /// <param name="processFactory">Factory for creating target-app process wrappers.</param>
    /// <param name="httpClientFactory">HTTP client factory used for readiness probing.</param>
    public ExportSourceResolver(
        ILoggerFactory loggerFactory,
        ITargetAppProcessFactory processFactory,
        IHttpClientFactory httpClientFactory)
        : this(loggerFactory, processFactory, httpClientFactory, CreateDefaultExecutor(loggerFactory))
    {
    }

    internal ExportSourceResolver(
        ILoggerFactory loggerFactory,
        ITargetAppProcessFactory processFactory,
        IHttpClientFactory httpClientFactory,
        ICommandExecutor razorWireExecutor)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(processFactory);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(razorWireExecutor);

        _logger = loggerFactory.CreateLogger<ExportSourceResolver>();
        _processFactory = processFactory;
        _httpClientFactory = httpClientFactory;
        _razorWireExecutor = razorWireExecutor;
    }

    private static ICommandExecutor CreateDefaultExecutor(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        return new CommandExecutor(loggerFactory.CreateLogger<CommandExecutor>());
    }

    internal async Task<ResolvedExportSource> ResolveAsync(
        ExportSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        var startupStopwatch = Stopwatch.StartNew();

        if (request.SourceKind == ExportSourceKind.Url)
        {
            _logger.LogInformation("Using URL source directly: {BaseUrl}", request.SourceValue);
            await ValidateUrlSourceAsync(request.SourceValue, cancellationToken);

            return new ResolvedExportSource(request.SourceValue, null);
        }

        var launchRequest = await ResolveLaunchRequestAsync(request, cancellationToken);

        var logs = new ConcurrentQueue<string>();
        var boundBaseUrlSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var spec = BuildProcessLaunchSpec(launchRequest);
        var process = _processFactory.Create(spec);
        var processExited = 0;

        process.OutputLineReceived += line => CaptureLog(line, logs, boundBaseUrlSource);
        process.ErrorLineReceived += line => CaptureLog(line, logs, boundBaseUrlSource);
        process.Exited += () =>
        {
            Interlocked.Exchange(ref processExited, 1);
            boundBaseUrlSource.TrySetException(
                new InvalidOperationException(
                    $"Target application exited before publishing a listening URL.{Environment.NewLine}{GetRecentLogs(logs)}"));
        };

        try
        {
            process.Start();
            _logger.LogInformation(
                "Started target application process for export. Listening timeout: {ListeningTimeout}s, ready timeout: {ReadyTimeout}s",
                ListeningUrlTimeout.TotalSeconds,
                AppReadyTimeout.TotalSeconds);

            var baseUrl = await WaitForBoundBaseUrlAsync(boundBaseUrlSource, logs, cancellationToken);
            await WaitForAppReadyAsync(
                baseUrl,
                process,
                () => Volatile.Read(ref processExited) == 1,
                logs,
                cancellationToken);

            startupStopwatch.Stop();
            _logger.LogInformation(
                "Resolved export source URL: {BaseUrl} (startup took {ElapsedMs}ms)",
                baseUrl,
                startupStopwatch.ElapsedMilliseconds);

            return new ResolvedExportSource(baseUrl, process);
        }
        catch
        {
            await process.DisposeAsync();

            throw;
        }
    }

    internal async Task<ExportSourceRequest> ResolveLaunchRequestAsync(
        ExportSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.SourceKind != ExportSourceKind.Project)
        {
            return request;
        }

        var projectPath = request.SourceValue;

        if (string.IsNullOrWhiteSpace(request.Framework) && IsMultiTargetProject(projectPath))
        {
            throw new CommandException(
                "The publish target is not supported without specifying a target framework in a multi-target project. Use the --framework option to specify.");
        }

        var projectDirectory = Path.GetDirectoryName(projectPath) ?? Directory.GetCurrentDirectory();
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        var assemblyName = await TryResolveAssemblyNameAsync(projectPath, projectName, request.Framework, cancellationToken);
        var publishOutputDirectory = Path.Combine(projectDirectory, "bin", ExportPublishFolder);

        if (!request.NoBuild)
        {
            if (Directory.Exists(publishOutputDirectory))
            {
                Directory.Delete(publishOutputDirectory, true);
            }

            Directory.CreateDirectory(publishOutputDirectory);
            _logger.LogInformation(
                "Publishing project for export: {ProjectPath} -> {OutputPath}",
                projectPath,
                publishOutputDirectory);

            var publishArgs = new List<string>
            {
                "publish",
                projectPath,
                // Captured CLI executions should not leave reusable build servers holding inherited pipes open.
                "--disable-build-servers",
                "-c",
                "Release",
                "-o",
                publishOutputDirectory
            };
            if (!string.IsNullOrWhiteSpace(request.Framework))
            {
                publishArgs.Add("-f");
                publishArgs.Add(request.Framework);
            }

            await RunCommandOrThrowAsync(
                "dotnet",
                publishArgs,
                projectDirectory,
                cancellationToken);
        }
        else
        {
            _logger.LogInformation("Skipping project publish (--no-build): {ProjectPath}", projectPath);
        }

        var dllPath = ResolveBuiltDllPath(
            projectDirectory,
            assemblyName,
            request.NoBuild ? null : publishOutputDirectory,
            request.Framework);
        _logger.LogInformation("Launching published DLL for export: {DllPath}", dllPath);

        return request with
        {
            SourceKind = ExportSourceKind.Dll,
            SourceValue = dllPath
        };
    }

    internal ProcessLaunchSpec BuildProcessLaunchSpec(ExportSourceRequest request)
    {
        var effectiveAppArgs = BuildEffectiveAppArgs(request.AppArgs);

        if (request.SourceKind == ExportSourceKind.Project)
        {
            throw new InvalidOperationException(
                "Project sources must be resolved to a DLL launch request before building process launch spec.");
        }

        var dllDirectory = Path.GetDirectoryName(request.SourceValue)
                           ?? Directory.GetCurrentDirectory();
        var dllArgs = new List<string> { request.SourceValue };
        dllArgs.AddRange(effectiveAppArgs);
        // Deliberately force production hosting for launched export targets so middleware/static-asset
        // behavior matches deployed runtime semantics. Keep both keys in dllEnvironmentOverrides.
        var dllEnvironmentOverrides = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["DOTNET_ENVIRONMENT"] = "Production"
        };

        return new ProcessLaunchSpec
        {
            FileName = "dotnet",
            Arguments = dllArgs,
            EnvironmentOverrides = dllEnvironmentOverrides,
            WorkingDirectory = dllDirectory
        };
    }

    private async Task ValidateUrlSourceAsync(string baseUrl, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(AppReadyTimeout);
        using var client = _httpClientFactory.CreateClient("ExportEngine");

        try
        {
            // Any HTTP response proves the URL source is reachable enough to begin crawling.
            using var response = await client.GetAsync(
                baseUrl,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token);
            _logger.LogDebug(
                "Validated URL source {BaseUrl} with status code {StatusCode}",
                baseUrl,
                (int)response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            throw new CommandException(
                $"Could not reach --url target '{baseUrl}'. Ensure the application is running and reachable. {ex.Message}");
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (TaskCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new CommandException(BuildUrlSourceTimeoutMessage(baseUrl, AppReadyTimeout));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested
                                            && !timeoutCts.IsCancellationRequested)
        {
            var effectiveTimeout = client.Timeout != Timeout.InfiniteTimeSpan
                ? client.Timeout
                : AppReadyTimeout;

            throw new CommandException(BuildUrlSourceTimeoutMessage(baseUrl, effectiveTimeout));
        }
    }

    private static string BuildUrlSourceTimeoutMessage(string baseUrl, TimeSpan timeout)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Timed out while connecting to --url target '{baseUrl}' after {timeout.TotalSeconds:0.###} seconds. Ensure the application is running and reachable.");
    }

    /// <summary>
    /// Executes a command through the configured <see cref="ICommandExecutor"/>.
    /// </summary>
    /// <param name="fileName">The executable to start.</param>
    /// <param name="args">The ordered command-line arguments for the executable.</param>
    /// <param name="workingDirectory">The working directory used for process start.</param>
    /// <param name="cancellationToken">Cancels the command execution.</param>
    /// <returns>
    /// A <see cref="ProcessResult"/> containing the exit code and captured
    /// output streams from the delegated executor.
    /// </returns>
    /// <remarks>
    /// This seam exists so resolver tests can verify command composition
    /// without launching real processes. Callers should treat a non-zero exit
    /// as data and decide whether to raise an exception or fall back.
    /// </remarks>
    internal Task<ProcessResult> ExecuteProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        return _razorWireExecutor.ExecuteCommandAsync(fileName, args, workingDirectory, cancellationToken);
    }

    private async Task RunCommandOrThrowAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteProcessAsync(fileName, args, workingDirectory, cancellationToken);

        if (!string.IsNullOrWhiteSpace(result.Stdout))
        {
            _logger.LogDebug(
                "Command output ({FileName}):{NewLine}{Output}",
                fileName,
                Environment.NewLine,
                result.Stdout);
        }

        if (!string.IsNullOrWhiteSpace(result.Stderr))
        {
            _logger.LogDebug(
                "Command error output ({FileName}):{NewLine}{Output}",
                fileName,
                Environment.NewLine,
                result.Stderr);
        }

        if (result.ExitCode == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Command failed with exit code {result.ExitCode}: {fileName} {string.Join(" ", args)}{Environment.NewLine}Stdout:{Environment.NewLine}{result.Stdout}{Environment.NewLine}Stderr:{Environment.NewLine}{result.Stderr}");
    }

    internal static string ResolveBuiltDllPath(string projectDirectory, string assemblyName)
    {
        return ResolveBuiltDllPath(projectDirectory, assemblyName, null);
    }

    internal static string ResolveBuiltDllPath(
        string projectDirectory,
        string assemblyName,
        string? explicitPublishDirectory,
        string? requestedFramework = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            projectDirectory = NormalizePathForMacOS(projectDirectory);
            if (explicitPublishDirectory != null)
            {
                explicitPublishDirectory = NormalizePathForMacOS(explicitPublishDirectory);
            }
        }

        var explicitPublishPath = string.IsNullOrWhiteSpace(explicitPublishDirectory)
            ? null
            : Path.GetFullPath(explicitPublishDirectory);

        List<string> candidatePaths;
        if (!string.IsNullOrWhiteSpace(explicitPublishPath))
        {
            if (!Directory.Exists(explicitPublishPath))
            {
                throw new FileNotFoundException(
                    $"Could not find published output folder. Expected: {explicitPublishPath}");
            }

            candidatePaths = Directory.EnumerateFiles(
                    explicitPublishPath,
                    $"{assemblyName}.dll",
                    SearchOption.AllDirectories)
                .Where(path => !IsRefAssemblyPath(path))
                .Where(path => IsPublishedArtifact(explicitPublishPath, path, requirePublishSegment: false))
                .ToList();
        }
        else
        {
            var releaseDir = Path.Combine(projectDirectory, "bin", "Release");
            if (!Directory.Exists(releaseDir))
            {
                throw new FileNotFoundException(
                    $"Could not find any publish output. Expected either explicit publish directory or '{releaseDir}'.");
            }

            candidatePaths = Directory.EnumerateFiles(
                    releaseDir,
                    $"{assemblyName}.dll",
                    SearchOption.AllDirectories)
                .Where(path => !IsRefAssemblyPath(path))
                .Where(path => IsPublishedArtifact(releaseDir, path, requirePublishSegment: true))
                .ToList();

            if (candidatePaths.Count == 0)
            {
                var binDirectory = Path.Combine(projectDirectory, "bin");
                if (Directory.Exists(binDirectory))
                {
                    candidatePaths = Directory.EnumerateFiles(
                            binDirectory,
                            $"{assemblyName}.dll",
                            SearchOption.AllDirectories)
                        .Where(path => !IsRefAssemblyPath(path))
                        .Where(path => IsPublishedArtifact(binDirectory, path, requirePublishSegment: true))
                        .ToList();
                }
            }
        }

        if (candidatePaths.Count == 0)
        {
            throw new FileNotFoundException(
                $"Could not locate published DLL for assembly '{assemblyName}' under '{projectDirectory}'.");
        }

        var preferredFramework =
            string.IsNullOrWhiteSpace(explicitPublishPath) &&
            !string.IsNullOrWhiteSpace(requestedFramework)
                ? requestedFramework
                : ResolvePreferredFramework(candidatePaths, projectDirectory);
        if (!string.IsNullOrWhiteSpace(preferredFramework))
        {
            candidatePaths = candidatePaths
                .Where(path => string.Equals(
                    TryGetFrameworkSegment(projectDirectory, path),
                    preferredFramework,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidatePaths.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No published candidate DLLs remained after selecting preferred framework '{preferredFramework}' under '{projectDirectory}'.");
            }
        }

        if (candidatePaths.Count == 0)
        {
            throw new FileNotFoundException(
                $"Could not locate any valid DLL for assembly '{assemblyName}' under '{projectDirectory}'.");
        }

        var candidates = candidatePaths
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            // When timestamps tie within the selected framework, use path as a deterministic final tie-breaker.
            .ThenBy(
                info => info.FullName,
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal)
            .ToList();

        if (candidates.Count == 0)
        {
            throw new FileNotFoundException(
                $"No files found for candidate paths in '{projectDirectory}'.");
        }

        return candidates[0].FullName;
    }

    /// <summary>
    /// Resolves the effective assembly name for a project, preferring MSBuild
    /// evaluation and falling back to raw project XML parsing when needed.
    /// </summary>
    /// <param name="projectPath">The project file whose assembly name should be resolved.</param>
    /// <param name="fallbackName">The fallback assembly name when no explicit value can be determined.</param>
    /// <param name="framework">
    /// The target framework used for MSBuild evaluation. Supplying this keeps
    /// conditional <c>AssemblyName</c> values aligned with the publish target.
    /// </param>
    /// <param name="cancellationToken">Cancels MSBuild evaluation.</param>
    /// <returns>
    /// The assembly name reported by MSBuild when available; otherwise the
    /// value returned by <see cref="TryResolveAssemblyNameFromXml"/>.
    /// </returns>
    /// <remarks>
    /// MSBuild is preferred because it evaluates imports and conditional
    /// properties that raw XML parsing cannot see. If MSBuild exits non-zero,
    /// produces no usable stdout, or throws a non-cancellation exception, the
    /// resolver logs the failure details and falls back to XML parsing.
    /// Omitting <paramref name="framework"/> can mis-evaluate conditional
    /// <c>AssemblyName</c> declarations for multi-target projects.
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// Thrown when command execution is canceled.
    /// </exception>
    internal async Task<string> TryResolveAssemblyNameAsync(
        string projectPath,
        string fallbackName,
        string? framework,
        CancellationToken cancellationToken)
    {
        try
        {
            var msbuildArgs = new List<string>
            {
                "msbuild",
                projectPath,
                "-getProperty:AssemblyName",
                "-nologo",
                // Keep MSBuild property probes one-shot for the same reason as publish.
                "-nodeReuse:false",
                "-p:Configuration=Release"
            };

            if (!string.IsNullOrWhiteSpace(framework))
            {
                msbuildArgs.Add($"-p:TargetFramework={framework}");
            }

            var result = await ExecuteProcessAsync(
                "dotnet",
                msbuildArgs,
                Path.GetDirectoryName(projectPath) ?? Directory.GetCurrentDirectory(),
                cancellationToken);

            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Stdout))
            {
                return result.Stdout.Trim();
            }

            _logger.LogWarning(
                "Failed to resolve assembly name via MSBuild for {ProjectPath} (ExitCode: {ExitCode}). Falling back to XML parsing.{NewLine}Stdout: {Stdout}{NewLine}Stderr: {Stderr}",
                projectPath,
                result.ExitCode,
                Environment.NewLine,
                result.Stdout,
                Environment.NewLine,
                result.Stderr);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve assembly name via MSBuild for {ProjectPath}. Falling back to XML parsing.", projectPath);
        }

        return TryResolveAssemblyNameFromXml(projectPath, fallbackName);
    }

    /// <summary>
    /// Reads a project file directly and returns the first explicit
    /// <c>AssemblyName</c> value found in the XML.
    /// </summary>
    /// <param name="projectPath">The project file to inspect.</param>
    /// <param name="fallbackName">The value returned when the XML cannot be read or has no assembly name.</param>
    /// <returns>
    /// The explicit <c>AssemblyName</c> from the project file, or
    /// <paramref name="fallbackName"/> when no usable value is available.
    /// </returns>
    /// <remarks>
    /// This method intentionally does not evaluate imports or conditional MSBuild
    /// properties. It is the low-cost fallback used when MSBuild evaluation is
    /// unavailable or fails to return a usable value.
    /// </remarks>
    internal static string TryResolveAssemblyNameFromXml(string projectPath, string fallbackName)
    {
        try
        {
            var document = XDocument.Load(projectPath);
            var assemblyName = document
                .Descendants()
                .FirstOrDefault(node => string.Equals(
                    node.Name.LocalName,
                    "AssemblyName",
                    StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?.Trim();

            return string.IsNullOrWhiteSpace(assemblyName) ? fallbackName : assemblyName;
        }
        catch (IOException)
        {
            return fallbackName;
        }
        catch (UnauthorizedAccessException)
        {
            return fallbackName;
        }
        catch (XmlException)
        {
            return fallbackName;
        }
        catch (ArgumentException)
        {
            return fallbackName;
        }
        catch (NotSupportedException)
        {
            return fallbackName;
        }
    }

    internal static bool IsMultiTargetProject(string projectPath)
    {
        try
        {
            var document = XDocument.Load(projectPath);

            return document
                .Descendants()
                .Where(node => string.Equals(
                    node.Name.LocalName,
                    "TargetFrameworks",
                    StringComparison.OrdinalIgnoreCase))
                .SelectMany(node => node.Value.Split(
                    ';',
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Skip(1)
                .Any();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (XmlException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    internal static bool IsRefAssemblyPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/');

        return normalized.Contains("/ref/", StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizePathForMacOS(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return path;
        }

        if (path.StartsWith("/var/", StringComparison.Ordinal))
        {
            return "/private" + path;
        }

        return path;
    }

    private static bool IsPublishedArtifact(string publishSearchRoot, string path, bool requirePublishSegment)
    {
        var publishSearchRootFull = NormalizePathForMacOS(Path.GetFullPath(publishSearchRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar);
        var pathFull = NormalizePathForMacOS(Path.GetFullPath(path));

        var segmentComparer = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!pathFull.StartsWith(publishSearchRootFull, segmentComparer))
        {
            return false;
        }

        if (!requirePublishSegment)
        {
            return true;
        }

        var comparer = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        bool HasMatch(string searchPath)
        {
            var parts = searchPath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            return parts.Contains("publish", comparer)
                   || parts.Contains(ExportPublishFolder, comparer);
        }

        var relative = pathFull.Substring(publishSearchRootFull.Length);
        return HasMatch(publishSearchRootFull) || HasMatch(relative);
    }

    internal static string? TryGetFrameworkSegment(string rootDirectory, string path)
    {
        var releaseFull = Path.GetFullPath(rootDirectory);
        var pathFull = Path.GetFullPath(path);

        var relative = Path.GetRelativePath(releaseFull, pathFull);
        if (string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }

        if (relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            return null;
        }

        var frameworkSegment = relative
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(segment => FrameworkSegmentRegex.IsMatch(segment));
        if (string.IsNullOrWhiteSpace(frameworkSegment))
        {
            return null;
        }

        return frameworkSegment;
    }

    internal static string? ResolvePreferredFramework(IReadOnlyList<string> candidatePaths, string releaseDir)
    {
        var frameworks = candidatePaths
            .Select(path => TryGetFrameworkSegment(releaseDir, path))
            .OfType<string>()
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (frameworks.Count == 0)
        {
            return null;
        }

        return frameworks
            .OrderByDescending(ParseFrameworkVersion)
            .ThenByDescending(name => name, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    internal static Version ParseFrameworkVersion(string framework)
    {
        var match = FrameworkSegmentRegex.Match(framework);
        if (!match.Success)
        {
            return new Version(0, 0);
        }

        if (!int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || major > ushort.MaxValue)
        {
            return new Version(0, 0);
        }

        var minor = 0;
        if (match.Groups["minor"].Success
            && (!int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out minor)
                || minor > ushort.MaxValue))
        {
            return new Version(major, 0);
        }

        return new Version(major, minor);
    }

    internal static IReadOnlyList<string> BuildEffectiveAppArgs(IReadOnlyList<string> appArgs)
    {
        var effectiveArgs = appArgs.ToList();
        if (!ContainsUrlsOption(effectiveArgs))
        {
            effectiveArgs.Add("--urls");
            effectiveArgs.Add(EphemeralUrlsValue);
        }

        return effectiveArgs;
    }

    internal static bool ContainsUrlsOption(IReadOnlyList<string> args)
    {
        return args.Any(arg =>
            string.Equals(arg, "--urls", StringComparison.OrdinalIgnoreCase)
            || arg.StartsWith("--urls=", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool TryParseListeningBaseUrl(string line, out string baseUrl)
    {
        baseUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var match = ListeningUrlRegex.Match(line);
        if (!match.Success)
        {
            return false;
        }

        if (!Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        baseUrl = uri.GetLeftPart(UriPartial.Authority);

        return true;
    }

    private void CaptureLog(
        string line,
        ConcurrentQueue<string> logs,
        TaskCompletionSource<string> boundBaseUrlSource)
    {
        _logger.LogInformation("{TargetAppLog}", line);
        logs.Enqueue(line);
        while (logs.Count > MaxLogLines)
        {
            logs.TryDequeue(out _);
        }

        if (TryParseListeningBaseUrl(line, out var baseUrl))
        {
            boundBaseUrlSource.TrySetResult(baseUrl);
        }
    }

    private async Task<string> WaitForBoundBaseUrlAsync(
        TaskCompletionSource<string> boundBaseUrlSource,
        ConcurrentQueue<string> logs,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ListeningUrlTimeout);
        var delayTask = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);

        var completedTask = await Task.WhenAny(
            boundBaseUrlSource.Task,
            delayTask);

        if (completedTask == boundBaseUrlSource.Task)
        {
            timeoutCts.Cancel();
            try
            {
                await delayTask;
            }
            catch (OperationCanceledException)
            {
                // The delay is expected to be canceled when boundBaseUrlSource completes first.
            }

            return await boundBaseUrlSource.Task;
        }

        cancellationToken.ThrowIfCancellationRequested();

        throw new TimeoutException(
            $"Target application did not publish a listening URL within {ListeningUrlTimeout.TotalSeconds} seconds.{Environment.NewLine}{GetRecentLogs(logs)}");
    }

    private async Task WaitForAppReadyAsync(
        string baseUrl,
        ITargetAppProcess process,
        Func<bool> processExited,
        ConcurrentQueue<string> logs,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(AppReadyTimeout);
        using var client = _httpClientFactory.CreateClient("ExportEngine");

        while (!timeoutCts.Token.IsCancellationRequested)
        {
            if (process.HasExited || processExited())
            {
                throw new InvalidOperationException(
                    $"Target application exited before it became ready.{Environment.NewLine}{GetRecentLogs(logs)}");
            }

            try
            {
                using var response = await client.GetAsync(baseUrl, timeoutCts.Token);
                // Any HTTP response proves the app is reachable; we don't require a specific status code.
                _logger.LogDebug(
                    "Readiness probe returned {StatusCode} for {BaseUrl}",
                    (int)response.StatusCode,
                    baseUrl);

                return;
            }
            catch (HttpRequestException)
            {
                // The app may still be booting.
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (TaskCanceledException) when (!timeoutCts.IsCancellationRequested)
            {
                // Retry until global timeout.
            }
            catch (TaskCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Target application did not become ready within {AppReadyTimeout.TotalSeconds} seconds.{Environment.NewLine}{GetRecentLogs(logs)}");
            }

            try
            {
                await Task.Delay(AppReadyPollInterval, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Target application did not become ready within {AppReadyTimeout.TotalSeconds} seconds.{Environment.NewLine}{GetRecentLogs(logs)}");
            }
        }

        throw new TimeoutException(
            $"Target application did not become ready within {AppReadyTimeout.TotalSeconds} seconds.{Environment.NewLine}{GetRecentLogs(logs)}");
    }

    private static string GetRecentLogs(ConcurrentQueue<string> logs)
    {
        return string.Join(Environment.NewLine, logs);
    }
}
