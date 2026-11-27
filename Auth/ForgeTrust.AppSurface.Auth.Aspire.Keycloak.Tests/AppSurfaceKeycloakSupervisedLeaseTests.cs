using System.Runtime.InteropServices;
using System.Text;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class AppSurfaceKeycloakSupervisedLeaseTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task AcquireRejectsInvalidDaemonMetadataBeforePlatformAccess(string daemonId)
    {
        var path = NonexistentRoot();

        var failure = await Assert.ThrowsAsync<QualifiedOwnershipFailure>(() =>
            SupervisedVolumeLease.Acquire(path, daemonId, "volume"));

        Assert.Equal("DaemonIdentityUnavailable", failure.Kind);
        Assert.False(Directory.Exists(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".volume")]
    [InlineData("_volume")]
    [InlineData("volume/name")]
    [InlineData("volumé")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task AcquireRejectsInvalidVolumeNamesBeforePlatformAccess(string volumeName)
    {
        var path = NonexistentRoot();

        var failure = await Assert.ThrowsAsync<QualifiedOwnershipFailure>(() =>
            SupervisedVolumeLease.Acquire(path, "daemon", volumeName));

        Assert.Equal("InvalidVolumeName", failure.Kind);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task AcquireRejectsOversizedDaemonMetadataBeforePlatformAccess()
    {
        var path = NonexistentRoot();
        var daemonId = new string('d', 1025);

        var failure = await Assert.ThrowsAsync<QualifiedOwnershipFailure>(() =>
            SupervisedVolumeLease.Acquire(path, daemonId, "volume"));

        Assert.Equal("DaemonIdentityUnavailable", failure.Kind);
        Assert.False(Directory.Exists(path));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData(null, false)]
    public void FileLockingDisabledSettingIsRecognizedWithoutPlatformAccess(string? value, bool expected)
    {
        Assert.Equal(expected, SupervisedVolumeLease.IsFileLockingDisabled(value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65_537)]
    public void MarkerValidationRejectsEmptyShortAndUnboundedCopies(int length)
    {
        var failure = Assert.Throws<QualifiedOwnershipFailure>(() =>
            SupervisedVolumeLease.ValidateMarkerBytes(new byte[length]));

        Assert.Equal("InvalidMarker", failure.Kind);
    }

    [Fact]
    public void MarkerValidationAcceptsTheMaximumBoundedCopy()
    {
        SupervisedVolumeLease.ValidateMarkerBytes(new byte[65_536]);
    }

    [Fact]
    public async Task NativeLeaseSerializesSameDaemonVolumeAndExposesExistingMarkerForRecovery()
    {
        if (!Supported()) return;
        using var folder = new PrivateFolder();
        var marker = "{\"OwnerId\":\"owner\",\"Daemon\":\"daemon\",\"VolumeStamp\":{\"RunRoot\":\"/tmp/run\"}}"u8.ToArray();
        var folderPath = folder.Path;

        using (var first = await SupervisedVolumeLease.Acquire(folder.Path, "daemon", "volume"))
        {
            Assert.False(first.HasActive);
            first.WriteNewActive(marker);
            Assert.True(first.HasActive);

            var contention = await Assert.ThrowsAsync<QualifiedOwnershipFailure>(() =>
                SupervisedVolumeLease.Acquire(folderPath, "daemon", "volume"));
            Assert.Equal("Contention", contention.Kind);
        }

        using var recovery = await SupervisedVolumeLease.Acquire(folder.Path, "daemon", "volume");
        Assert.True(recovery.HasActive);
        Assert.Equal(marker, recovery.ReadActive());
        Assert.Equal(TestPathUtils.PathUnder(folder.Path, MarkerKey("daemon", "volume")), recovery.Path);
    }

    [Fact]
    public async Task MarkerOperationsRequireUnchangedIdentityAndBytesAndRestoreOnlyWhenMissing()
    {
        if (!Supported()) return;
        using var folder = new PrivateFolder();
        var marker = "{\"Phase\":\"reserved\"}"u8.ToArray();
        var replacement = "{\"Phase\":\"running\"}"u8.ToArray();
        using var lease = await SupervisedVolumeLease.Acquire(folder.Path, "daemon", "volume");

        lease.WriteNewActive(marker);
        lease.ReplaceActive(replacement);
        Assert.Equal(replacement, lease.ReadActive());
        Assert.Throws<QualifiedOwnershipFailure>(() => lease.RestoreActive(marker));

        lease.ClearActive();
        Assert.False(lease.HasActive);
        lease.RestoreActive(marker);
        Assert.Equal(marker, lease.ReadActive());

        var activePath = TestPathUtils.PathUnder(lease.Path, "active.json");
        File.WriteAllBytes(activePath, replacement);
        Assert.Equal(replacement, File.ReadAllBytes(activePath));
        Assert.NotEqual(marker, File.ReadAllBytes(activePath));
        Assert.Throws<QualifiedOwnershipFailure>(() => lease.ReadActive());
        var changed = Assert.Throws<QualifiedOwnershipFailure>(() => lease.ReplaceActive(replacement));
        Assert.Equal("MarkerChanged", changed.Kind);
        Assert.Equal("MarkerChanged", Assert.Throws<QualifiedOwnershipFailure>(() => lease.ClearActive()).Kind);
        Assert.Equal(replacement, File.ReadAllBytes(activePath));
    }

    [Fact]
    public async Task MarkerOperationsRequireThisLeaseToObserveAnActiveRecordFirst()
    {
        if (!Supported()) return;
        using var folder = new PrivateFolder();
        using var lease = await SupervisedVolumeLease.Acquire(folder.Path, "daemon", "volume");
        var marker = """{"Phase":"reserved"}"""u8.ToArray();

        Assert.Equal("MarkerChanged", Assert.Throws<QualifiedOwnershipFailure>(() => lease.ReadActive()).Kind);
        Assert.Equal("MarkerChanged", Assert.Throws<QualifiedOwnershipFailure>(() => lease.ReplaceActive(marker)).Kind);
        Assert.Equal("MarkerChanged", Assert.Throws<QualifiedOwnershipFailure>(() => lease.ClearActive()).Kind);
        Assert.False(lease.HasActive);

        lease.WriteNewActive(marker);
        Assert.Equal(marker, lease.ReadActive());
    }

    [Fact]
    public async Task VerifiedMarkerClearRequiresExactBytesAndAnActiveStopBudget()
    {
        if (!Supported()) return;
        using var folder = new PrivateFolder();
        using var lease = await SupervisedVolumeLease.Acquire(folder.Path, "daemon", "volume");
        var marker = """{"Phase":"stopped"}"""u8.ToArray();
        var conflicting = """{"Phase":"replacement"}"""u8.ToArray();
        lease.WriteNewActive(marker);

        var mismatch = Assert.Throws<QualifiedOwnershipFailure>(() =>
            SupervisedKeycloakOwnership.ClearVerifiedMarker(lease, () => TimeSpan.Zero, default, conflicting));
        Assert.Equal("MarkerChanged", mismatch.Kind);
        Assert.Equal(marker, lease.ReadActive());

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            SupervisedKeycloakOwnership.ClearVerifiedMarker(lease, () => TimeSpan.Zero, cancelled.Token, marker));
        Assert.Equal(marker, lease.ReadActive());

        SupervisedKeycloakOwnership.ClearVerifiedMarker(lease, () => TimeSpan.Zero, default, marker);
        Assert.False(lease.HasActive);
    }

    [Fact]
    public async Task MarkerClearThatFinishesAtTheStopDeadlineRestoresTheExactOriginalBytes()
    {
        if (!Supported()) return;
        using var folder = new PrivateFolder();
        using var lease = await SupervisedVolumeLease.Acquire(folder.Path, "daemon", "volume");
        var marker = """{"Phase":"stopped","OwnerId":"original"}"""u8.ToArray();
        lease.WriteNewActive(marker);

        Assert.Throws<TimeoutException>(() => SupervisedKeycloakOwnership.ClearVerifiedMarker(lease, () =>
        {
            Assert.False(lease.HasActive);
            return TimeSpan.FromSeconds(30);
        }, default, marker));

        Assert.True(lease.HasActive);
        Assert.Equal(marker, lease.ReadActive());
    }

    [Fact]
    public async Task NativeLeaseRejectsLinkedOrWrongModeMarkersWithoutFollowingThem()
    {
        if (!Supported()) return;
        using var folder = new PrivateFolder();
        using var lease = await SupervisedVolumeLease.Acquire(folder.Path, "daemon", "volume");
        var activePath = TestPathUtils.PathUnder(lease.Path, "active.json");
        var target = TestPathUtils.PathUnder(folder.Path, "target.json");
        File.WriteAllText(target, "{}", Encoding.UTF8);
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        File.CreateSymbolicLink(activePath, target);
        Assert.Throws<QualifiedOwnershipFailure>(() => _ = lease.HasActive);
        File.Delete(activePath);

        lease.WriteNewActive("{}"u8.ToArray());
        File.SetUnixFileMode(activePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        Assert.Throws<QualifiedOwnershipFailure>(() => lease.ReadActive());
        Assert.Equal("{}", File.ReadAllText(target));
    }

    [Fact]
    public async Task FailedLeaseConstructionReleasesReservationForRetry()
    {
        if (!Supported()) return;
        using var folder = new PrivateFolder();
        var leaseRoot = folder.Path;
        var leasePath = TestPathUtils.PathUnder(leaseRoot, MarkerKey("daemon", "volume"));
        Directory.CreateDirectory(leasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var target = TestPathUtils.PathUnder(leaseRoot, "foreign-marker.json");
        File.WriteAllText(target, "foreign marker");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var active = TestPathUtils.PathUnder(leasePath, "active.json");
        File.CreateSymbolicLink(active, target);

        var failure = await Assert.ThrowsAsync<QualifiedOwnershipFailure>(() =>
            SupervisedVolumeLease.Acquire(leaseRoot, "daemon", "volume"));

        Assert.Equal("UnsafeMarker", failure.Kind);
        Assert.Equal("foreign marker", File.ReadAllText(target));
        File.Delete(active);

        using var recovered = await SupervisedVolumeLease.Acquire(leaseRoot, "daemon", "volume");
        Assert.False(recovered.HasActive);
    }

    [Fact]
    public async Task FailedConstructionOnOversizedNativeMarkerReleasesReservationForRetry()
    {
        if (!Supported()) return;
        using var folder = new PrivateFolder();
        var root = folder.Path;
        var leasePath = TestPathUtils.PathUnder(root, MarkerKey("daemon", "volume"));
        Directory.CreateDirectory(leasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var marker = TestPathUtils.PathUnder(leasePath, "active.json");
        File.WriteAllBytes(marker, new byte[65_537]);
        File.SetUnixFileMode(marker, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var failure = await Assert.ThrowsAsync<QualifiedOwnershipFailure>(() =>
            SupervisedVolumeLease.Acquire(root, "daemon", "volume"));

        Assert.Equal("InvalidMarker", failure.Kind);
        Assert.Equal(65_537, new FileInfo(marker).Length);
        File.Delete(marker);

        using var recovered = await SupervisedVolumeLease.Acquire(root, "daemon", "volume");
        Assert.False(recovered.HasActive);
    }

    [Fact]
    public async Task NativeLeaseRejectsIdenticalBytesAtADifferentMarkerIncarnationBeforeClear()
    {
        if (!Supported()) return;
        using var folder = new PrivateFolder();
        using var lease = await SupervisedVolumeLease.Acquire(folder.Path, "daemon", "volume");
        var marker = "{\"Phase\":\"active\"}"u8.ToArray();
        lease.WriteNewActive(marker);
        var activePath = TestPathUtils.PathUnder(lease.Path, "active.json");
        var replacementPath = TestPathUtils.PathUnder(lease.Path, "replacement.json");
        File.WriteAllBytes(replacementPath, marker);
        File.SetUnixFileMode(replacementPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        File.Move(replacementPath, activePath, overwrite: true);

        Assert.Equal("MarkerChanged", Assert.Throws<QualifiedOwnershipFailure>(() => lease.ClearActive()).Kind);
        Assert.Equal(marker, File.ReadAllBytes(activePath));
    }

    [Fact]
    public async Task DisposeLeavesTheDurableMarkerAndReleasesLeaseForRecovery()
    {
        if (!Supported()) return;
        using var folder = new PrivateFolder();
        var marker = "{\"Phase\":\"active\"}"u8.ToArray();
        var lease = await SupervisedVolumeLease.Acquire(folder.Path, "daemon", "volume");
        lease.WriteNewActive(marker);
        var path = TestPathUtils.PathUnder(lease.Path, "active.json");

        lease.Dispose();
        Assert.True(File.Exists(path));
        Assert.Equal(marker, File.ReadAllBytes(path));

        using var recovery = await SupervisedVolumeLease.Acquire(folder.Path, "daemon", "volume");
        Assert.Equal(marker, recovery.ReadActive());
    }

    [Fact]
    public async Task RecoveryNeverClobbersMarkerAndDisposedLeaseRejectsFurtherOperations()
    {
        if (!Supported()) return;
        using var folder = new PrivateFolder();
        var marker = "{\"Phase\":\"recovered\"}"u8.ToArray();
        var competing = "{\"Phase\":\"newer\"}"u8.ToArray();
        var lease = await SupervisedVolumeLease.Acquire(folder.Path, "daemon", "volume");
        lease.RestoreActive(marker);

        Assert.Equal("QualifiedActiveOwner", Assert.Throws<QualifiedOwnershipFailure>(
            () => lease.RestoreActive(competing)).Kind);
        Assert.Equal(marker, lease.ReadActive());

        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = lease.HasActive);
        Assert.Throws<ObjectDisposedException>(() => lease.ReadActive());
        Assert.Throws<ObjectDisposedException>(() => lease.ClearActive());
        Assert.Equal(marker, File.ReadAllBytes(TestPathUtils.PathUnder(lease.Path, "active.json")));
    }

    [System.Runtime.Versioning.SupportedOSPlatformGuard("macos")]
    private static bool Supported() => OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    private static string MarkerKey(string daemonId, string volumeName) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(daemonId + "\n" + volumeName)))
            .ToLowerInvariant();

    private static string NonexistentRoot() => TestPathUtils.PathUnder("/private/tmp", "issue888-invalid-" + Guid.NewGuid().ToString("N"));

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private sealed class PrivateFolder : IDisposable
    {
        internal string Path { get; } = TestPathUtils.PathUnder("/private/tmp", "issue888-supervised-" + Guid.NewGuid().ToString("N"));

        internal PrivateFolder()
        {
            Directory.CreateDirectory(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
