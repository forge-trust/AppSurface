using System.Text.Json;
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
        Assert.Equal(3, runner.Requests.Count);
        Assert.All(runner.Requests, request => Assert.Contains("-getItem:PackageReference,PackageVersion", request.Arguments));
        Assert.Contains(runner.Requests[1].Arguments, argument => argument == "-property:TargetFramework=net8.0");
        Assert.Contains(runner.Requests[2].Arguments, argument => argument == "-property:TargetFramework=net10.0");
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

    private static string CapabilityJson(
        string targetFramework,
        string? version = null,
        string? versionOverride = null,
        string? centralVersion = null,
        IReadOnlyList<string>? centralVersions = null,
        IReadOnlyList<string>? targetFrameworks = null)
    {
        var properties = new Dictionary<string, object?>
        {
            ["TargetFramework"] = targetFramework,
            ["TargetFrameworks"] = targetFrameworks is { Count: > 0 } ? string.Join(";", targetFrameworks) : string.Empty,
        };
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
