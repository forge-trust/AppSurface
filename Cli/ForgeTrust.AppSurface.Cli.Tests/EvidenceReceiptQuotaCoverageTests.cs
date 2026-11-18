using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceReceiptQuotaCoverageTests
{
    [Fact]
    public void ReservationEstimateIncludesTheFullArtifactAndMetadataAllowanceAtItsFileBound()
    {
        var estimate = EvidenceReceiptQuota.EstimatePayloadReservation(
            artifactBytes: 11, planBytes: 13, manifestBytes: 17, producerBytes: 19, identityBytes: 23, artifactCount: 256);

        Assert.Equal(540_755, estimate);
        Assert.Throws<OverflowException>(() => EvidenceReceiptQuota.EstimatePayloadReservation(
            long.MaxValue, 1, 0, 0, 0, 0));
    }

    [Theory]
    [InlineData("artifact")]
    [InlineData("plan")]
    [InlineData("manifest")]
    [InlineData("producer")]
    [InlineData("identity")]
    [InlineData("count")]
    public void ReservationEstimateRejectsNegativeInputsAndExcessArtifactCount(string invalidInput)
    {
        var artifactBytes = invalidInput == "artifact" ? -1 : 1;
        var planBytes = invalidInput == "plan" ? -1 : 1;
        var manifestBytes = invalidInput == "manifest" ? -1 : 1;
        var producerBytes = invalidInput == "producer" ? -1 : 1;
        var identityBytes = invalidInput == "identity" ? -1 : 1;
        var artifactCount = invalidInput == "count" ? 257 : 1;

        Assert.Throws<ArgumentOutOfRangeException>(() => EvidenceReceiptQuota.EstimatePayloadReservation(
            artifactBytes, planBytes, manifestBytes, producerBytes, identityBytes, artifactCount));
    }

    [Fact]
    public async Task BoundReservationsAccountExactBytesAndReleaseOnlyTheirOwnBinding()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("bound-quota-repository");
        var key = Hash("bound-quota-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));

        var firstId = Guid.NewGuid().ToString("N");
        var secondId = Guid.NewGuid().ToString("N");
        Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 113, DateTimeOffset.UtcNow,
            firstId, "payload-stage-" + firstId, "payload-" + firstId));
        Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 211, DateTimeOffset.UtcNow,
            secondId, "payload-stage-" + secondId, "payload-" + secondId));

        Assert.Equal(324, Entry(root, key).GetProperty("ReservedPayloadBytes").GetInt64());
        EvidenceReceiptQuota.ReleasePayloadReservation(root, repository, key, firstId);
        var afterRelease = Entry(root, key);
        Assert.Equal(211, afterRelease.GetProperty("ReservedPayloadBytes").GetInt64());
        Assert.Equal(secondId, Assert.Single(afterRelease.GetProperty("PayloadReservations").EnumerateArray())
            .GetProperty("ReservationId").GetString());

        EvidenceReceiptQuota.ReleasePayloadReservation(root, repository, key, "unknown-reservation");
        Assert.Equal(211, Entry(root, key).GetProperty("ReservedPayloadBytes").GetInt64());
    }

    [Fact]
    public async Task IdenticalBoundReservationRetryIsIdempotentAndCannotRebindItsIdentifier()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("reservation-retry-repository");
        var key = Hash("reservation-retry-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        var reservationId = Guid.NewGuid().ToString("N");
        var stageName = "payload-stage-" + reservationId;
        var payloadName = "payload-" + reservationId;

        Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 109, DateTimeOffset.UtcNow,
            reservationId, stageName, payloadName));
        Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 109, DateTimeOffset.UtcNow,
            reservationId, stageName, payloadName));
        Assert.Equal(109, Entry(root, key).GetProperty("ReservedPayloadBytes").GetInt64());
        Assert.Single(Entry(root, key).GetProperty("PayloadReservations").EnumerateArray());

        Assert.Throws<InvalidDataException>(() => EvidenceReceiptQuota.TryReservePayload(root, repository, key,
            110, DateTimeOffset.UtcNow, reservationId, stageName, payloadName));
        Assert.Equal(109, Entry(root, key).GetProperty("ReservedPayloadBytes").GetInt64());
    }

    [Fact]
    public async Task ByteReleaseRemovesOneUniqueBoundReservationAndIgnoresNonpositiveRequests()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("unique-byte-release-repository");
        var key = Hash("unique-byte-release-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        var reservationId = Guid.NewGuid().ToString("N");
        Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 157, DateTimeOffset.UtcNow,
            reservationId, "payload-stage-" + reservationId, "payload-" + reservationId));

        EvidenceReceiptQuota.ReleasePayloadReservation(root, repository, key, 0);
        EvidenceReceiptQuota.ReleasePayloadReservation(root, repository, key, -1);
        Assert.Equal(157, Entry(root, key).GetProperty("ReservedPayloadBytes").GetInt64());
        EvidenceReceiptQuota.ReleasePayloadReservation(root, repository, key, 157);
        var released = Entry(root, key);
        Assert.Equal(0, released.GetProperty("ReservedPayloadBytes").GetInt64());
        var reservations = released.GetProperty("PayloadReservations");
        Assert.True(reservations.ValueKind == JsonValueKind.Null
            || reservations.ValueKind == JsonValueKind.Array && reservations.GetArrayLength() == 0);
    }

    [Fact]
    public async Task PayloadReservationCapacityRejectsTheNextBindingWithoutChangingAccounting()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("payload-reservation-capacity-repository");
        var key = Hash("payload-reservation-capacity-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        for (var index = 0; index < 64; index++)
        {
            var id = index.ToString("x32", System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 1, DateTimeOffset.UtcNow,
                id, "payload-stage-" + id, "payload-" + id));
        }

        var rejectedId = Guid.NewGuid().ToString("N");
        Assert.Throws<EvidenceReceiptQuotaUnavailableException>(() => EvidenceReceiptQuota.TryReservePayload(
            root, repository, key, 1, DateTimeOffset.UtcNow, rejectedId,
            "payload-stage-" + rejectedId, "payload-" + rejectedId));
        var entry = Entry(root, key);
        Assert.Equal(64, entry.GetProperty("ReservedPayloadBytes").GetInt64());
        Assert.Equal(64, entry.GetProperty("PayloadReservations").GetArrayLength());
    }

    [Fact]
    public async Task EssentialReservationCapacityRejectsAdditionalAuthorityWithoutLosingExistingHolds()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("essential-reservation-capacity-repository");
        var key = Hash("essential-reservation-capacity-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        for (var index = 0; index < 63; index++)
            _ = EvidenceReceiptQuota.ReserveEssential(root, repository, [key], DateTimeOffset.UtcNow);

        Assert.Throws<EvidenceReceiptQuotaUnavailableException>(() => EvidenceReceiptQuota.ReserveEssential(
            root, repository, [key], DateTimeOffset.UtcNow));
        var entry = Entry(root, key);
        Assert.Equal(64, entry.GetProperty("EssentialReservationIds").GetArrayLength());
        Assert.True(entry.GetProperty("EssentialReserved").GetBoolean());
    }

    [Fact]
    public async Task ConfirmEssentialReservationRejectsTheSixtyFifthHoldWithoutChangingTheIndex()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("confirm-essential-capacity-repository");
        var key = Hash("confirm-essential-capacity-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        var confirmedIds = new List<string>(63);
        for (var index = 0; confirmedIds.Count < 63; index++)
        {
            var id = index.ToString("x32", System.Globalization.CultureInfo.InvariantCulture);
            if (id == lease.EssentialReservationId) continue;
            confirmedIds.Add(id);
        }
        foreach (var id in confirmedIds)
            EvidenceReceiptQuota.ConfirmEssentialReservation(root, repository, key, id, DateTimeOffset.UtcNow);

        var fullIndex = await File.ReadAllBytesAsync(TestPathUtils.PathUnder(root, "quota-index.json"));
        var fullEntry = Entry(root, key);
        Assert.Equal(64, fullEntry.GetProperty("EssentialReservationIds").GetArrayLength());
        Assert.True(fullEntry.GetProperty("EssentialReserved").GetBoolean());
        Assert.True(fullEntry.GetProperty("MetadataBytes").GetInt64() > 0);

        Assert.Throws<EvidenceReceiptQuotaUnavailableException>(() => EvidenceReceiptQuota.ConfirmEssentialReservation(
            root, repository, key, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow));

        Assert.Equal(fullIndex, await File.ReadAllBytesAsync(TestPathUtils.PathUnder(root, "quota-index.json")));
        Assert.Equal(fullEntry.GetRawText(), Entry(root, key).GetRawText());
    }

    [Fact]
    public async Task PayloadReservationRefusesBytesBeyondTheRealIndexedChargeWithoutMutation()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("global-byte-capacity-repository");
        var key = Hash("global-byte-capacity-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        var indexPath = TestPathUtils.PathUnder(root, "quota-index.json");
        var before = await File.ReadAllBytesAsync(indexPath);
        var beforeEntry = Entry(root, key);
        var indexedCharge = beforeEntry.GetProperty("MetadataBytes").GetInt64()
            + beforeEntry.GetProperty("PayloadBytes").GetInt64()
            + beforeEntry.GetProperty("ReservedPayloadBytes").GetInt64();
        Assert.True(indexedCharge + before.LongLength > 0);

        Assert.False(EvidenceReceiptQuota.TryReservePayload(root, repository, key,
            EvidenceReceiptQuota.MaximumBytes - 1, DateTimeOffset.UtcNow));

        Assert.Equal(before, await File.ReadAllBytesAsync(indexPath));
        Assert.Equal(beforeEntry.GetRawText(), Entry(root, key).GetRawText());
        Assert.Equal(0, Entry(root, key).GetProperty("ReservedPayloadBytes").GetInt64());
    }

    [Fact]
    public async Task ConfirmEssentialReservationAddsMissingHoldWithoutDuplicatingExistingHold()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("confirm-essential-repository");
        var key = Hash("confirm-essential-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        var addedId = Guid.NewGuid().ToString("N");

        EvidenceReceiptQuota.ConfirmEssentialReservation(root, repository, key, addedId, DateTimeOffset.UtcNow);
        EvidenceReceiptQuota.ConfirmEssentialReservation(root, repository, key, addedId, DateTimeOffset.UtcNow);

        var entry = Entry(root, key);
        Assert.Equal(2, entry.GetProperty("EssentialReservationIds").GetArrayLength());
        Assert.Contains(lease.EssentialReservationId,
            entry.GetProperty("EssentialReservationIds").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(addedId,
            entry.GetProperty("EssentialReservationIds").EnumerateArray().Select(item => item.GetString()));
        Assert.True(entry.GetProperty("EssentialReserved").GetBoolean());
        Assert.Equal(EvidenceReceiptQuota.EssentialReservationBytes,
            entry.GetProperty("MetadataBytes").GetInt64());
    }

    [Fact]
    public void ConfirmEssentialReservationCreatesMissingIndexEntryUnderHeldKeyLease()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("confirm-missing-row-repository");
        var key = Hash("confirm-missing-row-key");
        var keyPath = TestPathUtils.PathUnder(root, repository, key);
        using (var rootDirectory = CoverageRunOutputLease.AcquireArtifactDirectory(root, createMissing: true, requirePrivate: true)!)
        using (var namespaceDirectory = rootDirectory.CreateArtifactDirectory(repository))
        using (namespaceDirectory.CreateArtifactDirectory(key)) { }

        var reservationId = Guid.NewGuid().ToString("N");
        using (CoverageArtifactOperationLease.Acquire(keyPath, exclusive: true))
        {
            EvidenceReceiptQuota.ConfirmEssentialReservation(root, repository, key, reservationId, DateTimeOffset.UtcNow);

            var entry = Entry(root, key);
            Assert.Equal(reservationId, Assert.Single(entry.GetProperty("EssentialReservationIds").EnumerateArray()).GetString());
            Assert.True(entry.GetProperty("EssentialReserved").GetBoolean());
            Assert.Equal(EvidenceReceiptQuota.EssentialReservationBytes, entry.GetProperty("MetadataBytes").GetInt64());
            Assert.Equal(EvidenceReceiptQuota.EssentialReservationBytes,
                entry.GetProperty("EssentialReservationBytes").GetInt64());
        }
        Assert.True(Directory.Exists(keyPath));
    }

    [Fact]
    public void EssentialReservationCreatesMissingPrivateNamespaceAndKeyDirectories()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("essential-private-path-repository");
        var key = Hash("essential-private-path-key");
        using (CoverageRunOutputLease.AcquireArtifactDirectory(root, createMissing: true, requirePrivate: true)!) { }

        var reservationId = EvidenceReceiptQuota.ReserveEssential(root, repository, [key], DateTimeOffset.UtcNow);

        using var namespaceDirectory = CoverageRunOutputLease.AcquireArtifactDirectory(
            TestPathUtils.PathUnder(root, repository), createMissing: false, requirePrivate: true);
        Assert.NotNull(namespaceDirectory);
        using var keyDirectory = CoverageRunOutputLease.AcquireArtifactDirectory(
            TestPathUtils.PathUnder(root, repository, key), createMissing: false, requirePrivate: true);
        Assert.NotNull(keyDirectory);
        Assert.Equal(reservationId, Assert.Single(Entry(root, key).GetProperty("EssentialReservationIds").EnumerateArray()).GetString());
    }

    [Fact]
    public void TotalEntryCapacityRefusesNewEssentialReservationsWithoutEvictingUnownedRows()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var namespaceForRows = Hash("full-index-row-repository");
        var entries = Enumerable.Range(0, EvidenceReceiptQuota.MaximumEntries)
            .Select(index => new EvidenceReceiptQuotaEntry(namespaceForRows, Hash("full-index-row-" + index),
                0, 0, 0, 0, false, false, false, null, null, null, false,
                RetiredPayloads: [], EssentialReservationIds: []))
            .ToList();
        var originalIdentities = entries.Select(entry => entry.RepositoryNamespace + ":" + entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        using (var rootDirectory = CoverageRunOutputLease.AcquireArtifactDirectory(root, createMissing: true, requirePrivate: true)!)
        using (var indexFile = rootDirectory.OpenArtifactFile("quota-index.json", createNew: true))
        {
            var indexBytes = JsonSerializer.SerializeToUtf8Bytes(new EvidenceReceiptQuotaIndex(4, entries));
            indexFile.Write(indexBytes);
            indexFile.Flush(flushToDisk: true);
        }

        var requestedRepository = Hash("full-index-request-repository");
        var requestedKey = Hash("full-index-request-key");
        Assert.Throws<EvidenceReceiptQuotaUnavailableException>(() => EvidenceReceiptQuota.ReserveEssential(
            root, requestedRepository, [requestedKey], DateTimeOffset.UtcNow));
        Assert.Throws<EvidenceReceiptQuotaUnavailableException>(() => EvidenceReceiptQuota.ConfirmEssentialReservation(
            root, requestedRepository, requestedKey, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow));

        var persisted = Entries(root);
        Assert.Equal(EvidenceReceiptQuota.MaximumEntries, persisted.Length);
        Assert.True(originalIdentities.SetEquals(persisted.Select(entry => entry.GetProperty("RepositoryNamespace").GetString()
            + ":" + entry.GetProperty("Key").GetString())));
        Assert.DoesNotContain(persisted, entry => entry.GetProperty("RepositoryNamespace").GetString() == requestedRepository
            && entry.GetProperty("Key").GetString() == requestedKey);
        Assert.All(persisted, entry =>
        {
            Assert.Equal(0, entry.GetProperty("MetadataBytes").GetInt64());
            Assert.Equal(0, entry.GetProperty("ReservedPayloadBytes").GetInt64());
            Assert.False(entry.TryGetProperty("EvictionPending", out var evictionPending) && evictionPending.GetBoolean());
        });
    }

    [Fact]
    public async Task BoundPayloadCommitConsumesOnlyItsMatchingReservation()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("selective-commit-repository");
        var key = Hash("selective-commit-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        var committedId = Guid.NewGuid().ToString("N");
        var remainingId = Guid.NewGuid().ToString("N");
        var committedName = "payload-" + committedId;
        Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 130, DateTimeOffset.UtcNow,
            committedId, "payload-stage-" + committedId, committedName));
        Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 270, DateTimeOffset.UtcNow,
            remainingId, "payload-stage-" + remainingId, "payload-" + remainingId));

        EvidenceReceiptQuota.CommitPayload(root, repository, key, 100, committedName,
            DateTimeOffset.UtcNow, stateBytes: 23);

        var committed = Entry(root, key);
        Assert.Equal(270, committed.GetProperty("ReservedPayloadBytes").GetInt64());
        Assert.Equal(270, Assert.Single(committed.GetProperty("PayloadReservations").EnumerateArray())
            .GetProperty("ReservedBytes").GetInt64());
        Assert.Equal(100, committed.GetProperty("PayloadBytes").GetInt64());
        Assert.Equal(committedName, committed.GetProperty("PayloadDirectoryName").GetString());
        Assert.Equal(23, committed.GetProperty("MetadataBytes").GetInt64());
    }

    [Fact]
    public async Task PayloadCommitCannotSpendAnUnmatchedBoundReservation()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("unmatched-commit-repository");
        var key = Hash("unmatched-commit-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        var reservationId = Guid.NewGuid().ToString("N");
        var boundPayloadName = "payload-" + reservationId;
        Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 91, DateTimeOffset.UtcNow,
            reservationId, "payload-stage-" + reservationId, boundPayloadName));
        var before = await File.ReadAllBytesAsync(TestPathUtils.PathUnder(root, "quota-index.json"));

        Assert.Throws<EvidenceReceiptQuotaUnavailableException>(() => EvidenceReceiptQuota.CommitPayload(root,
            repository, key, 1, "payload-" + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, stateBytes: 17));

        Assert.Equal(before, await File.ReadAllBytesAsync(TestPathUtils.PathUnder(root, "quota-index.json")));
        var unchanged = Entry(root, key);
        Assert.Equal(91, unchanged.GetProperty("ReservedPayloadBytes").GetInt64());
        Assert.Equal(reservationId, Assert.Single(unchanged.GetProperty("PayloadReservations").EnumerateArray())
            .GetProperty("ReservationId").GetString());
        Assert.Null(unchanged.GetProperty("PayloadDirectoryName").GetString());
        Assert.Equal(0, unchanged.GetProperty("PayloadBytes").GetInt64());
    }

    [Fact]
    public void MaintenanceRemovesOrphanEssentialReservationButKeepsItsPrivateKeyDirectory()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("orphan-essential-repository");
        var key = Hash("orphan-essential-key");

        using (CoverageRunOutputLease.AcquireArtifactDirectory(root, createMissing: true, requirePrivate: true)!) { }
        using (var namespaceRoot = CoverageRunOutputLease.AcquireArtifactDirectory(
            TestPathUtils.PathUnder(root, repository), createMissing: true, requirePrivate: true)!)
        using (namespaceRoot.CreateArtifactDirectory(key)) { }
        _ = EvidenceReceiptQuota.ReserveEssential(root, repository, [key], DateTimeOffset.UtcNow);
        var keyPath = TestPathUtils.PathUnder(root, repository, key);
        Assert.True(Directory.Exists(keyPath));
        Assert.True(Entry(root, key).GetProperty("EssentialReserved").GetBoolean());

        EvidenceReceiptQuota.Maintain(root, DateTimeOffset.UtcNow);

        Assert.Empty(Entries(root));
        Assert.True(Directory.Exists(keyPath));
    }

    [Fact]
    public async Task ExpiredReceiptDeletesItsPrivatePayloadAndRemovesItsIndexEntry()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("expired-tombstone-repository");
        var key = Hash("expired-tombstone-key");
        var payloadName = "payload-expired-private";
        var expectedState = await AddExpiredReceiptAsync(root, repository, key, payloadName, includeForeignFile: false);
        var payloadPath = TestPathUtils.PathUnder(root, repository, key, payloadName);
        var indexPath = TestPathUtils.PathUnder(root, "quota-index.json");
        var indexBeforeRead = await File.ReadAllBytesAsync(indexPath);
        Assert.Equal(expectedState,
            EvidenceReceiptQuota.ReadAuthority(root, repository, key, readOnly: true));
        Assert.Empty(EvidenceReceiptQuota.ReadComparable(root, repository, "no-comparable-producer",
            DateTimeOffset.UtcNow, readOnly: true));
        Assert.Equal(indexBeforeRead, await File.ReadAllBytesAsync(indexPath));

        EvidenceReceiptQuota.Maintain(root, DateTimeOffset.UtcNow.AddDays(8));
        Assert.Empty(Entries(root));
        Assert.False(Directory.Exists(payloadPath));
    }

    [Fact]
    public async Task MaintenanceTombstonePreservesRegularFileAtOwnedPayloadName()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("expired-file-payload-repository");
        var key = Hash("expired-file-payload-key");
        var payloadName = "payload-expired-file-entry";
        await AddExpiredReceiptAsync(root, repository, key, payloadName, includeForeignFile: false);
        var keyPath = TestPathUtils.PathUnder(root, repository, key);
        var payloadPath = TestPathUtils.PathUnder(keyPath, payloadName);
        using (var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(keyPath, false, true)!)
            keyRoot.DeleteArtifactEntry(payloadName, 16);
        await File.WriteAllTextAsync(payloadPath, "operator replacement survives");

        EvidenceReceiptQuota.Maintain(root, DateTimeOffset.UtcNow.AddDays(8));

        var tombstone = Entry(root, key);
        Assert.True(tombstone.GetProperty("EvictionPending").GetBoolean());
        Assert.Equal(payloadName, tombstone.GetProperty("PayloadDirectoryName").GetString());
        Assert.Equal("operator replacement survives", await File.ReadAllTextAsync(payloadPath));
        Assert.Single(Entries(root));
    }

    [Fact]
    public async Task ExpiredReceiptTombstonePreservesForeignPayloadEntryUntilItIsRemoved()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("expired-foreign-repository");
        var key = Hash("expired-foreign-key");
        var payloadName = "payload-expired-foreign";
        var foreignName = "foreign-linked-file.bin";
        await AddExpiredReceiptAsync(root, repository, key, payloadName, includeForeignFile: true,
            foreignFileName: foreignName);
        var payloadPath = TestPathUtils.PathUnder(root, repository, key, payloadName);
        var foreignPath = TestPathUtils.PathUnder(payloadPath, foreignName);
        File.Delete(foreignPath);
        var externalSentinel = TestPathUtils.PathUnder(temporary.Path, "foreign-target.txt");
        await File.WriteAllTextAsync(externalSentinel, "foreign target survives");
        try { File.CreateSymbolicLink(foreignPath, externalSentinel); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { throw new InvalidOperationException("The quota symlink-safety fixture requires file-symlink capability on this host.", exception); }
        var sentinel = await File.ReadAllBytesAsync(externalSentinel);

        EvidenceReceiptQuota.Maintain(root, DateTimeOffset.UtcNow.AddDays(8));
        var tombstone = Entry(root, key);
        Assert.True(tombstone.GetProperty("EvictionPending").GetBoolean());
        Assert.Null(tombstone.GetProperty("AuthorityState").GetString());
        Assert.Equal(0, tombstone.GetProperty("ReservedPayloadBytes").GetInt64());
        Assert.Equal(payloadName, tombstone.GetProperty("PayloadDirectoryName").GetString());
        Assert.Equal(sentinel, await File.ReadAllBytesAsync(externalSentinel));
        Assert.Single(Entries(root));
        Assert.True(Entry(root, key).GetProperty("EvictionPending").GetBoolean());

        var tombstoneBytes = await File.ReadAllBytesAsync(TestPathUtils.PathUnder(root, "quota-index.json"));
        var reservationId = Guid.NewGuid().ToString("N");
        Assert.Throws<EvidenceReceiptQuotaUnavailableException>(() => EvidenceReceiptQuota.TryReservePayload(
            root, repository, key, 1, DateTimeOffset.UtcNow, reservationId,
            "payload-stage-" + reservationId, "payload-" + reservationId));
        Assert.Equal(tombstoneBytes, await File.ReadAllBytesAsync(TestPathUtils.PathUnder(root, "quota-index.json")));
        Assert.Equal(sentinel, await File.ReadAllBytesAsync(externalSentinel));

        // Simulate operator cleanup through the retained artifact-directory API.
        File.Delete(foreignPath);
        EvidenceReceiptQuota.Maintain(root, DateTimeOffset.UtcNow.AddDays(8));
        Assert.Empty(Entries(root));
        Assert.False(Directory.Exists(payloadPath));
        Assert.Equal("foreign target survives", await File.ReadAllTextAsync(externalSentinel));
    }

    [Fact]
    public async Task MaintenanceClearsExpiredSettledEssentialHoldsBeforePublishingRefusedDeletionTombstone()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("expired-essential-hold-repository");
        var key = Hash("expired-essential-hold-key");
        var payloadName = "payload-expired-essential-hold";
        var settled = await AddExpiredReceiptAsync(root, repository, key, payloadName, includeForeignFile: false);
        Assert.Equal(settled, EvidenceReceiptQuota.ReadAuthority(root, repository, key, readOnly: true));
        var keyPath = TestPathUtils.PathUnder(root, repository, key);
        var payloadPath = TestPathUtils.PathUnder(keyPath, payloadName);
        var linkedPath = TestPathUtils.PathUnder(payloadPath, "foreign-link");
        var sentinelPath = TestPathUtils.PathUnder(temporary.Path, "external-sentinel.txt");
        await File.WriteAllTextAsync(sentinelPath, "outside remains unchanged");
        var sentinelBytes = await File.ReadAllBytesAsync(sentinelPath);
        try { File.CreateSymbolicLink(linkedPath, sentinelPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { throw new InvalidOperationException("The tombstone fixture requires file-symlink capability on this host.", exception); }

        using (CoverageArtifactOperationLease.Acquire(keyPath, exclusive: true))
            EvidenceReceiptQuota.ConfirmEssentialReservation(root, repository, key,
                Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var before = Entry(root, key);
        Assert.Single(before.GetProperty("EssentialReservationIds").EnumerateArray());
        Assert.True(before.GetProperty("EssentialReserved").GetBoolean());

        EvidenceReceiptQuota.Maintain(root, DateTimeOffset.UtcNow.AddDays(8));

        var tombstone = Entry(root, key);
        Assert.True(tombstone.GetProperty("EvictionPending").GetBoolean());
        Assert.Empty(tombstone.GetProperty("EssentialReservationIds").EnumerateArray());
        Assert.False(tombstone.GetProperty("EssentialReserved").GetBoolean());
        Assert.Equal(0, tombstone.GetProperty("MetadataBytes").GetInt64());
        Assert.Null(tombstone.GetProperty("AuthorityState").GetString());
        Assert.True(File.Exists(linkedPath));
        Assert.Equal(sentinelBytes, await File.ReadAllBytesAsync(sentinelPath));

        File.Delete(linkedPath);
        EvidenceReceiptQuota.Maintain(root, DateTimeOffset.UtcNow.AddDays(8));
        Assert.Empty(Entries(root));
        Assert.False(Directory.Exists(payloadPath));
        Assert.Equal(sentinelBytes, await File.ReadAllBytesAsync(sentinelPath));
    }

    [Fact]
    public async Task EvictionTombstoneRetainsRetiredChargeUntilLinkedPayloadEntryIsRemoved()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("retired-tombstone-repository");
        var key = Hash("retired-tombstone-key");
        var activePayload = "payload-retired-tombstone-active";
        var retiredPayload = "payload-retired-tombstone-old";
        await AddExpiredReceiptAsync(root, repository, key, activePayload, includeForeignFile: false);
        var keyPath = TestPathUtils.PathUnder(root, repository, key);
        var retiredPath = TestPathUtils.PathUnder(keyPath, retiredPayload);
        var sentinelPath = TestPathUtils.PathUnder(temporary.Path, "retired-tombstone-sentinel.txt");
        var sentinelBytes = Encoding.UTF8.GetBytes("external retired target remains unchanged");
        await File.WriteAllBytesAsync(sentinelPath, sentinelBytes);
        using (var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(keyPath, false, true)!)
        using (var retired = keyRoot.CreateArtifactDirectory(retiredPayload))
        using (var file = retired.OpenArtifactFile("owned.bin", createNew: true))
        {
            file.Write(new byte[23]);
            file.Flush(flushToDisk: true);
        }
        var linkedPath = TestPathUtils.PathUnder(retiredPath, "foreign-link");
        try { File.CreateSymbolicLink(linkedPath, sentinelPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { throw new InvalidOperationException("The retired tombstone fixture requires file-symlink capability on this host.", exception); }

        var indexPath = TestPathUtils.PathUnder(root, "quota-index.json");
        var index = JsonNode.Parse(await File.ReadAllBytesAsync(indexPath))!;
        index["Entries"]!.AsArray().Single()!["RetiredPayloads"] = new JsonArray(
            JsonSerializer.SerializeToNode(new { DirectoryName = retiredPayload, Bytes = 23L }));
        await File.WriteAllBytesAsync(indexPath, Encoding.UTF8.GetBytes(index.ToJsonString()));

        EvidenceReceiptQuota.Maintain(root, DateTimeOffset.UtcNow.AddDays(8));

        var tombstone = Entry(root, key);
        Assert.True(tombstone.GetProperty("EvictionPending").GetBoolean());
        Assert.Equal(23, Assert.Single(tombstone.GetProperty("RetiredPayloads").EnumerateArray())
            .GetProperty("Bytes").GetInt64());
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(keyPath, activePayload)));
        Assert.True(Directory.Exists(retiredPath));
        Assert.True(File.Exists(linkedPath));
        Assert.Equal(sentinelBytes, await File.ReadAllBytesAsync(sentinelPath));

        File.Delete(linkedPath);
        EvidenceReceiptQuota.Maintain(root, DateTimeOffset.UtcNow.AddDays(8));

        Assert.Empty(Entries(root));
        Assert.False(Directory.Exists(retiredPath));
        Assert.Equal(sentinelBytes, await File.ReadAllBytesAsync(sentinelPath));
    }

    [Fact]
    public async Task MaintenancePreservesExpiredPayloadChargeWhenAuthorityIsAbsentButProvenanceRemains()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("missing-authority-provenance-repository");
        var key = Hash("missing-authority-provenance-key");
        var payloadName = "payload-missing-authority-provenance";
        var state = await AddExpiredReceiptAsync(root, repository, key, payloadName, includeForeignFile: false);
        Assert.Equal(state, EvidenceReceiptQuota.ReadAuthority(root, repository, key, readOnly: true));
        var keyPath = TestPathUtils.PathUnder(root, repository, key);
        var payloadFile = TestPathUtils.PathUnder(keyPath, payloadName, "owned.bin");
        using (CoverageArtifactOperationLease.Acquire(keyPath, exclusive: true))
        {
            using (var payload = CoverageRunOutputLease.AcquireArtifactDirectory(
                TestPathUtils.PathUnder(keyPath, payloadName), false, true)!)
            using (var file = payload.OpenArtifactFile("owned.bin", createNew: true))
            {
                file.Write(new byte[] { 7, 8, 9, 10 });
                file.Flush(flushToDisk: true);
            }
            Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 4, DateTimeOffset.UtcNow));
            EvidenceReceiptQuota.CommitPayload(root, repository, key, 4, payloadName, state.OriginCompletedAt!.Value,
                stateBytes: 0);
            EvidenceReceiptQuota.RecordState(root, repository, key, JsonSerializer.SerializeToUtf8Bytes(state).Length, state);
        }

        var indexPath = TestPathUtils.PathUnder(root, "quota-index.json");
        var index = JsonNode.Parse(await File.ReadAllBytesAsync(indexPath))!;
        var entry = index["Entries"]!.AsArray().Single()!;
        entry["AuthorityState"] = null;
        await File.WriteAllBytesAsync(indexPath, Encoding.UTF8.GetBytes(index.ToJsonString()));
        EvidenceReceiptQuota.Maintain(root, DateTimeOffset.UtcNow.AddDays(8));

        Assert.Single(Entries(root));
        var preserved = Entry(root, key);
        Assert.Null(preserved.GetProperty("AuthorityState").GetString());
        Assert.Equal(payloadName, preserved.GetProperty("PayloadDirectoryName").GetString());
        Assert.Equal(state.OriginCompletedAt, preserved.GetProperty("OriginCompletedAt").GetDateTimeOffset());
        Assert.Equal(4, preserved.GetProperty("PayloadBytes").GetInt64());
        Assert.Equal(new byte[] { 7, 8, 9, 10 }, await File.ReadAllBytesAsync(payloadFile));
    }

    [Fact]
    public async Task MaintenanceRemovesExpiredSettledAuthorityWithNoPayloadOrRetiredFiles()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("expired-empty-authority-repository");
        var key = Hash("expired-empty-authority-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using (var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None)))
        {
            var completedAt = DateTimeOffset.UtcNow;
            var state = new EvidenceReceiptState(1, key, null, null, Hash("expired-empty-authority-origin"),
                completedAt, null, null, completedAt, false, null);
            EvidenceReceiptQuota.RecordState(root, repository, key,
                JsonSerializer.SerializeToUtf8Bytes(state).Length, state);
        }
        Assert.Single(Entries(root));
        Assert.Null(Entry(root, key).GetProperty("PayloadDirectoryName").GetString());
        Assert.Empty(Entry(root, key).GetProperty("RetiredPayloads").EnumerateArray());

        EvidenceReceiptQuota.Maintain(root, DateTimeOffset.UtcNow.AddDays(8));

        Assert.Empty(Entries(root));
        Assert.True(Directory.Exists(TestPathUtils.PathUnder(root, repository, key)));
    }

    [Fact]
    public async Task MaintenancePreservesSettledReceiptWhenAuthorityPayloadBindingDisagreesWithIndex()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("mismatched-settled-authority-repository");
        var key = Hash("mismatched-settled-authority-key");
        var payloadName = "payload-mismatched-settled-authority";
        var settled = await AddExpiredReceiptAsync(root, repository, key, payloadName, includeForeignFile: false);
        Assert.Equal(settled, EvidenceReceiptQuota.ReadAuthority(root, repository, key, readOnly: true));
        var indexPath = TestPathUtils.PathUnder(root, "quota-index.json");
        var index = JsonNode.Parse(await File.ReadAllBytesAsync(indexPath))!;
        var entry = index["Entries"]!.AsArray().Single()!;
        entry["AuthorityState"]!["PayloadDirectoryName"] = "payload-other-authority";
        await File.WriteAllBytesAsync(indexPath, Encoding.UTF8.GetBytes(index.ToJsonString()));
        var payloadPath = TestPathUtils.PathUnder(root, repository, key, payloadName);

        EvidenceReceiptQuota.Maintain(root, DateTimeOffset.UtcNow.AddDays(8));

        Assert.Single(Entries(root));
        var preserved = Entry(root, key);
        Assert.Equal(payloadName, preserved.GetProperty("PayloadDirectoryName").GetString());
        Assert.Equal("payload-other-authority",
            preserved.GetProperty("AuthorityState").GetProperty("PayloadDirectoryName").GetString());
        Assert.True(Directory.Exists(payloadPath));
    }

    [Theory]
    [InlineData("in-flight")]
    [InlineData("audit-intent")]
    [InlineData("audit-mismatch")]
    public async Task MaintenancePreservesPayloadWhenPersistedAuthorityIsNotSettled(string defect)
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("unsettled-authority-repository-" + defect);
        var key = Hash("unsettled-authority-key-" + defect);
        var payloadName = "payload-unsettled-authority-" + defect;
        var payloadBytes = Encoding.UTF8.GetBytes("owned quota payload");
        var completedAt = DateTimeOffset.UtcNow;
        var settled = await AddSettledPayloadAsync(root, repository, key, payloadName, payloadBytes, completedAt);
        Assert.Equal(settled, EvidenceReceiptQuota.ReadAuthority(root, repository, key, readOnly: true));

        var indexPath = TestPathUtils.PathUnder(root, "quota-index.json");
        var index = JsonNode.Parse(await File.ReadAllBytesAsync(indexPath))!;
        var entry = index["Entries"]!.AsArray().Single()!;
        var state = entry["AuthorityState"]!;
        if (defect == "in-flight") state["InFlightExecutionId"] = "unresolved-flight";
        else if (defect == "audit-intent") state["AuditIntentExecutionId"] = "unresolved-audit";
        else state["AuditMismatch"] = true;
        await File.WriteAllBytesAsync(indexPath, Encoding.UTF8.GetBytes(index.ToJsonString()));
        var preservedIndex = await File.ReadAllBytesAsync(indexPath);
        var payloadFile = TestPathUtils.PathUnder(root, repository, key, payloadName, "owned.bin");

        EvidenceReceiptQuota.Maintain(root, completedAt.AddDays(8));

        // Maintenance may republish the same index using its normal JSON escaping; every value must remain intact.
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(preservedIndex),
            JsonNode.Parse(await File.ReadAllBytesAsync(indexPath))));
        var preserved = Entry(root, key);
        Assert.Equal(payloadBytes.LongLength, preserved.GetProperty("PayloadBytes").GetInt64());
        Assert.Equal(payloadName, preserved.GetProperty("PayloadDirectoryName").GetString());
        Assert.False(preserved.TryGetProperty("EvictionPending", out var pending) && pending.GetBoolean());
        Assert.Equal(payloadBytes, await File.ReadAllBytesAsync(payloadFile));
        var persistedState = preserved.GetProperty("AuthorityState");
        if (defect == "in-flight")
            Assert.Equal("unresolved-flight", persistedState.GetProperty("InFlightExecutionId").GetString());
        else if (defect == "audit-intent")
            Assert.Equal("unresolved-audit", persistedState.GetProperty("AuditIntentExecutionId").GetString());
        else
            Assert.True(persistedState.GetProperty("AuditMismatch").GetBoolean());
    }

    [Fact]
    public async Task MaintenanceExpiresFutureDatedSettledPayloadAndRemovesItsExactCharge()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("future-settlement-repository");
        var key = Hash("future-settlement-key");
        var payloadName = "payload-future-settlement";
        var payloadBytes = Encoding.UTF8.GetBytes("future-dated owned bytes");
        var completedAt = DateTimeOffset.UtcNow;
        var state = await AddSettledPayloadAsync(root, repository, key, payloadName, payloadBytes, completedAt);
        var payloadPath = TestPathUtils.PathUnder(root, repository, key, payloadName);
        Assert.Equal(state, EvidenceReceiptQuota.ReadAuthority(root, repository, key, readOnly: true));
        Assert.Equal(payloadBytes.LongLength, Entry(root, key).GetProperty("PayloadBytes").GetInt64());
        var futureCompletion = DateTimeOffset.UtcNow.AddMinutes(5);
        var indexPath = TestPathUtils.PathUnder(root, "quota-index.json");
        var index = JsonNode.Parse(await File.ReadAllBytesAsync(indexPath))!;
        var entry = index["Entries"]!.AsArray().Single()!;
        entry["OriginCompletedAt"] = futureCompletion;
        entry["AuthorityState"]!["OriginCompletedAt"] = futureCompletion;
        await File.WriteAllBytesAsync(indexPath, Encoding.UTF8.GetBytes(index.ToJsonString()));

        EvidenceReceiptQuota.Maintain(root, DateTimeOffset.UtcNow);

        Assert.Empty(Entries(root));
        Assert.False(Directory.Exists(payloadPath));
    }

    [Fact]
    public async Task MaintenancePreservesEssentialReservationWhenItsPrivateKeyDirectoryIsMissing()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("missing-key-directory-repository");
        var key = Hash("missing-key-directory-key");
        var reservationId = Guid.NewGuid().ToString("N");
        using (var rootLease = CoverageRunOutputLease.AcquireArtifactDirectory(
            root, createMissing: true, requirePrivate: true)!)
        using (var indexFile = rootLease.OpenArtifactFile("quota-index.json", createNew: true))
        {
            var entry = new EvidenceReceiptQuotaEntry(repository, key, EvidenceReceiptQuota.EssentialReservationBytes,
                0, 0, EvidenceReceiptQuota.EssentialReservationBytes, true, false, false,
                null, null, null, false, RetiredPayloads: [], EssentialReservationIds: [reservationId]);
            indexFile.Write(JsonSerializer.SerializeToUtf8Bytes(new EvidenceReceiptQuotaIndex(4, [entry])));
            indexFile.Flush(flushToDisk: true);
        }
        var keyPath = TestPathUtils.PathUnder(root, repository, key);
        var indexPath = TestPathUtils.PathUnder(root, "quota-index.json");
        var indexBefore = await File.ReadAllBytesAsync(indexPath);

        EvidenceReceiptQuota.Maintain(root, DateTimeOffset.UtcNow.AddDays(20));

        Assert.Equal(indexBefore, await File.ReadAllBytesAsync(indexPath));
        Assert.False(Directory.Exists(keyPath));
        var preserved = Entry(root, key);
        Assert.Equal(EvidenceReceiptQuota.EssentialReservationBytes,
            preserved.GetProperty("MetadataBytes").GetInt64());
        Assert.Equal(EvidenceReceiptQuota.EssentialReservationBytes,
            preserved.GetProperty("EssentialReservationBytes").GetInt64());
        Assert.Equal(reservationId, Assert.Single(preserved.GetProperty("EssentialReservationIds")
            .EnumerateArray()).GetString());
    }

    [Fact]
    public async Task InvalidationRefusesAReceiptAlreadyAtRetiredPayloadCapacity()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("retired-capacity-invalidation-repository");
        var key = Hash("retired-capacity-invalidation-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        var payloadName = "payload-retired-capacity-active";
        var completedAt = DateTimeOffset.UtcNow;
        using (var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(lease.DirectoryPath, false, true)!)
        using (var payload = keyRoot.CreateArtifactDirectory(payloadName))
        using (var file = payload.OpenArtifactFile("owned.bin", createNew: true))
        {
            file.Write(new byte[] { 1, 2, 3 });
            file.Flush(flushToDisk: true);
        }
        Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 3, completedAt));
        EvidenceReceiptQuota.CommitPayload(root, repository, key, 3, payloadName, completedAt, stateBytes: 0);
        var settled = new EvidenceReceiptState(1, key, null, null, Hash("retired-capacity-origin"), completedAt,
            payloadName, null, completedAt, false, null);
        EvidenceReceiptQuota.RecordState(root, repository, key, JsonSerializer.SerializeToUtf8Bytes(settled).Length, settled);

        var indexPath = TestPathUtils.PathUnder(root, "quota-index.json");
        var index = JsonNode.Parse(await File.ReadAllBytesAsync(indexPath))!;
        index["Entries"]!.AsArray().Single()!["RetiredPayloads"] = RetiredPayloads(64);
        await File.WriteAllBytesAsync(indexPath, Encoding.UTF8.GetBytes(index.ToJsonString()));
        var fullIndex = await File.ReadAllBytesAsync(indexPath);
        var invalidated = new EvidenceReceiptState(1, key, null, null, null, null, null, null, null, false, null);

        Assert.Throws<EvidenceReceiptQuotaUnavailableException>(() =>
            EvidenceReceiptQuota.InvalidatePayload(lease, settled, invalidated));

        Assert.Equal(fullIndex, await File.ReadAllBytesAsync(indexPath));
        Assert.Equal(settled, lease.ReadState());
        var unchanged = Entry(root, key);
        Assert.Equal(payloadName, unchanged.GetProperty("PayloadDirectoryName").GetString());
        Assert.Equal(3, unchanged.GetProperty("PayloadBytes").GetInt64());
        Assert.Equal(64, unchanged.GetProperty("RetiredPayloads").GetArrayLength());
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(
            TestPathUtils.PathUnder(lease.DirectoryPath, payloadName, "owned.bin")));
    }

    [Theory]
    [InlineData("index-flight")]
    [InlineData("index-audit")]
    [InlineData("index-quarantine")]
    [InlineData("index-payload-binding")]
    [InlineData("index-origin-binding")]
    [InlineData("active-payload-already-retired")]
    public async Task InvalidationPreservesSettledPayloadForEveryForbiddenAuthorityShape(string defect)
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("invalidation-shape-repository-" + defect);
        var key = Hash("invalidation-shape-key-" + defect);
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        lease.EstablishAuthority("invalidation-shape-owner");
        var payloadName = "payload-invalidation-shape";
        var completedAt = DateTimeOffset.UtcNow;
        using (var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(lease.DirectoryPath, false, true)!)
        using (var payload = keyRoot.CreateArtifactDirectory(payloadName))
        using (var file = payload.OpenArtifactFile("owned.bin", createNew: true))
        {
            file.Write(new byte[] { 7, 8, 9 });
            file.Flush(flushToDisk: true);
        }
        Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 12, completedAt));
        EvidenceReceiptQuota.CommitPayload(root, repository, key, 3, payloadName, completedAt, stateBytes: 0);
        var passed = EvidenceCanonicalJson.Serialize(new EvidenceProducerResult("producer",
            EvidenceProducerOutcome.Passed, ["assertion"]));
        lease.CommitPassed("invalidation-shape-owner", Hash("invalidation-shape-origin"), completedAt,
            payloadName, passed);
        var settled = lease.ReadState()!;
        var invalidated = new EvidenceReceiptState(1, key, null, null, null, null, null);
        var indexPath = TestPathUtils.PathUnder(root, "quota-index.json");

        var originalIndex = await File.ReadAllBytesAsync(indexPath);
        {
            var index = JsonNode.Parse(await File.ReadAllBytesAsync(indexPath))!;
            var entry = index["Entries"]!.AsArray().Single()!;
            if (defect == "index-flight") entry["HasInFlight"] = true;
            else if (defect == "index-audit") entry["HasAuditIntent"] = true;
            else if (defect == "index-quarantine") entry["Quarantined"] = true;
            else if (defect == "index-payload-binding") entry["PayloadDirectoryName"] = "payload-other-binding";
            else if (defect == "index-origin-binding") entry["OriginCompletedAt"] = completedAt.AddTicks(-1);
            else entry["RetiredPayloads"] = new JsonArray(JsonSerializer.SerializeToNode(
                new { DirectoryName = payloadName, Bytes = 3L }));
            await File.WriteAllBytesAsync(indexPath, Encoding.UTF8.GetBytes(index.ToJsonString()));
        }

        var unchangedIndex = await File.ReadAllBytesAsync(indexPath);
        try
        {
            if (defect == "active-payload-already-retired")
            {
                Assert.Throws<InvalidDataException>(() => EvidenceReceiptQuota.InvalidatePayload(lease, settled, invalidated));
                Assert.Throws<InvalidDataException>(() => lease.ReadState());
            }
            else
            {
                EvidenceReceiptQuota.InvalidatePayload(lease, settled, invalidated);
                Assert.Equal(settled, lease.ReadState());
            }
            Assert.Equal(unchangedIndex, await File.ReadAllBytesAsync(indexPath));
            var unchanged = Entry(root, key);
            var expectedPayloadBinding = defect == "index-payload-binding" ? "payload-other-binding" : payloadName;
            Assert.Equal(expectedPayloadBinding, unchanged.GetProperty("PayloadDirectoryName").GetString());
            Assert.Equal(3, unchanged.GetProperty("PayloadBytes").GetInt64());
            Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(
                TestPathUtils.PathUnder(lease.DirectoryPath, payloadName, "owned.bin")));
        }
        finally
        {
            await File.WriteAllBytesAsync(indexPath, originalIndex);
        }
    }

    [Fact]
    public async Task InvalidationSettlesAuthorityWhenThereWasNoPayloadToRetire()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("invalidation-empty-payload-repository");
        var key = Hash("invalidation-empty-payload-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        var settled = new EvidenceReceiptState(1, key, null, null, null, null, null, null, DateTimeOffset.UtcNow);
        EvidenceReceiptQuota.RecordState(root, repository, key, JsonSerializer.SerializeToUtf8Bytes(settled).Length, settled);
        var invalidated = new EvidenceReceiptState(1, key, null, null, null, null, null);

        EvidenceReceiptQuota.InvalidatePayload(lease, settled, invalidated);

        Assert.Equal(invalidated, lease.ReadState());
        var entry = Entry(root, key);
        Assert.Null(entry.GetProperty("PayloadDirectoryName").GetString());
        Assert.Equal(0, entry.GetProperty("PayloadBytes").GetInt64());
        Assert.True(entry.GetProperty("EssentialReserved").GetBoolean());
        Assert.True(entry.GetProperty("MetadataBytes").GetInt64() > 0);
        Assert.Null(lease.ReadState()?.LastSuccessfulUse);
        Assert.Empty(entry.GetProperty("RetiredPayloads").EnumerateArray());
    }

    [Fact]
    public void ReadOnlyQuotaReadsTreatMissingRootAsEmptyWithoutCreatingIt()
    {
        using var temporary = TestDirectory.Create();
        var missingRoot = TestPathUtils.PathUnder(temporary.Path, "missing", "receipt-root");
        var repository = Hash("readonly-missing-repository");

        Assert.Null(EvidenceReceiptQuota.ReadAuthority(missingRoot, repository, Hash("missing-key"), readOnly: true));
        Assert.Empty(EvidenceReceiptQuota.ReadComparable(missingRoot, repository, "producer", DateTimeOffset.UtcNow,
            readOnly: true));
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(temporary.Path, "missing")));
    }

    [Fact]
    public void ReadOnlyQuotaReadsRejectFileOrLinkedComponentsInMissingRootSuffix()
    {
        using var temporary = TestDirectory.Create();
        var repository = Hash("readonly-unsafe-repository");
        var fileComponent = TestPathUtils.PathUnder(temporary.Path, "not-a-directory");
        File.WriteAllText(fileComponent, "sentinel");
        var fileSuffix = TestPathUtils.PathUnder(fileComponent, "missing-root");

        Assert.Throws<IOException>(() => EvidenceReceiptQuota.ReadAuthority(fileSuffix, repository,
            Hash("file-component-key"), readOnly: true));
        Assert.Equal("sentinel", File.ReadAllText(fileComponent));

        var external = TestPathUtils.PathUnder(temporary.Path, "external");
        Directory.CreateDirectory(external);
        var externalSentinel = TestPathUtils.PathUnder(external, "keep.txt");
        File.WriteAllText(externalSentinel, "keep");
        var link = TestPathUtils.PathUnder(temporary.Path, "linked-component");
        try { Directory.CreateSymbolicLink(link, external); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { throw new InvalidOperationException("The read-only quota symlink-safety fixture requires directory-symlink capability on this host.", exception); }
        var linkedSuffix = TestPathUtils.PathUnder(link, "missing-root");
        Assert.Throws<IOException>(() => EvidenceReceiptQuota.ReadComparable(linkedSuffix, repository, "producer",
            DateTimeOffset.UtcNow, readOnly: true));
        Assert.True(File.Exists(externalSentinel));
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(external, "missing-root")));
    }

    [Fact]
    public async Task ReadOnlyQuotaReadsTreatAnExistingRootWithoutAnIndexAsEmptyWithoutPublishingOne()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("readonly-empty-index-repository");
        var key = Hash("readonly-empty-index-key");
        using (CoverageRunOutputLease.AcquireArtifactDirectory(root, createMissing: true, requirePrivate: true)) { }
        var rootEntriesBefore = Directory.EnumerateFileSystemEntries(root).ToArray();

        Assert.Null(EvidenceReceiptQuota.ReadAuthority(root, repository, key, readOnly: true));
        Assert.Empty(EvidenceReceiptQuota.ReadComparable(root, repository, "producer", DateTimeOffset.UtcNow,
            readOnly: true));

        Assert.False(File.Exists(TestPathUtils.PathUnder(root, "quota-index.json")));
        Assert.Equal(rootEntriesBefore, Directory.EnumerateFileSystemEntries(root).ToArray());
    }

    [Fact]
    public async Task ReadOnlyComparableSnapshotReturnsOnlyConsistentCurrentSettledCandidatesInOrder()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("readonly-comparable-repository");
        var producer = "readonly-comparable-producer";
        var now = DateTimeOffset.UtcNow;
        var older = now.AddMinutes(-2);
        var newer = now.AddMinutes(-1);
        var matchingDigest = Hash("readonly-comparable-input");

        EvidenceReceiptQuotaEntry Candidate(string key, DateTimeOffset completedAt, string digest, string stateDigest,
            bool inFlight = false)
        {
            var payloadName = "payload-" + key[..12];
            var state = new EvidenceReceiptState(1, key, inFlight ? "unfinished" : null, null,
                Hash("origin-" + key), completedAt, payloadName, Convert.ToBase64String([1]), null, false, null)
            { InputIdentityDigest = stateDigest };
            var reservationIds = inFlight ? new[] { key[..32] } : Array.Empty<string>();
            var metadataBytes = inFlight
                ? Math.Max(0, EvidenceReceiptQuota.EssentialReservationBytes - JsonSerializer.SerializeToUtf8Bytes(state).Length)
                : 0;
            return new EvidenceReceiptQuotaEntry(repository, key, metadataBytes, 5, 0,
                EvidenceReceiptQuota.EssentialReservationBytes, inFlight, inFlight, false,
                payloadName, completedAt, null, false, state, [], producer, digest, reservationIds, []);
        }

        var firstKey = Hash("readonly-comparable-first");
        var secondKey = Hash("readonly-comparable-second");
        var expiredKey = Hash("readonly-comparable-expired");
        var futureKey = Hash("readonly-comparable-future");
        var mismatchedKey = Hash("readonly-comparable-mismatched");
        var entries = new List<EvidenceReceiptQuotaEntry>
        {
            Candidate(firstKey, older, matchingDigest, matchingDigest),
            Candidate(secondKey, newer, matchingDigest, matchingDigest),
            Candidate(expiredKey, now.AddDays(-8), matchingDigest, matchingDigest),
            Candidate(futureKey, now.AddMinutes(1), matchingDigest, matchingDigest),
            Candidate(mismatchedKey, now, matchingDigest, Hash("different-state-input")),
            Candidate(Hash("readonly-comparable-inflight"), now, matchingDigest, matchingDigest, inFlight: true)
        };
        using (var rootLease = CoverageRunOutputLease.AcquireArtifactDirectory(root, createMissing: true, requirePrivate: true)!)
        using (var namespaceRoot = rootLease.CreateArtifactDirectory(repository))
        {
            foreach (var entry in entries)
            {
                using var keyRoot = namespaceRoot.CreateArtifactDirectory(entry.Key);
                using var payload = keyRoot.CreateArtifactDirectory(entry.PayloadDirectoryName!);
                using var file = payload.OpenArtifactFile("owned.bin", createNew: true);
                file.Write(new byte[5]);
                file.Flush(flushToDisk: true);
            }
            using var indexFile = rootLease.OpenArtifactFile("quota-index.json", createNew: true);
            indexFile.Write(JsonSerializer.SerializeToUtf8Bytes(new EvidenceReceiptQuotaIndex(4, entries)));
            indexFile.Flush(flushToDisk: true);
        }
        var indexPath = TestPathUtils.PathUnder(root, "quota-index.json");
        var indexBefore = await File.ReadAllBytesAsync(indexPath);
        var rootEntriesBefore = Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal).ToArray();

        var candidates = EvidenceReceiptQuota.ReadComparable(root, repository, producer, now, readOnly: true);

        Assert.Equal(new[] { secondKey, firstKey }, candidates.Select(candidate => candidate.Key));
        Assert.Equal(new[] { "payload-" + secondKey[..12], "payload-" + firstKey[..12] },
            candidates.Select(candidate => candidate.PayloadDirectoryName));
        Assert.All(candidates, candidate => Assert.Equal(matchingDigest, candidate.InputIdentityDigest));
        Assert.Equal(indexBefore, await File.ReadAllBytesAsync(indexPath));
        Assert.Equal(rootEntriesBefore, Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaintenanceReconcilesOnlyTheBoundStageActualBytesAndPreservesLiveExecution(bool createStage)
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("stage-only-reconciliation-repository-" + createStage);
        var key = Hash("stage-only-reconciliation-key-" + createStage);
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        var reservationId = Guid.NewGuid().ToString("N");
        var stageName = "payload-stage-" + reservationId;
        var payloadName = "payload-" + reservationId;
        using (var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None)))
        {
            lease.EstablishAuthority("stage-only-live-execution");
            Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 4096, DateTimeOffset.UtcNow,
                reservationId, stageName, payloadName));
            if (createStage)
            {
                using var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(lease.DirectoryPath, false, true)!;
                using var stage = keyRoot.CreateArtifactDirectory(stageName);
                using var file = stage.OpenArtifactFile("partial.bin", createNew: true);
                file.Write(new byte[37]);
                file.Flush(flushToDisk: true);
            }
        }

        EvidenceReceiptQuota.Maintain(root, DateTimeOffset.UtcNow);

        var reconciled = Entry(root, key);
        Assert.Equal(createStage ? 37 : 0, reconciled.GetProperty("ReservedPayloadBytes").GetInt64());
        Assert.Equal("stage-only-live-execution",
            reconciled.GetProperty("AuthorityState").GetProperty("InFlightExecutionId").GetString());
        Assert.True(reconciled.GetProperty("HasInFlight").GetBoolean());
        var reservations = reconciled.GetProperty("PayloadReservations").EnumerateArray().ToArray();
        if (createStage)
        {
            var reservation = Assert.Single(reservations);
            Assert.Equal(reservationId, reservation.GetProperty("ReservationId").GetString());
            Assert.Equal(37, reservation.GetProperty("ReservedBytes").GetInt64());
            Assert.True(Directory.Exists(TestPathUtils.PathUnder(root, repository, key, stageName)));
        }
        else
        {
            Assert.Empty(reservations);
            Assert.False(Directory.Exists(TestPathUtils.PathUnder(root, repository, key, stageName)));
        }
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(root, repository, key, payloadName)));
    }

    [Fact]
    public async Task MaintenanceKeepsLegacyUnboundReservationChargeAndDoesNotTreatItAsDeletionAuthority()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("legacy-unbound-repository");
        var key = Hash("legacy-unbound-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using (var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None)))
            Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 900_000_000, DateTimeOffset.UtcNow));
        var keyPath = TestPathUtils.PathUnder(root, repository, key);
        var unrelatedPayload = TestPathUtils.PathUnder(keyPath, "payload-unbound-foreign");
        using (var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(keyPath, false, true)!)
        using (keyRoot.CreateArtifactDirectory("payload-unbound-foreign")) { }

        EvidenceReceiptQuota.Maintain(root, DateTimeOffset.UtcNow.AddDays(20));

        var preserved = Entry(root, key);
        Assert.Equal(900_000_000, preserved.GetProperty("ReservedPayloadBytes").GetInt64());
        Assert.True(Directory.Exists(unrelatedPayload));
        var isEvictionPending = preserved.TryGetProperty("EvictionPending", out var evictionPending)
            && evictionPending.GetBoolean();
        Assert.False(isEvictionPending);
    }

    [Fact]
    public async Task MaintenanceMeasuresFinalPayloadOnlyAndPreservesUnknownExecutionAuthority()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("final-only-reservation-repository");
        var key = Hash("final-only-reservation-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        var reservationId = Guid.NewGuid().ToString("N");
        var stageName = "payload-stage-" + reservationId;
        var payloadName = "payload-" + reservationId;
        using (var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None)))
        {
            lease.EstablishAuthority("final-only-unknown-execution");
            Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 4096, DateTimeOffset.UtcNow,
                reservationId, stageName, payloadName));
            using var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(lease.DirectoryPath, false, true)!;
            using var payload = keyRoot.CreateArtifactDirectory(payloadName);
            using var file = payload.OpenArtifactFile("measured.bin", createNew: true);
            file.Write(new byte[83]);
            file.Flush(flushToDisk: true);
        }

        EvidenceReceiptQuota.Maintain(root, DateTimeOffset.UtcNow);

        var repaired = Entry(root, key);
        Assert.Equal(83, repaired.GetProperty("ReservedPayloadBytes").GetInt64());
        Assert.Equal(83, Assert.Single(repaired.GetProperty("PayloadReservations").EnumerateArray())
            .GetProperty("ReservedBytes").GetInt64());
        Assert.True(repaired.GetProperty("HasInFlight").GetBoolean());
        Assert.Equal("final-only-unknown-execution",
            repaired.GetProperty("AuthorityState").GetProperty("InFlightExecutionId").GetString());
        Assert.True(Directory.Exists(TestPathUtils.PathUnder(root, repository, key, payloadName)));
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(root, repository, key, stageName)));
    }

    [Fact]
    public async Task MaintenanceKeepsFullBoundChargeWhenStageContainsLinkedEntry()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("linked-stage-repository");
        var key = Hash("linked-stage-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        var reservationId = Guid.NewGuid().ToString("N");
        var stageName = "payload-stage-" + reservationId;
        var payloadName = "payload-" + reservationId;
        var sentinelPath = TestPathUtils.PathUnder(temporary.Path, "linked-stage-target.txt");
        var sentinelBytes = Encoding.UTF8.GetBytes("external target remains unchanged");
        await File.WriteAllBytesAsync(sentinelPath, sentinelBytes);
        string stagePath;
        using (var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None)))
        {
            Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 4096, DateTimeOffset.UtcNow,
                reservationId, stageName, payloadName));
            using (var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(lease.DirectoryPath, false, true)!)
            using (keyRoot.CreateArtifactDirectory(stageName)) { }
            stagePath = TestPathUtils.PathUnder(lease.DirectoryPath, stageName);
            try { File.CreateSymbolicLink(TestPathUtils.PathUnder(stagePath, "foreign-link"), sentinelPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            { throw new InvalidOperationException("The bound-stage safety fixture requires file-symlink capability on this host.", exception); }
        }

        EvidenceReceiptQuota.Maintain(root, DateTimeOffset.UtcNow);

        var preserved = Entry(root, key);
        Assert.Equal(4096, preserved.GetProperty("ReservedPayloadBytes").GetInt64());
        Assert.Equal(reservationId, Assert.Single(preserved.GetProperty("PayloadReservations").EnumerateArray())
            .GetProperty("ReservationId").GetString());
        Assert.True(File.Exists(TestPathUtils.PathUnder(stagePath, "foreign-link")));
        Assert.Equal(sentinelBytes, await File.ReadAllBytesAsync(sentinelPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaintenanceReleasesRetiredPayloadChargeOnlyAfterOwnedTreeDeletion(bool containsForeignLink)
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("retired-cleanup-repository-" + containsForeignLink);
        var key = Hash("retired-cleanup-key-" + containsForeignLink);
        var payloadName = "payload-retired-" + Guid.NewGuid().ToString("N");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        string payloadPath;
        using (var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None)))
        {
            payloadPath = TestPathUtils.PathUnder(lease.DirectoryPath, payloadName);
            using (var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(lease.DirectoryPath, false, true)!)
            using (var payload = keyRoot.CreateArtifactDirectory(payloadName))
            using (var file = payload.OpenArtifactFile("owned.bin", createNew: true))
            {
                file.Write(new byte[64]);
                file.Flush(flushToDisk: true);
            }

            if (containsForeignLink)
            {
                var outside = TestPathUtils.PathUnder(temporary.Path, "foreign-target.txt");
                await File.WriteAllTextAsync(outside, "external sentinel");
                try { File.CreateSymbolicLink(TestPathUtils.PathUnder(payloadPath, "foreign-link"), outside); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                { throw new InvalidOperationException("Retired-payload reconciliation requires file-symlink capability on this host.", exception); }
            }

            var completedAt = DateTimeOffset.UtcNow;
            Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 128, completedAt));
            EvidenceReceiptQuota.CommitPayload(root, repository, key, 128, payloadName, completedAt, stateBytes: 0);
            var state = new EvidenceReceiptState(1, key, null, null, Hash("retired-origin"), completedAt,
                payloadName, null, completedAt, false, null);
            EvidenceReceiptQuota.RecordState(root, repository, key,
                JsonSerializer.SerializeToUtf8Bytes(state).Length, state);
            var invalidated = new EvidenceReceiptState(1, key, null, null, null, null, null, null, null, false, null);
            EvidenceReceiptQuota.InvalidatePayload(lease, state, invalidated);
        }

        var retired = Entry(root, key);
        Assert.Equal(128, Assert.Single(retired.GetProperty("RetiredPayloads").EnumerateArray())
            .GetProperty("Bytes").GetInt64());
        EvidenceReceiptQuota.Maintain(root, DateTimeOffset.UtcNow);

        if (containsForeignLink)
        {
            Assert.Equal(128, Entry(root, key).GetProperty("RetiredPayloads").EnumerateArray()
                .Single().GetProperty("Bytes").GetInt64());
            Assert.True(Directory.Exists(payloadPath));
            File.Delete(TestPathUtils.PathUnder(payloadPath, "foreign-link"));
            EvidenceReceiptQuota.Maintain(root, DateTimeOffset.UtcNow);
        }

        var reclaimed = Entry(root, key);
        Assert.Empty(reclaimed.GetProperty("RetiredPayloads").EnumerateArray());
        Assert.False(Directory.Exists(payloadPath));
        Assert.Equal(0, reclaimed.GetProperty("PayloadBytes").GetInt64());
    }

    [Fact]
    public async Task InvalidOrAmbiguousByteReleaseLeavesPersistedReservationsUntouched()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("ambiguous-quota-repository");
        var key = Hash("ambiguous-quota-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));

        var firstId = Guid.NewGuid().ToString("N");
        var secondId = Guid.NewGuid().ToString("N");
        Assert.False(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 0, DateTimeOffset.UtcNow));
        Assert.False(EvidenceReceiptQuota.TryReservePayload(root, repository, key,
            EvidenceReceiptQuota.MaximumBytes + 1, DateTimeOffset.UtcNow));
        Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 101, DateTimeOffset.UtcNow,
            firstId, "payload-stage-" + firstId, "payload-" + firstId));
        Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 101, DateTimeOffset.UtcNow,
            secondId, "payload-stage-" + secondId, "payload-" + secondId));

        EvidenceReceiptQuota.ReleasePayloadReservation(root, repository, key, 101);
        var unchanged = Entry(root, key);
        Assert.Equal(202, unchanged.GetProperty("ReservedPayloadBytes").GetInt64());
        Assert.Equal(2, unchanged.GetProperty("PayloadReservations").GetArrayLength());
    }

    [Fact]
    public async Task InvalidationCannotClearLiveExecutionAuthorityOrAStaleReceiptSnapshot()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("invalidation-guard-repository");
        var key = Hash("invalidation-guard-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        var invalidated = new EvidenceReceiptState(1, key, null, null, null, null, null);

        lease.EstablishAuthority("active-invalidation-owner");
        var active = lease.ReadState()!;
        var activeIndex = await File.ReadAllBytesAsync(TestPathUtils.PathUnder(root, "quota-index.json"));
        EvidenceReceiptQuota.InvalidatePayload(lease, active, invalidated);
        Assert.Equal(activeIndex, await File.ReadAllBytesAsync(TestPathUtils.PathUnder(root, "quota-index.json")));
        Assert.Equal("active-invalidation-owner", lease.ReadState()?.InFlightExecutionId);

        var payloadName = "payload-invalidation-guard";
        var completedAt = DateTimeOffset.UtcNow;
        using (var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(lease.DirectoryPath, false, true)!)
        using (var payload = keyRoot.CreateArtifactDirectory(payloadName))
        using (var file = payload.OpenArtifactFile("owned.bin", createNew: true))
        {
            file.Write(new byte[] { 4, 5, 6 });
            file.Flush(flushToDisk: true);
        }
        Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 16, completedAt));
        EvidenceReceiptQuota.CommitPayload(root, repository, key, 3, payloadName, completedAt, stateBytes: 0);
        var passed = EvidenceCanonicalJson.Serialize(new EvidenceProducerResult("producer",
            EvidenceProducerOutcome.Passed, ["assertion"]));
        lease.CommitPassed("active-invalidation-owner", Hash("invalidation-origin"), completedAt, payloadName, passed);
        var settled = lease.ReadState()!;
        var stale = settled with { OriginManifestDigest = Hash("stale-invalidation-origin") };
        var settledIndex = await File.ReadAllBytesAsync(TestPathUtils.PathUnder(root, "quota-index.json"));

        EvidenceReceiptQuota.InvalidatePayload(lease, stale, invalidated);

        Assert.Equal(settledIndex, await File.ReadAllBytesAsync(TestPathUtils.PathUnder(root, "quota-index.json")));
        Assert.Equal(settled, lease.ReadState());
        Assert.Equal(payloadName, Entry(root, key).GetProperty("PayloadDirectoryName").GetString());
        Assert.Equal(3, Entry(root, key).GetProperty("PayloadBytes").GetInt64());
        Assert.Empty(Entry(root, key).GetProperty("RetiredPayloads").EnumerateArray());
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(
            TestPathUtils.PathUnder(lease.DirectoryPath, payloadName, "owned.bin")));
    }

    [Fact]
    public async Task ReconciliationKeepsFullChargeWhenStageAndFinalPayloadBothExist()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("ambiguous-rename-repository");
        var key = Hash("ambiguous-rename-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        var reservationId = Guid.NewGuid().ToString("N");
        var stageName = "payload-stage-" + reservationId;
        var payloadName = "payload-" + reservationId;
        using (var initial = Assert.Single(await store.AcquireAsync([key], CancellationToken.None)))
        {
            Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 4096, DateTimeOffset.UtcNow,
                reservationId, stageName, payloadName));
            using var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(initial.DirectoryPath, false, true)!;
            using (keyRoot.CreateArtifactDirectory(stageName)) { }
            using (keyRoot.CreateArtifactDirectory(payloadName)) { }
        }

        using var next = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        var entry = Entry(root, key);
        Assert.Equal(4096, entry.GetProperty("ReservedPayloadBytes").GetInt64());
        Assert.Equal(4096, Assert.Single(entry.GetProperty("PayloadReservations").EnumerateArray())
            .GetProperty("ReservedBytes").GetInt64());
        Assert.True(Directory.Exists(TestPathUtils.PathUnder(next.DirectoryPath, stageName)));
        Assert.True(Directory.Exists(TestPathUtils.PathUnder(next.DirectoryPath, payloadName)));
    }

    public static IEnumerable<object[]> IndexCorruptions => new[]
    {
        "duplicate-entry", "null-entries", "null-entry", "invalid-repository", "invalid-key", "invalid-input-hash",
        "producer-empty", "producer-long", "producer-control",
        "negative-metadata", "negative-payload", "negative-reserved",
        "retired-null", "retired-over-capacity", "retired-negative", "retired-bad-name",
        "retired-null-entry", "retired-null-name", "retired-invalid-component",
        "active-empty-name", "active-invalid-component", "active-stage-name", "retired-stage-name",
        "active-stage-case-name", "retired-stage-case-name",
        "retired-active-alias", "retired-active-case-alias", "retired-reservation-alias", "retired-reservation-case-alias",
        "active-reservation-alias", "active-reservation-case-alias",
        "essential-null", "essential-null-id", "essential-over-capacity", "essential-bad-length", "essential-nonhex", "essential-duplicate",
        "payload-over-capacity", "payload-bad-id", "payload-bad-stage", "payload-bad-name",
        "payload-negative", "payload-sum-exceeds-charge", "payload-duplicate-id",
        "tombstone-authority", "tombstone-metadata", "tombstone-reserved", "tombstone-essential-bytes",
        "tombstone-essential-flag", "tombstone-flight-flag", "tombstone-audit-flag", "tombstone-quarantine-flag",
        "tombstone-origin-time", "tombstone-use-time", "tombstone-producer", "tombstone-input-hash",
        "tombstone-essential-id", "tombstone-payload-binding",
        "state-schema", "state-key", "state-audit-utf8-limit", "unsupported-index-schema", "oversized-index",
        "state-payload-empty", "state-payload-component", "state-payload-stage", "state-payload-stage-case"
    }.Select(corruption => new object[] { corruption });

    [Theory]
    [MemberData(nameof(IndexCorruptions))]
    public async Task ReadAuthorityRejectsDuplicateOrInvalidPersistedQuotaEntries(string corruption)
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("corrupt-index-repository");
        var key = Hash("corrupt-index-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));

        var usesPayloadReservation = corruption.StartsWith("payload-", StringComparison.Ordinal)
            || corruption == "tombstone-payload-binding";
        string? reservationId = null;
        if (usesPayloadReservation)
        {
            reservationId = Guid.NewGuid().ToString("N");
            Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 41, DateTimeOffset.UtcNow,
                reservationId, "payload-stage-" + reservationId, "payload-" + reservationId));
        }
        if (corruption.StartsWith("state-", StringComparison.Ordinal))
            lease.EstablishAuthority("index-corruption-owner");

        var indexPath = TestPathUtils.PathUnder(root, "quota-index.json");
        var originalIndexBytes = await File.ReadAllBytesAsync(indexPath);
        var outsideSentinel = TestPathUtils.PathUnder(temporary.Path, "outside-sentinel.txt");
        await File.WriteAllTextAsync(outsideSentinel, "must remain unchanged");
        var originalSentinel = await File.ReadAllBytesAsync(outsideSentinel);
        var corruptIndexBytes = corruption == "oversized-index"
            ? new byte[EvidenceReceiptQuota.IndexMaximumBytes + 1]
            : CreateCorruptIndexBytes(originalIndexBytes, corruption, key, reservationId);
        try
        {
            if (corruption.StartsWith("tombstone-", StringComparison.Ordinal))
            {
                var validTombstone = CreateCorruptIndexBytes(originalIndexBytes, "tombstone-baseline", key, null);
                await File.WriteAllBytesAsync(indexPath, validTombstone);
                Assert.Null(EvidenceReceiptQuota.ReadAuthority(root, repository, key, readOnly: true));
            }
            await File.WriteAllBytesAsync(indexPath, corruptIndexBytes);
            Assert.Throws<InvalidDataException>(() => EvidenceReceiptQuota.ReadAuthority(
                root, repository, key, readOnly: true));
            Assert.Equal(corruptIndexBytes, await File.ReadAllBytesAsync(indexPath));
            Assert.Equal(originalSentinel, await File.ReadAllBytesAsync(outsideSentinel));
        }
        finally
        {
            // The active lease reconciles quota state when disposed; restore this test-owned
            // index first so cleanup can release its lock normally without masking the assertion.
            await File.WriteAllBytesAsync(indexPath, originalIndexBytes);
        }
    }

    private static byte[] CreateCorruptIndexBytes(byte[] originalIndexBytes, string corruption, string key,
        string? reservationId)
    {
        var index = JsonNode.Parse(originalIndexBytes)!;
        var entries = index["Entries"]!.AsArray();
        var entry = entries[0]!;
        var validId = reservationId ?? "0123456789abcdef0123456789abcdef";
        var validStage = "payload-stage-" + validId;
        var validPayload = "payload-" + validId;
        var tombstoneCorruption = corruption.StartsWith("tombstone-", StringComparison.Ordinal);
        var stateCorruption = corruption.StartsWith("state-", StringComparison.Ordinal);

        if (tombstoneCorruption)
        {
            entry["EvictionPending"] = true;
            entry["AuthorityState"] = null;
            entry["MetadataBytes"] = 0;
            entry["ReservedPayloadBytes"] = 0;
            entry["EssentialReservationBytes"] = 0;
            entry["EssentialReserved"] = false;
            entry["HasInFlight"] = false;
            entry["HasAuditIntent"] = false;
            entry["Quarantined"] = false;
            entry["OriginCompletedAt"] = null;
            entry["LastSuccessfulUse"] = null;
            entry["ProducerId"] = null;
            entry["InputIdentityDigest"] = null;
            entry["EssentialReservationIds"] = new JsonArray();
            entry["PayloadReservations"] = null;
        }

        if (stateCorruption)
        {
            var state = JsonNode.Parse(entry["AuthorityState"]!.ToJsonString())!;
            if (corruption == "state-schema") state["SchemaVersion"] = 2;
            else if (corruption == "state-key") state["Key"] = Hash("different-state-key");
            else if (corruption.StartsWith("state-payload-", StringComparison.Ordinal))
                state["PayloadDirectoryName"] = corruption switch
                {
                    "state-payload-empty" => "",
                    "state-payload-component" => "payload-x/../../outside",
                    "state-payload-stage" => validStage,
                    _ => validStage.Replace("stage", "STAGE", StringComparison.Ordinal),
                };
            else state["AuditExpectedAssertions"] = new string('x', 4097);
            entry["AuthorityState"] = state;
        }

        switch (corruption)
        {
            case "duplicate-entry": entries.Add(entry.DeepClone()); break;
            case "null-entries": index["Entries"] = null; break;
            case "null-entry": entries[0] = null; break;
            case "invalid-repository": entry["RepositoryNamespace"] = new string('g', 64); break;
            case "invalid-key": entry["Key"] = "short"; break;
            case "invalid-input-hash": entry["InputIdentityDigest"] = "not-a-hash"; break;
            case "producer-empty": entry["ProducerId"] = ""; break;
            case "producer-long": entry["ProducerId"] = new string('p', 257); break;
            case "producer-control": entry["ProducerId"] = "producer\u0001id"; break;
            case "negative-metadata": entry["MetadataBytes"] = -1; break;
            case "negative-payload": entry["PayloadBytes"] = -1; break;
            case "negative-reserved": entry["ReservedPayloadBytes"] = -1; break;
            case "retired-null": entry["RetiredPayloads"] = null; break;
            case "retired-over-capacity": entry["RetiredPayloads"] = RetiredPayloads(65); break;
            case "retired-negative": entry["RetiredPayloads"] = new JsonArray(JsonSerializer.SerializeToNode(new { DirectoryName = "payload-old", Bytes = -1L })); break;
            case "retired-bad-name": entry["RetiredPayloads"] = new JsonArray(JsonSerializer.SerializeToNode(new { DirectoryName = "foreign-old", Bytes = 1L })); break;
            case "retired-null-entry": entry["RetiredPayloads"] = new JsonArray((JsonNode?)null); break;
            case "retired-null-name": entry["RetiredPayloads"] = new JsonArray(JsonSerializer.SerializeToNode(new { DirectoryName = (string?)null, Bytes = 1L })); break;
            case "retired-invalid-component": entry["RetiredPayloads"] = new JsonArray(JsonSerializer.SerializeToNode(new { DirectoryName = "payload-x/../../outside", Bytes = 1L })); break;
            case "active-empty-name": entry["PayloadDirectoryName"] = ""; break;
            case "active-invalid-component": entry["PayloadDirectoryName"] = "payload-x/../../outside"; break;
            case "active-stage-name": entry["PayloadDirectoryName"] = validStage; break;
            case "active-stage-case-name": entry["PayloadDirectoryName"] = validStage.Replace("stage", "STAGE", StringComparison.Ordinal); break;
            case "retired-stage-name": entry["RetiredPayloads"] = new JsonArray(JsonSerializer.SerializeToNode(new { DirectoryName = validStage, Bytes = 1L })); break;
            case "retired-stage-case-name": entry["RetiredPayloads"] = new JsonArray(JsonSerializer.SerializeToNode(new { DirectoryName = validStage.Replace("stage", "STAGE", StringComparison.Ordinal), Bytes = 1L })); break;
            case "retired-active-alias":
            case "retired-active-case-alias":
                entry["PayloadDirectoryName"] = "payload-live";
                entry["RetiredPayloads"] = new JsonArray(JsonSerializer.SerializeToNode(new
                { DirectoryName = corruption.EndsWith("case-alias", StringComparison.Ordinal) ? "payload-LIVE" : "payload-live", Bytes = 1L }));
                break;
            case "active-reservation-alias":
            case "active-reservation-case-alias":
                SetPayloadReservations(entry, validId, validStage, validPayload, 1);
                entry["ReservedPayloadBytes"] = 1;
                entry["PayloadDirectoryName"] = corruption.EndsWith("case-alias", StringComparison.Ordinal)
                    ? validPayload.ToUpperInvariant().Replace("PAYLOAD-", "payload-", StringComparison.Ordinal) : validPayload;
                break;
            case "retired-reservation-alias":
            case "retired-reservation-case-alias":
                SetPayloadReservations(entry, validId, validStage, validPayload, 1);
                entry["ReservedPayloadBytes"] = 1;
                entry["RetiredPayloads"] = new JsonArray(JsonSerializer.SerializeToNode(new
                { DirectoryName = corruption.EndsWith("case-alias", StringComparison.Ordinal) ? validPayload.ToUpperInvariant().Replace("PAYLOAD-", "payload-", StringComparison.Ordinal) : validPayload, Bytes = 1L }));
                break;
            case "essential-null": entry["EssentialReservationIds"] = null; break;
            case "essential-null-id": entry["EssentialReservationIds"] = new JsonArray((JsonNode?)null); break;
            case "essential-over-capacity": entry["EssentialReservationIds"] = new JsonArray(Enumerable.Range(0, 65).Select(i => JsonValue.Create(i.ToString("x32", System.Globalization.CultureInfo.InvariantCulture))).ToArray()); break;
            case "essential-bad-length": entry["EssentialReservationIds"] = new JsonArray(JsonValue.Create("short")); break;
            case "essential-nonhex": entry["EssentialReservationIds"] = new JsonArray(JsonValue.Create(new string('z', 32))); break;
            case "essential-duplicate": entry["EssentialReservationIds"] = new JsonArray(JsonValue.Create(validId), JsonValue.Create(validId)); break;
            case "payload-over-capacity": entry["PayloadReservations"] = PayloadReservations(65, validId); break;
            case "payload-bad-id": SetPayloadReservations(entry, "invalid-id", validStage, validPayload, 1); break;
            case "payload-bad-stage": SetPayloadReservations(entry, validId, "payload-stage-foreign", validPayload, 1); break;
            case "payload-bad-name": SetPayloadReservations(entry, validId, validStage, "payload-foreign", 1); break;
            case "payload-negative": SetPayloadReservations(entry, validId, validStage, validPayload, -1); break;
            case "payload-sum-exceeds-charge": SetPayloadReservations(entry, validId, validStage, validPayload, 42); entry["ReservedPayloadBytes"] = 41; break;
            case "payload-duplicate-id": SetDuplicatePayloadReservations(entry, validId); break;
            case "tombstone-authority": entry["AuthorityState"] = JsonSerializer.SerializeToNode(new EvidenceReceiptState(1, key, null, null, Hash("tombstone-state"), DateTimeOffset.UtcNow, null)); break;
            case "tombstone-metadata": entry["MetadataBytes"] = 1; break;
            case "tombstone-reserved": entry["ReservedPayloadBytes"] = 1; break;
            case "tombstone-essential-bytes": entry["EssentialReservationBytes"] = 1; break;
            case "tombstone-essential-flag": entry["EssentialReserved"] = true; break;
            case "tombstone-flight-flag": entry["HasInFlight"] = true; break;
            case "tombstone-audit-flag": entry["HasAuditIntent"] = true; break;
            case "tombstone-quarantine-flag": entry["Quarantined"] = true; break;
            case "tombstone-origin-time": entry["OriginCompletedAt"] = DateTimeOffset.UtcNow; break;
            case "tombstone-use-time": entry["LastSuccessfulUse"] = DateTimeOffset.UtcNow; break;
            case "tombstone-producer": entry["ProducerId"] = "producer"; break;
            case "tombstone-input-hash": entry["InputIdentityDigest"] = Hash("tombstone-input"); break;
            case "tombstone-essential-id": entry["EssentialReservationIds"] = new JsonArray(JsonValue.Create("0123456789abcdef0123456789abcdef")); break;
            case "tombstone-payload-binding": SetPayloadReservations(entry, validId, validStage, validPayload, 1); break;
            case "unsupported-index-schema": index["SchemaVersion"] = 5; break;
        }
        return Encoding.UTF8.GetBytes(index.ToJsonString());
    }

    private static JsonArray RetiredPayloads(int count)
        => new(Enumerable.Range(0, count).Select(i => JsonSerializer.SerializeToNode(
            new { DirectoryName = "payload-retired-" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture), Bytes = 1L })).ToArray());

    private static JsonArray PayloadReservations(int count, string seed)
        => new(Enumerable.Range(0, count).Select(i =>
        {
            var id = i == 0 ? seed : i.ToString("x32", System.Globalization.CultureInfo.InvariantCulture);
            return JsonSerializer.SerializeToNode(new
            {
                ReservationId = id,
                StageDirectoryName = "payload-stage-" + id,
                PayloadDirectoryName = "payload-" + id,
                ReservedBytes = 0L
            });
        }).ToArray());

    private static void SetPayloadReservations(JsonNode entry, string bindingId, string stageName,
        string payloadName, long bytes)
    {
        entry["PayloadReservations"] = new JsonArray(JsonSerializer.SerializeToNode(new
        {
            ReservationId = bindingId,
            StageDirectoryName = stageName,
            PayloadDirectoryName = payloadName,
            ReservedBytes = bytes
        }));
        entry["ReservedPayloadBytes"] = Math.Max(41, bytes);
    }

    private static void SetDuplicatePayloadReservations(JsonNode entry, string reservationId)
    {
        var reservation = JsonSerializer.SerializeToNode(new
        {
            ReservationId = reservationId,
            StageDirectoryName = "payload-stage-" + reservationId,
            PayloadDirectoryName = "payload-" + reservationId,
            ReservedBytes = 1L
        });
        entry["PayloadReservations"] = new JsonArray(reservation, reservation?.DeepClone());
        entry["ReservedPayloadBytes"] = 41;
    }

    [Fact]
    public async Task PayloadCommitCannotConsumeMoreBytesThanItsPersistedReservation()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("commit-bound-repository");
        var key = Hash("commit-bound-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        var reservationId = Guid.NewGuid().ToString("N");
        var payloadName = "payload-" + reservationId;
        Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 73, DateTimeOffset.UtcNow,
            reservationId, "payload-stage-" + reservationId, payloadName));

        Assert.Throws<EvidenceReceiptQuotaUnavailableException>(() => EvidenceReceiptQuota.CommitPayload(
            root, repository, key, 74, payloadName, DateTimeOffset.UtcNow, stateBytes: 19));
        var unchanged = Entry(root, key);
        Assert.Equal(73, unchanged.GetProperty("ReservedPayloadBytes").GetInt64());
        Assert.Equal(73, Assert.Single(unchanged.GetProperty("PayloadReservations").EnumerateArray())
            .GetProperty("ReservedBytes").GetInt64());
        Assert.Null(unchanged.GetProperty("PayloadDirectoryName").GetString());
    }

    [Fact]
    public async Task PayloadCommitRejectsNegativeBytesWithoutSpendingItsValidReservation()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("negative-commit-bytes-repository");
        var key = Hash("negative-commit-bytes-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        var reservationId = Guid.NewGuid().ToString("N");
        var payloadName = "payload-" + reservationId;
        Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 29, DateTimeOffset.UtcNow,
            reservationId, "payload-stage-" + reservationId, payloadName));
        var before = await File.ReadAllBytesAsync(TestPathUtils.PathUnder(root, "quota-index.json"));

        Assert.Throws<EvidenceReceiptQuotaUnavailableException>(() => EvidenceReceiptQuota.CommitPayload(root,
            repository, key, -1, payloadName, DateTimeOffset.UtcNow, stateBytes: 0));

        Assert.Equal(before, await File.ReadAllBytesAsync(TestPathUtils.PathUnder(root, "quota-index.json")));
        var unchanged = Entry(root, key);
        Assert.Equal(29, unchanged.GetProperty("ReservedPayloadBytes").GetInt64());
        Assert.Equal(29, Assert.Single(unchanged.GetProperty("PayloadReservations").EnumerateArray())
            .GetProperty("ReservedBytes").GetInt64());
        Assert.Null(unchanged.GetProperty("PayloadDirectoryName").GetString());
        Assert.Equal(0, unchanged.GetProperty("PayloadBytes").GetInt64());
    }

    [Fact]
    public async Task SuccessfulPayloadCommitMovesReservedBytesIntoCommittedPayloadAccounting()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("successful-payload-commit-repository");
        var key = Hash("successful-payload-commit-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 500, DateTimeOffset.UtcNow));
        var completedAt = DateTimeOffset.UtcNow;

        EvidenceReceiptQuota.CommitPayload(root, repository, key, 321, "payload-committed", completedAt, stateBytes: 88);

        var committed = Entry(root, key);
        Assert.Equal(0, committed.GetProperty("ReservedPayloadBytes").GetInt64());
        Assert.Equal(321, committed.GetProperty("PayloadBytes").GetInt64());
        Assert.Equal("payload-committed", committed.GetProperty("PayloadDirectoryName").GetString());
        Assert.Equal(completedAt, committed.GetProperty("OriginCompletedAt").GetDateTimeOffset());
        Assert.Equal(88, committed.GetProperty("MetadataBytes").GetInt64());
        Assert.False(committed.GetProperty("EssentialReserved").GetBoolean());
    }

    [Fact]
    public async Task EssentialReservationReleasePreservesPersistedAuthorityWhenFlightFlagDisagrees()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("release-mismatched-authority-repository");
        var key = Hash("release-mismatched-authority-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        lease.EstablishAuthority("mismatched-flag-owner");

        var indexPath = TestPathUtils.PathUnder(root, "quota-index.json");
        var index = JsonNode.Parse(await File.ReadAllBytesAsync(indexPath))!;
        var entry = index["Entries"]!.AsArray().Single()!;
        entry["HasInFlight"] = false;
        await File.WriteAllBytesAsync(indexPath, Encoding.UTF8.GetBytes(index.ToJsonString()));
        var inconsistentBytes = await File.ReadAllBytesAsync(indexPath);

        EvidenceReceiptQuota.ReleaseEssentialReservation(root, repository, key, lease.EssentialReservationId);

        Assert.Equal(inconsistentBytes, await File.ReadAllBytesAsync(indexPath));
        using var persisted = JsonDocument.Parse(await File.ReadAllBytesAsync(indexPath));
        var persistedEntry = Assert.Single(persisted.RootElement.GetProperty("Entries").EnumerateArray());
        Assert.False(persistedEntry.GetProperty("HasInFlight").GetBoolean());
        Assert.Equal("mismatched-flag-owner",
            persistedEntry.GetProperty("AuthorityState").GetProperty("InFlightExecutionId").GetString());
    }

    [Fact]
    public async Task ReleasingReservationDoesNotEraseInFlightAuthority()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("release-authority-repository");
        var key = Hash("release-authority-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        var reservationId = lease.EssentialReservationId;
        lease.EstablishAuthority("protected-execution");

        EvidenceReceiptQuota.ReleaseEssentialReservation(root, repository, key, reservationId);

        var entry = Entry(root, key);
        Assert.True(entry.GetProperty("HasInFlight").GetBoolean());
        Assert.True(entry.GetProperty("EssentialReserved").GetBoolean());
        Assert.Equal("protected-execution", lease.ReadState()?.InFlightExecutionId);
    }

    [Theory]
    [InlineData("wrong-stage")]
    [InlineData("wrong-payload")]
    public async Task BoundReservationRejectsNamesThatDoNotMatchItsIdentifier(string mismatch)
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("invalid-binding-repository");
        var key = Hash("invalid-binding-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        var reservationId = Guid.NewGuid().ToString("N");

        Assert.Throws<ArgumentException>(() => EvidenceReceiptQuota.TryReservePayload(root, repository, key, 50,
            DateTimeOffset.UtcNow, reservationId,
            mismatch == "wrong-stage" ? "payload-stage-foreign" : "payload-stage-" + reservationId,
            mismatch == "wrong-payload" ? "payload-foreign" : "payload-" + reservationId));
        Assert.Equal(0, Entry(root, key).GetProperty("ReservedPayloadBytes").GetInt64());
        var reservations = Entry(root, key).GetProperty("PayloadReservations");
        Assert.True(reservations.ValueKind == JsonValueKind.Null
            || reservations.ValueKind == JsonValueKind.Array && reservations.GetArrayLength() == 0);
    }

    [Fact]
    public async Task EssentialReservationWaitBudgetRejectsOtherNegativeValuesAndAcceptsCappedLongBudgets()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("wait-budget-repository");
        var key = Hash("wait-budget-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        var indexPath = TestPathUtils.PathUnder(root, "quota-index.json");
        var originalIndex = await File.ReadAllBytesAsync(indexPath);

        Assert.Throws<ArgumentOutOfRangeException>(() => EvidenceReceiptQuota.ReserveEssential(root,
            repository, [key], DateTimeOffset.UtcNow, waitTimeout: TimeSpan.FromMilliseconds(-2)));

        Assert.Equal(originalIndex, await File.ReadAllBytesAsync(indexPath));
        var infiniteWaitReservation = EvidenceReceiptQuota.ReserveEssential(root, repository, [key],
            DateTimeOffset.UtcNow, waitTimeout: Timeout.InfiniteTimeSpan);
        var oversizedWaitReservation = EvidenceReceiptQuota.ReserveEssential(root, repository, [key],
            DateTimeOffset.UtcNow, waitTimeout: TimeSpan.FromHours(1));

        var reservations = Entry(root, key).GetProperty("EssentialReservationIds").EnumerateArray()
            .Select(item => item.GetString()!).ToArray();
        Assert.Contains(infiniteWaitReservation, reservations);
        Assert.Contains(oversizedWaitReservation, reservations);
        Assert.Equal(3, reservations.Length);
    }

    [Fact]
    public async Task FailedBatchAuthorityValidationDoesNotPersistEarlierInMemoryCommits()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("atomic-batch-repository");
        var firstKey = Hash("atomic-batch-first");
        var secondKey = Hash("atomic-batch-second");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        var leases = await store.AcquireAsync([firstKey, secondKey], CancellationToken.None);
        try
        {
            var first = leases.Single(lease => lease.Key == firstKey);
            var second = leases.Single(lease => lease.Key == secondKey);
            first.EstablishAuthority("batch-owner");
            second.EstablishAuthority("batch-owner");
            var firstId = Guid.NewGuid().ToString("N");
            var secondId = Guid.NewGuid().ToString("N");
            Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, firstKey, 90, DateTimeOffset.UtcNow,
                firstId, "payload-stage-" + firstId, "payload-" + firstId));
            Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, secondKey, 90, DateTimeOffset.UtcNow,
                secondId, "payload-stage-" + secondId, "payload-" + secondId));
            var completedAt = DateTimeOffset.UtcNow;
            var firstState = first.ReadState()! with
            { InFlightExecutionId = null, OriginCompletedAt = completedAt, PayloadDirectoryName = "payload-" + firstId };
            var secondState = second.ReadState()! with
            { InFlightExecutionId = null, OriginCompletedAt = completedAt, PayloadDirectoryName = "payload-" + secondId };
            var commits = new[]
            {
                new EvidenceReceiptBatchCommit(repository, firstKey, "first-producer", "batch-owner", firstState, 120,
                    "payload-" + firstId, completedAt, 40, 90, first.EssentialReservationId),
                new EvidenceReceiptBatchCommit(repository, secondKey, "second-producer", "wrong-owner", secondState, 120,
                    "payload-" + secondId, completedAt, 40, 90, second.EssentialReservationId)
            };

            Assert.Throws<InvalidOperationException>(() => EvidenceReceiptQuota.CommitInvocation(root, commits));
            Assert.Equal("batch-owner", first.ReadState()?.InFlightExecutionId);
            Assert.Equal("batch-owner", second.ReadState()?.InFlightExecutionId);
            Assert.Null(Entry(root, firstKey).GetProperty("PayloadDirectoryName").GetString());
            Assert.Null(Entry(root, secondKey).GetProperty("PayloadDirectoryName").GetString());
            Assert.Equal(90, Entry(root, firstKey).GetProperty("ReservedPayloadBytes").GetInt64());
            Assert.Equal(90, Entry(root, secondKey).GetProperty("ReservedPayloadBytes").GetInt64());
        }
        finally
        {
            foreach (var lease in leases.Reverse()) lease.Dispose();
        }
    }

    [Fact]
    public async Task ExplicitEmptyLegacyPayloadBindingsCommitTheAggregateReservation()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("empty-legacy-bindings-repository");
        var key = Hash("empty-legacy-bindings-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        var completedAt = DateTimeOffset.UtcNow;
        Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 93, completedAt));

        var indexPath = TestPathUtils.PathUnder(root, "quota-index.json");
        var index = JsonNode.Parse(await File.ReadAllBytesAsync(indexPath))!;
        var entry = index["Entries"]!.AsArray().Single()!;
        entry["PayloadReservations"] = new JsonArray();
        await File.WriteAllBytesAsync(indexPath, Encoding.UTF8.GetBytes(index.ToJsonString()));

        EvidenceReceiptQuota.CommitPayload(root, repository, key, 71, "payload-empty-legacy-commit", completedAt,
            stateBytes: 22);

        var committed = Entry(root, key);
        Assert.Equal(0, committed.GetProperty("ReservedPayloadBytes").GetInt64());
        Assert.Equal(71, committed.GetProperty("PayloadBytes").GetInt64());
        Assert.Equal(22, committed.GetProperty("MetadataBytes").GetInt64());
        Assert.Equal("payload-empty-legacy-commit", committed.GetProperty("PayloadDirectoryName").GetString());
        Assert.Equal(completedAt, committed.GetProperty("OriginCompletedAt").GetDateTimeOffset());
        Assert.Empty(committed.GetProperty("PayloadReservations").EnumerateArray());
    }

    [Fact]
    public async Task LegacyBatchCommitConsumesOnlyItsDeclaredAggregateReservation()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("legacy-batch-commit-repository");
        var key = Hash("legacy-batch-commit-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        const string executionId = "legacy-batch-commit-owner";
        lease.EstablishAuthority(executionId);
        var completedAt = DateTimeOffset.UtcNow;
        const string payloadName = "payload-legacy-batch-commit";
        Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 100, completedAt));
        using (var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(lease.DirectoryPath, false, true)!)
        using (var payload = keyRoot.CreateArtifactDirectory(payloadName))
        using (var file = payload.OpenArtifactFile("owned.bin", createNew: true))
        {
            file.Write(new byte[37]);
            file.Flush(flushToDisk: true);
        }

        var prior = lease.ReadState()!;
        var committedState = prior with
        {
            InFlightExecutionId = null,
            OriginCompletedAt = completedAt,
            PayloadDirectoryName = payloadName
        };
        EvidenceReceiptQuota.CommitInvocation(root,
        [
            new EvidenceReceiptBatchCommit(repository, key, "legacy-batch-producer", executionId,
                committedState, JsonSerializer.SerializeToUtf8Bytes(committedState).Length,
                payloadName, completedAt, PayloadBytes: 37, ReservationBytes: 80,
                EssentialReservationId: lease.EssentialReservationId)
        ]);

        var committed = Entry(root, key);
        Assert.Equal(37, committed.GetProperty("PayloadBytes").GetInt64());
        Assert.Equal(20, committed.GetProperty("ReservedPayloadBytes").GetInt64());
        Assert.Equal(payloadName, committed.GetProperty("PayloadDirectoryName").GetString());
        Assert.Equal(committedState, lease.ReadState());
        Assert.False(committed.GetProperty("HasInFlight").GetBoolean());
        Assert.False(committed.GetProperty("EssentialReserved").GetBoolean());
        Assert.Empty(committed.GetProperty("EssentialReservationIds").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, committed.GetProperty("PayloadReservations").ValueKind);
        Assert.Equal(new byte[37], await File.ReadAllBytesAsync(
            TestPathUtils.PathUnder(lease.DirectoryPath, payloadName, "owned.bin")));
    }

    [Fact]
    public async Task ByteReleaseConsumesOneBoundReservationBeforeLegacyUnboundBytes()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("mixed-legacy-bound-release-repository");
        var key = Hash("mixed-legacy-bound-release-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        var reservationId = Guid.NewGuid().ToString("N");
        var stageName = "payload-stage-" + reservationId;
        var payloadName = "payload-" + reservationId;
        Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 50, DateTimeOffset.UtcNow));
        Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, 60, DateTimeOffset.UtcNow,
            reservationId, stageName, payloadName));

        EvidenceReceiptQuota.ReleasePayloadReservation(root, repository, key, 60);

        var afterBoundRelease = Entry(root, key);
        Assert.Equal(50, afterBoundRelease.GetProperty("ReservedPayloadBytes").GetInt64());
        Assert.Empty(afterBoundRelease.GetProperty("PayloadReservations").EnumerateArray());
        EvidenceReceiptQuota.ReleasePayloadReservation(root, repository, key, 50);
        Assert.Equal(0, Entry(root, key).GetProperty("ReservedPayloadBytes").GetInt64());
    }

    [Theory]
    [InlineData("indexed")]
    [InlineData("previous-index")]
    [InlineData("replacement-index")]
    [InlineData("abandoned-staging")]
    public void IndexPublicationCapacityRejectsEachNegativeByteComponent(string component)
    {
        var indexedBytes = component == "indexed" ? -1 : 1;
        var previousIndexBytes = component == "previous-index" ? -1 : 1;
        var replacementIndexBytes = component == "replacement-index" ? -1 : 1;
        var abandonedStagingBytes = component == "abandoned-staging" ? -1 : 1;

        Assert.False(EvidenceReceiptQuota.IndexPublicationFits(indexedBytes, previousIndexBytes,
            replacementIndexBytes, abandonedStagingBytes));
    }

    [Fact]
    public void IndexPublicationCapacityUsesLargerAbandonedStagingCopyAtItsExactBoundary()
    {
        var indexedBytes = EvidenceReceiptQuota.MaximumBytes - 256;

        Assert.True(EvidenceReceiptQuota.IndexPublicationFits(indexedBytes, 128, 64, abandonedStagingBytes: 128));
        Assert.False(EvidenceReceiptQuota.IndexPublicationFits(indexedBytes, 128, 64, abandonedStagingBytes: 129));
    }

    [Fact]
    public async Task QuotaPublicationIncludesOwnedAbandonedMetadataStageAndPreservesItOnCapacityRefusal()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("abandoned-quota-stage-repository");
        var key = Hash("abandoned-quota-stage-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        var indexPath = TestPathUtils.PathUnder(root, "quota-index.json");
        var originalIndex = await File.ReadAllBytesAsync(indexPath);
        var index = JsonNode.Parse(originalIndex)!;
        var entry = index["Entries"]!.AsArray().Single()!;
        var stagingBytes = Encoding.UTF8.GetBytes(new string('s', 4096));
        var indexedBytes = EvidenceReceiptQuota.MaximumBytes - stagingBytes.LongLength - originalIndex.LongLength;
        byte[] configuredIndex;
        for (var attempt = 0; ; attempt++)
        {
            entry["MetadataBytes"] = indexedBytes;
            configuredIndex = Encoding.UTF8.GetBytes(index.ToJsonString());
            var adjusted = EvidenceReceiptQuota.MaximumBytes - stagingBytes.LongLength - configuredIndex.LongLength;
            if (adjusted == indexedBytes) break;
            Assert.True(attempt < 3, "The bounded index-charge fixture did not reach a stable encoded length.");
            indexedBytes = adjusted;
        }
        Assert.Equal(indexedBytes, entry["MetadataBytes"]!.GetValue<long>());
        Assert.True(stagingBytes.LongLength > configuredIndex.LongLength);
        await File.WriteAllBytesAsync(indexPath, configuredIndex);

        var stagingPath = TestPathUtils.PathUnder(root, CoverageArtifactMetadataPublication.StagingName);
        string? stagingIdentity = null;
        try
        {
            using (var rootLease = CoverageRunOutputLease.AcquireArtifactDirectory(root, false, true)!)
            using (var staging = rootLease.OpenArtifactFile(CoverageArtifactMetadataPublication.StagingName, true))
            {
                stagingIdentity = CoverageRunOutputLease.GetArtifactObjectIdentity(staging.SafeFileHandle);
                staging.Write(stagingBytes);
                staging.Flush(flushToDisk: true);
            }

            var indexedBefore = await File.ReadAllBytesAsync(indexPath);
            var stageBefore = await File.ReadAllBytesAsync(stagingPath);
            Assert.Equal(stagingBytes, stageBefore);
            Assert.Equal(EvidenceReceiptQuota.MaximumBytes,
                indexedBytes + indexedBefore.LongLength + stageBefore.LongLength);
            var failure = Assert.Throws<EvidenceReceiptQuotaUnavailableException>(() =>
                EvidenceReceiptQuota.TryReservePayload(root, repository, key, 1, DateTimeOffset.UtcNow));

            Assert.Equal("receipt-quota-index-capacity-unavailable", failure.Message);
            Assert.Equal(indexedBefore, await File.ReadAllBytesAsync(indexPath));
            Assert.Equal(stageBefore, await File.ReadAllBytesAsync(stagingPath));
            Assert.Equal(indexedBytes, Entry(root, key).GetProperty("MetadataBytes").GetInt64());
        }
        finally
        {
            await File.WriteAllBytesAsync(indexPath, originalIndex);
            if (stagingIdentity is not null)
            {
                using var rootLease = CoverageRunOutputLease.AcquireArtifactDirectory(root, false, true)!;
                rootLease.DeleteArtifactEntry(CoverageArtifactMetadataPublication.StagingName, 1, stagingIdentity);
            }
        }
    }

    [Fact]
    public async Task MissingQuotaEntryOperationsRefuseMutationAndConfirmCanRecreateOnlyItsOwnRow()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("missing-quota-entry-repository");
        var missingKey = Hash("missing-quota-entry-key");
        var controlKey = Hash("missing-quota-entry-control-key");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        IReadOnlyList<EvidenceReceiptStore.EvidenceReceiptKeyLease>? leases = null;
        var indexPath = TestPathUtils.PathUnder(root, "quota-index.json");
        byte[]? originalIndex = null;
        try
        {
            leases = await store.AcquireAsync([missingKey, controlKey], CancellationToken.None);
            var missingLease = Assert.Single(leases, lease => lease.Key == missingKey);
            var controlLease = Assert.Single(leases, lease => lease.Key == controlKey);
            controlLease.EstablishAuthority("control-execution");
            var controlState = controlLease.ReadState();
            var originalIndexBytes = await File.ReadAllBytesAsync(indexPath);
            originalIndex = originalIndexBytes;
            var sentinelPath = TestPathUtils.PathUnder(temporary.Path, "outside-sentinel.txt");
            var sentinelBytes = Encoding.UTF8.GetBytes("unchanged outside receipt custody");
            await File.WriteAllBytesAsync(sentinelPath, sentinelBytes);

            var index = JsonNode.Parse(originalIndexBytes)!;
            var entries = index["Entries"]!.AsArray();
            var missingEntry = entries.Single(entry => entry!["Key"]!.GetValue<string>() == missingKey);
            Assert.True(entries.Remove(missingEntry));
            await File.WriteAllTextAsync(indexPath, index.ToJsonString());
            var missingIndex = await File.ReadAllBytesAsync(indexPath);
            var state = new EvidenceReceiptState(1, missingKey, null, null, null, null, null);

            Assert.Throws<InvalidOperationException>(() => EvidenceReceiptQuota.TryReservePayload(
                root, repository, missingKey, 1, DateTimeOffset.UtcNow));
            Assert.Equal(missingIndex, await File.ReadAllBytesAsync(indexPath));
            Assert.Throws<EvidenceReceiptQuotaUnavailableException>(() => EvidenceReceiptQuota.RecordState(
                root, repository, missingKey, 0, state));
            Assert.Equal(missingIndex, await File.ReadAllBytesAsync(indexPath));
            Assert.Throws<EvidenceReceiptQuotaUnavailableException>(() => EvidenceReceiptQuota.CommitPayload(
                root, repository, missingKey, 0, "payload-missing-row", DateTimeOffset.UtcNow, stateBytes: 0));
            Assert.Equal(missingIndex, await File.ReadAllBytesAsync(indexPath));
            Assert.Empty(EvidenceReceiptQuota.ReadRetiredPayloadNames(root, repository, missingKey));
            Assert.Equal(missingIndex, await File.ReadAllBytesAsync(indexPath));

            EvidenceReceiptQuota.ReleaseEssentialReservation(root, repository, missingKey,
                missingLease.EssentialReservationId);
            EvidenceReceiptQuota.ReleasePayloadReservation(root, repository, missingKey, 1L);
            EvidenceReceiptQuota.ReleasePayloadReservation(root, repository, missingKey, "missing-reservation");
            Assert.Equal(missingIndex, await File.ReadAllBytesAsync(indexPath));
            Assert.Throws<EvidenceReceiptQuotaUnavailableException>(() => EvidenceReceiptQuota.RecordSuccessfulUse(
                root, repository, missingKey, DateTimeOffset.UtcNow, 0, state));
            Assert.Equal(missingIndex, await File.ReadAllBytesAsync(indexPath));

            EvidenceReceiptQuota.ConfirmEssentialReservation(root, repository, missingKey,
                missingLease.EssentialReservationId, DateTimeOffset.UtcNow);
            var recreated = Entry(root, missingKey);
            Assert.Equal(EvidenceReceiptQuota.EssentialReservationBytes,
                recreated.GetProperty("EssentialReservationBytes").GetInt64());
            Assert.Equal(missingLease.EssentialReservationId,
                Assert.Single(recreated.GetProperty("EssentialReservationIds").EnumerateArray()).GetString());
            Assert.Equal(controlState, controlLease.ReadState());
            Assert.Equal(sentinelBytes, await File.ReadAllBytesAsync(sentinelPath));
        }
        finally
        {
            if (leases is not null)
            {
                // Restore the test-owned valid index before lease disposal can reconcile its reservations.
                if (originalIndex is not null) await File.WriteAllBytesAsync(indexPath, originalIndex);
                foreach (var lease in leases) lease.Dispose();
            }
        }
    }

    [Fact]
    public void PayloadMeasurementRecursesAndCountsActualFileBytes()
    {
        using var temporary = TestDirectory.Create();
        var payloadPath = TestPathUtils.PathUnder(temporary.Path, "payload");
        using var payload = CoverageRunOutputLease.AcquireArtifactDirectory(payloadPath, createMissing: true,
            requirePrivate: true)!;
        using (var file = payload.OpenArtifactFile("root.bin", createNew: true))
        {
            file.Write(new byte[] { 1, 2, 3 });
            file.Flush(flushToDisk: true);
        }
        using (var nested = payload.CreateArtifactDirectory("nested"))
        using (var file = nested.OpenArtifactFile("nested.bin", createNew: true))
        {
            file.Write(new byte[] { 4, 5, 6, 7, 8 });
            file.Flush(flushToDisk: true);
        }

        Assert.Equal(8, EvidenceReceiptQuota.MeasurePayload(payload));
    }

    private static JsonElement Entry(string root, string key)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(TestPathUtils.PathUnder(root, "quota-index.json")));
        return document.RootElement.GetProperty("Entries").EnumerateArray()
            .Single(item => item.GetProperty("Key").GetString() == key).Clone();
    }

    private static JsonElement[] Entries(string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(TestPathUtils.PathUnder(root, "quota-index.json")));
        return document.RootElement.GetProperty("Entries").EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static async Task<EvidenceReceiptState> AddExpiredReceiptAsync(string root, string repository,
        string key, string payloadName, bool includeForeignFile, string foreignFileName = "foreign.bin")
    {
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        var completedAt = DateTimeOffset.UtcNow;
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        using (var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(lease.DirectoryPath, false, true)!)
        using (var payload = keyRoot.CreateArtifactDirectory(payloadName))
        {
            if (includeForeignFile)
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(payload.ArtifactDirectoryPath, foreignFileName),
                    "foreign payload bytes");
        }

        var state = new EvidenceReceiptState(1, key, null, null, Hash("expired-origin"), completedAt,
            payloadName, null, completedAt, false, null);
        EvidenceReceiptQuota.RecordState(root, repository, key,
            JsonSerializer.SerializeToUtf8Bytes(state).Length, state);
        return state;
    }

    private static async Task<EvidenceReceiptState> AddSettledPayloadAsync(string root, string repository,
        string key, string payloadName, byte[] payloadBytes, DateTimeOffset completedAt)
    {
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        const string executionId = "quota-maintenance-settled-owner";
        lease.EstablishAuthority(executionId);
        using (var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(lease.DirectoryPath, false, true)!)
        using (var payload = keyRoot.CreateArtifactDirectory(payloadName))
        using (var file = payload.OpenArtifactFile("owned.bin", createNew: true))
        {
            file.Write(payloadBytes);
            file.Flush(flushToDisk: true);
        }
        Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repository, key, payloadBytes.LongLength, completedAt));
        EvidenceReceiptQuota.CommitPayload(root, repository, key, payloadBytes.LongLength, payloadName,
            completedAt, stateBytes: 0);
        var passed = EvidenceCanonicalJson.Serialize(new EvidenceProducerResult("quota-maintenance-producer",
            EvidenceProducerOutcome.Passed, ["quota-maintenance-assertion"]));
        lease.CommitPassed(executionId, Hash("quota-maintenance-origin-" + key), completedAt, payloadName, passed);
        return lease.ReadState()!;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
