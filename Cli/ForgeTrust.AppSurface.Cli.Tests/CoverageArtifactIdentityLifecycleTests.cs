using System.Runtime.InteropServices;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Native host lifecycle evidence for live artifact-directory identity checks.</summary>
public sealed partial class CoverageArtifactIdentityLifecycleTests
{
    [Fact]
    public void ArtifactDirectoryIdentity_IsStableAcrossCloseAndReopenOnCurrentMountedVolume()
    {
        using var temp = TestDirectory.Create();
        var path = temp.CreateDirectory("artifact-root");
        string firstIdentity;
        using (var first = CoverageRunOutputLease.AcquireArtifactDirectory(path, false, false)!)
        {
            firstIdentity = first.ArtifactDirectoryIdentity;
            first.VerifyArtifactBinding();
        }

        using var reopened = CoverageRunOutputLease.AcquireArtifactDirectory(path, false, false)!;
        Assert.Equal(firstIdentity, reopened.ArtifactDirectoryIdentity);
        reopened.VerifyArtifactBinding();
    }

    [Fact]
    public void CopiedSchemaOneStore_IsRefusedAndItsBytesRemainUntouched()
    {
        using var temp = TestDirectory.Create();
        var original = TestPathUtils.PathUnder(temp.Path, "original-store");
        var copied = TestPathUtils.PathUnder(temp.Path, "copied-store");
        using (CoverageArtifactStore.Open(original)) { }
        CopyTree(original, copied);
        var originalBytes = ReadTree(original);
        var copiedBytes = ReadTree(copied);

        var writableFailure = Assert.ThrowsAny<InvalidDataException>(() => { using var _ = CoverageArtifactStore.Open(copied); });
        var readOnlyFailure = Assert.ThrowsAny<InvalidDataException>(() => { using var _ = CoverageArtifactStore.OpenReadOnly(copied); });
        foreach (var failure in new[] { writableFailure, readOnlyFailure })
        {
            // Both roots are on the same native volume. The copied marker must report
            // an object-identity refusal without suggesting that only volumes differ.
            var diagnostic = CoverageArtifactDiagnostic.Failure("ASCOV201", "status", failure);
            Assert.Equal("IdentityMismatch", diagnostic.Reason);
            Assert.Contains("ownership and continuity are unproven", diagnostic.Cause, StringComparison.Ordinal);
            Assert.Contains("in-place reconciliation is refused", diagnostic.Recovery, StringComparison.Ordinal);
            Assert.DoesNotContain(copied, diagnostic.Cause, StringComparison.Ordinal);
        }

        AssertTreeBytesEqual(originalBytes, ReadTree(original));
        AssertTreeBytesEqual(copiedBytes, ReadTree(copied));
    }

    [Fact]
    public void RetainedRootLease_RejectsPathReplacementAfterRename()
    {
        using var temp = TestDirectory.Create();
        var path = temp.CreateDirectory("root");
        var sentinel = TestPathUtils.PathUnder(path, "sentinel");
        File.WriteAllText(sentinel, "original");
        using var retained = CoverageRunOutputLease.AcquireArtifactDirectory(path, false, false, allowWindowsRenameSharing: true)!;
        var renamedRoot = TestPathUtils.PathUnder(temp.Path, "renamed-root");
        Directory.Move(path, renamedRoot);
        Directory.CreateDirectory(path);
        File.WriteAllText(sentinel, "replacement");

        Assert.Throws<IOException>(retained.VerifyArtifactBinding);
        Assert.Equal("original", File.ReadAllText(TestPathUtils.PathUnder(renamedRoot, "sentinel")));
        Assert.Equal("replacement", File.ReadAllText(sentinel));
    }

    [Fact]
    public void RetainedNestedLease_RejectsAncestorReplacement()
    {
        using var temp = TestDirectory.Create();
        var ancestor = temp.CreateDirectory("ancestor");
        var child = Directory.CreateDirectory(TestPathUtils.PathUnder(ancestor, "child")).FullName;
        using var retained = CoverageRunOutputLease.AcquireArtifactDirectory(child, false, false, allowWindowsRenameSharing: true)!;
        Directory.Move(ancestor, TestPathUtils.PathUnder(temp.Path, "renamed-ancestor"));
        Directory.CreateDirectory(ancestor);
        Directory.CreateDirectory(TestPathUtils.PathUnder(ancestor, "child"));

        Assert.Throws<IOException>(retained.VerifyArtifactBinding);
    }

    [Fact]
    public void ArtifactDirectoryAcquisition_RejectsLinkedRoot()
    {
        using var temp = TestDirectory.Create();
        var target = temp.CreateDirectory("target");
        var link = TestPathUtils.PathUnder(temp.Path, "linked-root");
        TestFileSystem.CreateDirectoryLinkOrSkip(link, target);

        Assert.Throws<IOException>(() => CoverageRunOutputLease.AcquireArtifactDirectory(link, false, false));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void MacOsApfsClone_HasDistinctObjectIdentityAndCopyOnWriteData()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var temp = TestDirectory.Create();
        var source = temp.WriteFile("source", "original clone payload");
        var clone = TestPathUtils.PathUnder(temp.Path, "clone");
        Assert.Equal(0, CloneFile(source, clone, 0));
        using var sourceStream = File.OpenRead(source);
        using var cloneStream = File.OpenRead(clone);
        var sourceIdentity = CoverageRunOutputLease.GetArtifactObjectIdentity(sourceStream.SafeFileHandle);
        var cloneIdentity = CoverageRunOutputLease.GetArtifactObjectIdentity(cloneStream.SafeFileHandle);
        Assert.NotEqual(sourceIdentity, cloneIdentity);

        File.WriteAllText(clone, "changed clone payload");
        Assert.Equal("original clone payload", File.ReadAllText(source));
        Assert.Equal("changed clone payload", File.ReadAllText(clone));
    }

    private static void CopyTree(string source, string destination)
    {
        using var copied = CoverageRunOutputLease.AcquireArtifactDirectory(destination, true, true)!;
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyTree(directory, TestPathUtils.PathUnder(destination, Path.GetFileName(directory)));
        foreach (var file in Directory.EnumerateFiles(source))
        {
            using var input = File.OpenRead(file);
            using var output = copied.OpenArtifactFile(Path.GetFileName(file), createNew: true);
            input.CopyTo(output);
            output.Flush(true);
        }
    }

    private static Dictionary<string, byte[]> ReadTree(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes, StringComparer.Ordinal);

    private static void AssertTreeBytesEqual(IReadOnlyDictionary<string, byte[]> expected, IReadOnlyDictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in expected) Assert.Equal(bytes, actual[path]);
    }

    [DllImport("libc", EntryPoint = "clonefile", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I4)]
    private static extern int CloneFile([MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, int flags);
}
