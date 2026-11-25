using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceReceiptStoreTests
{
    [Fact]
    public void ResolveRootRejectsMissingBaseAndHonorsOverridePrecedence()
    {
        Assert.Throws<ArgumentException>(() => EvidenceReceiptStore.ResolveRoot("  ", "/env", "/local"));
        Assert.Equal("/explicit", EvidenceReceiptStore.ResolveRoot("/explicit", "/env", null));
        Assert.Equal("/env", EvidenceReceiptStore.ResolveRoot(null, "/env", null));
        Assert.Throws<InvalidOperationException>(() => EvidenceReceiptStore.ResolveRoot(null, " ", ""));
        Assert.Equal(Path.Combine("/local", "ForgeTrust", "AppSurface", "evidence-receipts", "v1"),
            EvidenceReceiptStore.ResolveRoot(null, " ", "/local"));
    }

    [Fact]
    public void ReadOnlyQuotaSnapshotsDoNotCreateOrAcquireTheIndexLock()
    {
        var root = NewRoot();
        try
        {
            using (CoverageRunOutputLease.AcquireArtifactDirectory(root, createMissing: true, requirePrivate: true)) { }
            Assert.Null(EvidenceReceiptQuota.ReadAuthority(root, Hash("repo"), Hash("key"), readOnly: true));
            Assert.Empty(EvidenceReceiptQuota.ReadComparable(root, Hash("repo"), "producer", DateTimeOffset.UtcNow,
                readOnly: true));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { DeleteIfPresent(root); }
    }

    [Fact]
    public void ReadOnlyPeekTreatsSafeMissingRootAsMissWithoutCreatingIt()
    {
        var root = NewRoot();
        var store = EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = Hash("repo") });
        var result = store.Peek(Hash("key"));
        Assert.Equal(EvidenceReceiptReadOutcome.Miss, result.Outcome);
        Assert.Equal("missing", result.Reason);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void ReadOnlyPeekDoesNotClassifyLinkedRootAsMissing()
    {
        var parent = NewRoot();
        var target = Path.Join(parent, "target");
        var link = Path.Join(parent, "link");
        try
        {
            Directory.CreateDirectory(target);
            TestFileSystem.CreateDirectoryLinkOrSkip(link, target);
            var store = EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
            { RootDirectory = link, RepositoryNamespace = Hash("repo") });
            Assert.True(store.Peek(Hash("key")).IsUnavailable);
        }
        finally { DeleteIfPresent(parent); }
    }

    [Fact]
    public void IndexReplacementCapacityIncludesOldAndStagedIndexCopies()
    {
        var indexedBytes = EvidenceReceiptQuota.MaximumBytes - 200;
        Assert.True(EvidenceReceiptQuota.IndexPublicationFits(indexedBytes, 100, 100));
        Assert.True(EvidenceReceiptQuota.IndexPublicationFits(indexedBytes, 100, 50, abandonedStagingBytes: 100));
        Assert.False(EvidenceReceiptQuota.IndexPublicationFits(indexedBytes, 101, 100));
        Assert.False(EvidenceReceiptQuota.IndexPublicationFits(indexedBytes, 100, 101));
        Assert.False(EvidenceReceiptQuota.IndexPublicationFits(indexedBytes, 100, 50, abandonedStagingBytes: 101));
    }

    [Fact]
    public async Task PayloadCleanupGuardRetainsReservationUntilExactEntryIsAbsent()
    {
        var root = NewRoot();
        try
        {
            var repositoryNamespace = Hash("repo").ToLowerInvariant();
            var key = Hash("payload-cleanup-guard").ToLowerInvariant();
            var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
            { RootDirectory = root, RepositoryNamespace = repositoryNamespace });
            using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
            var reservation = EvidenceReceiptPayloadReservation.Create(Guid.NewGuid().ToString("N"), 128);
            Assert.True(EvidenceReceiptQuota.TryReservePayload(root, repositoryNamespace, key, DateTimeOffset.UtcNow, reservation));

            var payloadFile = Path.Join(lease.DirectoryPath, reservation.PayloadDirectoryName);
            File.WriteAllText(payloadFile, "keep");
            Assert.False(EvidenceReceiptPayload.TryDeleteExactDirectory(lease.DirectoryPath, reservation.PayloadDirectoryName));
            Assert.False(lease.ReleaseReservationAfterConfirmedCleanup(reservation, cleanupConfirmed: false));
            var held = Assert.Single(ReadQuotaEntries(root), entry => entry.GetProperty("Key").GetString() == key);
            Assert.Equal(reservation.ReservedBytes, held.GetProperty("ReservedPayloadBytes").GetInt64());
            Assert.Equal(reservation.ReservationId, Assert.Single(held.GetProperty("PayloadReservations").EnumerateArray())
                .GetProperty("ReservationId").GetString());
            Assert.Equal("keep", File.ReadAllText(payloadFile));

            File.Delete(payloadFile);
            Assert.True(lease.ReleaseReservationAfterConfirmedCleanup(reservation,
                cleanupConfirmed: EvidenceReceiptPayload.TryDeleteExactDirectory(lease.DirectoryPath, reservation.PayloadDirectoryName)));
            var released = Assert.Single(ReadQuotaEntries(root), entry => entry.GetProperty("Key").GetString() == key);
            Assert.Equal(0, released.GetProperty("ReservedPayloadBytes").GetInt64());
            Assert.Empty(released.GetProperty("PayloadReservations").EnumerateArray());
        }
        finally { DeleteIfPresent(root); }
    }

    [Fact]
    public void ReadOnlyOpenResolvesConfigurationWithoutCreatingStore()
    {
        var root = NewRoot();
        try
        {
            var store = EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
            {
                RootDirectory = root,
                RepositoryNamespace = Hash("repo")
            });

            Assert.NotNull(store);
            Assert.False(Directory.Exists(root));
        }
        finally { DeleteIfPresent(root); }
    }

    [Fact]
    public async Task AuthoritySurvivesNativeLeaseReleaseAndBlocksAnotherExecution()
    {
        var root = NewRoot();
        try
        {
            var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
            {
                RootDirectory = root,
                RepositoryNamespace = Hash("repo")
            });
            var key = Hash("producer");
            var leases = await store.AcquireAsync([key], CancellationToken.None);
            var lease = Assert.Single(leases);
            lease.EstablishAuthority("execution-a");
            lease.Dispose();
            EvidenceReceiptQuota.Maintain(root, DateTimeOffset.UtcNow);

            var next = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
            try
            {
                var state = next.ReadState();
                Assert.Equal("execution-a", state?.InFlightExecutionId);
                Assert.Throws<InvalidOperationException>(() => next.EstablishAuthority("execution-b"));
            }
            finally { next.Dispose(); }
        }
        finally { DeleteIfPresent(root); }
    }

    [Fact]
    public async Task AuthorityCanOnlyBeSettledByItsExecutionId()
    {
        var root = NewRoot();
        try
        {
            var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
            {
                RootDirectory = root,
                RepositoryNamespace = Hash("repo")
            });
            var lease = Assert.Single(await store.AcquireAsync([Hash("producer")], CancellationToken.None));
            using (lease)
            {
                lease.EstablishAuthority("execution-a", auditIntent: true);
                Assert.Throws<InvalidOperationException>(() => lease.SettleExecution("execution-b"));
                lease.SettleExecution("execution-a");
                var state = lease.ReadState();
                Assert.Null(state?.InFlightExecutionId);
                Assert.Equal("execution-a", state?.AuditIntentExecutionId);
            }
        }
        finally { DeleteIfPresent(root); }
    }

    [Fact]
    public async Task PreparedAndCancelledAcquisitionsReleaseTheirOwnEssentialHolds()
    {
        var root = NewRoot();
        try
        {
            var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
            { RootDirectory = root, RepositoryNamespace = Hash("repo") });
            var key = Hash("prepared-key");
            var prepared = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
            Assert.Contains(ReadQuotaEntries(root), entry => entry.GetProperty("Key").GetString() == key.ToLowerInvariant());
            prepared.Dispose();
            Assert.DoesNotContain(ReadQuotaEntries(root), entry => entry.GetProperty("Key").GetString() == key.ToLowerInvariant());

            var active = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
            using var cancellation = new CancellationTokenSource();
            var waiting = store.AcquireAsync([key], cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            active.Dispose();

            EvidenceReceiptQuota.Maintain(root, DateTimeOffset.UtcNow);
            Assert.DoesNotContain(ReadQuotaEntries(root), entry => entry.GetProperty("Key").GetString() == key.ToLowerInvariant());
        }
        finally { DeleteIfPresent(root); }
    }

    [Fact]
    public async Task AcquiringTheSameFreeKeyReclaimsAbandonedPreparationHolds()
    {
        var root = NewRoot();
        try
        {
            var repositoryNamespace = Hash("repo").ToLowerInvariant();
            var key = Hash("abandoned-same-key").ToLowerInvariant();
            var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
            { RootDirectory = root, RepositoryNamespace = repositoryNamespace });
            using (Assert.Single(await store.AcquireAsync([key], CancellationToken.None))) { }
            var abandoned = EvidenceReceiptQuota.ReserveEssential(root, repositoryNamespace, [key], DateTimeOffset.UtcNow);
            using (var current = Assert.Single(await store.AcquireAsync([key], CancellationToken.None)))
            {
                var entry = Assert.Single(ReadQuotaEntries(root));
                var holds = entry.GetProperty("EssentialReservationIds").EnumerateArray().Select(item => item.GetString()).ToArray();
                Assert.Equal(current.EssentialReservationId, Assert.Single(holds));
                Assert.DoesNotContain(abandoned, holds);
            }
            Assert.Empty(ReadQuotaEntries(root));
        }
        finally { DeleteIfPresent(root); }
    }

    [Fact]
    public void MaintenanceReclaimsAProcessAbandonedPreexecutionReservationOnlyWithAFreeKey()
    {
        var root = NewRoot();
        try
        {
            using (CoverageRunOutputLease.AcquireArtifactDirectory(root, createMissing: true, requirePrivate: true)) { }
            var repositoryNamespace = Hash("repo").ToLowerInvariant();
            var key = Hash("abandoned-key").ToLowerInvariant();
            var reservation = EvidenceReceiptQuota.ReserveEssential(root, repositoryNamespace, [key], DateTimeOffset.UtcNow);
            Assert.Contains(ReadQuotaEntries(root), entry => entry.GetProperty("Key").GetString() == key);

            EvidenceReceiptQuota.Maintain(root, DateTimeOffset.UtcNow);

            Assert.DoesNotContain(ReadQuotaEntries(root), entry => entry.GetProperty("Key").GetString() == key);
            Assert.False(string.IsNullOrWhiteSpace(reservation));
        }
        finally { DeleteIfPresent(root); }
    }

    [Fact]
    public async Task AuditMismatchQuarantinesReceiptUntilOwnerCommitsSuccessfulAudit()
    {
        var root = NewRoot();
        var sourceRoot = NewRoot();
        try
        {
            var producer = ReceiptProducer();
            var plan = CreatePlan(producer);
            Directory.CreateDirectory(sourceRoot);
            var writer = new EvidenceArtifactWriter(producer, sourceRoot);
            await writer.WriteAsync("report", "reports/audit.txt", "fresh audited report"u8.ToArray());
            var manifest = EvidenceManifestBuilder.Build(plan, [new EvidenceProducerResult(producer.Id,
                EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: writer.WrittenArtifacts)]);
            var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
            {
                RootDirectory = root,
                RepositoryNamespace = Hash("repo")
            });
            var key = Hash("producer");
            var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
            using (lease)
            {
                lease.EstablishAuthority("audit-a", auditIntent: true, expectedAssertions: "assertion-a:required");
                lease.MarkAuditMismatch("audit-a", "assertion-a:required");
                lease.SettleExecution("audit-a");
                var quarantined = lease.ProposeReadReceipt();
                Assert.Equal(EvidenceReceiptReadOutcome.Miss, quarantined.Outcome);
                Assert.Equal("audit-quarantined", quarantined.Reason);

                lease.EstablishAuthority("audit-b", auditIntent: true, expectedAssertions: "replacement-must-not-win");
                Assert.Equal("audit-b", lease.ReadState()?.InFlightExecutionId);
                Assert.Equal("audit-b", lease.ReadState()?.AuditIntentExecutionId);
                Assert.Equal("assertion-a:required", lease.ReadState()?.AuditExpectedAssertions);
                var publication = await lease.PublishAsync("audit-b", plan, manifest, producer.Id,
                    sourceRoot, DateTimeOffset.UtcNow, CancellationToken.None);
                Assert.True(publication.Published, publication.Reason);
                var recovered = lease.ProposeReadReceipt();
                Assert.True(recovered.IsCandidate);
                Assert.Null(recovered.State?.AuditIntentExecutionId);
                Assert.False(recovered.State?.AuditMismatch);
                Assert.Equal(publication.PayloadDirectoryName, recovered.State?.PayloadDirectoryName);
            }
        }
        finally { DeleteIfPresent(root); DeleteIfPresent(sourceRoot); }
    }

    [Fact]
    public async Task QuarantinedFreshExecutionPreservesIntentAndCannotPublish()
    {
        var root = NewRoot();
        var sourceRoot = NewRoot();
        try
        {
            Directory.CreateDirectory(sourceRoot);
            var producer = ReceiptProducer();
            var plan = CreatePlan(producer);
            var sourceWriter = new EvidenceArtifactWriter(producer, sourceRoot);
            await sourceWriter.WriteAsync("report", "reports/held.txt", Encoding.UTF8.GetBytes("still quarantined"));
            var manifest = EvidenceManifestBuilder.Build(plan, [new EvidenceProducerResult(producer.Id,
                EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: sourceWriter.WrittenArtifacts)]);
            var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions { RootDirectory = root, RepositoryNamespace = Hash("repo") });
            var lease = Assert.Single(await store.AcquireAsync([Hash("producer")], CancellationToken.None));
            using (lease)
            {
                lease.EstablishAuthority("audit-a", auditIntent: true, expectedAssertions: "expected");
                lease.MarkAuditMismatch("audit-a", "expected");
                lease.SettleExecution("audit-a");
                lease.EstablishAuthority("ordinary", allowQuarantinedFresh: true);
                var state = lease.ReadState();
                Assert.Equal("ordinary", state?.InFlightExecutionId);
                Assert.Equal("audit-a", state?.AuditIntentExecutionId);
                Assert.True(state?.AuditMismatch);
                Assert.Equal("expected", state?.AuditExpectedAssertions);
                Assert.Equal("unresolved-execution", lease.ProposeReadReceipt().Reason);
                var refused = await lease.PublishAsync("ordinary", plan, manifest, producer.Id, sourceRoot,
                    DateTimeOffset.UtcNow, CancellationToken.None);
                Assert.False(refused.Published);
                Assert.Equal("audit-intent-owner-mismatch", refused.Reason);
            }
        }
        finally { DeleteIfPresent(root); DeleteIfPresent(sourceRoot); }
    }

    [Fact]
    public async Task PeekReturnsOriginWithoutCreatingStateOrUpdatingLastUse()
    {
        var storeRoot = NewRoot();
        var sourceRoot = NewRoot();
        try
        {
            Directory.CreateDirectory(sourceRoot);
            var producer = ReceiptProducer();
            var plan = CreatePlan(producer);
            var writer = new EvidenceArtifactWriter(producer, sourceRoot);
            await writer.WriteAsync("report", "reports/report.txt", Encoding.UTF8.GetBytes("data"));
            var manifest = EvidenceManifestBuilder.Build(plan, [new EvidenceProducerResult(producer.Id,
                EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: writer.WrittenArtifacts, ElapsedMilliseconds: 39)]);
            var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions { RootDirectory = storeRoot, RepositoryNamespace = Hash("repo") });
            var key = Hash("peek");
            var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
            using (lease)
            {
                lease.EstablishAuthority("execution");
                Assert.True((await lease.PublishAsync("execution", plan, manifest, producer.Id, sourceRoot,
                    DateTimeOffset.UtcNow, CancellationToken.None)).Published);
            }
            var statePath = Path.Combine(storeRoot, "quota-index.json");
            var before = await File.ReadAllBytesAsync(statePath);
            var view = EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions { RootDirectory = storeRoot, RepositoryNamespace = Hash("repo") });
            var peek = view.Peek(key);
            Assert.Equal(EvidenceReceiptReadOutcome.Candidate, peek.Outcome);
            Assert.Equal(EvidenceProducerOutcome.Passed, peek.Result?.Outcome);
            Assert.Equal(39, peek.OriginElapsedMilliseconds);
            Assert.Equal(manifest.ManifestDigest, peek.OriginManifest?.ManifestDigest);
            Assert.NotNull(peek.OriginPlan);
            Assert.Equal(before, await File.ReadAllBytesAsync(statePath));
        }
        finally { DeleteIfPresent(storeRoot); DeleteIfPresent(sourceRoot); }
    }

    [Fact]
    public async Task QuotaMaintenanceExpiresOnlySettledReceiptPayloadsAfterSevenDays()
    {
        var storeRoot = NewRoot();
        var sourceRoot = NewRoot();
        try
        {
            Directory.CreateDirectory(sourceRoot);
            var producer = ReceiptProducer();
            var plan = CreatePlan(producer);
            var writer = new EvidenceArtifactWriter(producer, sourceRoot);
            await writer.WriteAsync("report", "reports/report.txt", Encoding.UTF8.GetBytes("expires"));
            var manifest = EvidenceManifestBuilder.Build(plan, [new EvidenceProducerResult(producer.Id,
                EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: writer.WrittenArtifacts)]);
            var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions { RootDirectory = storeRoot, RepositoryNamespace = Hash("repo") });
            var key = Hash("expiry");
            var completedAt = DateTimeOffset.UtcNow;
            var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
            using (lease)
            {
                lease.EstablishAuthority("execution");
                var published = await lease.PublishAsync("execution", plan, manifest, producer.Id, sourceRoot,
                    completedAt, CancellationToken.None);
                Assert.True(published.Published, published.Reason);
            }

            EvidenceReceiptQuota.Maintain(storeRoot, completedAt.AddDays(7));
            var keyDirectory = Path.Combine(storeRoot, Hash("repo").ToLowerInvariant(), key.ToLowerInvariant());
            Assert.Equal("missing", EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
            { RootDirectory = storeRoot, RepositoryNamespace = Hash("repo") }).Peek(key).Reason);
            Assert.Equal(OperatingSystem.IsWindows() ? [".appsurface-coverage-operation.lock"] : Array.Empty<string>(),
                Directory.EnumerateFileSystemEntries(keyDirectory).Select(Path.GetFileName));
            if (OperatingSystem.IsWindows())
                Assert.Equal(0, new FileInfo(Path.Combine(keyDirectory, ".appsurface-coverage-operation.lock")).Length);
        }
        finally { DeleteIfPresent(storeRoot); DeleteIfPresent(sourceRoot); }
    }

    [Theory]
    [InlineData(49_153, "producer-result-too-large")]
    [InlineData(49_152, "payload-publication-failed")]
    public async Task OversizedProducerAuthorityIsRejectedBeforePublication(int rawBytes, string expectedReason)
    {
        var storeRoot = NewRoot();
        var sourceRoot = NewRoot();
        try
        {
            var producer = ReceiptProducer();
            var plan = CreatePlan(producer);
            var writer = new EvidenceArtifactWriter(producer, sourceRoot);
            await writer.WriteAsync("report", "reports/result.txt", "report"u8.ToArray());
            var result = new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Passed,
                ["assertion"], string.Empty, writer.WrittenArtifacts);
            var overhead = EvidenceCanonicalJson.Serialize(result).Length;
            result = result with { Diagnostic = new string('x', rawBytes - overhead) };
            Assert.Equal(rawBytes, EvidenceCanonicalJson.Serialize(result).Length);
            var manifest = EvidenceManifestBuilder.Build(plan, [result]);
            var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
            { RootDirectory = storeRoot, RepositoryNamespace = Hash("repo") });
            using var lease = Assert.Single(await store.AcquireAsync([Hash("producer")], CancellationToken.None));
            lease.EstablishAuthority("oversized-authority");
            var before = lease.ReadState();

            var publication = await lease.PublishAsync("oversized-authority", plan, manifest, producer.Id,
                sourceRoot, DateTimeOffset.UtcNow, CancellationToken.None);

            Assert.False(publication.Published);
            Assert.Equal(expectedReason, publication.Reason);
            Assert.Equal(before, lease.ReadState());
            Assert.DoesNotContain(Directory.EnumerateDirectories(lease.DirectoryPath), path =>
                Path.GetFileName(path).StartsWith("payload", StringComparison.Ordinal));
            Assert.Equal("report", await File.ReadAllTextAsync(Path.Combine(sourceRoot, "reports/result.txt")));
        }
        finally { DeleteIfPresent(storeRoot); DeleteIfPresent(sourceRoot); }
    }

    [Fact]
    public async Task FailedSecondAuditStageLeavesBothAuthorityRowsQuarantined()
    {
        var storeRoot = NewRoot();
        var sourceA = NewRoot();
        var sourceB = NewRoot();
        try
        {
            Directory.CreateDirectory(sourceA);
            Directory.CreateDirectory(sourceB);
            var producerA = ReceiptProducer("producer-a");
            var producerB = ReceiptProducer("producer-b");
            var plan = CreatePlan(producerA, producerB);
            var writerA = new EvidenceArtifactWriter(producerA, sourceA);
            var writerB = new EvidenceArtifactWriter(producerB, sourceB);
            await writerA.WriteAsync("report", "reports/a.txt", Encoding.UTF8.GetBytes("alpha"));
            await writerB.WriteAsync("report", "reports/b.txt", Encoding.UTF8.GetBytes("bravo"));
            var manifest = EvidenceManifestBuilder.Build(plan,
            [
                new(producerA.Id, EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: writerA.WrittenArtifacts),
                new(producerB.Id, EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: writerB.WrittenArtifacts)
            ]);
            var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
            { RootDirectory = storeRoot, RepositoryNamespace = Hash("repo") });
            var keyA = Hash("key-a");
            var keyB = Hash("key-b");
            var leases = await store.AcquireAsync([keyA, keyB], CancellationToken.None);
            try
            {
                var byKey = leases.ToDictionary(static lease => lease.Key, StringComparer.OrdinalIgnoreCase);
                foreach (var lease in leases) lease.EstablishAuthority("audit-exec", auditIntent: true, expectedAssertions: "assertion");

                var outcomes = await store.PublishInvocationAsync("audit-exec", plan, manifest,
                [
                    new(byKey[keyA], producerA.Id, sourceA, DateTimeOffset.UtcNow),
                    new(byKey[keyB], producerB.Id, Path.Combine(sourceB, "missing-source-root"), DateTimeOffset.UtcNow)
                ], CancellationToken.None);

                Assert.All(outcomes.Values, static outcome => Assert.False(outcome.Published));
                Assert.Equal(2, outcomes.Count);
                foreach (var lease in leases)
                {
                    var state = lease.ReadState();
                    Assert.Equal("audit-exec", state?.InFlightExecutionId);
                    Assert.Equal("audit-exec", state?.AuditIntentExecutionId);
                    Assert.Equal("assertion", state?.AuditExpectedAssertions);
                    Assert.True(EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
                    { RootDirectory = storeRoot, RepositoryNamespace = Hash("repo") }).Peek(lease.Key).IsUnavailable);
                    Assert.DoesNotContain(Directory.EnumerateDirectories(lease.DirectoryPath), path =>
                        Path.GetFileName(path).StartsWith("payload-", StringComparison.Ordinal));
                }
            }
            finally
            {
                foreach (var lease in leases.Reverse()) lease.Dispose();
            }
        }
        finally { DeleteIfPresent(storeRoot); DeleteIfPresent(sourceA); DeleteIfPresent(sourceB); }
    }

    [Fact]
    public async Task AuditMismatchCannotBeRecordedWithoutMatchingDurableIntent()
    {
        var root = NewRoot();
        try
        {
            var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
            {
                RootDirectory = root,
                RepositoryNamespace = Hash("repo")
            });
            var lease = Assert.Single(await store.AcquireAsync([Hash("producer")], CancellationToken.None));
            using (lease)
            {
                lease.EstablishAuthority("ordinary-run");
                Assert.Throws<InvalidOperationException>(() => lease.MarkAuditMismatch("ordinary-run", "expected"));
            }
        }
        finally { DeleteIfPresent(root); }
    }

    [Fact]
    public async Task PublishAndReadReceiptVerifyCanonicalOriginAndRestoreCompleteArtifacts()
    {
        var storeRoot = NewRoot();
        var sourceRoot = NewRoot();
        var restoreRoot = NewRoot();
        try
        {
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(restoreRoot);
            var producer = ReceiptProducer();
            var plan = CreatePlan(producer);
            var sourceWriter = new EvidenceArtifactWriter(producer, sourceRoot);
            await sourceWriter.WriteAsync("report", "reports/a.txt", Encoding.UTF8.GetBytes("alpha"));
            await sourceWriter.WriteAsync("report", "reports/nested/b.txt", Encoding.UTF8.GetBytes("bravo"));
            var result = new EvidenceProducerResult("producer", EvidenceProducerOutcome.Passed, ["assertion"],
                Artifacts: sourceWriter.WrittenArtifacts, ElapsedMilliseconds: 42);
            var manifest = EvidenceManifestBuilder.Build(plan, [result]);

            var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-11-15T12:00:00Z"));
            var repositoryNamespace = Hash("repo").ToLowerInvariant();
            var storeOptions = new EvidenceReceiptStoreOptions
            { RootDirectory = storeRoot, RepositoryNamespace = repositoryNamespace, Clock = clock };
            var store = EvidenceReceiptStore.Open(storeOptions);
            var key = Hash("producer-key");
            var identity = new EvidenceProducerIdentity(key.ToLowerInvariant(), repositoryNamespace,
                [new EvidenceProducerInputFile(" Cli/README.md ", 33188, 5, Hash("input").ToLowerInvariant())],
                new Dictionary<string, string>(StringComparer.Ordinal) { ["tools"] = Hash("tools").ToLowerInvariant() });
            var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
            using (lease)
            {
                lease.EstablishAuthority("execution-1");
                var publications = await store.PublishInvocationAsync("execution-1", plan, manifest,
                    [new EvidenceReceiptPublicationRequest(lease, producer.Id, sourceRoot, clock.GetUtcNow()) { Identity = identity }],
                    CancellationToken.None);
                Assert.True(publications[producer.Id].Published, publications[producer.Id].Reason);
            }

            var indexPath = Path.Combine(storeRoot, "quota-index.json");
            var indexBeforeExplain = await File.ReadAllBytesAsync(indexPath);
            var readOnly = EvidenceReceiptStore.OpenReadOnly(storeOptions);
            var comparable = readOnly.FindComparable(producer.Id, identity);
            Assert.True(comparable.Found);
            Assert.Equal("identity-matches", comparable.Reason);
            Assert.Equal(indexBeforeExplain, await File.ReadAllBytesAsync(indexPath));
            var changedIdentity = identity with
            {
                Key = Hash("different-key").ToLowerInvariant(),
                Files = [new EvidenceProducerInputFile(" Cli/README.md ", 33188, 6, Hash("changed").ToLowerInvariant())]
            };
            var differing = readOnly.FindComparable(producer.Id, changedIdentity);
            Assert.Equal("input-files-differ", differing.Reason);
            Assert.Equal(" Cli/README.md ", differing.FirstDifferingPath);
            var changedComponent = readOnly.FindComparable(producer.Id, identity with
            {
                Key = Hash("different-component-key").ToLowerInvariant(),
                ComponentHashes = new Dictionary<string, string>(StringComparer.Ordinal)
                { ["tools"] = Hash("changed-tools").ToLowerInvariant() }
            });
            Assert.Equal("identity-component-differs", changedComponent.Reason);
            Assert.Equal("tools", changedComponent.DifferingComponent);
            Assert.Equal(indexBeforeExplain, await File.ReadAllBytesAsync(indexPath));

            var readerLease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
            using (readerLease)
            {
                var restoreWriter = new EvidenceArtifactWriter(producer, restoreRoot);
                var restored = await readerLease.ReadReceiptAsync(producer, restoreWriter, CancellationToken.None);
                Assert.Equal(EvidenceReceiptReadOutcome.Restored, restored.Outcome);
                Assert.Equal(EvidenceProducerOutcome.Reused, restored.Result?.Outcome);
                Assert.Equal(42, restored.OriginElapsedMilliseconds);
                Assert.Equal(["assertion"], restored.Result?.SatisfiedAssertionIds);
                Assert.Equal(manifest.ManifestDigest, restored.OriginManifest?.ManifestDigest);
                Assert.Equal(identity.Files, restored.InputIdentity?.Files);
                Assert.Equal("alpha", await File.ReadAllTextAsync(Path.Join(restoreRoot, "reports/a.txt")));
                Assert.Equal("bravo", await File.ReadAllTextAsync(Path.Join(restoreRoot, "reports/nested/b.txt")));
                Assert.True(await restoreWriter.VerifyWrittenArtifactsAsync());
            }
            clock.Advance(TimeSpan.FromDays(8));
            Assert.Equal("origin-expired", readOnly.Peek(key).Reason);
        }
        finally
        {
            DeleteIfPresent(storeRoot);
            DeleteIfPresent(sourceRoot);
            DeleteIfPresent(restoreRoot);
        }
    }

    [Fact]
    public async Task PostCommitFailureRefusesButKeepsCommittedReceiptRestorableAndAccounted()
    {
        var storeRoot = NewRoot();
        var sourceRoot = NewRoot();
        var restoreRoot = NewRoot();
        try
        {
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(restoreRoot);
            var producer = ReceiptProducer();
            var plan = CreatePlan(producer);
            var sourceWriter = new EvidenceArtifactWriter(producer, sourceRoot);
            await sourceWriter.WriteAsync("report", "reports/committed.txt", Encoding.UTF8.GetBytes("committed payload"));
            var manifest = EvidenceManifestBuilder.Build(plan,
                [new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Passed, ["assertion"],
                    Artifacts: sourceWriter.WrittenArtifacts)]);
            var repositoryNamespace = Hash("repo").ToLowerInvariant();
            var key = Hash("post-commit-failure").ToLowerInvariant();
            var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
            { RootDirectory = storeRoot, RepositoryNamespace = repositoryNamespace });
            var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
            using (lease)
            {
                const string executionId = "post-commit-visible-execution";
                lease.EstablishAuthority(executionId);
                var outcomes = await EvidenceReceiptStore.EvidenceReceiptKeyLease.PublishInvocationCoreAsync(
                    storeRoot, repositoryNamespace, executionId, plan, manifest,
                    [new EvidenceReceiptPublicationRequest(lease, producer.Id, sourceRoot, DateTimeOffset.UtcNow)],
                    CancellationToken.None,
                    afterAuthorityCommitForTests: () => throw new IOException("Injected failure after authority commit."));

                var outcome = outcomes[producer.Id];
                Assert.False(outcome.Published);
                Assert.Equal("invocation-batch-commit-failed", outcome.Reason);

                var state = lease.ReadState();
                Assert.Null(state?.InFlightExecutionId);
                var payloadDirectoryName = Assert.IsType<string>(state?.PayloadDirectoryName);
                var payloadDirectory = Path.Combine(lease.DirectoryPath, payloadDirectoryName);
                Assert.True(Directory.Exists(payloadDirectory));

                var quotaEntry = Assert.Single(ReadQuotaEntries(storeRoot),
                    entry => entry.GetProperty("Key").GetString() == key);
                Assert.Equal(payloadDirectoryName, quotaEntry.GetProperty("PayloadDirectoryName").GetString());
                Assert.Equal(0, quotaEntry.GetProperty("ReservedPayloadBytes").GetInt64());
                Assert.Empty(quotaEntry.GetProperty("PayloadReservations").EnumerateArray());
                using (var payloadRoot = CoverageRunOutputLease.AcquireArtifactDirectory(payloadDirectory,
                           createMissing: false, requirePrivate: true)!)
                    Assert.Equal(EvidenceReceiptQuota.MeasurePayload(payloadRoot),
                        quotaEntry.GetProperty("PayloadBytes").GetInt64());

                var restoreWriter = new EvidenceArtifactWriter(producer, restoreRoot);
                var restored = await lease.ReadReceiptAsync(producer, restoreWriter, CancellationToken.None);
                Assert.Equal(EvidenceReceiptReadOutcome.Restored, restored.Outcome);
                Assert.Equal(EvidenceProducerOutcome.Reused, restored.Result?.Outcome);
                Assert.Equal("committed payload", await File.ReadAllTextAsync(Path.Join(restoreRoot, "reports/committed.txt")));
            }
        }
        finally
        {
            DeleteIfPresent(storeRoot);
            DeleteIfPresent(sourceRoot);
            DeleteIfPresent(restoreRoot);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReceiptWithAlternateRegularFileModePublishesAndRestores(bool readOnly)
    {
        var storeRoot = NewRoot();
        var sourceRoot = NewRoot();
        var restoreRoot = NewRoot();
        string? inputPath = null;
        try
        {
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(restoreRoot);
            inputPath = Path.Join(sourceRoot, "inputs", "source.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
            await File.WriteAllTextAsync(inputPath, "selected input");
            if (OperatingSystem.IsWindows())
            {
                var attributes = File.GetAttributes(inputPath);
                File.SetAttributes(inputPath, readOnly
                    ? attributes | FileAttributes.ReadOnly
                    : attributes & ~FileAttributes.ReadOnly);
            }
            else
            {
                var readOnlyMode = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
                var groupWritableMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead
                    | UnixFileMode.GroupWrite | UnixFileMode.OtherRead;
                var mode = readOnly ? readOnlyMode : groupWritableMode;
                File.SetUnixFileMode(inputPath, mode);
            }

            var inputMode = OperatingSystem.IsWindows()
                ? ((File.GetAttributes(inputPath) & FileAttributes.ReadOnly) != 0 ? 0x8124 : 0x81B6)
                : 0x8000 | (int)File.GetUnixFileMode(inputPath);
            var expectedWritableMode = OperatingSystem.IsWindows() ? 0x81B6 : 0x81B4;
            Assert.Equal(readOnly ? 0x8124 : expectedWritableMode, inputMode);

            var producer = ReceiptProducer();
            var plan = CreatePlan(producer);
            var sourceWriter = new EvidenceArtifactWriter(producer, sourceRoot);
            await sourceWriter.WriteAsync("report", "reports/result.txt", Encoding.UTF8.GetBytes("receipt payload"));
            var manifest = EvidenceManifestBuilder.Build(plan,
                [new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Passed, ["assertion"],
                    Artifacts: sourceWriter.WrittenArtifacts)]);
            var repositoryNamespace = Hash("mode-repo").ToLowerInvariant();
            var key = Hash(readOnly ? "readonly-input-key" : "nondefault-input-key").ToLowerInvariant();
            var identity = new EvidenceProducerIdentity(key, repositoryNamespace,
                [new EvidenceProducerInputFile("inputs/source.cs", inputMode,
                    new FileInfo(inputPath).Length, await HashFileAsync(inputPath))],
                new Dictionary<string, string>(StringComparer.Ordinal)
                { ["tools"] = Hash("tool-v1").ToLowerInvariant() });
            var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
            { RootDirectory = storeRoot, RepositoryNamespace = repositoryNamespace });
            var publisher = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
            using (publisher)
            {
                publisher.EstablishAuthority("alternate-mode-execution");
                var outcomes = await store.PublishInvocationAsync("alternate-mode-execution", plan, manifest,
                    [new EvidenceReceiptPublicationRequest(publisher, producer.Id, sourceRoot, DateTimeOffset.UtcNow)
                    { Identity = identity }], CancellationToken.None);
                Assert.True(outcomes[producer.Id].Published, outcomes[producer.Id].Reason);
            }

            var reader = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
            using (reader)
            {
                var restoreWriter = new EvidenceArtifactWriter(producer, restoreRoot);
                var restored = await reader.ReadReceiptAsync(producer, restoreWriter, CancellationToken.None);
                Assert.Equal(EvidenceReceiptReadOutcome.Restored, restored.Outcome);
                Assert.Equal(inputMode, Assert.Single(restored.InputIdentity!.Files).Mode);
                Assert.Equal(EvidenceProducerOutcome.Reused, restored.Result?.Outcome);
                var restoredText = await File.ReadAllTextAsync(Path.Join(restoreRoot, "reports/result.txt"));
                Assert.Equal("receipt payload", restoredText);
                Assert.True(await restoreWriter.VerifyWrittenArtifactsAsync());
            }
        }
        finally
        {
            if (OperatingSystem.IsWindows() && inputPath is not null && File.Exists(inputPath))
                File.SetAttributes(inputPath, FileAttributes.Normal);
            DeleteIfPresent(storeRoot);
            DeleteIfPresent(sourceRoot);
            DeleteIfPresent(restoreRoot);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0x4000)]
    [InlineData(0x18000)]
    public void ReceiptIdentityRejectsNonregularOrOutOfRangeFileModes(int mode)
    {
        var identity = new EvidenceProducerIdentity(Hash("invalid-mode-key").ToLowerInvariant(),
            Hash("repo").ToLowerInvariant(),
            [new EvidenceProducerInputFile("inputs/source.cs", mode, 1, Hash("input").ToLowerInvariant())],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["tools"] = Hash("tools").ToLowerInvariant() });

        Assert.Throws<InvalidDataException>(() => EvidenceReceiptInputIdentity.Create(identity));
    }

    [Fact]
    public async Task ReceiptRestoreAcceptsDeclarationArraysInDifferentOrdersForTheSameKey()
    {
        var storeRoot = NewRoot();
        var sourceRoot = NewRoot();
        var restoreRoot = NewRoot();
        try
        {
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(restoreRoot);
            var firstSlot = new EvidenceArtifactSlot("report-a", "reports", "text/plain", true, 1024)
            { MaximumFiles = 4 };
            var secondSlot = new EvidenceArtifactSlot("report-b", "reports", "text/plain", true, 1024)
            { MaximumFiles = 4 };
            var producer = new EvidenceProducerDeclaration("producer", "unit", "1", [], ["assertion", "secondary"],
                [firstSlot, secondSlot], 30)
            {
                Inputs = new EvidenceProducerInputs(["src/**", "tests/**"], ["obj/**", "bin/**"])
            };
            var reorderedProducer = producer with
            {
                AssertionIds = ["secondary", "assertion"],
                ArtifactSlots = [secondSlot, firstSlot],
                Inputs = new EvidenceProducerInputs(["tests/**", "src/**"], ["bin/**", "obj/**"])
            };
            var originDeclarationHash = EvidenceProducerIdentityBuilder.CanonicalDeclarationHash(producer);
            var reorderedDeclarationHash = EvidenceProducerIdentityBuilder.CanonicalDeclarationHash(reorderedProducer);
            Assert.Equal(originDeclarationHash, reorderedDeclarationHash);

            var plan = CreatePlan(producer);
            var sourceWriter = new EvidenceArtifactWriter(producer, sourceRoot);
            await sourceWriter.WriteAsync("report-a", "reports/a.txt", Encoding.UTF8.GetBytes("alpha"));
            await sourceWriter.WriteAsync("report-b", "reports/b.txt", Encoding.UTF8.GetBytes("bravo"));
            var manifest = EvidenceManifestBuilder.Build(plan,
                [new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Passed, ["assertion"],
                    Artifacts: sourceWriter.WrittenArtifacts)]);
            var repositoryNamespace = Hash("declaration-order-repo").ToLowerInvariant();
            var key = Hash("same-normalized-declaration-key").ToLowerInvariant();
            var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
            { RootDirectory = storeRoot, RepositoryNamespace = repositoryNamespace });
            const string executionId = "declaration-order-execution";
            var publisher = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
            using (publisher)
            {
                publisher.EstablishAuthority(executionId);
                var publication = await publisher.PublishAsync(executionId, plan, manifest, producer.Id,
                    sourceRoot, DateTimeOffset.UtcNow, CancellationToken.None);
                Assert.True(publication.Published, publication.Reason);
            }

            var reader = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
            using (reader)
            {
                var restoreWriter = new EvidenceArtifactWriter(reorderedProducer, restoreRoot);
                var restored = await reader.ReadReceiptAsync(reorderedProducer, restoreWriter, CancellationToken.None);
                Assert.Equal(EvidenceReceiptReadOutcome.Restored, restored.Outcome);
                Assert.Equal(EvidenceProducerOutcome.Reused, restored.Result?.Outcome);
                Assert.Equal("alpha", await File.ReadAllTextAsync(Path.Join(restoreRoot, "reports/a.txt")));
                Assert.Equal("bravo", await File.ReadAllTextAsync(Path.Join(restoreRoot, "reports/b.txt")));
                Assert.True(await restoreWriter.VerifyWrittenArtifactsAsync());
            }
        }
        finally
        {
            DeleteIfPresent(storeRoot);
            DeleteIfPresent(sourceRoot);
            DeleteIfPresent(restoreRoot);
        }
    }

    [Theory]
    [InlineData("reports/Foo/a.txt", "reports/foo/b.txt", "reports/Foo", "reports/foo")]
    [InlineData("reports/A.txt", "reports/a.txt", "reports", "reports")]
    public async Task PublicationRejectsPortableCaseCollisionsWithoutInvalidatingFreshManifest(
        string firstPath, string secondPath, string firstRoot, string secondRoot)
    {
        var storeRoot = NewRoot();
        var sourceRoot = NewRoot();
        try
        {
            Directory.CreateDirectory(sourceRoot);
            var producer = ReceiptProducer() with
            {
                ArtifactSlots =
                [
                    new EvidenceArtifactSlot("first", firstRoot, "text/plain", true, 128),
                    new EvidenceArtifactSlot("second", secondRoot, "text/plain", true, 128),
                ]
            };
            var plan = CreatePlan(producer);
            var sourceWriter = new EvidenceArtifactWriter(producer, sourceRoot);
            var sameBytes = Encoding.UTF8.GetBytes("same bytes");
            await sourceWriter.WriteAsync("first", firstPath, sameBytes);
            await sourceWriter.WriteAsync("second", secondPath, sameBytes);
            var manifest = EvidenceManifestBuilder.Build(plan,
                [new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Passed, ["assertion"],
                    Artifacts: sourceWriter.WrittenArtifacts)]);
            Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
            Assert.Equal(EvidenceExecutionVerdict.Passed, manifest.ExecutionVerdict);

            var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
            { RootDirectory = storeRoot, RepositoryNamespace = Hash("repo") });
            var key = Hash("case-colliding-paths");
            const string executionId = "case-collision-execution";
            var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
            using (lease)
            {
                lease.EstablishAuthority(executionId);
                var refused = await lease.PublishAsync(executionId, plan, manifest, producer.Id,
                    sourceRoot, DateTimeOffset.UtcNow, CancellationToken.None);
                Assert.False(refused.Published);
                Assert.Equal("payload-publication-failed", refused.Reason);
                Assert.Null(lease.ReadState()?.PayloadDirectoryName);
                Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(lease.DirectoryPath),
                    path => Path.GetFileName(path).StartsWith("payload-", StringComparison.Ordinal));

                lease.SettleExecution(executionId);
                Assert.Null(lease.ReadState()?.InFlightExecutionId);
            }
        }
        finally
        {
            DeleteIfPresent(storeRoot);
            DeleteIfPresent(sourceRoot);
        }
    }

    [Fact]
    public async Task LargestEligibleArtifactStreamsThroughPublicationAndRestoreWithExactByteCustody()
    {
        var storeRoot = NewRoot();
        var sourceRoot = NewRoot();
        var restoreRoot = NewRoot();
        try
        {
            Directory.CreateDirectory(sourceRoot);
            var maximumBytes = EvidenceArtifactWriter.MaximumTotalArtifactBytes;
            var producer = ReceiptProducer() with
            {
                ArtifactSlots = [new EvidenceArtifactSlot("report", "reports", "application/octet-stream", true, maximumBytes)
                { MaximumFiles = 1 }]
            };
            var plan = CreatePlan(producer);
            var writer = new EvidenceArtifactWriter(producer, sourceRoot);
            var artifact = await writer.WriteAsync("report", "reports/largest.bin", new PatternStream(maximumBytes));
            Assert.Equal(maximumBytes, artifact.LengthBytes);

            // Protects the maximum accepted artifact boundary across writer, receipt staging and restore, with hashes
            // calculated from the actual source and restored files rather than trusting only writer metadata.
            var sourcePath = Path.Join(sourceRoot, "reports", "largest.bin");
            var sourceHash = await HashFileAsync(sourcePath);
            Assert.Equal(artifact.Sha256, sourceHash);
            var manifest = EvidenceManifestBuilder.Build(plan,
                [new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Passed, ["assertion"],
                    Artifacts: writer.WrittenArtifacts)]);
            Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));

            var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
            { RootDirectory = storeRoot, RepositoryNamespace = Hash("repo") });
            var key = Hash("maximum-artifact");
            var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
            using (lease)
            {
                lease.EstablishAuthority("maximum-artifact-execution");
                var publication = await lease.PublishAsync("maximum-artifact-execution", plan, manifest,
                    producer.Id, sourceRoot, DateTimeOffset.UtcNow, CancellationToken.None);
                Assert.True(publication.Published, publication.Reason);
                var storedPath = Path.Join(storeRoot, Hash("repo").ToLowerInvariant(), key.ToLowerInvariant(),
                    publication.PayloadDirectoryName!, "artifacts", "reports", "largest.bin");
                Assert.Equal(maximumBytes, new FileInfo(storedPath).Length);
                Assert.Equal(sourceHash, await HashFileAsync(storedPath));

                var restoredWriter = new EvidenceArtifactWriter(producer, restoreRoot);
                var restored = await lease.ReadReceiptAsync(producer, restoredWriter, CancellationToken.None);
                Assert.Equal(EvidenceReceiptReadOutcome.Restored, restored.Outcome);
                Assert.Equal(manifest.ManifestDigest, restored.OriginManifest?.ManifestDigest);
                Assert.Equal(manifest.ManifestDigest, restored.Result?.OriginManifestDigest);
                Assert.Equal(EvidenceProducerOutcome.Reused, restored.Result?.Outcome);
                var restoredArtifact = Assert.Single(restoredWriter.WrittenArtifacts);
                Assert.Equal(maximumBytes, restoredArtifact.LengthBytes);
                Assert.Equal(artifact.Sha256, restoredArtifact.Sha256);
                Assert.True(await restoredWriter.VerifyWrittenArtifactsAsync());
                var restoredPath = Path.Join(restoreRoot, "reports", "largest.bin");
                Assert.Equal(maximumBytes, new FileInfo(restoredPath).Length);
                Assert.Equal(sourceHash, await HashFileAsync(restoredPath));
            }
        }
        finally
        {
            DeleteIfPresent(storeRoot);
            DeleteIfPresent(sourceRoot);
            DeleteIfPresent(restoreRoot);
        }
    }

    [Fact]
    public async Task ReplacementPublicationDeletesRetiredPayloadOnlyAfterConfirmedCommitCleanup()
    {
        var storeRoot = NewRoot();
        var sourceA = NewRoot();
        var sourceB = NewRoot();
        try
        {
            Directory.CreateDirectory(sourceA);
            Directory.CreateDirectory(sourceB);
            var initial = await CreatePublishedReceiptAsync(storeRoot, sourceA, "first origin");
            var oldPayloadPath = Path.Join(storeRoot, Hash("repo").ToLowerInvariant(), initial.Key, initial.PayloadName);
            Assert.True(Directory.Exists(oldPayloadPath));

            var writer = new EvidenceArtifactWriter(initial.Producer, sourceB);
            await writer.WriteAsync("report", "reports/report.txt", Encoding.UTF8.GetBytes("replacement origin"));
            var plan = CreatePlan(initial.Producer);
            var manifest = EvidenceManifestBuilder.Build(plan, [new EvidenceProducerResult(initial.Producer.Id,
                EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: writer.WrittenArtifacts)]);
            var lease = Assert.Single(await initial.Store.AcquireAsync([initial.Key], CancellationToken.None));
            string replacementPayload;
            using (lease)
            {
                lease.EstablishAuthority("execution-2");
                var publication = await lease.PublishAsync("execution-2", plan, manifest, initial.Producer.Id,
                    sourceB, DateTimeOffset.UtcNow, CancellationToken.None);
                Assert.True(publication.Published, publication.Reason);
                replacementPayload = publication.PayloadDirectoryName!;
            }

            Assert.False(Directory.Exists(oldPayloadPath));
            Assert.True(Directory.Exists(Path.Join(storeRoot, Hash("repo").ToLowerInvariant(), initial.Key, replacementPayload)));
            var entry = Assert.Single(ReadQuotaEntries(storeRoot));
            Assert.Empty(entry.GetProperty("RetiredPayloads").EnumerateArray());
        }
        finally { DeleteIfPresent(storeRoot); DeleteIfPresent(sourceA); DeleteIfPresent(sourceB); }
    }

    [Fact]
    public async Task PublicationCopiesOnlyDeclaredArtifactsAndPreservesOtherInvocationOutput()
    {
        var storeRoot = NewRoot();
        var sourceRoot = NewRoot();
        var restoredRoot = NewRoot();
        try
        {
            Directory.CreateDirectory(sourceRoot);
            var producer = ReceiptProducer();
            var writer = new EvidenceArtifactWriter(producer, sourceRoot);
            await writer.WriteAsync("report", "reports/report.txt", Encoding.UTF8.GetBytes("declared"));
            await File.WriteAllTextAsync(Path.Join(sourceRoot, "unlisted.txt"), "not in producer inventory");
            var plan = CreatePlan(producer);
            var manifest = EvidenceManifestBuilder.Build(plan,
                [new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: writer.WrittenArtifacts)]);
            var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
            { RootDirectory = storeRoot, RepositoryNamespace = Hash("repo") });
            var key = Hash("source-inventory");
            var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
            using (lease)
            {
                lease.EstablishAuthority("execution-source-inventory");
                var result = await lease.PublishAsync("execution-source-inventory", plan, manifest, producer.Id,
                    sourceRoot, DateTimeOffset.UtcNow, CancellationToken.None);
                Assert.True(result.Published, result.Reason);
                var restored = await lease.ReadReceiptAsync(producer, new EvidenceArtifactWriter(producer, restoredRoot), CancellationToken.None);
                Assert.Equal(EvidenceReceiptReadOutcome.Restored, restored.Outcome);
                Assert.Equal("declared", await File.ReadAllTextAsync(Path.Join(restoredRoot, "reports", "report.txt")));
                Assert.False(File.Exists(Path.Join(restoredRoot, "unlisted.txt")));
                Assert.Equal("not in producer inventory", await File.ReadAllTextAsync(Path.Join(sourceRoot, "unlisted.txt")));
            }
        }
        finally { DeleteIfPresent(storeRoot); DeleteIfPresent(sourceRoot); DeleteIfPresent(restoredRoot); }
    }

    [Fact]
    public async Task RestoreOutputFailureKeepsVerifiedSharedReceipt()
    {
        var storeRoot = NewRoot();
        var sourceRoot = NewRoot();
        var restoreRoot = NewRoot();
        try
        {
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(restoreRoot);
            var published = await CreatePublishedReceiptAsync(storeRoot, sourceRoot, "valid shared payload");
            File.WriteAllText(Path.Join(restoreRoot, "reports"), "blocks the restore directory");
            var lease = Assert.Single(await published.Store.AcquireAsync([published.Key], CancellationToken.None));
            using (lease)
            {
                var result = await lease.ReadReceiptAsync(published.Producer,
                    new EvidenceArtifactWriter(published.Producer, restoreRoot), CancellationToken.None);
                Assert.Equal(EvidenceReceiptReadOutcome.Miss, result.Outcome);
                Assert.Equal("restore-or-lru-unavailable", result.Reason);
                Assert.Equal(published.PayloadName, lease.ReadState()?.PayloadDirectoryName);
            }
            Assert.True(EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
            { RootDirectory = storeRoot, RepositoryNamespace = Hash("repo") }).Peek(published.Key).IsCandidate);
        }
        finally { DeleteIfPresent(storeRoot); DeleteIfPresent(sourceRoot); DeleteIfPresent(restoreRoot); }
    }

    [Fact]
    public async Task LruUpdateFailureKeepsVerifiedSharedReceipt()
    {
        var storeRoot = NewRoot();
        var sourceRoot = NewRoot();
        var restoreRoot = NewRoot();
        try
        {
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(restoreRoot);
            var published = await CreatePublishedReceiptAsync(storeRoot, sourceRoot, "valid shared payload");
            var failingClock = new ThrowOnCallTimeProvider(4, DateTimeOffset.UtcNow);
            var reader = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
            { RootDirectory = storeRoot, RepositoryNamespace = Hash("repo"), Clock = failingClock });
            var lease = Assert.Single(await reader.AcquireAsync([published.Key], CancellationToken.None));
            using (lease)
            {
                var result = await lease.ReadReceiptAsync(published.Producer,
                    new EvidenceArtifactWriter(published.Producer, restoreRoot), CancellationToken.None);
                Assert.Equal(EvidenceReceiptReadOutcome.Miss, result.Outcome);
                Assert.Equal("restore-or-lru-unavailable", result.Reason);
                Assert.Equal(published.PayloadName, lease.ReadState()?.PayloadDirectoryName);
            }
            Assert.True(EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
            { RootDirectory = storeRoot, RepositoryNamespace = Hash("repo") }).Peek(published.Key).IsCandidate);
        }
        finally { DeleteIfPresent(storeRoot); DeleteIfPresent(sourceRoot); DeleteIfPresent(restoreRoot); }
    }

    [Fact]
    public async Task CoverageReceiptRequiresClosedInventoryEvenWhenSlotIsOptional()
    {
        var storeRoot = NewRoot();
        var sourceRoot = NewRoot();
        try
        {
            Directory.CreateDirectory(sourceRoot);
            var coverageSlot = CoverageEvidenceInventory.CreateSlot() with { Required = false };
            var producer = new EvidenceProducerDeclaration("coverage", "coverage", "1", [], ["assertion"], [coverageSlot], 30)
            {
                Inputs = new EvidenceProducerInputs(["**"], [])
            };
            var plan = CreatePlan(producer);
            var sourceWriter = new EvidenceArtifactWriter(producer, sourceRoot);
            await sourceWriter.WriteAsync(coverageSlot.LogicalName, "coverage/only-optional.txt", Encoding.UTF8.GetBytes("optional"));
            var manifest = EvidenceManifestBuilder.Build(plan,
                [new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: sourceWriter.WrittenArtifacts)]);
            var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions { RootDirectory = storeRoot, RepositoryNamespace = Hash("repo") });
            var lease = Assert.Single(await store.AcquireAsync([Hash("coverage-key")], CancellationToken.None));
            using (lease)
            {
                lease.EstablishAuthority("coverage-run");
                var result = await lease.PublishAsync("coverage-run", plan, manifest, producer.Id, sourceRoot,
                    DateTimeOffset.UtcNow, CancellationToken.None);
                Assert.False(result.Published);
                Assert.Equal("inventory-required-file-missing", result.Reason);
                Assert.Equal("coverage-run", lease.ReadState()?.InFlightExecutionId);
                Assert.Null(lease.ReadState()?.PayloadDirectoryName);
            }
        }
        finally { DeleteIfPresent(storeRoot); DeleteIfPresent(sourceRoot); }
    }

    [Fact]
    public async Task ReceiptAdmissionRejectsNonportablePathsFromLegacySingleFileSlots()
    {
        var storeRoot = NewRoot();
        var sourceRoot = NewRoot();
        try
        {
            Directory.CreateDirectory(sourceRoot);
            var producer = new EvidenceProducerDeclaration("producer", "unit", "1", [], ["assertion"],
                [new EvidenceArtifactSlot("report", "reports", "text/plain", true, 1024)], 30)
            { Inputs = new EvidenceProducerInputs(["**"], []) };
            var plan = CreatePlan(producer);
            var writer = new EvidenceArtifactWriter(producer, sourceRoot);
            var report = await writer.WriteAsync("report", "reports/result.txt", Encoding.UTF8.GetBytes("legacy-valid fresh output"));
            // Legacy manifests accept backslashes for an existing file. Receipt custody requires the portable spelling.
            EvidenceArtifactResult[] artifacts = [report with { RelativePath = @"reports\result.txt" }];
            Assert.True(EvidenceArtifactValidation.AreValid(producer, artifacts));
            var rejected = await Assert.ThrowsAsync<InvalidDataException>(() =>
                EvidenceReceiptPayload.VerifySourceArtifactsAsync(sourceRoot, producer, artifacts, CancellationToken.None));
            Assert.Equal("Receipt artifact path is not portable.", rejected.Message);
            var manifest = EvidenceManifestBuilder.Build(plan,
                [new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: artifacts)]);
            var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
            { RootDirectory = storeRoot, RepositoryNamespace = Hash("repo") });
            var lease = Assert.Single(await store.AcquireAsync([Hash("unsafe-path-key")], CancellationToken.None));
            using (lease)
            {
                lease.EstablishAuthority("unsafe-path-execution");
                var result = await lease.PublishAsync("unsafe-path-execution", plan, manifest, producer.Id,
                    sourceRoot, DateTimeOffset.UtcNow, CancellationToken.None);
                Assert.False(result.Published);
                Assert.Equal("payload-publication-failed", result.Reason);
                Assert.Null(lease.ReadState()?.PayloadDirectoryName);
            }
        }
        finally { DeleteIfPresent(storeRoot); DeleteIfPresent(sourceRoot); }
    }

    [Fact]
    public async Task CorruptOwnedPayloadIsInvalidatedAsMissBeforeAnyArtifactCopy()
    {
        var storeRoot = NewRoot();
        var sourceRoot = NewRoot();
        var restoreRoot = NewRoot();
        try
        {
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(restoreRoot);
            var producer = ReceiptProducer();
            var plan = CreatePlan(producer);
            var sourceWriter = new EvidenceArtifactWriter(producer, sourceRoot);
            await sourceWriter.WriteAsync("report", "reports/a.txt", Encoding.UTF8.GetBytes("valid"));
            var manifest = EvidenceManifestBuilder.Build(plan, [new EvidenceProducerResult("producer", EvidenceProducerOutcome.Passed,
                ["assertion"], Artifacts: sourceWriter.WrittenArtifacts)]);
            var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions { RootDirectory = storeRoot, RepositoryNamespace = Hash("repo") });
            var key = Hash("producer-key").ToLowerInvariant();
            var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
            string payloadName;
            using (lease)
            {
                lease.EstablishAuthority("execution-1");
                var published = await lease.PublishAsync("execution-1", plan, manifest, producer.Id, sourceRoot,
                    DateTimeOffset.UtcNow, CancellationToken.None);
                Assert.True(published.Published, published.Reason);
                payloadName = published.PayloadDirectoryName!;
            }

            var payload = Path.Combine(storeRoot, Hash("repo").ToLowerInvariant(), key.ToLowerInvariant(), payloadName, "artifacts", "reports", "a.txt");
            await File.WriteAllTextAsync(payload, "tampered");
            var foreignTarget = Path.Combine(sourceRoot, "foreign-target.txt");
            await File.WriteAllTextAsync(foreignTarget, "preserve");
            var foreignLink = Path.Combine(Path.GetDirectoryName(payload)!, "foreign-link");
            TestFileSystem.CreateFileLinkOrSkip(foreignLink, foreignTarget);
            var contender = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
            using (contender)
            {
                var writer = new EvidenceArtifactWriter(producer, restoreRoot);
                var read = await contender.ReadReceiptAsync(producer, writer, CancellationToken.None);
                Assert.Equal(EvidenceReceiptReadOutcome.Miss, read.Outcome);
                Assert.Empty(writer.WrittenArtifacts);
                Assert.False(File.Exists(Path.Join(restoreRoot, "reports/a.txt")));
                Assert.Null(contender.ReadState()?.PayloadDirectoryName);
            }

            var invalidated = Assert.Single(ReadQuotaEntries(storeRoot), entry => entry.GetProperty("Key").GetString() == key);
            Assert.Null(invalidated.GetProperty("PayloadDirectoryName").GetString());
            Assert.Equal(0, invalidated.GetProperty("PayloadBytes").GetInt64());
            var retired = Assert.Single(invalidated.GetProperty("RetiredPayloads").EnumerateArray());
            Assert.Equal(payloadName, retired.GetProperty("DirectoryName").GetString());
            Assert.True(retired.GetProperty("Bytes").GetInt64() > 0);
            var invalidPayloadDirectory = Path.Combine(storeRoot, Hash("repo").ToLowerInvariant(), key, payloadName);
            Assert.True(Directory.Exists(invalidPayloadDirectory));

            // A new input identity receives a new key while cleanup of the old, known payload is deferred.
            var changedKey = Hash("producer-key-changed-input");
            var changedWriter = new EvidenceArtifactWriter(producer, sourceRoot);
            await changedWriter.WriteAsync("report", "reports/changed.txt", Encoding.UTF8.GetBytes("changed input"));
            var changedManifest = EvidenceManifestBuilder.Build(plan,
                [new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Passed, ["assertion"],
                    Artifacts: changedWriter.WrittenArtifacts)]);
            using (var changedLease = Assert.Single(await store.AcquireAsync([changedKey], CancellationToken.None)))
            {
                changedLease.EstablishAuthority("changed-input-execution");
                var changedPublication = await changedLease.PublishAsync("changed-input-execution", plan,
                    changedManifest, producer.Id, sourceRoot, DateTimeOffset.UtcNow, CancellationToken.None);
                Assert.True(changedPublication.Published, changedPublication.Reason);
            }

            EvidenceReceiptQuota.Maintain(storeRoot, DateTimeOffset.UtcNow);
            Assert.True(Directory.Exists(invalidPayloadDirectory)); // Retired custody remains while its foreign link prevents safe deletion.
            Assert.Equal(payloadName, Assert.Single(ReadQuotaEntries(storeRoot)
                .Single(entry => entry.GetProperty("Key").GetString() == key)
                .GetProperty("RetiredPayloads").EnumerateArray()).GetProperty("DirectoryName").GetString());

            File.Delete(foreignLink);
            EvidenceReceiptQuota.Maintain(storeRoot, DateTimeOffset.UtcNow);
            EvidenceReceiptQuota.Maintain(storeRoot, DateTimeOffset.UtcNow);
            var reclaimed = Assert.Single(ReadQuotaEntries(storeRoot), entry => entry.GetProperty("Key").GetString() == key);
            Assert.Empty(reclaimed.GetProperty("RetiredPayloads").EnumerateArray());
            Assert.False(Directory.Exists(Path.Combine(storeRoot, Hash("repo").ToLowerInvariant(), key, payloadName)));
            Assert.Equal("preserve", await File.ReadAllTextAsync(foreignTarget));
        }
        finally
        {
            DeleteIfPresent(storeRoot);
            DeleteIfPresent(sourceRoot);
            DeleteIfPresent(restoreRoot);
        }
    }

    private static EvidenceProducerDeclaration ReceiptProducer(string producerId = "producer") => new(producerId, "unit", "1", [], ["assertion"],
        [new EvidenceArtifactSlot("report", "reports", "text/plain", true, 1024) { MaximumFiles = 4 }], 30)
    {
        Inputs = new EvidenceProducerInputs(["**"], [])
    };

    private static EvidencePlan CreatePlan(params EvidenceProducerDeclaration[] producers)
    {
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [], producers,
            producers.Select(producer => new EvidenceObligation("obligation-" + producer.Id, "risk", "reason", [producer.Id], "assertion")).ToArray());
        var policy = new EvidencePolicy("policy", "1", profile.Id, [profile], []);
        var paths = Array.Empty<NormalizedDiffPath>();
        var plan = new EvidencePlan("1.1", policy.Id, EvidenceDigest.CanonicalSha256(policy),
            EvidenceDigest.CanonicalSha256(paths), profile, paths, [], string.Empty, policy);
        return plan with { PlanDigest = EvidenceDigest.CanonicalSha256(plan) };
    }

    private static async Task<(EvidenceReceiptStore Store, EvidenceProducerDeclaration Producer, string Key, string PayloadName)>
        CreatePublishedReceiptAsync(string storeRoot, string sourceRoot, string content)
    {
        var producer = ReceiptProducer();
        var plan = CreatePlan(producer);
        var writer = new EvidenceArtifactWriter(producer, sourceRoot);
        await writer.WriteAsync("report", "reports/report.txt", Encoding.UTF8.GetBytes(content));
        var manifest = EvidenceManifestBuilder.Build(plan,
            [new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: writer.WrittenArtifacts)]);
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = storeRoot, RepositoryNamespace = Hash("repo") });
        var key = Hash("shared-receipt").ToLowerInvariant();
        var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        using (lease)
        {
            lease.EstablishAuthority("execution-1");
            var publication = await lease.PublishAsync("execution-1", plan, manifest, producer.Id,
                sourceRoot, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.True(publication.Published, publication.Reason);
            return (store, producer, key, publication.PayloadDirectoryName!);
        }
    }

    private static JsonElement[] ReadQuotaEntries(string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Join(root, "quota-index.json")));
        return document.RootElement.GetProperty("Entries").EnumerateArray().Select(static entry => entry.Clone()).ToArray();
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("g0000000000000000000000000000000000000000000000000000000000000000")]
    public void InvalidNamespaceIsRejected(string repositoryNamespace)
    {
        Assert.Throws<ArgumentException>(() => EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
        {
            RootDirectory = NewRoot(),
            RepositoryNamespace = repositoryNamespace
        }));
    }

    [Fact]
    public void NonpositiveOrInfiniteWaitIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
        {
            RepositoryNamespace = Hash("repo"),
            WaitTimeout = Timeout.InfiniteTimeSpan
        }));
        Assert.NotNull(EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
        {
            RepositoryNamespace = Hash("repo"),
            WaitTimeout = TimeSpan.FromDays(365)
        }));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "appsurface-receipts-" + Guid.NewGuid().ToString("N"));
    private static void DeleteIfPresent(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        internal void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class ThrowOnCallTimeProvider(int throwOnCall, DateTimeOffset utcNow) : TimeProvider
    {
        private int _calls;
        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Increment(ref _calls) == throwOnCall) throw new IOException("Injected transient LRU time source failure.");
            return utcNow;
        }
    }

    private sealed class PatternStream(long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, length - _position);
            for (var index = 0; index < count; index++) buffer[index] = (byte)((_position + index) & 0xff);
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
    }
}
