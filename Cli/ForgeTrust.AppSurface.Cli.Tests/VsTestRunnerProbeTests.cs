using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class VsTestRunnerProbeTests
{
    [Fact]
    public void ClassifyGlobalJson_ShouldUseTheNearestFileFromTheStartDirectory()
    {
        using var repo = TempDirectory.Create("appsurface-runner-probe-");
        repo.WriteFile("global.json", """{ "test": { "runner": "VSTest" } }""");
        var nested = repo.WriteFile("tests/Mtp.Tests/global.json", """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");
        repo.WriteFile("tests/Vs.Tests/Vs.Tests.csproj", "<Project />");

        var fromNested = VsTestRunnerProbe.ClassifyGlobalJson(Path.GetDirectoryName(nested)!);
        var fromSibling = VsTestRunnerProbe.ClassifyGlobalJson(Path.Join(repo.Path, "tests", "Vs.Tests"));

        Assert.False(fromNested.IsSupported);
        Assert.Equal(Path.GetFullPath(nested), fromNested.GlobalJsonPath);
        Assert.Equal("Microsoft.Testing.Platform", fromNested.Detail);
        Assert.True(fromSibling.IsSupported);
        Assert.Equal(Path.GetFullPath(Path.Join(repo.Path, "global.json")), fromSibling.GlobalJsonPath);
        Assert.Null(fromSibling.Detail);
    }

    [Theory]
    [InlineData("{ }")]
    [InlineData("{ \"test\": { } }")]
    [InlineData("{ \"test\": { \"runner\": \"vstest\" } }")]
    public void ClassifyGlobalJson_ShouldSupportVsTestOrNoRunner(string globalJson)
    {
        using var repo = TempDirectory.Create("appsurface-runner-probe-");
        repo.WriteFile("global.json", globalJson);

        Assert.True(VsTestRunnerProbe.ClassifyGlobalJson(repo.Path).IsSupported);
    }

    [Theory]
    [InlineData("[]", "malformed root object")]
    [InlineData("{ \"test\": 1 }", "malformed test configuration")]
    [InlineData("{ \"test\": { \"runner\": 5 } }", "malformed runner (Number)")]
    [InlineData("{ nope", "malformed JSON (JsonReaderException)")]
    public void ClassifyGlobalJson_ShouldDescribeMalformedFiles(string globalJson, string detail)
    {
        using var repo = TempDirectory.Create("appsurface-runner-probe-");
        repo.WriteFile("global.json", globalJson);

        var classification = VsTestRunnerProbe.ClassifyGlobalJson(repo.Path);

        Assert.False(classification.IsSupported);
        Assert.Equal(detail, classification.Detail);
    }

    [Fact]
    public void ClassifyGlobalJson_ShouldReportAnUnreadableFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var repo = TempDirectory.Create("appsurface-runner-probe-");
        var globalJson = repo.WriteFile("global.json", "{}");
        File.SetUnixFileMode(globalJson, UnixFileMode.None);
        try
        {
            var classification = VsTestRunnerProbe.ClassifyGlobalJson(repo.Path);

            Assert.False(classification.IsSupported);
            Assert.StartsWith("unreadable global.json (", classification.Detail, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(globalJson, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void ReadEvaluatedProperties_ShouldReadRunnerFrameworksAndRunSettings()
    {
        var evaluated = Read("""
            { "TestingPlatformDotnetTestSupport": "True", "TargetFrameworks": " net10.0 ; net9.0;", "TargetFramework": "ignored", "RunSettingsFilePath": " ci.runsettings " }
            """);

        Assert.True(evaluated.UsesTestingPlatform);
        Assert.Equal(["net10.0", "net9.0"], evaluated.TargetFrameworks);
        Assert.True(evaluated.DeclaresTargetFrameworks);
        Assert.Equal("ci.runsettings", evaluated.RunSettingsFilePath);
    }

    [Fact]
    public void ReadEvaluatedProperties_ShouldFallBackToTheSingleTargetFramework()
    {
        var evaluated = Read("""{ "TestingPlatformDotnetTestSupport": "", "TargetFrameworks": "  ", "TargetFramework": " net10.0 ", "RunSettingsFilePath": "" }""");

        Assert.False(evaluated.UsesTestingPlatform);
        Assert.Equal(["net10.0"], evaluated.TargetFrameworks);
        Assert.False(evaluated.DeclaresTargetFrameworks);
        Assert.Null(evaluated.RunSettingsFilePath);
    }

    [Theory]
    [InlineData("{ }")]
    [InlineData("{ \"TestingPlatformDotnetTestSupport\": true, \"TargetFramework\": 10, \"RunSettingsFilePath\": null }")]
    [InlineData("[]")]
    public void ReadEvaluatedProperties_ShouldTreatMissingOrNonStringValuesAsAbsent(string json)
    {
        var evaluated = Read(json);

        Assert.False(evaluated.UsesTestingPlatform);
        Assert.Empty(evaluated.TargetFrameworks);
        Assert.False(evaluated.DeclaresTargetFrameworks);
        Assert.Null(evaluated.RunSettingsFilePath);
    }

    private static VsTestEvaluatedProperties Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        return VsTestRunnerProbe.ReadEvaluatedProperties(document.RootElement);
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create(string prefix)
        {
            var path = System.IO.Path.Join(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(System.IO.Path.GetFullPath(path));
        }

        public string WriteFile(string relativePath, string content)
        {
            var fullPath = TestPathUtils.PathUnder(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
            return fullPath;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
