using System.Text.Json;
using System.Text.Json.Nodes;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageRunCollectorVersionPreflightTests
{
    [Fact]
    public void CollectorVersionPreflight_ShouldAcceptExactDirectReferenceVersion()
    {
        var json = CapabilityJson("net10.0", version: "10.0.1");

        Assert.True(CoverageRunCollectorVersionPreflight.HasExactVersion(json, "net10.0", "10.0.1"));
        Assert.False(CoverageRunCollectorVersionPreflight.HasExactVersion(json, "net10.0", "10.0.0"));
    }

    [Fact]
    public void CollectorVersionPreflight_ShouldPreferPackageReferenceVersionOverride()
    {
        var exactOverride = CapabilityJson("net10.0", version: "10.0.0", versionOverride: "10.0.1");
        var conflictingOverride = CapabilityJson("net10.0", version: "10.0.1", versionOverride: "10.0.0");

        Assert.True(CoverageRunCollectorVersionPreflight.HasExactVersion(exactOverride, "net10.0", "10.0.1"));
        Assert.False(CoverageRunCollectorVersionPreflight.HasExactVersion(conflictingOverride, "net10.0", "10.0.1"));
    }

    [Fact]
    public void CollectorVersionPreflight_ShouldUseCentralPackageVersionWhenReferenceVersionIsEmpty()
    {
        var json = CapabilityJson("net10.0", centralVersion: "10.0.1");

        Assert.True(CoverageRunCollectorVersionPreflight.HasExactVersion(json, "net10.0", "10.0.1"));
        Assert.False(CoverageRunCollectorVersionPreflight.HasExactVersion(
            CapabilityJson("net10.0", centralVersion: "10.0.0"),
            "net10.0",
            "10.0.1"));
    }

    [Fact]
    public void CollectorVersionPreflight_ShouldRejectMissingOrAmbiguousEvaluatedVersions()
    {
        var missing = CapabilityJson("net10.0");
        var duplicateCentral = CapabilityJson("net10.0", centralVersions: ["10.0.1", "10.0.1"]);

        Assert.False(CoverageRunCollectorVersionPreflight.HasExactVersion(missing, "net10.0", "10.0.1"));
        Assert.False(CoverageRunCollectorVersionPreflight.HasExactVersion(duplicateCentral, "net10.0", "10.0.1"));
        Assert.False(CoverageRunCollectorVersionPreflight.HasExactVersion(missing, "net10.0", " "));
    }

    [Fact]
    public void CollectorVersionPreflight_ShouldRejectAResultForAnotherFramework()
    {
        Assert.False(CoverageRunCollectorVersionPreflight.HasExactVersion(
            CapabilityJson("net8.0", version: "10.0.1"),
            "net10.0",
            "10.0.1"));
    }

    [Fact]
    public void CollectorVersionPreflight_ShouldRejectANullRequiredVersion()
    {
        Assert.False(CoverageRunCollectorVersionPreflight.HasExactVersion(
            CapabilityJson("net10.0", version: "10.0.1"),
            "net10.0",
            null!));
    }

    [Fact]
    public async Task DriverPreflight_ShouldQueryCentralVersionsAndEvaluateEverySelectedFramework()
    {
        using var directory = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        await using var supervisor = CreateSupervisor(console);
        var projectPath = Path.Join(directory.Path, "Sample.Tests.csproj");
        var project = new CoverageRunProject("Sample.Tests.csproj", projectPath, "sample-tests", false);
        var resolution = new CoverageProjectResolution(null, directory.Path, [project], []);
        var runner = new CapabilityProcessRunner(request =>
        {
            var frameworkArgument = request.Arguments
                .FirstOrDefault(argument => argument.StartsWith("-property:TargetFramework=", StringComparison.Ordinal));
            var framework = frameworkArgument is null ? null : frameworkArgument.Split('=', 2)[1];
            return framework switch
            {
                null => CapabilityJson("", targetFrameworks: ["net8.0", "net10.0"]),
                "net8.0" => CapabilityJson("net8.0", version: "10.0.0"),
                "net10.0" => CapabilityJson("net10.0", centralVersion: "10.0.1"),
                _ => throw new InvalidOperationException($"Unexpected target framework '{framework}'."),
            };
        });
        var observedFrameworks = new List<string?>();

        var exception = await Assert.ThrowsAsync<CoverageExecutionException>(() => CoverageRunDriverPreflight.ValidateAsync(
            CoverageRunDriver.Collector,
            "Release",
            resolution,
            runner,
            supervisor,
            CancellationToken.None,
            observeCapabilities: (_, framework, _) => observedFrameworks.Add(framework),
            requiredCollectorVersion: "10.0.1"));

        Assert.Contains("net8.0", exception.Message, StringComparison.Ordinal);
        Assert.Equal(new string?[] { null, "net8.0", "net10.0" }, observedFrameworks);
        Assert.Equal(4, runner.Requests.Count);
        Assert.Equal("restore", runner.Requests[0].Arguments[0]);
        Assert.All(runner.Requests.Skip(1), request => Assert.Contains("-getItem:PackageReference,PackageVersion", request.Arguments));
        Assert.Contains(runner.Requests[2].Arguments, argument => argument == "-property:TargetFramework=net8.0");
        Assert.Contains(runner.Requests[3].Arguments, argument => argument == "-property:TargetFramework=net10.0");
    }

    [Fact]
    public async Task DriverPreflight_ShouldKeepLegacyCapabilityQueryWhenExactVersionIsNotRequired()
    {
        using var directory = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        await using var supervisor = CreateSupervisor(console);
        var project = new CoverageRunProject(
            "Sample.Tests.csproj",
            Path.Join(directory.Path, "Sample.Tests.csproj"),
            "sample-tests",
            false);
        var resolution = new CoverageProjectResolution(null, directory.Path, [project], []);
        var runner = new CapabilityProcessRunner(_ => CapabilityJson("net10.0", version: "10.0.1"));

        await CoverageRunDriverPreflight.ValidateAsync(
            CoverageRunDriver.Collector,
            "Release",
            resolution,
            runner,
            supervisor,
            CancellationToken.None);

        Assert.Single(runner.Requests);
        Assert.Contains("-getItem:PackageReference", runner.Requests[0].Arguments);
        Assert.DoesNotContain("-getItem:PackageReference,PackageVersion", runner.Requests[0].Arguments);
    }

    [Fact]
    public async Task DriverPreflight_ShouldRejectARestoredCollectorVersionMismatchForASelectedFramework()
    {
        using var directory = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        await using var supervisor = CreateSupervisor(console);
        var projectPath = Path.Join(directory.Path, "Sample.Tests.csproj");
        var project = new CoverageRunProject("Sample.Tests.csproj", projectPath, "sample-tests", false);
        var resolution = new CoverageProjectResolution(null, directory.Path, [project], []);
        var assetsPath = Path.Join(directory.Path, "obj", "project.assets.json");
        Directory.CreateDirectory(Path.GetDirectoryName(assetsPath)!);
        await File.WriteAllTextAsync(assetsPath, RestoredAssetsJson(projectPath,
            ("net8.0", "10.0.1"),
            ("net10.0", "10.0.2")));
        var runner = new CapabilityProcessRunner(request =>
        {
            if (request.Arguments[0] == "restore")
            {
                return string.Empty;
            }

            var frameworkArgument = request.Arguments
                .FirstOrDefault(argument => argument.StartsWith("-property:TargetFramework=", StringComparison.Ordinal));
            var framework = frameworkArgument is null ? null : frameworkArgument.Split('=', 2)[1];
            return framework switch
            {
                null => CapabilityJson("", targetFrameworks: ["net8.0", "net10.0"], projectAssetsFile: assetsPath, projectPath: projectPath),
                "net8.0" => CapabilityJson("net8.0", version: "10.0.1", projectAssetsFile: assetsPath, projectPath: projectPath),
                "net10.0" => CapabilityJson("net10.0", version: "10.0.1", projectAssetsFile: assetsPath, projectPath: projectPath),
                _ => throw new InvalidOperationException($"Unexpected target framework '{framework}'."),
            };
        });

        var exception = await Assert.ThrowsAsync<CoverageExecutionException>(() => CoverageRunDriverPreflight.ValidateAsync(
            CoverageRunDriver.Collector,
            "Release",
            resolution,
            runner,
            supervisor,
            CancellationToken.None,
            requiredCollectorVersion: "10.0.1"));

        Assert.Contains("net10.0", exception.Message, StringComparison.Ordinal);
        var restoreIndex = runner.Requests.FindIndex(request => request.Arguments.FirstOrDefault() == "restore");
        var frameworkCheckIndex = runner.Requests.FindIndex(request => request.Arguments.Contains("-property:TargetFramework=net8.0"));
        Assert.True(restoreIndex >= 0 && restoreIndex < frameworkCheckIndex);
    }

    [Fact]
    public async Task DriverPreflight_ShouldRejectAnIncorrectSingleFrameworkCollectorVersion()
    {
        using var directory = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        await using var supervisor = CreateSupervisor(console);
        var project = new CoverageRunProject(
            "Sample.Tests.csproj",
            Path.Join(directory.Path, "Sample.Tests.csproj"),
            "sample-tests",
            false);
        var resolution = new CoverageProjectResolution(null, directory.Path, [project], []);
        var runner = new CapabilityProcessRunner(_ => CapabilityJson("net10.0", version: "10.0.0"));
        var observedFrameworks = new List<string?>();

        var exception = await Assert.ThrowsAsync<CoverageExecutionException>(() => CoverageRunDriverPreflight.ValidateAsync(
            CoverageRunDriver.Collector,
            "Release",
            resolution,
            runner,
            supervisor,
            CancellationToken.None,
            observeCapabilities: (_, framework, _) => observedFrameworks.Add(framework),
            requiredCollectorVersion: "10.0.1"));

        Assert.Contains("Sample.Tests.csproj: coverlet.collector must be exactly 10.0.1", exception.Message, StringComparison.Ordinal);
        Assert.Equal(new string?[] { null }, observedFrameworks);
        Assert.Equal(2, runner.Requests.Count);
        Assert.Equal("restore", runner.Requests[0].Arguments[0]);
        Assert.Contains("-getItem:PackageReference,PackageVersion", runner.Requests[1].Arguments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DriverPreflight_ShouldAcceptTheRestoredGraphAndHonorNoRestore(bool noRestore)
    {
        using var directory = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        await using var supervisor = CreateSupervisor(console);
        var projectPath = directory.WriteFile("Sample.Tests.csproj", "<Project />");
        var assetsPath = directory.WriteFile("obj/project.assets.json", RestoredAssetsJson(projectPath, ("net10.0", "10.0.1")));
        var project = new CoverageRunProject("Sample.Tests.csproj", projectPath, "sample-tests", false);
        var runner = new CapabilityProcessRunner(_ => CapabilityJson("net10.0", centralVersion: "10.0.1",
            projectAssetsFile: assetsPath, projectPath: projectPath));

        await CoverageRunDriverPreflight.ValidateAsync(CoverageRunDriver.Collector, "Release",
            new CoverageProjectResolution(null, directory.Path, [project], []), runner, supervisor,
            CancellationToken.None, requiredCollectorVersion: "10.0.1", noRestore: noRestore);

        Assert.Equal(noRestore ? 1 : 2, runner.Requests.Count);
        Assert.Equal(noRestore ? "msbuild" : "restore", runner.Requests[0].Arguments[0]);
        Assert.Contains("-property:Configuration=Release", runner.Requests[0].Arguments);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("valid-rid")]
    [InlineData("higher-version")]
    [InlineData("rid-mismatch")]
    [InlineData("missing-framework")]
    [InlineData("wrong-project")]
    [InlineData("not-package")]
    [InlineData("missing-library")]
    [InlineData("duplicate-target")]
    [InlineData("malformed")]
    [InlineData("oversize")]
    [InlineData("missing-assets")]
    [InlineData("linked-assets")]
    public void RestoredCollectorProof_ShouldValidateAllSelectedGraphsAndBoundTheRead(string mutation)
    {
        using var directory = TestDirectory.Create();
        var projectPath = directory.WriteFile("Sample.Tests.csproj", "<Project />");
        var graph = JsonNode.Parse(RestoredAssetsJson(projectPath, ("net10.0", "10.0.1")))!.AsObject();
        var targets = graph["targets"]!.AsObject();
        switch (mutation)
        {
            case "valid": break;
            case "valid-rid": targets["net10.0/linux-x64"] = targets["net10.0"]!.DeepClone(); break;
            case "higher-version": graph = JsonNode.Parse(RestoredAssetsJson(projectPath, ("net10.0", "10.0.2")))!.AsObject(); break;
            case "rid-mismatch": targets["net10.0/linux-x64"] = JsonNode.Parse("{\"coverlet.collector/10.0.2\":{\"type\":\"package\"}}"); break;
            case "missing-framework": targets.Clear(); break;
            case "wrong-project": graph["project"]!["restore"]!["projectPath"] = TestPathUtils.PathUnder(directory.Path, "Other.csproj"); break;
            case "not-package": targets["net10.0"]!["coverlet.collector/10.0.1"]!["type"] = "project"; break;
            case "missing-library": graph["libraries"]!.AsObject().Clear(); break;
            case "duplicate-target":
            case "malformed":
            case "oversize":
            case "missing-assets":
            case "linked-assets": break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        var json = graph.ToJsonString();
        if (mutation == "duplicate-target")
            json = json.Replace("\"targets\":{", "\"targets\":{\"net10.0\":{},", StringComparison.Ordinal);
        if (mutation == "malformed") json = "{";
        if (mutation == "oversize") json += new string(' ', 1024 * 1024);
        var assetsPath = directory.WriteFile("obj/project.assets.json", json);
        if (mutation == "missing-assets") File.Delete(assetsPath);
        if (mutation == "linked-assets")
        {
            var outside = directory.WriteFile("outside.json", json);
            File.Delete(assetsPath);
            TestFileSystem.CreateFileLinkOrSkip(assetsPath, outside);
        }
        var capability = CapabilityJson("net10.0", version: "10.0.1", projectAssetsFile: assetsPath, projectPath: projectPath);

        Assert.Equal(mutation is "valid" or "valid-rid",
            CoverageRunCollectorVersionPreflight.HasRestoredVersion(capability, "net10.0", "10.0.1", projectPath));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"Properties\":{},\"Properties\":{},\"Items\":{\"PackageReference\":[]}}")]
    [InlineData("{\"Properties\":{\"TargetFramework\":\"net10.0\"}}")]
    [InlineData("{\"Properties\":{\"TargetFramework\":\"net10.0\"},\"Items\":{\"PackageReference\":{}}}")]
    [InlineData("{\"Properties\":{\"TargetFramework\":\"net10.0\"},\"Items\":{\"PackageReference\":[{\"Identity\":\"coverlet.collector\",\"Version\":null}]}}")]
    [InlineData("{\"Properties\":{\"TargetFramework\":\"net10.0\"},\"Items\":{\"PackageReference\":[{\"Identity\":\"coverlet.collector\",\"VersionOverride\":10,\"Version\":\"10.0.1\"}]}}")]
    [InlineData("{\"Properties\":{\"TargetFramework\":\"net10.0\"},\"Items\":{\"PackageReference\":[{\"Identity\":\"coverlet.collector\",\"Version\":\"10.0.1\",\"Version\":\"10.0.1\"}]}}")]
    [InlineData("{\"Properties\":{\"TargetFramework\":\"net10.0\"},\"Items\":{\"PackageReference\":[{\"Identity\":\"coverlet.collector\"}]}}")]
    [InlineData("{\"Properties\":{\"TargetFramework\":\"net10.0\"},\"Items\":{\"PackageReference\":[{\"Identity\":\"coverlet.collector\",\"Version\":10}]}}")]
    [InlineData("{\"Properties\":{\"TargetFramework\":\"net10.0\"},\"Items\":{\"PackageReference\":[{\"Identity\":\"coverlet.collector\"}],\"PackageVersion\":[{\"Identity\":\"coverlet.collector\",\"Version\":null}]}}")]
    public void CollectorVersionPreflight_ShouldRejectMalformedOrAmbiguousCapabilityProof(string json)
    {
        Assert.False(CoverageRunCollectorVersionPreflight.HasExactVersion(json, "net10.0", "10.0.1"));
    }

    private static string CapabilityJson(
        string targetFramework,
        string? version = null,
        string? versionOverride = null,
        string? centralVersion = null,
        IReadOnlyList<string>? centralVersions = null,
        IReadOnlyList<string>? targetFrameworks = null,
        string? projectAssetsFile = null,
        string? projectPath = null)
    {
        var properties = new Dictionary<string, object?>
        {
            ["TargetFramework"] = targetFramework,
            ["TargetFrameworks"] = targetFrameworks is { Count: > 0 } ? string.Join(";", targetFrameworks) : string.Empty,
        };
        if (projectAssetsFile is not null)
        {
            properties["ProjectAssetsFile"] = projectAssetsFile;
            properties["MSBuildProjectExtensionsPath"] = Path.GetDirectoryName(projectAssetsFile);
            properties["MSBuildProjectFullPath"] = projectPath;
        }

        var packageReference = new Dictionary<string, object?> { ["Identity"] = "coverlet.collector" };
        if (version is not null)
        {
            packageReference["Version"] = version;
        }

        if (versionOverride is not null)
        {
            packageReference["VersionOverride"] = versionOverride;
        }

        IReadOnlyList<string> packageVersions = centralVersions
            ?? (centralVersion is null ? Array.Empty<string>() : new[] { centralVersion });
        var itemVersions = packageVersions
            .Select(value => (object)new Dictionary<string, object?>
            {
                ["Identity"] = "coverlet.collector",
                ["Version"] = value,
            })
            .ToArray();
        return JsonSerializer.Serialize(new
        {
            Properties = properties,
            Items = new
            {
                PackageReference = new[] { packageReference },
                PackageVersion = itemVersions,
            },
        });
    }

    private static string RestoredAssetsJson(string projectPath, params (string Framework, string Version)[] collectors)
    {
        var targets = new Dictionary<string, object?>();
        var libraries = new Dictionary<string, object?>();
        foreach (var (framework, version) in collectors)
        {
            var packageIdentity = $"coverlet.collector/{version}";
            targets[framework] = new Dictionary<string, object?>
            {
                [packageIdentity] = new Dictionary<string, object?> { ["type"] = "package" },
            };
            libraries[packageIdentity] = new Dictionary<string, object?>
            {
                ["type"] = "package",
                ["path"] = packageIdentity,
            };
        }

        return JsonSerializer.Serialize(new
        {
            targets,
            libraries,
            project = new { restore = new { projectPath } },
        });
    }

    private static CoverageRunWatchdogSupervisor CreateSupervisor(FakeInMemoryConsole console)
        => new(
            CoverageRunWatchdogMode.Off,
            TimeSpan.Zero,
            TimeSpan.Zero,
            CoverageTextWriters.Create(console.Output, console.Error),
            TimeProvider.System,
            CancellationToken.None);

    private sealed class CapabilityProcessRunner(Func<CoverageRunProcessRequest, string> outputFactory) : ICoverageRunProcessRunner
    {
        public List<CoverageRunProcessRequest> Requests { get; } = [];

        public Task<CoverageRunProcessResult> RunAsync(CoverageRunProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            request.Lease.Complete(processExited: true);
            var output = outputFactory(request);
            return Task.FromResult(new CoverageRunProcessResult(0, output, StandardOutput: output));
        }
    }
}
