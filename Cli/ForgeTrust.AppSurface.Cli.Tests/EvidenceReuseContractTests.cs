using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceReuseContractTests
{
    [Fact]
    public void ContainedPathCanResolveAnArtifactBeforeItExists()
    {
        using var directory = TempDirectory.Create();
        var expected = Path.Join(directory.Path, "reports", "result.txt");

        var actual = EvidenceArtifactValidation.GetContainedPath(directory.Path, "reports/result.txt");

        Assert.Equal(expected, actual);
        Assert.False(File.Exists(expected));
    }

    public enum ConsumerEvidenceState
    {
        Ready,
        Waiting,
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"evidence-reuse-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, "reports/result.txt", true)]
    [InlineData(false, "reports\\result.txt", true)]
    [InlineData(true, "reports/result.txt", true)]
    [InlineData(true, "reports\\result.txt", false)]
    public void ArtifactPathValidationPreservesLegacyNormalization(bool multiFile, string path, bool expected)
    {
        var slot = new EvidenceArtifactSlot("report", "reports", "text/plain", true, 32)
        {
            MaximumFiles = multiFile ? 2 : null
        };
        var producer = new EvidenceProducerDeclaration("p", "kind", "1", [], [], [slot], 30);
        var artifact = new EvidenceArtifactResult("report", path, "text/plain", 1, new string('a', 64));

        Assert.Equal(expected, EvidenceArtifactValidation.AreValid(producer, [artifact]));
    }

    [Fact]
    public void LegacyConstructorsAndAbsentReuseFieldsKeepLegacyJsonShape()
    {
        var declaration = new EvidenceProducerDeclaration("p", "kind", "1", [], [],
            [new EvidenceArtifactSlot("report", "reports", "text/plain", true, 32)], 30);
        var result = new EvidenceProducerResult("p", EvidenceProducerOutcome.Passed, [], null, [], 3);
        var manifest = new EvidenceManifest("1.0", "plan", EvidenceExecutionVerdict.Passed,
            EvidenceClaimKind.TargetedComplete, EvidenceClaimEligibility.PullRequestGate,
            EvidenceEnvelopeStatus.NotRequired, [], [], [], [], [result], new EvidenceExecutionMetrics(), "digest");

        Assert.Null(declaration.Inputs);
        Assert.Null(declaration.ArtifactSlots[0].MaximumFiles);
        Assert.Null(result.ProducerKey);
        Assert.Equal(0, manifest.ReuseCount);
        Assert.DoesNotContain("inputs", System.Text.Encoding.UTF8.GetString(EvidenceCanonicalJson.Serialize(declaration)), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("maximumFiles", System.Text.Encoding.UTF8.GetString(EvidenceCanonicalJson.Serialize(declaration)), StringComparison.Ordinal);
        Assert.DoesNotContain("producerKey", System.Text.Encoding.UTF8.GetString(EvidenceCanonicalJson.Serialize(result)), StringComparison.Ordinal);
        Assert.DoesNotContain("reuseCount", System.Text.Encoding.UTF8.GetString(EvidenceCanonicalJson.Serialize(manifest)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyByteWriterRetainsOverwriteBehaviorAcrossFreshWriters()
    {
        using var directory = TempDirectory.Create();
        var producer = new EvidenceProducerDeclaration("p", "kind", "1", [], [],
            [new EvidenceArtifactSlot("report", "reports", "text/plain", true, 32)], 30);
        var first = new EvidenceArtifactWriter(producer, directory.Path);
        await first.WriteAsync("report", "reports/result.txt", "first"u8.ToArray());
        var second = new EvidenceArtifactWriter(producer, directory.Path);

        await second.WriteAsync("report", "reports/result.txt", "second"u8.ToArray());

        Assert.Equal("second", await File.ReadAllTextAsync(Path.Join(directory.Path, "reports", "result.txt")));
        Assert.True(await second.VerifyWrittenArtifactsAsync());
        Assert.True(EvidenceArtifactValidation.AreValid(producer, second.WrittenArtifacts));
    }

    [Fact]
    public void LegacySingleFileValidationKeepsOriginalPathAndCaseRules()
    {
        var slots = new EvidenceArtifactSlot[] { new("a", "reports", "text/plain", true, 32), new("b", "reports", "text/plain", true, 32) };
        var producer = new EvidenceProducerDeclaration("p", "kind", "1", [], [], slots, 30);
        var artifacts = new EvidenceArtifactResult[]
        {
            new("a", "reports/Report.txt", "text/plain", 1, new string('a', 64)),
            new("b", "reports/report.txt", "text/plain", 1, new string('b', 64)),
        };

        Assert.True(EvidenceArtifactValidation.AreValid(producer, artifacts));
        Assert.Equal("reports/time:stamp.txt", EvidenceArtifactValidation.NormalizeRelativePath(" reports/time:stamp.txt "));
        Assert.False(EvidenceArtifactValidation.AreValid(producer with
        {
            ArtifactSlots = slots.Select(slot => slot with { MaximumFiles = 2 }).ToArray(),
        }, artifacts));
        Assert.Throws<ArgumentException>(() => EvidenceArtifactValidation.ValidatePathForSlot(slots[0] with { MaximumFiles = 2 }, "reports/time:stamp.txt"));
    }

    [Fact]
    public async Task MultiFileSlotEnforcesCountAndAggregateBytesAndReturnsStableOrder()
    {
        using var directory = TempDirectory.Create();
        var producer = new EvidenceProducerDeclaration("p", "kind", "1", [], [],
            [new EvidenceArtifactSlot("report", "reports", "text/plain", true, 5) { MaximumFiles = 2 }], 30);
        var writer = new EvidenceArtifactWriter(producer, directory.Path);

        await writer.WriteAsync("report", "reports/z.txt", new byte[] { 1, 2 });
        await writer.WriteAsync("report", "reports/a.txt", new byte[] { 3, 4, 5 });
        var sorted = writer.WrittenArtifacts;

        Assert.Equal(["reports/a.txt", "reports/z.txt"], sorted.Select(static artifact => artifact.RelativePath));
        Assert.True(EvidenceArtifactValidation.AreValid(producer, sorted));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync("report", "reports/third.txt", new byte[] { 6 }).AsTask());
        Assert.False(EvidenceArtifactValidation.AreValid(producer, sorted.Append(sorted[0]).ToArray()));
    }

    [Fact]
    public void ReusedResultClosesCurrentObligationButIsInformationalAndCannotPassGateAdmission()
    {
        var producer = new EvidenceProducerDeclaration("p", "kind", "1", [], ["assert"], [], 30);
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [
            new EvidenceResourceDeclaration("unused", "completion", 10, [])], [producer], [
            new EvidenceObligation("obligation", "risk", "reason", ["p"], "assert")]);
        var plan = CreatePlan(profile, "1.1");
        var reused = new EvidenceProducerResult("p", EvidenceProducerOutcome.Reused, ["assert"], Artifacts: [], ElapsedMilliseconds: 0)
        {
            ProducerKey = new string('c', 64),
            OriginManifestDigest = new string('a', 64),
            OriginCompletedAt = DateTimeOffset.Parse("2026-11-14T12:00:00Z"),
        };
        var manifest = EvidenceManifestBuilder.Build(plan, [reused], resourceResults: [
            new EvidenceResourceResult("unused", EvidenceResourceOutcome.NotRequiredForExecution, 0)]);

        Assert.Equal(EvidenceExecutionVerdict.Passed, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.LocalReusedComplete, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.Informational, manifest.Eligibility);
        Assert.Equal(1, manifest.ReuseCount);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.False(EvidenceManifestBuilder.VerifyForGate(plan, manifest));
        Assert.False(EvidenceManifestBuilder.Verify(plan, manifest with { ContractVersion = "9.0" }));
        var forgedGateClaim = manifest with
        {
            ClaimKind = EvidenceClaimKind.TargetedComplete,
            Eligibility = EvidenceClaimEligibility.PullRequestGate,
            ManifestDigest = string.Empty,
        };
        forgedGateClaim = forgedGateClaim with { ManifestDigest = EvidenceDigest.CanonicalSha256(forgedGateClaim) };
        Assert.False(EvidenceManifestBuilder.VerifyForGate(plan, forgedGateClaim));
    }

    [Fact]
    public void ReusedResultRequiresProvenanceAndCannotSkipResourceNeededByFreshProducer()
    {
        var reused = new EvidenceProducerResult("p", EvidenceProducerOutcome.Reused, ["assert"]);
        var plan = CreatePlan(new EvidenceProfile("targeted", EvidenceProfileScope.Targeted,
            [new EvidenceResourceDeclaration("r", "completion", 10, [])],
            [new EvidenceProducerDeclaration("p", "kind", "1", [], ["assert"], [], 30),
             new EvidenceProducerDeclaration("fresh", "kind", "1", ["r"], [], [], 30)],
            [new EvidenceObligation("o", "risk", "reason", ["p"], "assert")]), "1.1");

        var invalidReuse = EvidenceManifestBuilder.Build(plan, [reused], resourceResults: [new EvidenceResourceResult("r", EvidenceResourceOutcome.Ready, 1)]);
        Assert.Equal(EvidenceExecutionVerdict.Invalid, invalidReuse.ExecutionVerdict);
        var freshResourceSkipped = EvidenceManifestBuilder.Build(plan, [reused with
        {
            ProducerKey = new string('c', 64), OriginManifestDigest = new string('b', 64), OriginCompletedAt = DateTimeOffset.UtcNow,
        }, new EvidenceProducerResult("fresh", EvidenceProducerOutcome.Passed, [])], resourceResults: [new EvidenceResourceResult("r", EvidenceResourceOutcome.NotRequiredForExecution, 0)]);
        Assert.Equal(EvidenceExecutionVerdict.Incomplete, freshResourceSkipped.ExecutionVerdict);
    }

    [Fact]
    public void LegacyCanonicalBytesAndDigestsMatchUnchangedHeadSource()
    {
        // Captured from HEAD's Contracts sources, before the 1.1 additions.
        var producer = new EvidenceProducerDeclaration("p", "kind", "1", [], ["assert"], [], 30);
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [], [producer],
            [new EvidenceObligation("o", "risk", "reason", ["p"], "assert")]);
        var plan = CreatePlan(profile, "1.0");
        var manifest = EvidenceManifestBuilder.Build(plan, [new EvidenceProducerResult("p", EvidenceProducerOutcome.Passed, ["assert"])]);
        object[] values = [producer, new EvidenceArtifactSlot("report", "reports", "text/plain", true, 32),
            new EvidenceExecutionMetrics(), plan.PolicySnapshot!, plan, manifest];
        string[] expected =
        [
            """{"ArtifactSlots":[],"AssertionIds":["assert"],"CoverageArtifactRetention":"managed","CoverageGate":null,"Id":"p","Kind":"kind","RequiredResources":[],"TimeoutSeconds":30,"Version":"1"}""",
            """{"LogicalName":"report","MaximumBytes":32,"MediaType":"text/plain","RelativeRoot":"reports","Required":true}""",
            """{"CleanupCompleted":true,"CleanupDiagnostic":null,"CleanupMilliseconds":0,"PlanningMilliseconds":0,"ProducerMilliseconds":0,"ResourceReadinessMilliseconds":0,"TotalMilliseconds":0}""",
            """{"ConservativeProfileId":"targeted","Id":"policy","Profiles":[{"Id":"targeted","Obligations":[{"Id":"o","Rationale":"reason","RequiredAssertionId":"assert","RequiredProducerIds":["p"],"RiskClass":"risk"}],"Producers":[{"ArtifactSlots":[],"AssertionIds":["assert"],"CoverageArtifactRetention":"managed","CoverageGate":null,"Id":"p","Kind":"kind","RequiredResources":[],"TimeoutSeconds":30,"Version":"1"}],"Resources":[],"Scope":"Targeted"}],"Rules":[],"Version":"1"}""",
            """{"ChangedPaths":[],"ContractVersion":"1.0","DiffDigest":"4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945","MatchedRuleIds":[],"PlanDigest":"d2f70add1a7a31977f6d43c3950898aecc4492cc1319677b751bac44d8b5e262","PolicyDigest":"5799f1e77608815b2a65412c3c541672a3801d214975f3fe114e440d506b8bc5","PolicyId":"policy","PolicySnapshot":{"ConservativeProfileId":"targeted","Id":"policy","Profiles":[{"Id":"targeted","Obligations":[{"Id":"o","Rationale":"reason","RequiredAssertionId":"assert","RequiredProducerIds":["p"],"RiskClass":"risk"}],"Producers":[{"ArtifactSlots":[],"AssertionIds":["assert"],"CoverageArtifactRetention":"managed","CoverageGate":null,"Id":"p","Kind":"kind","RequiredResources":[],"TimeoutSeconds":30,"Version":"1"}],"Resources":[],"Scope":"Targeted"}],"Rules":[],"Version":"1"},"Profile":{"Id":"targeted","Obligations":[{"Id":"o","Rationale":"reason","RequiredAssertionId":"assert","RequiredProducerIds":["p"],"RiskClass":"risk"}],"Producers":[{"ArtifactSlots":[],"AssertionIds":["assert"],"CoverageArtifactRetention":"managed","CoverageGate":null,"Id":"p","Kind":"kind","RequiredResources":[],"TimeoutSeconds":30,"Version":"1"}],"Resources":[],"Scope":"Targeted"}}""",
            """{"ClaimKind":"TargetedComplete","ClosedObligationIds":["o"],"ContractVersion":"1.0","Eligibility":"PullRequestGate","EnvelopeStatus":"NotRequired","ExecutionVerdict":"Passed","ManifestDigest":"2b23e071bdb0b32c61d5283f25179484c6edb97746d176d7194f7a23717b8aee","Metrics":{"CleanupCompleted":true,"CleanupDiagnostic":null,"CleanupMilliseconds":0,"PlanningMilliseconds":0,"ProducerMilliseconds":0,"ResourceReadinessMilliseconds":0,"TotalMilliseconds":0},"PlanDigest":"d2f70add1a7a31977f6d43c3950898aecc4492cc1319677b751bac44d8b5e262","ProducerResults":[{"Artifacts":null,"Diagnostic":null,"ElapsedMilliseconds":0,"Outcome":"Passed","ProducerId":"p","SatisfiedAssertionIds":["assert"]}],"ResourceResults":[],"SelectedObligationIds":["o"],"UnmediatedObligationIds":[]}""",
        ];
        string[] digests =
        [
            "03bb6bc8b7ec93bb7ea2a73546a2496df1d60b7ec679b8e38449d9194551ee34",
            "a9b929bd5e73e9c150c4bba8f5c989e316e002c930bce1f723161de478499cdf",
            "9d9cc6d310429bbea5f398f0585e1f97bde61fd602b00c4ae6364bc29542c6bd",
            "5799f1e77608815b2a65412c3c541672a3801d214975f3fe114e440d506b8bc5",
            "54d8ce16af39611c144fba52db7350a881197723c0a9f287d5586294e8d6f3ca",
            "1cfd7dde4c65d5d8144a6f7d003ba02e09659b7acb5891cb3fcce771707ef57a",
        ];
        for (var i = 0; i < values.Length; i++)
        {
            Assert.Equal(expected[i], Encoding.UTF8.GetString(EvidenceCanonicalJson.Serialize(values[i])));
            Assert.Equal(digests[i], EvidenceDigest.CanonicalSha256(values[i]));
        }
        Assert.True(EvidenceManifestBuilder.VerifyForGate(plan, manifest));
    }

    [Fact]
    public void OptionalMetricsRoundTripAndLeaveZeroFieldsAbsent()
    {
        var metrics = new EvidenceExecutionMetrics
        {
            PreparationMilliseconds = 10,
            ReceiptWaitMilliseconds = 20,
            ArtifactRestoreMilliseconds = 30,
        };
        var json = EvidenceCanonicalJson.Serialize(metrics);
        Assert.Equal(metrics, EvidenceCanonicalJson.Deserialize<EvidenceExecutionMetrics>(json));
        Assert.Contains("\"PreparationMilliseconds\":10", Encoding.UTF8.GetString(json), StringComparison.Ordinal);
        Assert.DoesNotContain("PreparationMilliseconds", Encoding.UTF8.GetString(EvidenceCanonicalJson.Serialize(new EvidenceExecutionMetrics())), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("preparation")]
    [InlineData("wait")]
    [InlineData("restore")]
    public void NegativeCurrentReuseTimingsCannotProduceOrVerifyAGateClaim(string metric)
    {
        var producer = new EvidenceProducerDeclaration("p", "kind", "1", [], ["assert"], [], 30);
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [], [producer],
            [new EvidenceObligation("o", "risk", "reason", ["p"], "assert")]);
        var plan = CreatePlan(profile, "1.1");
        var results = new[] { new EvidenceProducerResult("p", EvidenceProducerOutcome.Passed, ["assert"]) };
        var metrics = new EvidenceExecutionMetrics
        {
            PreparationMilliseconds = metric == "preparation" ? -1 : 0,
            ReceiptWaitMilliseconds = metric == "wait" ? -1 : 0,
            ArtifactRestoreMilliseconds = metric == "restore" ? -1 : 0,
        };

        var invalid = EvidenceManifestBuilder.Build(plan, results, metrics: metrics);
        Assert.Equal(EvidenceExecutionVerdict.Invalid, invalid.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, invalid.ClaimKind);
        Assert.False(EvidenceManifestBuilder.VerifyForGate(plan, invalid));

        var valid = EvidenceManifestBuilder.Build(plan, results);
        var inconsistent = valid with { Metrics = metrics, ManifestDigest = string.Empty };
        inconsistent = inconsistent with { ManifestDigest = EvidenceDigest.CanonicalSha256(inconsistent) };
        Assert.False(EvidenceManifestBuilder.Verify(plan, inconsistent));
    }

    [Fact]
    public void GenericCanonicalJsonPreservesConsumerEnumBytesRoundTripAndHash()
    {
        const ConsumerEvidenceState value = ConsumerEvidenceState.Waiting;
        var legacyBytes = "\"Waiting\""u8.ToArray();

        Assert.Equal(legacyBytes, EvidenceCanonicalJson.Serialize(value));
        Assert.Equal(value, EvidenceCanonicalJson.Deserialize<ConsumerEvidenceState>(legacyBytes));
        Assert.Equal(EvidenceDigest.Sha256(legacyBytes), EvidenceDigest.CanonicalSha256(value));
    }

    [Fact]
    public void PolicyReaderRetainsDefinedNumericEnumCompatibility()
    {
        const string policyJson = """{"Id":"policy","Version":"1","ConservativeProfileId":"targeted","Profiles":[{"Id":"targeted","Scope":0,"Resources":[],"Producers":[],"Obligations":[]}],"Rules":[]}""";

        var policy = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(Encoding.UTF8.GetBytes(policyJson));

        Assert.Equal(EvidenceProfileScope.Targeted, policy.Profiles[0].Scope);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.1")]
    public void PlanAndManifestReadersRequireDefinedNamedEnums(string version)
    {
        var producer = new EvidenceProducerDeclaration("p", "kind", "1", ["r"], ["assert"], [], 30)
        {
            CoverageExecution = new EvidenceCoverageExecutionOptions(TestResults: EvidenceCoverageTestResultFormat.Junit),
        };
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted,
            [new EvidenceResourceDeclaration("r", "completion", 10, [])], [producer],
            [new EvidenceObligation("o", "risk", "reason", ["p"], "assert")]);
        var plan = CreatePlan(profile, version);
        var manifest = EvidenceManifestBuilder.Build(plan, [new EvidenceProducerResult("p", EvidenceProducerOutcome.Passed, ["assert"])],
            resourceResults: [new EvidenceResourceResult("r", EvidenceResourceOutcome.Ready, 1)]);
        var planJson = EvidenceCanonicalJson.Serialize(plan);
        var manifestJson = EvidenceCanonicalJson.Serialize(manifest);

        var planRead = EvidenceCanonicalJson.Deserialize<EvidencePlan>(planJson);
        var manifestRead = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(manifestJson);
        Assert.Equal(EvidenceProfileScope.Targeted, planRead.Profile.Scope);
        Assert.Equal(EvidenceCoverageTestResultFormat.Junit, planRead.Profile.Producers[0].CoverageExecution!.TestResults);
        Assert.Equal(EvidenceExecutionVerdict.Passed, manifestRead.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.TargetedComplete, manifestRead.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.PullRequestGate, manifestRead.Eligibility);
        Assert.Equal(EvidenceEnvelopeStatus.NotRequired, manifestRead.EnvelopeStatus);
        Assert.Equal(EvidenceProducerOutcome.Passed, manifestRead.ProducerResults[0].Outcome);
        Assert.Equal(EvidenceResourceOutcome.Ready, manifestRead.ResourceResults[0].Outcome);

        foreach (var numericToken in new[] { "0", "\"0\"" })
        {
            Assert.Throws<JsonException>(() => EvidenceCanonicalJson.Deserialize<EvidencePlan>(
                ReplaceEnumToken(planJson, "Scope", "Targeted", numericToken)));
            Assert.Throws<JsonException>(() => EvidenceCanonicalJson.Deserialize<EvidencePlan>(
                ReplaceEnumToken(planJson, "TestResults", "Junit", numericToken)));
            Assert.Throws<JsonException>(() => EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
                ReplaceEnumToken(manifestJson, "ExecutionVerdict", "Passed", numericToken)));
            Assert.Throws<JsonException>(() => EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
                ReplaceEnumToken(manifestJson, "ClaimKind", "TargetedComplete", numericToken)));
            Assert.Throws<JsonException>(() => EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
                ReplaceEnumToken(manifestJson, "Eligibility", "PullRequestGate", numericToken)));
            Assert.Throws<JsonException>(() => EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
                ReplaceEnumToken(manifestJson, "EnvelopeStatus", "NotRequired", numericToken)));
            Assert.Throws<JsonException>(() => EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
                ReplaceEnumToken(manifestJson, "Outcome", "Passed", numericToken)));
            Assert.Throws<JsonException>(() => EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
                ReplaceEnumToken(manifestJson, "Outcome", "Ready", numericToken)));
        }

        foreach (var nonName in new[] { "\"Unknown\"", "\"Passed, Reused\"" })
        {
            Assert.Throws<JsonException>(() => EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
                ReplaceEnumToken(manifestJson, "Outcome", "Passed", nonName)));
        }
    }

    [Theory]
    [InlineData("9.0")]
    [InlineData("1")]
    [InlineData("")]
    public void ReaderAndVerifierRejectUnknownContractVersionsEvenWithRecomputedDigests(string version)
    {
        var plan = CreatePlan(new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [], [], []), version);
        var manifest = EvidenceManifestBuilder.Build(plan, []);
        Assert.Equal(EvidenceExecutionVerdict.Invalid, manifest.ExecutionVerdict);
        Assert.False(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.False(EvidenceManifestBuilder.VerifyForGate(plan, manifest));
        Assert.Throws<JsonException>(() => EvidenceCanonicalJson.Deserialize<EvidencePlan>(EvidenceCanonicalJson.Serialize(plan)));
        Assert.Throws<JsonException>(() => EvidenceCanonicalJson.Deserialize<EvidenceManifest>(EvidenceCanonicalJson.Serialize(manifest)));
    }

    [Theory]
    [InlineData("key")]
    [InlineData("digest")]
    [InlineData("time")]
    [InlineData("elapsed")]
    public void InvalidReuseProvenanceCannotCloseAnObligation(string defect)
    {
        var result = ValidReusedResult();
        result = defect switch
        {
            "key" => result with { ProducerKey = "not-a-key" },
            "digest" => result with { OriginManifestDigest = new string('A', 64) },
            "time" => result with { OriginCompletedAt = default(DateTimeOffset) },
            _ => result with { ElapsedMilliseconds = 1 },
        };
        var manifest = EvidenceManifestBuilder.Build(ReusePlan(), [result]);
        Assert.Equal(EvidenceExecutionVerdict.Invalid, manifest.ExecutionVerdict);
        Assert.Empty(manifest.ClosedObligationIds);
        Assert.Equal(["o"], manifest.UnmediatedObligationIds);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.False(EvidenceManifestBuilder.VerifyForGate(ReusePlan(), manifest));
    }

    [Fact]
    public void ReaderRejectsForgedReuseCountAndFreshResultProvenanceIsInvalid()
    {
        var plan = ReusePlan();
        var result = ValidReusedResult();
        var manifest = EvidenceManifestBuilder.Build(plan, [result]);
        var json = Encoding.UTF8.GetString(EvidenceCanonicalJson.Serialize(manifest));
        var read = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(Encoding.UTF8.GetBytes(json));
        Assert.Equal(1, read.ReuseCount);
        Assert.True(EvidenceManifestBuilder.Verify(plan, read));
        Assert.Throws<JsonException>(() => EvidenceCanonicalJson.Deserialize<EvidenceManifest>(Encoding.UTF8.GetBytes(json.Replace("\"ReuseCount\":1", "\"ReuseCount\":0", StringComparison.Ordinal))));
        Assert.Throws<JsonException>(() => EvidenceCanonicalJson.Deserialize<EvidenceManifest>(Encoding.UTF8.GetBytes(json.Replace("\"ReuseCount\":1", "\"ReuseCount\":\"1\"", StringComparison.Ordinal))));
        Assert.Equal(EvidenceExecutionVerdict.Invalid, EvidenceManifestBuilder.Build(plan, [result with { Outcome = EvidenceProducerOutcome.Passed }]).ExecutionVerdict);
    }

    [Fact]
    public void FreshTransitiveResourceClosureCannotBeSkippedInMixedRun()
    {
        var basePlan = ReusePlan();
        var profile = basePlan.Profile with
        {
            Resources = [new EvidenceResourceDeclaration("parent", "completion", 10, []),
                new EvidenceResourceDeclaration("child", "completion", 10, ["parent"]),
                new EvidenceResourceDeclaration("unused", "completion", 10, [])],
            Producers = [basePlan.Profile.Producers[0], new EvidenceProducerDeclaration("fresh", "kind", "1", ["child"], [], [], 30)],
        };
        var plan = CreatePlan(profile, "1.1");
        EvidenceProducerResult[] results = [ValidReusedResult(), new("fresh", EvidenceProducerOutcome.Passed, [])];
        EvidenceResourceResult[] resources = [new("parent", EvidenceResourceOutcome.NotRequiredForExecution, 0),
            new("child", EvidenceResourceOutcome.Ready, 1), new("unused", EvidenceResourceOutcome.NotRequiredForExecution, 0)];
        Assert.Equal(EvidenceExecutionVerdict.Incomplete, EvidenceManifestBuilder.Build(plan, results, resourceResults: resources).ExecutionVerdict);
        resources[0] = resources[0] with { Outcome = EvidenceResourceOutcome.Ready };
        var complete = EvidenceManifestBuilder.Build(plan, results, resourceResults: resources);
        Assert.Equal(EvidenceClaimKind.LocalReusedComplete, complete.ClaimKind);
        Assert.True(EvidenceManifestBuilder.Verify(plan, complete));
        Assert.False(EvidenceManifestBuilder.VerifyForGate(plan, complete));
    }

    [Fact]
    public void LegacyVersionCannotAdmitReuseOrMultiFileDeclarations()
    {
        var plan = ReusePlan() with { ContractVersion = "1.0", PlanDigest = string.Empty };
        plan = plan with { PlanDigest = EvidenceDigest.CanonicalSha256(plan) };
        var manifest = EvidenceManifestBuilder.Build(plan, [ValidReusedResult()]);
        Assert.Equal(EvidenceExecutionVerdict.Invalid, manifest.ExecutionVerdict);
        Assert.False(EvidenceManifestBuilder.Verify(plan, manifest));
        var profile = plan.Profile with { Producers = [plan.Profile.Producers[0] with { Inputs = new EvidenceProducerInputs(["**"], []) }] };
        var legacy = CreatePlan(profile, "1.0");
        var fresh = EvidenceManifestBuilder.Build(legacy, [new EvidenceProducerResult("p", EvidenceProducerOutcome.Passed, ["assert"])]);
        Assert.Equal(EvidenceExecutionVerdict.Invalid, fresh.ExecutionVerdict);
        Assert.False(EvidenceManifestBuilder.Verify(legacy, fresh));
    }

    [Fact]
    public void LegacyVersionRefusesCooperativeLifetimeWithoutAnInputDeclaration()
    {
        var profile = ReusePlan().Profile;
        var producer = profile.Producers[0] with
        {
            Inputs = null,
            CoverageExecution = new EvidenceCoverageExecutionOptions { CooperativeChildLifetime = true },
        };
        var legacy = CreatePlan(profile with { Producers = [producer] }, "1.0");
        var manifest = EvidenceManifestBuilder.Build(legacy,
            [new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Passed, producer.AssertionIds)]);

        Assert.Equal(EvidenceExecutionVerdict.Invalid, manifest.ExecutionVerdict);
        Assert.False(EvidenceManifestBuilder.Verify(legacy, manifest));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultiFileAggregateFailureReleasesOnlyFailedReservationAndAllowsRetry(bool stream)
    {
        using var directory = TempDirectory.Create();
        var producer = new EvidenceProducerDeclaration("p", "kind", "1", [], [],
            [new EvidenceArtifactSlot("report", "reports", "text/plain", true, 4) { MaximumFiles = 2 }], 30);
        var writer = new EvidenceArtifactWriter(producer, directory.Path);
        await writer.WriteAsync("report", "reports/first", new byte[] { 1, 2 });
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (stream)
            {
                using var contents = new MemoryStream([3, 4, 5]);
                await writer.WriteAsync("report", "reports/second", contents);
            }
            else await writer.WriteAsync("report", "reports/second", new byte[] { 3, 4, 5 });
        });
        Assert.False(File.Exists(Path.Join(directory.Path, "reports/second")));
        await writer.WriteAsync("report", "reports/second", new byte[] { 3, 4 });
        Assert.Equal(2, writer.WrittenArtifacts.Count);
        Assert.True(EvidenceArtifactValidation.AreValid(producer, writer.WrittenArtifacts));
        Assert.True(await writer.VerifyWrittenArtifactsAsync());
    }

    [Fact]
    public async Task NewMemoryWriterPreservesExistingDestinationAndLegacySlotRemainsSingleFile()
    {
        using var directory = TempDirectory.Create();
        Directory.CreateDirectory(Path.Join(directory.Path, "reports"));
        await File.WriteAllTextAsync(Path.Join(directory.Path, "reports/existing"), "caller-owned");
        var producer = new EvidenceProducerDeclaration("p", "kind", "1", [], [],
            [new EvidenceArtifactSlot("report", "reports", "text/plain", true, 10) { MaximumFiles = 1 }], 30);
        var writer = new EvidenceArtifactWriter(producer, directory.Path);
        await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync("report", "reports/existing", new byte[] { 1 }).AsTask());
        Assert.Equal("caller-owned", await File.ReadAllTextAsync(Path.Join(directory.Path, "reports/existing")));
        await writer.WriteAsync("report", "reports/new", new byte[] { 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync("report", "reports/other", new byte[] { 2 }).AsTask());
        Assert.False(EvidenceArtifactValidation.AreValid(producer, [writer.WrittenArtifacts[0], writer.WrittenArtifacts[0] with { RelativePath = "reports/other" }]));
        var legacy = producer with { ArtifactSlots = [producer.ArtifactSlots[0] with { MaximumFiles = null }] };
        var legacyWriter = new EvidenceArtifactWriter(legacy, directory.Path);
        await legacyWriter.WriteAsync("report", "reports/legacy", new byte[] { 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => legacyWriter.WriteAsync("report", "reports/legacy-other", new byte[] { 2 }).AsTask());
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("C:/outside")]
    [InlineData("reports/../outside")]
    [InlineData("reports//file")]
    [InlineData("reports/file\n")]
    [InlineData("reports\\file")]
    [InlineData(" reports/file")]
    [InlineData("reports/CON.txt")]
    [InlineData("reports/COM¹.txt")]
    [InlineData("reports/file.")]
    [InlineData("reports/file?")]
    public async Task MultiFileRejectsUnsafeAndNoncanonicalPathsBeforeWriting(string path)
    {
        using var directory = TempDirectory.Create();
        var producer = new EvidenceProducerDeclaration("p", "kind", "1", [], [],
            [new EvidenceArtifactSlot("report", "reports", "text/plain", false, 10) { MaximumFiles = 2 }], 30);
        var writer = new EvidenceArtifactWriter(producer, directory.Path);
        await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync("report", path, new byte[] { 1 }).AsTask());
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
        Assert.False(EvidenceArtifactValidation.AreValid(producer, [new EvidenceArtifactResult("report", path, "text/plain", 1, new string('a', 64))]));
    }

    [Fact]
    public void MultiFileValidatorPreservesAggregateSlotGlobalAndDestinationLimits()
    {
        var slot = new EvidenceArtifactSlot("report", "reports", "text/plain", true, long.MaxValue) { MaximumFiles = 128 };
        var producer = new EvidenceProducerDeclaration("p", "kind", "1", [], [], [slot], 30);
        var artifact = new EvidenceArtifactResult("report", "reports/a", "text/plain", EvidenceArtifactWriter.MaximumTotalArtifactBytes, new string('a', 64));
        Assert.True(EvidenceArtifactValidation.AreValid(producer, [artifact]));
        Assert.False(EvidenceArtifactValidation.AreValid(producer, [artifact, artifact with { RelativePath = "reports/b", LengthBytes = 1 }]));
        Assert.False(EvidenceArtifactValidation.AreValid(producer with { ArtifactSlots = [slot with { MaximumBytes = 3 }] },
            [artifact with { LengthBytes = 2 }, artifact with { RelativePath = "reports/b", LengthBytes = 2 }]));
        Assert.False(EvidenceArtifactValidation.AreValid(producer, Enumerable.Range(0, 129).Select(i => artifact with { RelativePath = $"reports/{i}", LengthBytes = 0 }).ToArray()));
        var other = slot with { LogicalName = "other" };
        Assert.False(EvidenceArtifactValidation.AreValid(producer with { ArtifactSlots = [slot, other] }, [artifact, artifact with { LogicalName = "other" }]));
    }

    [Fact]
    public async Task WriterRejectsLinkedAncestorWithoutWritingOutsideRoot()
    {
        using var directory = TempDirectory.Create();
        using var outside = TempDirectory.Create();
        TestFileSystem.CreateDirectoryLinkOrSkip(Path.Join(directory.Path, "reports"), outside.Path);
        var producer = new EvidenceProducerDeclaration("p", "kind", "1", [], [],
            [new EvidenceArtifactSlot("report", "reports", "text/plain", false, 10) { MaximumFiles = 2 }], 30);
        var writer = new EvidenceArtifactWriter(producer, directory.Path);
        await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync("report", "reports/file", new byte[] { 1 }).AsTask());
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
        Assert.Empty(writer.WrittenArtifacts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyWriterRetainsSupportForConsumerLinkedOutput(bool stream)
    {
        using var directory = TempDirectory.Create();
        using var target = TempDirectory.Create();
        TestFileSystem.CreateDirectoryLinkOrSkip(Path.Join(directory.Path, "reports"), target.Path);
        var producer = new EvidenceProducerDeclaration("p", "kind", "1", [], [],
            [new EvidenceArtifactSlot("report", "reports", "text/plain", true, 10)], 30);
        var writer = new EvidenceArtifactWriter(producer, directory.Path);
        var bytes = new byte[] { 1, 2 };
        if (stream)
        {
            using var contents = new MemoryStream(bytes);
            await writer.WriteAsync("report", "reports/file", contents);
        }
        else
        {
            await writer.WriteAsync("report", "reports/file", bytes);
        }
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Join(target.Path, "file")));
        Assert.True(await writer.VerifyWrittenArtifactsAsync());
    }

    [Fact]
    public void Version11ManifestSortsArtifactInventoryAndRejectsCaseCollidingDestinations()
    {
        var profile = ReusePlan().Profile;
        var producer = profile.Producers[0] with { ArtifactSlots = [new EvidenceArtifactSlot("report", "reports", "text/plain", true, 10) { MaximumFiles = 2 }] };
        var plan = CreatePlan(profile with { Producers = [producer] }, "1.1");
        var a = new EvidenceArtifactResult("report", "reports/a", "text/plain", 1, new string('a', 64));
        var b = a with { RelativePath = "reports/b" };
        var result = new EvidenceProducerResult("p", EvidenceProducerOutcome.Passed, ["assert"], Artifacts: [b, a]);
        var first = EvidenceManifestBuilder.Build(plan, [result]);
        var second = EvidenceManifestBuilder.Build(plan, [result with { Artifacts = [a, b] }]);
        Assert.Equal(first.ManifestDigest, second.ManifestDigest);
        Assert.Equal([a, b], first.ProducerResults[0].Artifacts);
        Assert.True(EvidenceManifestBuilder.Verify(plan, first));
        Assert.False(EvidenceArtifactValidation.AreValid(producer, [a, a with { RelativePath = "reports/A" }]));
    }

    [Theory]
    [InlineData("preparation")]
    [InlineData("wait")]
    [InlineData("restore")]
    public void NonzeroReuseMetricsRequireVersion11(string metric)
    {
        var metrics = metric switch
        {
            "preparation" => new EvidenceExecutionMetrics { PreparationMilliseconds = 1 },
            "wait" => new EvidenceExecutionMetrics { ReceiptWaitMilliseconds = 1 },
            _ => new EvidenceExecutionMetrics { ArtifactRestoreMilliseconds = 1 },
        };
        var profile = ReusePlan().Profile;
        var legacy = CreatePlan(profile, "1.0");
        EvidenceProducerResult[] results = [new("p", EvidenceProducerOutcome.Passed, ["assert"])];
        var rejected = EvidenceManifestBuilder.Build(legacy, results, metrics: metrics);
        Assert.Equal(EvidenceExecutionVerdict.Invalid, rejected.ExecutionVerdict);
        Assert.False(EvidenceManifestBuilder.Verify(legacy, rejected));
        var current = CreatePlan(profile, "1.1");
        var accepted = EvidenceManifestBuilder.Build(current, results, metrics: metrics);
        Assert.Equal(EvidenceExecutionVerdict.Passed, accepted.ExecutionVerdict);
        Assert.True(EvidenceManifestBuilder.Verify(current, accepted));
    }

    private static EvidenceProducerResult ValidReusedResult() => new("p", EvidenceProducerOutcome.Reused, ["assert"])
    {
        ProducerKey = new string('a', 64),
        OriginManifestDigest = new string('b', 64),
        OriginCompletedAt = DateTimeOffset.Parse("2026-11-14T12:00:00Z"),
    };

    private static EvidencePlan ReusePlan() => CreatePlan(new EvidenceProfile("targeted", EvidenceProfileScope.Targeted,
        [], [new EvidenceProducerDeclaration("p", "kind", "1", [], ["assert"], [], 30)],
        [new EvidenceObligation("o", "risk", "reason", ["p"], "assert")]), "1.1");

    private static EvidencePlan CreatePlan(EvidenceProfile profile, string version)
    {
        var policy = new EvidencePolicy("policy", "1", profile.Id, [profile], []);
        var policyDigest = EvidenceDigest.CanonicalSha256(policy);
        var paths = Array.Empty<NormalizedDiffPath>();
        var plan = new EvidencePlan(version, policy.Id, policyDigest, EvidenceDigest.CanonicalSha256(paths), profile, paths, [], string.Empty, policy);
        return plan with { PlanDigest = EvidenceDigest.CanonicalSha256(plan) };
    }

    private static byte[] ReplaceEnumToken(byte[] json, string property, string enumName, string replacement)
    {
        var text = Encoding.UTF8.GetString(json);
        var original = $"\"{property}\":\"{enumName}\"";
        Assert.Contains(original, text, StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(text.Replace(original, $"\"{property}\":{replacement}", StringComparison.Ordinal));
    }
}
