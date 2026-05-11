using System.Runtime.InteropServices;
using FakeItEasy;
using ForgeTrust.AppSurface.Web.Tailwind;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Web.Tailwind.Tests;

[CollectionDefinition("EnvVarIsolation", DisableParallelization = true)]
public sealed class EnvVarIsolationCollection
{
}

[Collection("EnvVarIsolation")]
public class TailwindCliManagerTests : IDisposable
{
    private readonly string _tempPath;
    private readonly string? _originalPath;
    private readonly string? _originalPathExt;
    private readonly ILogger<TailwindCliManager> _logger;
    private readonly TailwindCliManager _manager;
    private readonly string _binaryName;
    private readonly string _currentHostRid;
    private readonly string _currentHostRuntimeProjectBinaryName;

    public TailwindCliManagerTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), "TailwindTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempPath);
        _logger = A.Fake<ILogger<TailwindCliManager>>();
        _manager = new TailwindCliManager(_logger);
        _manager.BaseDirectoryOverride = _tempPath;
        _manager.AssemblyDirectoryOverride = Path.Combine(_tempPath, "isolated-assembly");
        _binaryName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "tailwindcss.exe" : "tailwindcss";
        _currentHostRid = GetSupportedHostRid();
        _currentHostRuntimeProjectBinaryName = GetRuntimeProjectBinaryName(_currentHostRid);
        _manager.RidOverride = _currentHostRid;
        _originalPath = Environment.GetEnvironmentVariable("PATH");
        _originalPathExt = Environment.GetEnvironmentVariable("PATHEXT");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        Environment.SetEnvironmentVariable("PATHEXT", _originalPathExt);
        TailwindCliManager.IsOSPlatformOverride = null;
        TailwindCliManager.ProcessArchitectureOverride = null;

        if (Directory.Exists(_tempPath))
        {
            Directory.Delete(_tempPath, true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void GetTailwindPath_ReturnsRuntimePath_IfFound()
    {
        // Arrange
        var runtimeNativeDir = Path.Combine(_tempPath, "runtimes", _currentHostRid, "native");
        Directory.CreateDirectory(runtimeNativeDir);
        var expectedPath = Path.Combine(runtimeNativeDir, _binaryName);
        File.WriteAllText(expectedPath, "dummy");

        // Act
        var result = _manager.GetTailwindPath();

        // Assert
        Assert.Equal(expectedPath, result);
    }

    [Fact]
    public void GetTailwindPath_ReturnsLocalPath_IfRuntimeNotFound()
    {
        // Arrange
        var expectedPath = Path.Combine(_tempPath, _binaryName);
        File.WriteAllText(expectedPath, "dummy");

        // Act
        var result = _manager.GetTailwindPath();

        // Assert
        Assert.Equal(expectedPath, result);
    }

    [Fact]
    public void GetTailwindPath_ReturnsPathValue_IfFoundInPath()
    {
        // Arrange
        var pathDir = Path.Combine(_tempPath, "bin");
        Directory.CreateDirectory(pathDir);

        var expectedPath = Path.Combine(pathDir, _binaryName);
        File.WriteAllText(expectedPath, "dummy");
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, [pathDir, _originalPath ?? string.Empty]));

        // Act
        var result = _manager.GetTailwindPath();

        // Assert
        Assert.Equal(expectedPath, result);
    }

    [Fact]
    public void GetTailwindPath_UsesWindowsBinaryName_WhenWindowsOverrideIsSet()
    {
        var pathDir = Path.Combine(_tempPath, "bin");
        Directory.CreateDirectory(pathDir);
        var expectedPath = Path.Combine(pathDir, "tailwindcss.exe");
        File.WriteAllText(expectedPath, "dummy");
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, [pathDir, _originalPath ?? string.Empty]));
        TailwindCliManager.IsOSPlatformOverride = platform => platform == OSPlatform.Windows;

        var manager = new TailwindCliManager(_logger)
        {
            BaseDirectoryOverride = _tempPath,
            AssemblyDirectoryOverride = _tempPath,
            RidOverride = "win-x64"
        };

        var result = manager.GetTailwindPath();

        Assert.Equal(expectedPath, result);
    }

    [Fact]
    public void GetTailwindPath_ResolvesBinary_WhenUsingDefaultBaseDirectory()
    {
        var pathDir = Path.Combine(_tempPath, "bin");
        Directory.CreateDirectory(pathDir);

        var expectedPath = Path.Combine(pathDir, _binaryName);
        File.WriteAllText(expectedPath, "dummy");
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, [pathDir, _originalPath ?? string.Empty]));

        _manager.BaseDirectoryOverride = null;
        _manager.AssemblyDirectoryOverride = null;

        var result = _manager.GetTailwindPath();

        Assert.True(Path.IsPathRooted(result));
        Assert.True(File.Exists(result));
        Assert.Contains("tailwindcss", Path.GetFileName(result), StringComparison.Ordinal);
    }

    [Fact]
    public void GetTailwindPath_ReturnsAssemblyFallbackRuntimePath_IfFound()
    {
        var baseDir = Path.Combine(_tempPath, "app-base");
        var assemblyDir = Path.Combine(_tempPath, "assembly-base");
        Directory.CreateDirectory(baseDir);
        _manager.BaseDirectoryOverride = baseDir;
        _manager.AssemblyDirectoryOverride = assemblyDir;

        var runtimeNativeDir = Path.Combine(assemblyDir, "runtimes", _currentHostRid, "native");
        Directory.CreateDirectory(runtimeNativeDir);
        var expectedPath = Path.Combine(runtimeNativeDir, _binaryName);

        File.WriteAllText(expectedPath, "dummy");

        var result = _manager.GetTailwindPath();

        Assert.Equal(expectedPath, result);
    }

    [Fact]
    public void GetTailwindPath_ReturnsDevelopmentRuntimeProjectPath_IfFound()
    {
        var baseDir = Path.Combine(_tempPath, "examples", "sample-app", "bin", "Debug", "net10.0");
        var assemblyDir = Path.Combine(baseDir, "assembly-shadow");
        Directory.CreateDirectory(baseDir);
        _manager.BaseDirectoryOverride = baseDir;
        _manager.AssemblyDirectoryOverride = assemblyDir;

        var runtimeProjectDir = Path.Combine(
            _tempPath,
            "Web",
            "ForgeTrust.AppSurface.Web.Tailwind",
            "runtimes",
            "obj",
            $"ForgeTrust.AppSurface.Web.Tailwind.Runtime.{_currentHostRid}",
            "Debug",
            "net10.0");
        Directory.CreateDirectory(runtimeProjectDir);
        var expectedPath = Path.Combine(runtimeProjectDir, _currentHostRuntimeProjectBinaryName);
        File.WriteAllText(expectedPath, "dummy");

        var result = _manager.GetTailwindPath();

        Assert.Equal(expectedPath, result);
    }

    [Theory]
    [MemberData(nameof(DevelopmentRuntimeRidCases))]
    public void GetTailwindPath_ReturnsDevelopmentRuntimeProjectPath_ForSupportedRidOverride(string rid, string binaryName)
    {
        var baseDir = Path.Combine(_tempPath, "examples", "sample-app", "bin", "Debug", "net10.0");
        var assemblyDir = Path.Combine(baseDir, "assembly-shadow");
        Directory.CreateDirectory(baseDir);
        _manager.BaseDirectoryOverride = baseDir;
        _manager.AssemblyDirectoryOverride = assemblyDir;
        _manager.RidOverride = rid;

        var runtimeProjectDir = Path.Combine(
            _tempPath,
            "Web",
            "ForgeTrust.AppSurface.Web.Tailwind",
            "runtimes",
            "obj",
            $"ForgeTrust.AppSurface.Web.Tailwind.Runtime.{rid}",
            "Debug",
            "net10.0");
        Directory.CreateDirectory(runtimeProjectDir);
        var expectedPath = Path.Combine(runtimeProjectDir, binaryName);
        File.WriteAllText(expectedPath, "dummy");

        var result = _manager.GetTailwindPath();

        Assert.Equal(expectedPath, result);
    }

    [Fact]
    public void GetTailwindPath_ReturnsDevelopmentRuntimeProjectPath_FromAssemblyDirectory_WhenBaseDirectoryShapeIsUnsupported()
    {
        var baseDir = Path.Combine(_tempPath, "examples", "sample-app");
        var assemblyDir = Path.Combine(_tempPath, "examples", "shadow-app", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(assemblyDir);
        _manager.BaseDirectoryOverride = baseDir;
        _manager.AssemblyDirectoryOverride = assemblyDir;

        var runtimeProjectDir = Path.Combine(
            _tempPath,
            "Web",
            "ForgeTrust.AppSurface.Web.Tailwind",
            "runtimes",
            "obj",
            $"ForgeTrust.AppSurface.Web.Tailwind.Runtime.{_currentHostRid}",
            "Debug",
            "net10.0");
        Directory.CreateDirectory(runtimeProjectDir);
        var expectedPath = Path.Combine(runtimeProjectDir, _currentHostRuntimeProjectBinaryName);
        File.WriteAllText(expectedPath, "dummy");

        var result = _manager.GetTailwindPath();

        Assert.Equal(expectedPath, result);
    }

    [Fact]
    public void GetTailwindPath_ThrowsFileNotFoundException_WhenSupportedRidHasNoDevelopmentRuntimeProjectInAncestorTree()
    {
        var baseDir = Path.Combine(_tempPath, "examples", "sample-app", "bin", "Debug", "net10.0");
        var assemblyDir = Path.Combine(_tempPath, "shadow", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(assemblyDir);
        _manager.BaseDirectoryOverride = baseDir;
        _manager.AssemblyDirectoryOverride = assemblyDir;
        _manager.RidOverride = _currentHostRid;
        Environment.SetEnvironmentVariable("PATH", string.Empty);

        Assert.Throws<FileNotFoundException>(() => _manager.GetTailwindPath());
    }

    [Fact]
    public void GetTailwindPath_ThrowsFileNotFoundException_WhenRidOverrideIsUnsupported()
    {
        var baseDir = Path.Combine(_tempPath, "examples", "sample-app", "bin", "Debug", "net10.0");
        var assemblyDir = Path.Combine(baseDir, "assembly-shadow");
        Directory.CreateDirectory(baseDir);
        _manager.BaseDirectoryOverride = baseDir;
        _manager.AssemblyDirectoryOverride = assemblyDir;
        _manager.RidOverride = "mystery-rid";
        Environment.SetEnvironmentVariable("PATH", string.Empty);

        Assert.Throws<FileNotFoundException>(() => _manager.GetTailwindPath());
    }

    [Fact]
    public void GetTailwindPath_ThrowsFileNotFoundException_IfNotFoundAnywhere()
    {
        Environment.SetEnvironmentVariable("PATH", string.Empty);

        // Act & Assert
        Assert.Throws<FileNotFoundException>(() => _manager.GetTailwindPath());
    }

    [Fact]
    public void GetTailwindPath_ReturnsWindowsCmdShim_IfFoundInPath()
    {
        var pathDir = Path.Combine(_tempPath, "bin");
        Directory.CreateDirectory(pathDir);
        var expectedPath = Path.Combine(pathDir, "tailwindcss.cmd");
        File.WriteAllText(expectedPath, "@echo off");
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, [pathDir, _originalPath ?? string.Empty]));
        TailwindCliManager.IsOSPlatformOverride = platform => platform == OSPlatform.Windows;

        var manager = new TailwindCliManager(_logger)
        {
            BaseDirectoryOverride = _tempPath,
            AssemblyDirectoryOverride = _tempPath,
            RidOverride = "win-x64"
        };

        var result = manager.GetTailwindPath();

        Assert.Equal(expectedPath, result);
    }

    [Fact]
    public void GetTailwindPath_ReturnsWindowsPowerShellShim_IfFoundInPath()
    {
        var pathDir = Path.Combine(_tempPath, "bin");
        Directory.CreateDirectory(pathDir);
        var expectedPath = Path.Combine(pathDir, "tailwindcss.ps1");
        File.WriteAllText(expectedPath, "Write-Output 'tailwind'");
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, [pathDir, _originalPath ?? string.Empty]));
        TailwindCliManager.IsOSPlatformOverride = platform => platform == OSPlatform.Windows;

        var manager = new TailwindCliManager(_logger)
        {
            BaseDirectoryOverride = _tempPath,
            AssemblyDirectoryOverride = _tempPath,
            RidOverride = "win-x64"
        };

        var result = manager.GetTailwindPath();

        Assert.Equal(expectedPath, result);
    }

    [Fact]
    public void BuildInvocation_UsesCommandPrompt_ForWindowsBatchShim()
    {
        TailwindCliManager.IsOSPlatformOverride = platform => platform == OSPlatform.Windows;
        var invocation = TailwindCliManager.BuildInvocation(
            @"C:\tools\tailwindcss.cmd",
            ["-i", "app.css", "-o", "site.css"]);

        Assert.Equal("cmd.exe", invocation.FileName);
        Assert.Equal(["/d", "/c", @"C:\tools\tailwindcss.cmd", "-i", "app.css", "-o", "site.css"], invocation.Arguments);
    }

    [Fact]
    public void BuildInvocation_UsesPowerShell_ForWindowsPowerShellShim()
    {
        TailwindCliManager.IsOSPlatformOverride = platform => platform == OSPlatform.Windows;
        var invocation = TailwindCliManager.BuildInvocation(
            @"C:\tools\tailwindcss.ps1",
            ["-i", "app.css", "-o", "site.css"]);

        Assert.Equal("powershell.exe", invocation.FileName);
        Assert.Equal(
            ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", @"C:\tools\tailwindcss.ps1", "-i", "app.css", "-o", "site.css"],
            invocation.Arguments);
    }

    [Fact]
    public void BuildInvocation_ReturnsDirectInvocation_ForWindowsExecutable()
    {
        TailwindCliManager.IsOSPlatformOverride = platform => platform == OSPlatform.Windows;
        var arguments = new[] { "-i", "app.css", "-o", "site.css" };

        var invocation = TailwindCliManager.BuildInvocation(
            @"C:\tools\tailwindcss.exe",
            arguments);

        Assert.Equal(@"C:\tools\tailwindcss.exe", invocation.FileName);
        Assert.Equal(arguments, invocation.Arguments);
    }

    [Fact]
    public void GetTailwindPath_ThrowsFileNotFoundException_WhenNonWindowsPathProbeHasNoMatches()
    {
        TailwindCliManager.IsOSPlatformOverride = _ => false;
        Environment.SetEnvironmentVariable("PATH", Path.Combine(_tempPath, "missing-bin"));

        Assert.Throws<FileNotFoundException>(() => _manager.GetTailwindPath());
    }

    [Fact]
    public void GetTailwindPath_ThrowsFileNotFoundException_WhenWindowsPathProbeHasNoMatchesAndPathExtIsUnavailable()
    {
        TailwindCliManager.IsOSPlatformOverride = platform => platform == OSPlatform.Windows;
        Environment.SetEnvironmentVariable("PATH", Path.Combine(_tempPath, "missing-bin"));
        Environment.SetEnvironmentVariable("PATHEXT", null);

        var manager = new TailwindCliManager(_logger)
        {
            BaseDirectoryOverride = _tempPath,
            AssemblyDirectoryOverride = _tempPath,
            RidOverride = "win-x64"
        };

        Assert.Throws<FileNotFoundException>(() => manager.GetTailwindPath());
    }

    [Fact]
    public void GetTailwindPath_ReturnsWindowsPathValue_FromNormalizedPathExtExtension()
    {
        var pathDir = Path.Combine(_tempPath, "bin-custom-ext");
        Directory.CreateDirectory(pathDir);
        var expectedPath = Path.Combine(pathDir, "tailwindcss.COM");
        File.WriteAllText(expectedPath, "dummy");
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, [pathDir, _originalPath ?? string.Empty]));
        Environment.SetEnvironmentVariable("PATHEXT", "EXE;COM");
        TailwindCliManager.IsOSPlatformOverride = platform => platform == OSPlatform.Windows;

        var manager = new TailwindCliManager(_logger)
        {
            BaseDirectoryOverride = _tempPath,
            AssemblyDirectoryOverride = _tempPath,
            RidOverride = "win-x64"
        };

        var result = manager.GetTailwindPath();

        Assert.Equal(expectedPath, result);
    }

    [Fact]
    public void GetTailwindPath_LogsDebug_WhenPathEnvironmentVariableIsUnavailable()
    {
        Environment.SetEnvironmentVariable("PATH", null);

        Assert.Throws<FileNotFoundException>(() => _manager.GetTailwindPath());

        A.CallTo(_logger)
            .Where(call => call.Method.Name == "Log"
                && call.Arguments.Count > 2
                && Equals(call.Arguments[0], LogLevel.Debug)
                && call.Arguments[2] != null
                && call.Arguments[2]!.ToString()!.Contains("PATH environment variable is not set.", StringComparison.Ordinal))
            .MustHaveHappened();
    }

    [Theory]
    [MemberData(nameof(RidMatrixCases))]
    public void ResolveRid_ReturnsExpectedMapping(OSPlatform osPlatform, Architecture architecture, string expectedRid)
    {
        var result = TailwindCliManager.ResolveRid(osPlatform, architecture);

        Assert.Equal(expectedRid, result);
    }

    [Theory]
    [MemberData(nameof(CurrentRidOverrideCases))]
    public void GetCurrentRid_UsesCurrentPlatformOverrides(
        Func<OSPlatform, bool> osPlatformOverride,
        Architecture architecture,
        string expectedRid)
    {
        TailwindCliManager.IsOSPlatformOverride = osPlatformOverride;
        TailwindCliManager.ProcessArchitectureOverride = () => architecture;

        var result = TailwindCliManager.GetCurrentRid();

        Assert.Equal(expectedRid, result);
    }

    public static IEnumerable<object[]> RidMatrixCases()
    {
        yield return [OSPlatform.Windows, Architecture.X64, "win-x64"];
        yield return [OSPlatform.Windows, Architecture.Arm64, "win-x64"];
        yield return [OSPlatform.Windows, Architecture.X86, "unknown"];
        yield return [OSPlatform.Linux, Architecture.X64, "linux-x64"];
        yield return [OSPlatform.Linux, Architecture.Arm64, "linux-arm64"];
        yield return [OSPlatform.Linux, Architecture.X86, "unknown"];
        yield return [OSPlatform.OSX, Architecture.X64, "osx-x64"];
        yield return [OSPlatform.OSX, Architecture.Arm64, "osx-arm64"];
        yield return [OSPlatform.OSX, Architecture.X86, "unknown"];
        yield return [OSPlatform.Create("FREEBSD"), Architecture.X64, "unknown"];
    }

    public static IEnumerable<object[]> DevelopmentRuntimeRidCases()
    {
        yield return ["win-x64", "tailwindcss-windows-x64.exe"];
        yield return ["osx-arm64", "tailwindcss-macos-arm64"];
        yield return ["osx-x64", "tailwindcss-macos-x64"];
        yield return ["linux-arm64", "tailwindcss-linux-arm64"];
        yield return ["linux-x64", "tailwindcss-linux-x64"];
    }

    public static IEnumerable<object[]> CurrentRidOverrideCases()
    {
        yield return [(Func<OSPlatform, bool>)(platform => platform == OSPlatform.Windows), Architecture.X64, "win-x64"];
        yield return [(Func<OSPlatform, bool>)(platform => platform == OSPlatform.Linux), Architecture.Arm64, "linux-arm64"];
        yield return [(Func<OSPlatform, bool>)(platform => platform == OSPlatform.OSX), Architecture.X64, "osx-x64"];
        yield return [(Func<OSPlatform, bool>)(_ => false), Architecture.X64, "unknown"];
    }

    private static string GetRuntimeProjectBinaryName(string rid) => rid switch
    {
        "win-x64" => "tailwindcss-windows-x64.exe",
        "osx-arm64" => "tailwindcss-macos-arm64",
        "osx-x64" => "tailwindcss-macos-x64",
        "linux-arm64" => "tailwindcss-linux-arm64",
        "linux-x64" => "tailwindcss-linux-x64",
        _ => throw new InvalidOperationException($"Unsupported RID for test: {rid}")
    };

    private static string GetSupportedHostRid()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "win-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "osx-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "linux-x64";
        }

        throw new PlatformNotSupportedException("Tailwind CLI manager tests require a supported host operating system.");
    }
}
