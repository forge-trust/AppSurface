using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceReceiptAuthorityGuardCoverageTests
{
    [Theory]
    [InlineData("ascii")]
    [InlineData("two-byte")]
    [InlineData("surrogate-pair")]
    public async Task AssertionByteLimitIsEnforcedBeforeAuthorityMutationAndAcceptsTheExactBoundary(string shape)
    {
        using var fixture = await Fixture.CreateAsync();
        var exact = shape switch
        {
            "ascii" => new string('a', 4096),
            "two-byte" => new string('é', 2048),
            _ => string.Concat(Enumerable.Repeat("😀", 1024))
        };
        Assert.Equal(4096, Encoding.UTF8.GetByteCount(exact));
        var original = fixture.IndexBytes();

        Assert.Throws<InvalidDataException>(() => fixture.Lease.EstablishAuthority("audit", auditIntent: true,
            expectedAssertions: exact + "x"));
        Assert.Null(fixture.Lease.ReadState());
        Assert.Equal(original, fixture.IndexBytes());

        fixture.Lease.EstablishAuthority("audit", auditIntent: true, expectedAssertions: exact);
        fixture.Lease.SettleExecution("audit");
        var beforeMismatch = fixture.Lease.ReadState();
        var beforeIndex = fixture.IndexBytes();
        Assert.Throws<InvalidDataException>(() => fixture.Lease.MarkAuditMismatch("audit", exact + "x"));
        Assert.Equal(beforeMismatch, fixture.Lease.ReadState());
        Assert.Equal(beforeIndex, fixture.IndexBytes());

        fixture.Lease.MarkAuditMismatch("audit", exact);
        var state = fixture.Lease.ReadState()!;
        Assert.True(state.AuditMismatch);
        Assert.Equal("audit", state.AuditIntentExecutionId);
        Assert.Equal(exact, state.AuditExpectedAssertions);
        Assert.Null(state.InFlightExecutionId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingExecutionIdentifierCannotCreateOrQuarantineAuthority(string? execution)
    {
        using var fixture = await Fixture.CreateAsync();
        var original = fixture.IndexBytes();

        Assert.ThrowsAny<ArgumentException>(() => fixture.Lease.EstablishAuthority(execution!));
        Assert.ThrowsAny<ArgumentException>(() => fixture.Lease.MarkAuditMismatch(execution!, "[]"));

        Assert.Null(fixture.Lease.ReadState());
        Assert.Equal(original, fixture.IndexBytes());
    }

    [Fact]
    public async Task EstablishAuthorityCannotReplaceAnUnresolvedExecutionOwner()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Lease.EstablishAuthority("first-execution");
        var originalState = fixture.Lease.ReadState();
        var originalIndex = fixture.IndexBytes();

        Assert.Throws<InvalidOperationException>(() => fixture.Lease.EstablishAuthority("second-execution"));

        Assert.Equal(originalState, fixture.Lease.ReadState());
        Assert.Equal(originalIndex, fixture.IndexBytes());
        Assert.Equal("first-execution", fixture.Lease.ReadState()?.InFlightExecutionId);
    }

    [Fact]
    public async Task SettleExecutionCannotClearAnotherExecutionsAuthority()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Lease.EstablishAuthority("current-execution");
        var originalState = fixture.Lease.ReadState();
        var originalIndex = fixture.IndexBytes();

        Assert.Throws<InvalidOperationException>(() => fixture.Lease.SettleExecution("different-execution"));

        Assert.Equal(originalState, fixture.Lease.ReadState());
        Assert.Equal(originalIndex, fixture.IndexBytes());
        Assert.Equal("current-execution", fixture.Lease.ReadState()?.InFlightExecutionId);
    }

    [Theory]
    [InlineData("digest-null", typeof(ArgumentException))]
    [InlineData("digest-short", typeof(ArgumentException))]
    [InlineData("digest-nonhex", typeof(ArgumentException))]
    [InlineData("future-origin", typeof(ArgumentOutOfRangeException))]
    [InlineData("payload-escape", typeof(InvalidDataException))]
    [InlineData("record-null", typeof(ArgumentNullException))]
    [InlineData("record-oversized", typeof(InvalidDataException))]
    public async Task InvalidPassedCommitCannotReplaceOrSettleTheCurrentExecution(string defect, Type expectedException)
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Lease.EstablishAuthority("execution");
        var state = fixture.Lease.ReadState();
        var original = fixture.IndexBytes();
        var hash = defect switch { "digest-null" => null!, "digest-short" => "abcd", "digest-nonhex" => new string('g', 64), _ => new string('b', 64) };
        var completed = fixture.Clock.GetUtcNow() + (defect == "future-origin" ? TimeSpan.FromTicks(1) : TimeSpan.Zero);
        var payload = defect == "payload-escape" ? "../outside" : "payload-guard";
        var record = defect switch
        {
            "record-null" => null!,
            "record-oversized" => new byte[32 * 1024 + 1],
            _ => EvidenceCanonicalJson.Serialize(new EvidenceProducerResult("producer", EvidenceProducerOutcome.Passed, []))
        };

        var failure = Record.Exception(() => fixture.Lease.CommitPassed("execution", hash, completed, payload, record));

        Assert.IsType(expectedException, failure);
        Assert.Equal(state, fixture.Lease.ReadState());
        Assert.Equal(original, fixture.IndexBytes());
        Assert.Equal("execution", fixture.Lease.ReadState()!.InFlightExecutionId);
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(fixture.Lease.DirectoryPath, "payload-guard")));
        if (defect == "payload-escape")
        {
            var escaped = TestPathUtils.PathUnder(Path.GetDirectoryName(fixture.Lease.DirectoryPath)!, "outside");
            Assert.False(File.Exists(escaped));
            Assert.False(Directory.Exists(escaped));
        }
    }

    [Fact]
    public async Task QuarantinedFreshCommitCannotClearAnotherExecutionsAuditIntent()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Lease.EstablishAuthority("audit", auditIntent: true, expectedAssertions: "[\"expected\"]");
        fixture.Lease.MarkAuditMismatch("audit", "[\"expected\"]");
        fixture.Lease.SettleExecution("audit");
        fixture.Lease.EstablishAuthority("fresh", allowQuarantinedFresh: true);
        var state = fixture.Lease.ReadState();
        var original = fixture.IndexBytes();

        Assert.Throws<InvalidOperationException>(() => fixture.Lease.CommitPassed("fresh", new string('b', 64),
            fixture.Clock.GetUtcNow(), "payload-guard", EvidenceCanonicalJson.Serialize(
                new EvidenceProducerResult("producer", EvidenceProducerOutcome.Passed, []))));

        Assert.Equal(state, fixture.Lease.ReadState());
        Assert.Equal(original, fixture.IndexBytes());
        Assert.Equal("audit", fixture.Lease.ReadState()!.AuditIntentExecutionId);
        Assert.Equal("fresh", fixture.Lease.ReadState()!.InFlightExecutionId);
        Assert.True(fixture.Lease.ReadState()!.AuditMismatch);
    }

    [Theory]
    [InlineData("payload-dir/child")]
    [InlineData("")]
    [InlineData("payload-stage-owned")]
    [InlineData("payload-STAGE-owned")]
    public async Task InvalidationRefusesMalformedActivePayloadComponentWithoutChangingCustody(string malformedPayloadName)
    {
        using var fixture = await Fixture.CreateAsync();
        var completedAt = fixture.Clock.GetUtcNow();
        var outsideSentinel = TestPathUtils.PathUnder(fixture.DirectoryPath, "operator-sentinel.txt");
        var sentinelBytes = Encoding.UTF8.GetBytes("outside receipt custody");
        await File.WriteAllBytesAsync(outsideSentinel, sentinelBytes);
        var settled = new EvidenceReceiptState(1, fixture.Lease.Key, null, null, new string('b', 64), completedAt,
            malformedPayloadName, null, completedAt, false, null);
        var original = fixture.IndexBytes();
        Assert.Throws<InvalidDataException>(() => EvidenceReceiptQuota.RecordState(
            fixture.Root, fixture.Repository, fixture.Lease.Key,
            JsonSerializer.SerializeToUtf8Bytes(settled).Length, settled));
        Assert.Equal(original, fixture.IndexBytes());
        Assert.Null(EvidenceReceiptQuota.ReadAuthority(fixture.Root, fixture.Repository, fixture.Lease.Key, readOnly: true));
        var corrupt = JsonNode.Parse(original)!;
        var entry = corrupt["Entries"]!.AsArray().Single()!;
        entry["PayloadDirectoryName"] = malformedPayloadName;
        entry["AuthorityState"] = JsonSerializer.SerializeToNode(settled);
        await File.WriteAllBytesAsync(TestPathUtils.PathUnder(fixture.Root, "quota-index.json"),
            Encoding.UTF8.GetBytes(corrupt.ToJsonString()));
        var before = fixture.IndexBytes();
        var invalidated = new EvidenceReceiptState(1, fixture.Lease.Key, null, null, null, null, null);

        try
        {
            var failure = Assert.Throws<InvalidDataException>(() =>
                EvidenceReceiptQuota.InvalidatePayload(fixture.Lease, settled, invalidated));
            Assert.Equal("Receipt quota index contains unsupported or invalid authority.", failure.Message);
            Assert.Throws<InvalidDataException>(() => EvidenceReceiptQuota.ReadAuthority(
                fixture.Root, fixture.Repository, fixture.Lease.Key, readOnly: true));
            Assert.Equal(before, fixture.IndexBytes());
            Assert.Equal(sentinelBytes, await File.ReadAllBytesAsync(outsideSentinel));
        }
        finally
        {
            await File.WriteAllBytesAsync(TestPathUtils.PathUnder(fixture.Root, "quota-index.json"), original);
        }
    }

    [Theory]
    [InlineData("in-flight")]
    [InlineData("audit-intent")]
    [InlineData("quarantine")]
    public async Task MissingAuthorityCannotEraseRetainedExecutionOrAuditFlags(string mode)
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Lease.EstablishAuthority("old", auditIntent: mode != "in-flight");
        if (mode == "audit-intent") fixture.Lease.SettleExecution("old");
        if (mode == "quarantine") fixture.Lease.MarkAuditMismatch("old", "[]");
        var original = fixture.IndexBytes();
        var index = JsonNode.Parse(original)!;
        index["Entries"]!.AsArray().Single()!["AuthorityState"] = null;
        var indexPath = TestPathUtils.PathUnder(fixture.Root, "quota-index.json");
        await File.WriteAllBytesAsync(indexPath, Encoding.UTF8.GetBytes(index.ToJsonString()));
        var before = fixture.IndexBytes();
        try
        {
            Assert.Throws<InvalidDataException>(() => EvidenceReceiptQuota.ReadAuthority(
                fixture.Root, fixture.Repository, fixture.Lease.Key, readOnly: true));
            Assert.Throws<InvalidDataException>(() => EvidenceReceiptQuota.ReadAuthority(
                fixture.Root, fixture.Repository, fixture.Lease.Key));
            Assert.Throws<InvalidDataException>(() => fixture.Lease.EstablishAuthority("new"));
            Assert.Equal(before, fixture.IndexBytes());
        }
        finally { await File.WriteAllBytesAsync(indexPath, original); }
    }


    [Fact]
    public async Task DisposedKeyLeaseCannotReadOrRewriteRetainedExecutionAuthority()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Lease.EstablishAuthority("execution");
        fixture.Lease.Dispose();
        var afterDisposal = fixture.IndexBytes();

        Assert.Throws<ObjectDisposedException>(() => fixture.Lease.ReadState());
        Assert.Throws<ObjectDisposedException>(() => fixture.Lease.VerifyBinding());
        Assert.Throws<ObjectDisposedException>(() => fixture.Lease.EstablishAuthority("another"));
        Assert.Throws<ObjectDisposedException>(() => fixture.Lease.MarkAuditMismatch("execution", "[]"));
        fixture.Lease.Dispose();

        Assert.Equal(afterDisposal, fixture.IndexBytes());
        var state = EvidenceReceiptQuota.ReadAuthority(fixture.Root, new string('a', 64), new string('c', 64));
        Assert.Equal("execution", state!.InFlightExecutionId);
    }

    [Fact]
    public async Task RetiredPayloadChargeRequiresConfirmedDeletionAndExactName()
    {
        using var fixture = await Fixture.CreateAsync();
        var repository = fixture.Repository;
        var key = fixture.Lease.Key;
        var payloadName = "payload-authority-guard-retired";
        var completedAt = fixture.Clock.GetUtcNow();
        var sentinelPath = TestPathUtils.PathUnder(fixture.DirectoryPath, "operator-sentinel.txt");
        await File.WriteAllTextAsync(sentinelPath, "preserve outside receipt custody");
        Assert.True(EvidenceReceiptQuota.TryReservePayload(fixture.Root, repository, key, 3, completedAt));
        using (var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(fixture.Lease.DirectoryPath,
            createMissing: false, requirePrivate: true)!)
        using (var payload = keyRoot.CreateArtifactDirectory(payloadName))
        using (var file = payload.OpenArtifactFile("owned.bin", createNew: true))
        {
            file.Write(new byte[] { 2, 4, 6 });
            file.Flush(flushToDisk: true);
        }
        EvidenceReceiptQuota.CommitPayload(fixture.Root, repository, key, 3, payloadName, completedAt,
            JsonSerializer.SerializeToUtf8Bytes(new EvidenceReceiptState(1, key, null, null,
                new string('b', 64), completedAt, payloadName)).Length);
        var settled = new EvidenceReceiptState(1, key, null, null, new string('b', 64), completedAt,
            payloadName, null, completedAt, false, null);
        EvidenceReceiptQuota.RecordState(fixture.Root, repository, key,
            JsonSerializer.SerializeToUtf8Bytes(settled).Length, settled);
        var invalidated = new EvidenceReceiptState(1, key, null, null, null, null, null);
        EvidenceReceiptQuota.InvalidatePayload(fixture.Lease, settled, invalidated);

        Assert.Equal(new[] { payloadName }, EvidenceReceiptQuota.ReadRetiredPayloadNames(fixture.Root, repository, key));
        AssertRetiredPayloadCharge(fixture.IndexBytes(), payloadName, 3);
        var beforeUnconfirmedRelease = fixture.IndexBytes();
        EvidenceReceiptQuota.ReleaseRetiredPayload(fixture.Root, repository, key, payloadName, deletionConfirmed: false);
        Assert.Equal(beforeUnconfirmedRelease, fixture.IndexBytes());
        EvidenceReceiptQuota.ReleaseRetiredPayload(fixture.Root, repository, key, "payload-unrelated",
            deletionConfirmed: true);
        Assert.Equal(beforeUnconfirmedRelease, fixture.IndexBytes());

        using (var keyRoot = CoverageRunOutputLease.AcquireArtifactDirectory(fixture.Lease.DirectoryPath,
            createMissing: false, requirePrivate: true)!)
            keyRoot.DeleteArtifactEntry(payloadName, 16);
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(fixture.Lease.DirectoryPath, payloadName)));
        AssertRetiredPayloadCharge(fixture.IndexBytes(), payloadName, 3);
        EvidenceReceiptQuota.ReleaseRetiredPayload(fixture.Root, repository, key, payloadName, deletionConfirmed: true);

        Assert.Empty(EvidenceReceiptQuota.ReadRetiredPayloadNames(fixture.Root, repository, key));
        using var index = JsonDocument.Parse(fixture.IndexBytes());
        var entry = Assert.Single(index.RootElement.GetProperty("Entries").EnumerateArray());
        Assert.Empty(entry.GetProperty("RetiredPayloads").EnumerateArray());
        Assert.Equal("preserve outside receipt custody", await File.ReadAllTextAsync(sentinelPath));
    }

    private static void AssertRetiredPayloadCharge(byte[] indexBytes, string payloadName, long expectedBytes)
    {
        using var index = JsonDocument.Parse(indexBytes);
        var entry = Assert.Single(index.RootElement.GetProperty("Entries").EnumerateArray());
        var retired = Assert.Single(entry.GetProperty("RetiredPayloads").EnumerateArray());
        Assert.Equal(payloadName, retired.GetProperty("DirectoryName").GetString());
        Assert.Equal(expectedBytes, retired.GetProperty("Bytes").GetInt64());
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class Fixture(TestDirectory directory, string root, TimeProvider clock,
        EvidenceReceiptStore.EvidenceReceiptKeyLease lease) : IDisposable
    {
        internal string Root => root;
        internal string Repository => new string('a', 64);
        internal string DirectoryPath => directory.Path;
        internal TimeProvider Clock => clock;
        internal EvidenceReceiptStore.EvidenceReceiptKeyLease Lease => lease;
        internal byte[] IndexBytes() => File.ReadAllBytes(TestPathUtils.PathUnder(root, "quota-index.json"));
        internal static async Task<Fixture> CreateAsync()
        {
            var directory = TestDirectory.Create();
            IReadOnlyList<EvidenceReceiptStore.EvidenceReceiptKeyLease>? leases = null;
            try
            {
                var root = TestPathUtils.PathUnder(directory.Path, "receipts");
                var clock = new FixedClock();
                var store = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
                { RootDirectory = root, RepositoryNamespace = new string('a', 64), Clock = clock });
                leases = await store.AcquireAsync([new string('c', 64)], CancellationToken.None);
                var lease = Assert.Single(leases);
                return new(directory, root, clock, lease);
            }
            catch
            {
                try { if (leases is not null) foreach (var acquired in leases) acquired.Dispose(); }
                finally { directory.Dispose(); }
                throw;
            }
        }
        public void Dispose()
        {
            try { lease.Dispose(); }
            finally { directory.Dispose(); }
        }
    }
}
