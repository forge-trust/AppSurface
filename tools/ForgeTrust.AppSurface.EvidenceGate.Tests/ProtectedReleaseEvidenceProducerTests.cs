using System.Security.Cryptography;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.EvidenceGate;
using ForgeTrust.AppSurface.Release;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.EvidenceGate.Tests;

public sealed class ProtectedReleaseEvidenceProducerTests
{
    private const string Version = "1.2.3-preview.1";
    private const string Tag = "v1.2.3-preview.1";
    private const string TagObjectId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string PeeledCommit = "cccccccccccccccccccccccccccccccccccccccc";
    private const string ComparisonBaseCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void ProducerRequiresTrustedInvocationProvider()
    {
        using var fixture = new ProducerFixture();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            new ProtectedReleaseEvidenceProducer(fixture.Root, null!));

        Assert.Equal("invocationProvider", exception.ParamName);
    }

    [Fact]
    public async Task RegisteredReleaseProducerFailsClosedWhenTrustedInvocationIsUnavailable()
    {
        using var fixture = new ProducerFixture();
        var registrations = EvidenceHostRunner.CreateFirstPartyRegistrations(fixture.Root);
        var producer = Assert.IsType<ProtectedReleaseEvidenceProducer>(
            registrations.Producers[ProtectedReleaseEvidenceProducer.ProducerId]);
        var declaration = CreateDeclaration();
        var writer = new EvidenceArtifactWriter(declaration, GetWriterRoot(fixture.Root));

        var result = await producer.ProduceAsync(CreateContext(declaration, writer), CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Unavailable, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Null(result.Artifacts);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.Contains("trusted protected-release invocation identity is unavailable", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefaultInspectAuthorityFailsClosedWhenTheTrustedTagIsUnavailable()
    {
        using var fixture = new ProducerFixture();
        var declaration = CreateDeclaration();
        var invocationProvider = new FixedInvocationProvider(CreateInvocation());
        var writer = new EvidenceArtifactWriter(declaration, GetWriterRoot(fixture.Root));
        var producer = new ProtectedReleaseEvidenceProducer(fixture.Root, invocationProvider);

        var result = await producer.ProduceAsync(CreateContext(declaration, writer), CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Invalid, result.Outcome);
        Assert.Equal(1, invocationProvider.Calls);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Null(result.Artifacts);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.Contains("Release inspect authority rejected", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidatedInspectIdentityAndCommittedReleaseDigestsAreWrittenAsTypedEvidenceButRemainUnavailable()
    {
        using var fixture = new ProducerFixture();
        var declaration = CreateDeclaration();
        var invocation = CreateInvocation();
        var inspection = CreateInspection();
        var inspectAuthority = new FakeInspectAuthority(inspection);
        var producer = new ProtectedReleaseEvidenceProducer(
            fixture.Root,
            new FixedInvocationProvider(invocation),
            inspectAuthority);
        var writer = new EvidenceArtifactWriter(declaration, GetWriterRoot(fixture.Root));

        var result = await producer.ProduceAsync(CreateContext(declaration, writer), CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Unavailable, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Equal("main", inspectAuthority.BaseRef);
        Assert.Equal(Version, inspectAuthority.Version);
        Assert.Equal(Tag, inspectAuthority.Tag);
        Assert.Equal(fixture.Root, inspectAuthority.RepositoryRoot);
        Assert.Equal(2, result.Artifacts!.Count);

        var projection = result.Artifacts.Single(artifact => artifact.LogicalName == ProtectedReleaseEvidenceProducer.ReleaseProjectionArtifactName);
        var projectionBytes = await File.ReadAllBytesAsync(TestPathUtils.PathUnder(GetWriterRoot(fixture.Root), projection.RelativePath));
        using var projectionDocument = JsonDocument.Parse(projectionBytes);
        Assert.Equal("appsurface-release-inspect-v1", projectionDocument.RootElement.GetProperty("schema").GetString());
        Assert.Equal(TagObjectId, projectionDocument.RootElement.GetProperty("tagObjectId").GetString());
        Assert.Equal(PeeledCommit, projectionDocument.RootElement.GetProperty("peeledCommit").GetString());
        Assert.Equal(ComparisonBaseCommit, projectionDocument.RootElement.GetProperty("comparisonBaseCommit").GetString());
        Assert.Equal(ComputeSha256(projectionBytes), projection.Sha256);

        var digestIndex = result.Artifacts.Single(artifact => artifact.LogicalName == ProtectedReleaseEvidenceProducer.ReleaseDigestIndexArtifactName);
        var digestIndexBytes = await File.ReadAllBytesAsync(TestPathUtils.PathUnder(GetWriterRoot(fixture.Root), digestIndex.RelativePath));
        using var digestDocument = JsonDocument.Parse(digestIndexBytes);
        Assert.Equal("appsurface-protected-release-digest-index-v1", digestDocument.RootElement.GetProperty("schema").GetString());
        var releaseDigests = digestDocument.RootElement.GetProperty("releaseArtifactDigests").EnumerateArray().ToArray();
        Assert.Equal(5, releaseDigests.Length);
        Assert.Equal(
            inspection.ReleaseArtifactDigests.Select(static artifact => artifact.Path).OrderBy(static path => path, StringComparer.Ordinal),
            releaseDigests.Select(static artifact => artifact.GetProperty("path").GetString()).OrderBy(static path => path, StringComparer.Ordinal));
        Assert.Equal(ComputeSha256(digestIndexBytes), digestIndex.Sha256);
        Assert.Contains("package/archive", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProducerRejectsMissingArtifactWriterAfterInspectionInsteadOfReturningUnwrittenEvidence()
    {
        using var fixture = new ProducerFixture();
        var declaration = CreateDeclaration();
        var inspectAuthority = new FakeInspectAuthority(CreateInspection());
        var producer = new ProtectedReleaseEvidenceProducer(
            fixture.Root,
            new FixedInvocationProvider(CreateInvocation()),
            inspectAuthority);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await producer.ProduceAsync(CreateContext(declaration, writer: null), CancellationToken.None));

        Assert.Equal(1, inspectAuthority.Calls);
    }

    [Fact]
    public async Task EvidenceHostCannotIssueReleaseCompleteFromLocalInspectEvenWithAcceptedEnvelope()
    {
        using var fixture = new ProducerFixture();
        var declaration = CreateDeclaration();
        var context = CreateContext(declaration, new EvidenceArtifactWriter(declaration, GetWriterRoot(fixture.Root)));
        var producer = new ProtectedReleaseEvidenceProducer(
            fixture.Root,
            new FixedInvocationProvider(CreateInvocation()),
            new FakeInspectAuthority(CreateInspection()));
        await using var host = EvidenceHostBootstrap.Create(
            context.Plan,
            registration =>
            {
                registration.AddProducer(producer);
                registration.SetEnvelopeVerifier(new AcceptedEnvelopeVerifier());
            },
            new EvidenceHostOptions(ArtifactDirectory: TestPathUtils.PathUnder(fixture.Root, "host-artifacts")));

        var manifest = await host.RunAsync();

        Assert.Equal(EvidenceExecutionVerdict.Incomplete, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.None, manifest.Eligibility);
        Assert.Equal(EvidenceEnvelopeStatus.ValidatedNotAttested, manifest.EnvelopeStatus);
        Assert.Empty(manifest.ClosedObligationIds);
        Assert.Equal(["protected-release-subject"], manifest.UnmediatedObligationIds);
        var producerResult = Assert.Single(manifest.ProducerResults);
        Assert.Equal(EvidenceProducerOutcome.Unavailable, producerResult.Outcome);
        Assert.Equal(2, producerResult.Artifacts!.Count);
        Assert.True(EvidenceManifestBuilder.Verify(context.Plan, manifest));
    }

    [Theory]
    [InlineData("tag-object")]
    [InlineData("peeled-commit")]
    public async Task InspectIdentityMismatchIsInvalidAndWritesNoTypedArtifacts(string mismatch)
    {
        using var fixture = new ProducerFixture();
        var declaration = CreateDeclaration();
        var inspection = CreateInspection() with
        {
            TagObjectId = mismatch == "tag-object" ? new string('d', 40) : TagObjectId,
            PeeledCommit = mismatch == "peeled-commit" ? new string('e', 40) : PeeledCommit,
        };
        var writer = new EvidenceArtifactWriter(declaration, GetWriterRoot(fixture.Root));
        var producer = new ProtectedReleaseEvidenceProducer(
            fixture.Root,
            new FixedInvocationProvider(CreateInvocation()),
            new FakeInspectAuthority(inspection));

        var result = await producer.ProduceAsync(CreateContext(declaration, writer), CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Invalid, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Null(result.Artifacts);
        Assert.Empty(writer.WrittenArtifacts);
    }

    [Fact]
    public async Task UnsupportedDeclarationIsRejectedBeforeInvocationOrInspection()
    {
        using var fixture = new ProducerFixture();
        var declaration = CreateDeclaration() with { Kind = "other" };
        var invocationProvider = new FixedInvocationProvider(CreateInvocation());
        var inspectAuthority = new FakeInspectAuthority(CreateInspection());
        var writer = new EvidenceArtifactWriter(declaration, GetWriterRoot(fixture.Root));
        var producer = new ProtectedReleaseEvidenceProducer(fixture.Root, invocationProvider, inspectAuthority);

        var result = await producer.ProduceAsync(CreateContext(declaration, writer), CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Invalid, result.Outcome);
        Assert.Equal(0, invocationProvider.Calls);
        Assert.Equal(0, inspectAuthority.Calls);
        Assert.Empty(writer.WrittenArtifacts);
    }

    [Fact]
    public async Task OversizedInspectionProjectionIsRejectedBeforeAnyArtifactIsWritten()
    {
        using var fixture = new ProducerFixture();
        var declaration = CreateDeclaration();
        var inspection = CreateInspection() with
        {
            ReleaseArtifactDigests = [new ReleaseInspectArtifactDigest(new string('p', 20 * 1024), new string('a', 64))],
        };
        var writer = new EvidenceArtifactWriter(declaration, GetWriterRoot(fixture.Root));
        var producer = new ProtectedReleaseEvidenceProducer(
            fixture.Root,
            new FixedInvocationProvider(CreateInvocation()),
            new FakeInspectAuthority(inspection));

        var result = await producer.ProduceAsync(CreateContext(declaration, writer), CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Invalid, result.Outcome);
        Assert.Contains("artifact bound", result.Diagnostic, StringComparison.Ordinal);
        Assert.Empty(writer.WrittenArtifacts);
    }

    [Theory]
    [InlineData("release-rejected")]
    [InlineData("unreadable-inspection")]
    public async Task InspectFailureIsInvalidAndCannotWriteReleaseArtifacts(string failure)
    {
        using var fixture = new ProducerFixture();
        var declaration = CreateDeclaration();
        var writer = new EvidenceArtifactWriter(declaration, GetWriterRoot(fixture.Root));
        Exception failureException = failure == "release-rejected"
            ? new ReleaseToolException(ReleaseDiagnostic.Error("release-invalid", "Tag inspection failed.", "Invalid tag.", "Correct the tag.", "releases/README.md"))
            : new IOException("The inspection stream is unavailable.");
        var producer = new ProtectedReleaseEvidenceProducer(
            fixture.Root,
            new FixedInvocationProvider(CreateInvocation()),
            new ThrowingInspectAuthority(failureException));

        var result = await producer.ProduceAsync(CreateContext(declaration, writer), CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Invalid, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.Contains(
            failure == "release-rejected" ? "Release inspect authority rejected" : "could not be captured",
            result.Diagnostic,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationPropagatesWithoutAReleaseClaim()
    {
        using var fixture = new ProducerFixture();
        var declaration = CreateDeclaration();
        var writer = new EvidenceArtifactWriter(declaration, GetWriterRoot(fixture.Root));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var producer = new ProtectedReleaseEvidenceProducer(
            fixture.Root,
            new FixedInvocationProvider(CreateInvocation()),
            new FakeInspectAuthority(CreateInspection()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await producer.ProduceAsync(CreateContext(declaration, writer), cancellation.Token));

        Assert.Empty(writer.WrittenArtifacts);
    }

    private static EvidenceProducerDeclaration CreateDeclaration() => new(
        ProtectedReleaseEvidenceProducer.ProducerId,
        "release-inspection",
        "1.0.0",
        [],
        [ProtectedReleaseEvidenceProducer.ProtectedReleaseAssertionId],
        [
            new EvidenceArtifactSlot("release-projection", "release/projection", "application/json", true, 2 * 1024 * 1024),
            new EvidenceArtifactSlot("release-digest-index", "release/digests", "application/json", true, 2 * 1024 * 1024),
        ],
        600);

    private static ProtectedReleaseInvocation CreateInvocation() => new(
        Version,
        Tag,
        "main",
        TagObjectId,
        PeeledCommit);

    private static ReleaseInspectMachineResult CreateInspection() => new(
        ReleaseInspectMachineResult.CurrentSchema,
        Version,
        Tag,
        "main",
        TagObjectId,
        PeeledCommit,
        ComparisonBaseCommit,
        new string('f', 64),
        new string('1', 64),
        [
            new ReleaseInspectArtifactDigest($"releases/v{Version}.md", new string('2', 64)),
            new ReleaseInspectArtifactDigest($"releases/v{Version}.md.yml", new string('3', 64)),
            new ReleaseInspectArtifactDigest($"releases/v{Version}.release.json", new string('4', 64)),
            new ReleaseInspectArtifactDigest("releases/current.md", new string('5', 64)),
            new ReleaseInspectArtifactDigest("releases/current.md.yml", new string('6', 64)),
        ]);

    private static EvidenceProducerContext CreateContext(EvidenceProducerDeclaration declaration, EvidenceArtifactWriter? writer)
    {
        var obligation = new EvidenceObligation(
            "protected-release-subject",
            "protected-release",
            "Bind protected release identity and artifacts.",
            [declaration.Id],
            ProtectedReleaseEvidenceProducer.ProtectedReleaseAssertionId);
        var profile = new EvidenceProfile("protected-release", EvidenceProfileScope.Release, [], [declaration], [obligation]);
        var policy = new EvidencePolicy("release-producer-tests", "1", profile.Id, [profile], []);
        var changedPaths = Array.Empty<NormalizedDiffPath>();
        var draft = new EvidencePlan(
            "1.0",
            policy.Id,
            EvidenceDigest.CanonicalSha256(policy),
            EvidenceDigest.CanonicalSha256(changedPaths),
            profile,
            changedPaths,
            [],
            string.Empty,
            policy);
        var plan = draft with { PlanDigest = EvidenceDigest.CanonicalSha256(draft) };
        return new EvidenceProducerContext(plan, declaration, TimeProvider.System, writer);
    }

    private static string GetWriterRoot(string root) =>
        TestPathUtils.PathUnder(root, "artifacts", ProtectedReleaseEvidenceProducer.ProducerId);

    private static string ComputeSha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class FixedInvocationProvider(ProtectedReleaseInvocation? invocation) : IProtectedReleaseInvocationProvider
    {
        public int Calls { get; private set; }

        public ValueTask<ProtectedReleaseInvocation?> ReadAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(invocation);
        }
    }

    private sealed class FakeInspectAuthority(ReleaseInspectMachineResult result) : IReleaseInspectMachineAuthority
    {
        public int Calls { get; private set; }

        public string? RepositoryRoot { get; private set; }

        public string? Version { get; private set; }

        public string? Tag { get; private set; }

        public string? BaseRef { get; private set; }

        public Task<ReleaseInspectMachineResult> InspectAsync(
            string repositoryRoot,
            string version,
            string tag,
            string baseRef,
            CancellationToken cancellationToken)
        {
            Calls++;
            RepositoryRoot = repositoryRoot;
            Version = version;
            Tag = tag;
            BaseRef = baseRef;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingInspectAuthority(Exception exception) : IReleaseInspectMachineAuthority
    {
        public Task<ReleaseInspectMachineResult> InspectAsync(
            string repositoryRoot,
            string version,
            string tag,
            string baseRef,
            CancellationToken cancellationToken) => Task.FromException<ReleaseInspectMachineResult>(exception);
    }

    private sealed class AcceptedEnvelopeVerifier : IEvidenceExecutionEnvelopeVerifier
    {
        public ValueTask<EvidenceEnvelopeResult> VerifyAsync(EvidencePlan plan, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new EvidenceEnvelopeResult(Accepted: true, Attested: false));
    }

    private sealed class ProducerFixture : IDisposable
    {
        public ProducerFixture()
        {
            Root = TestPathUtils.PathUnder(Path.GetTempPath(), "appsurface-protected-release-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
