using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceArtifactPublicationCoverageTests
{
    [Fact]
    public void DurableMetadataPublicationRejectsADirectoryBeforeRenamingIt()
    {
        using var temporary = TestDirectory.Create();
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(
            TestPathUtils.PathUnder(temporary.Path, "private"), true, true)!;
        root.CreateArtifactDirectory("source").Dispose();

        Assert.Throws<IOException>(() => root.MoveArtifactEntry("source", root, "destination", durableDirectoryEntry: true));

        Assert.True(Directory.Exists(TestPathUtils.PathUnder(root.ArtifactDirectoryPath, "source")));
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(root.ArtifactDirectoryPath, "destination")));
    }

    [Fact]
    public void DurableFileMoveFlushesBothDirectoriesAfterMovingTheVerifiedBytes()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temporary = TestDirectory.Create();
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(
            TestPathUtils.PathUnder(temporary.Path, "private"), true, true)!;
        using var destination = root.CreateArtifactDirectory("destination");
        string identity;
        using (var file = root.OpenArtifactFile("source.json", true))
        {
            file.Write("authority"u8);
            file.Flush(true);
            identity = CoverageRunOutputLease.GetArtifactObjectIdentity(file.SafeFileHandle);
        }
        var flushes = 0;

        root.MoveArtifactEntry("source.json", destination, "state.json", expectedSourceIdentity: identity,
            durableDirectoryEntry: true, unixDirectoryFsync: descriptor =>
            {
                Assert.True(descriptor >= 0);
                Assert.False(File.Exists(TestPathUtils.PathUnder(root.ArtifactDirectoryPath, "source.json")));
                Assert.Equal("authority", File.ReadAllText(TestPathUtils.PathUnder(destination.ArtifactDirectoryPath, "state.json")));
                flushes++;
                return 0;
            });

        Assert.Equal(2, flushes);
        using var actual = destination.OpenArtifactFile("state.json", false);
        Assert.Equal(identity, CoverageRunOutputLease.GetArtifactObjectIdentity(actual.SafeFileHandle));
    }

    [Fact]
    public void WindowsPublicationRejectsTheUnixSyncSeamWithoutMovingCallerBytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temporary = TestDirectory.Create();
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(
            TestPathUtils.PathUnder(temporary.Path, "private"), true, true)!;
        using (var source = root.OpenArtifactFile("source.json", true)) source.Write("authority"u8);

        Assert.Throws<ArgumentException>(() => root.MoveArtifactEntry("source.json", root, "state.json",
            durableDirectoryEntry: true, unixDirectoryFsync: _ => throw new InvalidOperationException("Must not sync")));

        Assert.Equal("authority", File.ReadAllText(TestPathUtils.PathUnder(root.ArtifactDirectoryPath, "source.json")));
        Assert.False(File.Exists(TestPathUtils.PathUnder(root.ArtifactDirectoryPath, "state.json")));
    }

    [Fact]
    public void WindowsDurableDirectoryPathFailsBeforeCreatingAnyPathComponent()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temporary = TestDirectory.Create();
        var requestedPath = TestPathUtils.PathUnder(temporary.Path, "not-created", "artifact-root");

        var failure = Assert.Throws<PlatformNotSupportedException>(() =>
            CoverageRunOutputLease.AcquireArtifactDirectory(requestedPath, createMissing: true, requirePrivate: true,
                durableDirectoryPath: true));

        Assert.Contains("Durable directory-path publication is unavailable on Windows", failure.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(temporary.Path, "not-created")));
    }

    [Fact]
    public void WindowsDurableFilePublicationVerifiesFilesystemAndEitherMovesOnNtfsOrPreservesOnUnsupportedVolume()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temporary = TestDirectory.Create();
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(
            TestPathUtils.PathUnder(temporary.Path, "private"), createMissing: true, requirePrivate: true)!;
        using var destination = root.CreateArtifactDirectory("destination");
        var sourcePath = TestPathUtils.PathUnder(root.ArtifactDirectoryPath, "source.json");
        var destinationPath = TestPathUtils.PathUnder(destination.ArtifactDirectoryPath, "state.json");
        string identity;
        using (var source = root.OpenArtifactFile("source.json", createNew: true))
        {
            source.Write("durable-authority"u8);
            source.Flush(true);
            identity = CoverageRunOutputLease.GetArtifactObjectIdentity(source.SafeFileHandle);
        }

        try
        {
            root.MoveArtifactEntry("source.json", destination, "state.json", expectedSourceIdentity: identity,
                durableDirectoryEntry: true);
        }
        catch (PlatformNotSupportedException exception) when (exception.Message.Contains("requires NTFS", StringComparison.Ordinal))
        {
            Assert.Equal("durable-authority", File.ReadAllText(sourcePath));
            Assert.False(File.Exists(destinationPath));
            return;
        }

        Assert.False(File.Exists(sourcePath));
        Assert.Equal("durable-authority", File.ReadAllText(destinationPath));
        using var published = destination.OpenArtifactFile("state.json", createNew: false);
        Assert.Equal(identity, CoverageRunOutputLease.GetArtifactObjectIdentity(published.SafeFileHandle));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyOrWhitespaceWindowsFileSystemNameIsAnInvalidArgument(string name)
        => Assert.Throws<ArgumentException>(() => CoverageRunOutputLease.EnsureWindowsWriteThroughDurabilitySupported(name));
}
