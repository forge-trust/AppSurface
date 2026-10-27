using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactInspectionBoundaryTests
{
    [Fact]
    public void InvalidBudgetsAndOversizedContinuationAreRejectedBeforeTraversal()
    {
        using var temp = TestDirectory.Create();
        Assert.Throws<ArgumentOutOfRangeException>(() => Inspect(temp.Path, TimeSpan.FromTicks(-1), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Inspect(temp.Path, TimeSpan.FromSeconds(1), -1));
        Assert.Throws<InvalidDataException>(() => CoverageArtifactInspector.Inspect(temp.Path,
            TimeProvider.System, TimeSpan.FromSeconds(1), 100, CancellationToken.None,
            Enumerable.Range(0, 129).Select(i => "missing-" + i).ToArray()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a\\b")]
    [InlineData("TestResults//nested")]
    [InlineData("TestResults/./nested")]
    [InlineData("obj/TestResults")]
    public void UntrustedContinuationNeverCreatesPaths(string continuation)
    {
        using var temp = TestDirectory.Create();
        Assert.Throws<InvalidDataException>(() => CoverageArtifactInspector.Inspect(temp.Path,
            TimeProvider.System, TimeSpan.FromSeconds(1), 100, CancellationToken.None, [continuation]));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public void InvalidRootRemainsAnIncompleteObservationWithSanitizedCause()
    {
        var result = Inspect("\0", TimeSpan.FromSeconds(1), 100);
        Assert.False(result.Complete);
        Assert.Equal(0, result.Bytes);
        Assert.Single(result.Errors);
        Assert.Equal("ArgumentException", result.Errors[0].Type);
        Assert.Equal(".", result.Errors[0].Scope);
    }

    [Fact]
    public void MissingContinuationErrorsAreBoundedAndDoNotInventBytes()
    {
        using var temp = TestDirectory.Create();
        var result = CoverageArtifactInspector.Inspect(temp.Path, TimeProvider.System,
            TimeSpan.FromSeconds(1), 100, CancellationToken.None,
            Enumerable.Range(0, 40).Select(i => "missing-" + i).ToArray());
        Assert.False(result.Complete);
        Assert.Equal(0, result.Bytes);
        Assert.Equal(32, result.Errors.Count);
        Assert.All(result.Errors, error => Assert.Equal("ContinuationUnavailable", error.Type));
    }

    [Fact]
    public void RootWithLinkedAncestorIsRejectedBeforeCountingOutsideFiles()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = TestDirectory.Create();
        var outside = temp.CreateDirectory("outside/repository/TestResults");
        File.WriteAllText(Path.Join(outside, "coverage.xml"), "outside bytes");
        var link = Path.Join(temp.Path, "alias");
        Directory.CreateSymbolicLink(link, Path.Join(temp.Path, "outside"));
        try
        {
            var result = Inspect(Path.Join(link, "repository"), TimeSpan.FromSeconds(1), 100);
            Assert.False(result.Complete);
            Assert.Equal(0, result.Bytes);
            Assert.Contains(result.Errors, error => error.Type == "LinkAncestor");
            Assert.Equal("outside bytes", File.ReadAllText(Path.Join(outside, "coverage.xml")));
        }
        finally { Directory.Delete(link); }
    }

    private static CoverageArtifactObservation Inspect(string root, TimeSpan budget, int entries)
        => CoverageArtifactInspector.Inspect(root, TimeProvider.System, budget, entries, CancellationToken.None);
}
