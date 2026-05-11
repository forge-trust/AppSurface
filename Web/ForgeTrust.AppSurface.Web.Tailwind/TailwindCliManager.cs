using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("ForgeTrust.AppSurface.Web.Tailwind.Tests")]
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]

namespace ForgeTrust.AppSurface.Web.Tailwind;

/// <summary>
/// Manages the location and execution of the Tailwind CLI binary.
/// </summary>
public class TailwindCliManager
{
    /// <summary>
    /// Represents the concrete process invocation required to launch the resolved Tailwind CLI.
    /// </summary>
    /// <param name="FileName">The executable or launcher to start.</param>
    /// <param name="Arguments">The complete ordered argument list to pass to <paramref name="FileName" />.</param>
    internal sealed record TailwindCliInvocation(string FileName, IReadOnlyList<string> Arguments);

    private readonly ILogger<TailwindCliManager> _logger;
    private readonly string _binaryName = GetBinaryName();

    /// <summary>
    /// Initializes a new instance of the <see cref="TailwindCliManager"/> class.
    /// </summary>
    /// <param name="logger">The logger to use.</param>
    public TailwindCliManager(ILogger<TailwindCliManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets or sets a directory to override <see cref="AppContext.BaseDirectory"/> for testing.
    /// </summary>
    internal string? BaseDirectoryOverride { get; set; }

    /// <summary>
    /// Gets or sets a directory to override the resolved assembly directory for testing isolated fallback lookup.
    /// </summary>
    internal string? AssemblyDirectoryOverride { get; set; }

    /// <summary>
    /// Gets or sets a runtime identifier override for tests that need to exercise non-host RID resolution paths.
    /// </summary>
    internal string? RidOverride { get; set; }

    /// <summary>
    /// Gets or sets an operating-system detector override for tests that need deterministic platform simulation.
    /// </summary>
    internal static Func<OSPlatform, bool>? IsOSPlatformOverride { get; set; }

    /// <summary>
    /// Gets or sets a process-architecture override for tests that need deterministic RID resolution.
    /// </summary>
    internal static Func<Architecture>? ProcessArchitectureOverride { get; set; }

    /// <summary>
    /// Gets the path to the Tailwind CLI binary.
    /// </summary>
    /// <returns>The absolute path to the tailwindcss executable.</returns>
    /// <remarks>
    /// Resolution proceeds in this order:
    /// <list type="number">
    /// <item><description>RID-specific runtime assets under <see cref="AppContext.BaseDirectory"/>.</description></item>
    /// <item><description>A flat binary next to the application under <see cref="AppContext.BaseDirectory"/>.</description></item>
    /// <item><description>RID-specific runtime assets relative to this assembly, including local development runtime build outputs when running inside this repository.</description></item>
    /// <item><description>The system <c>PATH</c> as an escape hatch for custom or Node-managed Tailwind setups, including Windows shell shims such as <c>.cmd</c> and <c>.ps1</c>.</description></item>
    /// </list>
    /// If none of these locations contain a compatible binary, the method throws <see cref="FileNotFoundException"/>.
    /// </remarks>
    /// <exception cref="FileNotFoundException">Thrown if the binary cannot be found in runtimes, local directory, or PATH.</exception>
    public virtual string GetTailwindPath()
    {
        var baseDir = BaseDirectoryOverride ?? AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir))
        {
            var rid = RidOverride ?? GetCurrentRid();

            // 1. Check standard NuGet runtime asset path (for published apps or project-local runtimes folder)
            var runtimePath = Path.Combine(baseDir, "runtimes", rid, "native", _binaryName);
            if (File.Exists(runtimePath))
            {
                _logger.LogDebug("Found Tailwind CLI at runtime path: {Path}", runtimePath);
                return runtimePath;
            }

            // 2. Check AppContext.BaseDirectory directly (for single-file or non-standard deployments)
            var localPath = Path.Combine(baseDir, _binaryName);
            if (File.Exists(localPath))
            {
                _logger.LogDebug("Found Tailwind CLI at base path: {Path}", localPath);
                return localPath;
            }

            // 3. Check for the binary in any runtimes folder relative to the assembly
            // This handles cases where runtime packages are present but not yet deployed to a flat bin folder
            var assemblyDir = AssemblyDirectoryOverride;
            if (string.IsNullOrEmpty(assemblyDir))
            {
                var assemblyLocation = typeof(TailwindCliManager).Assembly.Location;
                if (!string.IsNullOrEmpty(assemblyLocation))
                {
                    assemblyDir = Path.GetDirectoryName(assemblyLocation);
                }
            }

            if (!string.IsNullOrEmpty(assemblyDir))
            {
                var fallbackRuntimePath = Path.Combine(assemblyDir, "runtimes", rid, "native", _binaryName);
                if (File.Exists(fallbackRuntimePath))
                {
                    _logger.LogDebug("Found Tailwind CLI at fallback runtime path: {Path}", fallbackRuntimePath);
                    return fallbackRuntimePath;
                }

                var devRuntimeProjectPath = GetDevRuntimeProjectPath(baseDir, rid) ?? GetDevRuntimeProjectPath(assemblyDir, rid);
                if (!string.IsNullOrEmpty(devRuntimeProjectPath) && File.Exists(devRuntimeProjectPath))
                {
                    _logger.LogDebug("Found Tailwind CLI at development runtime project path: {Path}", devRuntimeProjectPath);
                    return devRuntimeProjectPath;
                }
            }
        }

        // 4. Check system PATH
        if (TryGetFromPath(_binaryName, _logger, out var path))
        {
            _logger.LogDebug("Found Tailwind CLI in PATH: {Path}", path);
            return path;
        }

        throw new FileNotFoundException(
            $"Tailwind CLI not found. Please ensure a 'ForgeTrust.AppSurface.Web.Tailwind.Runtime.*' package for your platform is installed or tailwindcss is on PATH. (Missing: {_binaryName})",
            _binaryName);
    }

    /// <summary>
    /// Builds the process invocation needed to execute a resolved Tailwind CLI path.
    /// </summary>
    /// <param name="tailwindPath">The resolved Tailwind CLI path returned by <see cref="GetTailwindPath"/>.</param>
    /// <param name="tailwindArgs">The Tailwind CLI arguments to forward to the process.</param>
    /// <returns>
    /// A <see cref="TailwindCliInvocation"/> describing the executable to launch and the full ordered argument list.
    /// </returns>
    /// <remarks>
    /// Windows PATH resolution can return Node-managed shell shims such as <c>.cmd</c> or <c>.ps1</c>. These files
    /// cannot be launched reliably with <see cref="System.Diagnostics.ProcessStartInfo.UseShellExecute"/> disabled, so
    /// they are wrapped with <c>cmd.exe</c> or <c>powershell.exe</c> as appropriate.
    /// </remarks>
    internal static TailwindCliInvocation BuildInvocation(string tailwindPath, IReadOnlyList<string> tailwindArgs)
    {
        if (!IsCurrentOsPlatform(OSPlatform.Windows))
        {
            return new TailwindCliInvocation(tailwindPath, tailwindArgs);
        }

        var extension = Path.GetExtension(tailwindPath);
        if (string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase))
        {
            return new TailwindCliInvocation(
                "cmd.exe",
                CreateInvocationArguments("/d", "/c", tailwindPath, tailwindArgs));
        }

        if (string.Equals(extension, ".ps1", StringComparison.OrdinalIgnoreCase))
        {
            return new TailwindCliInvocation(
                "powershell.exe",
                CreateInvocationArguments("-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", tailwindPath, tailwindArgs));
        }

        return new TailwindCliInvocation(tailwindPath, tailwindArgs);
    }

    /// <summary>
    /// Gets the Runtime Identifier (RID) for the current platform.
    /// </summary>
    /// <returns>The RID string (e.g., "win-x64", "linux-arm64").</returns>
    /// <remarks>
    /// Must be kept in sync with the RID logic in the runtime package projects and
    /// build/ForgeTrust.AppSurface.Web.Tailwind.targets. Unsupported operating systems
    /// or architectures return <c>"unknown"</c>. Windows Arm64 intentionally maps to
    /// <c>win-x64</c> because Tailwind v4.1.18 does not ship a native Windows Arm64
    /// standalone binary.
    /// </remarks>
    public static string GetCurrentRid()
    {
        var processArchitecture = ProcessArchitectureOverride?.Invoke() ?? RuntimeInformation.ProcessArchitecture;

        if (IsCurrentOsPlatform(OSPlatform.Windows))
        {
            return ResolveRid(OSPlatform.Windows, processArchitecture);
        }

        if (IsCurrentOsPlatform(OSPlatform.Linux))
        {
            return ResolveRid(OSPlatform.Linux, processArchitecture);
        }

        if (IsCurrentOsPlatform(OSPlatform.OSX))
        {
            return ResolveRid(OSPlatform.OSX, processArchitecture);
        }

        return "unknown";
    }

    /// <summary>
    /// Resolves the Tailwind runtime identifier for a specific platform and process architecture.
    /// </summary>
    /// <param name="osPlatform">The operating system platform to evaluate.</param>
    /// <param name="architecture">The process architecture to map.</param>
    /// <returns>The Tailwind runtime identifier for the supplied platform/architecture pair.</returns>
    /// <remarks>
    /// Must be kept in sync with the RID logic in the runtime package projects and
    /// build/ForgeTrust.AppSurface.Web.Tailwind.targets.
    /// </remarks>
    internal static string ResolveRid(OSPlatform osPlatform, Architecture architecture)
    {
        if (osPlatform == OSPlatform.Windows)
        {
            return architecture switch
            {
                Architecture.X64 => "win-x64",
                // Tailwind v4.1.18 ships only a Windows x64 standalone binary.
                Architecture.Arm64 => "win-x64",
                _ => "unknown"
            };
        }

        if (osPlatform == OSPlatform.Linux)
        {
            return architecture switch
            {
                Architecture.X64 => "linux-x64",
                Architecture.Arm64 => "linux-arm64",
                _ => "unknown"
            };
        }

        if (osPlatform == OSPlatform.OSX)
        {
            return architecture switch
            {
                Architecture.X64 => "osx-x64",
                Architecture.Arm64 => "osx-arm64",
                _ => "unknown"
            };
        }

        return "unknown";
    }

    private static string? GetDevRuntimeProjectPath(string sourceDir, string rid)
    {
        var targetFramework = Path.GetFileName(sourceDir);
        var configurationDir = Directory.GetParent(sourceDir);
        var binDir = configurationDir?.Parent;

        if (string.IsNullOrEmpty(targetFramework) || configurationDir == null || binDir?.Name != "bin")
        {
            return null;
        }

        var binaryName = rid switch
        {
            "win-x64" => "tailwindcss-windows-x64.exe",
            "osx-arm64" => "tailwindcss-macos-arm64",
            "osx-x64" => "tailwindcss-macos-x64",
            "linux-arm64" => "tailwindcss-linux-arm64",
            "linux-x64" => "tailwindcss-linux-x64",
            _ => null
        };

        if (string.IsNullOrEmpty(binaryName))
        {
            return null;
        }

        foreach (var candidateRoot in EnumerateSelfAndAncestors(binDir.Parent))
        {
            var candidatePath = Path.Combine(
                candidateRoot,
                "Web",
                "ForgeTrust.AppSurface.Web.Tailwind",
                "runtimes",
                "obj",
                $"ForgeTrust.AppSurface.Web.Tailwind.Runtime.{rid}",
                configurationDir.Name,
                targetFramework,
                binaryName);

            if (File.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        return null;
    }

    private static bool IsCurrentOsPlatform(OSPlatform platform)
    {
        return IsOSPlatformOverride?.Invoke(platform) ?? RuntimeInformation.IsOSPlatform(platform);
    }

    private static string GetBinaryName()
    {
        return IsCurrentOsPlatform(OSPlatform.Windows) ? "tailwindcss.exe" : "tailwindcss";
    }

    private static IEnumerable<string> EnumerateSelfAndAncestors(DirectoryInfo? startDirectory)
    {
        for (var current = startDirectory; current != null; current = current.Parent)
        {
            yield return current.FullName;
        }
    }

    private static bool TryGetFromPath(string fileName, ILogger logger, out string path)
    {
        path = string.Empty;
        var values = Environment.GetEnvironmentVariable("PATH");
        if (values == null)
        {
            logger.LogDebug("PATH environment variable is not set.");
            return false;
        }

        foreach (var p in values.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidateName in EnumeratePathSearchNames(fileName))
            {
                var fullPath = Path.Combine(p, candidateName);
                if (File.Exists(fullPath))
                {
                    path = fullPath;
                    return true;
                }
            }
        }

        return false;
    }

    private static IReadOnlyList<string> CreateInvocationArguments(
        string firstArg,
        string secondArg,
        string thirdArg,
        IReadOnlyList<string> tailwindArgs)
    {
        var arguments = new List<string>(tailwindArgs.Count + 3)
        {
            firstArg,
            secondArg,
            thirdArg
        };
        arguments.AddRange(tailwindArgs);
        return arguments;
    }

    private static IReadOnlyList<string> CreateInvocationArguments(
        string firstArg,
        string secondArg,
        string thirdArg,
        string fourthArg,
        string fifthArg,
        string sixthArg,
        IReadOnlyList<string> tailwindArgs)
    {
        var arguments = new List<string>(tailwindArgs.Count + 6)
        {
            firstArg,
            secondArg,
            thirdArg,
            fourthArg,
            fifthArg,
            sixthArg
        };
        arguments.AddRange(tailwindArgs);
        return arguments;
    }

    private static IEnumerable<string> EnumeratePathSearchNames(string fileName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            fileName
        };
        yield return fileName;

        if (!IsCurrentOsPlatform(OSPlatform.Windows))
        {
            yield break;
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrEmpty(baseName))
        {
            yield break;
        }

        foreach (var extension in GetWindowsPathExtensions())
        {
            var candidate = baseName + extension;
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> GetWindowsPathExtensions()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var extension in new[] { ".exe", ".cmd", ".ps1" })
        {
            if (seen.Add(extension))
            {
                yield return extension;
            }
        }

        var pathExtensions = Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathExtensions))
        {
            yield break;
        }

        foreach (var rawExtension in pathExtensions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var extension = rawExtension.StartsWith(".", StringComparison.Ordinal) ? rawExtension : "." + rawExtension;
            if (seen.Add(extension))
            {
                yield return extension;
            }
        }
    }
}
