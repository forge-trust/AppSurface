using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

// The large native-tree refusal deliberately reaches this process's descriptor limit; isolate its peers.
[Collection(EvidenceReuseSyntheticLocalContextCollection.Name)]
public sealed class EvidenceReceiptStoreCoverageTests
{
    [Fact]
    public void OpenRejectsStoreRootsThatOverlapProtectedInputsOrOutputs()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var nested = TestPathUtils.PathUnder(root, "input");
        var parent = TestPathUtils.PathUnder(temporary.Path, "parent");
        var childStore = TestPathUtils.PathUnder(parent, "store");
        var fileSystemRoot = Path.GetPathRoot(temporary.Path)!;
        var siblingStore = TestPathUtils.PathUnder(temporary.Path, "receipts-sibling");

        Assert.Throws<InvalidOperationException>(() => EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = Hash("store-overlap"), ForbiddenRoots = [nested] }));
        Assert.Throws<InvalidOperationException>(() => EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = childStore, RepositoryNamespace = Hash("store-overlap"), ForbiddenRoots = [parent] }));
        Assert.Throws<InvalidOperationException>(() => EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = Hash("filesystem-root-overlap"), ForbiddenRoots = [fileSystemRoot] }));
        var sibling = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = Hash("sibling-store"), ForbiddenRoots = [siblingStore] });
        Assert.NotNull(sibling);
        Assert.False(Directory.Exists(root));
        Assert.False(Directory.Exists(parent));
    }

    [Fact]
    public void InputIdentityValidationRejectsOversizedEncodedInventoriesThroughTypedAndPersistedPaths()
    {
        const int fileCount = 60_000;
        var hash = Hash("oversized-receipt-input").ToLowerInvariant();
        var leaf = new string('a', 190);
        var receiptFiles = Enumerable.Range(0, fileCount)
            .Select(index => new EvidenceReceiptInputFile($"inputs/{index:D5}/{leaf}", hash, 0, 1))
            .ToArray();
        var producerFiles = receiptFiles.Select(file => new EvidenceProducerInputFile(
            file.RelativePath, file.Mode, file.LengthBytes, file.Sha256)).ToArray();

        Assert.Throws<InvalidDataException>(() => new EvidenceReceiptInputIdentity(
            new Dictionary<string, string>(StringComparer.Ordinal), receiptFiles).ValidateAndEncode());
        Assert.Throws<InvalidDataException>(() => EvidenceReceiptInputIdentity.Create(new EvidenceProducerIdentity(
            Hash("oversized-receipt-key"), Hash("oversized-receipt-repository"), producerFiles,
            new Dictionary<string, string>(StringComparer.Ordinal))));
    }

    [Fact]
    public void ExactTreeRefusesNestingBeyondProductionBoundAndRetainsCallerOwnedTree()
    {
        using var temporary = TestDirectory.Create();
        var artifactPath = TestPathUtils.PathUnder(temporary.Path, "artifacts");
        using var artifactRoot = CoverageRunOutputLease.AcquireArtifactDirectory(artifactPath, true, true)!;
        var segments = Enumerable.Repeat("d", 34).ToArray();
        var relativeFile = string.Join('/', segments) + "/report.txt";
        var filePath = TestPathUtils.PathUnder(artifactPath, relativeFile);
        using (var parent = CoverageRunOutputLease.AcquireArtifactDirectory(Path.GetDirectoryName(filePath)!,
                   createMissing: true, requirePrivate: true)!)
        using (var report = parent.OpenArtifactFile("report.txt", createNew: true))
            report.Write("operator-owned report"u8);

        var failure = Assert.Throws<InvalidDataException>(() =>
            EvidenceReceiptPayload.VerifyExactTree(artifactRoot, [relativeFile]));

        Assert.Contains("nesting exceeds its bound", failure.Message, StringComparison.Ordinal);
        Assert.Equal("operator-owned report", File.ReadAllText(filePath));
        Assert.Equal(segments[0], Assert.Single(artifactRoot.EnumerateArtifactEntries(8)).Name);
    }

    [Fact]
    public void OwnedPayloadCleanupRefusesMissingAndNonDirectoryKeyRoots()
    {
        using var temporary = TestDirectory.Create();
        var payloadName = "payload-" + Guid.NewGuid().ToString("N");
        var missingRoot = TestPathUtils.PathUnder(temporary.Path, "missing-key-root");
        var blockingFile = TestPathUtils.PathUnder(temporary.Path, "file-key-root");
        File.WriteAllText(blockingFile, "operator-owned blocker");

        Assert.False(EvidenceReceiptStore.EvidenceReceiptKeyLease.TryDeleteOwnedPayload(missingRoot, payloadName));
        Assert.False(EvidenceReceiptStore.EvidenceReceiptKeyLease.TryDeleteOwnedPayload(blockingFile, payloadName));
        Assert.Equal("operator-owned blocker", File.ReadAllText(blockingFile));
    }

    [Fact]
    public async Task PublicationRefusedByByteQuotaPreservesInFlightAuthorityAndSourceReports()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("publication-byte-limit-repository").ToLowerInvariant();
        var key = Hash("publication-byte-limit-key").ToLowerInvariant();
        var producer = PublicationProducer("publication-byte-limit");
        var (plan, manifest, sourceRoot) = await CreatePublicationAttemptAsync(temporary.Path, producer, "candidate");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        const string execution = "publication-byte-limit-execution";
        lease.EstablishAuthority(execution);
        var authority = lease.ReadState();
        var entry = QuotaEntry(root, key);
        var existingCharge = entry.GetProperty("MetadataBytes").GetInt64()
            + entry.GetProperty("PayloadBytes").GetInt64() + entry.GetProperty("ReservedPayloadBytes").GetInt64();
        // A legacy reservation has no staged path to reconcile, so it keeps this charge without allocating gigabytes.
        var reservedBytes = EvidenceReceiptQuota.MaximumBytes - existingCharge - 8 * 1024;
        await LegacyAggregateReservationFixture.SeedAsync(root, repository, key, reservedBytes);

        var outcomes = await store.PublishInvocationAsync(execution, plan, manifest,
            [new EvidenceReceiptPublicationRequest(lease, producer.Id, sourceRoot, DateTimeOffset.UtcNow)],
            CancellationToken.None);

        var outcome = outcomes[producer.Id];
        Assert.False(outcome.Published);
        Assert.Equal("payload-quota-full", outcome.Reason);
        Assert.Equal(authority, lease.ReadState());
        Assert.Equal(reservedBytes, QuotaEntry(root, key).GetProperty("ReservedPayloadBytes").GetInt64());
        Assert.Empty(Directory.EnumerateDirectories(lease.DirectoryPath, "payload*"));
        Assert.Equal("replacement receipt payload",
            await File.ReadAllTextAsync(TestPathUtils.PathUnder(sourceRoot, "reports", "attempt.txt")));
    }

    [Fact]
    public async Task PublicationRejectsFilesystemRootContainingStoreAsSourceRoot()
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "source-root-filesystem-overlap");
        var (plan, manifest, sourceRoot) = await CreatePublicationAttemptAsync(temporary.Path, fixture.Producer,
            "source-root-filesystem-overlap-attempt");
        const string executionId = "source-root-filesystem-overlap-execution";
        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        lease.EstablishAuthority(executionId);
        var before = SnapshotStoreBytes(fixture.StoreRoot);

        var outcomes = await fixture.Store.PublishInvocationAsync(executionId, plan, manifest,
            [new EvidenceReceiptPublicationRequest(lease, fixture.Producer.Id, Path.GetPathRoot(fixture.StoreRoot)!,
                DateTimeOffset.UtcNow)], CancellationToken.None);

        Assert.False(outcomes[fixture.Producer.Id].Published);
        Assert.Equal("source-root-overlaps-receipt-store", outcomes[fixture.Producer.Id].Reason);
        AssertStoreSnapshotUnchanged(before, fixture.StoreRoot);
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
        Assert.True(Directory.Exists(TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key,
            fixture.PayloadName)));
        Assert.True(Directory.Exists(sourceRoot));
    }

    [Fact]
    public async Task AcquisitionNormalizesDeduplicatesAndReturnsKeysInCanonicalOrder()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = Hash("canonical-order-repository") });
        var lowerKey = Hash("canonical-order-a").ToLowerInvariant();
        var upperKey = Hash("canonical-order-z").ToUpperInvariant();

        var leases = await store.AcquireAsync([upperKey, lowerKey, lowerKey.ToUpperInvariant()], CancellationToken.None);
        try
        {
            Assert.Equal(new[] { lowerKey, upperKey.ToLowerInvariant() }.Order(StringComparer.Ordinal),
                leases.Select(lease => lease.Key));
            Assert.Equal(2, Directory.EnumerateDirectories(TestPathUtils.PathUnder(root,
                Hash("canonical-order-repository").ToLowerInvariant())).Count());
        }
        finally
        {
            foreach (var lease in leases.Reverse()) lease.Dispose();
        }
    }

    [Fact]
    public async Task InvalidAcquisitionInputDoesNotLeavePartialStoreState()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = Hash("invalid-acquisition-repository") });

        await Assert.ThrowsAsync<ArgumentException>(() => store.AcquireAsync(
            [Hash("valid-key"), "not-a-sha256-key"], CancellationToken.None));
        Assert.False(Directory.Exists(root));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.AcquireAsync(
            [Hash("valid-key")], TimeSpan.Zero, CancellationToken.None));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task EmptyAcquisitionAndPublicationDoNotCreateStoreRoot()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "empty-operations", "store");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = Hash("empty-operations-repository") });
        var producer = PublicationProducer("empty-operations-producer");
        var plan = PublicationPlan(producer);
        var manifest = EvidenceManifestBuilder.Build(plan,
            [new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: [])]);

        Assert.Empty(await store.AcquireAsync([], CancellationToken.None));
        Assert.Empty(await store.PublishInvocationAsync("empty-operations-execution", plan, manifest, [],
            CancellationToken.None));

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task PositiveOneTickAcquisitionBudgetExpiresBeforeCreatingReservationState()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "one-tick-budget", "store");
        var repository = Hash("one-tick-budget-repository").ToLowerInvariant();
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });

        await Assert.ThrowsAsync<TimeoutException>(() => store.AcquireAsync([Hash("one-tick-budget-key")],
            TimeSpan.FromTicks(1), CancellationToken.None));

        Assert.True(Directory.Exists(root));
        Assert.False(File.Exists(TestPathUtils.PathUnder(root, "quota-index.json")));
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(root, repository)));
    }

    [Fact]
    public async Task AuditMismatchRemainsQuarantinedAcrossLeaseReleaseAndRecoveryAttempt()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var key = Hash("audit-quarantine-transition");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = Hash("audit-transition-repository") });
        var first = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        first.EstablishAuthority("audit-owner", auditIntent: true, expectedAssertions: "required-result");
        first.MarkAuditMismatch("audit-owner", "required-result");
        first.SettleExecution("audit-owner");
        first.Dispose();

        using var recovery = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        var denied = recovery.ProposeReadReceipt();
        Assert.False(denied.IsCandidate);
        Assert.False(denied.IsUnavailable);
        Assert.Equal("audit-quarantined", denied.Reason);
        Assert.Throws<InvalidOperationException>(() => recovery.EstablishAuthority("ordinary-work"));

        recovery.EstablishAuthority("audit-recovery", auditIntent: true, expectedAssertions: "must-not-replace-owner-record");
        var state = recovery.ReadState();
        Assert.Equal("audit-recovery", state?.InFlightExecutionId);
        Assert.Equal("audit-recovery", state?.AuditIntentExecutionId);
        Assert.Equal("required-result", state?.AuditExpectedAssertions);
        Assert.True(state?.AuditMismatch);
    }

    [Fact]
    public async Task PublicationRefusesFutureWrongOwnerAndQuarantinedAuthorityWithoutChangingState()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "commit-passed-guards", "store");
        var repository = Hash("commit-passed-guards-repository").ToLowerInvariant();
        var key = Hash("commit-passed-guards-key").ToLowerInvariant();
        var producer = PublicationProducer("publication-guard");
        var (plan, manifest, sourceRoot) = await CreatePublicationAttemptAsync(temporary.Path, producer, "candidate");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        lease.EstablishAuthority("commit-owner");

        var before = lease.ReadState();
        var indexBeforeRefusal = await File.ReadAllBytesAsync(TestPathUtils.PathUnder(root, "quota-index.json"));
        var future = await lease.PublishAsync("commit-owner", plan, manifest, producer.Id, sourceRoot,
            DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
        Assert.False(future.Published);
        Assert.Equal("payload-publication-failed", future.Reason);

        var wrongOwner = await lease.PublishAsync("other-owner", plan, manifest, producer.Id, sourceRoot,
            DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.False(wrongOwner.Published);
        Assert.Equal("execution-authority-mismatch", wrongOwner.Reason);
        Assert.Equal(before, lease.ReadState());
        Assert.Equal(indexBeforeRefusal, await File.ReadAllBytesAsync(TestPathUtils.PathUnder(root, "quota-index.json")));

        var quarantined = before! with { AuditMismatch = true, AuditIntentExecutionId = null };
        EvidenceReceiptQuota.RecordState(root, repository, key, JsonSerializer.SerializeToUtf8Bytes(quarantined).Length,
            quarantined);
        var beforeQuarantineRefusal = lease.ReadState();
        var indexBeforeQuarantineRefusal = await File.ReadAllBytesAsync(TestPathUtils.PathUnder(root, "quota-index.json"));

        var refused = await lease.PublishAsync("commit-owner", plan, manifest, producer.Id, sourceRoot,
            DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.False(refused.Published);
        Assert.Equal("audit-quarantined", refused.Reason);

        Assert.Equal(beforeQuarantineRefusal, lease.ReadState());
        Assert.Equal(indexBeforeQuarantineRefusal,
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(root, "quota-index.json")));
        Assert.True(lease.ReadState()?.AuditMismatch);
        Assert.Null(lease.ReadState()?.AuditIntentExecutionId);
        Assert.Equal("commit-owner", lease.ReadState()?.InFlightExecutionId);
        var quota = QuotaEntry(root, key);
        Assert.True(quota.GetProperty("Quarantined").GetBoolean());
        Assert.Equal(0, quota.GetProperty("PayloadBytes").GetInt64());
        Assert.Equal(0, quota.GetProperty("ReservedPayloadBytes").GetInt64());
    }

    [Fact]
    public async Task RecordSuccessfulUseRejectsUnresolvedExecutionWithoutChangingAuthorityOrIndex()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "lru-unresolved", "store");
        var repository = Hash("lru-unresolved-repository").ToLowerInvariant();
        var key = Hash("lru-unresolved-key").ToLowerInvariant();
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        lease.EstablishAuthority("still-running-execution");
        var authorityBefore = lease.ReadState();
        var indexPath = TestPathUtils.PathUnder(root, "quota-index.json");
        var indexBefore = await File.ReadAllBytesAsync(indexPath);

        Assert.Throws<InvalidOperationException>(() => lease.RecordSuccessfulUse(DateTimeOffset.UtcNow));

        Assert.Equal(authorityBefore, lease.ReadState());
        Assert.Equal("still-running-execution", lease.ReadState()?.InFlightExecutionId);
        Assert.Equal(indexBefore, await File.ReadAllBytesAsync(indexPath));
        Assert.Equal(0, QuotaEntry(root, key).GetProperty("PayloadBytes").GetInt64());
        Assert.DoesNotContain(Directory.EnumerateDirectories(lease.DirectoryPath), path =>
            Path.GetFileName(path).StartsWith("payload", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecordSuccessfulUseRejectsSettledAuditIntentWithExistingPayloadWithoutChangingIndex()
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "lru-settled-audit-intent");
        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        const string executionId = "settled-audit-intent-owner";
        lease.EstablishAuthority(executionId, auditIntent: true, expectedAssertions: "assertion");
        lease.SettleExecution(executionId);
        var authorityBefore = lease.ReadState();
        var indexPath = TestPathUtils.PathUnder(fixture.StoreRoot, "quota-index.json");
        var indexBefore = await File.ReadAllBytesAsync(indexPath);

        Assert.Null(authorityBefore?.InFlightExecutionId);
        Assert.Equal(executionId, authorityBefore?.AuditIntentExecutionId);
        Assert.NotNull(authorityBefore?.PayloadDirectoryName);
        Assert.Throws<InvalidOperationException>(() => lease.RecordSuccessfulUse(DateTimeOffset.UtcNow));

        Assert.Equal(authorityBefore, lease.ReadState());
        Assert.Equal(indexBefore, await File.ReadAllBytesAsync(indexPath));
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
    }

    [Theory]
    [InlineData("origin.plan.json")]
    [InlineData("origin.manifest.json")]
    [InlineData("passed-producer.json")]
    [InlineData("artifacts/reports/report.txt")]
    public async Task PubliclyReadableUnixPayloadIsUnavailableWithoutDestroyingItsAuthority(string relativePath)
    {
        if (OperatingSystem.IsWindows()) return;
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "payload-permissions");
        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        var payload = TestPathUtils.PathUnder(lease.DirectoryPath, fixture.PayloadName);
        var path = TestPathUtils.PathUnder(payload, relativePath);
        var before = await File.ReadAllBytesAsync(path);
        var originalMode = File.GetUnixFileMode(path);
        var authority = lease.ReadState();
        var indexPath = TestPathUtils.PathUnder(fixture.StoreRoot, "quota-index.json");
        var index = await File.ReadAllBytesAsync(indexPath);
        var writer = new EvidenceArtifactWriter(fixture.Producer, fixture.RestoreRoot);
        try
        {
            File.SetUnixFileMode(path, originalMode | UnixFileMode.GroupRead);

            var result = await lease.ReadReceiptAsync(fixture.Producer, writer, CancellationToken.None);

            Assert.False(result.IsCandidate);
            Assert.True(result.IsUnavailable);
            Assert.Equal("receipt-payload-io-unavailable", result.Reason);
            Assert.Null(result.Result);
            Assert.Empty(writer.WrittenArtifacts);
            Assert.Equal(authority, lease.ReadState());
            Assert.Equal(index, await File.ReadAllBytesAsync(indexPath));
            Assert.Equal(before, await File.ReadAllBytesAsync(path));
            Assert.True(Directory.Exists(payload));
            Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
        }
        finally { File.SetUnixFileMode(path, originalMode); }
    }

    [Theory]
    [InlineData("extra-container-entry", "payload-container-invalid", false)]
    [InlineData("malformed-plan", "owned-payload-invalid", false)]
    [InlineData("passed-result", "passed-producer-record-invalid", false)]
    [InlineData("identity-digest", "input-identity-digest-invalid", false)]
    [InlineData("declared-producer", "passed-producer-invalid", false)]
    [InlineData("missing-declaration", "passed-producer-invalid", false)]
    [InlineData("missing-artifact", "owned-payload-invalid", false)]
    public async Task CorruptReceiptMatrixReturnsExactMissAndPreservesForeignData(
        string corruption, string expectedReason, bool leavesOwnedPayload)
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, corruption);
        var payloadPath = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, fixture.PayloadName);
        var sentinelPath = TestPathUtils.PathUnder(temporary.Path, "foreign-sentinel.txt");
        await File.WriteAllTextAsync(sentinelPath, "operator-owned sentinel");
        var artifactPath = TestPathUtils.PathUnder(payloadPath, "artifacts", "reports", "report.txt");

        switch (corruption)
        {
            case "extra-container-entry":
                using (var payloadLease = CoverageRunOutputLease.AcquireArtifactDirectory(payloadPath,
                           createMissing: false, requirePrivate: true)!)
                using (var extraEntry = payloadLease.OpenArtifactFile("foreign-entry", createNew: true))
                    extraEntry.Write(Encoding.UTF8.GetBytes("unexpected owned entry"));
                break;
            case "malformed-plan":
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(payloadPath, "origin.plan.json"), "{");
                break;
            case "passed-result":
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(payloadPath, "passed-producer.json"), "null");
                break;
            case "identity-digest":
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(payloadPath, "identity.json"), "{}");
                break;
            case "missing-declaration":
                break;
            case "missing-artifact":
                File.Delete(artifactPath);
                break;
            case "declared-producer":
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, "Unknown receipt corruption case.");
        }

        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        var producer = corruption switch
        {
            "declared-producer" => fixture.Producer with { Version = "2" },
            "missing-declaration" => fixture.Producer with { Id = "undeclared-producer" },
            _ => fixture.Producer
        };
        var writer = new EvidenceArtifactWriter(producer, fixture.RestoreRoot);
        var read = await lease.ReadReceiptAsync(producer, writer, CancellationToken.None);

        Assert.False(read.IsCandidate);
        Assert.False(read.IsUnavailable);
        Assert.Equal(expectedReason, read.Reason);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(File.Exists(TestPathUtils.PathUnder(fixture.RestoreRoot, "reports", "report.txt")));
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(sentinelPath));
        Assert.Null(lease.ReadState()?.PayloadDirectoryName);
        var quota = QuotaEntry(fixture.StoreRoot, fixture.Key);
        Assert.Null(quota.GetProperty("PayloadDirectoryName").GetString());
        Assert.Equal(0, quota.GetProperty("PayloadBytes").GetInt64());
        Assert.Equal(leavesOwnedPayload ? 1 : 0, quota.GetProperty("RetiredPayloads").GetArrayLength());
        Assert.Equal(leavesOwnedPayload, Directory.Exists(payloadPath));
    }

    [Theory]
    [InlineData("expired", "origin-expired")]
    [InlineData("future", "origin-time-in-future")]
    public async Task ReceiptAgeMissPreservesCanonicalPayloadAndAuthority(string age, string expectedReason)
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "age-" + age);
        var payloadPath = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, fixture.PayloadName);
        var completedAt = age == "expired" ? DateTimeOffset.UtcNow.AddDays(-8) : DateTimeOffset.UtcNow.AddMinutes(5);
        await UpdateFixtureAuthorityAsync(fixture, state => state["OriginCompletedAt"] = completedAt,
            entry => entry["OriginCompletedAt"] = completedAt);
        var expectedPayloadBytes = QuotaEntry(fixture.StoreRoot, fixture.Key).GetProperty("PayloadBytes").GetInt64();

        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        var readOnly = EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
        { RootDirectory = fixture.StoreRoot, RepositoryNamespace = fixture.Repository });
        var explanation = readOnly.Peek(fixture.Key);
        var writer = new EvidenceArtifactWriter(fixture.Producer, fixture.RestoreRoot);
        var result = await lease.ReadReceiptAsync(fixture.Producer, writer, CancellationToken.None);

        Assert.False(result.IsCandidate);
        Assert.False(result.IsUnavailable);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Equal(result, explanation);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.True(Directory.Exists(payloadPath));
        Assert.Equal(fixture.PayloadName, lease.ReadState()?.PayloadDirectoryName);
        Assert.Equal(expectedPayloadBytes, QuotaEntry(fixture.StoreRoot, fixture.Key).GetProperty("PayloadBytes").GetInt64());
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
    }

    [Fact]
    public async Task UnresolvedForeignReceiptStateIsUnavailableAndPreservesPayloadCustody()
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "foreign-receipt-state");
        var payloadPath = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, fixture.PayloadName);
        await UpdateFixtureAuthorityAsync(fixture, state => state["InFlightExecutionId"] = "foreign-execution",
            entry => entry["HasInFlight"] = true);
        var expectedPayloadBytes = QuotaEntry(fixture.StoreRoot, fixture.Key).GetProperty("PayloadBytes").GetInt64();

        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        var writer = new EvidenceArtifactWriter(fixture.Producer, fixture.RestoreRoot);
        var result = await lease.ReadReceiptAsync(fixture.Producer, writer, CancellationToken.None);

        Assert.False(result.IsCandidate);
        Assert.True(result.IsUnavailable);
        Assert.Equal("unresolved-execution", result.Reason);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.Equal("foreign-execution", lease.ReadState()?.InFlightExecutionId);
        Assert.Equal(fixture.PayloadName, lease.ReadState()?.PayloadDirectoryName);
        Assert.True(Directory.Exists(payloadPath));
        Assert.Equal(expectedPayloadBytes, QuotaEntry(fixture.StoreRoot, fixture.Key).GetProperty("PayloadBytes").GetInt64());
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
    }

    [Fact]
    public async Task SettledExecutionWithoutPassedPayloadReportsNoPassedReceipt()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var repository = Hash("no-passed-receipt-repository").ToLowerInvariant();
        var key = Hash("no-passed-receipt-key").ToLowerInvariant();
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        lease.EstablishAuthority("completed-without-payload");
        lease.SettleExecution("completed-without-payload");

        var proposal = lease.ProposeReadReceipt();

        Assert.False(proposal.IsCandidate);
        Assert.False(proposal.IsUnavailable);
        Assert.Equal("no-passed-receipt", proposal.Reason);
        Assert.Null(lease.ReadState()?.PayloadDirectoryName);
        Assert.Equal(0, QuotaEntry(root, key).GetProperty("PayloadBytes").GetInt64());
    }

    [Fact]
    public async Task UnsupportedPersistedReceiptStateIsUnavailableAndPreservedByteForByte()
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "unsupported-receipt-state");
        var payloadPath = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, fixture.PayloadName);
        var indexPath = TestPathUtils.PathUnder(fixture.StoreRoot, "quota-index.json");
        var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        byte[] invalidIndex = [];
        try
        {
            var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
            var entry = index["Entries"]!.AsArray().Single(item =>
                StringComparer.Ordinal.Equals(item!["Key"]!.GetValue<string>(), fixture.Key))!.AsObject();
            entry["AuthorityState"] = "unsupported foreign authority format";
            invalidIndex = Encoding.UTF8.GetBytes(index.ToJsonString());
            await File.WriteAllBytesAsync(indexPath, invalidIndex);

            var writer = new EvidenceArtifactWriter(fixture.Producer, fixture.RestoreRoot);
            var result = await lease.ReadReceiptAsync(fixture.Producer, writer, CancellationToken.None);
            Assert.False(result.IsCandidate);
            Assert.True(result.IsUnavailable);
            Assert.Equal("foreign-or-unsupported-state-preserved", result.Reason);
            Assert.Empty(writer.WrittenArtifacts);
            Assert.True(Directory.Exists(payloadPath));
            Assert.True(QuotaEntry(fixture.StoreRoot, fixture.Key).GetProperty("PayloadBytes").GetInt64() > 0);
            Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
        }
        finally
        {
            lease.Dispose();
        }

        Assert.Equal(invalidIndex, await File.ReadAllBytesAsync(indexPath));
        using var releasedKeyLock = CoverageArtifactOperationLease.Acquire(lease.DirectoryPath, exclusive: true);
    }

    [Fact]
    public async Task NoncanonicalIdentityWithMatchingOuterDigestIsInvalidatedAsOwnedMiss()
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "noncanonical-identity");
        var payloadPath = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, fixture.PayloadName);
        var identityPath = TestPathUtils.PathUnder(payloadPath, "identity.json");
        var identityBytes = await File.ReadAllBytesAsync(identityPath);
        var noncanonical = identityBytes.Concat(Encoding.UTF8.GetBytes(" ")).ToArray();
        await File.WriteAllBytesAsync(identityPath, noncanonical);
        var identityDigest = Convert.ToHexString(SHA256.HashData(noncanonical)).ToLowerInvariant();
        await UpdateFixtureAuthorityAsync(fixture, state => state["InputIdentityDigest"] = identityDigest,
            entry => entry["InputIdentityDigest"] = identityDigest);

        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        var writer = new EvidenceArtifactWriter(fixture.Producer, fixture.RestoreRoot);
        var result = await lease.ReadReceiptAsync(fixture.Producer, writer, CancellationToken.None);

        Assert.False(result.IsCandidate);
        Assert.False(result.IsUnavailable);
        Assert.Equal("input-identity-noncanonical", result.Reason);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(Directory.Exists(payloadPath));
        var quota = QuotaEntry(fixture.StoreRoot, fixture.Key);
        Assert.Null(quota.GetProperty("PayloadDirectoryName").GetString());
        Assert.Equal(0, quota.GetProperty("PayloadBytes").GetInt64());
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
    }

    [Theory]
    [InlineData("authority-digest")]
    [InlineData("plan-binding")]
    public async Task InvalidManifestAuthorityOrPlanBindingIsInvalidatedAsOwnedMiss(string corruption)
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "manifest-binding-" + corruption);
        var payloadPath = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, fixture.PayloadName);
        if (corruption == "authority-digest")
        {
            await UpdateFixtureAuthorityAsync(fixture,
                state => state["OriginManifestDigest"] = Hash("different-authority-manifest"));
        }
        else
        {
            var manifestPath = TestPathUtils.PathUnder(payloadPath, "origin.manifest.json");
            var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(await File.ReadAllBytesAsync(manifestPath));
            var unbound = manifest with { PlanDigest = Hash("different-origin-plan"), ManifestDigest = string.Empty };
            var invalidManifest = unbound with { ManifestDigest = EvidenceDigest.CanonicalSha256(unbound) };
            await File.WriteAllBytesAsync(manifestPath, EvidenceCanonicalJson.Serialize(invalidManifest));
            await UpdateFixtureAuthorityAsync(fixture,
                state => state["OriginManifestDigest"] = invalidManifest.ManifestDigest);
        }

        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        var writer = new EvidenceArtifactWriter(fixture.Producer, fixture.RestoreRoot);
        var result = await lease.ReadReceiptAsync(fixture.Producer, writer, CancellationToken.None);

        Assert.False(result.IsCandidate);
        Assert.False(result.IsUnavailable);
        Assert.Equal("origin-binding-invalid", result.Reason);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(Directory.Exists(payloadPath));
        Assert.Null(QuotaEntry(fixture.StoreRoot, fixture.Key).GetProperty("PayloadDirectoryName").GetString());
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
    }

    [Fact]
    public async Task CanonicallyBoundManifestWithInvalidProducerArtifactMetadataIsMissedAndCleaned()
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "invalid-artifact-metadata");
        var payloadPath = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, fixture.PayloadName);
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(payloadPath, "origin.plan.json")));
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(payloadPath, "origin.manifest.json")));
        var originalResult = EvidenceCanonicalJson.Deserialize<EvidenceProducerResult>(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(payloadPath, "passed-producer.json")));
        var malformedResult = originalResult with
        {
            Artifacts = originalResult.Artifacts!.Select(artifact => artifact with { RelativePath = "../escape.txt" }).ToArray()
        };
        var results = manifest.ProducerResults.Select(result => result.ProducerId == malformedResult.ProducerId
            ? malformedResult : result).ToArray();
        var malformedManifest = EvidenceManifestBuilder.Build(plan, results, manifest.ClaimKind == EvidenceClaimKind.ObservationOnly,
            manifest.EnvelopeStatus, manifest.ResourceResults, manifest.Metrics);
        var resultBytes = EvidenceCanonicalJson.Serialize(malformedResult);
        await File.WriteAllBytesAsync(TestPathUtils.PathUnder(payloadPath, "passed-producer.json"), resultBytes);
        await File.WriteAllBytesAsync(TestPathUtils.PathUnder(payloadPath, "origin.manifest.json"),
            EvidenceCanonicalJson.Serialize(malformedManifest));
        await UpdateFixtureAuthorityAsync(fixture, state =>
        {
            state["PassedProducerRecord"] = Convert.ToBase64String(resultBytes);
            state["OriginManifestDigest"] = malformedManifest.ManifestDigest;
        });

        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        var writer = new EvidenceArtifactWriter(fixture.Producer, fixture.RestoreRoot);
        var result = await lease.ReadReceiptAsync(fixture.Producer, writer, CancellationToken.None);

        Assert.False(result.IsCandidate);
        Assert.False(result.IsUnavailable);
        Assert.Equal("passed-producer-invalid", result.Reason);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(Directory.Exists(payloadPath));
        Assert.Equal(0, QuotaEntry(fixture.StoreRoot, fixture.Key).GetProperty("PayloadBytes").GetInt64());
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
    }

    [Fact]
    public async Task CanonicallyBoundFailedProducerResultCannotBeRestoredAsPassedReceipt()
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "forged-failed-producer-result");
        var payloadPath = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, fixture.PayloadName);
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(payloadPath, "origin.plan.json")));
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(payloadPath, "origin.manifest.json")));
        var originalResult = EvidenceCanonicalJson.Deserialize<EvidenceProducerResult>(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(payloadPath, "passed-producer.json")));
        var failedResult = originalResult with { Outcome = EvidenceProducerOutcome.Failed };
        var results = manifest.ProducerResults.Select(result => result.ProducerId == failedResult.ProducerId
            ? failedResult : result).ToArray();
        var failedManifest = EvidenceManifestBuilder.Build(plan, results, manifest.ClaimKind == EvidenceClaimKind.ObservationOnly,
            manifest.EnvelopeStatus, manifest.ResourceResults, manifest.Metrics);
        var resultBytes = EvidenceCanonicalJson.Serialize(failedResult);
        await File.WriteAllBytesAsync(TestPathUtils.PathUnder(payloadPath, "passed-producer.json"), resultBytes);
        await File.WriteAllBytesAsync(TestPathUtils.PathUnder(payloadPath, "origin.manifest.json"),
            EvidenceCanonicalJson.Serialize(failedManifest));
        await UpdateFixtureAuthorityAsync(fixture, state =>
        {
            state["PassedProducerRecord"] = Convert.ToBase64String(resultBytes);
            state["OriginManifestDigest"] = failedManifest.ManifestDigest;
        });

        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        var writer = new EvidenceArtifactWriter(fixture.Producer, fixture.RestoreRoot);
        var result = await lease.ReadReceiptAsync(fixture.Producer, writer, CancellationToken.None);

        Assert.False(result.IsCandidate);
        Assert.False(result.IsUnavailable);
        Assert.Equal("passed-producer-invalid", result.Reason);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(Directory.Exists(payloadPath));
        Assert.Equal(0, QuotaEntry(fixture.StoreRoot, fixture.Key).GetProperty("PayloadBytes").GetInt64());
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
    }

    [Theory]
    [InlineData("producer-id")]
    [InlineData("assertion-id")]
    public async Task CanonicalPassedRecordMustMatchTheProducerResultBoundByItsManifest(string binding)
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "passed-record-binding-" + binding);
        var payloadPath = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, fixture.PayloadName);
        var originResult = EvidenceCanonicalJson.Deserialize<EvidenceProducerResult>(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(payloadPath, "passed-producer.json")));
        var detachedResult = binding switch
        {
            "producer-id" => originResult with { ProducerId = "different-producer" },
            "assertion-id" => originResult with { SatisfiedAssertionIds = ["different-assertion"] },
            _ => throw new ArgumentOutOfRangeException(nameof(binding), binding, "Unknown producer-result binding case.")
        };
        await RewritePassedRecordWithOwnedAuthorityAsync(fixture, detachedResult);

        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        var writer = new EvidenceArtifactWriter(fixture.Producer, fixture.RestoreRoot);
        var result = await lease.ReadReceiptAsync(fixture.Producer, writer, CancellationToken.None);

        Assert.False(result.IsCandidate);
        Assert.False(result.IsUnavailable);
        Assert.Equal("passed-producer-invalid", result.Reason);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(Directory.Exists(payloadPath));
        Assert.Null(lease.ReadState()?.PayloadDirectoryName);
        Assert.Equal(0, QuotaEntry(fixture.StoreRoot, fixture.Key).GetProperty("PayloadBytes").GetInt64());
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
    }

    [Fact]
    public async Task ReceiptForDifferentProducerVersionIsInvalidatedAsOwnedMiss()
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "producer-version-binding");
        var payloadPath = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, fixture.PayloadName);
        var changedProducer = fixture.Producer with { Version = "2" };

        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        var writer = new EvidenceArtifactWriter(changedProducer, fixture.RestoreRoot);
        var result = await lease.ReadReceiptAsync(changedProducer, writer, CancellationToken.None);

        Assert.False(result.IsCandidate);
        Assert.False(result.IsUnavailable);
        Assert.Equal("passed-producer-invalid", result.Reason);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(Directory.Exists(payloadPath));
        Assert.Null(lease.ReadState()?.PayloadDirectoryName);
        Assert.Equal(0, QuotaEntry(fixture.StoreRoot, fixture.Key).GetProperty("PayloadBytes").GetInt64());
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptWithNoDeclaredArtifactsRestoresPassedResultWithEmptyArtifactList(bool createDirectories)
    {
        using var temporary = TestDirectory.Create();
        var storeRoot = TestPathUtils.PathUnder(temporary.Path, "empty-artifact-receipts");
        var sourceRoot = TestPathUtils.PathUnder(temporary.Path, "empty-artifact-source");
        var restoreRoot = TestPathUtils.PathUnder(temporary.Path, "empty-artifact-restore");
        if (createDirectories)
        {
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(restoreRoot);
        }
        var producer = new EvidenceProducerDeclaration("empty-artifact-producer", "unit", "1", [], ["assertion"], [], 30)
        {
            Inputs = new EvidenceProducerInputs(["**"], [])
        };
        var plan = PublicationPlan(producer);
        var manifest = EvidenceManifestBuilder.Build(plan,
            [new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Passed, ["assertion"])]);
        var repository = Hash("empty-artifact-repository").ToLowerInvariant();
        var key = Hash("empty-artifact-key").ToLowerInvariant();
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = storeRoot, RepositoryNamespace = repository });
        string payloadName;
        const string execution = "empty-artifact-execution";
        using (var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None)))
        {
            lease.EstablishAuthority(execution);
            var outcomes = await store.PublishInvocationAsync(execution, plan, manifest,
                [new EvidenceReceiptPublicationRequest(lease, producer.Id, sourceRoot, DateTimeOffset.UtcNow)],
                CancellationToken.None);
            Assert.True(outcomes[producer.Id].Published, outcomes[producer.Id].Reason);
            payloadName = outcomes[producer.Id].PayloadDirectoryName!;
        }

        using var readLease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        var restored = await readLease.ReadReceiptAsync(producer,
            new EvidenceArtifactWriter(producer, restoreRoot), CancellationToken.None);

        Assert.True(restored.IsCandidate);
        Assert.Equal(EvidenceProducerOutcome.Reused, restored.Result?.Outcome);
        Assert.Empty(restored.Result?.Artifacts ?? []);
        Assert.Equal(payloadName, readLease.ReadState()?.PayloadDirectoryName);
        var usedAt = Assert.IsType<DateTimeOffset>(readLease.ReadState()?.LastSuccessfulUse);
        Assert.Equal(usedAt, QuotaEntry(storeRoot, key).GetProperty("LastSuccessfulUse").GetDateTimeOffset());
        Assert.Equal(createDirectories, Directory.Exists(sourceRoot));
        Assert.Equal(createDirectories, Directory.Exists(restoreRoot));
        Assert.Null(readLease.ReadState()?.InputIdentityDigest);
        Assert.False(File.Exists(TestPathUtils.PathUnder(storeRoot, repository, key, payloadName, "identity.json")));
    }

    [Fact]
    public async Task RestoreWriterFailureKeepsVerifiedReceiptAndPayloadCharge()
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "restore-writer-failure");
        var payloadPath = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, fixture.PayloadName);
        var expectedPayloadBytes = QuotaEntry(fixture.StoreRoot, fixture.Key).GetProperty("PayloadBytes").GetInt64();
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(fixture.RestoreRoot, "reports"), "blocks artifact directory creation");

        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        var writer = new EvidenceArtifactWriter(fixture.Producer, fixture.RestoreRoot);
        var result = await lease.ReadReceiptAsync(fixture.Producer, writer, CancellationToken.None);

        Assert.False(result.IsCandidate);
        Assert.False(result.IsUnavailable);
        Assert.Equal("restore-or-lru-unavailable", result.Reason);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.Equal(fixture.PayloadName, lease.ReadState()?.PayloadDirectoryName);
        Assert.True(Directory.Exists(payloadPath));
        Assert.Equal(expectedPayloadBytes, QuotaEntry(fixture.StoreRoot, fixture.Key).GetProperty("PayloadBytes").GetInt64());
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
    }

    [Fact]
    public async Task SameLengthArtifactMutationFailsDigestVerificationAndCleansOwnedPayload()
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "artifact-digest-mutation");
        var payloadPath = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, fixture.PayloadName);
        var artifactPath = TestPathUtils.PathUnder(payloadPath, "artifacts", "reports", "report.txt");
        var bytes = await File.ReadAllBytesAsync(artifactPath);
        var originalLength = bytes.Length;
        var originalDigest = Convert.ToHexString(SHA256.HashData(bytes));
        bytes[0] ^= 1;
        await File.WriteAllBytesAsync(artifactPath, bytes);
        Assert.Equal(originalLength, new FileInfo(artifactPath).Length);
        Assert.NotEqual(originalDigest, Convert.ToHexString(SHA256.HashData(bytes)));

        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        var writer = new EvidenceArtifactWriter(fixture.Producer, fixture.RestoreRoot);
        var result = await lease.ReadReceiptAsync(fixture.Producer, writer, CancellationToken.None);

        Assert.False(result.IsCandidate);
        Assert.False(result.IsUnavailable);
        Assert.Equal("owned-payload-invalid", result.Reason);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(Directory.Exists(payloadPath));
        Assert.Null(lease.ReadState()?.PayloadDirectoryName);
        Assert.Equal(0, QuotaEntry(fixture.StoreRoot, fixture.Key).GetProperty("PayloadBytes").GetInt64());
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
    }

    [Theory]
    [InlineData("invalid-mode")]
    [InlineData("duplicate-path")]
    public async Task CanonicallyBoundInvalidIdentityIsRejectedAndOwnedPayloadIsCleaned(string corruption)
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "bound-identity-" + corruption);
        var payloadPath = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, fixture.PayloadName);
        var identityPath = TestPathUtils.PathUnder(payloadPath, "identity.json");
        var original = EvidenceCanonicalJson.Deserialize<EvidenceReceiptInputIdentity>(await File.ReadAllBytesAsync(identityPath));
        var files = corruption == "invalid-mode"
            ? new[] { new EvidenceReceiptInputFile("inputs/input.txt", Hash("identity-file"), 0x4000, 1) }
            : new[]
            {
                new EvidenceReceiptInputFile("inputs/input.txt", Hash("identity-file-a"), 0, 1),
                new EvidenceReceiptInputFile("inputs/input.txt", Hash("identity-file-b"), 0, 1)
            };
        var invalidIdentity = original with { Files = files };
        var identityBytes = EvidenceCanonicalJson.Serialize(invalidIdentity);
        await File.WriteAllBytesAsync(identityPath, identityBytes);
        var identityDigest = Convert.ToHexString(SHA256.HashData(identityBytes)).ToLowerInvariant();
        await UpdateFixtureAuthorityAsync(fixture, state => state["InputIdentityDigest"] = identityDigest,
            entry => entry["InputIdentityDigest"] = identityDigest);

        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        var writer = new EvidenceArtifactWriter(fixture.Producer, fixture.RestoreRoot);
        var result = await lease.ReadReceiptAsync(fixture.Producer, writer, CancellationToken.None);

        Assert.False(result.IsCandidate);
        Assert.False(result.IsUnavailable);
        Assert.Equal("owned-payload-invalid", result.Reason);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(Directory.Exists(payloadPath));
        Assert.Null(lease.ReadState()?.PayloadDirectoryName);
        Assert.Equal(0, QuotaEntry(fixture.StoreRoot, fixture.Key).GetProperty("PayloadBytes").GetInt64());
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
    }

    [Theory]
    [InlineData("source-inside-store", "source-root-overlaps-receipt-store")]
    [InlineData("store-inside-source", "source-root-overlaps-receipt-store")]
    [InlineData("cleanup-incomplete", "invocation-not-complete-and-passed")]
    [InlineData("audit-quarantine", "audit-quarantined")]
    public async Task PublicationPreflightRefusalPreservesAuthorityAndOwnedPayload(string refusal, string expectedReason)
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "publication-preflight-" + refusal);
        var payloadPath = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, fixture.PayloadName);
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(payloadPath, "origin.plan.json")));
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(payloadPath, "origin.manifest.json")));
        if (refusal == "audit-quarantine")
        {
            await UpdateFixtureAuthorityAsync(fixture, state =>
            {
                state["AuditIntentExecutionId"] = null;
                state["AuditMismatch"] = true;
            }, entry =>
            {
                entry["HasAuditIntent"] = false;
                entry["Quarantined"] = true;
                var authorityBytes = Encoding.UTF8.GetByteCount(entry["AuthorityState"]!.ToJsonString());
                entry["MetadataBytes"] = Math.Max(0,
                    entry["EssentialReservationBytes"]!.GetValue<long>() - authorityBytes);
                entry["EssentialReserved"] = true;
            });
        }
        var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        const string executionId = "wave-four-preflight-owner";
        lease.EstablishAuthority(executionId, allowQuarantinedFresh: refusal == "audit-quarantine");
        var indexPath = TestPathUtils.PathUnder(fixture.StoreRoot, "quota-index.json");
        var indexBefore = await File.ReadAllBytesAsync(indexPath);
        var sourceRoot = refusal switch
        {
            "source-inside-store" => TestPathUtils.PathUnder(fixture.StoreRoot, "nested-source"),
            "store-inside-source" => Directory.GetParent(fixture.StoreRoot)!.FullName,
            _ => fixture.SourceRoot
        };
        if (refusal == "cleanup-incomplete")
            manifest = EvidenceManifestBuilder.Build(plan, manifest.ProducerResults,
                manifest.ClaimKind == EvidenceClaimKind.ObservationOnly, manifest.EnvelopeStatus,
                manifest.ResourceResults, manifest.Metrics with { CleanupCompleted = false });

        try
        {
            var outcomes = await fixture.Store.PublishInvocationAsync(executionId, plan, manifest,
                [new EvidenceReceiptPublicationRequest(lease, fixture.Producer.Id, sourceRoot, DateTimeOffset.UtcNow)],
                CancellationToken.None);

            Assert.False(outcomes[fixture.Producer.Id].Published);
            Assert.Equal(expectedReason, outcomes[fixture.Producer.Id].Reason);
            Assert.Equal(indexBefore, await File.ReadAllBytesAsync(indexPath));
            Assert.Equal(executionId, lease.ReadState()?.InFlightExecutionId);
            Assert.Equal(fixture.PayloadName, lease.ReadState()?.PayloadDirectoryName);
            Assert.True(Directory.Exists(payloadPath));
            Assert.True(QuotaEntry(fixture.StoreRoot, fixture.Key).GetProperty("PayloadBytes").GetInt64() > 0);
            Assert.DoesNotContain(Directory.EnumerateDirectories(lease.DirectoryPath), path =>
                Path.GetFileName(path).StartsWith("payload-stage-", StringComparison.Ordinal));
            Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
        }
        finally
        {
            lease.Dispose();
        }
    }

    [Fact]
    public async Task PostCommitRetiredPayloadIndexIoRefusalKeepsCommittedReceiptAndRetiredCharge()
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "retired-payload-index-io");
        var oldPayloadPath = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, fixture.PayloadName);
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(oldPayloadPath, "origin.plan.json")));
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(oldPayloadPath, "origin.manifest.json")));
        var oldPayloadBytes = QuotaEntry(fixture.StoreRoot, fixture.Key).GetProperty("PayloadBytes").GetInt64();
        const string executionId = "retired-payload-index-io-owner";
        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        lease.EstablishAuthority(executionId);
        var indexPath = TestPathUtils.PathUnder(fixture.StoreRoot, "quota-index.json");
        byte[]? committedIndexBytes = null;
        IReadOnlyDictionary<string, EvidenceReceiptPublicationResult> outcomes;

        try
        {
            outcomes = await EvidenceReceiptStore.EvidenceReceiptKeyLease.PublishInvocationCoreAsync(
                fixture.StoreRoot, fixture.Repository, executionId, plan, manifest,
                [new EvidenceReceiptPublicationRequest(lease, fixture.Producer.Id, fixture.SourceRoot, DateTimeOffset.UtcNow)],
                CancellationToken.None,
                afterAuthorityCommitForTests: () =>
                {
                    committedIndexBytes = File.ReadAllBytes(indexPath);
                    File.Delete(indexPath);
                    Directory.CreateDirectory(indexPath);
                });
        }
        finally
        {
            if (committedIndexBytes is not null)
            {
                if (Directory.Exists(indexPath)) Directory.Delete(indexPath);
                using var root = CoverageRunOutputLease.AcquireArtifactDirectory(fixture.StoreRoot, false, true)!;
                await using var restored = root.OpenArtifactFile("quota-index.json", createNew: true);
                await restored.WriteAsync(committedIndexBytes);
                restored.Flush(flushToDisk: true);
            }
        }

        var outcome = outcomes[fixture.Producer.Id];
        Assert.True(outcome.Published, outcome.Reason);
        Assert.NotEqual(fixture.PayloadName, outcome.PayloadDirectoryName);
        Assert.Equal(outcome.PayloadDirectoryName, lease.ReadState()?.PayloadDirectoryName);
        Assert.True(Directory.Exists(oldPayloadPath));
        Assert.True(Directory.Exists(TestPathUtils.PathUnder(lease.DirectoryPath, outcome.PayloadDirectoryName!)));
        Assert.True(File.Exists(indexPath));
        var quota = QuotaEntry(fixture.StoreRoot, fixture.Key);
        Assert.Equal(outcome.PayloadDirectoryName, quota.GetProperty("PayloadDirectoryName").GetString());
        Assert.Equal(0, quota.GetProperty("ReservedPayloadBytes").GetInt64());
        var retired = Assert.Single(quota.GetProperty("RetiredPayloads").EnumerateArray());
        Assert.Equal(fixture.PayloadName, retired.GetProperty("DirectoryName").GetString());
        Assert.Equal(oldPayloadBytes, retired.GetProperty("Bytes").GetInt64());
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
    }

    [Theory]
    [InlineData("identity-key")]
    [InlineData("identity-namespace")]
    [InlineData("future-completion")]
    [InlineData("changed-source")]
    public async Task PublicationRejectsInvalidRequestIdentityTimeOrChangedSourceWithoutLeakingReservation(string refusal)
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "publication-reject-" + refusal);
        var oldPayload = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, fixture.PayloadName);
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(oldPayload, "origin.plan.json")));
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(oldPayload, "origin.manifest.json")));
        const string executionId = "publication-request-owner";
        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        lease.EstablishAuthority(executionId);
        var authorityBefore = lease.ReadState();
        var quotaBefore = QuotaEntry(fixture.StoreRoot, fixture.Key);
        var indexPath = TestPathUtils.PathUnder(fixture.StoreRoot, "quota-index.json");
        var indexBefore = await File.ReadAllBytesAsync(indexPath);
        var sourceRoot = fixture.SourceRoot;
        var completedAt = DateTimeOffset.UtcNow;
        EvidenceProducerIdentity? identity = null;
        if (refusal == "identity-key")
            identity = FixtureIdentity(fixture) with { Key = Hash("different-receipt-key").ToLowerInvariant() };
        else if (refusal == "identity-namespace")
            identity = FixtureIdentity(fixture, Hash("different-receipt-namespace").ToLowerInvariant());
        else if (refusal == "future-completion")
            completedAt = DateTimeOffset.UtcNow.AddHours(1);
        else if (refusal == "changed-source")
        {
            var declaredArtifact = Assert.Single(manifest.ProducerResults
                .Single(item => item.ProducerId == fixture.Producer.Id).Artifacts!);
            var changedBytes = "tampered origin bytes"u8.ToArray();
            Assert.Equal(declaredArtifact.LengthBytes, changedBytes.LongLength);
            Assert.NotEqual(declaredArtifact.Sha256,
                Convert.ToHexString(SHA256.HashData(changedBytes)).ToLowerInvariant());
            await File.WriteAllBytesAsync(TestPathUtils.PathUnder(sourceRoot, "reports", "report.txt"), changedBytes);
        }
        else
            throw new ArgumentOutOfRangeException(nameof(refusal), refusal, "Unknown publication refusal case.");

        var result = await fixture.Store.PublishInvocationAsync(executionId, plan, manifest,
            [new EvidenceReceiptPublicationRequest(lease, fixture.Producer.Id, sourceRoot, completedAt)
            { Identity = identity }], CancellationToken.None);

        Assert.False(result[fixture.Producer.Id].Published);
        Assert.Equal("payload-publication-failed", result[fixture.Producer.Id].Reason);
        Assert.Equal(authorityBefore, lease.ReadState());
        Assert.Equal(executionId, lease.ReadState()?.InFlightExecutionId);
        Assert.Equal(fixture.PayloadName, lease.ReadState()?.PayloadDirectoryName);
        Assert.Equal(indexBefore, await File.ReadAllBytesAsync(indexPath));
        var quotaAfter = QuotaEntry(fixture.StoreRoot, fixture.Key);
        Assert.Equal(quotaBefore.GetProperty("PayloadBytes").GetInt64(), quotaAfter.GetProperty("PayloadBytes").GetInt64());
        Assert.Equal(quotaBefore.GetProperty("ReservedPayloadBytes").GetInt64(), quotaAfter.GetProperty("ReservedPayloadBytes").GetInt64());
        Assert.Equal(quotaBefore.GetProperty("PayloadReservations").ValueKind, quotaAfter.GetProperty("PayloadReservations").ValueKind);
        Assert.Equal(quotaBefore.GetProperty("PayloadReservations").GetRawText(), quotaAfter.GetProperty("PayloadReservations").GetRawText());
        var payloadDirectories = Directory.EnumerateDirectories(lease.DirectoryPath)
            .Select(Path.GetFileName).Where(name => name is not null && name.StartsWith("payload-", StringComparison.Ordinal))
            .Select(name => name!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { fixture.PayloadName }, payloadDirectories);
        Assert.DoesNotContain(Directory.EnumerateDirectories(lease.DirectoryPath), path =>
            Path.GetFileName(path).StartsWith("payload-stage-", StringComparison.Ordinal));
        Assert.True(Directory.Exists(oldPayload));
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
    }

    [Theory]
    [InlineData("undeclared-file")]
    [InlineData("undeclared-directory")]
    public async Task ReadReceiptInvalidatesOwnedArtifactTreeEntriesAndReleasesOnlyOwnedPayload(string corruption)
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "artifact-tree-" + corruption);
        var payloadPath = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, fixture.PayloadName);
        var artifactsPath = TestPathUtils.PathUnder(payloadPath, "artifacts");
        using (var artifacts = CoverageRunOutputLease.AcquireArtifactDirectory(artifactsPath,
                   createMissing: false, requirePrivate: true)!)
        {
            if (corruption == "undeclared-file")
            {
                using var unexpected = artifacts.OpenArtifactFile("undeclared.txt", createNew: true);
                unexpected.Write("owned unexpected bytes"u8);
            }
            else if (corruption == "undeclared-directory")
            {
                using var unexpected = artifacts.CreateArtifactDirectory("undeclared");
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, "Unknown artifact tree corruption case.");
            }
        }

        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        var writer = new EvidenceArtifactWriter(fixture.Producer, fixture.RestoreRoot);
        var result = await lease.ReadReceiptAsync(fixture.Producer, writer, CancellationToken.None);

        Assert.False(result.IsCandidate);
        Assert.False(result.IsUnavailable);
        Assert.Equal("owned-payload-invalid", result.Reason);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(Directory.Exists(payloadPath));
        Assert.Null(lease.ReadState()?.PayloadDirectoryName);
        var quota = QuotaEntry(fixture.StoreRoot, fixture.Key);
        Assert.Equal(0, quota.GetProperty("PayloadBytes").GetInt64());
        Assert.Equal(0, quota.GetProperty("ReservedPayloadBytes").GetInt64());
        Assert.True(NoPayloadReservations(quota.GetProperty("PayloadReservations")));
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
    }

    [Fact]
    public async Task NonAuditPublicationBatchCommitsGoodProducerAndKeepsFailedProducerAuthority()
    {
        using var temporary = TestDirectory.Create();
        var storeRoot = TestPathUtils.PathUnder(temporary.Path, "mixed-batch", "store");
        var sourceA = TestPathUtils.PathUnder(temporary.Path, "mixed-batch", "source-a");
        var sourceB = TestPathUtils.PathUnder(temporary.Path, "mixed-batch", "source-b");
        var restoreA = TestPathUtils.PathUnder(temporary.Path, "mixed-batch", "restore-a");
        var sentinel = TestPathUtils.PathUnder(temporary.Path, "mixed-batch", "operator-sentinel.txt");
        Directory.CreateDirectory(sourceA);
        Directory.CreateDirectory(sourceB);
        Directory.CreateDirectory(restoreA);
        await File.WriteAllTextAsync(sentinel, "operator-owned sentinel");
        var producerA = PublicationProducer("mixed-batch-a");
        var producerB = PublicationProducer("mixed-batch-b");
        var plan = PublicationPlan(producerA, producerB);
        var writerA = new EvidenceArtifactWriter(producerA, sourceA);
        var writerB = new EvidenceArtifactWriter(producerB, sourceB);
        var bytesA = "producer A passed"u8.ToArray();
        var bytesB = "producer B was removed"u8.ToArray();
        await writerA.WriteAsync("report", "reports/report.txt", bytesA);
        await writerB.WriteAsync("report", "reports/report.txt", bytesB);
        var manifest = EvidenceManifestBuilder.Build(plan,
        [
            new EvidenceProducerResult(producerA.Id, EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: writerA.WrittenArtifacts),
            new EvidenceProducerResult(producerB.Id, EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: writerB.WrittenArtifacts)
        ]);
        File.Delete(TestPathUtils.PathUnder(sourceB, "reports", "report.txt"));
        Assert.False(File.Exists(TestPathUtils.PathUnder(sourceB, "reports", "report.txt")));
        var repository = Hash("mixed-batch-repository").ToLowerInvariant();
        var keyA = Hash("mixed-batch-key-a").ToLowerInvariant();
        var keyB = Hash("mixed-batch-key-b").ToLowerInvariant();
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = storeRoot, RepositoryNamespace = repository });
        var leases = await store.AcquireAsync([keyA, keyB], CancellationToken.None);
        try
        {
            var leaseA = leases.Single(item => item.Key == keyA);
            var leaseB = leases.Single(item => item.Key == keyB);
            const string executionId = "mixed-batch-execution";
            leaseA.EstablishAuthority(executionId);
            leaseB.EstablishAuthority(executionId);
            Assert.Null(leaseA.ReadState()?.AuditIntentExecutionId);
            Assert.Null(leaseB.ReadState()?.AuditIntentExecutionId);

            var outcomes = await store.PublishInvocationAsync(executionId, plan, manifest,
            [
                new EvidenceReceiptPublicationRequest(leaseA, producerA.Id, sourceA, DateTimeOffset.UtcNow),
                new EvidenceReceiptPublicationRequest(leaseB, producerB.Id, sourceB, DateTimeOffset.UtcNow)
            ], CancellationToken.None);

            Assert.True(outcomes[producerA.Id].Published, outcomes[producerA.Id].Reason);
            Assert.False(outcomes[producerB.Id].Published);
            Assert.Equal("payload-publication-failed", outcomes[producerB.Id].Reason);
            Assert.Null(leaseA.ReadState()?.InFlightExecutionId);
            Assert.Equal(outcomes[producerA.Id].PayloadDirectoryName, leaseA.ReadState()?.PayloadDirectoryName);
            Assert.Equal(executionId, leaseB.ReadState()?.InFlightExecutionId);
            Assert.Null(leaseB.ReadState()?.PayloadDirectoryName);
            var quotaA = QuotaEntry(storeRoot, keyA);
            var quotaB = QuotaEntry(storeRoot, keyB);
            Assert.True(quotaA.GetProperty("PayloadBytes").GetInt64() > 0);
            Assert.Equal(0, quotaA.GetProperty("ReservedPayloadBytes").GetInt64());
            Assert.Equal(0, quotaB.GetProperty("PayloadBytes").GetInt64());
            Assert.Equal(0, quotaB.GetProperty("ReservedPayloadBytes").GetInt64());
            Assert.True(NoPayloadReservations(quotaB.GetProperty("PayloadReservations")));
            Assert.DoesNotContain(Directory.EnumerateDirectories(leaseB.DirectoryPath), path =>
                Path.GetFileName(path).StartsWith("payload", StringComparison.Ordinal));

            var restoreWriter = new EvidenceArtifactWriter(producerA, restoreA);
            var restored = await leaseA.ReadReceiptAsync(producerA, restoreWriter, CancellationToken.None);
            Assert.True(restored.IsCandidate, restored.Reason);
            Assert.Equal(EvidenceProducerOutcome.Reused, restored.Result?.Outcome);
            Assert.Equal(bytesA, await File.ReadAllBytesAsync(TestPathUtils.PathUnder(restoreA, "reports", "report.txt")));
            Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(sentinel));
        }
        finally
        {
            foreach (var lease in leases.Reverse()) lease.Dispose();
        }
    }

    [Fact]
    public async Task CancelledPublicationBatchPreservesExistingReceiptAndExecutionAuthority()
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "cancelled-publication-batch");
        var oldPayload = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, fixture.PayloadName);
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(oldPayload, "origin.plan.json")));
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(oldPayload, "origin.manifest.json")));
        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        const string executionId = "cancelled-batch-owner";
        lease.EstablishAuthority(executionId);
        var authorityBefore = lease.ReadState();
        var indexPath = TestPathUtils.PathUnder(fixture.StoreRoot, "quota-index.json");
        var indexBefore = await File.ReadAllBytesAsync(indexPath);
        var quotaBefore = QuotaEntry(fixture.StoreRoot, fixture.Key);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.PublishInvocationAsync(executionId,
            plan, manifest,
            [new EvidenceReceiptPublicationRequest(lease, fixture.Producer.Id, fixture.SourceRoot, DateTimeOffset.UtcNow)],
            cancellation.Token));

        Assert.Equal(authorityBefore, lease.ReadState());
        Assert.Equal(executionId, lease.ReadState()?.InFlightExecutionId);
        Assert.Equal(fixture.PayloadName, lease.ReadState()?.PayloadDirectoryName);
        Assert.Equal(indexBefore, await File.ReadAllBytesAsync(indexPath));
        Assert.Equal(quotaBefore.GetProperty("PayloadBytes").GetInt64(),
            QuotaEntry(fixture.StoreRoot, fixture.Key).GetProperty("PayloadBytes").GetInt64());
        Assert.DoesNotContain(Directory.EnumerateDirectories(lease.DirectoryPath), path =>
            Path.GetFileName(path).StartsWith("payload-stage-", StringComparison.Ordinal));
        Assert.True(Directory.Exists(oldPayload));
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
    }

    [Theory]
    [InlineData(false, "same-length-content")]
    [InlineData(true, "same-length-content")]
    [InlineData(false, "source-length-change")]
    [InlineData(true, "source-length-change")]
    [InlineData(false, "source-root-move")]
    [InlineData(true, "source-root-move")]
    [InlineData(false, "missing-file-move")]
    [InlineData(true, "missing-file-move")]
    public async Task PostPreflightSourceMutationRollsBackAuditBatchAndKeepsNonAuditSuccess(
        bool auditBatch, string mutation)
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "post-preflight-mutation", "store");
        var sourceA = TestPathUtils.PathUnder(temporary.Path, "post-preflight-mutation", "source-a");
        var sourceB = TestPathUtils.PathUnder(temporary.Path, "post-preflight-mutation", "source-b");
        var sentinel = TestPathUtils.PathUnder(temporary.Path, "post-preflight-mutation", "sentinel.txt");
        Directory.CreateDirectory(sourceA);
        Directory.CreateDirectory(sourceB);
        await File.WriteAllTextAsync(sentinel, "operator-owned sentinel");
        var producerA = PublicationProducer("post-preflight-a");
        var producerB = PublicationProducer("post-preflight-b");
        var plan = PublicationPlan(producerA, producerB);
        var writerA = new EvidenceArtifactWriter(producerA, sourceA);
        var writerB = new EvidenceArtifactWriter(producerB, sourceB);
        var bytesA = "first producer source"u8.ToArray();
        var bytesB = "second producer source"u8.ToArray();
        await writerA.WriteAsync("report", "reports/a.txt", bytesA);
        await writerB.WriteAsync("report", "reports/b.txt", bytesB);
        var manifest = EvidenceManifestBuilder.Build(plan,
        [
            new EvidenceProducerResult(producerA.Id, EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: writerA.WrittenArtifacts),
            new EvidenceProducerResult(producerB.Id, EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: writerB.WrittenArtifacts)
        ]);
        var repository = Hash("post-preflight-mutation-repository").ToLowerInvariant();
        var keyA = Hash("post-preflight-mutation-key-a").ToLowerInvariant();
        var keyB = Hash("post-preflight-mutation-key-b").ToLowerInvariant();
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        var leases = await store.AcquireAsync([keyA, keyB], CancellationToken.None);
        try
        {
            var leaseA = leases.Single(lease => lease.Key == keyA);
            var leaseB = leases.Single(lease => lease.Key == keyB);
            const string execution = "post-preflight-mutation-execution";
            leaseA.EstablishAuthority(execution, auditBatch, expectedAssertions: auditBatch ? "assertion" : null);
            leaseB.EstablishAuthority(execution, auditBatch, expectedAssertions: auditBatch ? "assertion" : null);
            var authorityA = leaseA.ReadState();
            var authorityB = leaseB.ReadState();
            var corruptedSource = TestPathUtils.PathUnder(sourceB, "reports", "b.txt");
            var movedSourceRoot = TestPathUtils.PathUnder(temporary.Path, "post-preflight-mutation", "source-b-backup");
            var movedSourceFile = TestPathUtils.PathUnder(temporary.Path, "post-preflight-mutation", "b.txt-backup");
            var changedBytes = mutation == "same-length-content"
                ? "mutate producer source"u8.ToArray()
                : "x"u8.ToArray();
            if (mutation == "same-length-content") Assert.Equal(bytesB.Length, changedBytes.Length);
            var copied = new List<string>();
            IReadOnlyDictionary<string, EvidenceReceiptPublicationResult> outcomes;

            try
            {
                outcomes = await EvidenceReceiptStore.EvidenceReceiptKeyLease.PublishInvocationCoreAsync(root,
                    repository, execution, plan, manifest,
                    [
                        new EvidenceReceiptPublicationRequest(leaseA, producerA.Id, sourceA, DateTimeOffset.UtcNow),
                        new EvidenceReceiptPublicationRequest(leaseB, producerB.Id, sourceB, DateTimeOffset.UtcNow)
                    ], CancellationToken.None,
                    beforeArtifactCopyForTests: (request, artifact) =>
                    {
                        copied.Add(request.ProducerId);
                        if (request.ProducerId != producerB.Id) return;
                        Assert.Equal("reports/b.txt", artifact.RelativePath);
                        switch (mutation)
                        {
                            case "same-length-content":
                            case "source-length-change":
                                File.WriteAllBytes(corruptedSource, changedBytes);
                                break;
                            case "source-root-move":
                                Directory.Move(sourceB, movedSourceRoot);
                                break;
                            case "missing-file-move":
                                File.Move(corruptedSource, movedSourceFile);
                                break;
                        }
                    });
            }
            finally
            {
                if (Directory.Exists(movedSourceRoot)) Directory.Move(movedSourceRoot, sourceB);
                if (File.Exists(movedSourceFile)) File.Move(movedSourceFile, corruptedSource);
            }

            Assert.Equal(new[] { producerA.Id, producerB.Id }, copied);
            if (mutation is "same-length-content" or "source-length-change")
                Assert.NotEqual(bytesB, await File.ReadAllBytesAsync(corruptedSource));
            else
                Assert.Equal(bytesB, await File.ReadAllBytesAsync(corruptedSource));
            Assert.True(Directory.Exists(sourceB));
            Assert.False(Directory.Exists(movedSourceRoot));
            Assert.False(File.Exists(movedSourceFile));
            Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(sentinel));
            if (auditBatch)
            {
                Assert.Equal("audit-batch-aborted:payload-publication-failed", outcomes[producerA.Id].Reason);
                Assert.Equal(outcomes[producerA.Id].Reason, outcomes[producerB.Id].Reason);
                Assert.All(outcomes.Values, outcome => Assert.False(outcome.Published));
                Assert.Equal(authorityA, leaseA.ReadState());
                Assert.Equal(authorityB, leaseB.ReadState());
                Assert.Equal(execution, leaseA.ReadState()?.AuditIntentExecutionId);
                Assert.Equal(execution, leaseB.ReadState()?.AuditIntentExecutionId);
                Assert.DoesNotContain(Directory.EnumerateDirectories(leaseA.DirectoryPath), path =>
                    Path.GetFileName(path).StartsWith("payload", StringComparison.Ordinal));
                Assert.DoesNotContain(Directory.EnumerateDirectories(leaseB.DirectoryPath), path =>
                    Path.GetFileName(path).StartsWith("payload", StringComparison.Ordinal));
                Assert.True(NoPayloadReservations(QuotaEntry(root, keyA).GetProperty("PayloadReservations")));
                Assert.True(NoPayloadReservations(QuotaEntry(root, keyB).GetProperty("PayloadReservations")));
            }
            else
            {
                Assert.True(outcomes[producerA.Id].Published, outcomes[producerA.Id].Reason);
                Assert.False(outcomes[producerB.Id].Published);
                Assert.Equal("payload-publication-failed", outcomes[producerB.Id].Reason);
                Assert.Null(leaseA.ReadState()?.InFlightExecutionId);
                Assert.Equal(outcomes[producerA.Id].PayloadDirectoryName, leaseA.ReadState()?.PayloadDirectoryName);
                Assert.Equal(authorityB, leaseB.ReadState());
                Assert.Equal(execution, leaseB.ReadState()?.InFlightExecutionId);
                Assert.Null(leaseB.ReadState()?.PayloadDirectoryName);
                Assert.True(NoPayloadReservations(QuotaEntry(root, keyB).GetProperty("PayloadReservations")));
                Assert.DoesNotContain(Directory.EnumerateDirectories(leaseB.DirectoryPath), path =>
                    Path.GetFileName(path).StartsWith("payload", StringComparison.Ordinal));
                var restoreRoot = TestPathUtils.PathUnder(temporary.Path, "post-preflight-mutation", "restored");
                var restoreWriter = new EvidenceArtifactWriter(producerA, restoreRoot);
                var restored = await leaseA.ReadReceiptAsync(producerA, restoreWriter, CancellationToken.None);
                Assert.True(restored.IsCandidate, restored.Reason);
                Assert.Equal(bytesA, await File.ReadAllBytesAsync(TestPathUtils.PathUnder(restoreRoot, "reports", "a.txt")));
            }

            foreach (var lease in leases)
            {
                var quota = QuotaEntry(root, lease.Key);
                Assert.Equal(0, quota.GetProperty("ReservedPayloadBytes").GetInt64());
                Assert.True(NoPayloadReservations(quota.GetProperty("PayloadReservations")));
            }
            Assert.Equal(auditBatch ? 0 : 1,
                Directory.EnumerateDirectories(leaseA.DirectoryPath)
                    .Count(path => Path.GetFileName(path).StartsWith("payload-", StringComparison.Ordinal)));
        }
        finally
        {
            foreach (var lease in leases.Reverse()) lease.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostCommitFailureRetainsEveryCommittedBatchPayloadAndCharge(bool auditBatch)
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "post-commit-batch", "store");
        var sourceA = TestPathUtils.PathUnder(temporary.Path, "post-commit-batch", "source-a");
        var sourceB = TestPathUtils.PathUnder(temporary.Path, "post-commit-batch", "source-b");
        var restoreA = TestPathUtils.PathUnder(temporary.Path, "post-commit-batch", "restore-a");
        var restoreB = TestPathUtils.PathUnder(temporary.Path, "post-commit-batch", "restore-b");
        var sentinel = TestPathUtils.PathUnder(temporary.Path, "post-commit-batch", "sentinel.txt");
        Directory.CreateDirectory(sourceA);
        Directory.CreateDirectory(sourceB);
        Directory.CreateDirectory(restoreA);
        Directory.CreateDirectory(restoreB);
        await File.WriteAllTextAsync(sentinel, "operator-owned sentinel");
        var producerA = PublicationProducer("post-commit-batch-a");
        var producerB = PublicationProducer("post-commit-batch-b");
        var plan = PublicationPlan(producerA, producerB);
        var bytesA = "first committed payload"u8.ToArray();
        var bytesB = "second committed payload"u8.ToArray();
        var sourceWriterA = new EvidenceArtifactWriter(producerA, sourceA);
        var sourceWriterB = new EvidenceArtifactWriter(producerB, sourceB);
        await sourceWriterA.WriteAsync("report", "reports/a.txt", bytesA);
        await sourceWriterB.WriteAsync("report", "reports/b.txt", bytesB);
        var manifest = EvidenceManifestBuilder.Build(plan,
        [
            new EvidenceProducerResult(producerA.Id, EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: sourceWriterA.WrittenArtifacts),
            new EvidenceProducerResult(producerB.Id, EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: sourceWriterB.WrittenArtifacts)
        ]);
        var repository = Hash("post-commit-batch-repository").ToLowerInvariant();
        var keyA = Hash("post-commit-batch-key-a").ToLowerInvariant();
        var keyB = Hash("post-commit-batch-key-b").ToLowerInvariant();
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        var leases = await store.AcquireAsync([keyA, keyB], CancellationToken.None);
        try
        {
            var leaseA = leases.Single(lease => lease.Key == keyA);
            var leaseB = leases.Single(lease => lease.Key == keyB);
            const string execution = "post-commit-batch-execution";
            leaseA.EstablishAuthority(execution, auditBatch, expectedAssertions: auditBatch ? "assertion" : null);
            leaseB.EstablishAuthority(execution, auditBatch, expectedAssertions: auditBatch ? "assertion" : null);

            var outcomes = await EvidenceReceiptStore.EvidenceReceiptKeyLease.PublishInvocationCoreAsync(root,
                repository, execution, plan, manifest,
                [
                    new EvidenceReceiptPublicationRequest(leaseA, producerA.Id, sourceA, DateTimeOffset.UtcNow),
                    new EvidenceReceiptPublicationRequest(leaseB, producerB.Id, sourceB, DateTimeOffset.UtcNow)
                ], CancellationToken.None,
                afterAuthorityCommitForTests: () => throw new IOException("Injected failure after batch authority commit."));

            Assert.Equal(new[] { producerA.Id, producerB.Id }, outcomes.Keys.Order(StringComparer.Ordinal));
            Assert.All(outcomes.Values, outcome =>
            {
                Assert.False(outcome.Published);
                Assert.Equal("invocation-batch-commit-failed", outcome.Reason);
            });
            Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(sentinel));

            foreach (var (lease, producer, restoreRoot, expectedBytes, relativePath) in new[]
                     {
                         (leaseA, producerA, restoreA, bytesA, "reports/a.txt"),
                         (leaseB, producerB, restoreB, bytesB, "reports/b.txt")
                     })
            {
                var state = lease.ReadState();
                Assert.Null(state?.InFlightExecutionId);
                Assert.Null(state?.AuditIntentExecutionId);
                Assert.False(string.IsNullOrWhiteSpace(state?.PayloadDirectoryName));
                var payloadPath = TestPathUtils.PathUnder(lease.DirectoryPath, state!.PayloadDirectoryName!);
                Assert.True(Directory.Exists(payloadPath));
                Assert.DoesNotContain(Directory.EnumerateDirectories(lease.DirectoryPath), path =>
                    Path.GetFileName(path).StartsWith("payload-stage-", StringComparison.Ordinal));

                var quota = QuotaEntry(root, lease.Key);
                Assert.Equal(state.PayloadDirectoryName, quota.GetProperty("PayloadDirectoryName").GetString());
                Assert.Equal(0, quota.GetProperty("ReservedPayloadBytes").GetInt64());
                Assert.True(NoPayloadReservations(quota.GetProperty("PayloadReservations")));
                using (var payload = CoverageRunOutputLease.AcquireArtifactDirectory(payloadPath,
                           createMissing: false, requirePrivate: true)!)
                    Assert.Equal(EvidenceReceiptQuota.MeasurePayload(payload), quota.GetProperty("PayloadBytes").GetInt64());

                var restored = await lease.ReadReceiptAsync(producer,
                    new EvidenceArtifactWriter(producer, restoreRoot), CancellationToken.None);
                Assert.True(restored.IsCandidate, restored.Reason);
                Assert.Equal(EvidenceProducerOutcome.Reused, restored.Result?.Outcome);
                Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(TestPathUtils.PathUnder(restoreRoot,
                    relativePath.Split('/'))));
            }
        }
        finally
        {
            foreach (var lease in leases.Reverse()) lease.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MidBatchCancellationCleansStagedPayloadWithoutChangingAuthority(bool quotaReleaseUnavailable)
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "mid-batch-cancel", "store");
        var sourceA = TestPathUtils.PathUnder(temporary.Path, "mid-batch-cancel", "source-a");
        var sourceB = TestPathUtils.PathUnder(temporary.Path, "mid-batch-cancel", "source-b");
        var sentinel = TestPathUtils.PathUnder(temporary.Path, "mid-batch-cancel", "sentinel.txt");
        Directory.CreateDirectory(sourceA);
        Directory.CreateDirectory(sourceB);
        await File.WriteAllTextAsync(sentinel, "operator-owned sentinel");
        var producerA = PublicationProducer("mid-batch-cancel-a");
        var producerB = PublicationProducer("mid-batch-cancel-b");
        var plan = PublicationPlan(producerA, producerB);
        var writerA = new EvidenceArtifactWriter(producerA, sourceA);
        var writerB = new EvidenceArtifactWriter(producerB, sourceB);
        await writerA.WriteAsync("report", "reports/a.txt", "first producer payload"u8.ToArray());
        await writerB.WriteAsync("report", "reports/b.txt", "second producer payload"u8.ToArray());
        var manifest = EvidenceManifestBuilder.Build(plan,
        [
            new EvidenceProducerResult(producerA.Id, EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: writerA.WrittenArtifacts),
            new EvidenceProducerResult(producerB.Id, EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: writerB.WrittenArtifacts)
        ]);
        var repository = Hash("mid-batch-cancel-repository").ToLowerInvariant();
        var keyA = Hash("mid-batch-cancel-key-a").ToLowerInvariant();
        var keyB = Hash("mid-batch-cancel-key-b").ToLowerInvariant();
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        var leases = await store.AcquireAsync([keyA, keyB], CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var indexPath = TestPathUtils.PathUnder(root, "quota-index.json");
        byte[]? retainedIndexBytes = null;
        try
        {
            var leaseA = leases.Single(lease => lease.Key == keyA);
            var leaseB = leases.Single(lease => lease.Key == keyB);
            const string execution = "mid-batch-cancel-execution";
            leaseA.EstablishAuthority(execution);
            leaseB.EstablishAuthority(execution);
            var authorityA = leaseA.ReadState();
            var authorityB = leaseB.ReadState();
            var copyCallbacks = new List<string>();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                EvidenceReceiptStore.EvidenceReceiptKeyLease.PublishInvocationCoreAsync(root, repository, execution,
                    plan, manifest,
                    [
                        new EvidenceReceiptPublicationRequest(leaseA, producerA.Id, sourceA, DateTimeOffset.UtcNow),
                        new EvidenceReceiptPublicationRequest(leaseB, producerB.Id, sourceB, DateTimeOffset.UtcNow)
                    ], cancellation.Token,
                    beforeArtifactCopyForTests: (request, _) =>
                    {
                        copyCallbacks.Add(request.ProducerId);
                        if (request.ProducerId == producerB.Id)
                        {
                            if (quotaReleaseUnavailable)
                            {
                                retainedIndexBytes = File.ReadAllBytes(indexPath);
                                File.WriteAllText(indexPath, "invalid fixture quota JSON");
                            }
                            cancellation.Cancel();
                        }
                    }));

            // Check the live corrupt bytes before restoring the fixture snapshot for cleanup/inspection.
            // Restoration must never serve as evidence that rollback preserved the index.
            if (retainedIndexBytes is not null)
            {
                Assert.Equal("invalid fixture quota JSON", await File.ReadAllTextAsync(indexPath));
                await File.WriteAllBytesAsync(indexPath, retainedIndexBytes);
            }

            Assert.Equal(new[] { producerA.Id, producerB.Id }, copyCallbacks);
            Assert.Equal(authorityA, leaseA.ReadState());
            Assert.Equal(authorityB, leaseB.ReadState());
            Assert.Equal(execution, leaseA.ReadState()?.InFlightExecutionId);
            Assert.Equal(execution, leaseB.ReadState()?.InFlightExecutionId);
            foreach (var lease in leases)
            {
                var quota = QuotaEntry(root, lease.Key);
                Assert.Equal(0, quota.GetProperty("PayloadBytes").GetInt64());
                if (quotaReleaseUnavailable)
                {
                    Assert.True(quota.GetProperty("ReservedPayloadBytes").GetInt64() > 0);
                    Assert.False(NoPayloadReservations(quota.GetProperty("PayloadReservations")));
                }
                else
                {
                    Assert.Equal(0, quota.GetProperty("ReservedPayloadBytes").GetInt64());
                    Assert.True(NoPayloadReservations(quota.GetProperty("PayloadReservations")));
                }
                Assert.DoesNotContain(Directory.EnumerateDirectories(lease.DirectoryPath), path =>
                    Path.GetFileName(path).StartsWith("payload", StringComparison.Ordinal));
            }
            Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(sentinel));
        }
        finally
        {
            if (retainedIndexBytes is not null) await File.WriteAllBytesAsync(indexPath, retainedIndexBytes);
            foreach (var lease in leases.Reverse()) lease.Dispose();
        }
    }

    [Fact]
    public async Task ConfirmedPrecommitFailureRemovesStagedPayloadAndReleasesReservation()
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "confirmed-precommit-cleanup");
        var (plan, manifest, sourceRoot) = await CreatePublicationAttemptAsync(temporary.Path, fixture.Producer,
            "confirmed-precommit-cleanup-attempt");
        const string executionId = "confirmed-precommit-cleanup-execution";
        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        lease.EstablishAuthority(executionId);
        var previousState = lease.ReadState();

        var outcomes = await EvidenceReceiptStore.EvidenceReceiptKeyLease.PublishInvocationCoreAsync(
            fixture.StoreRoot, fixture.Repository, executionId, plan, manifest,
            [new EvidenceReceiptPublicationRequest(lease, fixture.Producer.Id, sourceRoot, DateTimeOffset.UtcNow)],
            CancellationToken.None,
            beforeAuthorityCommitForTests: () => throw new IOException("Injected confirmed precommit failure."));

        var outcome = outcomes[fixture.Producer.Id];
        Assert.False(outcome.Published);
        Assert.Equal("invocation-batch-commit-failed", outcome.Reason);
        Assert.Equal(previousState, lease.ReadState());
        Assert.Equal(fixture.PayloadName, lease.ReadState()?.PayloadDirectoryName);
        var quota = QuotaEntry(fixture.StoreRoot, fixture.Key);
        Assert.Equal(fixture.PayloadName, quota.GetProperty("PayloadDirectoryName").GetString());
        Assert.Equal(0, quota.GetProperty("ReservedPayloadBytes").GetInt64());
        Assert.True(NoPayloadReservations(quota.GetProperty("PayloadReservations")));
        Assert.Equal([fixture.PayloadName], Directory.EnumerateDirectories(lease.DirectoryPath)
            .Select(Path.GetFileName).Where(name => name is not null && name.StartsWith("payload-", StringComparison.Ordinal)));
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RolledBackPayloadRenameIsReclaimedBeforeInvalidationOrMaintenanceReleasesItsCharge(bool maintenance)
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "rolled-back-rename");
        var keyDirectory = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key);
        var stageName = "payload-stage-" + fixture.PayloadName["payload-".Length..];
        var stagePath = TestPathUtils.PathUnder(keyDirectory, stageName);
        // Model the post-crash namespace: the durable authority names the final payload, but its rename rolled back.
        using (var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(keyDirectory, false, true)!)
            keyRoot.MoveArtifactEntry(fixture.PayloadName, keyRoot, stageName);
        Assert.True(Directory.Exists(stagePath));
        Assert.True(QuotaEntry(fixture.StoreRoot, fixture.Key).GetProperty("PayloadBytes").GetInt64() > 0);

        if (maintenance)
        {
            EvidenceReceiptQuota.Maintain(fixture.StoreRoot, DateTimeOffset.UtcNow.AddDays(8));
            using var index = JsonDocument.Parse(await File.ReadAllBytesAsync(
                TestPathUtils.PathUnder(fixture.StoreRoot, "quota-index.json")));
            Assert.DoesNotContain(index.RootElement.GetProperty("Entries").EnumerateArray(),
                entry => entry.GetProperty("Key").GetString() == fixture.Key);
        }
        else
        {
            using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
            var read = await lease.ReadReceiptAsync(fixture.Producer,
                new EvidenceArtifactWriter(fixture.Producer, fixture.RestoreRoot), CancellationToken.None);
            Assert.False(read.IsCandidate);
            var quota = QuotaEntry(fixture.StoreRoot, fixture.Key);
            Assert.Equal(0, quota.GetProperty("PayloadBytes").GetInt64());
            Assert.Empty(quota.GetProperty("RetiredPayloads").EnumerateArray());
            Assert.Null(lease.ReadState()?.PayloadDirectoryName);
        }
        Assert.False(Directory.Exists(stagePath));
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
        Assert.True(Directory.Exists(fixture.SourceRoot));
    }

    [Fact]
    public async Task RetirementIndexFailureDoesNotRelabelAnAlreadyCommittedPublication()
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "retirement-index-failure");
        var (plan, manifest, sourceRoot) = await CreatePublicationAttemptAsync(temporary.Path, fixture.Producer,
            "retirement-index-failure-attempt");
        const string executionId = "retirement-index-failure-execution";
        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        lease.EstablishAuthority(executionId);
        var indexPath = TestPathUtils.PathUnder(fixture.StoreRoot, "quota-index.json");
        byte[]? committedIndex = null;
        IReadOnlyDictionary<string, EvidenceReceiptPublicationResult> outcomes;
        try
        {
            outcomes = await EvidenceReceiptStore.EvidenceReceiptKeyLease.PublishInvocationCoreAsync(
                fixture.StoreRoot, fixture.Repository, executionId, plan, manifest,
                [new EvidenceReceiptPublicationRequest(lease, fixture.Producer.Id, sourceRoot, DateTimeOffset.UtcNow)],
                CancellationToken.None,
                afterAuthorityCommitForTests: () =>
                {
                    committedIndex = File.ReadAllBytes(indexPath);
                    File.WriteAllText(indexPath, "{");
                });
            Assert.Equal("{", await File.ReadAllTextAsync(indexPath));
        }
        finally
        {
            if (committedIndex is not null) await File.WriteAllBytesAsync(indexPath, committedIndex);
        }
        var outcome = outcomes[fixture.Producer.Id];
        Assert.True(outcome.Published, outcome.Reason);
        Assert.Equal(outcome.PayloadDirectoryName, lease.ReadState()?.PayloadDirectoryName);
        Assert.Null(lease.ReadState()?.InFlightExecutionId);
        Assert.Equal(fixture.PayloadName, Assert.Single(EvidenceReceiptQuota.ReadRetiredPayloadNames(
            fixture.StoreRoot, fixture.Repository, fixture.Key)));
        Assert.True(Directory.Exists(TestPathUtils.PathUnder(lease.DirectoryPath, fixture.PayloadName)));
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
    }

    [Fact]
    public async Task IndeterminateCommitFailureRetainsStagedPayloadAndReservationAccounting()
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "indeterminate-commit-retention");
        var (plan, manifest, sourceRoot) = await CreatePublicationAttemptAsync(temporary.Path, fixture.Producer,
            "indeterminate-commit-retention-attempt");
        const string executionId = "indeterminate-commit-retention-execution";
        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        lease.EstablishAuthority(executionId);
        var indexPath = TestPathUtils.PathUnder(fixture.StoreRoot, "quota-index.json");
        byte[]? committedIndex = null;

        var outcomes = await EvidenceReceiptStore.EvidenceReceiptKeyLease.PublishInvocationCoreAsync(
            fixture.StoreRoot, fixture.Repository, executionId, plan, manifest,
            [new EvidenceReceiptPublicationRequest(lease, fixture.Producer.Id, sourceRoot, DateTimeOffset.UtcNow)],
            CancellationToken.None,
            afterAuthorityCommitForTests: () =>
            {
                committedIndex = File.ReadAllBytes(indexPath);
                File.WriteAllText(indexPath, "{");
                throw new IOException("Injected indeterminate postcommit failure.");
            });
        await File.WriteAllBytesAsync(indexPath, committedIndex!);

        var outcome = outcomes[fixture.Producer.Id];
        Assert.False(outcome.Published);
        Assert.Equal("invocation-batch-commit-failed", outcome.Reason);
        var state = lease.ReadState();
        var payloadName = Assert.IsType<string>(state?.PayloadDirectoryName);
        Assert.Null(state?.InFlightExecutionId);
        var payloadPath = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, payloadName);
        Assert.True(Directory.Exists(payloadPath));
        Assert.True(Directory.Exists(TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key,
            fixture.PayloadName)));
        var quota = QuotaEntry(fixture.StoreRoot, fixture.Key);
        Assert.Equal(payloadName, quota.GetProperty("PayloadDirectoryName").GetString());
        Assert.Equal(0, quota.GetProperty("ReservedPayloadBytes").GetInt64());
        Assert.True(NoPayloadReservations(quota.GetProperty("PayloadReservations")));
        using (var payloadRoot = CoverageRunOutputLease.AcquireArtifactDirectory(payloadPath,
                   createMissing: false, requirePrivate: true)!)
            Assert.Equal(EvidenceReceiptQuota.MeasurePayload(payloadRoot), quota.GetProperty("PayloadBytes").GetInt64());
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
    }

    [Fact]
    public async Task StagingCallbackFailureRemovesEarlierStageReleasesReservationsAndPropagates()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "callback-failure", "store");
        var sourceA = TestPathUtils.PathUnder(temporary.Path, "callback-failure", "source-a");
        var sourceB = TestPathUtils.PathUnder(temporary.Path, "callback-failure", "source-b");
        var sentinel = TestPathUtils.PathUnder(temporary.Path, "callback-failure", "sentinel.txt");
        Directory.CreateDirectory(sourceA);
        Directory.CreateDirectory(sourceB);
        await File.WriteAllTextAsync(sentinel, "operator-owned sentinel");
        var producerA = PublicationProducer("callback-failure-a");
        var producerB = PublicationProducer("callback-failure-b");
        var plan = PublicationPlan(producerA, producerB);
        var writerA = new EvidenceArtifactWriter(producerA, sourceA);
        var writerB = new EvidenceArtifactWriter(producerB, sourceB);
        await writerA.WriteAsync("report", "reports/a.txt", "first producer payload"u8.ToArray());
        await writerB.WriteAsync("report", "reports/b.txt", "second producer payload"u8.ToArray());
        var manifest = EvidenceManifestBuilder.Build(plan,
        [
            new EvidenceProducerResult(producerA.Id, EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: writerA.WrittenArtifacts),
            new EvidenceProducerResult(producerB.Id, EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: writerB.WrittenArtifacts)
        ]);
        var repository = Hash("callback-failure-repository").ToLowerInvariant();
        var keyA = Hash("callback-failure-key-a").ToLowerInvariant();
        var keyB = Hash("callback-failure-key-b").ToLowerInvariant();
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        var leases = await store.AcquireAsync([keyA, keyB], CancellationToken.None);
        try
        {
            var leaseA = leases.Single(lease => lease.Key == keyA);
            var leaseB = leases.Single(lease => lease.Key == keyB);
            const string execution = "callback-failure-execution";
            leaseA.EstablishAuthority(execution);
            leaseB.EstablishAuthority(execution);
            var authorityA = leaseA.ReadState();
            var authorityB = leaseB.ReadState();
            var callbacks = new List<string>();

            var failure = await Assert.ThrowsAsync<NotSupportedException>(() =>
                EvidenceReceiptStore.EvidenceReceiptKeyLease.PublishInvocationCoreAsync(root, repository, execution,
                    plan, manifest,
                    [
                        new EvidenceReceiptPublicationRequest(leaseA, producerA.Id, sourceA, DateTimeOffset.UtcNow),
                        new EvidenceReceiptPublicationRequest(leaseB, producerB.Id, sourceB, DateTimeOffset.UtcNow)
                    ], CancellationToken.None,
                    beforeArtifactCopyForTests: (request, _) =>
                    {
                        callbacks.Add(request.ProducerId);
                        if (request.ProducerId == producerB.Id)
                            throw new NotSupportedException("injected second-stage failure");
                    }));

            Assert.Equal("injected second-stage failure", failure.Message);
            Assert.Equal(new[] { producerA.Id, producerB.Id }, callbacks);
            Assert.Equal(authorityA, leaseA.ReadState());
            Assert.Equal(authorityB, leaseB.ReadState());
            Assert.Equal(execution, leaseA.ReadState()?.InFlightExecutionId);
            Assert.Equal(execution, leaseB.ReadState()?.InFlightExecutionId);
            foreach (var lease in leases)
            {
                var quota = QuotaEntry(root, lease.Key);
                Assert.Equal(0, quota.GetProperty("PayloadBytes").GetInt64());
                Assert.Equal(0, quota.GetProperty("ReservedPayloadBytes").GetInt64());
                Assert.True(NoPayloadReservations(quota.GetProperty("PayloadReservations")));
                Assert.DoesNotContain(Directory.EnumerateDirectories(lease.DirectoryPath), path =>
                    Path.GetFileName(path).StartsWith("payload", StringComparison.Ordinal));
            }
            Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(sentinel));
        }
        finally
        {
            foreach (var lease in leases.Reverse()) lease.Dispose();
        }
    }

    [Theory]
    [InlineData("duplicate-key")]
    [InlineData("duplicate-producer")]
    [InlineData("foreign-domain")]
    public async Task BatchPreflightRejectsDuplicateOrForeignRequestsWithoutChangingIndexes(string refusal)
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "batch-preflight-" + refusal);
        var payloadPath = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, fixture.PayloadName);
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(payloadPath, "origin.plan.json")));
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(payloadPath, "origin.manifest.json")));
        var indexPath = TestPathUtils.PathUnder(fixture.StoreRoot, "quota-index.json");
        var initialLease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        EvidenceReceiptStore? foreignStore = null;
        EvidenceReceiptStore.EvidenceReceiptKeyLease? secondLease = null;
        EvidenceReceiptStore.EvidenceReceiptKeyLease? foreignLease = null;
        var leases = new List<EvidenceReceiptStore.EvidenceReceiptKeyLease> { initialLease };
        try
        {
            IReadOnlyList<EvidenceReceiptPublicationRequest> requests;
            byte[] beforeLocal;
            byte[]? beforeForeign = null;
            if (refusal == "duplicate-key")
            {
                beforeLocal = await File.ReadAllBytesAsync(indexPath);
                requests =
                [
                    new(initialLease, fixture.Producer.Id, fixture.SourceRoot, DateTimeOffset.UtcNow),
                    new(initialLease, "another-producer", fixture.SourceRoot, DateTimeOffset.UtcNow)
                ];
            }
            else if (refusal == "duplicate-producer")
            {
                var secondKey = Hash("batch-preflight-second-key").ToLowerInvariant();
                secondLease = Assert.Single(await fixture.Store.AcquireAsync([secondKey], CancellationToken.None));
                leases.Add(secondLease);
                beforeLocal = await File.ReadAllBytesAsync(indexPath);
                requests =
                [
                    new(initialLease, fixture.Producer.Id, fixture.SourceRoot, DateTimeOffset.UtcNow),
                    new(secondLease, fixture.Producer.Id, fixture.SourceRoot, DateTimeOffset.UtcNow)
                ];
            }
            else
            {
                var foreignRoot = TestPathUtils.PathUnder(temporary.Path, "foreign-store");
                foreignStore = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
                { RootDirectory = foreignRoot, RepositoryNamespace = Hash("foreign-repository").ToLowerInvariant() });
                foreignLease = Assert.Single(await foreignStore.AcquireAsync(
                    [Hash("foreign-store-key").ToLowerInvariant()], CancellationToken.None));
                beforeLocal = await File.ReadAllBytesAsync(indexPath);
                beforeForeign = await File.ReadAllBytesAsync(TestPathUtils.PathUnder(foreignRoot, "quota-index.json"));
                requests = [new(foreignLease, fixture.Producer.Id, fixture.SourceRoot, DateTimeOffset.UtcNow)];
            }

            var foreignIndexPath = refusal == "foreign-domain"
                ? TestPathUtils.PathUnder(foreignLease!.StoreRoot, "quota-index.json") : null;
            if (refusal == "duplicate-key" || refusal == "duplicate-producer")
                await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.PublishInvocationAsync(
                    "batch-preflight", plan, manifest, requests, CancellationToken.None));
            else
                await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.PublishInvocationAsync(
                    "batch-preflight", plan, manifest, requests, CancellationToken.None));

            Assert.Equal(beforeLocal, await File.ReadAllBytesAsync(indexPath));
            if (foreignIndexPath is not null)
                Assert.Equal(beforeForeign, await File.ReadAllBytesAsync(foreignIndexPath));
            Assert.True(Directory.Exists(payloadPath));
            Assert.Equal(fixture.PayloadName, initialLease.ReadState()?.PayloadDirectoryName);
            Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
        }
        finally
        {
            foreignLease?.Dispose();
            foreach (var heldLease in leases.AsEnumerable().Reverse()) heldLease.Dispose();
        }
    }

    [Theory]
    [InlineData("wrong-execution", "execution-authority-mismatch")]
    [InlineData("unknown-producer", "producer-not-reusable-passed-result")]
    public async Task RefusedPublicationRequestLeavesAuthorityIndexAndPayloadSetUnchanged(string requestKind,
        string expectedReason)
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "publication-guard-" + requestKind);
        var payloadPath = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, fixture.PayloadName);
        var originPlan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(payloadPath, "origin.plan.json")));
        var originManifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(payloadPath, "origin.manifest.json")));
        const string executionId = "publication-guard-owner";

        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        lease.EstablishAuthority(executionId);
        var indexPath = TestPathUtils.PathUnder(fixture.StoreRoot, "quota-index.json");
        var beforeIndex = await File.ReadAllBytesAsync(indexPath);
        var producerId = requestKind == "unknown-producer" ? "not-in-origin-plan" : fixture.Producer.Id;
        var requestedExecutionId = requestKind == "wrong-execution" ? "different-execution" : executionId;
        var result = await EvidenceReceiptStore.EvidenceReceiptKeyLease.PublishInvocationCoreAsync(
            fixture.StoreRoot, fixture.Repository, requestedExecutionId, originPlan, originManifest,
            [new EvidenceReceiptPublicationRequest(lease, producerId, fixture.SourceRoot, DateTimeOffset.UtcNow)],
            CancellationToken.None);

        Assert.False(result[producerId].Published);
        Assert.Equal(expectedReason, result[producerId].Reason);
        Assert.Equal(beforeIndex, await File.ReadAllBytesAsync(indexPath));
        Assert.DoesNotContain(Directory.EnumerateDirectories(lease.DirectoryPath),
            path => Path.GetFileName(path).StartsWith("payload-stage-", StringComparison.Ordinal));
        Assert.DoesNotContain(Directory.EnumerateDirectories(lease.DirectoryPath),
            path => Path.GetFileName(path).StartsWith("payload-", StringComparison.Ordinal)
                && !StringComparer.Ordinal.Equals(Path.GetFileName(path), fixture.PayloadName));
        Assert.Equal(fixture.PayloadName, lease.ReadState()?.PayloadDirectoryName);
        Assert.True(QuotaEntry(fixture.StoreRoot, fixture.Key).GetProperty("PayloadBytes").GetInt64() > 0);
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
    }

    [Fact]
    public async Task FailedSecondAuditPublicationCleansEarlierStageAndReleasesItsExactReservation()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "receipts");
        var sourceA = TestPathUtils.PathUnder(temporary.Path, "source-a");
        var missingSourceB = TestPathUtils.PathUnder(temporary.Path, "source-b-missing");
        Directory.CreateDirectory(sourceA);
        var sourceBManifest = TestPathUtils.PathUnder(temporary.Path, "source-b-manifest");
        Directory.CreateDirectory(sourceBManifest);
        var repository = Hash("audit-stage-cleanup-repository");
        var producerA = PublicationProducer("producer-a");
        var producerB = PublicationProducer("producer-b");
        var plan = PublicationPlan(producerA, producerB);
        var writerA = new EvidenceArtifactWriter(producerA, sourceA);
        var writerB = new EvidenceArtifactWriter(producerB, sourceBManifest);
        await writerA.WriteAsync("report", "reports/a.txt", Encoding.UTF8.GetBytes("first staged payload"));
        await writerB.WriteAsync("report", "reports/b.txt", Encoding.UTF8.GetBytes("second manifest payload"));
        var manifest = EvidenceManifestBuilder.Build(plan,
        [
            new EvidenceProducerResult(producerA.Id, EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: writerA.WrittenArtifacts),
            new EvidenceProducerResult(producerB.Id, EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: writerB.WrittenArtifacts)
        ]);
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        var keyA = Hash("audit-stage-cleanup-a").ToLowerInvariant();
        var keyB = Hash("audit-stage-cleanup-b").ToLowerInvariant();
        var leases = await store.AcquireAsync([keyA, keyB], CancellationToken.None);
        try
        {
            var leaseA = leases.Single(lease => lease.Key == keyA);
            var leaseB = leases.Single(lease => lease.Key == keyB);
            leaseA.EstablishAuthority("audit-batch", auditIntent: true, expectedAssertions: "assertion");
            leaseB.EstablishAuthority("audit-batch", auditIntent: true, expectedAssertions: "assertion");
            var outcomes = await EvidenceReceiptStore.EvidenceReceiptKeyLease.PublishInvocationCoreAsync(root, repository,
                "audit-batch", plan, manifest,
                [
                    new EvidenceReceiptPublicationRequest(leaseA, producerA.Id, sourceA, DateTimeOffset.UtcNow),
                    new EvidenceReceiptPublicationRequest(leaseB, producerB.Id, missingSourceB, DateTimeOffset.UtcNow)
                ], CancellationToken.None);

            Assert.False(outcomes[producerA.Id].Published);
            Assert.False(outcomes[producerB.Id].Published);
            Assert.Equal("audit-batch-aborted:payload-publication-failed", outcomes[producerA.Id].Reason);
            Assert.Equal(outcomes[producerA.Id].Reason, outcomes[producerB.Id].Reason);
            Assert.DoesNotContain(Directory.EnumerateDirectories(leaseA.DirectoryPath),
                path => Path.GetFileName(path).StartsWith("payload", StringComparison.Ordinal));
            Assert.Equal(0, QuotaEntry(root, keyA).GetProperty("ReservedPayloadBytes").GetInt64());
            Assert.True(NoPayloadReservations(QuotaEntry(root, keyA).GetProperty("PayloadReservations")));
            Assert.Equal("audit-batch", leaseA.ReadState()?.AuditIntentExecutionId);
            Assert.Equal("audit-batch", leaseA.ReadState()?.InFlightExecutionId);
        }
        finally
        {
            foreach (var lease in leases.Reverse()) lease.Dispose();
        }
    }

    [Fact]
    public async Task CompleteCoverageInventoryPublishesAndRestoresAsVerifiedReceipt()
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreateCoverageReceiptFixtureAsync(temporary.Path, "valid", malformedCase: null);
        var expected = fixture.SourceArtifacts.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray();

        string payloadName;
        using (var publisher = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None)))
        {
            publisher.EstablishAuthority("coverage-inventory-valid-owner");
            var published = await publisher.PublishAsync("coverage-inventory-valid-owner", fixture.Plan,
                fixture.Manifest, fixture.Producer.Id, fixture.SourceRoot, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.True(published.Published, published.Reason);
            payloadName = published.PayloadDirectoryName!;
        }
        var payload = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, payloadName);
        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        var writer = new EvidenceArtifactWriter(fixture.Producer, fixture.RestoreRoot);
        var result = await lease.ReadReceiptAsync(fixture.Producer, writer, CancellationToken.None);

        Assert.True(result.IsCandidate, result.Reason);
        Assert.False(result.IsUnavailable);
        Assert.Equal(EvidenceProducerOutcome.Reused, result.Result?.Outcome);
        Assert.Equal(expected, result.Result?.Artifacts?.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray());
        Assert.Equal(expected.Length, writer.WrittenArtifacts.Count);
        Assert.All(expected, artifact => Assert.Equal(
            File.ReadAllBytes(TestPathUtils.PathUnder(fixture.SourceRoot, artifact.RelativePath)),
            File.ReadAllBytes(TestPathUtils.PathUnder(fixture.RestoreRoot, artifact.RelativePath))));
        Assert.Equal(payloadName, lease.ReadState()?.PayloadDirectoryName);
        Assert.True(Directory.Exists(payload));
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
    }

    [Theory]
    [InlineData("malformed-timings", "payload-publication-failed")]
    [InlineData("empty-project-slug", "inventory-project-manifest-mismatch")]
    [InlineData("missing-project-manifest", "inventory-required-file-missing")]
    [InlineData("missing-project-cobertura", "inventory-required-file-missing")]
    [InlineData("missing-test-result", "inventory-required-file-missing")]
    [InlineData("oversized-timings", "inventory-metadata-limit")]
    [InlineData("ineligible-slot", "inventory-slot-ineligible")]
    [InlineData("empty-inventory", "inventory-required-file-missing")]
    public async Task CoverageInventoryRefusalPreservesAuthorityAndReleasesTemporaryCustody(
        string invalidCase, string expectedReason)
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreateCoverageReceiptFixtureAsync(temporary.Path, invalidCase, invalidCase);
        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        const string execution = "coverage-inventory-refusal-owner";
        lease.EstablishAuthority(execution);
        var authorityBefore = lease.ReadState();

        var outcome = await lease.PublishAsync(execution, fixture.Plan, fixture.Manifest, fixture.Producer.Id,
            fixture.SourceRoot, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.False(outcome.Published);
        Assert.Equal(expectedReason, outcome.Reason);
        Assert.Equal(execution, lease.ReadState()?.InFlightExecutionId);
        Assert.Equal(authorityBefore, lease.ReadState());
        Assert.Null(lease.ReadState()?.PayloadDirectoryName);
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
        Assert.DoesNotContain(Directory.EnumerateDirectories(lease.DirectoryPath), path =>
            Path.GetFileName(path).StartsWith("payload-stage-", StringComparison.Ordinal)
            || Path.GetFileName(path).StartsWith("payload-", StringComparison.Ordinal));
        var quota = QuotaEntry(fixture.StoreRoot, fixture.Key);
        Assert.Equal(0, quota.GetProperty("PayloadBytes").GetInt64());
        Assert.Equal(0, quota.GetProperty("ReservedPayloadBytes").GetInt64());
        Assert.True(NoPayloadReservations(quota.GetProperty("PayloadReservations")));
    }

    [Fact]
    public async Task ReadOnlyPeekAndComparisonReportMalformedIndexWithoutChangingItOrPayload()
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "readonly-malformed-index");
        var payloadPath = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, fixture.PayloadName);
        var indexPath = TestPathUtils.PathUnder(fixture.StoreRoot, "quota-index.json");
        var payloadArtifact = TestPathUtils.PathUnder(payloadPath, "artifacts", "reports", "report.txt");
        var payloadBytesBefore = await File.ReadAllBytesAsync(payloadArtifact);
        var malformedIndex = Encoding.UTF8.GetBytes("{\"SchemaVersion\":1,\"Entries\":[");
        await File.WriteAllBytesAsync(indexPath, malformedIndex);
        var readOnly = EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
        { RootDirectory = fixture.StoreRoot, RepositoryNamespace = fixture.Repository });
        var identity = new EvidenceProducerIdentity(fixture.Key, fixture.Repository, [],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["tools"] = Hash("readonly-index-tools").ToLowerInvariant() });

        var peek = readOnly.Peek(fixture.Key);
        var comparable = readOnly.FindComparable(fixture.Producer.Id, identity);

        Assert.True(peek.IsUnavailable);
        Assert.Equal("foreign-or-unsupported-state-preserved", peek.Reason);
        Assert.False(comparable.Found);
        Assert.Equal("receipt-index-unavailable", comparable.Reason);
        Assert.Equal(malformedIndex, await File.ReadAllBytesAsync(indexPath));
        Assert.True(Directory.Exists(payloadPath));
        Assert.Equal(payloadBytesBefore, await File.ReadAllBytesAsync(payloadArtifact));
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
    }

    [Theory]
    [InlineData("passed-result", "passed-producer-record-invalid")]
    [InlineData("identity-digest", "input-identity-invalid")]
    [InlineData("noncanonical-identity", "input-identity-noncanonical")]
    [InlineData("origin-binding", "origin-binding-invalid")]
    [InlineData("invalid-base64", "passed-producer-record-invalid")]
    [InlineData("missing-metadata", "no-passed-receipt")]
    [InlineData("audit-quarantine", "audit-quarantined")]
    [InlineData("future-origin", "origin-time-in-future")]
    [InlineData("missing-payload", "payload-missing")]
    [InlineData("missing-plan-file", "missing")]
    [InlineData("missing-manifest-file", "missing")]
    [InlineData("missing-result-file", "missing")]
    [InlineData("oversized-authority-record", "foreign-or-unsupported-state-preserved")]
    public async Task ReadOnlyPeekReportsVerifiedReceiptCorruptionWithoutChangingAnyStoreBytes(
        string corruption, string expectedReason)
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "peek-corruption-" + corruption);
        var payloadPath = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, fixture.PayloadName);
        var payloadBackup = TestPathUtils.PathUnder(temporary.Path, "peek-payload-backup");
        var readOnly = EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
        { RootDirectory = fixture.StoreRoot, RepositoryNamespace = fixture.Repository });
        var baseline = readOnly.Peek(fixture.Key);
        Assert.True(baseline.IsCandidate);
        Assert.Equal("candidate", baseline.Reason);
        switch (corruption)
        {
            case "passed-result":
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(payloadPath, "passed-producer.json"), "null");
                break;
            case "identity-digest":
                {
                    var identityPath = TestPathUtils.PathUnder(payloadPath, "identity.json");
                    await File.AppendAllTextAsync(identityPath, " ");
                    break;
                }
            case "noncanonical-identity":
                {
                    var identityPath = TestPathUtils.PathUnder(payloadPath, "identity.json");
                    await File.AppendAllTextAsync(identityPath, " ");
                    var identityBytes = await File.ReadAllBytesAsync(identityPath);
                    var digest = Convert.ToHexString(SHA256.HashData(identityBytes)).ToLowerInvariant();
                    await UpdateFixtureAuthorityAsync(fixture, state => state["InputIdentityDigest"] = digest,
                        entry => entry["InputIdentityDigest"] = digest);
                    break;
                }
            case "origin-binding":
                await File.AppendAllTextAsync(TestPathUtils.PathUnder(payloadPath, "origin.manifest.json"), " ");
                break;
            case "invalid-base64":
                await UpdateFixtureAuthorityAsync(fixture, state => state["PassedProducerRecord"] = "%%%not-base64%%%");
                break;
            case "missing-metadata":
                await UpdateFixtureAuthorityAsync(fixture, state => state["OriginManifestDigest"] = null);
                break;
            case "audit-quarantine":
                using (var auditLease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None)))
                {
                    const string execution = "peek-audit-quarantine-owner";
                    auditLease.EstablishAuthority(execution, auditIntent: true, expectedAssertions: "assertion");
                    auditLease.MarkAuditMismatch(execution, "assertion");
                    auditLease.SettleExecution(execution);
                }
                break;
            case "future-origin":
                var future = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
                await UpdateFixtureAuthorityAsync(fixture,
                    state => state["OriginCompletedAt"] = future,
                    entry => entry["OriginCompletedAt"] = future);
                break;
            case "missing-payload":
                Directory.Move(payloadPath, payloadBackup);
                break;
            case "missing-plan-file":
                File.Delete(TestPathUtils.PathUnder(payloadPath, "origin.plan.json"));
                break;
            case "missing-manifest-file":
                File.Delete(TestPathUtils.PathUnder(payloadPath, "origin.manifest.json"));
                break;
            case "missing-result-file":
                File.Delete(TestPathUtils.PathUnder(payloadPath, "passed-producer.json"));
                break;
            case "oversized-authority-record":
                await UpdateFixtureAuthorityAsync(fixture,
                    state => state["PassedProducerRecord"] = new string('A', 64 * 1024 + 1));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, "Unknown receipt corruption case.");
        }

        var bytesBeforePeek = SnapshotStoreBytes(fixture.StoreRoot);
        EvidenceReceiptReadResult result;
        try
        {
            result = readOnly.Peek(fixture.Key);
            AssertStoreSnapshotUnchanged(bytesBeforePeek, fixture.StoreRoot);
        }
        finally
        {
            if (Directory.Exists(payloadBackup)) Directory.Move(payloadBackup, payloadPath);
        }

        Assert.False(result.IsCandidate);
        Assert.Equal(corruption == "oversized-authority-record", result.IsUnavailable);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
    }

    [Fact]
    public async Task FindComparableRejectsWrongNamespaceAndSkipsInvalidCandidateWithoutMutatingStore()
    {
        using var temporary = TestDirectory.Create();
        var wrongNamespaceFixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "comparable-wrong-namespace");
        var wrongNamespaceStore = EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
        { RootDirectory = wrongNamespaceFixture.StoreRoot, RepositoryNamespace = wrongNamespaceFixture.Repository });
        var matchingNamespaceBaseline = wrongNamespaceStore.FindComparable(wrongNamespaceFixture.Producer.Id,
            FixtureIdentity(wrongNamespaceFixture));
        Assert.True(matchingNamespaceBaseline.Found);
        Assert.Equal("identity-matches", matchingNamespaceBaseline.Reason);
        var wrongNamespaceSnapshot = SnapshotStoreBytes(wrongNamespaceFixture.StoreRoot);

        var wrongNamespace = wrongNamespaceStore.FindComparable(wrongNamespaceFixture.Producer.Id,
            FixtureIdentity(wrongNamespaceFixture, Hash("different-repository-namespace").ToLowerInvariant()));

        Assert.False(wrongNamespace.Found);
        Assert.True(wrongNamespace.IsUnavailable);
        Assert.Equal("repository-namespace-mismatch", wrongNamespace.Reason);
        AssertStoreSnapshotUnchanged(wrongNamespaceSnapshot, wrongNamespaceFixture.StoreRoot);

        var invalidCandidateFixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "comparable-invalid-candidate");
        var payloadPath = TestPathUtils.PathUnder(invalidCandidateFixture.StoreRoot, invalidCandidateFixture.Repository,
            invalidCandidateFixture.Key, invalidCandidateFixture.PayloadName);
        var invalidCandidateStore = EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
        { RootDirectory = invalidCandidateFixture.StoreRoot, RepositoryNamespace = invalidCandidateFixture.Repository });
        var validCandidateBaseline = invalidCandidateStore.FindComparable(invalidCandidateFixture.Producer.Id,
            FixtureIdentity(invalidCandidateFixture));
        Assert.True(validCandidateBaseline.Found);
        Assert.Equal("identity-matches", validCandidateBaseline.Reason);
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(payloadPath, "passed-producer.json"), "null");
        var invalidCandidateSnapshot = SnapshotStoreBytes(invalidCandidateFixture.StoreRoot);

        var filtered = invalidCandidateStore.FindComparable(invalidCandidateFixture.Producer.Id,
            FixtureIdentity(invalidCandidateFixture));

        Assert.False(filtered.Found);
        Assert.False(filtered.IsUnavailable);
        Assert.Equal("no-comparable-receipt", filtered.Reason);
        AssertStoreSnapshotUnchanged(invalidCandidateSnapshot, invalidCandidateFixture.StoreRoot);
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(invalidCandidateFixture.SentinelPath));
    }

    [Fact]
    public async Task FindComparableReportsCompletionTimeFromVerifiedAuthoritySnapshot()
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "comparable-authority-time");
        var indexedTime = QuotaEntry(fixture.StoreRoot, fixture.Key).GetProperty("OriginCompletedAt").GetDateTimeOffset();
        var verifiedTime = indexedTime.AddMinutes(-1);
        await UpdateFixtureAuthorityAsync(fixture, state => state["OriginCompletedAt"] = verifiedTime);
        var readOnly = EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
        { RootDirectory = fixture.StoreRoot, RepositoryNamespace = fixture.Repository });

        var comparable = readOnly.FindComparable(fixture.Producer.Id, FixtureIdentity(fixture));

        Assert.True(comparable.Found, comparable.Reason);
        Assert.Equal(verifiedTime, comparable.OriginCompletedAt);
        Assert.NotEqual(indexedTime, comparable.OriginCompletedAt);
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(32, false)]
    public async Task OptionalReceiptCustodyBoundsNestingWithoutChangingSuccessfulSourceReports(
        int nestedDirectories, bool expectedPublication)
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "store");
        var source = TestPathUtils.PathUnder(temporary.Path, "source");
        var repository = Hash("receipt-depth-repository").ToLowerInvariant();
        var key = Hash("receipt-depth-key").ToLowerInvariant();
        var producer = PublicationProducer("receipt-depth");
        var plan = PublicationPlan(producer);
        var path = "reports/" + string.Join('/', Enumerable.Repeat("d", nestedDirectories)) + "/report.txt";
        var writer = new EvidenceArtifactWriter(producer, source);
        await writer.WriteAsync("report", path, "ordinary successful report"u8.ToArray());
        var manifest = EvidenceManifestBuilder.Build(plan,
            [new(producer.Id, EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: writer.WrittenArtifacts)]);
        Assert.Equal(EvidenceExecutionVerdict.Passed, manifest.ExecutionVerdict);
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        lease.EstablishAuthority("depth-owner");

        var result = await lease.PublishAsync("depth-owner", plan, manifest, producer.Id, source,
            DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(expectedPublication, result.Published);
        Assert.Equal(expectedPublication ? "published" : "payload-publication-failed", result.Reason);
        Assert.Equal("ordinary successful report", await File.ReadAllTextAsync(TestPathUtils.PathUnder(source, path)));
        Assert.Equal(EvidenceExecutionVerdict.Passed, manifest.ExecutionVerdict);
        Assert.True(NoPayloadReservations(QuotaEntry(root, key).GetProperty("PayloadReservations")));
        if (!expectedPublication)
        {
            Assert.Equal("depth-owner", lease.ReadState()?.InFlightExecutionId);
            Assert.Null(lease.ReadState()?.PayloadDirectoryName);
            Assert.DoesNotContain(Directory.EnumerateDirectories(lease.DirectoryPath), entry =>
                Path.GetFileName(entry).StartsWith("payload", StringComparison.Ordinal));
            Assert.Equal(0, QuotaEntry(root, key).GetProperty("PayloadBytes").GetInt64());
        }
    }

    [Fact]
    public async Task ReceiptMetadataCannotBeCommittedOrUsedWithoutExecutionAuthority()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "store");
        var key = Hash("no-authority-key").ToLowerInvariant();
        var producer = PublicationProducer("publication-without-authority");
        var (plan, manifest, sourceRoot) = await CreatePublicationAttemptAsync(temporary.Path, producer, "candidate");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = Hash("no-authority-repository").ToLowerInvariant() });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        var before = SnapshotStoreBytes(root);
        Assert.Null(lease.ReadState());

        Assert.Throws<InvalidOperationException>(() => lease.SettleExecution("missing-owner"));
        Assert.Throws<InvalidOperationException>(() => lease.MarkAuditMismatch("missing-owner", "assertion"));
        Assert.Throws<InvalidOperationException>(() => lease.RecordSuccessfulUse(DateTimeOffset.UtcNow));
        var refused = await lease.PublishAsync("missing-owner", plan, manifest, producer.Id, sourceRoot,
            DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.False(refused.Published);
        Assert.Equal("execution-authority-mismatch", refused.Reason);

        Assert.Null(lease.ReadState());
        AssertStoreSnapshotUnchanged(before, root);
        Assert.DoesNotContain(Directory.EnumerateDirectories(lease.DirectoryPath), entry =>
            Path.GetFileName(entry).StartsWith("payload", StringComparison.Ordinal));
        lease.EstablishAuthority("real-owner");
        Assert.Equal("real-owner", lease.ReadState()?.InFlightExecutionId);
        lease.SettleExecution("real-owner");
        Assert.Null(lease.ReadState()?.InFlightExecutionId);
    }

    [Fact]
    public async Task AuditIntentWithoutExpectedAssertionsFreezesTheFirstRecordedSet()
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "store");
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = Hash("first-audit-assertions") });
        using var lease = Assert.Single(await store.AcquireAsync([Hash("first-audit-key")], CancellationToken.None));
        lease.EstablishAuthority("audit", auditIntent: true);
        Assert.Null(lease.ReadState()?.AuditExpectedAssertions);

        lease.MarkAuditMismatch("audit", "first-set");
        var first = lease.ReadState();
        lease.MarkAuditMismatch("audit", "different-set");

        Assert.Equal(first, lease.ReadState());
        Assert.Equal("first-set", first?.AuditExpectedAssertions);
        Assert.True(first?.AuditMismatch);
        Assert.Equal("audit", first?.InFlightExecutionId);
        lease.SettleExecution("audit");
        Assert.Equal("audit-quarantined", lease.ProposeReadReceipt().Reason);
    }

    [Theory]
    [InlineData("PassedProducerRecord")]
    [InlineData("OriginManifestDigest")]
    public async Task IncompletePassedAuthorityCannotProposeReuseOrDeleteItsRetainedPayload(string field)
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "missing-passed-authority");
        await UpdateFixtureAuthorityAsync(fixture, state => state[field] = null);
        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        var before = SnapshotStoreBytes(fixture.StoreRoot);

        var proposal = lease.ProposeReadReceipt();
        var readOnly = EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
        { RootDirectory = fixture.StoreRoot, RepositoryNamespace = fixture.Repository });
        var explanation = readOnly.Peek(fixture.Key);

        Assert.False(proposal.IsCandidate);
        Assert.False(proposal.IsUnavailable);
        Assert.Equal("no-passed-receipt", proposal.Reason);
        Assert.Equal(proposal with { State = lease.ReadState() }, explanation);
        Assert.Equal(fixture.PayloadName, lease.ReadState()?.PayloadDirectoryName);
        AssertStoreSnapshotUnchanged(before, fixture.StoreRoot);
        Assert.Equal("operator-owned sentinel", await File.ReadAllTextAsync(fixture.SentinelPath));
    }

    [Fact]
    public async Task VerifiedLegacyReceiptWithoutIdentityCannotSupplyAComparableInputExplanation()
    {
        using var temporary = TestDirectory.Create();
        var fixture = await CreatePublishedReceiptFixtureAsync(temporary.Path, "legacy-no-identity");
        var payload = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, fixture.PayloadName);
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(payload, "origin.plan.json")));
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(payload, "origin.manifest.json")));
        using (var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None)))
        {
            lease.EstablishAuthority("legacy-no-identity");
            var published = await lease.PublishAsync("legacy-no-identity", plan, manifest, fixture.Producer.Id,
                fixture.SourceRoot, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.True(published.Published, published.Reason);
        }
        var readOnly = EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
        { RootDirectory = fixture.StoreRoot, RepositoryNamespace = fixture.Repository });
        var verified = readOnly.Peek(fixture.Key);
        Assert.True(verified.IsCandidate, verified.Reason);
        Assert.Null(verified.InputIdentity);
        var before = SnapshotStoreBytes(fixture.StoreRoot);

        var comparable = readOnly.FindComparable(fixture.Producer.Id, FixtureIdentity(fixture));

        Assert.False(comparable.Found);
        Assert.False(comparable.IsUnavailable);
        Assert.Equal("no-comparable-receipt", comparable.Reason);
        AssertStoreSnapshotUnchanged(before, fixture.StoreRoot);
    }

    [Fact]
    public void OwnedPayloadCleanupAcceptsTheSameAggregateTreeBoundAsAdmission()
    {
        using var temporary = TestDirectory.Create();
        var keyDirectory = TestPathUtils.PathUnder(temporary.Path, "key");
        var payload = TestPathUtils.PathUnder(keyDirectory, "payload-owned");
        var origin = temporary.WriteFile("origin.txt", "preserved source");
        using (var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(keyDirectory, true, true)!)
        using (keyRoot.CreateArtifactDirectory("payload-owned")) { }
        for (var index = 0; index < 40; index++)
        {
            var directory = TestPathUtils.PathUnder(payload, index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "a", "b", "c", "d", "e", "f");
            Directory.CreateDirectory(directory);
            File.WriteAllText(TestPathUtils.PathUnder(directory, "report.txt"), "owned report");
        }
        Assert.True(Directory.EnumerateFileSystemEntries(payload, "*", SearchOption.AllDirectories).Count() > 264);

        Assert.True(EvidenceReceiptStore.EvidenceReceiptKeyLease.TryDeleteOwnedPayload(keyDirectory, "payload-owned"));

        Assert.False(Directory.Exists(payload));
        Assert.Equal("preserved source", File.ReadAllText(origin));
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    public void CommittedLegacyPayloadDoesNotAuthorizeInferredStageCleanup(string suffix)
    {
        using var temporary = TestDirectory.Create();
        var keyDirectory = TestPathUtils.PathUnder(temporary.Path, "key");
        var payload = "payload-" + suffix;
        var stage = "payload-stage-" + suffix;
        using (var root = CoverageRunOutputLease.AcquireArtifactDirectory(keyDirectory, true, true)!)
        using (root.CreateArtifactDirectory(stage)) { }

        Assert.True(EvidenceReceiptStore.EvidenceReceiptKeyLease.TryDeleteOwnedPayload(keyDirectory, payload,
            recoverCommittedStage: true));

        Assert.True(Directory.Exists(TestPathUtils.PathUnder(keyDirectory, stage)));
    }

    [Theory]
    [InlineData("both")]
    [InlineData("file")]
    [InlineData("identity")]
    public void CommittedStageRecoveryPreservesAmbiguousOrChangedEntries(string refusal)
    {
        using var temporary = TestDirectory.Create();
        var keyDirectory = TestPathUtils.PathUnder(temporary.Path, "key");
        var payload = "payload-" + Guid.NewGuid().ToString("N");
        var stage = "payload-stage-" + payload["payload-".Length..];
        using (var root = CoverageRunOutputLease.AcquireArtifactDirectory(keyDirectory, true, true)!)
        {
            if (refusal == "file")
            {
                using var file = root.OpenArtifactFile(stage, true);
                file.Write("preserved"u8);
            }
            else
            {
                using var staged = root.CreateArtifactDirectory(stage);
            }
            if (refusal == "both")
            {
                using var final = root.CreateArtifactDirectory(payload);
            }
        }

        Assert.False(EvidenceReceiptStore.EvidenceReceiptKeyLease.TryDeleteOwnedPayload(keyDirectory, payload,
            identity: refusal == "identity" ? "different-native-identity" : null, recoverCommittedStage: true));

        Assert.True(Path.Exists(TestPathUtils.PathUnder(keyDirectory, stage)));
        if (refusal == "both") Assert.True(Directory.Exists(TestPathUtils.PathUnder(keyDirectory, payload)));
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public async Task ReceiptDepthRefusalOccursBeforeAnyPayloadReservation(int nestedDirectories, bool shouldPublish)
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "store");
        var source = TestPathUtils.PathUnder(temporary.Path, "source");
        var repository = Hash("receipt-depth-repository").ToLowerInvariant();
        var key = Hash("receipt-depth-key").ToLowerInvariant();
        var producer = PublicationProducer("receipt-depth") with
        { ArtifactSlots = [new("report", "reports", "text/plain", true, 1024) { MaximumFiles = 1 }] };
        var plan = PublicationPlan(producer);
        var writer = new EvidenceArtifactWriter(producer, source);
        var path = "reports/" + string.Join('/', Enumerable.Repeat("a", nestedDirectories)) + "/report.txt";
        await writer.WriteAsync("report", path, "passed"u8.ToArray());
        var manifest = EvidenceManifestBuilder.Build(plan,
            [new(producer.Id, EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: writer.WrittenArtifacts)]);
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        lease.EstablishAuthority("depth-owner");

        var publication = await lease.PublishAsync("depth-owner", plan, manifest, producer.Id, source,
            DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(shouldPublish, publication.Published);
        Assert.True(await writer.VerifyWrittenArtifactsAsync());
        Assert.Equal(0, QuotaEntry(root, key).GetProperty("ReservedPayloadBytes").GetInt64());
        if (!shouldPublish) Assert.Empty(Directory.EnumerateDirectories(lease.DirectoryPath, "payload-*"));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(128, false)]
    public async Task LargeReceiptTreesPreserveFreshReportsWhenOptionalCustodyIsUnavailable(int fileCount, bool shouldPublish)
    {
        using var temporary = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(temporary.Path, "store");
        var source = TestPathUtils.PathUnder(temporary.Path, "source");
        var repository = Hash("aggregate-tree-repository").ToLowerInvariant();
        var key = Hash("aggregate-tree-key").ToLowerInvariant();
        var producer = PublicationProducer("aggregate-tree") with
        { ArtifactSlots = [new("report", "reports", "text/plain", true, 1024) { MaximumFiles = 128 }] };
        var plan = PublicationPlan(producer);
        var writer = new EvidenceArtifactWriter(producer, source);
        for (var index = 0; index < fileCount; index++)
            // Large trees may exhaust native handles before they cross the 1,024-entry custody bound.
            await writer.WriteAsync("report", $"reports/{index}/a/b/c/d/e/f/report.txt", new byte[] { 42 });
        var manifest = EvidenceManifestBuilder.Build(plan,
            [new(producer.Id, EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: writer.WrittenArtifacts)]);
        Assert.True(EvidenceManifestBuilder.VerifyForGate(plan, manifest));
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = root, RepositoryNamespace = repository });
        using var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None));
        lease.EstablishAuthority("aggregate-tree-owner");

        var publication = await lease.PublishAsync("aggregate-tree-owner", plan, manifest, producer.Id, source,
            DateTimeOffset.UtcNow, CancellationToken.None);

        if (shouldPublish) Assert.True(publication.Published, publication.Reason);
        else Assert.False(publication.Published);
        Assert.True(await writer.VerifyWrittenArtifactsAsync());
        Assert.Equal(fileCount, writer.WrittenArtifacts.Count);
        Assert.True(EvidenceManifestBuilder.VerifyForGate(plan, manifest));
        if (shouldPublish) Assert.Equal(publication.PayloadDirectoryName, lease.ReadState()?.PayloadDirectoryName);
        else
        {
            Assert.Equal("payload-publication-failed", publication.Reason);
            Assert.Null(lease.ReadState()?.PayloadDirectoryName);
            Assert.Equal("aggregate-tree-owner", lease.ReadState()?.InFlightExecutionId);
            var quota = QuotaEntry(root, key);
            Assert.Equal(0, quota.GetProperty("ReservedPayloadBytes").GetInt64());
            Assert.True(NoPayloadReservations(quota.GetProperty("PayloadReservations")));
            Assert.Empty(Directory.EnumerateDirectories(lease.DirectoryPath, "payload-*"));
        }
    }

    private static EvidenceProducerIdentity FixtureIdentity(PublishedReceiptFixture fixture, string? repository = null)
        => new(fixture.Key, repository ?? fixture.Repository, [],
            new Dictionary<string, string>(StringComparer.Ordinal)
            { ["tools"] = Hash("receipt-matrix-tools").ToLowerInvariant() });

    private static Dictionary<string, byte[]> SnapshotStoreBytes(string storeRoot)
        => Directory.EnumerateFiles(storeRoot, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToDictionary(path => Path.GetRelativePath(storeRoot, path), ReadStoreSnapshotFile, StringComparer.Ordinal);

    [Fact]
    public void StoreSnapshotRetainsEmptyCoordinationFilesAndRejectsTheirContent()
    {
        using var directory = TestDirectory.Create();
        var coordination = directory.WriteFile(".appsurface-coverage-operation.lock", string.Empty);
        directory.WriteFile("authority.json", "retained authority");

        var snapshot = SnapshotStoreBytes(directory.Path);

        Assert.Empty(snapshot[".appsurface-coverage-operation.lock"]);
        Assert.Equal("retained authority", Encoding.UTF8.GetString(snapshot["authority.json"]));
        File.WriteAllText(coordination, "unexpected content");
        Assert.Throws<Xunit.Sdk.EqualException>(() => SnapshotStoreBytes(directory.Path));
    }

    private static byte[] ReadStoreSnapshotFile(string path)
    {
        if (Path.GetFileName(path) == ".appsurface-coverage-operation.lock")
        {
            // Windows protects the coordination byte range from reads while held. The
            // file is deliberately empty; preserve its presence and exact size in the snapshot.
            Assert.Equal(0, new FileInfo(path).Length);
            return [];
        }
        return File.ReadAllBytes(path);
    }

    private static void AssertStoreSnapshotUnchanged(IReadOnlyDictionary<string, byte[]> expected, string storeRoot)
    {
        var actual = SnapshotStoreBytes(storeRoot);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (relativePath, expectedBytes) in expected)
            Assert.Equal(expectedBytes, actual[relativePath]);
    }

    private static EvidenceProducerDeclaration PublicationProducer(string id) => new(id, "unit", "1", [], ["assertion"],
        [new EvidenceArtifactSlot("report", "reports", "text/plain", true, 1024) { MaximumFiles = 4 }], 30)
    {
        Inputs = new EvidenceProducerInputs(["**"], [])
    };

    private static EvidencePlan PublicationPlan(params EvidenceProducerDeclaration[] producers)
    {
        var profile = new EvidenceProfile("audit-cleanup", EvidenceProfileScope.Targeted, [], producers,
            producers.Select(producer => new EvidenceObligation("obligation-" + producer.Id, "risk", "reason",
                [producer.Id], "assertion")).ToArray());
        var policy = new EvidencePolicy("policy", "1", profile.Id, [profile], []);
        var paths = Array.Empty<NormalizedDiffPath>();
        var plan = new EvidencePlan("1.1", policy.Id, EvidenceDigest.CanonicalSha256(policy),
            EvidenceDigest.CanonicalSha256(paths), profile, paths, [], string.Empty, policy);
        return plan with { PlanDigest = EvidenceDigest.CanonicalSha256(plan) };
    }

    private static async Task<(EvidencePlan Plan, EvidenceManifest Manifest, string SourceRoot)> CreatePublicationAttemptAsync(
        string parent, EvidenceProducerDeclaration producer, string name)
    {
        var sourceRoot = TestPathUtils.PathUnder(parent, name, "source");
        Directory.CreateDirectory(sourceRoot);
        var plan = PublicationPlan(producer);
        var writer = new EvidenceArtifactWriter(producer, sourceRoot);
        await writer.WriteAsync("report", "reports/attempt.txt", "replacement receipt payload"u8.ToArray());
        var manifest = EvidenceManifestBuilder.Build(plan,
            [new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Passed, ["assertion"],
                Artifacts: writer.WrittenArtifacts)]);
        return (plan, manifest, sourceRoot);
    }

    private static JsonElement QuotaEntry(string root, string key)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(TestPathUtils.PathUnder(root, "quota-index.json")));
        return document.RootElement.GetProperty("Entries").EnumerateArray()
            .Single(entry => entry.GetProperty("Key").GetString() == key).Clone();
    }

    private static async Task UpdateFixtureAuthorityAsync(PublishedReceiptFixture fixture,
        Action<JsonObject> updateState, Action<JsonObject>? updateEntry = null)
    {
        var indexPath = TestPathUtils.PathUnder(fixture.StoreRoot, "quota-index.json");
        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
        var entry = index["Entries"]!.AsArray().Single(item =>
            StringComparer.Ordinal.Equals(item!["Key"]!.GetValue<string>(), fixture.Key))!.AsObject();
        updateState(entry["AuthorityState"]!.AsObject());
        updateEntry?.Invoke(entry);
        await File.WriteAllTextAsync(indexPath, index.ToJsonString());
    }

    private static async Task RewritePassedRecordWithOwnedAuthorityAsync(PublishedReceiptFixture fixture,
        EvidenceProducerResult result)
    {
        var payloadPath = TestPathUtils.PathUnder(fixture.StoreRoot, fixture.Repository, fixture.Key, fixture.PayloadName);
        var resultBytes = EvidenceCanonicalJson.Serialize(result);
        using var lease = Assert.Single(await fixture.Store.AcquireAsync([fixture.Key], CancellationToken.None));
        Assert.Equal(fixture.PayloadName, lease.ReadState()?.PayloadDirectoryName);
        await File.WriteAllBytesAsync(TestPathUtils.PathUnder(payloadPath, "passed-producer.json"), resultBytes);
        await UpdateFixtureAuthorityAsync(fixture,
            state => state["PassedProducerRecord"] = Convert.ToBase64String(resultBytes));
    }

    private static bool NoPayloadReservations(JsonElement reservations)
        => reservations.ValueKind == JsonValueKind.Null
            || reservations.ValueKind == JsonValueKind.Array && reservations.GetArrayLength() == 0;

    private static async Task<PublishedReceiptFixture> CreatePublishedReceiptFixtureAsync(string parent, string name)
    {
        var storeRoot = TestPathUtils.PathUnder(parent, name, "store");
        var sourceRoot = TestPathUtils.PathUnder(parent, name, "source");
        var restoreRoot = TestPathUtils.PathUnder(parent, name, "restore");
        var sentinelPath = TestPathUtils.PathUnder(parent, name, "operator-sentinel.txt");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(restoreRoot);
        await File.WriteAllTextAsync(sentinelPath, "operator-owned sentinel");
        var producer = PublicationProducer("receipt-matrix-producer");
        var plan = PublicationPlan(producer);
        var sourceWriter = new EvidenceArtifactWriter(producer, sourceRoot);
        await sourceWriter.WriteAsync("report", "reports/report.txt", Encoding.UTF8.GetBytes("verified origin bytes"));
        var manifest = EvidenceManifestBuilder.Build(plan,
            [new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Passed, ["assertion"],
                Artifacts: sourceWriter.WrittenArtifacts)]);
        var repository = Hash("receipt-matrix-repository").ToLowerInvariant();
        var key = Hash("receipt-matrix-key-" + name).ToLowerInvariant();
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = storeRoot, RepositoryNamespace = repository });
        string payloadName;
        using (var lease = Assert.Single(await store.AcquireAsync([key], CancellationToken.None)))
        {
            lease.EstablishAuthority("receipt-matrix-execution");
            var components = new Dictionary<string, string>(StringComparer.Ordinal)
            { ["tools"] = Hash("receipt-matrix-tools").ToLowerInvariant() };
            var identity = new EvidenceProducerIdentity(key, repository, [], components);
            var outcomes = await store.PublishInvocationAsync("receipt-matrix-execution", plan, manifest,
                [new EvidenceReceiptPublicationRequest(lease, producer.Id, sourceRoot, DateTimeOffset.UtcNow)
                    { Identity = identity }],
                CancellationToken.None);
            Assert.True(outcomes[producer.Id].Published, outcomes[producer.Id].Reason);
            payloadName = outcomes[producer.Id].PayloadDirectoryName!;
        }

        return new PublishedReceiptFixture(store, storeRoot, sourceRoot, restoreRoot, sentinelPath, repository, key,
            producer, payloadName);
    }

    private static async Task<CoverageReceiptFixture> CreateCoverageReceiptFixtureAsync(string parent, string name,
        string? malformedCase)
    {
        var storeRoot = TestPathUtils.PathUnder(parent, name, "store");
        var sourceRoot = TestPathUtils.PathUnder(parent, name, "source");
        var restoreRoot = TestPathUtils.PathUnder(parent, name, "restore");
        var sentinelPath = TestPathUtils.PathUnder(parent, name, "operator-sentinel.txt");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(restoreRoot);
        await File.WriteAllTextAsync(sentinelPath, "operator-owned sentinel");
        var slot = CoverageEvidenceInventory.CreateSlot();
        if (malformedCase == "ineligible-slot") slot = slot with { MaximumFiles = null };
        if (malformedCase == "empty-inventory") slot = slot with { Required = false };
        var producer = new EvidenceProducerDeclaration("coverage-inventory-producer", "coverage", "1", [], ["assertion"],
            [slot], 30)
        { Inputs = new EvidenceProducerInputs(["**"], []) };
        var plan = PublicationPlan(producer);
        var writer = new EvidenceArtifactWriter(producer, sourceRoot);
        var timings = malformedCase switch
        {
            "malformed-timings" => "{"u8.ToArray(),
            "empty-project-slug" => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            { projects = new[] { new { slug = " ", testResults = Array.Empty<object>() } } })),
            "oversized-timings" => Enumerable.Repeat((byte)' ', CoverageEvidenceInventory.MaximumMetadataBytes + 1).ToArray(),
            _ => Encoding.UTF8.GetBytes("{\"projects\":[{\"slug\":\"unit\",\"testResults\":[{\"path\":\"test-results/unit.xml\"}]}]}")
        };
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["coverage/coverage.cobertura.xml"] = "<coverage/>"u8.ToArray(),
            ["coverage/coverage-gate.json"] = "{}"u8.ToArray(),
            ["coverage/coverage-gate.md"] = "gate passed"u8.ToArray(),
            ["coverage/timings.json"] = timings,
            ["coverage/summary.txt"] = "summary"u8.ToArray(),
            ["coverage/projects/unit/coverage-project.json"] = "{\"schemaVersion\":1,\"slug\":\"unit\"}"u8.ToArray(),
            ["coverage/projects/unit/coverage.cobertura.xml"] = "<coverage/>"u8.ToArray(),
            ["coverage/test-results/unit.xml"] = "<testsuites/>"u8.ToArray()
        };
        if (malformedCase == "empty-inventory") files.Clear();
        if (malformedCase == "ineligible-slot")
        {
            // One file satisfies the generic single-file slot; receipt-specific admission must still reject it.
            files.Clear();
            files["coverage/coverage.cobertura.xml"] = "<coverage/>"u8.ToArray();
        }
        if (malformedCase == "missing-project-manifest") files.Remove("coverage/projects/unit/coverage-project.json");
        if (malformedCase == "missing-project-cobertura") files.Remove("coverage/projects/unit/coverage.cobertura.xml");
        if (malformedCase == "missing-test-result") files.Remove("coverage/test-results/unit.xml");
        foreach (var (path, bytes) in files)
            await writer.WriteAsync(CoverageEvidenceInventory.LogicalName, path, bytes);

        var manifest = EvidenceManifestBuilder.Build(plan,
            [new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Passed, ["assertion"],
                Artifacts: writer.WrittenArtifacts)]);
        var repository = Hash("coverage-inventory-repository").ToLowerInvariant();
        var key = Hash("coverage-inventory-key-" + name).ToLowerInvariant();
        var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
        { RootDirectory = storeRoot, RepositoryNamespace = repository });
        return new CoverageReceiptFixture(store, storeRoot, sourceRoot, restoreRoot, sentinelPath, repository, key,
            producer, plan, manifest, writer.WrittenArtifacts.ToArray());
    }

    private sealed record PublishedReceiptFixture(EvidenceReceiptStore Store, string StoreRoot, string SourceRoot,
        string RestoreRoot, string SentinelPath, string Repository, string Key, EvidenceProducerDeclaration Producer,
        string PayloadName);

    private sealed record CoverageReceiptFixture(EvidenceReceiptStore Store, string StoreRoot, string SourceRoot,
        string RestoreRoot, string SentinelPath, string Repository, string Key, EvidenceProducerDeclaration Producer,
        EvidencePlan Plan, EvidenceManifest Manifest, IReadOnlyList<EvidenceArtifactResult> SourceArtifacts);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
