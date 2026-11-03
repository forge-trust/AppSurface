using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Pure detached-data verifier controls; fixtures cannot establish protected execution or custody.</summary>
public sealed class EvidenceCoverageObservationFilesTests
{
    private const string AssertionId = "appsurface/coverage/behavioral-patch@1";
    private const string ProducerId = "coverage";
    private static readonly byte[] CoberturaBytes = Encoding.UTF8.GetBytes("<coverage line-rate=\"0.5\" />\n");

    [Fact]
    public void CanonicalResolvedCoverageFilesAndPhysicalArtifactReturnDetachedData()
    {
        var plan = Plan();
        var manifest = Manifest(plan);
        var planBytes = EvidenceCanonicalJson.Serialize(plan);
        var manifestBytes = EvidenceCanonicalJson.Serialize(manifest);
        var summaryBytes = Summary(manifest);
        var artifactMap = Artifacts(plan);
        Assert.Equal(2, plan.ChangedPaths.Count);
        Assert.Equal(EvidenceDigest.CanonicalSha256(plan.PolicySnapshot!), plan.PolicyDigest);
        Assert.Equal(EvidenceDigest.CanonicalSha256(plan.ChangedPaths), plan.DiffDigest);

        var verified = EvidenceCoverageObservationFiles.Verify(plan, planBytes, manifestBytes, summaryBytes, artifactMap);

        Assert.NotSame(manifest, verified);
        Assert.Equal(manifest.ManifestDigest, verified.ManifestDigest);
        Assert.Equal(plan.Profile.Producers.Single().Id, verified.ProducerResults.Single().ProducerId);
        Assert.Equal("merged/coverage.cobertura.xml", verified.ProducerResults.Single().Artifacts!.Single().RelativePath);
        Assert.Equal(EvidenceClaimKind.ObservationOnly, verified.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.Informational, verified.Eligibility);
        Array.Fill(planBytes, (byte)'x');
        Array.Fill(manifestBytes, (byte)'x');
        Array.Fill(summaryBytes, (byte)'x');
        Assert.Equal(manifest.ManifestDigest, verified.ManifestDigest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnusedOptionalSlotCannotWidenTheClosedTerminalCoverageDeclaration(bool application)
    {
        var original = application ? ApplicationPlan() : Plan();
        var validManifest = application ? ApplicationManifest(original) : Manifest(original);
        if (application) Assert.Equal(validManifest.ManifestDigest, VerifyApplication(original, validManifest).ManifestDigest);
        else Assert.Equal(validManifest.ManifestDigest, Verify(original, validManifest).ManifestDigest);
        var profile = original.Profile with
        {
            Producers = [original.Profile.Producers[0] with
            {
                ArtifactSlots = [.. original.Profile.Producers[0].ArtifactSlots,
                    new("unused-probe", "unused", "application/octet-stream", false, 1)],
            }],
        };
        var policy = original.PolicySnapshot! with
        {
            Profiles = original.PolicySnapshot!.Profiles.Select(item => item.Id == profile.Id ? profile : item).ToArray(),
        };
        var changed = new EvidencePlanner().Resolve(policy, original.ChangedPaths);
        var coherentManifest = application ? ApplicationManifest(changed) : Manifest(changed);
        Assert.True(EvidenceManifestBuilder.Verify(changed, coherentManifest));
        Assert.Single(coherentManifest.ProducerResults[0].Artifacts!);
        if (application) Reject(() => VerifyApplication(changed, coherentManifest));
        else Reject(() => Verify(changed, coherentManifest));
    }

    [Fact]
    public void PhysicalProducerPathIsRequiredAndManifestPathExcludesProducerId()
    {
        var plan = Plan();
        var manifest = Manifest(plan);
        Reject(() => Verify(plan, manifest, artifacts: new Dictionary<string, ReadOnlyMemory<byte>>
        {
            ["merged/coverage.cobertura.xml"] = CoberturaBytes,
        }));
        Reject(() => Verify(plan, manifest, artifacts: new Dictionary<string, ReadOnlyMemory<byte>>
        {
            ["coverage/merged/Cobertura.xml"] = CoberturaBytes,
        }));
        Reject(() => Verify(plan, Rehash(manifest with
        {
            ProducerResults = [manifest.ProducerResults[0] with
            {
                Artifacts = [manifest.ProducerResults[0].Artifacts![0] with { RelativePath = "coverage/merged/coverage.cobertura.xml" }],
            }],
        })));
    }

    [Fact]
    public void PhysicalArtifactKeyUsesTheResolvedProducerId()
    {
        var original = Plan();
        var policy = original.PolicySnapshot!;
        const string producerId = "coverage-runner";
        var profile = policy.Profiles.Single(item => item.Id == "coverage");
        var changedProfile = profile with
        {
            Producers = [profile.Producers[0] with { Id = producerId }],
            Obligations = profile.Obligations.Select(obligation => obligation with
            {
                RequiredProducerIds = [producerId],
            }).ToArray(),
        };
        policy = policy with
        {
            Profiles = policy.Profiles.Select(item => item.Id == profile.Id ? changedProfile : item).ToArray(),
        };
        var plan = new EvidencePlanner().Resolve(policy, original.ChangedPaths);

        Assert.Equal("coverage-runner/merged/coverage.cobertura.xml", PhysicalPath(plan));
        Assert.Equal(plan.PlanDigest, Verify(plan, Manifest(plan), Artifacts(plan)).PlanDigest);
    }

    [Fact]
    public void ActualArtifactLengthAndHashMustMatchTheManifestMetadata()
    {
        var plan = Plan();
        var manifest = Manifest(plan);
        Reject(() => EvidenceCoverageObservationFiles.Verify(plan, EvidenceCanonicalJson.Serialize(plan),
            EvidenceCanonicalJson.Serialize(manifest), Summary(manifest), new Dictionary<string, ReadOnlyMemory<byte>>
            {
                [PhysicalPath(plan)] = Encoding.UTF8.GetBytes("<coverage CANARY />"),
            }));
        Reject(() => Verify(plan, Rehash(manifest with
        {
            ProducerResults = [manifest.ProducerResults[0] with
            {
                Artifacts = [manifest.ProducerResults[0].Artifacts![0] with { LengthBytes = CoberturaBytes.Length + 1 }],
            }],
        })));
    }

    [Theory]
    [InlineData("outcome")]
    [InlineData("producer")]
    [InlineData("assertion")]
    [InlineData("extra-assertion")]
    [InlineData("extra-artifact")]
    [InlineData("elapsed")]
    public void ProducerResultMustBeOnePassedDeclaredCoverageResult(string field)
    {
        var plan = Plan();
        var manifest = Manifest(plan);
        var result = manifest.ProducerResults[0];
        result = field switch
        {
            "outcome" => result with { Outcome = EvidenceProducerOutcome.Failed },
            "producer" => result with { ProducerId = "other" },
            "assertion" => result with { SatisfiedAssertionIds = ["other"] },
            "extra-assertion" => result with { SatisfiedAssertionIds = [AssertionId, "other"] },
            "extra-artifact" => result with { Artifacts = [.. result.Artifacts!, result.Artifacts![0]] },
            _ => result with { ElapsedMilliseconds = -1 },
        };
        Reject(() => Verify(plan, Rehash(manifest with { ProducerResults = [result] })));
    }

    [Fact]
    public void PassedProducerMayCarryItsBoundedHumanReadableDiagnostic()
    {
        var plan = Plan();
        var manifest = Manifest(plan);
        var message = "Coverage gate passed with the configured thresholds.";
        manifest = Rehash(manifest with
        {
            ProducerResults = [manifest.ProducerResults[0] with { Diagnostic = message }],
        });

        var verified = Verify(plan, manifest);

        Assert.Equal(message, verified.ProducerResults.Single().Diagnostic);
    }

    [Fact]
    public void ActualCliMetricsMayLeaveWallClockFieldsUnreportedWithNonzeroProducerElapsedTime()
    {
        var plan = Plan();
        var manifest = Manifest(plan) with { Metrics = new EvidenceExecutionMetrics() };
        manifest = Rehash(manifest);

        var verified = Verify(plan, manifest);

        Assert.Equal(3, verified.ProducerResults.Single().ElapsedMilliseconds);
        Assert.Equal(0, verified.Metrics.ProducerMilliseconds);
        Assert.Equal(0, verified.Metrics.TotalMilliseconds);
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("verdict")]
    [InlineData("claim")]
    [InlineData("eligibility")]
    [InlineData("envelope")]
    [InlineData("assertion")]
    [InlineData("resource")]
    [InlineData("producer-count")]
    [InlineData("selected-obligation")]
    [InlineData("closed-obligation")]
    [InlineData("unmediated-obligation")]
    public void ManifestMustMatchTheTerminalObservationDispositionAndPlannerClosure(string field)
    {
        var plan = Plan();
        var manifest = Manifest(plan);
        manifest = field switch
        {
            "mode" => manifest with { Mode = EvidenceExecutionMode.Trusted },
            "verdict" => manifest with { ExecutionVerdict = EvidenceExecutionVerdict.Incomplete },
            "claim" => manifest with { ClaimKind = EvidenceClaimKind.TargetedComplete },
            "eligibility" => manifest with { Eligibility = EvidenceClaimEligibility.PullRequestGate },
            "envelope" => manifest with { EnvelopeStatus = EvidenceEnvelopeStatus.ValidatedNotAttested },
            "assertion" => manifest with
            {
                EnvelopeAssertion = new("d", "d", "d", "d", "d", "d", "d", "d",
                "d", "d", "d", "d", "d", "d", "d", "d", DateTimeOffset.UnixEpoch)
            },
            "resource" => manifest with { ResourceResults = [new("extra", EvidenceResourceOutcome.Ready, 0)] },
            "producer-count" => manifest with { ProducerResults = [] },
            "selected-obligation" => manifest with { SelectedObligationIds = ["extra"] },
            "closed-obligation" => manifest with { ClosedObligationIds = ["extra"] },
            _ => manifest with { UnmediatedObligationIds = ["extra"] },
        };
        Reject(() => Verify(plan, Rehash(manifest)));
    }

    [Theory]
    [InlineData("cleanup")]
    [InlineData("diagnostic")]
    [InlineData("terminal")]
    [InlineData("planning-negative")]
    [InlineData("producer-negative")]
    [InlineData("cleanup-negative")]
    [InlineData("total-negative")]
    [InlineData("producer-exceeds-total")]
    [InlineData("cleanup-exceeds-total")]
    public void CleanupAndLifecycleMetricsMustBeSuccessfulAndCoherent(string field)
    {
        var plan = Plan();
        var manifest = Manifest(plan);
        var metrics = field switch
        {
            "cleanup" => new EvidenceExecutionMetrics(CleanupCompleted: false),
            "diagnostic" => new(CleanupDiagnostic: "CANARY"),
            "terminal" => new(TerminalFailureCode: "CANARY"),
            "planning-negative" => new(PlanningMilliseconds: -1),
            "producer-negative" => new(ProducerMilliseconds: -1),
            "cleanup-negative" => new(CleanupMilliseconds: -1),
            "total-negative" => new(TotalMilliseconds: -1),
            "producer-exceeds-total" => new(PlanningMilliseconds: 3, ProducerMilliseconds: 3, TotalMilliseconds: 5),
            _ => new(PlanningMilliseconds: 2, ProducerMilliseconds: 3, CleanupMilliseconds: 2, TotalMilliseconds: 6),
        };
        Reject(() => Verify(plan, Rehash(manifest with { Metrics = metrics })));
    }

    [Fact]
    public void CanonicalSummaryHasExactlySevenFixedFields()
    {
        var plan = Plan();
        var manifest = Manifest(plan);
        var summary = JsonNode.Parse(Summary(manifest))!.AsObject();
        summary["SandboxAttestation"] = true;
        Reject(() => EvidenceCoverageObservationFiles.Verify(plan, EvidenceCanonicalJson.Serialize(plan),
            EvidenceCanonicalJson.Serialize(manifest), EvidenceCanonicalJson.Serialize(summary), Artifacts(plan)));
        var extra = JsonNode.Parse(Summary(manifest))!.AsObject();
        extra["CANARY"] = true;
        Reject(() => EvidenceCoverageObservationFiles.Verify(plan, EvidenceCanonicalJson.Serialize(plan),
            EvidenceCanonicalJson.Serialize(manifest), EvidenceCanonicalJson.Serialize(extra), Artifacts(plan)));
    }

    [Fact]
    public void FullPlanReconstructionRejectsChangedPathsAndNonCoverageProfiles()
    {
        var expected = Plan();
        var other = Plan("src/other.cs");
        Reject(() => EvidenceCoverageObservationFiles.Verify(expected, EvidenceCanonicalJson.Serialize(other),
            EvidenceCanonicalJson.Serialize(Manifest(other)), Summary(Manifest(other)), Artifacts(other)));
        Reject(() => Verify(expected with { PolicySnapshot = null }, Manifest(expected)));
        Reject(() => Verify(expected with { Profile = expected.PolicySnapshot!.Profiles.Single(x => x.Id == "empty") },
            Manifest(expected), Artifacts(expected)));
        Reject(() => Verify(expected with
        {
            Profile = expected.Profile with { Producers = [expected.Profile.Producers[0] with { Version = "2.0.0" }] },
        }, Manifest(expected)));

        var policy = expected.PolicySnapshot!;
        var coverage = policy.Profiles.Single(item => item.Id == "coverage");
        var unregistered = coverage with
        {
            Producers = [coverage.Producers[0] with { Kind = "not-coverage" }],
        };
        var unregisteredPolicy = policy with
        {
            Profiles = policy.Profiles.Select(item => item.Id == coverage.Id ? unregistered : item).ToArray(),
        };
        var unregisteredPlan = new EvidencePlanner().Resolve(unregisteredPolicy, expected.ChangedPaths);
        Reject(() => Verify(unregisteredPlan, Manifest(unregisteredPlan)));
    }

    [Fact]
    public void CancellationAndMalformedCanonicalInputsFailClosed()
    {
        var plan = Plan();
        var manifest = Manifest(plan);
        using var source = new CancellationTokenSource();
        source.Cancel();
        var cancelled = Assert.Throws<OperationCanceledException>(() => EvidenceCoverageObservationFiles.Verify(
            plan, EvidenceCanonicalJson.Serialize(plan), EvidenceCanonicalJson.Serialize(manifest), Summary(manifest), Artifacts(plan), source.Token));
        Assert.Equal(source.Token, cancelled.CancellationToken);
        Reject(() => EvidenceCoverageObservationFiles.Verify(plan, Encoding.UTF8.GetBytes("{CANARY"),
            EvidenceCanonicalJson.Serialize(manifest), Summary(manifest), Artifacts(plan)));
        Reject(() => EvidenceCoverageObservationFiles.Verify(plan, EvidenceCanonicalJson.Serialize(plan),
            EvidenceCanonicalJson.Serialize(manifest), Summary(manifest), new Dictionary<string, ReadOnlyMemory<byte>>
            {
                [PhysicalPath(plan)] = ReadOnlyMemory<byte>.Empty,
            }));
    }

    [Fact]
    public void ApplicationFilesVerifyAsInformationalDataWhileDependencyFreeVerifierRemainsStrict()
    {
        var plan = ApplicationPlan();
        var manifest = ApplicationManifest(plan);
        var verified = VerifyApplication(plan, manifest);
        Assert.Equal(EvidenceClaimKind.ObservationOnly, verified.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.Informational, verified.Eligibility);
        Assert.Null(verified.EnvelopeAssertion);
        Reject(() => Verify(plan, manifest));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ApplicationFilesRejectWrongUnreadyOrMalformedResourceMetadata(int variant)
    {
        var plan = ApplicationPlan();
        var manifest = ApplicationManifest(plan);
        var ready = manifest.ResourceResults.Single();
        var changed = variant switch
        {
            0 => ready with { ResourceId = "foreign" },
            1 => ready with { Outcome = EvidenceResourceOutcome.Unavailable },
            2 => ready with { ElapsedMilliseconds = -1 },
            _ => ready with { Diagnostic = "CANARY-resource-data" },
        };
        Reject(() => VerifyApplication(plan, Rehash(manifest with { ResourceResults = [changed] })));
    }

    [Theory]
    [InlineData(-1L, 9L)]
    [InlineData(3L, 8L)]
    [InlineData(1L, 0L)]
    public void ApplicationFilesIncludeReadinessInNonnegativeCumulativeMetrics(long readiness, long total)
    {
        var plan = ApplicationPlan();
        var manifest = ApplicationManifest(plan);
        var metrics = manifest.Metrics with { ResourceReadinessMilliseconds = readiness, TotalMilliseconds = total };
        Reject(() => VerifyApplication(plan, Rehash(manifest with { Metrics = metrics })));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("resource")]
    [InlineData("timeout-zero")]
    [InlineData("timeout-over")]
    [InlineData("gate")]
    [InlineData("assertion-count")]
    [InlineData("assertion-id")]
    [InlineData("slot-count")]
    [InlineData("slot-name")]
    [InlineData("slot-root")]
    [InlineData("slot-media")]
    [InlineData("slot-optional")]
    [InlineData("slot-bound")]
    public void EachClosedProducerDeclarationFieldIsCheckedBeforeTerminalDataCanBeAdopted(string field)
    {
        var valid = Plan();
        var manifest = Manifest(valid);
        Assert.Equal(manifest.ManifestDigest, Verify(valid, manifest).ManifestDigest);
        var producer = valid.Profile.Producers[0];
        var slot = producer.ArtifactSlots[0];
        var changed = field switch
        {
            "version" => producer with { Version = "2.0.0" },
            "resource" => producer with { RequiredResources = ["CANARY"] },
            "timeout-zero" => producer with { TimeoutSeconds = 0 },
            "timeout-over" => producer with { TimeoutSeconds = 1801 },
            "gate" => producer with { CoverageGate = null },
            "assertion-count" => producer with { AssertionIds = [] },
            "assertion-id" => producer with { AssertionIds = ["CANARY"] },
            "slot-count" => producer with { ArtifactSlots = [] },
            "slot-name" => producer with { ArtifactSlots = [slot with { LogicalName = "CANARY" }] },
            "slot-root" => producer with { ArtifactSlots = [slot with { RelativeRoot = "CANARY" }] },
            "slot-media" => producer with { ArtifactSlots = [slot with { MediaType = "text/plain" }] },
            "slot-optional" => producer with { ArtifactSlots = [slot with { Required = false }] },
            "slot-bound" => producer with { ArtifactSlots = [slot with { MaximumBytes = slot.MaximumBytes - 1 }] },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        var expected = valid with { Profile = valid.Profile with { Producers = [changed] } };
        Reject(() => Verify(expected, manifest));
    }

    [Theory]
    [InlineData("producer-id")]
    [InlineData("outcome")]
    [InlineData("assertion-count")]
    [InlineData("assertion-id")]
    [InlineData("elapsed")]
    [InlineData("artifact-null")]
    [InlineData("artifact-count")]
    public void EachTerminalProducerResultFieldMustMatchTheDeclaredPassingResult(string field)
    {
        var plan = Plan();
        var manifest = Manifest(plan);
        Assert.Equal(manifest.ManifestDigest, Verify(plan, manifest).ManifestDigest);
        var producer = manifest.ProducerResults[0];
        var changed = field switch
        {
            "producer-id" => producer with { ProducerId = "CANARY" },
            "outcome" => producer with { Outcome = EvidenceProducerOutcome.Failed },
            "assertion-count" => producer with { SatisfiedAssertionIds = [] },
            "assertion-id" => producer with { SatisfiedAssertionIds = ["CANARY"] },
            "elapsed" => producer with { ElapsedMilliseconds = -1 },
            "artifact-null" => producer with { Artifacts = null },
            "artifact-count" => producer with { Artifacts = [] },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        Reject(() => Verify(plan, Rehash(manifest with { ProducerResults = [changed] })));
    }

    [Theory]
    [InlineData("planning")]
    [InlineData("readiness")]
    public void CumulativeMetricsRejectAnEarlyStageThatAlreadyExceedsTheReportedTotal(string stage)
    {
        var plan = ApplicationPlan();
        var manifest = ApplicationManifest(plan);
        var metrics = stage == "planning"
            ? new EvidenceExecutionMetrics(PlanningMilliseconds: 10, TotalMilliseconds: 9)
            : new EvidenceExecutionMetrics(PlanningMilliseconds: 2, ResourceReadinessMilliseconds: 8, TotalMilliseconds: 9);
        Reject(() => VerifyApplication(plan, Rehash(manifest with { Metrics = metrics })));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    public void CanonicalNonobjectSummaryDoesNotBecomeTerminalAuthority(string json)
    {
        var plan = Plan();
        var manifest = Manifest(plan);
        Reject(() => EvidenceCoverageObservationFiles.Verify(plan, EvidenceCanonicalJson.Serialize(plan),
            EvidenceCanonicalJson.Serialize(manifest), Encoding.UTF8.GetBytes(json), Artifacts(plan)));
    }

    private static EvidencePlan ApplicationPlan()
    {
        var resource = new EvidenceResourceDeclaration("native-http", "aspire_health", 1, []);
        var policy = Policy();
        policy = policy with
        {
            Profiles = policy.Profiles.Select(profile => profile.Id == "coverage"
            ? profile with { Resources = [resource], Producers = [CoverageProducer() with { RequiredResources = [resource.Id] }] }
            : profile).ToArray()
        };
        return new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("src/changed.cs")]);
    }

    // Pure canonical file data only: these helpers issue no admission, lease, process or custody authority.
    private static EvidenceManifest ApplicationManifest(EvidencePlan plan)
    {
        var data = Manifest(plan);
        return Rehash(data with
        {
            ResourceResults = [new("native-http", EvidenceResourceOutcome.Ready, 1)],
            Metrics = data.Metrics with { ResourceReadinessMilliseconds = 1, TotalMilliseconds = 9 },
        });
    }

    private static EvidenceManifest VerifyApplication(EvidencePlan plan, EvidenceManifest manifest) =>
        EvidenceCoverageObservationFiles.VerifyApplication(plan, EvidenceCanonicalJson.Serialize(plan),
            EvidenceCanonicalJson.Serialize(manifest), Summary(manifest), Artifacts(plan));

    private static EvidencePlan Plan(string path = "src/changed.cs")
    {
        var policy = Policy();
        var diff = "diff --git a/src/from-diff.cs b/src/from-diff.cs\n"
            + "--- a/src/from-diff.cs\n+++ b/src/from-diff.cs\n@@ -1 +1 @@\n-old\n+new\n";
        var paths = new[] { new NormalizedDiffPath(path) }
            .Concat(EvidenceUnifiedDiffReader.Read(diff)).ToArray();
        return new EvidencePlanner().Resolve(policy, paths);
    }

    private static EvidencePolicy Policy() => new("coverage-sample", "1", "conservative",
    [
        new("empty", EvidenceProfileScope.Targeted, [], [], []),
        new("coverage", EvidenceProfileScope.Targeted, [], [CoverageProducer()],
            [new("coverage-obligation", "behavioral-change", "A protected coverage producer is required.", [ProducerId], AssertionId)]),
        new("conservative", EvidenceProfileScope.Release, [new("build", "completion", 1, [])], [], []),
    ], [new("source", "src/**", "coverage"), new("docs", "docs/**", "empty")]);

    private static EvidenceProducerDeclaration CoverageProducer() => new(ProducerId, "coverage", "1.0.0", [],
        [AssertionId], [new("coverage-report", "merged", "application/xml", true, 20L * 1024 * 1024)],
        120, new(0, 0));

    private static EvidenceManifest Manifest(EvidencePlan plan)
    {
        var artifact = new EvidenceArtifactResult("coverage-report", "merged/coverage.cobertura.xml", "application/xml",
            CoberturaBytes.LongLength, EvidenceDigest.Sha256(CoberturaBytes));
        var result = new EvidenceProducerResult(plan.Profile.Producers.Single().Id, EvidenceProducerOutcome.Passed, [AssertionId],
            Artifacts: [artifact], ElapsedMilliseconds: 3);
        return Rehash(new EvidenceManifest(plan.ContractVersion, plan.PlanDigest, EvidenceExecutionVerdict.Passed,
            EvidenceClaimKind.ObservationOnly, EvidenceClaimEligibility.Informational, EvidenceEnvelopeStatus.NotRequired,
            [], ["coverage-obligation"], ["coverage-obligation"], [], [result],
            new(PlanningMilliseconds: 2, ProducerMilliseconds: 3, CleanupMilliseconds: 2, TotalMilliseconds: 8),
            string.Empty, EvidenceExecutionMode.Observation));
    }

    private static EvidenceManifest Rehash(EvidenceManifest manifest) => manifest with
    {
        ManifestDigest = EvidenceDigest.CanonicalSha256(manifest with { ManifestDigest = string.Empty }),
    };

    private static byte[] Summary(EvidenceManifest manifest) => EvidenceCanonicalJson.Serialize(new
    {
        manifest.Mode,
        manifest.ClaimKind,
        manifest.Eligibility,
        manifest.ExecutionVerdict,
        manifest.EnvelopeStatus,
        Procedure = "registered-protected-producer",
        SandboxAttestation = false,
    });

    private static string PhysicalPath(EvidencePlan plan) =>
        plan.Profile.Producers.Single().Id + "/merged/coverage.cobertura.xml";

    private static Dictionary<string, ReadOnlyMemory<byte>> Artifacts(EvidencePlan plan) => new(StringComparer.Ordinal)
    {
        [PhysicalPath(plan)] = CoberturaBytes,
    };

    private static EvidenceManifest Verify(EvidencePlan plan, EvidenceManifest manifest,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>>? artifacts = null) => EvidenceCoverageObservationFiles.Verify(
            plan, EvidenceCanonicalJson.Serialize(plan), EvidenceCanonicalJson.Serialize(manifest), Summary(manifest),
            artifacts ?? Artifacts(plan));

    private static void Reject(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD410", error.Code);
        Assert.StartsWith("ASEVD410: The terminal coverage Observation files could not be verified.", error.Message);
        Assert.DoesNotContain("CANARY", error.Message);
        Assert.Null(error.InnerException);
    }
}
