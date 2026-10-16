using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Pure policy/diff controls for checkpoint-two planning; no case creates admission or producer proof.</summary>
public sealed class EvidenceCoverageObservationPlanTests
{
    private const string AssertionId = "appsurface/coverage/behavioral-patch@1";

    [Fact]
    public void ValidProtectedPolicyAndDiffProduceThePlannerPlanAndItsActualDigests()
    {
        var policy = Policy();
        var policyBytes = EvidenceCanonicalJson.Serialize(policy);
        var diffBytes = Diff("src/Two.cs");
        var request = Request(diff: diffBytes);

        var actual = Resolve(request, policyBytes, diffBytes);
        var paths = new List<NormalizedDiffPath> { new("src/One.cs") };
        paths.AddRange(EvidenceUnifiedDiffReader.Read(Encoding.UTF8.GetString(diffBytes)));
        var expected = new EvidencePlanner().Resolve(policy, paths);

        Assert.Equal(expected.PlanDigest, actual.PlanDigest);
        Assert.Equal(expected.PolicyDigest, actual.PolicyDigest);
        Assert.Equal(expected.DiffDigest, actual.DiffDigest);
        Assert.Equal("coverage", actual.Profile.Id);
        Assert.Equal(EvidenceProfileScope.Targeted, actual.Profile.Scope);
        Assert.Empty(actual.Profile.Resources);
        Assert.Equal("coverage", Assert.Single(actual.Profile.Producers).Id);
        Assert.Equal(AssertionId, Assert.Single(actual.Profile.Producers[0].AssertionIds));
        Assert.Equal(new[] { "src/One.cs", "src/Two.cs" }, actual.ChangedPaths.Select(path => path.Path));
        Assert.Equal("source", Assert.Single(actual.MatchedRuleIds));
    }

    [Fact]
    public void PlanningCopiesPolicyAndDiffBytesAndBindsPolicyHashToOriginalBytes()
    {
        var canonical = EvidenceCanonicalJson.Serialize(Policy());
        var policyBytes = Encoding.UTF8.GetBytes(" \n" + Encoding.UTF8.GetString(canonical) + "\n ");
        var diffBytes = Diff("src/Two.cs");
        var request = Request(diff: diffBytes);
        var policyHash = EvidenceDigest.Sha256(policyBytes);
        var diffHash = EvidenceDigest.Sha256(diffBytes);
        var expected = new EvidencePlanner().Resolve(Policy(), [new("src/One.cs"), new("src/Two.cs")]);

        var plan = EvidenceCoverageObservationPlan.Resolve(request, policyBytes, policyHash, diffBytes);
        Array.Fill(policyBytes, (byte)'x');
        Array.Fill(diffBytes, (byte)'x');

        Assert.Equal(EvidenceDigest.Sha256(canonical), plan.PolicyDigest);
        Assert.Equal(expected.PlanDigest, plan.PlanDigest);
        Assert.Equal(new[] { "src/One.cs", "src/Two.cs" }, plan.ChangedPaths.Select(path => path.Path));
        Assert.Equal("sample", plan.PolicySnapshot?.Id);
        Assert.NotEqual(policyHash, plan.PolicyDigest);
        Assert.NotEqual(diffHash, plan.DiffDigest);
    }

    [Fact]
    public void DiffOnlyRequestResolvesAndAbsentDiffRemainsDistinctFromPresentEmptyDiff()
    {
        var policy = EvidenceCanonicalJson.Serialize(Policy());
        var diff = Diff("src/Two.cs");
        var onlyDiff = Request(paths: [], diff: diff);
        Assert.Equal("coverage", Resolve(onlyDiff, policy, diff).Profile.Id);
        Assert.False(EvidenceCoverageObservationPlan.AsOptionalDiff(null).HasValue);
        Assert.True(EvidenceCoverageObservationPlan.AsOptionalDiff([]).HasValue);
        Reject(() => Resolve(Request(paths: [], diff: []), policy, Array.Empty<byte>()));
    }

    [Theory]
    [InlineData("docs/README.md")]
    [InlineData("unmatched/file.cs")]
    public void UnmatchedOrEmptySelectedProfileCannotRunCoverageObservation(string path)
    {
        var bytes = EvidenceCanonicalJson.Serialize(Policy());
        Reject(() => Resolve(Request(paths: [path]), bytes));
        Reject(() => Resolve(Request(ids: ["empty"]), bytes));
    }

    [Fact]
    public void ExplicitRuleToConservativeProfileCannotSelectCoverageObservation()
    {
        var policy = Policy() with
        {
            Rules = [new("source", "src/**", "conservative"), new("docs", "docs/**", "empty")],
        };
        Reject(() => Resolve(Request(), EvidenceCanonicalJson.Serialize(policy)));
    }

    [Fact]
    public void MixedProfilesAndRenamePreviousPathCannotHideConservativeFallback()
    {
        var bytes = EvidenceCanonicalJson.Serialize(Policy());
        Reject(() => Resolve(Request(paths: ["src/One.cs", "docs/README.md"]), bytes));
        var rename = Encoding.UTF8.GetBytes(
            "diff --git a/docs/README.md b/src/Two.cs\nsimilarity index 100%\nrename from docs/README.md\nrename to src/Two.cs\n");
        Reject(() => Resolve(Request(paths: [], diff: rename), bytes, rename));
    }

    [Fact]
    public void WhitelistedProfilesMustExistAndEveryRequestedProducerMustBeDeclaredByOne()
    {
        var bytes = EvidenceCanonicalJson.Serialize(Policy());
        Reject(() => Resolve(Request(ids: ["coverage", "missing"]), bytes));
        Reject(() => Resolve(Request(producers: []), bytes));
        Reject(() => Resolve(Request(producers: ["other"]), bytes));
        Reject(() => Resolve(Request(producers: ["coverage", "undeclared"]), bytes));
        Reject(() => Resolve(Request(ids: []), bytes));
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("version")]
    [InlineData("resources")]
    [InlineData("dependencies")]
    [InlineData("assertions")]
    [InlineData("gate")]
    [InlineData("artifact-name")]
    [InlineData("artifact-root")]
    [InlineData("artifact-media")]
    [InlineData("artifact-required")]
    [InlineData("artifact-limit")]
    [InlineData("artifact-missing")]
    [InlineData("artifact-extra")]
    [InlineData("timeout")]
    [InlineData("extra-producer")]
    public void SelectedAndWhitelistedCoverageProfilesRequireTheClosedSupportedDeclaration(string change)
    {
        var policy = Policy();
        var profile = policy.Profiles.Single(item => item.Id == "coverage");
        var producer = profile.Producers[0];
        producer = change switch
        {
            "kind" => producer with { Kind = "other" },
            "version" => producer with { Version = "2.0.0" },
            "resources" => producer,
            "dependencies" => producer with { RequiredResources = ["database"] },
            "assertions" => producer with { AssertionIds = [AssertionId, "other/assertion"] },
            "gate" => producer with { CoverageGate = null },
            "artifact-name" => producer with { ArtifactSlots = [producer.ArtifactSlots[0] with { LogicalName = "unknown" }] },
            "artifact-root" => producer with { ArtifactSlots = [producer.ArtifactSlots[0] with { RelativeRoot = "other" }] },
            "artifact-media" => producer with { ArtifactSlots = [producer.ArtifactSlots[0] with { MediaType = "text/plain" }] },
            "artifact-required" => producer with { ArtifactSlots = [producer.ArtifactSlots[0] with { Required = false }] },
            "artifact-limit" => producer with
            {
                ArtifactSlots = [producer.ArtifactSlots[0] with { MaximumBytes = producer.ArtifactSlots[0].MaximumBytes + 1 }],
            },
            "artifact-missing" => producer with { ArtifactSlots = [] },
            "artifact-extra" => producer with
            {
                ArtifactSlots = [producer.ArtifactSlots[0], producer.ArtifactSlots[0] with { LogicalName = "coverage-summary" }],
            },
            "timeout" => producer with { TimeoutSeconds = 1801 },
            "extra-producer" => producer,
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        EvidenceResourceDeclaration[] resources = change switch
        {
            "resources" => [new("database", "completion", 1, [])],
            "dependencies" => [new("base", "completion", 1, []), new("database", "completion", 1, ["base"])],
            _ => [],
        };
        EvidenceProducerDeclaration[] producers = change == "extra-producer"
            ? new[] { producer, producer with { Id = "second" } }
            : [producer];
        var changedProfile = profile with { Resources = resources, Producers = producers };
        policy = policy with
        {
            Profiles = policy.Profiles.Select(item => item.Id == changedProfile.Id ? changedProfile : item).ToArray(),
        };

        Reject(() => Resolve(Request(), EvidenceCanonicalJson.Serialize(policy)));
    }

    [Fact]
    public void NonselectedWhitelistedCoverageAndEmptyProfilesAreAllowedButUnlistedMalformedPolicyIsNot()
    {
        var policy = Policy();
        var secondCoverage = policy.Profiles.Single(item => item.Id == "coverage") with
        {
            Id = "alternate-coverage",
            Producers = [policy.Profiles.Single(item => item.Id == "coverage").Producers[0] with { Id = "alternate" }],
            Obligations = [policy.Profiles.Single(item => item.Id == "coverage").Obligations[0] with { RequiredProducerIds = ["alternate"] }],
        };
        policy = policy with
        {
            Profiles = [.. policy.Profiles, secondCoverage],
            Rules = [.. policy.Rules, new("alternate", "other/**", "alternate-coverage")],
        };
        var validBytes = EvidenceCanonicalJson.Serialize(policy);
        var request = Request(ids: ["coverage", "empty", "alternate-coverage"], producers: ["coverage"]);
        Assert.Equal("coverage", Resolve(request, validBytes).Profile.Id);

        var malformedUnselected = policy with
        {
            Profiles = policy.Profiles.Select(profile => profile.Id == "alternate-coverage"
                ? profile with { Producers = [profile.Producers[0] with { Version = "2.0.0" }] }
                : profile).ToArray(),
        };
        Reject(() => Resolve(request, EvidenceCanonicalJson.Serialize(malformedUnselected)));
    }

    [Fact]
    public void FullPlannerValidationRejectsMalformedUnselectedPolicyMembers()
    {
        var policy = Policy() with
        {
            Rules = [.. Policy().Rules, new("bad", "unselected/**", "missing")],
        };
        Reject(() => Resolve(Request(), EvidenceCanonicalJson.Serialize(policy)));
    }

    [Fact]
    public void DiffPresenceHashSyntaxAndPolicyBytesRemainBoundedAndCanarySafe()
    {
        var policyBytes = EvidenceCanonicalJson.Serialize(Policy());
        var diff = Diff("src/Two.cs");
        var request = Request(diff: diff);
        Resolve(request, policyBytes, diff);
        Reject(() => Resolve(request, policyBytes));
        Reject(() => Resolve(Request(), policyBytes, diff));
        Reject(() => Resolve(request, policyBytes, Diff("src/Other.cs")));
        Reject(() => Resolve(Request(paths: [], diff: [1, 2, 3]), policyBytes, new byte[] { 1, 2, 3 }));
        Reject(() => Resolve(Request(), Encoding.UTF8.GetBytes("{\"CANARY\":true}")));
        Reject(() => EvidenceCoverageObservationPlan.Resolve(Request(), policyBytes, new string('0', 64)));

        var malformed = Encoding.UTF8.GetBytes("@@ -1 +1 @@\n-CANARY\n+changed\n");
        Reject(() => Resolve(Request(diff: malformed), policyBytes, malformed));
    }

    [Fact]
    public void CountedInputAcceptsExactMaximumAndRejectsEmptyOrOverLimitBytes()
    {
        var canonical = EvidenceCanonicalJson.Serialize(Policy());
        var exact = new byte[EvidenceCanonicalJson.MaximumInputBytes];
        Array.Fill(exact, (byte)' ');
        canonical.CopyTo(exact, 0);
        Assert.Equal("coverage", Resolve(Request(), exact).Profile.Id);
        Reject(() => Resolve(Request(), ReadOnlyMemory<byte>.Empty));
        Reject(() => Resolve(Request(), new byte[EvidenceCanonicalJson.MaximumInputBytes + 1]));
        var overDiff = new byte[EvidenceCanonicalJson.MaximumInputBytes + 1];
        Reject(() => Resolve(Request(diff: overDiff), canonical, overDiff));
    }

    [Fact]
    public void CancellationAndAbsentRetainedInputDoNotCreateAuthority()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var bytes = EvidenceCanonicalJson.Serialize(Policy());
        Assert.Throws<OperationCanceledException>(() => EvidenceCoverageObservationPlan.Resolve(
            Request(), bytes, EvidenceDigest.Sha256(bytes), token: cancelled.Token));
        Reject(() => EvidenceCoverageObservationPlan.Resolve(null!, bytes, EvidenceDigest.Sha256(bytes)));
        Reject(() => EvidenceCoverageObservationPlan.FromInput(null!, CancellationToken.None));
    }

    private static EvidencePolicy Policy() => new("sample", "1", "conservative",
    [
        new("empty", EvidenceProfileScope.Targeted, [], [], []),
        new("coverage", EvidenceProfileScope.Targeted, [], [CoverageProducer("coverage")],
            [new("covered", "behavioral-change", "Changed code requires the declared coverage procedure.", ["coverage"], AssertionId)]),
        new("conservative", EvidenceProfileScope.Release, [new("release-build", "completion", 1, [])], [], []),
    ], [new("source", "src/**", "coverage"), new("docs", "docs/**", "empty")]);

    private static EvidenceProducerDeclaration CoverageProducer(string id) => new(id, "coverage", "1.0.0", [],
        [AssertionId],
        [new("coverage-report", "merged", "application/xml", true, 20L * 1024 * 1024)],
        120, new(0, 0));

    private static byte[] Diff(string path) => Encoding.UTF8.GetBytes(
        $"diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n@@ -1 +1 @@\n-old\n+new\n");

    private static EvidencePlan Resolve(EvidenceSupervisorRequest request, ReadOnlyMemory<byte> policyBytes,
        ReadOnlyMemory<byte>? diffBytes = null) => EvidenceCoverageObservationPlan.Resolve(
            request, policyBytes, EvidenceDigest.Sha256(policyBytes.Span), diffBytes);

    private static EvidenceSupervisorRequest Request(string[]? paths = null, string[]? ids = null,
        string[]? producers = null, byte[]? diff = null)
    {
        var values = new Dictionary<string, object?>
        {
            ["schema"] = "evidence-supervisor-linux-v1", ["mode"] = "observation",
            ["tool_root"] = "/opt/tool", ["runtime_root"] = "/opt/runtime",
            ["runtime_host"] = "/opt/runtime/dotnet", ["entry_path"] = "/opt/tool/Worker.dll",
            ["policy_file"] = "/opt/tool/policy.json", ["subject_root"] = "/work/subject",
            ["base_revision"] = new string('a', 40), ["subject_revision"] = new string('b', 40),
            ["workflow_identity"] = "workflow", ["paths"] = paths ?? ["src/One.cs"],
            ["observation_profile_ids"] = ids ?? ["coverage", "empty"],
            ["observation_producer_ids"] = producers ?? ["coverage"],
            ["job_deadline_utc"] = "2099-01-01T00:00:00Z", ["admission_seconds"] = 1,
            ["start_seconds"] = 1, ["collection_seconds"] = 60, ["cleanup_seconds"] = 2, ["stopping_seconds"] = 1,
            ["diff_file"] = diff is null ? null : "/opt/tool/changes.diff",
            ["diff_sha256"] = diff is null ? null : EvidenceDigest.Sha256(diff), ["solution"] = null,
        };
        return EvidenceSupervisorRequest.Parse(JsonSerializer.SerializeToUtf8Bytes(values));
    }

    private static void Reject(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD406", error.Code);
        Assert.StartsWith("ASEVD406: The protected coverage Observation plan could not be validated.", error.Message);
        Assert.DoesNotContain("CANARY", error.Message);
        Assert.Null(error.InnerException);
    }
}
