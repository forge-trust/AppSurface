using System.Text.Json;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;
using CoverageArtifactSettingsResult = ForgeTrust.AppSurface.Evidence.Coverage.CoverageArtifactSettingsResult;
using CoverageRunProject = ForgeTrust.AppSurface.Evidence.Coverage.CoverageRunProject;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Covers final path, continuation, settings, argument, and mount-parser inspection edges.</summary>
public sealed class CoverageArtifactInspectionFinalPathsTests
{
    private const string DisabledSettings = "<RunSettings />";
    private const string EnabledSettings = "<RunSettings><DataCollector friendlyName='XPlat Code Coverage' /></RunSettings>";

    [Fact]
    public void Inspect_RejectsASymbolicLinkAsTheScanRoot()
    {
        using var fixture = TestDirectory.Create();
        var target = fixture.CreateDirectory("target");
        var targetResults = TestPathUtils.PathUnder(target, "TestResults");
        Directory.CreateDirectory(targetResults);
        File.WriteAllText(TestPathUtils.PathUnder(targetResults, "coverage.xml"), "outside");
        var linkedRoot = TestPathUtils.PathUnder(fixture.Path, "linked-root");
        TestFileSystem.CreateDirectoryLinkOrSkip(linkedRoot, target);

        try
        {
            var result = Inspect(linkedRoot);

            Assert.False(result.Complete);
            Assert.Equal(0, result.Bytes);
            Assert.Equal("LinkSkipped", Assert.Single(result.Errors).Type);
        }
        finally
        {
            Directory.Delete(linkedRoot);
        }
    }

    [Fact]
    public void Inspect_SkipsEveryConfiguredSubtreeEvenInsideAnOutputRoot()
    {
        using var fixture = TestDirectory.Create();
        var results = TestPathUtils.PathUnder(fixture.Path, "TestResults");
        Directory.CreateDirectory(results);
        foreach (var skippedName in new[] { "bin", "obj", "node_modules", ".gstack", ".codex" })
        {
            var hiddenResults = TestPathUtils.PathUnder(results, skippedName, "TestResults");
            Directory.CreateDirectory(hiddenResults);
            File.WriteAllText(TestPathUtils.PathUnder(hiddenResults, "ignored.xml"), "ignored");
        }
        File.WriteAllText(TestPathUtils.PathUnder(results, "kept.xml"), "kept");

        var result = Inspect(fixture.Path);

        Assert.True(result.Complete);
        Assert.Equal(4, result.Bytes);
        Assert.Equal(1, result.FileCount);
        Assert.Equal("TestResults/kept.xml", Assert.Single(result.Largest).Path);
    }

    [Fact]
    public void Inspect_AcceptsA4096CharacterContinuationAndRejects4097()
    {
        using var fixture = TestDirectory.Create();
        var atLimit = ContinuationPathOfLength(4096);
        var overLimit = ContinuationPathOfLength(4097);

        var accepted = CoverageArtifactInspector.Inspect(
            fixture.Path, TimeProvider.System, TimeSpan.FromSeconds(1), 100, CancellationToken.None, [atLimit]);

        Assert.False(accepted.Complete);
        Assert.Contains(accepted.Errors, error => error.Type == "ContinuationUnavailable");
        Assert.Throws<InvalidDataException>(() => CoverageArtifactInspector.Inspect(
            fixture.Path, TimeProvider.System, TimeSpan.FromSeconds(1), 100, CancellationToken.None, [overLimit]));
    }

    [Fact]
    public void Inspect_RejectsAResumedOutputWhenAnAncestorHasAGitFile()
    {
        using var fixture = TestDirectory.Create();
        var output = TestPathUtils.PathUnder(fixture.Path, "workspace", "TestResults");
        Directory.CreateDirectory(TestPathUtils.PathUnder(output, "nested"));
        File.WriteAllText(TestPathUtils.PathUnder(fixture.Path, "workspace", ".git"), "gitdir: ../metadata");
        File.WriteAllText(TestPathUtils.PathUnder(output, "nested", "coverage.xml"), "must not count");

        var result = CoverageArtifactInspector.Inspect(
            fixture.Path, TimeProvider.System, TimeSpan.FromSeconds(1), 100, CancellationToken.None,
            ["workspace/TestResults"]);

        Assert.False(result.Complete);
        Assert.Equal(0, result.Bytes);
        Assert.Contains(result.Errors, error => error.Type == "ContinuationUnavailable");
    }

    [Fact]
    public void ReadCollectorRisk_RejectsSettingsFileLinks()
    {
        using var fixture = TestDirectory.Create();
        var target = fixture.WriteFile("target.runsettings", EnabledSettings);
        var link = TestPathUtils.PathUnder(fixture.Path, "selected.runsettings");
        TestFileSystem.CreateFileLinkOrSkip(link, target);

        try
        {
            var result = ObserveSettings(fixture.Path, ["--settings", "selected.runsettings"]);

            Assert.Equal("unknown", result.State);
            Assert.Equal("settings-file-link", result.Cause);
            Assert.Equal(Path.GetFullPath(link), result.EffectivePath);
        }
        finally
        {
            File.Delete(link);
        }
    }

    [Fact]
    public void ReadCollectorRisk_RejectsADeviceFileAsNonregular()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw Xunit.Sdk.SkipException.ForSkip("The nonregular device fixture is available on Linux and macOS.");

        var result = CoverageArtifactInspector.ReadCollectorRisk("/dev/null");

        Assert.Equal("unknown", result.State);
        Assert.Equal("settings-file-not-regular-or-unavailable", result.Cause);
    }

    [Fact]
    public void ReadCollectorRisk_ReportsASettingsOpenFailure()
    {
        if (OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("Unix file modes are required to create a deterministic access failure.");

        using var fixture = TestDirectory.Create();
        var settings = fixture.WriteFile("inaccessible.runsettings", DisabledSettings);
        var originalMode = File.GetUnixFileMode(settings);

        try
        {
            File.SetUnixFileMode(settings, UnixFileMode.None);
            var accessDenied = false;
            try
            {
                using var readable = File.OpenRead(settings);
            }
            catch (UnauthorizedAccessException)
            {
                accessDenied = true;
            }
            catch (IOException)
            {
                accessDenied = true;
            }

            if (!accessDenied)
                throw Xunit.Sdk.SkipException.ForSkip("The current test identity can read mode-000 files.");

            var result = CoverageArtifactInspector.ReadCollectorRisk(settings);

            Assert.Equal("unknown", result.State);
            Assert.Equal("settings-file-IOException", result.Cause);
        }
        finally
        {
            File.SetUnixFileMode(settings, originalMode);
        }
    }

    [Fact]
    public void Observe_AllowsEquivalentDuplicateSettingsAliasesAndIgnoresUnsupportedPropertyTokens()
    {
        using var fixture = TestDirectory.Create();
        var settings = fixture.WriteFile("selected.runsettings", DisabledSettings);
        var arguments = new[]
        {
            "--settings", "selected.runsettings",
            "-s=selected.runsettings",
            "--property:Configuration=Release",
            "--unrecognized-test-option", "value"
        };
        var originalArguments = arguments.ToArray();

        var result = ObserveSettings(fixture.Path, arguments);

        Assert.Equal("disabled", result.State);
        Assert.Equal("xplat-collector-not-configured-or-disabled", result.Cause);
        Assert.Equal(Path.GetFullPath(settings), result.EffectivePath);
        Assert.Equal(originalArguments, arguments);
    }

    [Theory]
    [InlineData("\\08x")]
    [InlineData("\\-10")]
    public void ReadLinuxMountType_SkipsShortRecordsAndPreservesAnInvalidOctalEscape(string escape)
    {
        string[] shortRecords =
        [
            "1 0 8:1 / - ext4 /dev/root rw",
            "2 0 8:2 / /mnt rw - ext4"
        ];

        Assert.Throws<IOException>(() => CoverageArtifactCapability.ReadLinuxMountType("/mnt/artifacts", shortRecords));

        var malformedEscape = "3 1 8:3 / /mnt/bad" + escape + " rw - xfs /dev/disk rw";
        var fileSystem = CoverageArtifactCapability.ReadLinuxMountType(
            "/mnt/bad" + escape + "/artifacts", [.. shortRecords, malformedEscape]);

        Assert.Equal("xfs", fileSystem);
    }

    private static CoverageArtifactObservation Inspect(string root) =>
        CoverageArtifactInspector.Inspect(root, TimeProvider.System, TimeSpan.FromSeconds(2), 1000, CancellationToken.None);

    private static CoverageArtifactSettingsResult ObserveSettings(string root, IReadOnlyList<string> arguments)
    {
        var project = new CoverageRunProject("Sample.csproj", TestPathUtils.PathUnder(root, "Sample.csproj"), "fixture", false);
        var properties = new Dictionary<string, string?>
        {
            ["Configuration"] = "Debug",
            ["TargetFramework"] = "net10.0"
        };
        var capability = JsonSerializer.Serialize(new { Properties = properties });
        return CoverageArtifactSettings.Observe(project, "net10.0", capability, "Debug", arguments);
    }

    private static string ContinuationPathOfLength(int length)
    {
        const int maximumComponentLength = 200;
        var components = new List<string>();
        while (length > 0)
        {
            var nextLength = Math.Min(maximumComponentLength, length - (components.Count > 0 ? 1 : 0));
            components.Add(new string('a', nextLength));
            length -= nextLength + (components.Count > 1 ? 1 : 0);
        }
        return string.Join('/', components);
    }
}
