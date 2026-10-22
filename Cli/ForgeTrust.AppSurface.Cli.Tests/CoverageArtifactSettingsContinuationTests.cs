using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactSettingsContinuationTests
{
    [Theory]
    [InlineData("--settings|selected.runsettings|--filter|Name~Case")]
    [InlineData("--settings=selected.runsettings|--filter|Name~Case")]
    [InlineData("/p:RunSettingsFilePath=selected.runsettings|--filter|Name~Case")]
    [InlineData("--property|RunSettingsFilePath=selected.runsettings|--filter|Name~Case")]
    public void RecognizedOverrideContinuesParsingAndPreservesAllTestArguments(string encodedArguments)
    {
        using var temp = TestDirectory.Create();
        File.WriteAllText(Path.Join(temp.Path, "selected.runsettings"),
            "<RunSettings><DataCollector friendlyName='XPlat Code Coverage' enabled='false'/></RunSettings>");
        var project = new CoverageRunProject("Test.csproj", Path.Join(temp.Path, "Test.csproj"), "test", false);
        var arguments = encodedArguments.Split('|');
        var unchanged = arguments.ToArray();
        var capabilities = JsonSerializer.Serialize(new { Properties = new { Configuration = "Debug", TargetFramework = "net10.0" } });
        var result = CoverageArtifactSettings.Observe(project, "net10.0", capabilities, "Debug", arguments);
        Assert.Equal("disabled", result.State);
        Assert.Equal(Path.Join(temp.Path, "selected.runsettings"), result.EffectivePath);
        Assert.Equal(unchanged, arguments);
    }

    [Theory]
    [InlineData("--settings|selected.runsettings|--settings|conflicting.runsettings")]
    [InlineData("--settings=selected.runsettings|--settings=conflicting.runsettings")]
    [InlineData("/p:RunSettingsFilePath=selected.runsettings|/p:RunSettingsFilePath=conflicting.runsettings")]
    public void AConflictingLaterOverrideRemainsUnknownInsteadOfReturningFromTheFirstMatch(string encodedArguments)
    {
        using var temp = TestDirectory.Create();
        var project = new CoverageRunProject("Test.csproj", Path.Join(temp.Path, "Test.csproj"), "test", false);
        var arguments = encodedArguments.Split('|');
        var capabilities = JsonSerializer.Serialize(new { Properties = new { Configuration = "Debug", TargetFramework = "net10.0" } });
        var result = CoverageArtifactSettings.Observe(project, "net10.0", capabilities, "Debug", arguments);
        Assert.Equal("unknown", result.State);
        Assert.Equal("settings-overrides-conflict", result.Cause);
        Assert.Null(result.EffectivePath);
    }
}
