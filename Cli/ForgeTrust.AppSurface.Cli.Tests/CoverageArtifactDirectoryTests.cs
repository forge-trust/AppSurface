using System.Text;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactDirectoryTests
{
    [Fact]
    public void NativeFileStateReportsActualByteLengthAndRetainedIdentity()
    {
        using var temp = TempDirectory.Create("appsurface-file-state-");
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(temp.Path, "private"), true, true)!;
        using var file = root.OpenArtifactFile("coverage.xml", true);
        file.Write(new byte[123]);
        file.Flush(true);

        var state = CoverageRunOutputLease.GetArtifactObjectState(file.SafeFileHandle);

        Assert.Equal(123, state.Length);
        Assert.Equal(CoverageRunOutputLease.GetArtifactObjectIdentity(file.SafeFileHandle), state.Identity);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Join(root.ArtifactDirectoryPath, "coverage.xml"))).ToUnixTimeSeconds(), state.ModifiedSeconds);
    }

    [Fact]
    public void PrivateDirectory_ShouldPublishOriginalBytesAndDeleteOnlyAuthorizedEntry()
    {
        using var temp = TempDirectory.Create("appsurface-private-artifacts-");
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(temp.Path, "private"), true, true)!;
        using (var stage = root.CreateArtifactDirectory("stage"))
        {
            using var payload = stage.OpenArtifactFile("payload.gz", createNew: true);
            payload.Write(Encoding.UTF8.GetBytes("exact bytes\n"));
        }
        using (var stage = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(root.ArtifactDirectoryPath, "stage"), false, true)!)
        {
            using var input = stage.OpenArtifactFile("payload.gz", false);
            using var reader = new StreamReader(input);
            Assert.Equal("exact bytes\n", reader.ReadToEnd());
        }
        root.MoveArtifactEntry("stage", root, "complete");
        var published = Assert.Single(root.EnumerateArtifactEntries(10));
        Assert.Equal("complete", published.Name);
        root.DeleteArtifactEntry("complete", 10);
        Assert.Empty(root.EnumerateArtifactEntries(10));
        Assert.True(Directory.Exists(root.ArtifactDirectoryPath));
    }

    [Fact]
    public void Publish_ShouldRejectExistingDestinationWithoutReplacingEitherTree()
    {
        using var temp = TempDirectory.Create("appsurface-private-artifacts-");
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(temp.Path, "private"), true, true)!;
        root.CreateArtifactDirectory("stage").Dispose();
        root.CreateArtifactDirectory("existing").Dispose();
        Assert.Throws<IOException>(() => root.MoveArtifactEntry("stage", root, "existing"));
        Assert.Equal(2, root.EnumerateArtifactEntries(2).Count);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:")]
    [InlineData("name.")]
    [InlineData("name ")]
    [InlineData("CON.txt")]
    [InlineData("lpt9")]
    [InlineData("a\n")]
    public void Component_ShouldRejectNonportableManifestNames(string name)
        => Assert.Throws<InvalidDataException>(() => CoverageRunOutputLease.ValidateArtifactComponent(name));

    [Fact]
    public void ExistingOnly_ShouldNotCreateMissingComponents()
    {
        using var temp = TempDirectory.Create("appsurface-private-artifacts-");
        var path = Path.Join(temp.Path, "missing", "child");
        Assert.Null(CoverageRunOutputLease.AcquireArtifactDirectory(path, false, true));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void EnumerationAndDeletion_ShouldRespectEntryLimitsBeforeMutation()
    {
        using var temp = TempDirectory.Create("appsurface-private-artifacts-");
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(temp.Path, "private"), true, true)!;
        using (var stage = root.CreateArtifactDirectory("stage"))
        {
            stage.OpenArtifactFile("one", true).Dispose();
            stage.OpenArtifactFile("two", true).Dispose();
            Assert.Throws<IOException>(() => stage.EnumerateArtifactEntries(1));
            Assert.Equal(2, stage.EnumerateArtifactEntries(2).Count);
        }
        Assert.Throws<IOException>(() => root.DeleteArtifactEntry("stage", 2));
        Assert.True(Directory.Exists(Path.Join(root.ArtifactDirectoryPath, "stage")));
        root.DeleteArtifactEntry("stage", 3);
        Assert.Empty(root.EnumerateArtifactEntries(1));
    }

    [Fact]
    public void ExistingBroadPermissions_ShouldFailWithoutRepair()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = TempDirectory.Create("appsurface-private-artifacts-");
        var path = Path.Join(temp.Path, "broad");
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);
        var mode = File.GetUnixFileMode(path);
        Assert.Throws<IOException>(() => CoverageRunOutputLease.AcquireArtifactDirectory(path, false, true));
        Assert.Equal(mode, File.GetUnixFileMode(path));
    }

    [Fact]
    public void ParentSwap_ShouldKeepExternalSentinelUnchangedAndFailBinding()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = TempDirectory.Create("appsurface-private-artifacts-");
        var parent = Path.Join(temp.Path, "parent");
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(parent, "private"), true, true)!;
        var outside = Path.Join(temp.Path, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Join(outside, "sentinel"), "untouched");
        Directory.Move(parent, parent + "-moved");
        Directory.CreateSymbolicLink(parent, outside);
        try
        {
            Assert.Throws<IOException>(() => root.CreateArtifactDirectory("escaped"));
            Assert.Equal("untouched", File.ReadAllText(Path.Join(outside, "sentinel")));
            Assert.False(Directory.Exists(Path.Join(outside, "private")));
        }
        finally { Directory.Delete(parent); }
    }

    [Fact]
    public void Publish_ShouldRejectChangedSourceIdentity()
    {
        using var temp = TempDirectory.Create("appsurface-private-artifacts-");
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(temp.Path, "private"), true, true)!;
        string identity;
        using (var stage = root.CreateArtifactDirectory("stage")) identity = stage.ArtifactDirectoryIdentity;
        root.MoveArtifactEntry("stage", root, "old");
        root.CreateArtifactDirectory("stage").Dispose();
        Assert.Throws<IOException>(() => root.MoveArtifactEntry("stage", root, "destination", expectedSourceIdentity: identity));
        Assert.False(Directory.Exists(Path.Join(root.ArtifactDirectoryPath, "destination")));
    }

    private sealed class TempDirectory(string path) : IDisposable
    {
        internal string Path { get; } = path;

        internal static TempDirectory Create(string prefix)
        {
            var path = System.IO.Path.Join(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
