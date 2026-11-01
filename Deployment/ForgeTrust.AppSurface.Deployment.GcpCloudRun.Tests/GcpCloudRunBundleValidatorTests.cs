using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Deployment;
using ForgeTrust.AppSurface.Deployment.GcpCloudRun;

public sealed partial class GcpCloudRunBundleValidatorTests
{
    [Fact]
    public async Task RenderWithoutProjectNumber_PreservesLegacyConstructorAndOmitsIdentityBytes()
    {
        // protects=old profile constructor and old artifact byte shape; fails_when=legacy omission gains provider identity; no_claim=provider identity is authenticated.
        using var fixture = new ProfileFixture(projectNumber: null);
        var target = new GcpCloudRunDeploymentTarget();
        var rendered = await target.RenderAsync(fixture.Request());

        Assert.DoesNotContain("projectNumber", Text(rendered, "gcp-cloud-run-migration.plan.json"), StringComparison.Ordinal);
        Assert.DoesNotContain("appsurface_execution_identity", Text(rendered, "gcp-cloud-run-migration.tf.json"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("123456789")]
    public async Task ValidatorAcceptsCanonicalRenderedBundle(string? projectNumber)
    {
        // protects=canonical render/observation compatibility; fails_when=valid generated bundles become Unknown; no_claim=execution occurred.
        using var fixture = new ProfileFixture(projectNumber);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());

        var plan = GcpCloudRunBundleValidator.ValidateForObservation(rendered);

        Assert.Equal("demo-project", plan.Project);
        Assert.Equal(projectNumber, plan.ProjectNumber);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("-1")]
    [InlineData("9223372036854775808")]
    [InlineData("project-number")]
    public async Task LoaderRejectsNonCanonicalProjectNumber(string projectNumber)
    {
        // protects=project-number admission; fails_when=invalid numeric identity reaches rendering; no_claim=number-to-project ownership is attested.
        using var fixture = new ProfileFixture(projectNumber);
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() =>
            GcpCloudRunBindingProfile.LoadAsync(fixture.ProfilePath, "Staging", fixture.Root));
        Assert.Equal("ASDEPLOY170", error.Diagnostic.Code);
    }

    [Fact]
    public async Task ValidatorRejectsMissingExecutionIdentityLocals()
    {
        // protects=plan/Terraform project-number linkage; fails_when=plan declares a number while Terraform omits locals; no_claim=locals prove cloud ownership.
        using var fixture = new ProfileFixture("123456789");
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var terraform = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.tf.json");
        var changedTerraform = Encoding.UTF8.GetString(terraform.Content).Replace(",\n  \"locals\": {\n    \"appsurface_execution_identity\": {\n      \"project\": \"demo-project\",\n      \"project_number\": \"123456789\"\n    }\n  }", string.Empty, StringComparison.Ordinal);
        var changed = rendered.Artifacts.Select(item => item.FileName == terraform.FileName ? DeploymentArtifact.Create(item.FileName, Encoding.UTF8.GetBytes(changedTerraform)) : item).ToArray();

        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(new DeploymentRenderResult(rendered.Target, changed)));
        Assert.Equal("ASDEPLOY152", error.Diagnostic.Code);
    }

    [Fact]
    public async Task ValidatorRejectsConflictingExecutionIdentityLocals()
    {
        // protects=exact project-number cross-artifact identity; fails_when=Terraform locals disagree with plan; no_claim=identity is provider-attested.
        using var fixture = new ProfileFixture("123456789");
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var terraform = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.tf.json");
        var changedTerraform = Encoding.UTF8.GetString(terraform.Content).Replace("123456789", "987654321", StringComparison.Ordinal);
        var changed = rendered.Artifacts.Select(item => item.FileName == terraform.FileName ? DeploymentArtifact.Create(item.FileName, Encoding.UTF8.GetBytes(changedTerraform)) : item).ToArray();

        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(new DeploymentRenderResult(rendered.Target, changed)));
        Assert.Equal("ASDEPLOY152", error.Diagnostic.Code);
    }

    [Fact]
    public async Task ValidatorRejectsDuplicatePlanProperty()
    {
        // protects=duplicate JSON ambiguity; fails_when=duplicate project fields are normalized by the parser; no_claim=filesystem replacement races are prevented.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var plan = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.plan.json");
        var json = Encoding.UTF8.GetString(plan.Content);
        var duplicate = json.TrimEnd().TrimEnd('}') + ",\"project\":\"other-project\"}\n";
        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(Replace(rendered, plan.FileName, Encoding.UTF8.GetBytes(duplicate))));
        Assert.Equal("ASDEPLOY171", error.Diagnostic.Code);
    }

    [Fact]
    public async Task ValidatorRejectsUnknownPlanProperty()
    {
        // protects=closed observation plan schema; fails_when=unknown top-level evidence is accepted; no_claim=all future provider fields are unsupported.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var plan = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.plan.json");
        var json = Encoding.UTF8.GetString(plan.Content);
        var changed = json.Replace("{\n", "{\n  \"unexpected\": true,\n", StringComparison.Ordinal);
        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(Replace(rendered, plan.FileName, Encoding.UTF8.GetBytes(changed))));
        Assert.Equal("ASDEPLOY171", error.Diagnostic.Code);
    }

    [Fact]
    public async Task ValidatorRejectsMutableImageBeforeLegacyCrossArtifactChecks()
    {
        // protects=immutable image admission; fails_when=tag image is coordinated into plan and Terraform; no_claim=image digest proves execution.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var plan = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.plan.json");
        var changed = Encoding.UTF8.GetString(plan.Content).Replace("@sha256:" + new string('b', 64), ":latest", StringComparison.Ordinal);
        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(Replace(rendered, plan.FileName, Encoding.UTF8.GetBytes(changed))));
        Assert.Equal("ASDEPLOY172", error.Diagnostic.Code);
    }

    [Fact]
    public async Task ValidatorRejectsArtifactOverFourMiB()
    {
        // protects=bounded observation input; fails_when=one artifact exceeds 4 MiB; no_claim=provider output limits are enforced here.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var intent = rendered.Artifacts.Single(item => item.FileName == "deployment-intent.v1.json");
        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(Replace(rendered, intent.FileName, new byte[(4 * 1024 * 1024) + 1])));
        Assert.Equal("ASDEPLOY171", error.Diagnostic.Code);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("schema-version")]
    [InlineData("target")]
    [InlineData("intent-hash")]
    [InlineData("terraform-hash")]
    [InlineData("empty-project")]
    [InlineData("empty-region")]
    [InlineData("resource-addresses-null")]
    [InlineData("capabilities-null")]
    [InlineData("expected-null")]
    [InlineData("expected-empty")]
    [InlineData("expected-null-entry")]
    [InlineData("expected-empty-logical-id")]
    [InlineData("expected-empty-job-name")]
    [InlineData("duplicate-logical-id")]
    [InlineData("duplicate-physical-name")]
    public async Task LegacyBundleGuardRejectsPlanIdentityAndInventoryMutations(string mutation)
    {
        // protects=legacy plan identity, cross-artifact references, and job inventory; fails_when=incomplete or ambiguous plan reaches parity comparison; no_claim=plan hash is a signature.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var artifact = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.plan.json");
        var root = System.Text.Json.Nodes.JsonNode.Parse(artifact.Content)!.AsObject();
        var expected = root["expected"]!.AsArray();
        switch (mutation)
        {
            case "schema": root["$schema"] = "https://example.invalid/schema"; break;
            case "schema-version": root["schemaVersion"] = "2.0"; break;
            case "target": root["target"] = "other-target"; break;
            case "intent-hash": root["intentSha256"] = new string('0', 64); break;
            case "terraform-hash": root["terraformSha256"] = new string('0', 64); break;
            case "empty-project": root["project"] = " "; break;
            case "empty-region": root["region"] = ""; break;
            case "resource-addresses-null": root["resourceAddresses"] = null; break;
            case "capabilities-null": root["requiredCapabilities"] = null; break;
            case "expected-null": root["expected"] = null; break;
            case "expected-empty": root["expected"] = new System.Text.Json.Nodes.JsonArray(); break;
            case "expected-null-entry": expected[0] = null; break;
            case "expected-empty-logical-id": expected[0]!["logicalId"] = " "; break;
            case "expected-empty-job-name": expected[0]!["physicalJobName"] = ""; break;
            case "duplicate-logical-id":
                expected.Add(System.Text.Json.Nodes.JsonNode.Parse(expected[0]!.ToJsonString()));
                break;
            case "duplicate-physical-name":
                var duplicate = System.Text.Json.Nodes.JsonNode.Parse(expected[0]!.ToJsonString())!;
                duplicate["logicalId"] = "second-job";
                expected.Add(duplicate);
                break;
            default: throw new InvalidOperationException();
        }
        var changedPlan = DeploymentArtifact.Create(artifact.FileName, Encoding.UTF8.GetBytes(root.ToJsonString()));
        var artifacts = rendered.Artifacts.Select(item => item.FileName == artifact.FileName ? changedPlan : item).ToArray();

        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunDeploymentTarget.ValidateBundleLegacy(new DeploymentRenderResult(rendered.Target, artifacts)));

        Assert.Equal("ASDEPLOY152", error.Diagnostic.Code);
    }

    [Fact]
    public void FiniteJsonAdmissionAcceptsInclusiveCollectionBoundary()
    {
        // protects=observer finite collection admission; fails_when=the documented inclusive 4096-entry boundary rejects valid evidence; no_claim=the bound is a provider semantic limit.
        using var document = JsonDocument.Parse("{" + string.Join(',', Enumerable.Range(0, GcpCloudRunJsonAdmission.MaxCollectionEntries).Select(index => $"\"k{index}\":0")) + "}");

        GcpCloudRunJsonAdmission.ValidateFiniteCollections(document.RootElement);
    }

    [Fact]
    public void FiniteJsonAdmissionRejectsCollectionBoundaryPlusOne()
    {
        // protects=observer finite collection admission; fails_when=4097 direct entries can amplify parser memory; no_claim=the bound changes legacy Verify behavior.
        using var document = JsonDocument.Parse("{" + string.Join(',', Enumerable.Range(0, GcpCloudRunJsonAdmission.MaxCollectionEntries + 1).Select(index => $"\"k{index}\":0")) + "}");

        Assert.Throws<JsonException>(() => GcpCloudRunJsonAdmission.ValidateFiniteCollections(document.RootElement));
    }

    [Fact]
    public void FiniteJsonAdmissionAcceptsInclusiveArrayBoundary()
    {
        // protects=inclusive array admission; fails_when=4096 items are incorrectly rejected;
        // no_claim=bounded JSON alone proves configuration parity.
        using var document = JsonDocument.Parse("[" + string.Join(',', Enumerable.Repeat("0", GcpCloudRunJsonAdmission.MaxCollectionEntries)) + "]");
        GcpCloudRunJsonAdmission.ValidateFiniteCollections(document.RootElement);
    }

    [Fact]
    public void FiniteJsonAdmissionRejectsArrayBoundaryPlusOne()
    {
        // protects=array-side finite admission; fails_when=4097 array items are accepted; no_claim=provider output is semantically valid merely because it is bounded.
        using var document = JsonDocument.Parse("[" + string.Join(',', Enumerable.Repeat("0", GcpCloudRunJsonAdmission.MaxCollectionEntries + 1)) + "]");

        Assert.Throws<JsonException>(() => GcpCloudRunJsonAdmission.ValidateFiniteCollections(document.RootElement));
    }

    [Fact]
    public async Task ValidatorRejectsMalformedUtf8()
    {
        // protects=UTF-8 JSON admission; fails_when=invalid bytes reach provider parsing; no_claim=provider output encoding is proven.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(Replace(rendered, "deployment-intent.v1.json", [0x7B, 0xFF, 0x7D])));
        Assert.Equal("ASDEPLOY171", error.Diagnostic.Code);
    }

    [Fact]
    public async Task ValidatorRejectsUnknownTerraformPropertyRecursively()
    {
        // protects=closed Terraform schema admission; fails_when=execution-affecting resource data is injected; no_claim=all provider defaults are observed.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var terraform = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.tf.json");
        var changed = Encoding.UTF8.GetString(terraform.Content).Replace("\"name\": \"${each.value.name}\"", "\"name\": \"${each.value.name}\", \"unexpected_execution_field\": true", StringComparison.Ordinal);
        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(Replace(rendered, terraform.FileName, Encoding.UTF8.GetBytes(changed))));
        Assert.Equal("ASDEPLOY171", error.Diagnostic.Code);
    }

    [Fact]
    public async Task ValidatorRejectsDuplicateArtifactNames()
    {
        // protects=exact three-artifact admission; fails_when=duplicate filenames are silently collapsed; no_claim=directory ownership is trusted.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var duplicate = new[] { rendered.Artifacts[0], rendered.Artifacts[0], rendered.Artifacts[2] };
        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(new DeploymentRenderResult(rendered.Target, duplicate)));
        Assert.Equal("ASDEPLOY163", error.Diagnostic.Code);
    }

    [Theory]
    [InlineData("\"image\": \"${each.value.image}\"", "\"image\": \"${each.value.other_image}\"")]
    [InlineData("\"mount_path\": \"/cloudsql\"", "\"mount_path\": \"/wrong\"")]
    [InlineData("\"name\": \"${each.value.secret_environment}\"", "\"name\": \"STATIC_SECRET\"")]
    [InlineData("\"network\": \"projects/demo-project/global/networks/main\"", "\"network\": \"projects/other-project/global/networks/main\"")]
    public async Task ValidatorRejectsCoordinatedTerraformProjectionTampering(string original, string replacement)
    {
        // protects=Terraform projection semantics after coordinated plan hash update; fails_when=changed image, mount, secret env, or resource projection passes; no_claim=provider execution is authenticated.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var terraform = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.tf.json");
        var changedTerraformText = Encoding.UTF8.GetString(terraform.Content).Replace(original, replacement, StringComparison.Ordinal);
        Assert.NotEqual(Encoding.UTF8.GetString(terraform.Content), changedTerraformText);
        var changedTerraform = DeploymentArtifact.Create(terraform.FileName, Encoding.UTF8.GetBytes(changedTerraformText));
        var plan = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.plan.json");
        var planText = Encoding.UTF8.GetString(plan.Content).Replace(terraform.Sha256, changedTerraform.Sha256, StringComparison.Ordinal);
        var changedPlan = DeploymentArtifact.Create(plan.FileName, Encoding.UTF8.GetBytes(planText));
        var artifacts = rendered.Artifacts.Select(item => item.FileName switch { var name when name == terraform.FileName => changedTerraform, var name when name == plan.FileName => changedPlan, _ => item }).ToArray();

        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(new DeploymentRenderResult(rendered.Target, artifacts)));
        Assert.Equal("ASDEPLOY152", error.Diagnostic.Code);
    }

    [Fact]
    public async Task ValidatorRejectsCoordinatedExtraContainer()
    {
        // protects=single-container execution contract; fails_when=an extra container is coordinated with updated plan hash; no_claim=sidecar behavior is observed.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var terraform = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.tf.json");
        var text = Encoding.UTF8.GetString(terraform.Content).Replace("\"containers\": [\n", "\"containers\": [{\"image\": \"extra\"},\n", StringComparison.Ordinal);
        var changedTerraform = DeploymentArtifact.Create(terraform.FileName, Encoding.UTF8.GetBytes(text));
        var plan = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.plan.json");
        var changedPlan = DeploymentArtifact.Create(plan.FileName, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(plan.Content).Replace(terraform.Sha256, changedTerraform.Sha256, StringComparison.Ordinal)));
        var artifacts = rendered.Artifacts.Select(item => item.FileName == terraform.FileName ? changedTerraform : item.FileName == plan.FileName ? changedPlan : item).ToArray();
        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(new DeploymentRenderResult(rendered.Target, artifacts)));
        Assert.Equal("ASDEPLOY152", error.Diagnostic.Code);
    }

    [Fact]
    public async Task ValidatorRejectsCoordinatedExtraResourceField()
    {
        // protects=closed resource inventory; fails_when=an unknown resource field is coordinated with updated plan hash; no_claim=provider defaults are inferred.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var terraform = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.tf.json");
        var text = Encoding.UTF8.GetString(terraform.Content).Replace("\"name\": \"${each.value.name}\"", "\"name\": \"${each.value.name}\", \"resource_marker\": true", StringComparison.Ordinal);
        var changedTerraform = DeploymentArtifact.Create(terraform.FileName, Encoding.UTF8.GetBytes(text));
        var plan = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.plan.json");
        var changedPlan = DeploymentArtifact.Create(plan.FileName, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(plan.Content).Replace(terraform.Sha256, changedTerraform.Sha256, StringComparison.Ordinal)));
        var artifacts = rendered.Artifacts.Select(item => item.FileName == terraform.FileName ? changedTerraform : item.FileName == plan.FileName ? changedPlan : item).ToArray();
        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(new DeploymentRenderResult(rendered.Target, artifacts)));
        Assert.Equal("ASDEPLOY171", error.Diagnostic.Code);
    }

    [Fact]
    public async Task ValidatorRejectsTaskCountProjectionTamperWithUpdatedHash()
    {
        // protects=template execution cardinality expression; fails_when=task count is redirected while plan hash is refreshed; no_claim=runtime scheduling is observed.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var terraform = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.tf.json");
        var text = Encoding.UTF8.GetString(terraform.Content).Replace("\"task_count\": \"${each.value.task_count}\"", "\"task_count\": \"${each.value.parallelism}\"", StringComparison.Ordinal);
        var changedTerraform = DeploymentArtifact.Create(terraform.FileName, Encoding.UTF8.GetBytes(text));
        var plan = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.plan.json");
        var planText = Encoding.UTF8.GetString(plan.Content).Replace(terraform.Sha256, changedTerraform.Sha256, StringComparison.Ordinal);
        var changedPlan = DeploymentArtifact.Create(plan.FileName, Encoding.UTF8.GetBytes(planText));
        var artifacts = rendered.Artifacts.Select(item => item.FileName == terraform.FileName ? changedTerraform : item.FileName == plan.FileName ? changedPlan : item).ToArray();

        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(new DeploymentRenderResult(rendered.Target, artifacts)));
        Assert.Equal("ASDEPLOY152", error.Diagnostic.Code);
    }

    private static DeploymentRenderResult Replace(DeploymentRenderResult result, string name, byte[] content) =>
        new(result.Target, result.Artifacts.Select(item => item.FileName == name ? DeploymentArtifact.Create(name, content) : item).ToArray());

    private static string Text(DeploymentRenderResult result, string name) => Encoding.UTF8.GetString(result.Artifacts.Single(item => item.FileName == name).Content);

    private sealed class ProfileFixture : IDisposable
    {
        public ProfileFixture(string? projectNumber)
        {
            Root = TestPathUtils.PathUnder(Path.GetTempPath(), "appsurface-profile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            ProfilePath = TestPathUtils.PathUnder(Root, "profile.json");
            var profile = new Dictionary<string, object?>
            {
                ["schemaVersion"] = "1.0",
                ["environment"] = "Staging",
                ["project"] = "demo-project",
                ["region"] = "us-central1",
                ["jobs"] = new Dictionary<string, string> { ["schema-preparation"] = "schema-preparation" },
                ["cloudSqlInstanceConnectionName"] = "demo-project:us-central1:primary",
                ["network"] = new { network = "projects/demo-project/global/networks/main", subnetwork = "projects/demo-project/regions/us-central1/subnetworks/main", egress = "PRIVATE_RANGES_ONLY" },
                ["serviceAccounts"] = new Dictionary<string, string> { ["migration-runtime"] = "migration-runtime@demo-project.iam.gserviceaccount.com" },
                ["secrets"] = new Dictionary<string, object> { ["db-connection"] = new { secretId = "db-connection", versionMode = "latest" } },
            };
            if (projectNumber is not null) profile["projectNumber"] = projectNumber;
            File.WriteAllText(ProfilePath, JsonSerializer.Serialize(profile));
        }

        public string Root { get; }
        public string ProfilePath { get; }
        public DeploymentRenderRequest Request() => new(Intent(), ProfilePath, Root, "test");
        private static DeploymentIntent Intent() => new("Staging", new SourceRevision(new string('a', 40)), [new MigrationJobIntent(new DeploymentLogicalId("schema-preparation"), DeploymentPhase.CandidatePreparation, new ImmutableImageReference($"us-docker.pkg.dev/demo/jobs/schema-preparation@sha256:{new string('b', 64)}"), "dotnet", ["SchemaPreparation.dll"], new DeploymentExecutionPolicy(1, 1, 3, TimeSpan.FromMinutes(10)), new SecretBinding(new DeploymentLogicalId("db-connection"), new DeploymentLogicalId("db-connection-parameter"), "ConnectionStrings__primary"), new DatabaseBinding(new DeploymentLogicalId("primary-db"), "ConnectionStrings:primary", new DeploymentLogicalId("db-connection")), new DeploymentLogicalId("migration-runtime"))]);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
