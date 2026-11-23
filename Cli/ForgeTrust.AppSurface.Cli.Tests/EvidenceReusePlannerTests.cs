using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceReusePlannerTests
{
    [Fact]
    public void Resolve_UsesVersion11OnlyWhenReuseAdditionsArePresent()
    {
        var baseline = new EvidencePlanner().Resolve(CreatePolicy(CreateProducer()), [new NormalizedDiffPath("src/a.cs")]);
        var withInputs = new EvidencePlanner().Resolve(CreatePolicy(CreateProducer() with
        {
            Inputs = new EvidenceProducerInputs(["src/**/*.cs"], ["src/**/Generated/**"]),
        }), [new NormalizedDiffPath("src/a.cs")]);
        var withMultiFileSlot = new EvidencePlanner().Resolve(CreatePolicy(CreateProducer() with
        {
            ArtifactSlots = [new EvidenceArtifactSlot("report", "reports", "text/plain", true, 10) { MaximumFiles = 3 }],
        }), [new NormalizedDiffPath("src/a.cs")]);

        Assert.Equal("1.0", baseline.ContractVersion);
        Assert.Equal("1.1", withInputs.ContractVersion);
        Assert.Equal("1.1", withMultiFileSlot.ContractVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/absolute/*.cs")]
    [InlineData("../outside.cs")]
    [InlineData("src//file.cs")]
    [InlineData("src/**/bad**segment.cs")]
    [InlineData("src\\file.cs")]
    [InlineData("C:/src/*.cs")]
    [InlineData("C:src/*.cs")]
    [InlineData("src/?.cs")]
    [InlineData("src/[ab].cs")]
    [InlineData("src/{a,b}.cs")]
    [InlineData("src/./file.cs")]
    [InlineData("src/file.cs/")]
    [InlineData("src/file.cs\n")]
    [InlineData("   ")]
    public void Resolve_RejectsInvalidInputPatternWithoutFilesystemAccess(string pattern)
    {
        var policy = CreatePolicy(CreateProducer() with { Inputs = new EvidenceProducerInputs([pattern], []) });

        var exception = Assert.Throws<EvidencePlanningException>(() =>
            new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("src/a.cs")]));

        Assert.Equal("ASEVD137", exception.Code);
    }

    [Fact]
    public void Resolve_RejectsMissingIncludesAndInvalidMaximumFileCount()
    {
        var planner = new EvidencePlanner();
        var emptyIncludes = CreatePolicy(CreateProducer() with { Inputs = new EvidenceProducerInputs([], []) });
        var invalidCount = CreatePolicy(CreateProducer() with
        {
            ArtifactSlots = [new EvidenceArtifactSlot("report", "reports", "text/plain", true, 10) { MaximumFiles = 0 }],
        });

        Assert.Equal("ASEVD137", Assert.Throws<EvidencePlanningException>(() => planner.Resolve(emptyIncludes, [new NormalizedDiffPath("src/a.cs")])).Code);
        Assert.Equal("ASEVD126", Assert.Throws<EvidencePlanningException>(() => planner.Resolve(invalidCount, [new NormalizedDiffPath("src/a.cs")])).Code);
    }

    [Theory]
    [InlineData("**")]
    [InlineData("*.cs")]
    [InlineData("src/**/file*.cs")]
    [InlineData("missing-checkout/ literal filename .cs")]
    public void ResolveAcceptsExistingGlobGrammarAndDoesNotRequireFilesToExist(string pattern)
    {
        var producer = CreateProducer() with { Inputs = new EvidenceProducerInputs([pattern], []) };
        var first = new EvidencePlanner().Resolve(CreatePolicy(producer), [new NormalizedDiffPath("src/a.cs")]);
        var second = new EvidencePlanner().Resolve(CreatePolicy(producer), [new NormalizedDiffPath("src/a.cs")]);
        Assert.Equal("1.1", first.ContractVersion);
        Assert.Equal(first.PlanDigest, second.PlanDigest);
        Assert.Equal(pattern, first.Profile.Producers[0].Inputs!.Include[0]);
    }

    [Fact]
    public void ResolveRejectsInvalidExclusionsAndNullLists()
    {
        EvidenceProducerInputs[] declarations = [new(["src/**"], ["../outside"]), new(null!, []), new(["src/**"], null!)];
        foreach (var inputs in declarations)
        {
            var policy = CreatePolicy(CreateProducer() with { Inputs = inputs });
            Assert.Equal("ASEVD137", Assert.Throws<EvidencePlanningException>(() => new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("src/a.cs")])).Code);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(129)]
    public void ResolveRejectsOutOfRangeFileCounts(int count)
    {
        var producer = CreateProducer() with { ArtifactSlots = [new EvidenceArtifactSlot("report", "reports", "text/plain", false, 10) { MaximumFiles = count }] };
        Assert.Equal("ASEVD126", Assert.Throws<EvidencePlanningException>(() => new EvidencePlanner().Resolve(CreatePolicy(producer), [new NormalizedDiffPath("src/a.cs")])).Code);
    }

    [Fact]
    public void ExplicitOneFileAndUnselectedInputsStillRequireVersion11()
    {
        var producer = CreateProducer() with { ArtifactSlots = [new EvidenceArtifactSlot("report", "reports", "text/plain", false, 10) { MaximumFiles = 1 }] };
        Assert.Equal("1.1", new EvidencePlanner().Resolve(CreatePolicy(producer), [new NormalizedDiffPath("src/a.cs")]).ContractVersion);
        var policy = CreatePolicy(CreateProducer());
        policy = policy with
        {
            Profiles = [policy.Profiles[0], new EvidenceProfile("other", EvidenceProfileScope.Targeted, [],
            [CreateProducer() with { Inputs = new EvidenceProducerInputs(["other/**"], []) }], [])]
        };
        var plan = new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("src/a.cs")]);
        Assert.Equal("targeted", plan.Profile.Id);
        Assert.Equal("1.1", plan.ContractVersion);
    }

    [Theory]
    [InlineData(" reports")]
    [InlineData("reports\\nested")]
    [InlineData("reports/CON")]
    public void MultiFileSlotRootMustAlreadyBeCanonicalDuringPlanning(string root)
    {
        var producer = CreateProducer() with { ArtifactSlots = [new EvidenceArtifactSlot("report", root, "text/plain", false, 10) { MaximumFiles = 2 }] };
        Assert.Equal("ASEVD126", Assert.Throws<EvidencePlanningException>(() => new EvidencePlanner().Resolve(CreatePolicy(producer), [new NormalizedDiffPath("src/a.cs")])).Code);
    }

    private static EvidencePolicy CreatePolicy(EvidenceProducerDeclaration producer)
    {
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [], [producer], []);
        return new EvidencePolicy("policy", "1", profile.Id, [profile], []);
    }

    private static EvidenceProducerDeclaration CreateProducer()
        => new("producer", "sample", "1", [], [], [], 30);
}
