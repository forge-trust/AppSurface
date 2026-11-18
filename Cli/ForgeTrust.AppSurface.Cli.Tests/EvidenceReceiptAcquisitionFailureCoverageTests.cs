using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceReceiptAcquisitionFailureCoverageTests
{
    [Fact]
    public async Task CancellationWhileWaitingForSecondKeyReleasesFirstSpeculativeReservation()
    {
        var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipt-store");
        var repositoryNamespace = Hash("partial-acquisition-repository");
        var firstKey = new string('a', 64);
        var secondKey = new string('b', 64);
        var options = new EvidenceReceiptStoreOptions
        {
            RootDirectory = root,
            RepositoryNamespace = repositoryNamespace,
            WaitTimeout = TimeSpan.FromSeconds(20),
        };
        EvidenceReceiptStore.EvidenceReceiptKeyLease? heldSecondKey = null;
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var reachedSecondKey = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<EvidenceReceiptStore.EvidenceReceiptKeyLease>? returnedLeases = null;
        Task<IReadOnlyList<EvidenceReceiptStore.EvidenceReceiptKeyLease>>? acquisition = null;

        try
        {
            var holderStore = EvidenceReceiptStore.Open(options);
            var waitingStore = EvidenceReceiptStore.Open(options);
            var firstKeyStore = EvidenceReceiptStore.Open(options with { WaitTimeout = TimeSpan.FromSeconds(3) });
            heldSecondKey = Assert.Single(await holderStore.AcquireAsync([secondKey], cancellation.Token));
            heldSecondKey.EstablishAuthority("unrelated-second-key-owner");
            var pendingAcquisition = waitingStore.AcquireAsync([secondKey, firstKey], cancellation.Token,
                (prefix, _, _) =>
                {
                    if (prefix == secondKey[..12]) reachedSecondKey.TrySetResult();
                });
            acquisition = pendingAcquisition;

            await reachedSecondKey.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(pendingAcquisition.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingAcquisition.WaitAsync(TimeSpan.FromSeconds(5)));

            var index = ReadQuotaEntries(root);
            Assert.DoesNotContain(index, entry => entry.GetProperty("Key").GetString() == firstKey);
            Assert.Equal("unrelated-second-key-owner",
                EvidenceReceiptQuota.ReadAuthority(root, repositoryNamespace, secondKey)?.InFlightExecutionId);

            returnedLeases = await firstKeyStore.AcquireAsync([firstKey], CancellationToken.None);
            Assert.Equal(firstKey, Assert.Single(returnedLeases).Key);
            foreach (var lease in returnedLeases.Reverse()) lease.Dispose();
            returnedLeases = null;
            Assert.Equal("unrelated-second-key-owner",
                EvidenceReceiptQuota.ReadAuthority(root, repositoryNamespace, secondKey)?.InFlightExecutionId);
        }
        finally
        {
            // The cleanup task retains every owner if a regression exceeds the test's bounded wait.
            // Only actual acquisition settlement permits removal of the private fixture directory.
            var cleanup = Task.Run(async () =>
            {
                var failures = new List<Exception>();
                Attempt(cancellation.Cancel);
                Attempt(() => heldSecondKey?.Dispose());
                if (acquisition is not null)
                {
                    try
                    {
                        var acquired = await acquisition;
                        foreach (var lease in acquired.Reverse()) Attempt(lease.Dispose);
                    }
                    catch (OperationCanceledException) when (acquisition.IsCompleted) { }
                    catch (Exception exception) { failures.Add(exception); }
                }
                if (returnedLeases is not null)
                    foreach (var lease in returnedLeases.Reverse()) Attempt(lease.Dispose);
                Attempt(cancellation.Dispose);
                if (failures.Count != 0)
                {
                    throw new AggregateException($"Cleanup failed; the private fixture is retained at {root}.", failures);
                }
                temporary.Dispose();

                void Attempt(Action action)
                {
                    try { action(); }
                    catch (Exception exception) { failures.Add(exception); }
                }
            });
            _ = cleanup.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try { await cleanup.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException exception) when (!cleanup.IsCompleted)
            {
                throw new TimeoutException($"Actual acquisition cleanup still owns the retained fixture at {root}.", exception);
            }
        }
    }

    [Fact]
    public async Task CorruptQuotaDuringKeyWaitFailsConfirmationAndReleasesNativeKeyLease()
    {
        var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipt-store");
        var repositoryNamespace = Hash("confirm-failure-repository");
        var key = Hash("confirm-failure-key");
        var keyPath = TestPathUtils.PathUnder(root, repositoryNamespace, key);
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        {
            RootDirectory = root,
            RepositoryNamespace = repositoryNamespace,
            WaitTimeout = TimeSpan.FromSeconds(15),
        });
        IReadOnlyList<EvidenceReceiptStore.EvidenceReceiptKeyLease>? setupLeases = null;
        CoverageArtifactOperationLease? nativeHolder = null;
        Task<IReadOnlyList<EvidenceReceiptStore.EvidenceReceiptKeyLease>>? acquisition = null;
        byte[]? reservationIndexBytes = null;
        byte[]? corruptIndexBytes = null;
        var indexPath = TestPathUtils.PathUnder(root, "quota-index.json");
        var outsideSentinel = TestPathUtils.PathUnder(temporary.Path, "outside-sentinel.txt");
        await File.WriteAllTextAsync(outsideSentinel, "preserve outside fixture");
        var sentinelBytes = await File.ReadAllBytesAsync(outsideSentinel);

        try
        {
            setupLeases = await store.AcquireAsync([key], CancellationToken.None);
            foreach (var lease in setupLeases.Reverse()) lease.Dispose();
            setupLeases = null;

            nativeHolder = CoverageArtifactOperationLease.Acquire(keyPath, exclusive: true);
            var corruptedDuringWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            acquisition = store.AcquireAsync([key], CancellationToken.None, (_, _, _) =>
            {
                reservationIndexBytes = File.ReadAllBytes(indexPath);
                corruptIndexBytes = [(byte)'{'];
                WritePrivateIndex(root, corruptIndexBytes);
                nativeHolder!.Dispose();
                nativeHolder = null;
                corruptedDuringWait.TrySetResult();
            });

            await corruptedDuringWait.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<JsonException>(() => acquisition!.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(corruptIndexBytes, await File.ReadAllBytesAsync(indexPath));
            Assert.Equal(sentinelBytes, await File.ReadAllBytesAsync(outsideSentinel));

            // The failed post-lock confirmation must unwind the native key lease even though
            // quota cleanup cannot safely parse the index.
            using (CoverageArtifactOperationLease.Acquire(keyPath, exclusive: true)) { }
        }
        finally
        {
            nativeHolder?.Dispose();
            if (acquisition is not null)
            {
                try
                {
                    var acquired = await acquisition.WaitAsync(TimeSpan.FromSeconds(20));
                    foreach (var lease in acquired.Reverse()) lease.Dispose();
                }
                catch (TimeoutException) when (!acquisition.IsCompleted)
                {
                    throw new TimeoutException($"Actual acquisition still owns the retained fixture at {root}.");
                }
                catch (Exception) when (acquisition.IsCompleted) { }
            }
            if (reservationIndexBytes is not null)
            {
                WritePrivateIndex(root, reservationIndexBytes);
                using var document = JsonDocument.Parse(reservationIndexBytes);
                var entry = Assert.Single(document.RootElement.GetProperty("Entries").EnumerateArray());
                Assert.Equal(EvidenceReceiptQuota.EssentialReservationBytes,
                    entry.GetProperty("EssentialReservationBytes").GetInt64());
                Assert.Equal(1, entry.GetProperty("EssentialReservationIds").GetArrayLength());
                Assert.Equal(sentinelBytes, await File.ReadAllBytesAsync(outsideSentinel));
            }
            if (setupLeases is not null)
                foreach (var lease in setupLeases.Reverse()) lease.Dispose();
            temporary.Dispose();
        }
    }

    private static JsonElement[] ReadQuotaEntries(string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(TestPathUtils.PathUnder(root, "quota-index.json")));
        return document.RootElement.GetProperty("Entries").EnumerateArray()
            .Select(static entry => entry.Clone()).ToArray();
    }

    private static void WritePrivateIndex(string root, byte[] bytes)
    {
        using var rootLease = CoverageRunOutputLease.AcquireArtifactDirectory(root, createMissing: false, requirePrivate: true)!;
        CoverageArtifactMetadataPublication.Write(rootLease, "quota-index.json", bytes,
            EvidenceReceiptQuota.IndexMaximumBytes, durableDirectoryEntry: true);
    }

    private static string Hash(string value)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
