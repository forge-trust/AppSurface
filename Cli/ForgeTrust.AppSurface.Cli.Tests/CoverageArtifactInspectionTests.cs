using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactInspectionTests
{
    [Fact]
    public void Inspect_CountsOnlyRegularFilesInKnownOutputRoots()
    {
        using var temp = TempDirectory.Create("coverage-inspect-");
        Directory.CreateDirectory(Path.Join(temp.Path, "src", "TestResults", "run"));
        File.WriteAllBytes(Path.Join(temp.Path, "src", "TestResults", "run", "a.cobertura.xml"), [1, 2, 3]);
        File.WriteAllText(Path.Join(temp.Path, "src", "Program.cs"), "ignored");
        var result = Inspect(temp.Path, 100);
        Assert.Equal(3, result.Bytes);
        Assert.Equal(1, result.FileCount);
        Assert.True(result.Complete);
    }

    [Fact]
    public void Inspect_ExactEntryLimitCompletesAndOneLessReturnsPartialLowerBound()
    {
        using var temp = TempDirectory.Create("coverage-inspect-limit-");
        Directory.CreateDirectory(Path.Join(temp.Path, "TestResults"));
        File.WriteAllText(Path.Join(temp.Path, "TestResults", "one.xml"), "1234");
        var exact = Inspect(temp.Path, 2);
        var shortResult = Inspect(temp.Path, 1);
        Assert.True(exact.Complete);
        Assert.Equal(1, exact.FileCount);
        Assert.False(shortResult.Complete);
        Assert.Equal(0, shortResult.Bytes);
    }

    [Fact]
    public void Inspect_ReturnsAccumulatedLowerBoundWhenCancelled()
    {
        using var temp = TempDirectory.Create("coverage-inspect-cancel-");
        Directory.CreateDirectory(Path.Join(temp.Path, "TestResults"));
        File.WriteAllText(Path.Join(temp.Path, "TestResults", "one.xml"), "abc");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = CoverageArtifactInspector.Inspect(temp.Path, TimeProvider.System, TimeSpan.FromSeconds(1), 100, cts.Token);
        Assert.True(result.Cancelled);
        Assert.False(result.Complete);
    }

    [Fact]
    public void Inspect_MissingRootIsIncompleteWithSanitizedCause()
    {
        using var temp = TempDirectory.Create("coverage-inspect-missing-");
        var result = Inspect(Path.Join(temp.Path, "missing"), 10);
        Assert.False(result.Complete);
        Assert.Equal("MissingRoot", Assert.Single(result.Errors).Type);
        Assert.Equal(".", result.Errors[0].Scope);
    }

    [Fact]
    public void Inspect_SkipsNestedRepositoriesAndSecretLikeContributorNames()
    {
        using var temp = TempDirectory.Create("coverage-inspect-scope-");
        var outer = Path.Join(temp.Path, "TestResults");
        var nested = Path.Join(outer, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Join(outer, "api_token_report.xml"), "123");
        File.WriteAllText(Path.Join(nested, ".git"), "gitdir: elsewhere");
        Directory.CreateDirectory(Path.Join(nested, "TestResults"));
        File.WriteAllText(Path.Join(nested, "TestResults", "ignored.xml"), "ignored");
        var result = Inspect(temp.Path, 100);
        Assert.Equal(3, result.Bytes);
        Assert.DoesNotContain(result.Largest, item => item.Path.Contains("api_token", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Largest, item => item.Path.Contains("<redacted>", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_SkipsDirectoryLinks()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = TempDirectory.Create("coverage-inspect-link-");
        var output = Path.Join(temp.Path, "TestResults");
        var target = Path.Join(temp.Path, "outside");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Join(target, "linked.xml"), "must not count");
        Directory.CreateSymbolicLink(Path.Join(output, "linked"), target);
        var result = Inspect(temp.Path, 100);
        Assert.Equal(0, result.Bytes);
        Assert.Equal(0, result.FileCount);
    }

    [Fact]
    public void Inspect_StopsAtTimeBudgetWithPartialResult()
    {
        using var temp = TempDirectory.Create("coverage-inspect-time-");
        Directory.CreateDirectory(Path.Join(temp.Path, "TestResults"));
        File.WriteAllText(Path.Join(temp.Path, "TestResults", "one.xml"), "abc");
        var clock = new AdvancingTimeProvider();
        var result = CoverageArtifactInspector.Inspect(temp.Path, clock, TimeSpan.FromMilliseconds(1), 100, CancellationToken.None);
        Assert.False(result.Complete);
        Assert.True(result.Bytes >= 0);
    }

    [Fact]
    public void Inspect_ExactlyReachedEntryLimitIsCompleteWhenTraversalIsExhausted()
    {
        using var temp = TempDirectory.Create("coverage-inspect-exact-");
        Directory.CreateDirectory(Path.Join(temp.Path, "TestResults"));
        File.WriteAllText(Path.Join(temp.Path, "TestResults", "one.xml"), "x");
        Assert.True(Inspect(temp.Path, 2).Complete);
    }

    [Theory]
    [InlineData("<RunSettings><DataCollectionRunSettings><DataCollectors><DataCollector friendlyName=\"XPlat Code Coverage\" /></DataCollectors></DataCollectionRunSettings></RunSettings>", "enabled")]
    [InlineData("<RunSettings><DataCollector friendlyName=\"XPlat Code Coverage\" enabled=\"false\" /></RunSettings>", "disabled")]
    [InlineData("<RunSettings><DataCollector friendlyName=\"XPlat Code Coverage\" enabled=\"true\" /></RunSettings>", "enabled")]
    [InlineData("<RunSettings><DataCollector friendlyName=\"XPlat Code Coverage\" enabled=\"unexpected\" /></RunSettings>", "unknown")]
    [InlineData("<RunSettings><DataCollector friendlyName=\"XPlat Code Coverage\" isEnabled=\"false\" /></RunSettings>", "unknown")]
    [InlineData("<RunSettings />", "disabled")]
    [InlineData("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><x>&e;</x>", "unknown")]
    [InlineData("<broken", "unknown")]
    public void ReadCollectorRisk_ReportsConfiguredStateWithoutInferringExecution(string xml, string state)
    {
        using var temp = TempDirectory.Create("coverage-settings-");
        var path = Path.Join(temp.Path, "settings.runsettings");
        File.WriteAllText(path, xml);
        Assert.Equal(state, CoverageArtifactInspector.ReadCollectorRisk(path).State);
    }

    [Fact]
    public void ReadCollectorRisk_ReportsAbsentAndOversizedFiles()
    {
        using var temp = TempDirectory.Create("coverage-settings-bounds-");
        Assert.Equal("absent", CoverageArtifactInspector.ReadCollectorRisk(Path.Join(temp.Path, "missing")).State);
        var path = Path.Join(temp.Path, "large.runsettings");
        File.WriteAllBytes(path, new byte[65_537]);
        var result = CoverageArtifactInspector.ReadCollectorRisk(path);
        Assert.Equal("unknown", result.State);
        Assert.Equal("settings-file-over-64KiB", result.Cause);
    }

    private static CoverageArtifactObservation Inspect(string root, int entries) =>
        CoverageArtifactInspector.Inspect(root, TimeProvider.System, TimeSpan.FromSeconds(5), entries, CancellationToken.None);

    private sealed class TempDirectory(string path) : IDisposable
    {
        internal string Path { get; } = path;
        internal static TempDirectory Create(string prefix)
        {
            var path = System.IO.Path.Join(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new(path);
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1_000;
        public override long GetTimestamp() => Interlocked.Add(ref _timestamp, 1);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }
}
