using System.Text.Json;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;
using CoverageArtifactSettingsResult = ForgeTrust.AppSurface.Evidence.Coverage.CoverageArtifactSettingsResult;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Covers remaining reachable framework, override, and project-path settings edges.</summary>
public sealed class CoverageArtifactSettingsRemainingTests
{
    [Theory]
    [InlineData("{\"Properties\":{\"Configuration\":\"Debug\"}}")]
    [InlineData("{\"Properties\":{\"Configuration\":\"Debug\",\"TargetFramework\":null}}")]
    [InlineData("{\"Properties\":{\"Configuration\":\"Debug\",\"TargetFramework\":\"net10.0\",\"TargetFramework\":\"net10.0\"}}")]
    public void OuterEvaluationRequiresOneUsableTargetFramework(string capability)
    {
        using var temp = TestDirectory.Create();

        var result = Observe(Project(temp.Path), null, capability, []);

        Assert.Equal("unknown", result.State);
        Assert.Equal("framework-evaluation-unavailable", result.Cause);
        Assert.Null(result.EffectivePath);
    }

    [Fact]
    public void Observe_RejectsNullFrameworkListsAndDuplicateSettingsProperties()
    {
        using var temp = TestDirectory.Create();
        var project = Project(temp.Path);
        const string nullFrameworkList = "{\"Properties\":{\"Configuration\":\"Debug\",\"TargetFramework\":\"net10.0\",\"TargetFrameworks\":null}}";
        const string duplicateSettings = "{\"Properties\":{\"Configuration\":\"Debug\",\"TargetFramework\":\"net10.0\",\"RunSettingsFilePath\":\"one.runsettings\",\"RunSettingsFilePath\":\"two.runsettings\"}}";

        var frameworkResult = Observe(project, "net10.0", nullFrameworkList, []);
        var settingsResult = Observe(project, "net10.0", duplicateSettings, []);

        Assert.Equal("framework-evaluation-unavailable", frameworkResult.Cause);
        Assert.Equal("settings-property-unusable", settingsResult.Cause);
        Assert.Equal("unknown", frameworkResult.State);
        Assert.Equal("unknown", settingsResult.State);
    }

    [Fact]
    public void JoinedSettingsOverrideResolvesTheSelectedFile()
    {
        using var temp = TestDirectory.Create();
        var settingsPath = TestPathUtils.PathUnder(temp.Path, "selected.runsettings");
        File.WriteAllText(settingsPath, "<RunSettings />");
        var arguments = new[] { "--settings=" + Path.GetFileName(settingsPath) };
        var originalArguments = arguments.ToArray();

        var result = Observe(Project(temp.Path), "net10.0", Capability(), arguments);

        Assert.Equal("disabled", result.State);
        Assert.Equal(Path.GetFullPath(settingsPath), result.EffectivePath);
        Assert.Equal(originalArguments, arguments);
    }

    [Fact]
    public void SettingsOverridesRejectEmptyAndOverlongPaths()
    {
        using var temp = TestDirectory.Create();
        var project = Project(temp.Path);

        var empty = Observe(project, "net10.0", Capability(), ["--settings="]);
        var overlong = Observe(project, "net10.0", Capability(), ["--settings", new string('s', 4097)]);

        Assert.Equal("settings-override-unusable", empty.Cause);
        Assert.Equal("settings-override-unusable", overlong.Cause);
        Assert.Null(empty.EffectivePath);
        Assert.Null(overlong.EffectivePath);
    }

    [Fact]
    public void SplitOptionValuesRespectTheSingleArgumentLimit()
    {
        using var temp = TestDirectory.Create();
        var project = Project(temp.Path);
        var oversized = new string('x', 16 * 1024 + 1);

        var settings = Observe(project, "net10.0", Capability(), ["--settings", oversized]);
        var property = Observe(project, "net10.0", Capability(), ["--property", oversized]);

        Assert.Equal("settings-arguments-over-limit", settings.Cause);
        Assert.Equal("settings-arguments-over-limit", property.Cause);
        Assert.Null(settings.EffectivePath);
        Assert.Null(property.EffectivePath);
    }

    [Fact]
    public void PropertyPayloadSkipsUnrelatedAssignmentsAndAcceptsRootedSettingsPath()
    {
        using var temp = TestDirectory.Create();
        var settingsPath = TestPathUtils.PathUnder(temp.Path, "selected.runsettings");
        File.WriteAllText(settingsPath, "<RunSettings />");

        var result = Observe(
            Project(temp.Path),
            "net10.0",
            Capability(),
            ["--property:Configuration=Release;RunSettingsFilePath=" + settingsPath]);

        Assert.Equal("disabled", result.State);
        Assert.Equal(Path.GetFullPath(settingsPath), result.EffectivePath);
    }

    [Fact]
    public void PropertyPayloadRejectsEmptyAndMalformedAssignments()
    {
        using var temp = TestDirectory.Create();
        var project = Project(temp.Path);

        var empty = Observe(project, "net10.0", Capability(), ["--property="]);
        var trailingEmptyAssignment = Observe(project, "net10.0", Capability(), ["/p:Other=Value;"]);

        Assert.Equal("settings-override-unusable", empty.Cause);
        Assert.Equal("settings-override-unusable", trailingEmptyAssignment.Cause);
        Assert.Null(empty.EffectivePath);
        Assert.Null(trailingEmptyAssignment.EffectivePath);
    }

    [Fact]
    public void EmptyAndNonemptyOverridesConflict()
    {
        using var temp = TestDirectory.Create();
        File.WriteAllText(TestPathUtils.PathUnder(temp.Path, "selected.runsettings"), "<RunSettings />");

        var result = Observe(
            Project(temp.Path),
            "net10.0",
            Capability("inherited.runsettings"),
            ["-p", "RunSettingsFilePath=", "--settings=selected.runsettings"]);

        Assert.Equal("unknown", result.State);
        Assert.Equal("settings-overrides-conflict", result.Cause);
        Assert.Null(result.EffectivePath);
    }

    [Fact]
    public void RelativeSettingsCannotResolveWhenProjectDirectoryCannotBeDerived()
    {
        using var temp = TestDirectory.Create();
        var fileAtRoot = new CoverageRunProject("Sample.csproj", Path.GetPathRoot(temp.Path)!, "fixture", false);
        var parentWouldEscapeRoot = new CoverageRunProject(
            "nested/Sample.csproj",
            TestPathUtils.PathUnder(Path.GetPathRoot(temp.Path)!, "Sample.csproj"),
            "fixture",
            false);

        var missingParent = Observe(fileAtRoot, "net10.0", Capability("selected.runsettings"), []);
        var exhaustedParents = Observe(parentWouldEscapeRoot, "net10.0", Capability("selected.runsettings"), []);

        Assert.Equal("unknown", missingParent.State);
        Assert.Equal("settings-path-unusable", missingParent.Cause);
        Assert.Equal("unknown", exhaustedParents.State);
        Assert.Equal("settings-path-unusable", exhaustedParents.Cause);
    }

    [Fact]
    public void UnsafeProjectNamesAreReplacedWithTheStablePlaceholder()
    {
        using var temp = TestDirectory.Create();
        var fullPath = TestPathUtils.PathUnder(temp.Path, "Sample.csproj");
        var longName = new CoverageRunProject(new string('a', 129) + ".csproj", fullPath, "fixture", false);
        var controlName = new CoverageRunProject("bad\u0001.csproj", fullPath, "fixture", false);

        var longNameResult = Observe(longName, "net10.0", Capability(), []);
        var controlNameResult = Observe(controlName, "net10.0", Capability(), []);

        Assert.Equal("<project>", longNameResult.Project);
        Assert.Equal("<project>", controlNameResult.Project);
        Assert.Equal("absent", longNameResult.State);
        Assert.Equal("absent", controlNameResult.State);
    }

    private static string Capability(string? settingsPath = null)
    {
        var properties = new Dictionary<string, string?>
        {
            ["Configuration"] = "Debug",
            ["TargetFramework"] = "net10.0"
        };
        if (settingsPath is not null) properties["RunSettingsFilePath"] = settingsPath;
        return JsonSerializer.Serialize(new { Properties = properties });
    }

    private static CoverageArtifactSettingsResult Observe(
        CoverageRunProject project,
        string? framework,
        string capability,
        IReadOnlyList<string> arguments) =>
        CoverageArtifactSettings.Observe(project, framework, capability, "Debug", arguments);

    private static CoverageRunProject Project(string root) =>
        new("Sample.csproj", TestPathUtils.PathUnder(root, "Sample.csproj"), "fixture", false);
}
