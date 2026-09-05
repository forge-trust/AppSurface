extern alias TailwindTasks;

using System.Collections;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Web.Tailwind.Internal;
using Microsoft.Build.Framework;
using TailwindBuildTask = TailwindTasks::ForgeTrust.AppSurface.Web.Tailwind.Tasks.RunTailwindBuildTask;

namespace ForgeTrust.AppSurface.Web.Tailwind.Tests;

/// <summary>
/// Verifies the host-scoped Tailwind package contract and MSBuild-task integration.
/// </summary>
public sealed class TailwindBuildTargetsTests : IDisposable
{
    private readonly string _tempRoot = Path.Join(Path.GetTempPath(), "tailwind-build-targets-tests-" + Guid.NewGuid().ToString("N"));

    public TailwindBuildTargetsTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void TailwindProject_DoesNotReferenceRuntimeCompanionProjects()
    {
        var document = XDocument.Load(GetTailwindProjectPath());
        var references = document.Descendants("ProjectReference")
            .Select(static element => (string?)element.Attribute("Include"))
            .Where(static include => include is not null)
            .ToArray();

        Assert.DoesNotContain(references, static include => include!.Contains("Tailwind.Runtime.", StringComparison.Ordinal));
    }

    [Fact]
    public void Targets_PassThePackedReleaseManifestToTheBuildTask()
    {
        var targets = File.ReadAllText(GetTailwindTargetsPath());

        Assert.Contains("tailwind.release.json", targets, StringComparison.Ordinal);
        Assert.Contains("TailwindReleaseManifestPath=\"$(_TailwindReleaseManifest)\"", targets, StringComparison.Ordinal);
        Assert.Contains("$(_TailwindVersionFile);$(_TailwindReleaseManifest)", targets, StringComparison.Ordinal);
        Assert.DoesNotContain("TailwindRuntimeBinaryResolutionEnabled", targets, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTailwindBuildTask_UsesExplicitPathWithoutAReleaseManifest()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var projectDirectory = Path.Join(_tempRoot, "explicit");
        var markerPath = Path.Join(projectDirectory, "marker");
        var cliPath = await CreateExecutableStubAsync(projectDirectory, markerPath, exitCode: 0);
        var buildEngine = new RecordingBuildEngine();
        var task = CreateTask(projectDirectory, buildEngine, configure: task => task.TailwindCliPath = cliPath);

        var result = task.Execute();

        Assert.True(result);
        Assert.True(File.Exists(markerPath));
        Assert.Empty(buildEngine.Errors);
    }

    [Fact]
    public async Task RunTailwindBuildTask_UsesExplicitPathBeforeLoadingAnInvalidReleaseManifest()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var projectDirectory = Path.Join(_tempRoot, "explicit-invalid-manifest");
        var markerPath = Path.Join(projectDirectory, "marker");
        var cliPath = await CreateExecutableStubAsync(projectDirectory, markerPath, exitCode: 0);
        var invalidManifestPath = Path.Join(projectDirectory, "invalid.release.json");
        await File.WriteAllTextAsync(invalidManifestPath, "{");
        var buildEngine = new RecordingBuildEngine();
        var task = CreateTask(projectDirectory, buildEngine, configure: task =>
        {
            task.TailwindCliPath = cliPath;
            task.TailwindReleaseManifestPath = invalidManifestPath;
        });

        var result = task.Execute();

        Assert.True(result);
        Assert.True(File.Exists(markerPath));
        Assert.Empty(buildEngine.Errors);
    }

    [Fact]
    public void RunTailwindBuildTask_ReportsAstw003ForMissingExplicitPath()
    {
        var projectDirectory = Path.Join(_tempRoot, "missing-explicit");
        Directory.CreateDirectory(projectDirectory);
        var buildEngine = new RecordingBuildEngine();
        var task = CreateTask(projectDirectory, buildEngine, configure: task => task.TailwindCliPath = "tools/missing-tailwindcss");

        var result = task.Execute();

        Assert.False(result);
        Assert.Contains(buildEngine.Errors, error => error.Message?.Contains("ASTW003", StringComparison.Ordinal) is true);
    }

    [Fact]
    public void RunTailwindBuildTask_ReportsAstw001ForUnsupportedHostWithoutExplicitPath()
    {
        var projectDirectory = Path.Join(_tempRoot, "unsupported-host");
        Directory.CreateDirectory(projectDirectory);
        var buildEngine = new RecordingBuildEngine();
        var task = CreateTask(projectDirectory, buildEngine, configure: task =>
        {
            task.TailwindReleaseManifestPath = GetReleaseManifestPath();
            task.TailwindVersion = "4.1.18";
            task.TailwindTargetRid = "unknown";
        });

        var result = task.Execute();

        Assert.False(result);
        Assert.Contains(buildEngine.Errors, error => error.Message?.Contains("ASTW001", StringComparison.Ordinal) is true);
    }

    [Fact]
    public void RunTailwindBuildTask_ReportsAstw002ForMissingVersion()
    {
        var projectDirectory = Path.Join(_tempRoot, "missing-version");
        Directory.CreateDirectory(projectDirectory);
        var buildEngine = new RecordingBuildEngine();
        var task = CreateTask(projectDirectory, buildEngine, configure: task =>
        {
            task.TailwindReleaseManifestPath = GetReleaseManifestPath();
            task.TailwindTargetRid = "linux-x64";
        });

        var result = task.Execute();

        Assert.False(result);
        Assert.Contains(buildEngine.Errors, error => error.Message?.Contains("ASTW002", StringComparison.Ordinal) is true);
    }

    [Fact]
    public void RunTailwindBuildTask_ReportsAstw012ForInvalidVersionBeforeNetworkWork()
    {
        var projectDirectory = Path.Join(_tempRoot, "invalid-version");
        Directory.CreateDirectory(projectDirectory);
        var buildEngine = new RecordingBuildEngine();
        var task = CreateTask(projectDirectory, buildEngine, configure: task =>
        {
            task.TailwindReleaseManifestPath = GetReleaseManifestPath();
            task.TailwindVersion = "4.1.18-preview";
            task.TailwindTargetRid = "linux-x64";
        });

        var result = task.Execute();

        Assert.False(result);
        Assert.Contains(buildEngine.Errors, error => error.Message?.Contains("ASTW012", StringComparison.Ordinal) is true);
        Assert.DoesNotContain(Directory.EnumerateFiles(projectDirectory, "*.partial-*", SearchOption.AllDirectories), static _ => true);
    }

    [Fact]
    public async Task RunTailwindBuildTask_ReportsAstw006ForNonZeroExplicitCliExit()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var projectDirectory = Path.Join(_tempRoot, "non-zero-exit");
        var markerPath = Path.Join(projectDirectory, "marker");
        var cliPath = await CreateExecutableStubAsync(projectDirectory, markerPath, exitCode: 1);
        var buildEngine = new RecordingBuildEngine();
        var task = CreateTask(projectDirectory, buildEngine, configure: task => task.TailwindCliPath = cliPath);

        var result = task.Execute();

        Assert.False(result);
        Assert.True(File.Exists(markerPath));
        Assert.Contains(buildEngine.Errors, error => error.Message?.Contains("ASTW006", StringComparison.Ordinal) is true);
    }

    [Fact]
    public async Task RunTailwindBuildTask_ReportsClassifiedOutputAndCapturedStderrForANonZeroExit()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var projectDirectory = Path.Join(_tempRoot, "stderr-classification");
        var cliPath = await CreateOutputStubAsync(projectDirectory);
        var buildEngine = new RecordingBuildEngine();
        var task = CreateTask(projectDirectory, buildEngine, configure: task => task.TailwindCliPath = cliPath);

        var result = task.Execute();

        Assert.False(result);
        Assert.Contains(buildEngine.Messages, message => message.Message?.Contains("≈ tailwindcss v4.1.18", StringComparison.Ordinal) is true);
        Assert.Contains(buildEngine.Messages, message => message.Message?.Contains("Done in 34ms", StringComparison.Ordinal) is true);
        Assert.Contains(buildEngine.Warnings, warning => warning.Message?.Contains("Error: boom", StringComparison.Ordinal) is true);
        Assert.Contains(buildEngine.Errors, error => error.Message?.Contains("Captured stderr tail:", StringComparison.Ordinal) is true);
    }

    [Fact]
    public async Task RunTailwindBuildTask_ReportsAstw005WhenAnExplicitCliCannotStart()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var projectDirectory = Path.Join(_tempRoot, "process-start");
        var toolDirectory = Path.Join(projectDirectory, "tools");
        Directory.CreateDirectory(toolDirectory);
        var cliPath = Path.Join(toolDirectory, "tailwindcss");
        await File.WriteAllTextAsync(cliPath, "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(cliPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        var buildEngine = new RecordingBuildEngine();
        var task = CreateTask(projectDirectory, buildEngine, configure: task => task.TailwindCliPath = cliPath);

        var result = task.Execute();

        Assert.False(result);
        Assert.Contains(buildEngine.Errors, error => error.Message?.Contains("ASTW005", StringComparison.Ordinal) is true);
    }

    [Fact]
    public void RunTailwindBuildTask_ReportsAstw012ForMalformedReleaseManifest()
    {
        var projectDirectory = Path.Join(_tempRoot, "malformed-manifest");
        Directory.CreateDirectory(projectDirectory);
        var manifestPath = Path.Join(projectDirectory, "tailwind.release.json");
        File.WriteAllText(manifestPath, "{");
        var buildEngine = new RecordingBuildEngine();
        var task = CreateTask(projectDirectory, buildEngine, configure: task =>
        {
            task.TailwindReleaseManifestPath = manifestPath;
            task.TailwindVersion = "4.1.18";
            task.TailwindTargetRid = "linux-x64";
        });

        var result = task.Execute();

        Assert.False(result);
        Assert.Contains(buildEngine.Errors, error => error.Message?.Contains("ASTW012", StringComparison.Ordinal) is true);
        Assert.Contains(buildEngine.Errors, error => error.Message?.Contains("not valid JSON", StringComparison.Ordinal) is true);
    }

    [Fact]
    public void RunTailwindBuildTask_ReportsAstw012ForAnInvalidReleaseManifestPath()
    {
        var projectDirectory = Path.Join(_tempRoot, "invalid-manifest-path");
        Directory.CreateDirectory(projectDirectory);
        var buildEngine = new RecordingBuildEngine();
        var task = CreateTask(projectDirectory, buildEngine, configure: task =>
        {
            task.TailwindReleaseManifestPath = "\0";
            task.TailwindVersion = "4.1.18";
            task.TailwindTargetRid = "linux-x64";
        });

        var result = task.Execute();

        Assert.False(result);
        Assert.Contains(buildEngine.Errors, error => error.Message?.Contains("ASTW012", StringComparison.Ordinal) is true);
    }

    [Fact]
    public void RunTailwindBuildTask_ReportsAstw012WhenTheReleaseManifestIsMissing()
    {
        var projectDirectory = Path.Join(_tempRoot, "missing-manifest");
        Directory.CreateDirectory(projectDirectory);
        var buildEngine = new RecordingBuildEngine();
        var task = CreateTask(projectDirectory, buildEngine, configure: task =>
        {
            task.TailwindCliPath = null;
            task.TailwindReleaseManifestPath = null;
        });

        var result = task.Execute();

        Assert.False(result);
        Assert.Contains(buildEngine.Errors, error =>
            error.Message?.Contains("ASTW012", StringComparison.Ordinal) is true
            && error.Message.Contains("release manifest is missing", StringComparison.Ordinal));
    }

    [Fact]
    public void RunTailwindBuildTask_ReportsSafeCacheIdentityForClassifiedAcquisitionFailures()
    {
        var projectDirectory = Path.Join(_tempRoot, "cache-identity");
        Directory.CreateDirectory(projectDirectory);
        var buildEngine = new RecordingBuildEngine();
        var task = CreateTask(projectDirectory, buildEngine, configure: task =>
        {
            task.TailwindReleaseManifestPath = GetReleaseManifestPath();
            task.TailwindVersion = "4.1.18";
            task.TailwindTargetRid = "linux-x64";
            task.TailwindDownloadCacheRoot = "\0";
        });

        var result = task.Execute();

        Assert.False(result);
        Assert.Contains(buildEngine.Errors, error =>
            error.Message?.Contains("ASTW012", StringComparison.Ordinal) is true
            && error.Message.Contains("Safe cache identity: tailwind-4.1.18/linux-x64.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunTailwindBuildTask_ReportsAstw007WhenCanceledWhileWaitingForTheCacheLock()
    {
        var projectDirectory = Path.Join(_tempRoot, "resolution-cancellation");
        Directory.CreateDirectory(projectDirectory);
        var manifest = TailwindReleaseManifest.LoadFromFile(GetReleaseManifestPath());
        var asset = manifest.GetAsset("linux-x64");
        var cacheRoot = Path.Join(projectDirectory, "cache");
        var cacheEntry = TailwindDownloadCache.GetRuntimeBinaryPath(cacheRoot, manifest.Version, asset.Rid, asset.BinaryName);
        Directory.CreateDirectory(Path.GetDirectoryName(cacheEntry)!);
        await using var heldLock = new FileStream(cacheEntry + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var buildEngine = new RecordingBuildEngine();
        var task = CreateTask(projectDirectory, buildEngine, configure: task =>
        {
            task.TailwindReleaseManifestPath = GetReleaseManifestPath();
            task.TailwindVersion = manifest.Version;
            task.TailwindTargetRid = asset.Rid;
            task.TailwindDownloadCacheRoot = cacheRoot;
        });

        var execution = Task.Run(task.Execute);
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        task.Cancel();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result);
        Assert.Contains(buildEngine.Errors, error => error.Message?.Contains("ASTW007", StringComparison.Ordinal) is true);
    }

    [Fact]
    public void TailwindProject_PacksManifestAndDoesNotDeclareRuntimeCompanionDependencies()
    {
        var project = XDocument.Load(GetTailwindProjectPath());
        var manifest = Assert.Single(
            project.Descendants("None"),
            static item => string.Equals((string?)item.Attribute("Include"), "tailwind.release.json", StringComparison.Ordinal));

        Assert.Equal("true", (string?)manifest.Attribute("Pack"));
        Assert.Equal("build", (string?)manifest.Attribute("PackagePath"));
        Assert.DoesNotContain(
            project.Descendants("PackageReference").Select(static item => (string?)item.Attribute("Include")),
            static id => id?.StartsWith("ForgeTrust.AppSurface.Web.Tailwind.Runtime.", StringComparison.OrdinalIgnoreCase) is true);
    }

    private static TailwindBuildTask CreateTask(
        string projectDirectory,
        RecordingBuildEngine buildEngine,
        Action<TailwindBuildTask>? configure = null)
    {
        var task = new TailwindBuildTask
        {
            BuildEngine = buildEngine,
            ProjectDirectory = projectDirectory,
            InputPath = "wwwroot/css/app.css",
            OutputPath = "wwwroot/css/site.gen.css",
            TargetsDirectory = projectDirectory,
            TailwindDownloadCacheRoot = Path.Join(projectDirectory, "cache")
        };
        configure?.Invoke(task);
        return task;
    }

    private static async Task<string> CreateExecutableStubAsync(string projectDirectory, string markerPath, int exitCode)
    {
        var toolDirectory = Path.Join(projectDirectory, "tools");
        Directory.CreateDirectory(toolDirectory);
        var path = Path.Join(toolDirectory, "tailwindcss");
        await File.WriteAllTextAsync(path, $"#!/bin/sh\nprintf invoked > \"{markerPath}\"\nexit {exitCode}\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    private static async Task<string> CreateOutputStubAsync(string projectDirectory)
    {
        var toolDirectory = Path.Join(projectDirectory, "tools");
        Directory.CreateDirectory(toolDirectory);
        var path = Path.Join(toolDirectory, "tailwindcss");
        await File.WriteAllTextAsync(path, "#!/bin/sh\nprintf 'generated css\\n'\nprintf '≈ tailwindcss v4.1.18\\nDone in 34ms\\nError: boom\\n' >&2\nexit 1\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    private static string GetRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Join(current.FullName, "ForgeTrust.AppSurface.slnx")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static string GetTailwindProjectPath()
    {
        return Path.Join(GetRepositoryRoot(), "Web", "ForgeTrust.AppSurface.Web.Tailwind", "ForgeTrust.AppSurface.Web.Tailwind.csproj");
    }

    private static string GetTailwindTargetsPath()
    {
        return Path.Join(GetRepositoryRoot(), "Web", "ForgeTrust.AppSurface.Web.Tailwind", "build", "ForgeTrust.AppSurface.Web.Tailwind.targets");
    }

    private static string GetReleaseManifestPath()
    {
        return Path.Join(GetRepositoryRoot(), "Web", "ForgeTrust.AppSurface.Web.Tailwind", "tailwind.release.json");
    }

    private sealed class RecordingBuildEngine : IBuildEngine
    {
        public List<BuildErrorEventArgs> Errors { get; } = [];

        public List<BuildWarningEventArgs> Warnings { get; } = [];

        public List<BuildMessageEventArgs> Messages { get; } = [];

        public bool ContinueOnError => false;

        public int LineNumberOfTaskNode => 0;

        public int ColumnNumberOfTaskNode => 0;

        public string ProjectFileOfTaskNode => string.Empty;

        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e);

        public void LogWarningEvent(BuildWarningEventArgs e) => Warnings.Add(e);

        public void LogMessageEvent(BuildMessageEventArgs e) => Messages.Add(e);

        public void LogCustomEvent(CustomBuildEventArgs e)
        {
        }

        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => true;
    }
}
