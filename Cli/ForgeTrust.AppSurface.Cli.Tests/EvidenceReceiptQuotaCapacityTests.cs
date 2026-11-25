using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceReceiptQuotaCapacityTests
{
    [Fact]
    public async Task MaintenanceReleasesMissingBoundStageBytesWithoutClearingUnknownExecutionAuthority()
    {
        var root = NewRoot();
        try
        {
            var repositoryNamespace = Hash("quota-orphan-repository");
            var key = Hash("quota-orphan-producer");
            var store = OpenStore(root, repositoryNamespace);
            var now = DateTimeOffset.UtcNow;
            var reservationId = Guid.NewGuid().ToString("N");
            var stageName = "payload-stage-" + reservationId;

            using (var owner = Assert.Single(await store.AcquireAsync([key], CancellationToken.None)))
            {
                owner.EstablishAuthority("crashed-execution-917");
                Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repositoryNamespace, key, now,
                    EvidenceReceiptPayloadReservation.Create(reservationId, 1_500_000_000)));
                Assert.False(Directory.Exists(Path.Combine(owner.DirectoryPath, stageName)));
                EvidenceReceiptQuota.Maintain(root, now);
                Assert.Equal(1_500_000_000, ReadEntry(root, key).GetProperty("ReservedPayloadBytes").GetInt64());
            }

            EvidenceReceiptQuota.Maintain(root, now);

            var repaired = ReadEntry(root, key);
            Assert.Equal(0, repaired.GetProperty("ReservedPayloadBytes").GetInt64());
            Assert.Empty(repaired.GetProperty("PayloadReservations").EnumerateArray());
            Assert.True(repaired.GetProperty("HasInFlight").GetBoolean());
            Assert.Equal("crashed-execution-917",
                repaired.GetProperty("AuthorityState").GetProperty("InFlightExecutionId").GetString());

            using var contender = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
            Assert.Throws<InvalidOperationException>(() => contender.EstablishAuthority("new-execution-must-not-start"));
        }
        finally { DeleteIfPresent(root); }
    }

    [Fact]
    public async Task MaintenanceReducesBoundReservationToRecognizedStageBytesAndPreservesTheStage()
    {
        var root = NewRoot();
        try
        {
            var repositoryNamespace = Hash("quota-partial-repository");
            var key = Hash("quota-partial-producer");
            var store = OpenStore(root, repositoryNamespace);
            var now = DateTimeOffset.UtcNow;
            var reservationId = Guid.NewGuid().ToString("N");
            var stageName = "payload-stage-" + reservationId;
            var stagedBytes = new byte[137];

            using (var owner = Assert.Single(await store.AcquireAsync([key], CancellationToken.None)))
            {
                owner.EstablishAuthority("partial-stage-execution");
                Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repositoryNamespace, key, now,
                    EvidenceReceiptPayloadReservation.Create(reservationId, 8_000_000)));
                using (var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(owner.DirectoryPath, false, true)!)
                using (var stage = keyRoot.CreateArtifactDirectory(stageName))
                using (var file = stage.OpenArtifactFile("partial.bin", createNew: true))
                {
                    file.Write(stagedBytes);
                    file.Flush(flushToDisk: true);
                }
                Assert.Equal(8_000_000 - stagedBytes.Length,
                    EvidenceReceiptQuota.ReconcilePayloadReservations(owner));
            }

            EvidenceReceiptQuota.Maintain(root, now);

            var repaired = ReadEntry(root, key);
            Assert.Equal(stagedBytes.Length, repaired.GetProperty("ReservedPayloadBytes").GetInt64());
            var reservation = Assert.Single(repaired.GetProperty("PayloadReservations").EnumerateArray());
            Assert.Equal(stagedBytes.Length, reservation.GetProperty("ReservedBytes").GetInt64());
            Assert.True(Directory.Exists(Path.Combine(root, repositoryNamespace, key, stageName)));
            Assert.Equal("partial-stage-execution",
                repaired.GetProperty("AuthorityState").GetProperty("InFlightExecutionId").GetString());
        }
        finally { DeleteIfPresent(root); }
    }

    [Fact]
    public async Task MaintenancePreservesLegacyUnboundReservationsAndForeignStageEntries()
    {
        var root = NewRoot();
        try
        {
            var repositoryNamespace = Hash("quota-foreign-repository");
            var store = OpenStore(root, repositoryNamespace);
            var now = DateTimeOffset.UtcNow;
            var legacyKey = Hash("quota-legacy-reservation");
            var foreignKey = Hash("quota-foreign-stage");
            var reservationId = Guid.NewGuid().ToString("N");
            var stageName = "payload-stage-" + reservationId;

            using (Assert.Single(await store.AcquireAsync([legacyKey], CancellationToken.None)))
                await LegacyAggregateReservationFixture.SeedAsync(root, repositoryNamespace, legacyKey, 900_000_000);

            using (var foreign = Assert.Single(await store.AcquireAsync([foreignKey], CancellationToken.None)))
            {
                Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repositoryNamespace, foreignKey, now,
                    EvidenceReceiptPayloadReservation.Create(reservationId, 700_000_000)));
                using var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(foreign.DirectoryPath, false, true)!;
                using var file = keyRoot.OpenArtifactFile(stageName, createNew: true);
                file.Write(Encoding.UTF8.GetBytes("foreign entry"));
            }

            EvidenceReceiptQuota.Maintain(root, now);

            Assert.Equal(900_000_000, ReadEntry(root, legacyKey).GetProperty("ReservedPayloadBytes").GetInt64());
            Assert.Equal(700_000_000, ReadEntry(root, foreignKey).GetProperty("ReservedPayloadBytes").GetInt64());
            Assert.Equal("foreign entry", await File.ReadAllTextAsync(Path.Combine(root, repositoryNamespace, foreignKey, stageName)));
        }
        finally { DeleteIfPresent(root); }
    }

    [Fact]
    public async Task SerializedIndexPressureEvictsSettledLruReceiptsButPreservesBusyUnknownAndQuarantinedKeys()
    {
        var root = NewRoot();
        try
        {
            var repositoryNamespace = Hash("quota-pressure-repository");
            var store = OpenStore(root, repositoryNamespace);
            var now = DateTimeOffset.UtcNow;
            var settledProducerRecord = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new string('x', 32 * 1024)));
            var triggerProducerRecord = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new string('x', 47 * 1024)));
            var firstSettledKey = Hash("quota-pressure-settled-0");
            var unknownKey = Hash("quota-pressure-unknown");
            var quarantinedKey = Hash("quota-pressure-quarantined");
            var busyKey = Hash("quota-pressure-busy");

            await AddReceiptStateAsync(store, unknownKey, "payload-unknown", now,
                inFlightExecutionId: "unknown-execution");
            await AddReceiptStateAsync(store, quarantinedKey, "payload-quarantined", now,
                auditIntentExecutionId: "audit-execution", auditMismatch: true);
            await AddReceiptStateAsync(store, busyKey, "payload-busy", now,
                lastSuccessfulUse: now.AddDays(-40));
            using var busyLease = Assert.Single(await store.AcquireAsync([busyKey], CancellationToken.None));

            await AddReceiptStateAsync(store, firstSettledKey, "payload-settled-0000", now,
                settledProducerRecord, lastSuccessfulUse: now.AddDays(-30));
            var triggerKey = Hash("quota-pressure-trigger");
            const string triggerPayload = "payload-trigger";
            using var triggerLease = Assert.Single(await store.AcquireAsync([triggerKey], CancellationToken.None));
            using (var triggerRoot = CoverageRunOutputLease.AcquireArtifactDirectory(triggerLease.DirectoryPath, false, true)!)
            using (triggerRoot.CreateArtifactDirectory(triggerPayload)) { }

            var triggerState = CreateReceiptState(triggerKey, triggerPayload, now, triggerProducerRecord);
            var triggerStateSize = JsonSerializer.SerializeToUtf8Bytes(triggerState).Length;
            Assert.InRange(triggerStateSize, 1, EvidenceReceiptState.MaximumSerializedBytes);
            var indexPath = Path.Combine(root, "quota-index.json");
            // Measure the exact growth of the trigger update, including its metadata charge.
            EvidenceReceiptQuota.RecordState(triggerLease, triggerState);
            var expandedIndexSize = new FileInfo(indexPath).Length;
            var initialTriggerState = triggerState with { PassedProducerRecord = null };
            EvidenceReceiptQuota.RecordState(triggerLease, initialTriggerState);
            var baseline = await File.ReadAllBytesAsync(indexPath);
            var triggerGrowth = expandedIndexSize - baseline.Length;
            Assert.True(triggerGrowth > 0);

            var index = JsonSerializer.Deserialize<EvidenceReceiptQuotaIndex>(baseline)!;
            var settledTemplate = Assert.Single(index.Entries, entry => entry.Key == firstSettledKey);
            var seedLength = baseline.Length;
            using (var namespaceRoot = CoverageRunOutputLease.AcquireArtifactDirectory(
                       Path.Combine(root, repositoryNamespace), false, true)!)
            {
                for (var settledIndex = 1; seedLength + triggerGrowth <= EvidenceReceiptQuota.IndexMaximumBytes; settledIndex++)
                {
                    Assert.InRange(settledIndex, 1, 1000);
                    var key = Hash("quota-pressure-settled-" + settledIndex);
                    var payloadName = "payload-settled-" + settledIndex.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
                    var usedAt = now.AddDays(-30).AddSeconds(settledIndex);
                    var row = settledTemplate with
                    {
                        Key = key,
                        PayloadDirectoryName = payloadName,
                        LastSuccessfulUse = usedAt,
                        AuthorityState = CreateReceiptState(key, payloadName, now, settledProducerRecord, lastSuccessfulUse: usedAt)
                    };
                    using var keyRoot = namespaceRoot.CreateArtifactDirectory(key);
                    using (keyRoot.CreateArtifactDirectory(payloadName)) { }
                    index.Entries.Add(row);
                    seedLength += JsonSerializer.SerializeToUtf8Bytes(row).Length + 1; // Comma between entries.
                }
            }

            var seed = JsonSerializer.SerializeToUtf8Bytes(index);
            Assert.Equal(seedLength, seed.Length);
            Assert.True(seed.Length <= EvidenceReceiptQuota.IndexMaximumBytes);
            Assert.True(seed.Length + triggerGrowth > EvidenceReceiptQuota.IndexMaximumBytes);
            await File.WriteAllBytesAsync(indexPath, seed);

            Assert.True(Directory.Exists(Path.Combine(root, repositoryNamespace, firstSettledKey, "payload-settled-0000")));
            EvidenceReceiptQuota.RecordState(triggerLease, triggerState);

            var indexBytes = await File.ReadAllBytesAsync(indexPath);
            Assert.True(indexBytes.Length <= EvidenceReceiptQuota.IndexMaximumBytes);
            using (var publishedIndex = JsonDocument.Parse(indexBytes))
            {
                var entryKeys = publishedIndex.RootElement.GetProperty("Entries").EnumerateArray()
                    .Select(static entry => entry.GetProperty("Key").GetString()).ToHashSet(StringComparer.Ordinal);
                Assert.Contains(triggerKey, entryKeys);
                Assert.Contains(unknownKey, entryKeys);
                Assert.Contains(quarantinedKey, entryKeys);
                Assert.Contains(busyKey, entryKeys);
                Assert.DoesNotContain(firstSettledKey, entryKeys);
                Assert.Contains(Hash("quota-pressure-settled-1"), entryKeys);
            }

            Assert.True(Directory.Exists(Path.Combine(root, repositoryNamespace, unknownKey, "payload-unknown")));
            Assert.True(Directory.Exists(Path.Combine(root, repositoryNamespace, quarantinedKey, "payload-quarantined")));
            Assert.True(Directory.Exists(Path.Combine(root, repositoryNamespace, busyKey, "payload-busy")));
            Assert.False(Directory.Exists(Path.Combine(root, repositoryNamespace, firstSettledKey, "payload-settled-0000")));
            Assert.Equal("unknown-execution", ReadEntry(root, unknownKey)
                .GetProperty("AuthorityState").GetProperty("InFlightExecutionId").GetString());
            Assert.True(ReadEntry(root, quarantinedKey).GetProperty("Quarantined").GetBoolean());
        }
        finally { DeleteIfPresent(root); }
    }

    [Fact]
    public async Task IndexPressureClearsOrphanedHoldBeforeChoosingWhetherToEvictItsReceipt()
    {
        var root = NewRoot();
        try
        {
            var repository = Hash("quota-orphaned-hold-repository");
            var store = OpenStore(root, repository);
            var now = DateTimeOffset.UtcNow;
            var settledKey = Hash("quota-orphaned-hold-settled");
            const string payloadName = "payload-settled";
            await AddReceiptStateAsync(store, settledKey, payloadName, now);

            using var trigger = Assert.Single(await store.AcquireAsync([Hash("quota-orphaned-hold-trigger")], CancellationToken.None));
            trigger.EstablishAuthority("quota-index-publication");
            var triggerState = Assert.IsType<EvidenceReceiptState>(trigger.ReadState());
            EvidenceReceiptQuota.ReserveEssential(root, repository, [settledKey], now);

            var indexPath = Path.Combine(root, "quota-index.json");
            var index = JsonSerializer.Deserialize<EvidenceReceiptQuotaIndex>(await File.ReadAllBytesAsync(indexPath))!;
            var settled = Assert.Single(index.Entries, entry => entry.Key == settledKey);
            var triggerEntry = Assert.Single(index.Entries, entry => entry.Key == trigger.Key);
            Assert.Single(settled.EssentialReservationIds!);
            Assert.True(settled.MetadataBytes > 0);

            // Model peak byte pressure in the index while leaving the owned test payload small.
            var payloadCharge = EvidenceReceiptQuota.MaximumBytes - triggerEntry.MetadataBytes - settled.MetadataBytes / 2;
            index.Entries[index.Entries.IndexOf(settled)] = settled with { PayloadBytes = payloadCharge };
            var indexBytes = JsonSerializer.SerializeToUtf8Bytes(index);
            var bytesWithoutOrphanedHold = payloadCharge + triggerEntry.MetadataBytes;
            Assert.False(EvidenceReceiptQuota.IndexPublicationFits(
                bytesWithoutOrphanedHold + settled.MetadataBytes, indexBytes.Length, indexBytes.Length));
            Assert.True(EvidenceReceiptQuota.IndexPublicationFits(
                bytesWithoutOrphanedHold, indexBytes.Length, indexBytes.Length));
            await File.WriteAllBytesAsync(indexPath, indexBytes);

            EvidenceReceiptQuota.RecordState(trigger, triggerState);

            Assert.True(Directory.Exists(Path.Join(root, repository, settledKey, payloadName)),
                "Clearing the orphaned hold makes the replacement index fit without evicting the settled payload.");
            var retained = ReadEntry(root, settledKey);
            Assert.Empty(retained.GetProperty("EssentialReservationIds").EnumerateArray());
            Assert.False(retained.GetProperty("EssentialReserved").GetBoolean());
            Assert.Equal(0, retained.GetProperty("MetadataBytes").GetInt64());
            Assert.Equal(payloadCharge, retained.GetProperty("PayloadBytes").GetInt64());
            Assert.Equal(settled.AuthorityState, EvidenceReceiptQuota.ReadAuthority(root, repository, settledKey));
        }
        finally { DeleteIfPresent(root); }
    }

    [Fact]
    public void IndexPublicationCapacityIncludesTheTwoGigabytePeakCopies()
    {
        Assert.True(EvidenceReceiptQuota.IndexPublicationFits(
            EvidenceReceiptQuota.MaximumBytes - 256, 128, 128));
        Assert.False(EvidenceReceiptQuota.IndexPublicationFits(
            EvidenceReceiptQuota.MaximumBytes - 255, 128, 128));
        Assert.False(EvidenceReceiptQuota.IndexPublicationFits(
            EvidenceReceiptQuota.MaximumBytes - 256, 129, 128));
    }

    [Fact]
    public void EssentialReservationDoesNotPublishAuthorityWhenKeyDirectoryCannotBeCreated()
    {
        var root = NewRoot();
        try
        {
            var repositoryNamespace = Hash("quota-blocked-directory-repository");
            var key = Hash("quota-blocked-directory-producer");
            using (var rootLease = CoverageRunOutputLease.AcquireArtifactDirectory(root, true, true)!)
            using (var namespaceLease = rootLease.CreateArtifactDirectory(repositoryNamespace))
                File.WriteAllText(Path.Combine(namespaceLease.ArtifactDirectoryPath, key), "blocks-key-directory");

            Assert.ThrowsAny<IOException>(() => EvidenceReceiptQuota.ReserveEssential(root,
                repositoryNamespace, [key], DateTimeOffset.UtcNow));

            Assert.False(File.Exists(Path.Combine(root, "quota-index.json")));
        }
        finally { DeleteIfPresent(root); }
    }

    [Fact]
    public async Task BytePressureStopsSelectingVictimsAfterOneDurableTombstoneCanSatisfyTheRequest()
    {
        var root = NewRoot();
        try
        {
            var repository = Hash("byte-pressure-repository");
            var store = OpenStore(root, repository);
            var now = DateTimeOffset.UtcNow;
            var victims = Enumerable.Range(0, 3).Select(index => Hash("byte-pressure-" + index)).ToArray();
            for (var index = 0; index < victims.Length; index++)
                await AddReceiptStateAsync(store, victims[index], "payload-pressure-" + index,
                    now.AddMinutes(-3 + index));
            using var requester = Assert.Single(await store.AcquireAsync([Hash("requester")], CancellationToken.None));
            requester.EstablishAuthority("byte-pressure-request");
            var indexPath = Path.Combine(root, "quota-index.json");
            var indexDocument = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!;
            foreach (var row in indexDocument["Entries"]!.AsArray())
                if (victims.Contains(row!["Key"]!.GetValue<string>())) row["PayloadBytes"] = 700_000_000;
            await File.WriteAllTextAsync(indexPath, indexDocument.ToJsonString());

            var reservationId = Guid.NewGuid().ToString("N");
            Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, requester.Key, now,
                EvidenceReceiptPayloadReservation.Create(reservationId, 100_000_000)));

            Assert.False(Directory.Exists(Path.Combine(root, repository, victims[0], "payload-pressure-0")));
            Assert.True(Directory.Exists(Path.Combine(root, repository, victims[1], "payload-pressure-1")));
            Assert.True(Directory.Exists(Path.Combine(root, repository, victims[2], "payload-pressure-2")));
            Assert.Equal(100_000_000, ReadEntry(root, requester.Key).GetProperty("ReservedPayloadBytes").GetInt64());
        }
        finally { DeleteIfPresent(root); }
    }

    [Fact]
    public async Task EssentialReservationHonorsRootIndexWaitBudgetAndCancellation()
    {
        var root = NewRoot();
        try
        {
            var repositoryNamespace = Hash("quota-index-wait-repository");
            var key = Hash("quota-index-wait-producer");
            var store = OpenStore(root, repositoryNamespace);
            using (Assert.Single(await store.AcquireAsync([key], CancellationToken.None))) { }
            var reservationId = EvidenceReceiptQuota.ReserveEssential(root, repositoryNamespace, [key], DateTimeOffset.UtcNow);
            using var keyLease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));

            var rootLease = CoverageArtifactOperationLease.Acquire(root, exclusive: true);
            using var cancellation = new CancellationTokenSource();
            using var confirmCancellation = new CancellationTokenSource();
            var ownedTasks = new List<Task>();
            try
            {
                var budgetOperation = Task.Run(() => EvidenceReceiptQuota.ReserveEssential(root, repositoryNamespace,
                    [key], DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(80)));
                ownedTasks.Add(budgetOperation);
                var unavailable = await Assert.ThrowsAsync<EvidenceReceiptQuotaUnavailableException>(() => budgetOperation);
                Assert.Equal("receipt-index-busy", unavailable.Message);

                var cancellationOperation = Task.Run(() => EvidenceReceiptQuota.ReserveEssential(root, repositoryNamespace,
                    [key], DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), cancellation.Token));
                ownedTasks.Add(cancellationOperation);
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(250));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellationOperation);

                var confirmBudgetOperation = Task.Run(() => EvidenceReceiptQuota.ConfirmEssentialReservation(root,
                    repositoryNamespace, key, reservationId, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(80)));
                ownedTasks.Add(confirmBudgetOperation);
                var confirmUnavailable = await Assert.ThrowsAsync<EvidenceReceiptQuotaUnavailableException>(() => confirmBudgetOperation);
                Assert.Equal("receipt-index-busy", confirmUnavailable.Message);

                var confirmCancellationOperation = Task.Run(() => EvidenceReceiptQuota.ConfirmEssentialReservation(root,
                    repositoryNamespace, key, reservationId, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5),
                    confirmCancellation.Token));
                ownedTasks.Add(confirmCancellationOperation);
                confirmCancellation.CancelAfter(TimeSpan.FromMilliseconds(250));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => confirmCancellationOperation);
            }
            finally
            {
                cancellation.Cancel();
                confirmCancellation.Cancel();
                rootLease.Dispose();
                try { await Task.WhenAll(ownedTasks); }
                catch (EvidenceReceiptQuotaUnavailableException) { }
                catch (OperationCanceledException) { }
            }
        }
        finally { DeleteIfPresent(root); }
    }

    [Fact]
    public async Task StoreAcquisitionPassesCumulativeBudgetAndCancellationToRootIndexWait()
    {
        var root = NewRoot();
        var cleanupCompleted = false;
        try
        {
            var repositoryNamespace = Hash("quota-store-wait-repository");
            var keys = new[] { Hash("quota-store-wait-producer-a"), Hash("quota-store-wait-producer-b") };
            var store = OpenStore(root, repositoryNamespace);
            var initialLeases = await store.AcquireAsync(keys, CancellationToken.None);
            foreach (var lease in initialLeases) lease.Dispose();

            var rootLease = CoverageArtifactOperationLease.Acquire(root, exclusive: true);
            using var cancellation = new CancellationTokenSource();
            var ownedTasks = new List<Task<IReadOnlyList<EvidenceReceiptStore.EvidenceReceiptKeyLease>>>();
            try
            {
                var budgetOperation = Task.Run(() => store.AcquireAsync(keys,
                    TimeSpan.FromMilliseconds(80), CancellationToken.None));
                ownedTasks.Add(budgetOperation);
                var unavailable = await Assert.ThrowsAsync<EvidenceReceiptQuotaUnavailableException>(() => budgetOperation);
                Assert.Equal("receipt-index-busy", unavailable.Message);

                var cancellationOperation = Task.Run(() => store.AcquireAsync(keys,
                    TimeSpan.FromSeconds(5), cancellation.Token));
                ownedTasks.Add(cancellationOperation);
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(250));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellationOperation);
            }
            finally
            {
                cancellation.Cancel();
                rootLease.Dispose();
                try { await Task.WhenAll(ownedTasks); }
                catch (EvidenceReceiptQuotaUnavailableException) { }
                catch (OperationCanceledException) { }
                var cleanupFailures = new List<Exception>();
                foreach (var task in ownedTasks.Where(static task => task.IsCompletedSuccessfully))
                {
                    var acquired = await task;
                    foreach (var lease in acquired.Reverse())
                    {
                        try { lease.Dispose(); }
                        catch (Exception failure) { cleanupFailures.Add(failure); }
                    }
                }
                if (cleanupFailures.Count != 0)
                    throw new AggregateException($"Lease cleanup failed; retained fixture at {root}.", cleanupFailures);
            }
            cleanupCompleted = true;
        }
        finally { if (cleanupCompleted) DeleteIfPresent(root); }
    }

    [Fact]
    public async Task BatchCommitCannotSpendAnotherBoundReservation()
    {
        var root = NewRoot();
        try
        {
            var repositoryNamespace = Hash("quota-commit-repository");
            var key = Hash("quota-commit-producer");
            var store = OpenStore(root, repositoryNamespace);
            var now = DateTimeOffset.UtcNow;
            using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
            lease.EstablishAuthority("quota-commit-execution");

            var firstId = Guid.NewGuid().ToString("N");
            var secondId = Guid.NewGuid().ToString("N");
            var firstPayload = "payload-" + firstId;
            Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repositoryNamespace, key, now,
                EvidenceReceiptPayloadReservation.Create(firstId, 100)));
            Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repositoryNamespace, key, now,
                EvidenceReceiptPayloadReservation.Create(secondId, 100)));

            var priorState = lease.ReadState()
                ?? throw new InvalidOperationException("The test key must retain its established execution authority.");
            var committedState = priorState with
            {
                InFlightExecutionId = null,
                OriginCompletedAt = now,
                PayloadDirectoryName = firstPayload
            };
            var commit = new EvidenceReceiptBatchCommit(repositoryNamespace, key, "quota-commit-producer-id",
                "quota-commit-execution", committedState,
                firstPayload, now, 150, 100, lease.EssentialReservationId);

            Assert.Throws<EvidenceReceiptQuotaUnavailableException>(
                () => EvidenceReceiptQuota.CommitInvocation(root, [commit]));
            Assert.Equal(200, ReadEntry(root, key).GetProperty("ReservedPayloadBytes").GetInt64());
            Assert.Equal(2, ReadEntry(root, key).GetProperty("PayloadReservations").GetArrayLength());
        }
        finally { DeleteIfPresent(root); }
    }

    [Theory]
    [InlineData(65_536, "receipt-state-capacity-unavailable")]
    [InlineData(65_540, "receipt-passed-producer-record-capacity-unavailable")]
    public async Task BatchCommitRejectsPassedProducerRecordOutsideReaderBounds(int encodedRecordCharacters,
        string expectedReason)
    {
        var root = NewRoot();
        try
        {
            var repositoryNamespace = Hash("quota-state-bound-repository");
            var key = Hash("quota-state-bound-producer");
            var store = OpenStore(root, repositoryNamespace);
            var now = DateTimeOffset.UtcNow;
            const string executionId = "quota-state-bound-execution";
            using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
            lease.EstablishAuthority(executionId);
            var reservationId = Guid.NewGuid().ToString("N");
            var payloadName = "payload-" + reservationId;
            Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repositoryNamespace, key, now,
                EvidenceReceiptPayloadReservation.Create(reservationId, 128)));

            var priorState = lease.ReadState()
                ?? throw new InvalidOperationException("The test key must retain its established execution authority.");
            var committedState = priorState with
            {
                InFlightExecutionId = null,
                OriginCompletedAt = now,
                PayloadDirectoryName = payloadName,
                PassedProducerRecord = new string('A', encodedRecordCharacters)
            };
            var commit = new EvidenceReceiptBatchCommit(repositoryNamespace, key, "quota-state-bound-producer-id",
                executionId, committedState,
                payloadName, now, 0, 128, lease.EssentialReservationId);

            var failure = Assert.Throws<EvidenceReceiptQuotaUnavailableException>(
                () => EvidenceReceiptQuota.CommitInvocation(root, [commit]));

            Assert.Equal(expectedReason, failure.Message);
            Assert.Equal(executionId, lease.ReadState()?.InFlightExecutionId);
            var unchanged = ReadEntry(root, key);
            Assert.Equal(128, unchanged.GetProperty("ReservedPayloadBytes").GetInt64());
            Assert.Single(unchanged.GetProperty("PayloadReservations").EnumerateArray());
            Assert.Null(unchanged.GetProperty("PayloadDirectoryName").GetString());
        }
        finally { DeleteIfPresent(root); }
    }

    private static async Task AddReceiptStateAsync(EvidenceReceiptStore store, string key, string payloadName,
        DateTimeOffset completedAt, string? passedProducerRecord = null,
        string? inFlightExecutionId = null, string? auditIntentExecutionId = null, bool auditMismatch = false,
        DateTimeOffset? lastSuccessfulUse = null)
    {
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        using (var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(lease.DirectoryPath, false, true)!)
        using (keyRoot.CreateArtifactDirectory(payloadName)) { }

        var state = CreateReceiptState(key, payloadName, completedAt, passedProducerRecord,
            inFlightExecutionId, auditIntentExecutionId, auditMismatch, lastSuccessfulUse);
        EvidenceReceiptQuota.RecordState(lease, state);
    }

    private static EvidenceReceiptState CreateReceiptState(string key, string? payloadName, DateTimeOffset completedAt,
        string? passedProducerRecord = null, string? inFlightExecutionId = null, string? auditIntentExecutionId = null,
        bool auditMismatch = false, DateTimeOffset? lastSuccessfulUse = null)
        => new(1, key, inFlightExecutionId, auditIntentExecutionId, Hash("quota-origin-manifest"), completedAt,
            payloadName, passedProducerRecord, lastSuccessfulUse ?? completedAt, auditMismatch,
            auditMismatch ? "assertion:expected" : null);

    private static EvidenceReceiptStore OpenStore(string root, string repositoryNamespace)
        => EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repositoryNamespace });

    private static JsonElement ReadEntry(string root, string key)
        => ReadEntries(root).Single(entry => entry.GetProperty("Key").GetString() == key);

    private static JsonElement[] ReadEntries(string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "quota-index.json")));
        return document.RootElement.GetProperty("Entries").EnumerateArray().Select(static entry => entry.Clone()).ToArray();
    }

    private static string NewRoot()
        => Path.Combine(Path.GetTempPath(), "appsurface-receipt-quota-" + Guid.NewGuid().ToString("N"));

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void DeleteIfPresent(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
