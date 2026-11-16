using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceContractEdgeCoverageTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void ArtifactValidationRejectsMissingOrMalformedSha256(string? digest)
    {
        var producer = ProducerWithRequiredArtifact();
        var artifact = new EvidenceArtifactResult("report", "reports/result.txt", "text/plain", 4, digest!);

        Assert.False(EvidenceArtifactValidation.AreValid(producer, [artifact]));
        Assert.True(EvidenceArtifactValidation.AreValid(producer, [artifact with { Sha256 = new string('a', 64) }]));
    }

    [Theory]
    [InlineData("reports-old/result.txt")]
    [InlineData("Reports/result.txt")]
    public void ArtifactSlotValidationRequiresExactOrdinalRootBoundary(string path)
    {
        var producer = ProducerWithRequiredArtifact();
        var artifact = new EvidenceArtifactResult("report", path, "text/plain", 4, new string('a', 64));

        Assert.False(EvidenceArtifactValidation.AreValid(producer, [artifact]));
        Assert.True(EvidenceArtifactValidation.AreValid(producer,
            [artifact with { RelativePath = "reports/result.txt" }]));
    }

    [Fact]
    public void MultiFileArtifactPathsMustAlreadyUseCanonicalPortableSeparators()
    {
        var slot = new EvidenceArtifactSlot("report", "reports", "text/plain", true, 16)
        {
            MaximumFiles = 2,
        };

        EvidenceArtifactValidation.ValidatePathForSlot(slot, "reports/result.txt");
        var noncanonical = Assert.Throws<ArgumentException>(() =>
            EvidenceArtifactValidation.ValidatePathForSlot(slot, "reports\\result.txt"));
        var reservedName = Assert.Throws<ArgumentException>(() =>
            EvidenceArtifactValidation.ValidatePathForSlot(slot, "reports/CON.txt"));

        Assert.Equal("relativePath", noncanonical.ParamName);
        Assert.Contains("canonical and portable", noncanonical.Message, StringComparison.Ordinal);
        Assert.Equal("relativePath", reservedName.ParamName);
        Assert.Contains("canonical and portable", reservedName.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(4, 0)]
    [InlineData(4, 129)]
    public void ArtifactValidationRejectsInvalidDeclaredSlotBounds(int maximumBytes, int? maximumFiles)
    {
        var slot = new EvidenceArtifactSlot("report", "reports", "text/plain", true, maximumBytes)
        {
            MaximumFiles = maximumFiles,
        };
        var producer = new EvidenceProducerDeclaration("p", "kind", "1", [], [], [slot], 30);
        var artifact = new EvidenceArtifactResult("report", "reports/result.txt", "text/plain", 1, new string('a', 64));

        Assert.False(EvidenceArtifactValidation.AreValid(producer, [artifact]));
        Assert.True(EvidenceArtifactValidation.AreValid(ProducerWithRequiredArtifact(),
            [artifact with { LengthBytes = 4 }]));
    }

    [Fact]
    public void ArtifactValidationRejectsMissingRequiredSlotAndUnknownLogicalName()
    {
        var producer = ProducerWithRequiredArtifact();
        var unknown = new EvidenceArtifactResult("other", "reports/result.txt", "text/plain", 4, new string('a', 64));

        Assert.False(EvidenceArtifactValidation.AreValid(producer, []));
        Assert.False(EvidenceArtifactValidation.AreValid(producer, [unknown]));
        Assert.True(EvidenceArtifactValidation.AreValid(producer,
            [unknown with { LogicalName = "report" }]));
    }

    [Fact]
    public void MultiFileSlotRejectsTrimmedNoncanonicalPathAndCaseCollidingDestinations()
    {
        var slot = new EvidenceArtifactSlot("report", "reports", "text/plain", true, 16)
        {
            MaximumFiles = 2,
        };
        var noncanonical = Assert.Throws<ArgumentException>(() =>
            EvidenceArtifactValidation.ValidatePathForSlot(slot, " reports/result.txt"));
        var producer = new EvidenceProducerDeclaration("p", "kind", "1", [], [], [slot], 30);
        var first = new EvidenceArtifactResult("report", "reports/result.txt", "text/plain", 1, new string('a', 64));

        Assert.Equal("relativePath", noncanonical.ParamName);
        Assert.Contains("canonical and portable", noncanonical.Message, StringComparison.Ordinal);
        Assert.False(EvidenceArtifactValidation.AreValid(producer,
            [first, first with { RelativePath = "reports/RESULT.txt" }]));
        Assert.True(EvidenceArtifactValidation.AreValid(producer, [first]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void ArtifactPathNormalizationRejectsNullOrEmptyWhilePreservingCanonicalInput(string? path)
    {
        Assert.Throws<ArgumentException>(() => EvidenceArtifactValidation.NormalizeRelativePath(path!));
        Assert.Equal("reports/result.txt", EvidenceArtifactValidation.NormalizeRelativePath("reports/result.txt"));
    }

    [Fact]
    public void LegacyContractRejectsCooperativeChildLifetimeDeclaration()
    {
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [],
            [new EvidenceProducerDeclaration("p", "kind", "1", [], [], [], 30)
            {
                CoverageExecution = new EvidenceCoverageExecutionOptions { CooperativeChildLifetime = true },
            }], []);
        var plan = CreatePlan(profile, "1.0");
        var manifest = EvidenceManifestBuilder.Build(plan, [new EvidenceProducerResult("p", EvidenceProducerOutcome.Passed, [])]);

        Assert.Equal(EvidenceExecutionVerdict.Invalid, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.False(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.False(EvidenceManifestBuilder.VerifyForGate(plan, manifest));
    }

    [Fact]
    public void FreshResourceClosureVisitsSharedDependencyOnceButStillRequiresIt()
    {
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted,
            [new EvidenceResourceDeclaration("left", "completion", 10, ["shared"]),
             new EvidenceResourceDeclaration("right", "completion", 10, ["shared"]),
             new EvidenceResourceDeclaration("shared", "completion", 10, [])],
            [new EvidenceProducerDeclaration("p", "kind", "1", ["left", "right"], [], [], 30)], []);
        var plan = CreatePlan(profile, "1.1");
        var producerResults = new[] { new EvidenceProducerResult("p", EvidenceProducerOutcome.Passed, []) };
        EvidenceResourceResult[] resources =
        [
            new("left", EvidenceResourceOutcome.Ready, 1),
            new("right", EvidenceResourceOutcome.Ready, 1),
            new("shared", EvidenceResourceOutcome.NotRequiredForExecution, 0),
        ];

        var incomplete = EvidenceManifestBuilder.Build(plan, producerResults, resourceResults: resources);

        Assert.Equal(EvidenceExecutionVerdict.Incomplete, incomplete.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, incomplete.ClaimKind);
        resources[2] = resources[2] with { Outcome = EvidenceResourceOutcome.Ready, ElapsedMilliseconds = 1 };
        var complete = EvidenceManifestBuilder.Build(plan, producerResults, resourceResults: resources);

        Assert.Equal(EvidenceExecutionVerdict.Passed, complete.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.TargetedComplete, complete.ClaimKind);
        Assert.True(EvidenceManifestBuilder.Verify(plan, complete));
        Assert.True(EvidenceManifestBuilder.VerifyForGate(plan, complete));
    }

    [Fact]
    public void ObservationClaimVerifiesButCannotBeAdmittedAsAGateClaim()
    {
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [],
            [new EvidenceProducerDeclaration("p", "kind", "1", [], ["assert"], [], 30)],
            [new EvidenceObligation("o", "risk", "reason", ["p"], "assert")]);
        var plan = CreatePlan(profile, "1.1");
        var manifest = EvidenceManifestBuilder.Build(plan,
            [new EvidenceProducerResult("p", EvidenceProducerOutcome.Passed, ["assert"])], observationOnly: true);

        Assert.Equal(EvidenceExecutionVerdict.Passed, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.ObservationOnly, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.Informational, manifest.Eligibility);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.False(EvidenceManifestBuilder.VerifyForGate(plan, manifest));
    }

    [Fact]
    public void PlannerRejectsNullInputPatternWhileAcceptingItsValidBaseline()
    {
        var producer = new EvidenceProducerDeclaration("p", "kind", "1", [], [], [], 30)
        {
            Inputs = new EvidenceProducerInputs([null!], []),
        };
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [], [producer], []);
        var policy = new EvidencePolicy("policy", "1", profile.Id, [profile], []);
        var planner = new EvidencePlanner();

        var error = Assert.Throws<EvidencePlanningException>(() =>
            planner.Resolve(policy, [new NormalizedDiffPath("src/file.cs")]));

        Assert.Equal("ASEVD137", error.Code);
        var validPolicy = policy with
        {
            Profiles = [profile with
            {
                Producers = [producer with { Inputs = new EvidenceProducerInputs(["src/**/*.cs"], []) }],
            }],
        };
        Assert.Equal("1.1", planner.Resolve(validPolicy, [new NormalizedDiffPath("src/file.cs")]).ContractVersion);
    }

    [Theory]
    [InlineData("PRN.txt")]
    [InlineData("AUX.txt")]
    [InlineData("NUL.txt")]
    [InlineData("LPT1.txt")]
    [InlineData("LPT².txt")]
    [InlineData("COM³.txt")]
    [InlineData("name .txt ")]
    [InlineData("folder /result.txt")]
    [InlineData("line\nbreak.txt")]
    public void PortableSlotsRejectReservedDeviceNamesWhileAllowingSimilarOrdinaryNames(string name)
    {
        var slot = new EvidenceArtifactSlot("report", "reports", "text/plain", false, 16)
        {
            MaximumFiles = 2,
        };

        Assert.Throws<ArgumentException>(() =>
            EvidenceArtifactValidation.ValidatePathForSlot(slot, "reports/" + name));
        EvidenceArtifactValidation.ValidatePathForSlot(slot, "reports/COM0.txt");
        EvidenceArtifactValidation.ValidatePathForSlot(slot, "reports/LPTx.txt");
        EvidenceArtifactValidation.ValidatePathForSlot(slot, "reports/PRNx.txt");
    }

    [Theory]
    [InlineData("key")]
    [InlineData("digest")]
    [InlineData("time")]
    public void LegacyVerifierRejectsFreshResultsWithInjectedReuseProvenanceEvenWhenRehashed(string field)
    {
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [],
            [new EvidenceProducerDeclaration("p", "kind", "1", [], [], [], 30)], []);
        var plan = CreatePlan(profile, "1.0");
        var result = new EvidenceProducerResult("p", EvidenceProducerOutcome.Passed, []);
        var valid = EvidenceManifestBuilder.Build(plan, [result]);
        Assert.True(EvidenceManifestBuilder.Verify(plan, valid));
        Assert.True(EvidenceManifestBuilder.VerifyForGate(plan, valid));
        result = field switch
        {
            "key" => result with { ProducerKey = new string('a', 64) },
            "digest" => result with { OriginManifestDigest = new string('b', 64) },
            _ => result with { OriginCompletedAt = DateTimeOffset.UnixEpoch },
        };
        var injected = valid with { ProducerResults = [result], ManifestDigest = string.Empty };
        injected = injected with { ManifestDigest = EvidenceDigest.CanonicalSha256(injected) };

        Assert.False(EvidenceManifestBuilder.Verify(plan, injected));
        Assert.False(EvidenceManifestBuilder.VerifyForGate(plan, injected));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("uppercase")]
    [InlineData("nonhex")]
    public void PlanVerifierRequiresACanonicalCapturedPatchDigestEvenWhenAllDigestsAreRecomputed(string defect)
    {
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [],
            [new EvidenceProducerDeclaration("p", "kind", "1", [], [], [], 30)], []);
        var plan = BindPatch(CreatePlan(profile, "1.1"), new string('a', 64));
        EvidenceProducerResult[] results = [new("p", EvidenceProducerOutcome.Passed, [])];
        Assert.True(EvidenceManifestBuilder.VerifyForGate(plan, EvidenceManifestBuilder.Build(plan, results)));
        var invalid = defect switch
        {
            "short" => new string('a', 63),
            "uppercase" => new string('A', 64),
            _ => new string('g', 64),
        };
        plan = BindPatch(plan, invalid);
        var manifest = EvidenceManifestBuilder.Build(plan, results);

        Assert.False(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.False(EvidenceManifestBuilder.VerifyForGate(plan, manifest));

        static EvidencePlan BindPatch(EvidencePlan source, string patch)
        {
            var draft = source with { PatchSha256 = patch, PlanDigest = string.Empty };
            return draft with { PlanDigest = EvidenceDigest.CanonicalSha256(draft) };
        }
    }

    [Fact]
    public void MultiFileDeclarationRequiresVersion11EvenWithoutInputOrChildLifetimeAdditions()
    {
        var producer = new EvidenceProducerDeclaration("p", "kind", "1", [], [],
            [new("report", "reports", "text/plain", false, 16) { MaximumFiles = 2 }], 30);
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [], [producer], []);
        EvidenceProducerResult[] results = [new("p", EvidenceProducerOutcome.Passed, [])];
        var current = CreatePlan(profile, "1.1");
        Assert.True(EvidenceManifestBuilder.VerifyForGate(current, EvidenceManifestBuilder.Build(current, results)));

        var legacy = CreatePlan(profile, "1.0");
        var refused = EvidenceManifestBuilder.Build(legacy, results);

        Assert.Equal(EvidenceExecutionVerdict.Invalid, refused.ExecutionVerdict);
        Assert.False(EvidenceManifestBuilder.Verify(legacy, refused));
        Assert.False(EvidenceManifestBuilder.VerifyForGate(legacy, refused));
    }

    [Theory]
    [InlineData("skipped-resource")]
    [InlineData("local-reuse-claim")]
    public void LegacyVerifierRejectsNewResourceOrClaimSemanticsEvenWithARehashedManifest(string defect)
    {
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted,
            [new("resource", "completion", 10, [])],
            [new EvidenceProducerDeclaration("p", "kind", "1", [], [], [], 30)], []);
        var plan = CreatePlan(profile, "1.0");
        var valid = EvidenceManifestBuilder.Build(plan, [new("p", EvidenceProducerOutcome.Passed, [])],
            resourceResults: [new("resource", EvidenceResourceOutcome.Ready, 1)]);
        Assert.True(EvidenceManifestBuilder.VerifyForGate(plan, valid));
        var injected = defect == "skipped-resource"
            ? valid with { ResourceResults = [new("resource", EvidenceResourceOutcome.NotRequiredForExecution, 0)] }
            : valid with { ClaimKind = EvidenceClaimKind.LocalReusedComplete };
        injected = injected with { ManifestDigest = string.Empty };
        injected = injected with { ManifestDigest = EvidenceDigest.CanonicalSha256(injected) };

        Assert.False(EvidenceManifestBuilder.Verify(plan, injected));
        Assert.False(EvidenceManifestBuilder.VerifyForGate(plan, injected));
    }

    [Theory]
    [InlineData("plan-version")]
    [InlineData("manifest-version")]
    [InlineData("missing-policy")]
    public void GateRejectsUnsupportedOrUnboundContractInputs(string defect)
    {
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [],
            [new EvidenceProducerDeclaration("p", "kind", "1", [], [], [], 30)], []);
        var plan = CreatePlan(profile, "1.1");
        var manifest = EvidenceManifestBuilder.Build(plan, [new("p", EvidenceProducerOutcome.Passed, [])]);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.True(EvidenceManifestBuilder.VerifyForGate(plan, manifest));
        if (defect == "plan-version") plan = plan with { ContractVersion = "9.0" };
        else if (defect == "manifest-version") manifest = manifest with { ContractVersion = "1.0" };
        else plan = plan with { PolicySnapshot = null };

        Assert.False(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.False(EvidenceManifestBuilder.VerifyForGate(plan, manifest));
    }

    private static EvidenceProducerDeclaration ProducerWithRequiredArtifact() =>
        new("p", "kind", "1", [], [], [new EvidenceArtifactSlot("report", "reports", "text/plain", true, 16)], 30);

    private static EvidencePlan CreatePlan(EvidenceProfile profile, string version)
    {
        var policy = new EvidencePolicy("policy", "1", profile.Id, [profile], []);
        var paths = Array.Empty<NormalizedDiffPath>();
        var plan = new EvidencePlan(version, policy.Id, EvidenceDigest.CanonicalSha256(policy),
            EvidenceDigest.CanonicalSha256(paths), profile, paths, [], string.Empty, policy);
        return plan with { PlanDigest = EvidenceDigest.CanonicalSha256(plan) };
    }
}
