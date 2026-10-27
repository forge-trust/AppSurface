using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactInspectionRacePathsTests
{
    [Fact]
    public void MissingContinuationComponentIsRejectedWithoutFollowingItsParent()
    {
        using var temp = TestDirectory.Create();
        var hidden = temp.CreateDirectory("node_modules/TestResults");
        File.WriteAllText(Path.Join(hidden, "coverage.xml"), "private");
        Assert.Throws<InvalidDataException>(() => CoverageArtifactInspector.Inspect(temp.Path,
            TimeProvider.System, TimeSpan.FromSeconds(1), 100, default, ["TestResults/../escape"]));
        Assert.Equal("private", File.ReadAllText(Path.Join(hidden, "coverage.xml")));
    }

    [Fact]
    public void DirectoryEnumerationPermissionFailureIsAnIncompleteLowerBound()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = TestDirectory.Create();
        var output = temp.CreateDirectory("TestResults");
        var report = Path.Join(output, "coverage.xml");
        File.WriteAllText(report, "retained");
        var prior = File.GetUnixFileMode(output);
        try
        {
            File.SetUnixFileMode(output, UnixFileMode.UserExecute);
            var observed = CoverageArtifactInspector.Inspect(temp.Path, TimeProvider.System,
                TimeSpan.FromSeconds(2), 100, default);
            Assert.False(observed.Complete);
            Assert.Equal(0, observed.Bytes);
            Assert.Contains(observed.Errors, error => error.Type is "UnauthorizedAccessException" or "IOException");
        }
        finally { File.SetUnixFileMode(output, prior); }
        Assert.Equal("retained", File.ReadAllText(report));
    }

    [Fact]
    public void FileRemovedAfterEnumerationIsReportedWithoutDiscardingEarlierBytes()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = TestDirectory.Create();
        var output = temp.CreateDirectory("TestResults");
        var retained = temp.CreateDirectory("retained");
        File.WriteAllText(Path.Join(output, "first.xml"), "retained");
        File.WriteAllText(Path.Join(output, "second.xml"), "retained");
        var clock = new MutationClock(4, () =>
        {
            foreach (var path in Directory.EnumerateFiles(output))
                File.Move(path, TestPathUtils.PathUnder(retained, Path.GetFileName(path)));
        });
        var observed = CoverageArtifactInspector.Inspect(temp.Path, clock, TimeSpan.FromSeconds(2), 100, default, ["TestResults"]);
        Assert.False(observed.Complete);
        Assert.Equal(8, observed.Bytes);
        Assert.Contains(observed.Errors, error => error.Type == "FileNotFoundException");
        Assert.Equal(2, Directory.EnumerateFiles(retained).Count());
        Assert.All(Directory.EnumerateFiles(retained), path => Assert.Equal("retained", File.ReadAllText(path)));
    }

    private sealed class MutationClock(int observation, Action mutate) : TimeProvider
    {
        private int _calls;
        public override long GetTimestamp()
        {
            if (++_calls == observation) mutate();
            return 0;
        }
    }
}
