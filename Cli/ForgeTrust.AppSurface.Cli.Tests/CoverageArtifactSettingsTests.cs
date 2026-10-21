using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;
using CoverageArtifactSettingsResult = ForgeTrust.AppSurface.Evidence.Coverage.CoverageArtifactSettingsResult;
using CoverageRunProject = ForgeTrust.AppSurface.Evidence.Coverage.CoverageRunProject;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Verifies effective runsettings resolution and argument preservation.</summary>
public sealed class CoverageArtifactSettingsTests
{
    [Theory]
    [InlineData("<RunSettings><DataCollector friendlyName='XPlat Code Coverage'/></RunSettings>", "enabled")]
    [InlineData("<RunSettings><DataCollector friendlyName='XPlat Code Coverage' enabled='false'/></RunSettings>", "disabled")]
    public void Observe_InspectsInheritedSettings(string xml, string state)
    {
        using var temp = TestDirectory.Create();
        var project = Project(temp.Path);
        File.WriteAllText(Path.Join(temp.Path, "inherited.runsettings"), xml);
        var result = Observe(project, "{\"Properties\":{\"Configuration\":\"Debug\",\"TargetFramework\":\"net10.0\",\"RunSettingsFilePath\":\"inherited.runsettings\"}}", []);
        Assert.Equal(state, result.State);
        Assert.Equal("inherited.runsettings", Path.GetFileName(result.EffectivePath));
    }

    [Fact]
    public void Observe_ExplicitSettingsOverrideInheritedAndDoesNotMutateArguments()
    {
        using var temp = TestDirectory.Create();
        var project = Project(temp.Path);
        File.WriteAllText(Path.Join(temp.Path, "enabled.runsettings"), "<RunSettings><DataCollector friendlyName='XPlat Code Coverage'/></RunSettings>");
        File.WriteAllText(Path.Join(temp.Path, "disabled.runsettings"), "<RunSettings />");
        var args = new[] { "--settings", "disabled.runsettings", "--filter", "Name~Case" };
        var before = args.ToArray();
        var result = Observe(project, "{\"Properties\":{\"Configuration\":\"Debug\",\"TargetFramework\":\"net10.0\",\"RunSettingsFilePath\":\"enabled.runsettings\"}}", args);
        Assert.Equal("disabled", result.State);
        Assert.Equal(before, args);
    }

    [Theory]
    [InlineData("--settings=a.runsettings|-s|b.runsettings")]
    [InlineData("/p:RunSettingsFilePath=a|-property:RunSettingsFilePath=b")]
    [InlineData("/p:RunSettingsFilePath")]
    public void Observe_ReportsConflictingOrMalformedOverridesUnknown(string encodedArguments)
    {
        using var temp = TestDirectory.Create();
        var args = encodedArguments.Split('|');
        var result = Observe(Project(temp.Path), "{\"Properties\":{\"Configuration\":\"Debug\",\"TargetFramework\":\"net10.0\"}}", args);
        Assert.Equal("unknown", result.State);
        Assert.Null(result.EffectivePath);
    }

    [Theory]
    [InlineData("<broken", "unknown")]
    [InlineData("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><x>&e;</x>", "unknown")]
    public void Observe_UsesSafeBoundedCollectorReader(string xml, string state)
    {
        using var temp = TestDirectory.Create();
        var project = Project(temp.Path);
        File.WriteAllText(Path.Join(temp.Path, "bad.runsettings"), xml);
        Assert.Equal(state, Observe(project, "{\"Properties\":{\"Configuration\":\"Debug\",\"TargetFramework\":\"net10.0\"}}", ["--settings", "bad.runsettings"]).State);
    }

    [Fact]
    public void Observe_MissingExplicitFileAndMismatchedConfigurationAreUnknown()
    {
        using var temp = TestDirectory.Create();
        var project = Project(temp.Path);
        var missing = Observe(project, "{\"Properties\":{\"Configuration\":\"Debug\",\"TargetFramework\":\"net10.0\"}}", ["-s", "missing.runsettings"]);
        Assert.Equal("unknown", missing.State);
        Assert.Equal("settings-file-missing", missing.Cause);
        Assert.Equal("unknown", Observe(project, "{\"Properties\":{\"Configuration\":\"Release\"}}", []).State);
    }

    private static CoverageArtifactSettingsResult Observe(CoverageRunProject project, string json, IReadOnlyList<string> args) =>
        CoverageArtifactSettings.Observe(project, "net10.0", json, "Debug", args);

    private static CoverageRunProject Project(string root) =>
        new("Test.csproj", Path.Join(root, "Test.csproj"), "test", false);
}
