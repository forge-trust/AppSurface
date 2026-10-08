using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Real planner/data controls; none creates admission, protected input, a lease or native proof.</summary>
public sealed class EvidenceEmptyObservationPlanTests
{
    [Fact]
    public void ValidNonemptyConservativePolicyResolvesTheAllowedEmptyTargetedNeighbor()
    {
        var policy = Policy();
        var bytes = EvidenceCanonicalJson.Serialize(policy);
        var plan = Resolve(Request(), bytes);
        var expected = new EvidencePlanner().Resolve(policy, [new("docs/A.md")]);
        Assert.Equal(expected.PlanDigest, plan.PlanDigest);
        Assert.Equal(expected.PolicyDigest, plan.PolicyDigest);
        Assert.Equal(expected.DiffDigest, plan.DiffDigest);
        Assert.Equal("empty", plan.Profile.Id);
        Assert.Equal(EvidenceProfileScope.Targeted, plan.Profile.Scope);
        Assert.Empty(plan.Profile.Resources);
        Assert.Empty(plan.Profile.Producers);
        Assert.Empty(plan.Profile.Obligations);
        Assert.Equal("docs", Assert.Single(plan.MatchedRuleIds));
        Assert.NotNull(plan.PolicySnapshot);
        Assert.Single(plan.PolicySnapshot.Profiles.Single(profile => profile.Id == "conservative").Resources);
    }

    [Fact]
    public void OriginalByteHashAndDetachedPolicySnapshotSurviveCallerMutation()
    {
        var canonical = EvidenceCanonicalJson.Serialize(Policy());
        var bytes = Encoding.UTF8.GetBytes(" \n" + Encoding.UTF8.GetString(canonical) + "\n ");
        var plan = Resolve(Request(), bytes);
        Assert.NotEqual(EvidenceDigest.Sha256(bytes), plan.PolicyDigest);
        Array.Fill(bytes, (byte)'x');
        Assert.Equal("empty", plan.Profile.Id);
        Assert.Equal(EvidenceDigest.Sha256(canonical), plan.PolicyDigest);
        var second = Resolve(Request(), canonical);
        var mutableRules = Assert.IsAssignableFrom<IList<string>>(plan.MatchedRuleIds);
        mutableRules[0] = "mutated";
        Assert.Equal("docs", Assert.Single(second.MatchedRuleIds));
    }

    [Fact]
    public void DeclaredActualDiffAndExplicitPathsUseTheSamePlannerDigests()
    {
        var diff = Diff("docs/B.md");
        var policy = Policy();
        var plan = Resolve(Request(diff: diff), EvidenceCanonicalJson.Serialize(policy), diff);
        var paths = new List<NormalizedDiffPath> { new("docs/A.md") };
        paths.AddRange(EvidenceUnifiedDiffReader.Read(Encoding.UTF8.GetString(diff)));
        var expected = new EvidencePlanner().Resolve(policy, paths);
        Assert.Equal(expected.PlanDigest, plan.PlanDigest);
        Assert.Equal(new[] { "docs/A.md", "docs/B.md" }, plan.ChangedPaths.Select(path => path.Path));
    }

    [Fact]
    public void DiffOnlyRequestCanResolveButAnEmptyPathAndDiffRequestCannot()
    {
        var policy = EvidenceCanonicalJson.Serialize(Policy());
        var diff = Diff("docs/B.md");
        Assert.Equal("docs/B.md", Assert.Single(Resolve(Request(paths: [], diff: diff), policy, diff).ChangedPaths).Path);
        Reject(() => Resolve(Request(paths: []), policy));
    }

    [Theory]
    [InlineData("src/A.cs")]
    [InlineData("unmatched/file")]
    public void UnmatchedAndMixedPathsCannotUseAnEmptyProfileWhitelist(string path)
    {
        var bytes = EvidenceCanonicalJson.Serialize(Policy());
        Reject(() => Resolve(Request(paths: [path]), bytes));
        Reject(() => Resolve(Request(paths: ["docs/A.md", path]), bytes));
    }

    [Fact]
    public void TwoAllowedEmptyProfilesStillRejectThePlannersMixedConservativeFallback()
    {
        var policy = Policy();
        policy = policy with
        {
            Profiles = [.. policy.Profiles, new("other", EvidenceProfileScope.Targeted, [], [], [])],
            Rules = [.. policy.Rules, new("other", "notes/**", "other")],
        };
        var bytes = EvidenceCanonicalJson.Serialize(policy);
        Assert.Equal("other", Resolve(Request(paths: ["notes/A.md"], ids: ["empty", "other"]), bytes).Profile.Id);
        Reject(() => Resolve(Request(paths: ["docs/A.md", "notes/A.md"], ids: ["empty", "other"]), bytes));
        Reject(() => Resolve(Request(paths: ["notes/A.md"]), bytes));
    }

    [Fact]
    public void RenamePreviousPathAndChangedDiffPathCannotHideConservativeRequirements()
    {
        var bytes = EvidenceCanonicalJson.Serialize(Policy());
        var rename = Encoding.UTF8.GetBytes("diff --git a/src/A.cs b/docs/A.md\nsimilarity index 100%\nrename from src/A.cs\nrename to docs/A.md\n");
        Reject(() => Resolve(Request(paths: [], diff: rename), bytes, rename));
        var changed = Diff("src/B.cs");
        Reject(() => Resolve(Request(diff: changed), bytes, changed));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("conservative")]
    public void EveryWhitelistMemberMustExistAndBeEmptyEvenWhenNotSelected(string id)
    {
        var bytes = EvidenceCanonicalJson.Serialize(Policy());
        Reject(() => Resolve(Request(ids: ["empty", id]), bytes));
        Reject(() => Resolve(Request(ids: []), bytes));
        Reject(() => Resolve(Request(producers: ["producer"]), bytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FullPolicyValidationRejectsInvalidUnselectedAndEmptyReleaseDeclarations(int variant)
    {
        var policy = Policy();
        policy = variant switch
        {
            0 => policy with { ConservativeProfileId = "empty" },
            1 => policy with { Profiles = [policy.Profiles[0] with { Scope = EvidenceProfileScope.Release }, policy.Profiles[1]] },
            2 => policy with { Profiles = [.. policy.Profiles, policy.Profiles[0]] },
            _ => policy with { Rules = [.. policy.Rules, new("bad", "unselected/**", "missing")] },
        };
        Reject(() => Resolve(Request(), EvidenceCanonicalJson.Serialize(policy)));
    }

    [Theory]
    [InlineData("resources")]
    [InlineData("producers")]
    [InlineData("obligations")]
    public void AWhitelistedNonemptyTargetedProfileIsRejected(string member)
    {
        var policy = Policy();
        var profile = policy.Profiles[0];
        var producer = new EvidenceProducerDeclaration("producer", "ordinary", "1", [], ["assertion"], [], 1);
        profile = member switch
        {
            "resources" => profile with { Resources = [new("resource", "completion", 1, [])] },
            "producers" => profile with { Producers = [producer] },
            _ => profile with { Producers = [producer], Obligations = [new("obligation", "risk", "reason", ["producer"], "assertion")] },
        };
        policy = policy with { Profiles = [profile, policy.Profiles[1]] };
        EvidencePlanner.ValidatePolicy(policy);
        Reject(() => Resolve(Request(), EvidenceCanonicalJson.Serialize(policy)));
    }

    [Fact]
    public void DiffPresenceDigestAndSyntaxHaveAnOtherwiseValidNeighbor()
    {
        var bytes = EvidenceCanonicalJson.Serialize(Policy());
        var diff = Diff("docs/B.md");
        var request = Request(diff: diff);
        Resolve(request, bytes, diff);
        Reject(() => Resolve(request, bytes));
        Reject(() => Resolve(Request(), bytes, diff));
        Reject(() => Resolve(request, bytes, ReadOnlyMemory<byte>.Empty));
        Reject(() => Resolve(request, bytes, Diff("docs/C.md")));
        var malformed = Encoding.UTF8.GetBytes("@@ -1 +1 @@\n-CANARY\n+changed\n");
        Reject(() => Resolve(Request(diff: malformed), bytes, malformed));
    }

    [Fact]
    public void CountedPolicyLimitAcceptsTheExactLimitAndRejectsOneExtraByte()
    {
        var canonical = EvidenceCanonicalJson.Serialize(Policy());
        var exact = new byte[EvidenceCanonicalJson.MaximumInputBytes];
        Array.Fill(exact, (byte)' ');
        canonical.CopyTo(exact, 0);
        Assert.Equal("empty", Resolve(Request(), exact).Profile.Id);
        Reject(() => Resolve(Request(), ReadOnlyMemory<byte>.Empty));
        Reject(() => Resolve(Request(), new byte[EvidenceCanonicalJson.MaximumInputBytes + 1]));
        var overDiff = new byte[EvidenceCanonicalJson.MaximumInputBytes + 1];
        Reject(() => Resolve(Request(diff: overDiff), canonical, overDiff));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void ExpectedByteHashIsNotAnUncheckedPolicyClaim(string hash)
    {
        var bytes = EvidenceCanonicalJson.Serialize(Policy());
        Reject(() => EvidenceEmptyObservationPlan.Resolve(Request(), bytes, hash));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"CANARY\":true}")]
    [InlineData("{\"Id\":\"CANARY\",\"Id\":\"duplicate\"}")]
    [InlineData("{\"Profiles\":null}")]
    public void MalformedPolicyAndHashMismatchNeverExposeSuppliedText(string json)
    {
        Reject(() => Resolve(Request(), Encoding.UTF8.GetBytes(json)));
        var bytes = EvidenceCanonicalJson.Serialize(Policy());
        Reject(() => EvidenceEmptyObservationPlan.Resolve(Request(), bytes, new string('0', 64)));
    }

    [Fact]
    public void CancellationAndMissingNativeInputCannotTurnDataIntoOwnership()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var bytes = EvidenceCanonicalJson.Serialize(Policy());
        Assert.Throws<OperationCanceledException>(() => EvidenceEmptyObservationPlan.Resolve(
            Request(), bytes, EvidenceDigest.Sha256(bytes), token: cancelled.Token));
        Reject(() => EvidenceEmptyObservationPlan.Resolve(null!, bytes, EvidenceDigest.Sha256(bytes)));
        Reject(() => EvidenceEmptyObservationPlan.FromInput(null!, CancellationToken.None));
    }

    [Fact]
    public void AbsentOptionalDiffResolvesTheSameValidNoDiffPlan()
    {
        var bytes = EvidenceCanonicalJson.Serialize(Policy());
        var request = Request();
        var optional = EvidenceEmptyObservationPlan.AsOptionalDiff(null);
        Assert.False(optional.HasValue);

        var actual = Resolve(request, bytes, optional);
        var expected = Resolve(request, bytes);
        Assert.Equal("empty", actual.Profile.Id);
        Assert.Equal(expected.PlanDigest, actual.PlanDigest);
        Assert.Equal(expected.PolicyDigest, actual.PolicyDigest);
        Assert.Equal(expected.DiffDigest, actual.DiffDigest);
    }

    [Fact]
    public void PresentEmptyOptionalDiffRemainsPresentAndRejectsBothRequestShapes()
    {
        var bytes = EvidenceCanonicalJson.Serialize(Policy());
        var empty = Array.Empty<byte>();
        var optional = EvidenceEmptyObservationPlan.AsOptionalDiff(empty);
        Assert.True(optional.HasValue);
        Assert.True(optional.GetValueOrDefault().IsEmpty);

        Reject(() => Resolve(Request(), bytes, optional));
        Reject(() => Resolve(Request(diff: empty), bytes, optional));
    }

    [Fact]
    public void PresentOptionalDiffUsesTheDeclaredBytesAndUnchangedPlanDigests()
    {
        var bytes = EvidenceCanonicalJson.Serialize(Policy());
        var diff = Diff("docs/B.md");
        var request = Request(diff: diff);
        var optional = EvidenceEmptyObservationPlan.AsOptionalDiff(diff);
        Assert.True(optional.HasValue);
        Assert.Equal(diff, optional.GetValueOrDefault().ToArray());

        var actual = Resolve(request, bytes, optional);
        var expected = Resolve(request, bytes, diff);
        Assert.Equal(new[] { "docs/A.md", "docs/B.md" }, actual.ChangedPaths.Select(path => path.Path));
        Assert.Equal(expected.PlanDigest, actual.PlanDigest);
        Assert.Equal(expected.PolicyDigest, actual.PolicyDigest);
        Assert.Equal(expected.DiffDigest, actual.DiffDigest);
    }

    private static EvidencePolicy Policy() => new("sample", "1", "conservative",
    [
        new("empty", EvidenceProfileScope.Targeted, [], [], []),
        new("conservative", EvidenceProfileScope.Release, [new("build", "completion", 1, [])], [], []),
    ], [new("docs", "docs/**", "empty")]);

    private static byte[] Diff(string path) => Encoding.UTF8.GetBytes(
        $"diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n@@ -1 +1 @@\n-old\n+new\n");

    private static EvidencePlan Resolve(EvidenceSupervisorRequest request, ReadOnlyMemory<byte> bytes,
        ReadOnlyMemory<byte>? diff = null) =>
        EvidenceEmptyObservationPlan.Resolve(request, bytes, EvidenceDigest.Sha256(bytes.Span), diff);

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
            ["workflow_identity"] = "workflow", ["paths"] = paths ?? ["docs/A.md"],
            ["observation_profile_ids"] = ids ?? ["empty"], ["observation_producer_ids"] = producers ?? [],
            ["job_deadline_utc"] = "2099-01-01T00:00:00Z", ["admission_seconds"] = 1,
            ["start_seconds"] = 1, ["collection_seconds"] = 1, ["cleanup_seconds"] = 2, ["stopping_seconds"] = 1,
            ["diff_file"] = diff is null ? null : "/opt/tool/changes.diff",
            ["diff_sha256"] = diff is null ? null : EvidenceDigest.Sha256(diff), ["solution"] = null,
        };
        return EvidenceSupervisorRequest.Parse(JsonSerializer.SerializeToUtf8Bytes(values));
    }

    private static void Reject(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD406", error.Code);
        Assert.StartsWith("ASEVD406: The protected empty Observation plan could not be validated.", error.Message);
        Assert.DoesNotContain("CANARY", error.Message);
        Assert.Null(error.InnerException);
    }
}
