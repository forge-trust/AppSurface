using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class QualifiedLeaseDirectoryTests
{
    [Fact]
    public void NativeLeaseExcludesRetainsLockAndPersistsRecords()
    {
        if (!Supported()) return;
        using var folder = new PrivateFolder();
        QualifiedLeaseDirectory.FileId lockId;
        using (var first = new QualifiedLeaseDirectory(folder.Path, "key"))
        {
            lockId = first.Identity("owner.lock");
            var failure = Assert.Throws<QualifiedOwnershipFailure>(() => new QualifiedLeaseDirectory(folder.Path, "key"));
            Assert.Equal("Contention", failure.Kind);
            Assert.False(first.Exists("active.json"));
            first.WriteNew("active.json", "{\"state\":1}"u8.ToArray());
            Assert.Equal("{\"state\":1}", System.Text.Encoding.UTF8.GetString(first.Read("active.json")));
            first.Replace("active.json", "{\"state\":2}"u8.ToArray());
            first.VerifyAnchor();
            Assert.Equal("{\"state\":2}", System.Text.Encoding.UTF8.GetString(first.Read("active.json")));
            first.Delete("active.json");
            Assert.False(first.Exists("active.json"));
        }

        using var next = new QualifiedLeaseDirectory(folder.Path, "key");
        Assert.Equal(lockId, next.Identity("owner.lock"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65_537)]
    public void NativeLeaseRejectsMarkersOutsideTheBoundedRecordSize(int length)
    {
        if (!Supported()) return;
        using var folder = new PrivateFolder();
        using var lease = new QualifiedLeaseDirectory(folder.Path, "key");
        var marker = TestPathUtils.PathUnder(lease.Path, "marker.json");
        File.WriteAllBytes(marker, new byte[length]);
        File.SetUnixFileMode(marker, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var failure = Assert.Throws<QualifiedOwnershipFailure>(() => lease.Read("marker.json"));

        Assert.Equal("InvalidMarker", failure.Kind);
        Assert.Equal(length, new FileInfo(marker).Length);
    }

    [Fact]
    public void NativeLeaseWriteNewPreservesAnExistingMarker()
    {
        if (!Supported()) return;
        using var folder = new PrivateFolder();
        using var lease = new QualifiedLeaseDirectory(folder.Path, "key");
        var original = "{\"state\":\"original\"}"u8.ToArray();
        lease.WriteNew("active.json", original);

        var failure = Assert.Throws<QualifiedOwnershipFailure>(() =>
            lease.WriteNew("active.json", "{\"state\":\"replacement\"}"u8.ToArray()));

        Assert.Equal("QualifiedActiveOwner", failure.Kind);
        Assert.Equal(original, lease.Read("active.json"));
    }

    [Fact]
    public void NativeLeaseReportsMarkerPersistenceUncertainWhenTheFinalRenameFails()
    {
        if (!Supported()) return;
        using var folder = new PrivateFolder();
        using var lease = new QualifiedLeaseDirectory(folder.Path, "key");
        var marker = "{\"state\":\"durable\"}"u8.ToArray();

        var failure = Assert.Throws<QualifiedOwnershipFailure>(() =>
            lease.WriteNew("missing-parent/active.json", marker));

        Assert.Equal("MarkerPersistenceUncertain", failure.Kind);
        Assert.Empty(Directory.EnumerateDirectories(lease.Path));
        Assert.False(File.Exists(System.IO.Path.Combine(lease.Path, "missing-parent", "active.json")));
        var staged = Assert.Single(Directory.GetFiles(lease.Path, ".staged-*"));
        Assert.Equal(marker, File.ReadAllBytes(staged));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(staged));
    }

    [Fact]
    public void NativeLeaseRejectsAnUnsafeExistingLeaseDirectory()
    {
        if (!Supported()) return;
        using var folder = new PrivateFolder();
        var leasePath = TestPathUtils.PathUnder(folder.Path, "key");
        Directory.CreateDirectory(leasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(leasePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);

        var failure = Assert.Throws<QualifiedOwnershipFailure>(() =>
            new QualifiedLeaseDirectory(folder.Path, "key"));

        Assert.Equal("UnsafeLeasePath", failure.Kind);
        Assert.False(File.Exists(TestPathUtils.PathUnder(leasePath, "owner.lock")));
    }

    [Fact]
    public void NativeLeaseDoesNotFallBackWhenPrivateDirectoryCreationFails()
    {
        if (!Supported()) return;
        using var folder = new PrivateFolder();

        var failure = Assert.Throws<QualifiedOwnershipFailure>(() =>
            new QualifiedLeaseDirectory(folder.Path, "missing-parent/lease"));

        Assert.Equal("UnsafeLeasePath", failure.Kind);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Path));
        using var valid = new QualifiedLeaseDirectory(folder.Path, "lease");
        Assert.True(Directory.Exists(valid.Path));
    }

    [Fact]
    public void NativeLeaseDeletionOfAMissingRecordFailsClosed()
    {
        if (!Supported()) return;
        using var folder = new PrivateFolder();
        using var lease = new QualifiedLeaseDirectory(folder.Path, "key");

        var failure = Assert.Throws<QualifiedOwnershipFailure>(() => lease.Delete("missing.json"));

        Assert.Equal("ClearUncertain", failure.Kind);
        Assert.False(lease.Exists("missing.json"));
    }

    [Fact]
    public void NativeLeaseRejectsLinkedMarkerAndChangedDirectoryAnchor()
    {
        if (!Supported()) return;
        using var folder = new PrivateFolder();
        using var lease = new QualifiedLeaseDirectory(folder.Path, "key");
        var target = TestPathUtils.PathUnder(folder.Path, "target.json");
        File.WriteAllText(target, "{}");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.CreateSymbolicLink(TestPathUtils.PathUnder(lease.Path, "active.json"), target);
        Assert.Throws<QualifiedOwnershipFailure>(() => lease.Read("active.json"));
        File.Delete(TestPathUtils.PathUnder(lease.Path, "active.json"));
        Directory.Move(lease.Path, TestPathUtils.PathUnder(folder.Path, "lease-old"));
        Directory.CreateDirectory(lease.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.Throws<QualifiedOwnershipFailure>(() => lease.VerifyAnchor());
        Assert.Equal("{}", File.ReadAllText(target));
    }

    [Fact]
    public void NativeLeaseRejectsUnsafeRootAndMissingRecordsWithoutFollowingLinks()
    {
        if (!Supported()) return;
        using var folder = new PrivateFolder();
        var unsafeRoot = TestPathUtils.PathUnder(folder.Path, "unsafe-root");
        Directory.CreateDirectory(unsafeRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(unsafeRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
        Assert.Equal("UnsafeLeasePath", Assert.Throws<QualifiedOwnershipFailure>(
            () => new QualifiedLeaseDirectory(unsafeRoot, "key")).Kind);

        using var lease = new QualifiedLeaseDirectory(folder.Path, "key");
        Assert.False(lease.Exists("missing.json"));
        Assert.Equal("UnsafeRecord", Assert.Throws<QualifiedOwnershipFailure>(() => lease.Read("missing.json")).Kind);
        Assert.Equal("UnsafeRecord", Assert.Throws<QualifiedOwnershipFailure>(() => lease.Identity("missing.json")).Kind);
    }

    [Fact]
    public void NativeLeaseRejectsLinkedOwnerLockWithoutTakingTheTarget()
    {
        if (!Supported()) return;
        using var folder = new PrivateFolder();
        var target = TestPathUtils.PathUnder(folder.Path, "foreign-lock");
        File.WriteAllText(target, "foreign owner lock");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var leasePath = TestPathUtils.PathUnder(folder.Path, "key");
        Directory.CreateDirectory(leasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.CreateSymbolicLink(TestPathUtils.PathUnder(leasePath, "owner.lock"), target);

        var failure = Assert.Throws<QualifiedOwnershipFailure>(() => new QualifiedLeaseDirectory(folder.Path, "key"));

        Assert.Equal("UnsafeRecord", failure.Kind);
        Assert.Equal("foreign owner lock", File.ReadAllText(target));
    }

    [Fact]
    public void NativeLeaseConstructorFailsClosedOnUnsupportedPlatform()
    {
        if (Supported()) return;

        var folder = TestPathUtils.PathUnder(System.IO.Path.GetTempPath(), "issue888-unsupported-lease-" + Guid.NewGuid().ToString("N"));

        var failure = Assert.Throws<QualifiedOwnershipFailure>(() => new QualifiedLeaseDirectory(folder, "key"));

        Assert.Equal("UnsupportedPlatform", failure.Kind);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void StoppedPredicateMatchesProcessIncarnationRatherThanPidAlone()
    {
        using var current = Process.GetCurrentProcess();
        var start = current.StartTime.ToUniversalTime().Ticks;

        Assert.False(QualifiedProcessIdentity.Stopped(new QualifiedOwnedProcess("test", current.Id, start)));
        Assert.True(QualifiedProcessIdentity.Stopped(new QualifiedOwnedProcess("test", current.Id, start + 1)));
        Assert.True(QualifiedProcessIdentity.Stopped(new QualifiedOwnedProcess("missing", int.MaxValue, start)));
    }

    [System.Runtime.Versioning.SupportedOSPlatformGuard("macos")]
    private static bool Supported() => OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    private sealed class PrivateFolder : IDisposable
    {
        internal string Path { get; } = TestPathUtils.PathUnder("/private/tmp", "issue888-qualified-lease-" + Guid.NewGuid().ToString("N"));

        internal PrivateFolder()
        {
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
            Directory.CreateDirectory(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Dispose() => Directory.Delete(Path, true);
    }
}
