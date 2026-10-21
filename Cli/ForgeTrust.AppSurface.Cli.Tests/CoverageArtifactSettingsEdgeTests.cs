using System.Text.Json;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;
using CoverageArtifactSettingsResult = ForgeTrust.AppSurface.Evidence.Coverage.CoverageArtifactSettingsResult;
using CoverageRunProject = ForgeTrust.AppSurface.Evidence.Coverage.CoverageRunProject;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Checks settings-parser edges that affect the selected test invocation.</summary>
public sealed class CoverageArtifactSettingsEdgeTests
{
    private const string SettingsWithSpaces = "settings with spaces/selected.runsettings";
    private const string EnabledXml = "<RunSettings><DataCollectionRunSettings><DataCollectors><DataCollector friendlyName=\"XPlat Code Coverage\" /></DataCollectors></DataCollectionRunSettings></RunSettings>";

    [Theory]
    [InlineData("{}", "capability-properties-unavailable")]
    [InlineData("{\"Properties\":{\"Configuration\":\"Debug\",\"TargetFramework\":\"net10.0\",\"TargetFrameworks\":false}}", "framework-evaluation-unavailable")]
    [InlineData("{\"Properties\":{\"Configuration\":\"Debug\",\"TargetFramework\":\"net10.0\",\"RunSettingsFilePath\":false}}", "settings-property-unusable")]
    [InlineData("{\"Properties\":{\"Configuration\":\"Debug\",\"TargetFramework\":\"net10.0\",\"RunSettingsFilePath\":\"  \"}}", "settings-property-unusable")]
    public void Observe_RejectsUnusableEvaluatedProperties(string json, string cause)
    {
        using var temp = TestDirectory.Create();

        var result = Observe(Project(temp.Path), "net10.0", json, "Debug", []);

        Assert.Equal("unknown", result.State);
        Assert.Equal(cause, result.Cause);
        Assert.Null(result.EffectivePath);
    }

    [Fact]
    public void Observe_UsesTargetFrameworkListAndAllowsSingleFrameworkOuterEvaluation()
    {
        using var temp = TestDirectory.Create();
        File.WriteAllText(Path.Join(temp.Path, "selected.runsettings"), EnabledXml);
        var project = Project(temp.Path);

        var notSelected = Observe(project, "net10.0", Capability("net10.0", targetFrameworks: "net8.0;net9.0"), "Debug", []);
        var singleFrameworkOuter = Observe(project, null, Capability("net10.0", targetFrameworks: ""), "Debug", ["--settings", "selected.runsettings"]);

        Assert.Equal("unknown", notSelected.State);
        Assert.Equal("framework-evaluation-mismatch", notSelected.Cause);
        Assert.Null(notSelected.EffectivePath);
        Assert.Equal("enabled", singleFrameworkOuter.State);
        Assert.Equal(Path.GetFullPath(Path.Join(temp.Path, "selected.runsettings")), singleFrameworkOuter.EffectivePath);
        Assert.Null(singleFrameworkOuter.Framework);
    }

    [Theory]
    [InlineData("--settings:{settings}")]
    [InlineData("-s={settings}")]
    [InlineData("-s:{settings}")]
    [InlineData("--property=RunSettingsFilePath={settings}")]
    [InlineData("/property:RunSettingsFilePath={settings}")]
    [InlineData("-property=RunSettingsFilePath={settings}")]
    [InlineData("/p=RunSettingsFilePath={settings}")]
    [InlineData("-p:RunSettingsFilePath={settings}")]
    [InlineData("--property|RunSettingsFilePath={settings}")]
    [InlineData("-p|RunSettingsFilePath={settings}")]
    public void Observe_ResolvesSwitchAliasesAndSpaceContainingValuesWithoutChangingArguments(string encodedArguments)
    {
        using var temp = TestDirectory.Create();
        Directory.CreateDirectory(Path.Join(temp.Path, "settings with spaces"));
        File.WriteAllText(Path.Join(temp.Path, SettingsWithSpaces), "<RunSettings />");
        var arguments = encodedArguments.Split('|')
            .Select(argument => argument.Replace("{settings}", SettingsWithSpaces, StringComparison.Ordinal))
            .ToArray();
        var originalArguments = arguments.ToArray();

        var result = Observe(Project(temp.Path), "net10.0", Capability("net10.0"), "Debug", arguments);

        Assert.Equal("disabled", result.State);
        Assert.Equal("xplat-collector-not-configured-or-disabled", result.Cause);
        Assert.Equal(Path.GetFullPath(Path.Join(temp.Path, SettingsWithSpaces)), result.EffectivePath);
        Assert.Equal(originalArguments, arguments);
    }

    [Fact]
    public void Observe_RejectsConflictingOverridesAndHonorsAnEmptyPropertyOverride()
    {
        using var temp = TestDirectory.Create();
        File.WriteAllText(Path.Join(temp.Path, "enabled.runsettings"), EnabledXml);
        File.WriteAllText(Path.Join(temp.Path, "disabled.runsettings"), "<RunSettings />");
        var project = Project(temp.Path);
        var conflictingArguments = new[] { "--settings", "enabled.runsettings", "/p:RunSettingsFilePath=disabled.runsettings" };
        var originalArguments = conflictingArguments.ToArray();

        var conflict = Observe(project, "net10.0", Capability("net10.0", settingsPath: "enabled.runsettings"), "Debug", conflictingArguments);
        var cleared = Observe(project, "net10.0", Capability("net10.0", settingsPath: "enabled.runsettings"), "Debug", ["-p", "RunSettingsFilePath="]);

        Assert.Equal("unknown", conflict.State);
        Assert.Equal("settings-overrides-conflict", conflict.Cause);
        Assert.Null(conflict.EffectivePath);
        Assert.Equal(originalArguments, conflictingArguments);
        Assert.Equal("absent", cleared.State);
        Assert.Equal("settings-not-configured", cleared.Cause);
        Assert.Null(cleared.EffectivePath);
    }

    [Fact]
    public void Observe_EnforcesArgumentAndSettingsPathCharacterBounds()
    {
        using var temp = TestDirectory.Create();
        var project = Project(temp.Path);
        var capability = Capability("net10.0");

        var oversizedPath = Observe(project, "net10.0", capability, "Debug", ["--settings=" + new string('p', 4097)]);
        var oversizedTotal = Observe(project, "net10.0", capability, "Debug", Enumerable.Repeat(new string('x', 11_000), 6).ToArray());
        var maximumSingleArgument = Observe(project, "net10.0", capability, "Debug", [new string('x', 16 * 1024)]);

        Assert.Equal("settings-override-unusable", oversizedPath.Cause);
        Assert.Null(oversizedPath.EffectivePath);
        Assert.Equal("settings-arguments-over-limit", oversizedTotal.Cause);
        Assert.Null(oversizedTotal.EffectivePath);
        Assert.Equal("absent", maximumSingleArgument.State);
        Assert.Equal("settings-not-configured", maximumSingleArgument.Cause);
    }

    [Fact]
    public void Observe_RejectsInvalidSettingsAndProjectPaths()
    {
        using var temp = TestDirectory.Create();
        var project = Project(temp.Path);
        var invalidSettingsPath = Observe(project, "net10.0", Capability("net10.0"), "Debug", ["--settings", "bad\0.runsettings"]);
        var invalidProjectPath = Observe(
            Project(temp.Path, "../Test.csproj"),
            "net10.0",
            Capability("net10.0"),
            "Debug",
            ["--settings", "settings.runsettings"]);

        Assert.Equal("unknown", invalidSettingsPath.State);
        Assert.Equal("settings-path-unusable", invalidSettingsPath.Cause);
        Assert.Null(invalidSettingsPath.EffectivePath);
        Assert.Equal("unknown", invalidProjectPath.State);
        Assert.Equal("settings-path-unusable", invalidProjectPath.Cause);
        Assert.Null(invalidProjectPath.EffectivePath);
    }

    [Theory]
    [InlineData("<RunSettings><DataCollector friendlyName=\"XPlat Code Coverage\" enabled=\"false\" /></RunSettings>", "disabled", "xplat-collector-not-configured-or-disabled")]
    [InlineData("<RunSettings><DataCollector friendlyName=\"XPlat Code Coverage\" isEnabled=\"false\" /></RunSettings>", "unknown", "settings-collector-enablement-invalid")]
    [InlineData("<RunSettings><DataCollector friendlyName=\"XPlat Code Coverage\" enabled=\"perhaps\" /></RunSettings>", "unknown", "settings-collector-enablement-invalid")]
    [InlineData("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><RunSettings>&e;</RunSettings>", "unknown", "settings-xml-invalid-or-dtd-prohibited")]
    public void Observe_InspectsCollectorEnablementWithSafeXmlSettings(string xml, string state, string cause)
    {
        using var temp = TestDirectory.Create();
        var path = Path.Join(temp.Path, "settings.runsettings");
        File.WriteAllText(path, xml);

        var result = Observe(Project(temp.Path), "net10.0", Capability("net10.0"), "Debug", ["--settings", "settings.runsettings"]);

        Assert.Equal(state, result.State);
        Assert.Equal(cause, result.Cause);
        Assert.Equal(Path.GetFullPath(path), result.EffectivePath);
    }

    [Fact]
    public void Observe_RejectsSettingsXmlOverItsReadLimitAndRetainsTheResolvedPath()
    {
        using var temp = TestDirectory.Create();
        var path = Path.Join(temp.Path, "large.runsettings");
        File.WriteAllText(path, new string(' ', 64 * 1024 + 1));

        var result = Observe(Project(temp.Path), "net10.0", Capability("net10.0"), "Debug", ["-s", "large.runsettings"]);

        Assert.Equal("unknown", result.State);
        Assert.Equal("settings-file-over-64KiB", result.Cause);
        Assert.Equal(Path.GetFullPath(path), result.EffectivePath);
    }

    private static string Capability(string? targetFramework, string? targetFrameworks = null, string? settingsPath = null)
    {
        var properties = new Dictionary<string, string?> { ["Configuration"] = "Debug" };
        if (targetFramework is not null) properties["TargetFramework"] = targetFramework;
        if (targetFrameworks is not null) properties["TargetFrameworks"] = targetFrameworks;
        if (settingsPath is not null) properties["RunSettingsFilePath"] = settingsPath;
        return JsonSerializer.Serialize(new { Properties = properties });
    }

    private static CoverageArtifactSettingsResult Observe(CoverageRunProject project, string? framework, string json, string configuration, IReadOnlyList<string> arguments) =>
        CoverageArtifactSettings.Observe(project, framework, json, configuration, arguments);

    private static CoverageRunProject Project(string root, string relativePath = "Test.csproj") =>
        new(relativePath, Path.Join(root, "Test.csproj"), "test", false);
}
