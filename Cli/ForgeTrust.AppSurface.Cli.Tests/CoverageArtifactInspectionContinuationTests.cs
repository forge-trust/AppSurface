using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactInspectionContinuationTests
{
    [Fact]
    public void LimitedScanRotatesPendingDirectoriesInsteadOfRestartingTheSameBranch()
    {
        using var fixture = TestDirectory.Create();
        for (var index = 0; index < 4; index++)
        {
            var output = fixture.CreateDirectory($"project-{index}/TestResults");
            File.WriteAllText(Path.Join(output, "coverage.xml"), new string('x', index + 1));
        }
        IReadOnlyList<string>? cursor = null;
        var seen = new HashSet<string>();
        for (var iteration = 0; iteration < 12; iteration++)
        {
            var result = CoverageArtifactInspector.Inspect(fixture.Path, TimeProvider.System,
                TimeSpan.FromSeconds(1), 4, CancellationToken.None, cursor);
            foreach (var item in result.Largest) seen.Add(item.Path);
            Assert.InRange(result.Entries, 0, 4);
            Assert.InRange(result.ContinuationDirectories.Count, 0, 128);
            if (cursor is { Count: > 0 }) Assert.False(result.Complete);
            cursor = result.ContinuationDirectories;
        }
        Assert.Equal(4, seen.Count);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/outside")]
    [InlineData("TestResults/../outside")]
    [InlineData(".git/objects")]
    [InlineData("bin/TestResults")]
    public void InvalidContinuationNeverReadsOutsideTheApprovedScanRoot(string continuation)
    {
        using var fixture = TestDirectory.Create();
        Assert.Throws<InvalidDataException>(() => CoverageArtifactInspector.Inspect(fixture.Path,
            TimeProvider.System, TimeSpan.FromSeconds(1), 100, CancellationToken.None, [continuation]));
    }

    [Fact]
    public void ResumedDirectoryReplacedByLinkIsPreservedAndReportedIncomplete()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = TestDirectory.Create();
        var repository = fixture.CreateDirectory("repository");
        var outside = fixture.CreateDirectory("outside");
        File.WriteAllText(Path.Join(outside, "coverage.xml"), "do not inspect");
        var link = Path.Join(repository, "TestResults");
        Directory.CreateSymbolicLink(link, outside);
        try
        {
            var result = CoverageArtifactInspector.Inspect(repository, TimeProvider.System,
                TimeSpan.FromSeconds(1), 100, CancellationToken.None, ["TestResults"]);
            Assert.False(result.Complete);
            Assert.Equal(0, result.Bytes);
            Assert.Contains(result.Errors, error => error.Type == "ContinuationUnavailable");
            Assert.Equal("do not inspect", File.ReadAllText(Path.Join(outside, "coverage.xml")));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void ResumedContinuationRejectsPathInsideNestedRepository()
    {
        using var fixture = TestDirectory.Create();
        var nestedRepository = fixture.CreateDirectory("nested");
        Directory.CreateDirectory(Path.Join(nestedRepository, ".git"));
        var output = fixture.CreateDirectory("nested/TestResults");
        File.WriteAllText(Path.Join(output, "report.xml"), "must not count");

        var result = CoverageArtifactInspector.Inspect(fixture.Path, TimeProvider.System,
            TimeSpan.FromSeconds(1), 100, CancellationToken.None, ["nested/TestResults"]);

        Assert.False(result.Complete);
        Assert.Equal(0, result.Bytes);
        Assert.Contains(result.Errors, error => error.Type == "ContinuationUnavailable");
    }

    [Fact]
    public void OversizedPendingQueueIsPartialAndBoundedWithoutInventingTotals()
    {
        using var fixture = TestDirectory.Create();
        for (var index = 0; index < 140; index++) fixture.CreateDirectory("project-" + index);
        var result = CoverageArtifactInspector.Inspect(fixture.Path, TimeProvider.System,
            TimeSpan.FromSeconds(1), 1000, CancellationToken.None);
        Assert.False(result.Complete);
        Assert.InRange(result.ContinuationDirectories.Count, 0, 128);
        Assert.Contains(result.Errors, error => error.Type == "ContinuationCapacity");
        Assert.Equal(0, result.Bytes);
    }

    [Fact]
    public void RepeatedBoundedScansEventuallyReachDirectoriesBeyondContinuationCapacity()
    {
        using var fixture = TestDirectory.Create();
        const int directoryCount = 140;
        for (var index = 0; index < directoryCount; index++)
        {
            var output = fixture.CreateDirectory($"project-{index:D3}/TestResults");
            File.WriteAllText(Path.Join(output, $"coverage-{index:D3}.xml"), new string('x', index + 1));
        }

        IReadOnlyList<string>? cursor = null;
        var observed = new HashSet<string>(StringComparer.Ordinal);
        var reachedLateDirectory = false;
        for (var iteration = 0; iteration < 16 && !reachedLateDirectory; iteration++)
        {
            var result = CoverageArtifactInspector.Inspect(fixture.Path, TimeProvider.System,
                TimeSpan.FromSeconds(2), 512, CancellationToken.None, cursor);
            Assert.InRange(result.Entries, 0, 512);
            Assert.InRange(result.ContinuationDirectories.Count, 0, 128);
            if (cursor is { Count: > 0 } || result.ContinuationDirectories.Count > 0)
                Assert.False(result.Complete);
            foreach (var item in result.Largest)
            {
                observed.Add(item.Path);
                if (item.Path.EndsWith("project-139/TestResults/coverage-139.xml", StringComparison.Ordinal))
                    reachedLateDirectory = true;
            }
            cursor = result.ContinuationDirectories;
        }

        Assert.True(reachedLateDirectory, $"Late directory was not observed; saw {observed.Count} distinct contributor paths.");
    }
}
