using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;
using CoverageArtifactSettingsResult = ForgeTrust.AppSurface.Evidence.Coverage.CoverageArtifactSettingsResult;
using CoverageRunProject = ForgeTrust.AppSurface.Evidence.Coverage.CoverageRunProject;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Exercises collector-settings resolution at invocation and input boundaries.</summary>
public sealed class CoverageArtifactSettingsBoundaryTests
{
    private const string Enabled = "<RunSettings><DataCollectionRunSettings><DataCollectors><DataCollector friendlyName='XPlat Code Coverage'/></DataCollectors></DataCollectionRunSettings></RunSettings>";
    private const string Disabled = "<RunSettings />";

    [Fact]
    public void ExplicitSettingsOptionWinsOverEvaluatedPropertyAndConfigurationFrameworkMustMatch()
    {
        using var temp = TestDirectory.Create();
        File.WriteAllText(Path.Join(temp.Path, "evaluated.runsettings"), Enabled);
        File.WriteAllText(Path.Join(temp.Path, "explicit.runsettings"), Disabled);

        var result = Observe(Project(temp.Path), "net10.0", Capability("Debug", "net10.0", "evaluated.runsettings"), "Debug", ["--settings", "explicit.runsettings"]);

        Assert.Equal("disabled", result.State);
        Assert.Equal(Path.GetFullPath(Path.Join(temp.Path, "explicit.runsettings")), result.EffectivePath);
        Assert.Equal("unknown", Observe(Project(temp.Path), "net10.0", Capability("Release", "net10.0"), "Debug", []).State);
        Assert.Equal("framework-evaluation-mismatch", Observe(Project(temp.Path), "net10.0", Capability("Debug", "net8.0"), "Debug", []).Cause);
    }

    [Fact]
    public void OuterMultiFrameworkEvaluationIsNotApplicableButMatchingInnerEvaluationIsInspected()
    {
        using var temp = TestDirectory.Create();
        File.WriteAllText(Path.Join(temp.Path, "inner.runsettings"), Enabled);
        var outer = Observe(Project(temp.Path), null, Capability("Debug", "", targetFrameworks: "net8.0;net10.0", path: "missing.runsettings"), "Debug", []);
        var inner = Observe(Project(temp.Path), "net10.0", Capability("Debug", "net10.0", "inner.runsettings", "net8.0;net10.0"), "Debug", []);

        Assert.Equal("not-applicable", outer.State);
        Assert.Equal("enabled", inner.State);
    }

    [Theory]
    [InlineData("--settings|one.runsettings|-s|two.runsettings", "settings-overrides-conflict")]
    [InlineData("--settings", "settings-override-unusable")]
    [InlineData("--settings|one.runsettings|--|--settings|two.runsettings", "")]
    [InlineData("--unknown-option|value", "")]
    public void ArgumentBoundariesAreConservative(string encodedArguments, string expectedCause)
    {
        var args = encodedArguments.Split('|');
        using var temp = TestDirectory.Create();
        File.WriteAllText(Path.Join(temp.Path, "one.runsettings"), Enabled);
        var result = Observe(Project(temp.Path), "net10.0", Capability("Debug", "net10.0"), "Debug", args);
        if (expectedCause.Length == 0) Assert.NotEqual("unknown", result.State);
        else Assert.Equal(expectedCause, result.Cause);
    }

    [Fact]
    public void RepeatedSettingsAliasesMayResolveToTheSameInputWithoutBecomingAConflict()
    {
        using var temp = TestDirectory.Create();
        Directory.CreateDirectory(Path.Join(temp.Path, "settings"));
        var settings = Path.Join(temp.Path, "settings", "same.runsettings");
        File.WriteAllText(settings, Enabled);

        var result = Observe(Project(temp.Path), "net10.0", Capability("Debug", "net10.0"), "Debug",
            ["--settings", "settings/same.runsettings", "-s", "settings/../settings/same.runsettings"]);

        Assert.Equal("enabled", result.State);
        Assert.Equal(Path.GetFullPath(settings), result.EffectivePath);
        Assert.Equal(Enabled, File.ReadAllText(settings));
    }

    [Theory]
    [InlineData("{\"Properties\":{\"Configuration\":\"Debug\",\"Configuration\":\"Release\",\"TargetFramework\":\"net10.0\"}}", "configuration-evaluation-unavailable")]
    [InlineData("{\"Properties\":{\"Configuration\":null,\"TargetFramework\":\"net10.0\"}}", "configuration-evaluation-unavailable")]
    [InlineData("{broken", "capability-json-invalid")]
    public void JsonAmbiguityNullAndMalformedPayloadAreUnknown(string json, string cause)
    {
        using var temp = TestDirectory.Create();
        var result = Observe(Project(temp.Path), "net10.0", json, "Debug", []);
        Assert.Equal("unknown", result.State);
        Assert.Equal(cause, result.Cause);
    }

    [Fact]
    public void OversizedJsonAndArgumentsAreRejectedAtTheirBounds()
    {
        using var temp = TestDirectory.Create();
        var project = Project(temp.Path);
        Assert.Equal("capability-json-over-limit", Observe(project, "net10.0", new string(' ', 1024 * 1024 + 1), "Debug", []).Cause);
        Assert.Equal("settings-arguments-over-limit", Observe(project, "net10.0", Capability("Debug", "net10.0"), "Debug", [new string('x', 16 * 1024 + 1)]).Cause);
        Assert.Equal("settings-arguments-over-limit", Observe(project, "net10.0", Capability("Debug", "net10.0"), "Debug", Enumerable.Repeat("x", 1025).ToArray()).Cause);
    }

    [Fact]
    public void SafeXmlReaderRejectsMissingMalformedAndDtdFilesAndResolvesRelativeAliases()
    {
        using var temp = TestDirectory.Create();
        Directory.CreateDirectory(Path.Join(temp.Path, "settings"));
        File.WriteAllText(Path.Join(temp.Path, "settings", "safe.runsettings"), Enabled);
        File.WriteAllText(Path.Join(temp.Path, "malformed.runsettings"), "<RunSettings");
        File.WriteAllText(Path.Join(temp.Path, "dtd.runsettings"), "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><RunSettings>&e;</RunSettings>");
        var project = Project(temp.Path);

        var alias = Observe(project, "net10.0", Capability("Debug", "net10.0"), "Debug", ["--settings", "settings/../settings/safe.runsettings"]);
        var missing = Observe(project, "net10.0", Capability("Debug", "net10.0"), "Debug", ["-s", "missing.runsettings"]);
        var malformed = Observe(project, "net10.0", Capability("Debug", "net10.0"), "Debug", ["-s", "malformed.runsettings"]);
        var dtd = Observe(project, "net10.0", Capability("Debug", "net10.0"), "Debug", ["-s", "dtd.runsettings"]);

        Assert.Equal("enabled", alias.State);
        Assert.Equal(Path.GetFullPath(Path.Join(temp.Path, "settings", "safe.runsettings")), alias.EffectivePath);
        Assert.Equal("settings-file-missing", missing.Cause);
        Assert.Equal("unknown", malformed.State);
        Assert.Equal("unknown", dtd.State);
    }

    private static string Capability(string configuration, string framework, string? path = null, string? targetFrameworks = null)
    {
        var properties = new Dictionary<string, string?> { ["Configuration"] = configuration, ["TargetFramework"] = framework };
        if (path is not null) properties["RunSettingsFilePath"] = path;
        if (targetFrameworks is not null) properties["TargetFrameworks"] = targetFrameworks;
        return System.Text.Json.JsonSerializer.Serialize(new { Properties = properties });
    }

    private static CoverageArtifactSettingsResult Observe(CoverageRunProject project, string? framework, string json, string configuration, IReadOnlyList<string> args) =>
        CoverageArtifactSettings.Observe(project, framework, json, configuration, args);

    private static CoverageRunProject Project(string root) => new("Test.csproj", Path.Join(root, "Test.csproj"), "test", false);
}
