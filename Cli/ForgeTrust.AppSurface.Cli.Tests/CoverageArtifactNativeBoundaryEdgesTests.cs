using System.ComponentModel;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactNativeBoundaryEdgesTests
{
    [Fact]
    public void IncompleteWindowsSimulationCannotAccidentallyCallNativeExports()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = TestDirectory.Create();
        var missing = Path.Join(temp.Path, "missing");
        Assert.Throws<ArgumentException>(() => CoverageArtifactOperationLease.Acquire(missing, true,
            new CoverageArtifactOperationLeaseOptions { IsWindows = true }));
        Assert.False(Directory.Exists(missing));
        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(temp.Path, false, false)!;
        Assert.Throws<PlatformNotSupportedException>(() => directory.OpenArtifactOperationLock());
    }

    [Fact]
    public void DirectoryAndFileReplacementFailuresPreserveBothSources()
    {
        using var temp = TestDirectory.Create();
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(temp.Path, "private"), true, true)!;
        using (var child = root.CreateArtifactDirectory("source")) { }
        using (var destination = root.CreateArtifactDirectory("destination")) { }
        Assert.Throws<IOException>(() => root.MoveArtifactEntry("source", root, "destination", replaceFile: true));
        Assert.Throws<IOException>(() => root.DeleteArtifactEntry("source", 10, "wrong-identity"));
        Assert.Equal(2, root.EnumerateArtifactEntries(3).Count);
    }

    [Fact]
    public void UnixCreateExistingAndWrongKindErrorsDoNotTruncateFiles()
    {
        using var temp = TestDirectory.Create();
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(temp.Path, "private"), true, true)!;
        using (var stream = root.OpenArtifactFile("payload", true)) stream.Write("sentinel"u8);
        Assert.Throws<IOException>(() => root.OpenArtifactFile("payload", true));
        Assert.Throws<IOException>(() => root.CreateArtifactDirectory("payload"));
        Assert.Equal("sentinel", File.ReadAllText(Path.Join(root.ArtifactDirectoryPath, "payload")));
        Assert.Throws<ArgumentOutOfRangeException>(() => root.EnumerateArtifactEntries(0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void WindowsSimulationRequiresEveryDelegate(int missing)
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = TestDirectory.Create();
        var options = new CoverageArtifactOperationLeaseOptions
        {
            IsWindows = true,
            OpenWindowsLock = missing == 0 ? null : _ => throw new InvalidOperationException("must not open"),
            TryWindowsLock = missing == 1 ? null : (_, _) => throw new InvalidOperationException("must not lock"),
            VerifyWindowsLockBinding = missing == 2 ? null : (_, _, _) => throw new InvalidOperationException("must not verify")
        };
        Assert.Throws<ArgumentException>(() => CoverageArtifactOperationLease.Acquire(temp.Path, true, options));
    }
}
