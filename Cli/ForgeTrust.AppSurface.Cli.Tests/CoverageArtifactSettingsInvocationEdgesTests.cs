using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactSettingsInvocationEdgesTests
{
    [Theory]
    [InlineData(" ")]
    [InlineData("/")]
    [InlineData("../Sample.csproj")]
    [InlineData("Elsewhere.csproj")]
    [InlineData("a/b/c/Sample.csproj")]
    [InlineData("\0")]
    public void AmbiguousInvocationDirectoryDoesNotGuessRelativeSettings(string relative)
    {
        using var temp = TestDirectory.Create();
        var project = new CoverageRunProject(relative, Path.Join(temp.Path, "Sample.csproj"), "fixture", false);
        var result = CoverageArtifactSettings.Observe(project, "net10.0", Capability("enabled.runsettings"), "Debug", []);
        Assert.Equal("unknown", result.State);
        Assert.Equal("settings-path-unusable", result.Cause);
    }

    [Fact]
    public void NestedProjectResolvesSettingsAgainstInvocationRoot()
    {
        using var temp = TestDirectory.Create();
        var nested = temp.CreateDirectory("src/tests");
        var settings = Path.Join(temp.Path, "enabled.runsettings");
        File.WriteAllText(settings, "<RunSettings><DataCollector friendlyName='XPlat Code Coverage'/></RunSettings>");
        var project = new CoverageRunProject("src/tests/Sample.csproj", Path.Join(nested, "Sample.csproj"), "fixture", false);
        var result = CoverageArtifactSettings.Observe(project, "net10.0", Capability("enabled.runsettings"), "Debug", []);
        Assert.Equal("enabled", result.State);
        Assert.Equal(settings, result.EffectivePath);
    }

    [Theory]
    [InlineData("--settings", "selected.runsettings")]
    [InlineData("-s", "selected.runsettings")]
    [InlineData("--property", "RunSettingsFilePath=selected.runsettings")]
    [InlineData("-p", "RunSettingsFilePath=selected.runsettings")]
    [InlineData("/property", "RunSettingsFilePath=selected.runsettings")]
    public void SplitOverridesSelectActualSettings(string option, string value)
    {
        using var temp = TestDirectory.Create();
        File.WriteAllText(Path.Join(temp.Path, "selected.runsettings"), "<RunSettings/>");
        var project = new CoverageRunProject("Sample.csproj", Path.Join(temp.Path, "Sample.csproj"), "fixture", false);
        var result = CoverageArtifactSettings.Observe(project, "net10.0", Capability("missing.runsettings"), "Debug", [option, value]);
        Assert.Equal("disabled", result.State);
        Assert.Equal(Path.Join(temp.Path, "selected.runsettings"), result.EffectivePath);
    }

    [Theory]
    [InlineData("-p", "")]
    [InlineData("--property", " =bad")]
    [InlineData("--property", "RunSettingsFilePath=selected.runsettings;")]
    [InlineData("--settings", " ")]
    public void InvalidSplitPayloadIsUnknown(string option, string value)
    {
        using var temp = TestDirectory.Create();
        var project = new CoverageRunProject("Sample.csproj", Path.Join(temp.Path, "Sample.csproj"), "fixture", false);
        var result = CoverageArtifactSettings.Observe(project, "net10.0", Capability(null), "Debug", [option, value]);
        Assert.Equal("unknown", result.State);
        Assert.Equal("settings-override-unusable", result.Cause);
    }

    [Theory]
    [InlineData("--settings")]
    [InlineData("--property")]
    public void SplitPayloadCountsAgainstSingleArgumentAndTotalBounds(string option)
    {
        using var temp = TestDirectory.Create();
        var project = new CoverageRunProject("Sample.csproj", Path.Join(temp.Path, "Sample.csproj"), "fixture", false);
        var result = CoverageArtifactSettings.Observe(project, "net10.0", Capability(null), "Debug", [option, new string('x', 16385)]);
        Assert.Equal("settings-arguments-over-limit", result.Cause);
        var many = Enumerable.Repeat(new string('a', 16384), 4).Concat([option, "selected.runsettings"]).ToArray();
        var total = CoverageArtifactSettings.Observe(project, "net10.0", Capability(null), "Debug", many);
        Assert.Equal("settings-arguments-over-limit", total.Cause);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"Properties\":[]}")]
    public void NonObjectEvaluationNeverAssumesCoverageConfiguration(string json)
    {
        using var temp = TestDirectory.Create();
        var project = new CoverageRunProject("Sample.csproj", Path.Join(temp.Path, "Sample.csproj"), "fixture", false);
        Assert.Equal("unknown", CoverageArtifactSettings.Observe(project, null, json, "Debug", []).State);
    }

    [Fact]
    public void BlankEvaluatedTargetFrameworkAndOversizedSettingsRemainUnknown()
    {
        using var temp = TestDirectory.Create();
        var project = new CoverageRunProject("Sample.csproj", Path.Join(temp.Path, "Sample.csproj"), "fixture", false);
        var blank = "{\"Properties\":{\"Configuration\":\"Debug\",\"TargetFramework\":\" \"}}";
        Assert.Equal("framework-evaluation-unavailable", CoverageArtifactSettings.Observe(project, null, blank, "Debug", []).Cause);
        var oversized = CoverageArtifactSettings.Observe(project, "net10.0", Capability(new string('x', 4097)), "Debug", []);
        Assert.Equal("settings-path-unusable", oversized.Cause);
    }

    private static string Capability(string? settings)
    {
        var properties = new Dictionary<string, string?>
        {
            ["Configuration"] = "Debug",
            ["TargetFramework"] = "net10.0"
        };
        if (settings is not null) properties["RunSettingsFilePath"] = settings;
        return JsonSerializer.Serialize(new { Properties = properties });
    }
}
