using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceJsonShapeTests
{
    private const string SecretCanary = "schema-secret-canary-779";

    public static TheoryData<string, string, string> NullCollectionCases => new()
    {
        { "EvidencePolicy.Profiles", "policy", "Profiles" },
        { "EvidencePolicy.Rules", "policy", "Rules" },
        { "EvidenceProfile.Resources", "profile", "Resources" },
        { "EvidenceProfile.Producers", "profile", "Producers" },
        { "EvidenceProfile.Obligations", "profile", "Obligations" },
        { "EvidenceResourceDeclaration.Requires", "resource", "Requires" },
        { "EvidenceProducerDeclaration.RequiredResources", "producer", "RequiredResources" },
        { "EvidenceProducerDeclaration.AssertionIds", "producer", "AssertionIds" },
        { "EvidenceProducerDeclaration.ArtifactSlots", "producer", "ArtifactSlots" },
        { "EvidenceObligation.RequiredProducerIds", "obligation", "RequiredProducerIds" },
        { "EvidencePlan.ChangedPaths", "plan", "ChangedPaths" },
        { "EvidencePlan.MatchedRuleIds", "plan", "MatchedRuleIds" },
        { "EvidencePlan.Profile.Producers", "plan", "Profile.Producers" },
        { "EvidencePlan.PolicySnapshot.Profiles", "plan", "PolicySnapshot.Profiles" },
        { "EvidenceManifest.ResourceResults", "manifest", "ResourceResults" },
        { "EvidenceManifest.SelectedObligationIds", "manifest", "SelectedObligationIds" },
        { "EvidenceManifest.ClosedObligationIds", "manifest", "ClosedObligationIds" },
        { "EvidenceManifest.UnmediatedObligationIds", "manifest", "UnmediatedObligationIds" },
        { "EvidenceManifest.ProducerResults", "manifest", "ProducerResults" },
        { "EvidenceProducerResult.SatisfiedAssertionIds", "producer-result", "SatisfiedAssertionIds" },
        { "EvidenceProducerResult.Artifacts", "producer-result", "Artifacts" },
    };

    [Fact]
    public void CanonicalJson_RoundTripsCompleteNestedPolicyPlanAndManifest()
    {
        var fixture = CreateFixture();
        var policyBytes = EvidenceCanonicalJson.Serialize(fixture.Policy);
        var planBytes = EvidenceCanonicalJson.Serialize(fixture.Plan);
        var manifestBytes = EvidenceCanonicalJson.Serialize(fixture.Manifest);

        var policy = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(policyBytes);
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(planBytes);
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(manifestBytes);

        Assert.Equal(policyBytes, EvidenceCanonicalJson.Serialize(policy));
        Assert.Equal(planBytes, EvidenceCanonicalJson.Serialize(plan));
        Assert.Equal(manifestBytes, EvidenceCanonicalJson.Serialize(manifest));
        Assert.Equal("src/new.cs", Assert.Single(plan.ChangedPaths).Path);
        Assert.Equal("src/old.cs", plan.ChangedPaths[0].PreviousPath);
        Assert.Equal("postgres", Assert.Single(plan.Profile.Resources).Id);
        Assert.Equal("integration", Assert.Single(plan.Profile.Producers).Id);
        Assert.Equal("integration-risk", Assert.Single(plan.Profile.Obligations).Id);
        Assert.Equal("integration-report", Assert.Single(plan.Profile.Producers[0].ArtifactSlots).LogicalName);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.Equal(EvidenceClaimKind.TargetedComplete, manifest.ClaimKind);
        Assert.Equal(new EvidenceExecutionMetrics(2, 12, 24, 3, 41, true), manifest.Metrics);
        Assert.Equal("integration/assertion@1", Assert.Single(manifest.ProducerResults[0].SatisfiedAssertionIds));
        Assert.Equal("reports/result.json", Assert.Single(manifest.ProducerResults[0].Artifacts!).RelativePath);
    }

    [Fact]
    public void Deserialize_RejectsMissingConstructorArgumentsAndNullRequiredValuesSafely()
    {
        var fixture = CreateFixture();

        AssertSafeJsonException<EvidencePolicy>(Mutate(fixture.Policy, root => root.AsObject().Remove("Id")));
        AssertSafeJsonException<EvidenceProfile>(Mutate(fixture.Profile, root => root.AsObject().Remove("Producers")));
        AssertSafeJsonException<EvidencePolicy>(Mutate(fixture.Policy, root => SetNull(root, "Id")));
        AssertSafeJsonException<EvidencePolicy>(Mutate(fixture.Policy, root => SetNull(root, "Profiles")));
        AssertSafeJsonException<EvidencePolicy>(Mutate(fixture.Policy, root => SetNull(root, "Rules")));
        AssertSafeJsonException<EvidenceProducerDeclaration>(Mutate(fixture.Producer, root => SetNull(root, "Version")));
        AssertSafeJsonException<EvidencePlan>(Mutate(fixture.Plan, root => SetNull(root, "Profile")));
        AssertSafeJsonException<EvidenceManifest>(Mutate(fixture.Manifest, root => SetNull(root, "Metrics")));
    }

    [Theory]
    [MemberData(nameof(NullCollectionCases))]
    public void Deserialize_RejectsNullItemsInEveryKnownContractCollection(string caseName, string contract, string path)
    {
        var fixture = CreateFixture();
        var json = MutateContract(fixture, contract, root => SetNullCollectionItem(root, path));

        AssertContractRejects(contract, json, caseName);
    }

    [Fact]
    public void Deserialize_PreservesOptionalNullableFieldsAndConstructorDefaults()
    {
        var fixture = CreateFixture();

        var planWithNullSnapshot = EvidenceCanonicalJson.Deserialize<EvidencePlan>(
            Mutate(fixture.Plan with { PolicySnapshot = null }, static _ => { }));
        var planWithoutSnapshot = EvidenceCanonicalJson.Deserialize<EvidencePlan>(
            Mutate(fixture.Plan, root => root.AsObject().Remove("PolicySnapshot")));
        Assert.Null(planWithNullSnapshot.PolicySnapshot);
        Assert.Null(planWithoutSnapshot.PolicySnapshot);

        var producerWithoutCoverageGate = EvidenceCanonicalJson.Deserialize<EvidenceProducerDeclaration>(
            Mutate(fixture.Producer, root => root.AsObject().Remove("CoverageGate")));
        Assert.Null(producerWithoutCoverageGate.CoverageGate);

        var resultWithNullOptionals = fixture.ProducerResult with { Diagnostic = null, Artifacts = null };
        var result = EvidenceCanonicalJson.Deserialize<EvidenceProducerResult>(
            Mutate(resultWithNullOptionals, root => root.AsObject().Remove("ElapsedMilliseconds")));
        Assert.Null(result.Diagnostic);
        Assert.Null(result.Artifacts);
        Assert.Equal(0, result.ElapsedMilliseconds);

        var resourceResult = EvidenceCanonicalJson.Deserialize<EvidenceResourceResult>(
            Mutate(fixture.ResourceResult, root => root.AsObject().Remove("Diagnostic")));
        Assert.Null(resourceResult.Diagnostic);

        var rule = EvidenceCanonicalJson.Deserialize<EvidencePolicyRule>(
            Mutate(Assert.Single(fixture.Policy.Rules), root => root.AsObject().Remove("Precedence")));
        Assert.Equal(0, rule.Precedence);

        var coverageGate = EvidenceCanonicalJson.Deserialize<EvidenceCoverageGateRequirements>(
            """{"MinLinePercent":80,"MinBranchPercent":70}"""u8);
        Assert.Null(coverageGate.MinPatchLinePercent);
        Assert.Null(coverageGate.MinPatchBranchPercent);
        Assert.Equal("measurable", coverageGate.PatchLineMode);
        Assert.Equal(0.5m, coverageGate.TolerancePercent);

        var metrics = EvidenceCanonicalJson.Deserialize<EvidenceExecutionMetrics>("{}"u8);
        Assert.Equal(new EvidenceExecutionMetrics(), metrics);
    }

    [Fact]
    public void Deserialize_IgnoresAdditiveUnknownNullPropertiesAndNullArrayItems()
    {
        var fixture = CreateFixture();

        var policy = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(
            Mutate(fixture.Policy, AddUnknownNullableFields));
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(
            Mutate(fixture.Plan, root =>
            {
                AddUnknownNullableFields(root);
                AddUnknownNullableFields(ResolveNode(root, "PolicySnapshot"));
            }));
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
            Mutate(fixture.Manifest, AddUnknownNullableFields));

        Assert.Equal(EvidenceCanonicalJson.Serialize(fixture.Policy), EvidenceCanonicalJson.Serialize(policy));
        Assert.Equal(EvidenceCanonicalJson.Serialize(fixture.Plan), EvidenceCanonicalJson.Serialize(plan));
        Assert.Equal(EvidenceCanonicalJson.Serialize(fixture.Manifest), EvidenceCanonicalJson.Serialize(manifest));
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
    }

    [Fact]
    public async Task Workflow_MapsMalformedNullShapesToSafeDiagnosticsAndAcceptsCompatibleNeighbors()
    {
        using var directory = TestDirectory.Create();
        var fixture = CreateFixture();
        var policyPath = Path.Join(directory.Path, "policy.json");
        var planPath = Path.Join(directory.Path, "plan.json");
        var manifestPath = Path.Join(directory.Path, "manifest.json");
        var workflow = new EvidenceCliWorkflow(new EvidencePlanner());

        await File.WriteAllBytesAsync(policyPath, Mutate(fixture.Policy, AddUnknownNullableFields));
        await File.WriteAllBytesAsync(planPath, Mutate(fixture.Plan, root =>
        {
            AddUnknownNullableFields(root);
            AddUnknownNullableFields(ResolveNode(root, "PolicySnapshot"));
        }));
        await File.WriteAllBytesAsync(manifestPath, Mutate(fixture.Manifest, AddUnknownNullableFields));

        var doctorReport = await workflow.DoctorAsync(
            new EvidencePlanningRequest(policyPath, ["src/new.cs"], null),
            CancellationToken.None);
        Assert.Equal("integration", doctorReport.Plan.Profile.Id);
        var verified = await workflow.VerifyAsync(planPath, manifestPath, CancellationToken.None);
        Assert.True(EvidenceManifestBuilder.Verify(verified.Plan, verified.Manifest));

        await File.WriteAllBytesAsync(policyPath, Mutate(fixture.Policy, root => SetNullCollectionItem(root, "Profiles")));
        var doctorFailure = await Assert.ThrowsAsync<EvidenceCliException>(() => workflow.DoctorAsync(
            new EvidencePlanningRequest(policyPath, ["src/new.cs"], null),
            CancellationToken.None));
        AssertSafeCliFailure(doctorFailure, "ASEVD205");

        await File.WriteAllBytesAsync(planPath, Mutate(fixture.Plan, root => SetNullCollectionItem(root, "ChangedPaths")));
        var planFailure = await Assert.ThrowsAsync<EvidenceCliException>(() =>
            workflow.VerifyAsync(planPath, manifestPath, CancellationToken.None));
        AssertSafeCliFailure(planFailure, "ASEVD209");

        await File.WriteAllBytesAsync(planPath, Mutate(fixture.Plan, static _ => { }));
        await File.WriteAllBytesAsync(manifestPath, Mutate(fixture.Manifest, root => SetNullCollectionItem(root, "ProducerResults")));
        var manifestFailure = await Assert.ThrowsAsync<EvidenceCliException>(() =>
            workflow.VerifyAsync(planPath, manifestPath, CancellationToken.None));
        AssertSafeCliFailure(manifestFailure, "ASEVD209");
    }

    private static EvidenceFixture CreateFixture()
    {
        var resource = new EvidenceResourceDeclaration("postgres", "completion", 30, []);
        var slot = new EvidenceArtifactSlot("integration-report", "reports", "application/json", true, 1024);
        var producer = new EvidenceProducerDeclaration(
            "integration",
            "integration",
            "1",
            ["postgres"],
            ["integration/assertion@1"],
            [slot],
            60);
        var obligation = new EvidenceObligation(
            "integration-risk",
            "database",
            "Changed database behavior requires an integration assertion.",
            ["integration"],
            "integration/assertion@1");
        var profile = new EvidenceProfile(
            "integration",
            EvidenceProfileScope.Targeted,
            [resource],
            [producer],
            [obligation]);
        var policy = new EvidencePolicy(
            "schema-regression",
            "1",
            "integration",
            [profile],
            [new EvidencePolicyRule("source", "src/**", "integration")]);
        var plan = new EvidencePlanner().Resolve(
            policy,
            [new NormalizedDiffPath("src/new.cs", "renamed", "src/old.cs")]);
        var resourceResult = new EvidenceResourceResult("postgres", EvidenceResourceOutcome.Ready, 12);
        var artifact = new EvidenceArtifactResult(
            "integration-report",
            "reports/result.json",
            "application/json",
            32,
            new string('a', 64));
        var producerResult = new EvidenceProducerResult(
            "integration",
            EvidenceProducerOutcome.Passed,
            ["integration/assertion@1"],
            Artifacts: [artifact],
            ElapsedMilliseconds: 24);
        var metrics = new EvidenceExecutionMetrics(2, 12, 24, 3, 41, true);
        var manifest = EvidenceManifestBuilder.Build(
            plan,
            [producerResult],
            resourceResults: [resourceResult],
            metrics: metrics);

        return new EvidenceFixture(policy, profile, resource, producer, obligation, plan, resourceResult, producerResult, manifest);
    }

    private static byte[] MutateContract(EvidenceFixture fixture, string contract, Action<JsonNode> mutation) => contract switch
    {
        "policy" => Mutate(fixture.Policy, mutation),
        "profile" => Mutate(fixture.Profile, mutation),
        "resource" => Mutate(fixture.Resource, mutation),
        "producer" => Mutate(fixture.Producer, mutation),
        "obligation" => Mutate(fixture.Obligation, mutation),
        "plan" => Mutate(fixture.Plan, mutation),
        "manifest" => Mutate(fixture.Manifest, mutation),
        "producer-result" => Mutate(fixture.ProducerResult, mutation),
        _ => throw new ArgumentOutOfRangeException(nameof(contract), contract, "Unknown contract fixture."),
    };

    private static void AssertContractRejects(string contract, byte[] json, string caseName)
    {
        switch (contract)
        {
            case "policy":
                AssertSafeJsonException<EvidencePolicy>(json);
                break;
            case "profile":
                AssertSafeJsonException<EvidenceProfile>(json);
                break;
            case "resource":
                AssertSafeJsonException<EvidenceResourceDeclaration>(json);
                break;
            case "producer":
                AssertSafeJsonException<EvidenceProducerDeclaration>(json);
                break;
            case "obligation":
                AssertSafeJsonException<EvidenceObligation>(json);
                break;
            case "plan":
                AssertSafeJsonException<EvidencePlan>(json);
                break;
            case "manifest":
                AssertSafeJsonException<EvidenceManifest>(json);
                break;
            case "producer-result":
                AssertSafeJsonException<EvidenceProducerResult>(json);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(contract), contract, $"Unknown contract fixture for {caseName}.");
        }
    }

    private static void AssertSafeJsonException<TValue>(byte[] json)
    {
        var exception = Assert.Throws<JsonException>(() => EvidenceCanonicalJson.Deserialize<TValue>(json));

        Assert.DoesNotContain(SecretCanary, exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    private static void AssertSafeCliFailure(EvidenceCliException exception, string expectedCode)
    {
        Assert.Equal(expectedCode, exception.Code);
        Assert.DoesNotContain(SecretCanary, exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    private static byte[] Mutate<TValue>(TValue value, Action<JsonNode> mutation)
    {
        var root = JsonNode.Parse(EvidenceCanonicalJson.Serialize(value))!;
        root.AsObject()["UnknownSecretCanary"] = SecretCanary;
        mutation(root);
        return JsonSerializer.SerializeToUtf8Bytes(root);
    }

    private static void SetNullCollectionItem(JsonNode root, string path)
    {
        var segments = path.Split('.');
        var parent = ResolveNode(root, segments[..^1]).AsObject();
        var nullItem = new JsonArray();
        nullItem.Add((JsonNode?)null);
        parent[segments[^1]] = nullItem;
    }

    private static void SetNull(JsonNode root, string path)
    {
        var segments = path.Split('.');
        ResolveNode(root, segments[..^1]).AsObject()[segments[^1]] = null;
    }

    private static JsonNode ResolveNode(JsonNode root, params string[] path)
    {
        JsonNode? current = root;
        foreach (var segment in path)
        {
            current = current switch
            {
                JsonObject value => value[segment],
                JsonArray value => value[int.Parse(segment, CultureInfo.InvariantCulture)],
                _ => throw new InvalidOperationException($"Cannot traverse JSON path segment '{segment}'."),
            };
            if (current is null)
            {
                throw new InvalidOperationException($"JSON path segment '{segment}' is missing.");
            }
        }

        return current;
    }

    private static void AddUnknownNullableFields(JsonNode node)
    {
        var properties = node.AsObject();
        properties["FutureNullableValue"] = null;
        var futureArray = new JsonArray();
        futureArray.Add((JsonNode?)null);
        properties["FutureCollectionWithNullItem"] = futureArray;
    }

    private sealed record EvidenceFixture(
        EvidencePolicy Policy,
        EvidenceProfile Profile,
        EvidenceResourceDeclaration Resource,
        EvidenceProducerDeclaration Producer,
        EvidenceObligation Obligation,
        EvidencePlan Plan,
        EvidenceResourceResult ResourceResult,
        EvidenceProducerResult ProducerResult,
        EvidenceManifest Manifest);
}
