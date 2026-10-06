using System.Buffers;
using System.Text;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Planner and copied-data controls only; these fixtures cannot issue admission or native custody.</summary>
public sealed class EvidenceEmptyObservationFilesTests
{
    [Fact]
    public void GenuinePlannerPlanAndCanonicalEmptyObservationFilesReturnDetachedData()
    {
        var plan = Plan();
        var manifest = Manifest(plan);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
        var planBytes = EvidenceCanonicalJson.Serialize(plan);
        var manifestBytes = EvidenceCanonicalJson.Serialize(manifest);
        var summaryBytes = Summary(manifest);
        var verified = EvidenceEmptyObservationFiles.Verify(plan, planBytes, manifestBytes, summaryBytes);
        Assert.NotSame(manifest, verified);
        Assert.Equal(manifest.ManifestDigest, verified.ManifestDigest);
        Assert.Equal(EvidenceClaimEligibility.Informational, verified.Eligibility);
        Array.Fill(planBytes, (byte)'x');
        Array.Fill(manifestBytes, (byte)'x');
        Array.Fill(summaryBytes, (byte)'x');
        Assert.Equal(manifest.ManifestDigest, verified.ManifestDigest);
        Assert.Empty(verified.ProducerResults);
        Assert.Null(verified.EnvelopeAssertion);
    }

    [Fact]
    public void CoherentPlanningAndCleanupTimingsHaveAValidPositiveNeighbor()
    {
        var plan = Plan();
        var manifest = Rehash(Manifest(plan) with
        {
            Metrics = new(PlanningMilliseconds: 2, CleanupMilliseconds: 3, TotalMilliseconds: 5),
        });
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.Equal(5L, Verify(plan, manifest).Metrics.TotalMilliseconds);
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("legacy")]
    [InlineData("verdict")]
    [InlineData("claim")]
    [InlineData("eligibility")]
    [InlineData("envelope")]
    [InlineData("assertion")]
    public void RehashedManifestCannotChangeTheEmptyObservationDisposition(string field)
    {
        var plan = Plan();
        var manifest = Manifest(plan);
        manifest = field switch
        {
            "mode" => manifest with { Mode = EvidenceExecutionMode.Trusted },
            "legacy" => manifest with { Mode = null },
            "verdict" => manifest with { ExecutionVerdict = EvidenceExecutionVerdict.Incomplete },
            "claim" => manifest with { ClaimKind = EvidenceClaimKind.NoEvidenceRequired },
            "eligibility" => manifest with { Eligibility = EvidenceClaimEligibility.PullRequestGate },
            "envelope" => manifest with { EnvelopeStatus = EvidenceEnvelopeStatus.ValidatedNotAttested },
            _ => manifest with
            {
                EnvelopeAssertion = new("data", "data", "data", "data", "data", "data", "data", "data",
                    "data", "data", "data", "data", "data", "data", "data", "data", DateTimeOffset.UnixEpoch),
            },
        };
        Reject(() => Verify(plan, Rehash(manifest)));
    }

    [Theory]
    [InlineData("readiness")]
    [InlineData("producer")]
    [InlineData("selected")]
    [InlineData("closed")]
    [InlineData("unmediated")]
    public void AnyResultOrObligationOutsideTheEmptyPlanIsRejected(string field)
    {
        var plan = Plan();
        var manifest = Manifest(plan);
        manifest = field switch
        {
            "readiness" => manifest with { ResourceResults = [new("unexpected", EvidenceResourceOutcome.Ready, 0)] },
            "producer" => manifest with { ProducerResults = [new("unexpected", EvidenceProducerOutcome.Passed, [])] },
            "selected" => manifest with { SelectedObligationIds = ["unexpected"] },
            "closed" => manifest with { ClosedObligationIds = ["unexpected"] },
            _ => manifest with { UnmediatedObligationIds = ["unexpected"] },
        };
        Reject(() => Verify(plan, Rehash(manifest)));
    }

    [Theory]
    [InlineData("cleanup")]
    [InlineData("diagnostic")]
    [InlineData("terminal")]
    [InlineData("planning-negative")]
    [InlineData("cleanup-negative")]
    [InlineData("total-negative")]
    [InlineData("resource-time")]
    [InlineData("producer-time")]
    [InlineData("sum-time")]
    public void CleanupFailureOrIncoherentEmptyStageMetricsCannotBeRehashedIntoSuccess(string field)
    {
        var plan = Plan();
        EvidenceExecutionMetrics metrics = field switch
        {
            "cleanup" => new EvidenceExecutionMetrics(CleanupCompleted: false),
            "diagnostic" => new(CleanupDiagnostic: "CANARY"),
            "terminal" => new(TerminalFailureCode: "CANARY"),
            "planning-negative" => new(PlanningMilliseconds: -1),
            "cleanup-negative" => new(CleanupMilliseconds: -1),
            "total-negative" => new(TotalMilliseconds: -1),
            "resource-time" => new(ResourceReadinessMilliseconds: 1, TotalMilliseconds: 1),
            "producer-time" => new(ProducerMilliseconds: 1, TotalMilliseconds: 1),
            _ => new(PlanningMilliseconds: 3, CleanupMilliseconds: 3, TotalMilliseconds: 5),
        };
        Reject(() => Verify(plan, Rehash(Manifest(plan) with { Metrics = metrics })));
    }

    [Fact]
    public void WholePlanComparisonBindsPathsPolicyRulesAndDigestsRatherThanOnlySelectedProfile()
    {
        var expected = Plan();
        var different = Plan("docs/B.md");
        Assert.Equal(expected.Profile.Id, different.Profile.Id);
        Reject(() => EvidenceEmptyObservationFiles.Verify(expected, EvidenceCanonicalJson.Serialize(different),
            EvidenceCanonicalJson.Serialize(Manifest(different)), Summary(Manifest(different))));
        var policy = expected.PolicySnapshot! with { Version = "2" };
        var changedPolicy = new EvidencePlanner().Resolve(policy, expected.ChangedPaths);
        Reject(() => EvidenceEmptyObservationFiles.Verify(expected, EvidenceCanonicalJson.Serialize(changedPolicy),
            EvidenceCanonicalJson.Serialize(Manifest(changedPolicy)), Summary(Manifest(changedPolicy))));
        var edited = Rehash(expected with { MatchedRuleIds = ["CANARY"] });
        Reject(() => Verify(edited, Manifest(edited)));
        var wrongDigest = expected with { PlanDigest = new string('0', 64) };
        Reject(() => Verify(wrongDigest, Manifest(wrongDigest)));
    }

    [Fact]
    public void ExpectedPlanMustHaveActualPolicyNonemptyNormalizedPathsAndAnEmptyTargetedProfile()
    {
        var plan = Plan();
        Reject(() => Verify(plan with { PolicySnapshot = null }, Manifest(plan)));
        Reject(() => Verify(Rehash(plan with { ChangedPaths = [] }), Manifest(plan)));
        Reject(() => Verify(Rehash(plan with { ChangedPaths = [new("docs/../A.md")] }), Manifest(plan)));
        Reject(() => Verify(Rehash(plan with { Profile = plan.Profile with { Scope = EvidenceProfileScope.Release } }),
            Manifest(plan)));
        Reject(() => Verify(Rehash(plan with { Profile = plan.Profile with { Resources = [new("x", "completion", 1, [])] } }),
            Manifest(plan)));
        Reject(() => EvidenceEmptyObservationFiles.Verify(null!, EvidenceCanonicalJson.Serialize(plan),
            EvidenceCanonicalJson.Serialize(Manifest(plan)), Summary(Manifest(plan))));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("case")]
    [InlineData("duplicate")]
    [InlineData("type")]
    [InlineData("mode")]
    [InlineData("procedure")]
    [InlineData("attestation")]
    public void SummaryIsExactlySevenCanonicalFieldsWithNoAliasesOrAuthority(string variant)
    {
        var plan = Plan();
        var manifest = Manifest(plan);
        var summary = JsonNode.Parse(Summary(manifest))!.AsObject();
        switch (variant)
        {
            case "missing": summary.Remove("Mode"); break;
            case "extra": summary["CANARY"] = true; break;
            case "case": summary.Remove("Mode"); summary["mode"] = "Observation"; break;
            case "type": summary["SandboxAttestation"] = "false"; break;
            case "mode": summary["Mode"] = "Trusted"; break;
            case "procedure": summary["Procedure"] = "CANARY"; break;
            case "attestation": summary["SandboxAttestation"] = true; break;
        }
        var bytes = variant == "duplicate"
            ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Summary(manifest)).Replace(
                "\"Mode\":\"Observation\"", "\"Mode\":\"Observation\",\"mode\":\"CANARY\"", StringComparison.Ordinal))
            : EvidenceCanonicalJson.Serialize(summary);
        Reject(() => Verify(plan, manifest, bytes));
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("manifest")]
    [InlineData("nested")]
    public void AdditiveReaderCompatibilityDoesNotAllowCanaryPropertiesInCanonicalFiles(string field)
    {
        var plan = Plan();
        var manifest = Manifest(plan);
        var planJson = JsonNode.Parse(EvidenceCanonicalJson.Serialize(plan))!.AsObject();
        var manifestJson = JsonNode.Parse(EvidenceCanonicalJson.Serialize(manifest))!.AsObject();
        if (field == "plan") planJson["CANARY"] = true;
        else if (field == "manifest") manifestJson["CANARY"] = true;
        else manifestJson["Metrics"]!.AsObject()["CANARY"] = true;
        Reject(() => EvidenceEmptyObservationFiles.Verify(plan, EvidenceCanonicalJson.Serialize(planJson),
            EvidenceCanonicalJson.Serialize(manifestJson), Summary(manifest)));
    }

    [Fact]
    public void MalformedDuplicateAliasNoncanonicalAndTamperedFilesHaveFixedFailures()
    {
        var plan = Plan();
        var manifest = Manifest(plan);
        var planBytes = EvidenceCanonicalJson.Serialize(plan);
        var manifestBytes = EvidenceCanonicalJson.Serialize(manifest);
        var summaryBytes = Summary(manifest);
        Reject(() => EvidenceEmptyObservationFiles.Verify(plan, Encoding.UTF8.GetBytes("{CANARY"), manifestBytes, summaryBytes));
        Reject(() => EvidenceEmptyObservationFiles.Verify(plan, planBytes, Encoding.UTF8.GetBytes("null"), summaryBytes));
        Reject(() => Verify(plan, manifest, Encoding.UTF8.GetBytes("[]")));
        var alias = Encoding.UTF8.GetString(planBytes).Replace("\"PolicyId\":", "\"policyId\":", StringComparison.Ordinal);
        Reject(() => EvidenceEmptyObservationFiles.Verify(plan, Encoding.UTF8.GetBytes(alias), manifestBytes, summaryBytes));
        var duplicate = Encoding.UTF8.GetString(manifestBytes).Replace("\"Metrics\":{", "\"Metrics\":{\"CleanupCompleted\":true,", StringComparison.Ordinal);
        Reject(() => EvidenceEmptyObservationFiles.Verify(plan, planBytes, Encoding.UTF8.GetBytes(duplicate), summaryBytes));
        Reject(() => EvidenceEmptyObservationFiles.Verify(plan, Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(planBytes)),
            manifestBytes, summaryBytes));
        Reject(() => Verify(plan, manifest with { ManifestDigest = new string('0', 64) }));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void EveryInputLengthIsCheckedBeforeAnyMemoryIsReadOrCopied(int invalid, bool over)
    {
        using var plan = new CountedMemory(invalid == 0 ? over ? EvidenceCanonicalJson.MaximumInputBytes + 1 : 0 : 1);
        using var manifest = new CountedMemory(invalid == 1 ? over ? EvidenceCanonicalJson.MaximumInputBytes + 1 : 0 : 1);
        using var summary = new CountedMemory(invalid == 2 ? over ? EvidenceEmptyObservationFiles.MaximumSummaryBytes + 1 : 0 : 1);
        Reject(() => EvidenceEmptyObservationFiles.Verify(Plan(), plan.Bytes, manifest.Bytes, summary.Bytes));
        Assert.Equal(0, plan.ReadCount);
        Assert.Equal(0, manifest.ReadCount);
        Assert.Equal(0, summary.ReadCount);
    }

    [Fact]
    public void CallerCancellationKeepsItsOriginalTokenAndDoesNotInspectInputMemory()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var bytes = new CountedMemory(1);
        var error = Assert.Throws<OperationCanceledException>(() => EvidenceEmptyObservationFiles.Verify(
            Plan(), bytes.Bytes, bytes.Bytes, bytes.Bytes, cancelled.Token));
        Assert.Equal(cancelled.Token, error.CancellationToken);
        Assert.Equal(0, bytes.ReadCount);
    }

    [Fact]
    public void AnInputReadCancellationCannotExposeAnUnrelatedExceptionAsCallerCancellation()
    {
        var plan = Plan();
        var manifest = Manifest(plan);
        using var bytes = new CountedMemory(1, cancellationError: true);
        Reject(() => EvidenceEmptyObservationFiles.Verify(plan, bytes.Bytes,
            EvidenceCanonicalJson.Serialize(manifest), Summary(manifest)));
        Assert.Equal(1, bytes.ReadCount);
    }

    private static EvidencePlan Plan(string path = "docs/A.md") => new EvidencePlanner().Resolve(
        new EvidencePolicy("sample", "1", "conservative",
        [
            new("empty", EvidenceProfileScope.Targeted, [], [], []),
            new("conservative", EvidenceProfileScope.Release, [new("build", "completion", 1, [])], [], []),
        ], [new("docs", "docs/**", "empty")]), [new(path)]);

    // Detached structural data, not a call to an admission factory or a completed protected execution.
    private static EvidenceManifest Manifest(EvidencePlan plan) => Rehash(new EvidenceManifest(
        plan.ContractVersion, plan.PlanDigest, EvidenceExecutionVerdict.Passed, EvidenceClaimKind.ObservationOnly,
        EvidenceClaimEligibility.Informational, EvidenceEnvelopeStatus.NotRequired, [], [], [], [], [],
        new EvidenceExecutionMetrics(), string.Empty, EvidenceExecutionMode.Observation));

    private static EvidenceManifest Rehash(EvidenceManifest manifest) => manifest with
    {
        ManifestDigest = EvidenceDigest.CanonicalSha256(manifest with { ManifestDigest = string.Empty }),
    };

    private static EvidencePlan Rehash(EvidencePlan plan) => plan with
    {
        PlanDigest = EvidenceDigest.CanonicalSha256(plan with { PlanDigest = string.Empty }),
    };

    private static byte[] Summary(EvidenceManifest manifest) => EvidenceCanonicalJson.Serialize(new
    {
        manifest.Mode, manifest.ClaimKind, manifest.Eligibility, manifest.ExecutionVerdict, manifest.EnvelopeStatus,
        Procedure = "registered-protected-producer", SandboxAttestation = false,
    });

    private static EvidenceManifest Verify(EvidencePlan plan, EvidenceManifest manifest, byte[]? summary = null) =>
        EvidenceEmptyObservationFiles.Verify(plan, EvidenceCanonicalJson.Serialize(plan),
            EvidenceCanonicalJson.Serialize(manifest), summary ?? Summary(manifest));

    private static void Reject(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD410", error.Code);
        Assert.StartsWith("ASEVD410: The terminal empty Observation files could not be verified.", error.Message);
        Assert.DoesNotContain("CANARY", error.Message);
        Assert.Null(error.InnerException);
    }

    private sealed class CountedMemory(int length, bool cancellationError = false) : MemoryManager<byte>
    {
        internal int ReadCount { get; private set; }
        internal ReadOnlyMemory<byte> Bytes => CreateMemory(length);
        public override Span<byte> GetSpan()
        {
            ReadCount++;
            if (cancellationError) throw new OperationCanceledException("CANARY: unrelated input cancellation.");
            throw new InvalidOperationException("CANARY: input was read before all length guards.");
        }
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}
