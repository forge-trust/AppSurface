using ForgeTrust.AppSurface.Config;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class EnvironmentConfigCandidatePlanTests
{
    private static readonly AppSurfaceConfigKey Key = AppSurfaceConfigKey.Parse("A:B");
    private static EnvironmentNativeCandidate Item(string name) => new(name, EnvironmentCandidateScope.Scoped, false, false);

    [Fact]
    // protects=retained native plans own immutable lists; fails_when=constructor or init retains a mutable caller collection; no_claim=live names and policy.
    public void ConstructorAndWithAssignmentCopyCallerLists()
    {
        var source = new List<EnvironmentNativeCandidate> { Item("one") };
        var plan = new EnvironmentNativeKeyPlan(Key, source);
        source.Add(Item("two"));
        Assert.Single(plan.Candidates);
        var replacement = new List<EnvironmentNativeCandidate> { Item("three") };
        var changed = plan with { Candidates = replacement };
        replacement.Clear();
        Assert.Equal("three", Assert.Single(changed.Candidates).Name);
        Assert.Equal("one", Assert.Single((plan with { }).Candidates).Name);
    }

    [Fact]
    // protects=generated copy and deconstruction preserve immutable state; fails_when=copy loses names or key; no_claim=live variables.
    public void DeconstructionAndPlainCopyRetainImmutableState()
    {
        var plan = new EnvironmentNativeKeyPlan(Key, [Item("one")]);
        var copy = plan with { };
        var (key, candidates) = copy;
        Assert.Same(Key, key);
        Assert.Equal("one", Assert.Single(candidates).Name);
        Assert.Equal("one", Assert.Single(plan.Candidates).Name);
    }

    [Fact]
    // protects=native DTO validation; fails_when=null enters the immutable result; no_claim=candidate name validity.
    public void NullKeysListsAndEntriesAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new EnvironmentNativeKeyPlan(null!, []));
        Assert.Throws<ArgumentNullException>(() => new EnvironmentCompositionPathPlan(null!, []));
        Assert.Throws<ArgumentNullException>(() => new EnvironmentNativeKeyPlan(Key, null!));
        Assert.Throws<ArgumentException>(() => new EnvironmentNativeKeyPlan(Key, new EnvironmentNativeCandidate[] { null! }));
        var plan = new EnvironmentNativeKeyPlan(Key, []);
        Assert.Throws<ArgumentNullException>(() => plan with { Candidates = null! });
        Assert.Throws<ArgumentNullException>(() => plan with { Key = null! });
    }

    [Fact]
    // protects=same typed exception and safe fields; fails_when=enrichment wraps or loses fields; no_claim=arbitrary application failures.
    public void EnrichmentPreservesOriginalAndAddsSafeFields()
    {
        var original = new FormatException("safe parser text");
        var enriched = EnvironmentConfigCandidateErrors.Enrich(original, "config-key-invalid", "InspectNativeKey",
            "Use strict colon-delimited segments.", "fix_and_retry", EnvironmentConfigCandidateErrors.LogicalKeyGuideUri);
        Assert.Same(original, enriched);
        Assert.IsType<FormatException>(enriched);
        Assert.Equal(original.HResult, enriched.HResult);
        Assert.Same(original.InnerException, enriched.InnerException);
        Assert.Equal("config-key-invalid", enriched.Data["Code"]);
        Assert.Equal("InspectNativeKey", enriched.Data["Operation"]);
        Assert.Equal("fix_and_retry", enriched.Data["Recovery"]);
        Assert.Equal("none", enriched.Data["SideEffects"]);
        Assert.Equal(EnvironmentConfigCandidateErrors.LogicalKeyGuideUri, enriched.Data["HelpUri"]);
        Assert.Equal(EnvironmentConfigCandidateErrors.LogicalKeyGuideUri, enriched.HelpLink);
        Assert.DoesNotContain("sentinel-secret", string.Join(" ", enriched.Data.Values));
    }
    // Protects both immutable DTO construction paths; rejects list aliasing and init bypasses.
    // Does not establish any relationship between supplied names and live environment variables.
    [Fact]
    // protects=composition DTO copying and validation; fails_when=with or constructor shares mutable data; no_claim=composition presence.
    public void CompositionConstructorAssignmentsDeconstructionAndNullsPreserveCopies()
    {
        var source = new List<EnvironmentCompositionPathCandidate>
        {
            new("ONE", EnvironmentCandidateScope.Scoped, false)
        };
        var original = new EnvironmentCompositionPathPlan(Key, source);
        source.Clear();
        var replacement = new List<EnvironmentCompositionPathCandidate>
        {
            new("TWO", EnvironmentCandidateScope.Unscoped, true)
        };
        var changed = original with { Candidates = replacement, Path = AppSurfaceConfigKey.Parse("C") };
        replacement.Clear();
        var (path, candidates) = changed with { };
        Assert.Equal("C", path.ToString());
        Assert.Equal("TWO", Assert.Single(candidates).Name);
        Assert.Equal("ONE", Assert.Single(original.Candidates).Name);
        Assert.Same(Key, original.Path);
        Assert.Empty(new EnvironmentCompositionPathPlan(Key, []).Candidates);
        Assert.Empty(new EnvironmentNativeKeyPlan(Key, []).Candidates);
        Assert.Throws<ArgumentNullException>(() => new EnvironmentCompositionPathPlan(Key, null!));
        Assert.Throws<ArgumentException>(() => new EnvironmentCompositionPathPlan(Key, new EnvironmentCompositionPathCandidate[] { null! }));
        Assert.Throws<ArgumentNullException>(() => original with { Candidates = null! });
        Assert.Throws<ArgumentException>(() => original with { Candidates = new EnvironmentCompositionPathCandidate[] { null! } });
        Assert.Throws<ArgumentNullException>(() => original with { Path = null! });
        var native = new EnvironmentNativeKeyPlan(Key, []);
        Assert.Throws<ArgumentException>(() => native with { Candidates = new EnvironmentNativeCandidate[] { null! } });
        Assert.Equal("C", (native with { Key = path }).Key.ToString());
    }

    // Protects original exception identity and origin frames through the production enrichment seam.
    [Fact]
    // protects=original failure identity cause and origin; fails_when=helper replaces exception or stack; no_claim=unexpected failure redaction.
    public void EnrichmentRetainsThrownObjectInnerCauseAndStack()
    {
        var cause = new InvalidOperationException("retained cause");
        var original = new FormatException("safe text", cause);
        var caught = Assert.Throws<FormatException>(() => ThrowAtOrigin(original));
        var stack = caught.StackTrace;
        var enriched = EnvironmentConfigCandidateErrors.Enrich(caught, "config-key-invalid", "InspectNativeKey",
            "Fix the strict syntax.", "fix_and_retry", EnvironmentConfigCandidateErrors.LogicalKeyGuideUri);
        Assert.Same(original, enriched);
        Assert.Same(cause, enriched.InnerException);
        Assert.Equal(stack, enriched.StackTrace);
        Assert.Contains(nameof(ThrowAtOrigin), enriched.StackTrace);
        Assert.Equal("Fix the strict syntax.", enriched.Data["Fix"]);
        Assert.Throws<ArgumentNullException>(() => EnvironmentConfigCandidateErrors.Enrich<Exception>(null!, "code", "op", "fix", "recovery", "uri"));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowAtOrigin(Exception exception) => throw exception;

}
