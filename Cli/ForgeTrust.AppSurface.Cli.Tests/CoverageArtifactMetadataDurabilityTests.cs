using ForgeTrust.AppSurface.Evidence.Coverage;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactMetadataDurabilityTests
{
    [Fact]
    public void DurabilityIsOptInForExistingObservationPublication()
    {
        using var temp = TestDirectory.Create();
        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(temp.Path, "private"), true, true)!;
        using var operation = CoverageArtifactOperationLease.Acquire(directory.ArtifactDirectoryPath, true);
        var syncCalls = 0;

        CoverageArtifactMetadataPublication.Write(directory, "state.json", "observation"u8.ToArray(), 1024,
            unixDirectoryFsync: _ =>
            {
                syncCalls++;
                return -1;
            });

        Assert.Equal(0, syncCalls);
        Assert.Equal("observation", File.ReadAllText(Path.Join(directory.ArtifactDirectoryPath, "state.json")));
    }

    [Fact]
    public void DurablePublicationUsesNativeRenameAndDirectorySync()
    {
        using var temp = TestDirectory.Create();
        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(temp.Path, "private"), true, true)!;
        using var operation = CoverageArtifactOperationLease.Acquire(directory.ArtifactDirectoryPath, true);
        var destination = Path.Join(directory.ArtifactDirectoryPath, "index.json");
        var staging = Path.Join(directory.ArtifactDirectoryPath, CoverageArtifactMetadataPublication.StagingName);
        CoverageArtifactMetadataPublication.Write(directory, "index.json", "old index"u8.ToArray(), 1024);

        CoverageArtifactMetadataPublication.Write(directory, "index.json", "complete index"u8.ToArray(), 1024,
            durableDirectoryEntry: true);

        Assert.Equal("complete index", File.ReadAllText(destination));
        Assert.False(File.Exists(staging));
    }

    [Fact]
    public void UnixSyncFailureOccursAfterRenameAndPreventsSuccessfulReturn()
    {
        if (OperatingSystem.IsWindows()) return;

        using var temp = TestDirectory.Create();
        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(temp.Path, "private"), true, true)!;
        using var operation = CoverageArtifactOperationLease.Acquire(directory.ArtifactDirectoryPath, true);
        var destination = Path.Join(directory.ArtifactDirectoryPath, "index.json");
        var staging = Path.Join(directory.ArtifactDirectoryPath, CoverageArtifactMetadataPublication.StagingName);
        CoverageArtifactMetadataPublication.Write(directory, "index.json", "old index"u8.ToArray(), 1024);
        var order = new List<string>();

        var error = Assert.Throws<IOException>(() => CoverageArtifactMetadataPublication.Write(
            directory, "index.json", "new index"u8.ToArray(), 1024,
            beforePublish: () =>
            {
                Assert.Equal("old index", File.ReadAllText(destination));
                order.Add("before rename");
            },
            durableDirectoryEntry: true,
            unixDirectoryFsync: descriptor =>
            {
                Assert.True(descriptor >= 0);
                Assert.Equal("new index", File.ReadAllText(destination));
                Assert.False(File.Exists(staging));
                order.Add("directory sync");
                return -1;
            }));

        Assert.True(error.Message.Contains("directory metadata", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "before rename", "directory sync" }, order);
        Assert.Equal("new index", File.ReadAllText(destination));
        Assert.False(File.Exists(staging));
    }

    [Fact]
    public void WindowsWriteThroughDurabilityAcceptsOnlyDocumentedNtfsPath()
    {
        CoverageRunOutputLease.EnsureWindowsWriteThroughDurabilitySupported("NTFS");
        Assert.Throws<PlatformNotSupportedException>(() =>
            CoverageRunOutputLease.EnsureWindowsWriteThroughDurabilitySupported("ReFS"));
        Assert.Throws<PlatformNotSupportedException>(() =>
            CoverageRunOutputLease.EnsureWindowsWriteThroughDurabilitySupported("unknown"));
    }

    [Fact]
    public void DirectoryPathDurabilityIsOptIn()
    {
        using var temp = TestDirectory.Create();
        var path = Path.Join(temp.Path, "new-parent", "receipt-root");
        var syncCalls = 0;

        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(path, createMissing: true, requirePrivate: true,
            unixDirectoryFsync: _ =>
            {
                syncCalls++;
                return -1;
            })!;

        Assert.Equal(0, syncCalls);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void DurableDirectoryPathSyncsNativeRetainedAncestors()
    {
        using var temp = TestDirectory.Create();
        var path = Path.Join(temp.Path, "new-parent", "receipt-root");
        if (OperatingSystem.IsWindows())
        {
            var error = Assert.Throws<PlatformNotSupportedException>(() =>
                CoverageRunOutputLease.AcquireArtifactDirectory(path, createMissing: true, requirePrivate: true,
                    durableDirectoryPath: true));
            Assert.True(error.Message.Contains("Windows", StringComparison.OrdinalIgnoreCase));
            Assert.False(Directory.Exists(path));
            return;
        }

        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(path, createMissing: true, requirePrivate: true,
            durableDirectoryPath: true)!;
        directory.VerifyArtifactBinding();
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void DurableDirectoryPathFlushesEveryRetainedAncestorRootToLeaf()
    {
        if (OperatingSystem.IsWindows()) return;

        using var temp = TestDirectory.Create();
        var path = Path.Join(temp.Path, "new-parent", "receipt-root");
        var observed = new List<string>();
        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(path, createMissing: true, requirePrivate: true,
            durableDirectoryPath: true,
            unixDirectoryFsync: descriptor =>
            {
                using var handle = new SafeFileHandle((nint)descriptor, ownsHandle: false);
                observed.Add(CoverageRunOutputLease.GetArtifactObjectIdentity(handle));
                return 0;
            })!;

        var expected = DirectoryIdentitiesFromRootTo(directory.ArtifactDirectoryPath);
        Assert.Equal(expected, observed);
    }

    [Fact]
    public void DurableDirectoryPathPropagatesFirstAncestorSyncFailure()
    {
        if (OperatingSystem.IsWindows()) return;

        using var temp = TestDirectory.Create();
        var path = Path.Join(temp.Path, "new-parent", "receipt-root");
        using var fileSystemRoot = CoverageRunOutputLease.AcquireArtifactDirectory(
            Path.GetPathRoot(Path.GetFullPath(path))!, createMissing: false, requirePrivate: false)!;
        var observedIdentity = string.Empty;
        var syncCalls = 0;

        var error = Assert.Throws<IOException>(() => CoverageRunOutputLease.AcquireArtifactDirectory(path,
            createMissing: true, requirePrivate: true, durableDirectoryPath: true,
            unixDirectoryFsync: descriptor =>
            {
                syncCalls++;
                using var handle = new SafeFileHandle((nint)descriptor, ownsHandle: false);
                observedIdentity = CoverageRunOutputLease.GetArtifactObjectIdentity(handle);
                return -1;
            }));

        Assert.True(error.Message.Contains("directory metadata", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, syncCalls);
        Assert.Equal(fileSystemRoot.ArtifactDirectoryIdentity, observedIdentity);
        Assert.True(Directory.Exists(path));
    }

    private static IReadOnlyList<string> DirectoryIdentitiesFromRootTo(string path)
    {
        var normalized = CoverageRunOutputLease.NormalizePlatformPath(Path.GetFullPath(path));
        var root = Path.GetPathRoot(normalized)!;
        var paths = new List<string> { root };
        var current = root;
        foreach (var component in Path.GetRelativePath(root, normalized)
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".") continue;
            current = Path.Join(current, component);
            paths.Add(current);
        }

        var identities = new List<string>(paths.Count);
        foreach (var directoryPath in paths)
        {
            using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(
                directoryPath, createMissing: false, requirePrivate: false)!;
            identities.Add(directory.ArtifactDirectoryIdentity);
        }
        return identities;
    }
}
