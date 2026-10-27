using System.Text;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactMetadataPublicationTests
{
    [Fact]
    public void RepeatedFailedPublicationLeavesExistingStateAndNoGrowingRemnants()
    {
        using var temp = TestDirectory.Create();
        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(temp.Path, "private"), true, true)!;
        using var operation = CoverageArtifactOperationLease.Acquire(directory.ArtifactDirectoryPath, true);
        CoverageArtifactMetadataPublication.Write(directory, "state.json", "old state"u8.ToArray(), 1024);
        for (var attempt = 0; attempt < 25; attempt++)
        {
            Assert.Throws<IOException>(() => CoverageArtifactMetadataPublication.Write(directory, "state.json",
                "new state"u8.ToArray(), 1024, () => throw new IOException("publication failed")));
            Assert.Equal("old state", File.ReadAllText(Path.Join(directory.ArtifactDirectoryPath, "state.json")));
            Assert.Single(directory.EnumerateArtifactEntries(3), entry => entry.Name != ".appsurface-coverage-operation.lock");
        }
    }

    [Fact]
    public void InterruptedKnownPrivateSlotIsReconciledBeforeNextPublication()
    {
        using var temp = TestDirectory.Create();
        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(temp.Path, "private"), true, true)!;
        using var operation = CoverageArtifactOperationLease.Acquire(directory.ArtifactDirectoryPath, true);
        using (var abandoned = directory.OpenArtifactFile(CoverageArtifactMetadataPublication.StagingName, true))
            abandoned.Write("interrupted"u8);
        CoverageArtifactMetadataPublication.Write(directory, "state.json", "complete"u8.ToArray(), 1024);
        Assert.Equal("complete", File.ReadAllText(Path.Join(directory.ArtifactDirectoryPath, "state.json")));
        Assert.False(File.Exists(Path.Join(directory.ArtifactDirectoryPath, CoverageArtifactMetadataPublication.StagingName)));
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("oversized")]
    [InlineData("broad")]
    public void UnprovenSlotIsPreservedAndPreventsReplacement(string fault)
    {
        if (fault == "broad" && OperatingSystem.IsWindows()) return;
        using var temp = TestDirectory.Create();
        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(temp.Path, "private"), true, true)!;
        using var operation = CoverageArtifactOperationLease.Acquire(directory.ArtifactDirectoryPath, true);
        CoverageArtifactMetadataPublication.Write(directory, "state.json", "old"u8.ToArray(), 32);
        var slot = Path.Join(directory.ArtifactDirectoryPath, CoverageArtifactMetadataPublication.StagingName);
        if (fault == "directory") directory.CreateArtifactDirectory(CoverageArtifactMetadataPublication.StagingName).Dispose();
        else
        {
            using (var stream = directory.OpenArtifactFile(CoverageArtifactMetadataPublication.StagingName, true))
                stream.Write(new byte[fault == "oversized" ? 33 : 3]);
            if (fault == "broad") File.SetUnixFileMode(slot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        }
        var error = Assert.ThrowsAny<Exception>(() => CoverageArtifactMetadataPublication.Write(directory, "state.json", "new"u8.ToArray(), 32));
        Assert.True(error is IOException or InvalidDataException);
        Assert.Equal("old", File.ReadAllText(Path.Join(directory.ArtifactDirectoryPath, "state.json")));
        Assert.True(File.Exists(slot) || Directory.Exists(slot));
    }

    [Fact]
    public void OversizedNewStateFailsBeforeDeletingARecoverableSlot()
    {
        using var temp = TestDirectory.Create();
        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(temp.Path, "private"), true, true)!;
        using (var stream = directory.OpenArtifactFile(CoverageArtifactMetadataPublication.StagingName, true)) stream.Write("keep"u8);
        Assert.Throws<InvalidDataException>(() => CoverageArtifactMetadataPublication.Write(directory, "state.json", new byte[33], 32));
        Assert.Equal("keep", File.ReadAllText(Path.Join(directory.ArtifactDirectoryPath, CoverageArtifactMetadataPublication.StagingName)));
    }

    [Fact]
    public void PublicationCleanupNeverDeletesAReplacedSlot()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = TestDirectory.Create();
        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(temp.Path, "private"), true, true)!;
        using var operation = CoverageArtifactOperationLease.Acquire(directory.ArtifactDirectoryPath, true);
        var slot = Path.Join(directory.ArtifactDirectoryPath, CoverageArtifactMetadataPublication.StagingName);
        Assert.Throws<IOException>(() => CoverageArtifactMetadataPublication.Write(directory, "state.json", "stage"u8.ToArray(), 1024, () =>
        {
            File.Move(slot, slot + "-moved");
            File.WriteAllText(slot, "unowned replacement", new UTF8Encoding(false));
            throw new IOException("interrupted after replacement");
        }));
        Assert.Equal("unowned replacement", File.ReadAllText(slot));
        Assert.Equal("stage", File.ReadAllText(slot + "-moved"));
        Assert.False(File.Exists(Path.Join(directory.ArtifactDirectoryPath, "state.json")));
    }
}
