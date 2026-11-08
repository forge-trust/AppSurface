using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Auth.Keycloak;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

// testcard protects: snapshot acceptance leases and bounded publication under concurrent callers.
// fails_when: an accepted consumer is interrupted by handle disposal, or two writers commit one destination.
// why_new: lifecycle ordering and destination ownership require coordination rather than timing sleeps.
// seam: internal SnapshotStore/SnapshotHandle test seam exposed by the production assembly.
// no_claim: this does not prove filesystem behavior on every network or exotic filesystem.
public sealed class ConcurrencyContractTests
{
    [Fact]
    public async Task Accepted_consumer_completes_after_handle_disposal_and_lease_release_order_is_safe()
    {
        using var root = new TempDirectory();
        var source = TestPathUtils.PathUnder(root.Path, "source");
        await File.WriteAllTextAsync(source, "accepted", new UTF8Encoding(false));
        var digest = Digest(source);
        using var snapshot = await SnapshotStore.CaptureAsync(
            [new CaptureFile("input", source, digest, 8, 420)], new string('a', 64), new string('b', 64),
            TestPathUtils.PathUnder(root.Path, "snapshot"), CancellationToken.None);
        using var firstLease = snapshot.Enter();
        using var secondLease = snapshot.Enter();
        snapshot.Dispose();
        var firstDestination = TestPathUtils.PathUnder(root.Path, "accepted-copy-one");
        var secondDestination = TestPathUtils.PathUnder(root.Path, "accepted-copy-two");
        var firstCopy = SnapshotStore.CopyForAcceptedUseAsync(snapshot, firstLease, firstDestination, CancellationToken.None);
        var secondCopy = SnapshotStore.CopyForAcceptedUseAsync(snapshot, secondLease, secondDestination, CancellationToken.None);

        var copied = await Task.WhenAll(firstCopy, secondCopy);
        Assert.All(copied, path => Assert.Equal("accepted", File.ReadAllText(TestPathUtils.PathUnder(path, "input"))));
        Assert.Equal(2, snapshot.ActiveConsumers);
        secondLease.Dispose();
        Assert.Equal(1, snapshot.ActiveConsumers);
        firstLease.Dispose();
        Assert.Equal(0, snapshot.ActiveConsumers);
    }

    [Fact]
    public async Task Copy_rejects_a_lease_disposed_before_the_accepted_consumer_starts()
    {
        using var root = new TempDirectory();
        var source = TestPathUtils.PathUnder(root.Path, "source");
        await File.WriteAllTextAsync(source, "accepted", new UTF8Encoding(false));
        var digest = Digest(source);
        using var snapshot = await SnapshotStore.CaptureAsync(
            [new CaptureFile("input", source, digest, 8, 420)], new string('a', 64), new string('b', 64),
            TestPathUtils.PathUnder(root.Path, "snapshot"), CancellationToken.None);
        var lease = snapshot.Enter();
        lease.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => SnapshotStore.CopyForAcceptedUseAsync(
            snapshot, lease, TestPathUtils.PathUnder(root.Path, "rejected-copy"), CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_snapshot_publication_allows_one_absent_destination_owner()
    {
        using var root = new TempDirectory();
        var source = TestPathUtils.PathUnder(root.Path, "source");
        await File.WriteAllTextAsync(source, "bounded", new UTF8Encoding(false));
        var digest = Digest(source);
        var destination = TestPathUtils.PathUnder(root.Path, "shared");
        var firstReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<object> Publish(TaskCompletionSource<bool> ready) => Task.Run(async () =>
        {
            ready.SetResult(true);
            await gate.Task;
            return await Observe(SnapshotStore.CaptureAsync(
                [new CaptureFile("input", source, digest, 7, 420)], new string('a', 64), new string('b', 64), destination, CancellationToken.None));
        });
        var first = Publish(firstReady);
        var second = Publish(secondReady);
        await Task.WhenAll(firstReady.Task, secondReady.Task);
        gate.SetResult(true);
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(x => x is SnapshotHandle));
        Assert.Equal(1, results.Count(x => x is IOException));
        Assert.Equal("bounded", await File.ReadAllTextAsync(TestPathUtils.PathUnder(destination, "input")));
        (results.Single(x => x is SnapshotHandle) as SnapshotHandle)!.Dispose();
    }

    [Fact]
    public async Task Snapshot_capture_rejects_resource_exhaustion_before_staging_large_data()
    {
        using var root = new TempDirectory();
        var source = TestPathUtils.PathUnder(root.Path, "source");
        await File.WriteAllTextAsync(source, "bounded", new UTF8Encoding(false));
        var digest = Digest(source);

        await Assert.ThrowsAsync<InvalidDataException>(() => SnapshotStore.CaptureAsync(
            [new CaptureFile("input", source, digest, 7, 420)], new string('a', 64), new string('b', 64),
            TestPathUtils.PathUnder(root.Path, "exhausted"), CancellationToken.None,
            new InvocationLimits(MaxFileBytes: 6, MaxAggregateBytes: 6, MaxFileCount: 1)));
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(root.Path, "exhausted")));
    }

    private static async Task<object> Observe(Task<SnapshotHandle> task)
    {
        try { return await task; }
        catch (Exception error) { return error; }
    }

    private static string Digest(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class TempDirectory : IDisposable
    {
        internal string Path { get; } = TestPathUtils.PathUnder(
            OperatingSystem.IsMacOS() ? "/private/tmp" : System.IO.Path.GetTempPath(),
            "keycloak-concurrency-tests-" + Guid.NewGuid().ToString("N"));

        internal TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); }
            catch { }
        }
    }
}
