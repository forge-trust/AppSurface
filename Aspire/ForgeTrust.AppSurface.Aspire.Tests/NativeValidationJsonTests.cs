using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Evidence.Validation;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class NativeValidationJsonTests
{
    [Fact]
    public void Receipt_RejectsUnknownSchemaAndCannotSatisfyEvidenceManifestContract()
    {
        var (plan, receipt) = CreateRecipeReceipt();
        var bytes = NativeValidationReceiptCodec.Serialize(receipt);
        var changedSchema = JsonNode.Parse(bytes)!.AsObject();
        changedSchema["SchemaVersion"] = NativeValidationReceiptCodec.CurrentSchemaVersion + 1;
        var unknownEntry = JsonNode.Parse(bytes)!.AsObject();
        unknownEntry["Unexpected"] = true;
        var missingEntry = JsonNode.Parse(bytes)!.AsObject();
        missingEntry.Remove("ReceiptType");

        Assert.Throws<JsonException>(() =>
            NativeValidationReceiptCodec.Deserialize(Encoding.UTF8.GetBytes(changedSchema.ToJsonString())));
        Assert.Throws<JsonException>(() =>
            NativeValidationReceiptCodec.Deserialize(Encoding.UTF8.GetBytes(unknownEntry.ToJsonString())));
        Assert.Throws<JsonException>(() =>
            NativeValidationReceiptCodec.Deserialize(Encoding.UTF8.GetBytes(missingEntry.ToJsonString())));
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(bytes);
        Assert.False(EvidenceManifestBuilder.Verify(plan, manifest));
    }

    [Fact]
    public void Receipt_ReadEnforcesItsOwnByteLimitAndRejectsNull()
    {
        var (_, receipt) = CreateRecipeReceipt();
        var json = Encoding.UTF8.GetString(NativeValidationReceiptCodec.Serialize(receipt));
        var atLimit = Encoding.UTF8.GetBytes(json + new string(' ', 4 * 1024 * 1024 - json.Length));

        Assert.Equal(receipt.ReceiptDigest, NativeValidationReceiptCodec.Deserialize(atLimit).ReceiptDigest);
        Assert.Throws<InvalidDataException>(() => NativeValidationReceiptCodec.Deserialize([.. atLimit, (byte)' ']));
        Assert.Throws<JsonException>(() => NativeValidationReceiptCodec.Deserialize("null"u8));
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("input-digest")]
    [InlineData("plan-digest")]
    [InlineData("journeys")]
    [InlineData("null-journey")]
    [InlineData("producer-id")]
    [InlineData("outcome")]
    [InlineData("declared-assertions")]
    [InlineData("satisfied-assertions")]
    [InlineData("declared-slots")]
    [InlineData("null-slot")]
    [InlineData("artifacts")]
    [InlineData("null-artifact")]
    [InlineData("elapsed")]
    [InlineData("diagnostic")]
    public void Receipt_ReadRejectsCorruptedOrIncompleteData(string defect)
    {
        var (_, receipt) = CreateRecipeReceipt();
        var json = JsonNode.Parse(NativeValidationReceiptCodec.Serialize(receipt))!.AsObject();
        var journey = json["Journeys"]![0]!.AsObject();
        switch (defect)
        {
            case "digest": json["ReceiptDigest"] = new string('0', 64); break;
            case "input-digest": json["InputDigest"] = "invalid"; break;
            case "plan-digest": json["PlanDigest"] = "invalid"; break;
            case "journeys": json["Journeys"] = null; break;
            case "null-journey": json["Journeys"] = new JsonArray((JsonNode?)null); break;
            case "producer-id": journey["ProducerId"] = " "; break;
            case "outcome": journey["Outcome"] = "invented"; break;
            case "declared-assertions": journey["DeclaredAssertionIds"] = null; break;
            case "satisfied-assertions": journey["SatisfiedAssertionIds"] = null; break;
            case "declared-slots": journey["DeclaredArtifactSlots"] = null; break;
            case "null-slot": journey["DeclaredArtifactSlots"] = new JsonArray((JsonNode?)null); break;
            case "artifacts": journey["Artifacts"] = null; break;
            case "null-artifact": journey["Artifacts"] = new JsonArray((JsonNode?)null); break;
            case "elapsed": journey["ElapsedMilliseconds"] = -1; break;
            case "diagnostic": journey["Diagnostic"] = new string('x', 2049); break;
            default: throw new InvalidOperationException("Unknown receipt defect.");
        }

        Assert.Throws<JsonException>(() => NativeValidationReceiptCodec.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Fact]
    public void Receipt_VerificationRequiresOriginalPolicyAndDiffSnapshot()
    {
        var (plan, receipt) = CreateRecipeReceipt();

        Assert.False(NativeValidationReceiptCodec.Verify(plan with { PolicySnapshot = null }, receipt));
        Assert.False(NativeValidationReceiptCodec.Verify(plan with { PolicyDigest = new string('0', 64) }, receipt));
        Assert.False(NativeValidationReceiptCodec.Verify(plan with { ChangedPaths = [new NormalizedDiffPath("Other.cs")] }, receipt));
    }

    [Theory]
    [InlineData("duplicate-assertion")]
    [InlineData("missing-assertion")]
    [InlineData("undeclared-assertion")]
    [InlineData("changed-declaration")]
    [InlineData("changed-profile")]
    [InlineData("failed-cleanup-with-passed-outcome")]
    public void Receipt_ResealedDigestCannotHideAnInvalidRecipe(string defect)
    {
        var (plan, receipt) = CreateRecipeReceipt();
        var journey = receipt.Journeys.Single();
        var changed = defect switch
        {
            "duplicate-assertion" => receipt with { Journeys = [journey with { SatisfiedAssertionIds = ["recipe/assertion@1", "recipe/assertion@1"] }] },
            "missing-assertion" => receipt with { Journeys = [journey with { SatisfiedAssertionIds = [] }] },
            "undeclared-assertion" => receipt with { Journeys = [journey with { SatisfiedAssertionIds = ["recipe/other@1"] }] },
            "changed-declaration" => receipt with { Journeys = [journey with { DeclaredAssertionIds = ["recipe/other@1"] }] },
            "changed-profile" => receipt with { ProfileId = "other-recipe" },
            "failed-cleanup-with-passed-outcome" => receipt with { CleanupCompleted = false },
            _ => throw new InvalidOperationException("Unknown receipt defect."),
        };

        Assert.True(NativeValidationReceiptCodec.Verify(plan, receipt));
        if (defect == "failed-cleanup-with-passed-outcome")
        {
            Assert.Throws<InvalidDataException>(() => NativeValidationReceiptCodec.Serialize(changed));
        }
        else
        {
            var resealed = NativeValidationReceiptCodec.Deserialize(NativeValidationReceiptCodec.Serialize(changed));
            Assert.False(NativeValidationReceiptCodec.Verify(plan, resealed));
        }
    }

    [Theory]
    [InlineData("unknown-outcome")]
    [InlineData("empty-selection")]
    [InlineData("duplicate-journey")]
    [InlineData("cleanup-diagnostic-after-success")]
    [InlineData("claim")]
    [InlineData("eligibility")]
    [InlineData("proof")]
    public void Receipt_SerializationRejectsMalformedOrAuthorityBearingData(string defect)
    {
        var (_, receipt) = CreateRecipeReceipt();
        var journey = receipt.Journeys.Single();
        var changed = defect switch
        {
            "unknown-outcome" => receipt with { Journeys = [journey with { Outcome = "invented" }] },
            "empty-selection" => receipt with { Journeys = [] },
            "duplicate-journey" => receipt with { Journeys = [journey, journey] },
            "cleanup-diagnostic-after-success" => receipt with { CleanupDiagnostic = "cleanup failed" },
            "claim" => receipt with { EvidenceHostClaim = "targetedComplete" },
            "eligibility" => receipt with { EvidenceHostEligibility = "eligible" },
            "proof" => receipt with { EvidenceHostProofProduced = true },
            _ => throw new InvalidOperationException("Unknown receipt defect."),
        };

        Assert.Throws<InvalidDataException>(() => NativeValidationReceiptCodec.Serialize(changed));
    }

    private static (EvidencePlan Plan, NativeValidationReceipt Receipt) CreateRecipeReceipt()
    {
        var declaration = new EvidenceProducerDeclaration("recipe", "native-validation", "1", [], ["recipe/assertion@1"], [], 5);
        var profile = new EvidenceProfile("recipe", EvidenceProfileScope.Targeted, [], [declaration],
            [new EvidenceObligation("recipe", "native-validation", "Verify recipe behavior.", [declaration.Id], "recipe/assertion@1")]);
        var policy = new EvidencePolicy("recipe", "1", profile.Id, [profile], []);
        var plan = new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("Recipe.cs")]);
        var journey = new NativeValidationJourneyReceipt(declaration.Id, "passed", declaration.AssertionIds, declaration.AssertionIds, [], [], null, 1);
        var receipt = NativeValidationReceiptCodec.Create(plan, new string('b', 64), [journey], true, null, false);
        return (plan, receipt);
    }

    [Fact]
    public void ReadJsonFile_AcceptsValidJsonAndPreservesBytes()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Join(directory.Root, "candidate.json");
        var expected = Encoding.UTF8.GetBytes("{\"passed\":true}");
        File.WriteAllBytes(path, expected);

        using var document = ValidationProof.ReadJsonFile(path, 1024, CancellationToken.None);

        Assert.True(document.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal(expected, File.ReadAllBytes(path));
    }

    [Fact]
    public void ReadJsonFile_RejectsOversizedAndEmptyFilesWithoutChangingBytes()
    {
        using var directory = new TemporaryDirectory();
        var oversizedPath = System.IO.Path.Join(directory.Root, "oversized.json");
        var oversized = Encoding.UTF8.GetBytes("{\"value\":\"123456\"}");
        File.WriteAllBytes(oversizedPath, oversized);
        var emptyPath = System.IO.Path.Join(directory.Root, "empty.json");
        File.WriteAllBytes(emptyPath, []);

        Assert.Throws<InvalidDataException>(() => ValidationProof.ReadJsonFile(oversizedPath, 8, CancellationToken.None));
        Assert.Throws<InvalidDataException>(() => ValidationProof.ReadJsonFile(emptyPath, 1024, CancellationToken.None));
        Assert.Equal(oversized, File.ReadAllBytes(oversizedPath));
        Assert.Empty(File.ReadAllBytes(emptyPath));
    }

    [Fact]
    public void ReadJsonFile_RejectsOneByteAboveTheGateAndRunManifestCaps()
    {
        using var directory = new TemporaryDirectory();
        var gatePath = System.IO.Path.Join(directory.Root, "gate.json");
        var manifestPath = System.IO.Path.Join(directory.Root, "manifest.json");
        foreach (var (path, maximum) in new[] { (gatePath, 1 * 1024 * 1024), (manifestPath, 16 * 1024 * 1024) })
        {
            var exact = "{}" + new string(' ', maximum - 2);
            File.WriteAllText(path, exact, new UTF8Encoding(false));
            using (var accepted = ValidationProof.ReadJsonFile(path, maximum, CancellationToken.None))
                Assert.Equal(System.Text.Json.JsonValueKind.Object, accepted.RootElement.ValueKind);
            File.AppendAllText(path, " ", new UTF8Encoding(false));
            Assert.Equal(maximum + 1, new FileInfo(path).Length);

            var exception = Assert.Throws<InvalidDataException>(() => ValidationProof.ReadJsonFile(path, maximum, CancellationToken.None));

            Assert.Contains("size limit", exception.Message, StringComparison.Ordinal);
            Assert.Equal(exact + " ", File.ReadAllText(path));
        }
    }

    [Fact]
    public void ReadJsonFile_RejectsInvalidJsonAndHonorsCancellation()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Join(directory.Root, "invalid.json");
        File.WriteAllText(path, "{invalid");

        Assert.Throws<InvalidDataException>(() => ValidationProof.ReadJsonFile(path, 1024, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ValidationProof.ReadJsonFile(path, 1024, cancellation.Token));
    }

    [Fact]
    public void GateReportContract_AcceptsBoundedValidCandidateReportAndPreservesBytes()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Join(directory.Root, "coverage-gate.json");
        var json = """
            {"passed":true,"tolerancePercent":0,"thresholds":{"line":95,"branch":85},"effectiveThresholds":{"line":95,"branch":85}}
            """;
        File.WriteAllText(path, json);
        var expected = File.ReadAllBytes(path);

        InstalledConsumerProducer.ValidateGateReportForTests(directory.Root, hasPatch: false);

        Assert.Equal(expected, File.ReadAllBytes(path));
    }

    [Fact]
    public void GateReportContract_RejectsOversizedCandidateBytesBeforeParsing()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Join(directory.Root, "coverage-gate.json");
        const string validGate = """
            {"passed":true,"tolerancePercent":0,"thresholds":{"line":95,"branch":85},"effectiveThresholds":{"line":95,"branch":85}}
            """;
        var exact = validGate + new string(' ', 1024 * 1024 - validGate.Length);
        File.WriteAllText(path, exact, new UTF8Encoding(false));
        InstalledConsumerProducer.ValidateGateReportForTests(directory.Root, hasPatch: false);
        File.AppendAllText(path, " ", new UTF8Encoding(false));

        var exception = Assert.Throws<InvalidDataException>(() =>
            InstalledConsumerProducer.ValidateGateReportForTests(directory.Root, hasPatch: false));
        Assert.Contains("size limit", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1024 * 1024 + 1, new FileInfo(path).Length);
    }

    [Fact]
    public void ReadJsonFile_RejectsInvalidLimitsAndMissingFiles()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Join(directory.Root, "missing.json");

        Assert.Throws<ArgumentOutOfRangeException>(() => ValidationProof.ReadJsonFile(path, 0, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => ValidationProof.ReadJsonFile(path, (long)int.MaxValue + 1, CancellationToken.None));
        Assert.Throws<InvalidDataException>(() => ValidationProof.ReadJsonFile(path, 1024, CancellationToken.None));
        Assert.False(File.Exists(path));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Directory.CreateTempSubdirectory("appsurface-native-json-").FullName;
        }

        public string Root { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
