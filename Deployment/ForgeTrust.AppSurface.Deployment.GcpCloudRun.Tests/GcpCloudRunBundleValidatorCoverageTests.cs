using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Deployment;
using ForgeTrust.AppSurface.Deployment.GcpCloudRun;

public sealed partial class GcpCloudRunBundleValidatorTests
{
    [Fact]
    public void ValidatorRejectsNullRenderResult()
    {
        // protects=null input admission; fails_when=null is dereferenced before a safe validation exception; no_claim=provider state was queried.
        Assert.Throws<ArgumentNullException>(() => GcpCloudRunBundleValidator.ValidateForObservation(null!));
    }

    [Fact]
    public async Task ValidatorRejectsIncompleteArtifactCollection()
    {
        // protects=exact three-artifact boundary; fails_when=two artifacts reach legacy cross-validation; no_claim=missing evidence is recoverable.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());

        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(new DeploymentRenderResult(rendered.Target, rendered.Artifacts.Take(2).ToArray())));

        Assert.Equal("ASDEPLOY171", error.Diagnostic.Code);
    }

    [Fact]
    public async Task ValidatorRejectsArtifactCollectionWithExtraEntry()
    {
        // protects=exact finite bundle cardinality; fails_when=an extra artifact reaches cross-artifact parsing; no_claim=artifact bytes are authentic.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var artifacts = rendered.Artifacts.Append(rendered.Artifacts[0]).ToArray();

        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(new DeploymentRenderResult(rendered.Target, artifacts)));

        Assert.Equal("ASDEPLOY171", error.Diagnostic.Code);
    }

    [Fact]
    public async Task ValidatorRejectsNullArtifactEntry()
    {
        // protects=finite artifact snapshot; fails_when=a null slot is silently skipped; no_claim=artifact publication is trusted.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var artifacts = new DeploymentArtifact?[] { rendered.Artifacts[0], null, rendered.Artifacts[2] };

        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(new DeploymentRenderResult(rendered.Target, artifacts!)));

        Assert.Equal("ASDEPLOY171", error.Diagnostic.Code);
    }

    [Fact]
    public async Task ValidatorRejectsIntentWrongFieldType()
    {
        // protects=portable intent type admission; fails_when=non-string command is normalized into executable evidence; no_claim=command execution occurred.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var intent = rendered.Artifacts.Single(item => item.FileName == "deployment-intent.v1.json");
        var changed = Encoding.UTF8.GetString(intent.Content).Replace("\"command\": \"dotnet\"", "\"command\": 7", StringComparison.Ordinal);

        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(Replace(rendered, intent.FileName, Encoding.UTF8.GetBytes(changed))));

        Assert.Equal("ASDEPLOY171", error.Diagnostic.Code);
    }

    [Fact]
    public async Task ValidatorRejectsIntentArgumentsWrongShape()
    {
        // protects=portable argument vector admission; fails_when=a scalar argument is accepted as a command list; no_claim=the provider would execute it.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var intent = rendered.Artifacts.Single(item => item.FileName == "deployment-intent.v1.json");
        var changed = Encoding.UTF8.GetString(intent.Content).Replace("\"arguments\": [\n        \"SchemaPreparation.dll\"\n      ]", "\"arguments\": \"SchemaPreparation.dll\"", StringComparison.Ordinal);

        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(Replace(rendered, intent.FileName, Encoding.UTF8.GetBytes(changed))));

        Assert.Equal("ASDEPLOY171", error.Diagnostic.Code);
    }

    [Fact]
    public async Task ValidatorRejectsPlanExpectedImageWrongType()
    {
        // protects=plan immutable image field type; fails_when=a numeric image is treated as digest evidence; no_claim=the image was pulled.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var plan = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.plan.json");
        var changed = Encoding.UTF8.GetString(plan.Content).Replace("\"image\": \"us-docker.pkg.dev/demo/jobs/schema-preparation@sha256:" + new string('b', 64) + "\"", "\"image\": 7", StringComparison.Ordinal);

        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(Replace(rendered, plan.FileName, Encoding.UTF8.GetBytes(changed))));

        Assert.Equal("ASDEPLOY172", error.Diagnostic.Code);
    }

    [Fact]
    public async Task ValidatorRejectsTerraformProviderSourceTamper()
    {
        // protects=closed provider identity; fails_when=provider source changes while the plan hash is refreshed; no_claim=provider ownership is authenticated.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var changed = WithTerraform(rendered, "hashicorp/google", "other/google");

        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(changed));

        Assert.Equal("ASDEPLOY152", error.Diagnostic.Code);
    }

    [Fact]
    public async Task ValidatorRejectsTerraformMissingTemplateProjection()
    {
        // protects=closed template inventory; fails_when=task_count is omitted from the resource template; no_claim=Terraform defaults are known.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var changed = WithTerraform(rendered, "\"task_count\": \"${each.value.task_count}\",\n        ", string.Empty);

        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(changed));

        Assert.Equal("ASDEPLOY152", error.Diagnostic.Code);
    }

    [Fact]
    public async Task ValidatorRejectsUnknownNestedTerraformProperty()
    {
        // protects=closed nested Terraform schema; fails_when=an unknown network field is ignored; no_claim=Cloud Run network state was inspected.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var changed = WithTerraform(rendered, "\"network_interfaces\": [", "\"network_interfaces_missing\": [");

        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(changed));

        Assert.Equal("ASDEPLOY171", error.Diagnostic.Code);
    }

    [Fact]
    public async Task ValidatorRejectsNonObjectJsonRoot()
    {
        // protects=JSON object admission before schema traversal; fails_when=top-level array input is treated as artifact schema; no_claim=the JSON is valid evidence.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());

        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(Replace(rendered, "gcp-cloud-run-migration.plan.json", "[]"u8.ToArray())));

        Assert.Equal("ASDEPLOY171", error.Diagnostic.Code);
    }

    [Fact]
    public async Task ValidatorRejectsUnexpectedArtifactFileName()
    {
        // protects=closed artifact filename admission; fails_when=an unknown third artifact is normalized as a known plan; no_claim=artifact order is significant.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var plan = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.plan.json");
        var artifacts = rendered.Artifacts.Select(item => item.FileName == plan.FileName ? DeploymentArtifact.Create("unexpected-plan.json", plan.Content) : item).ToArray();

        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(new DeploymentRenderResult(rendered.Target, artifacts)));

        Assert.Equal("ASDEPLOY171", error.Diagnostic.Code);
    }

    [Theory]
    [InlineData("provider-name")]
    [InlineData("task-timeout")]
    [InlineData("egress")]
    [InlineData("network")]
    [InlineData("sql-name")]
    [InlineData("mounts-empty")]
    [InlineData("secret-ref-empty")]
    [InlineData("secret-ref-scalar")]
    [InlineData("required-object-scalar")]
    public async Task CoordinatedTerraformMutationsCannotChangeEffectiveSettings(string mutation)
    {
        // protects=full Terraform configuration projection after a coordinated hash refresh;
        // fails_when=an effective setting or nested shape differs while the bundle is admitted; no_claim=an apply occurred.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var artifact = rendered.Artifacts.Single(a => a.FileName == "gcp-cloud-run-migration.tf.json");
        var root = System.Text.Json.Nodes.JsonNode.Parse(artifact.Content)!.AsObject();
        var resource = root["resource"]!["google_cloud_run_v2_job"]!["appsurface_migration"]!;
        var task = resource["template"]!["template"]!;
        var container = task["containers"]![0]!;
        switch (mutation)
        {
            case "provider-name": resource["name"] = "other"; break;
            case "task-timeout": task["timeout"] = "601s"; break;
            case "egress": task["vpc_access"]!["egress"] = "ALL_TRAFFIC"; break;
            case "network": task["vpc_access"]!["network_interfaces"]![0]!["network"] = "projects/other-project/global/networks/main"; break;
            case "sql-name": task["volumes"]![0]!["name"] = "other"; break;
            case "mounts-empty": container["volume_mounts"] = new System.Text.Json.Nodes.JsonArray(); break;
            case "secret-ref-empty": container["env"]![0]!["value_source"]![0]!["secret_key_ref"] = new System.Text.Json.Nodes.JsonArray(); break;
            case "secret-ref-scalar": container["env"]![0]!["value_source"]![0]!["secret_key_ref"]![0] = 1; break;
            case "required-object-scalar": root["terraform"] = "unsupported"; break;
            default: throw new InvalidOperationException();
        }
        var changed = WithTerraform(rendered, Encoding.UTF8.GetString(artifact.Content), root.ToJsonString());
        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(changed));
        Assert.Equal("ASDEPLOY152", error.Diagnostic.Code);
    }

    [Theory]
    [InlineData("missing-environment")]
    [InlineData("revision-value-scalar")]
    [InlineData("job-missing-command")]
    [InlineData("secret-source-scalar")]
    public async Task IncompletePortableIntentCannotReachLegacyNormalization(string mutation)
    {
        // protects=portable required field admission; fails_when=missing or malformed facts reach provider comparison;
        // no_claim=hash consistency authenticates a bundle publisher.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var artifact = rendered.Artifacts.Single(a => a.FileName == "deployment-intent.v1.json");
        var root = System.Text.Json.Nodes.JsonNode.Parse(artifact.Content)!.AsObject();
        switch (mutation)
        {
            case "missing-environment": root.Remove("environment"); break;
            case "revision-value-scalar": root["sourceRevision"] = 1; break;
            case "job-missing-command": root["migrationJobs"]![0]!.AsObject().Remove("command"); break;
            case "secret-source-scalar": root["migrationJobs"]![0]!["connectionSecret"] = "unsupported"; break;
            default: throw new InvalidOperationException();
        }
        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(
            Replace(rendered, artifact.FileName, Encoding.UTF8.GetBytes(root.ToJsonString()))));
        Assert.Equal("ASDEPLOY171", error.Diagnostic.Code);
    }

    [Theory]
    [InlineData("jobs-empty")]
    [InlineData("revision-object-extra-key")]
    [InlineData("job-environment-scalar")]
    [InlineData("secret-id-nested-extra-key")]
    [InlineData("database-missing-connection-secret")]
    [InlineData("revision-value-missing")]
    [InlineData("execution-object-scalar")]
    [InlineData("arguments-number-item")]
    [InlineData("arguments-object-unknown")]
    [InlineData("intent-image-mutable")]
    [InlineData("blank-command")]
    [InlineData("environment-map-number-value")]
    public async Task PortableIntentAndParityRejectMalformedCollections(string mutation)
    {
        // protects=portable intent closed fields and array admission; fails_when=malformed execution detail reaches normalization; no_claim=publisher authenticity.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var artifact = rendered.Artifacts.Single(item => item.FileName == "deployment-intent.v1.json");
        var root = System.Text.Json.Nodes.JsonNode.Parse(artifact.Content)!.AsObject();
        var job = root["migrationJobs"]![0]!.AsObject();
        switch (mutation)
        {
            case "jobs-empty": root["migrationJobs"] = new System.Text.Json.Nodes.JsonArray(); break;
            case "revision-object-extra-key": root["sourceRevision"]!["unexpected"] = true; break;
            case "job-environment-scalar": job["environment"] = "unsupported"; break;
            case "secret-id-nested-extra-key": job["connectionSecret"]!["id"]!["unexpected"] = true; break;
            case "database-missing-connection-secret": job["database"]!.AsObject().Remove("connectionSecret"); break;
            case "revision-value-missing": root["sourceRevision"]!.AsObject().Remove("value"); break;
            case "execution-object-scalar": job["execution"] = "unsupported"; break;
            case "arguments-number-item": job["arguments"]![0] = 7; break;
            case "arguments-object-unknown": job["arguments"]![0] = System.Text.Json.Nodes.JsonNode.Parse("{\"unrecognized\":true}"); break;
            case "intent-image-mutable": job["image"]!["value"] = "us-docker.pkg.dev/demo/jobs/schema-preparation:latest"; break;
            case "blank-command": job["command"] = " "; break;
            case "environment-map-number-value": job["environment"]!["EXTRA"] = 7; break;
            default: throw new InvalidOperationException();
        }

        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(
            Replace(rendered, artifact.FileName, Encoding.UTF8.GetBytes(root.ToJsonString()))));

        var expectedCode = mutation switch
        {
            "job-environment-scalar" or "environment-map-number-value" => "ASDEPLOY152",
            "intent-image-mutable" => "ASDEPLOY172",
            _ => "ASDEPLOY171",
        };
        Assert.Equal(expectedCode, error.Diagnostic.Code);
    }

    [Theory]
    [InlineData("missing-expected")]
    [InlineData("expected-scalar")]
    [InlineData("expected-image-mutable")]
    [InlineData("expected-image-missing")]
    public async Task PlanExpectedAdmissionRejectsMissingAndMalformedInventory(string mutation)
    {
        // protects=plan expected array image identities; fails_when=missing or mutable expected parity is normalized; no_claim=plan publisher authenticity.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var artifact = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.plan.json");
        var root = System.Text.Json.Nodes.JsonNode.Parse(artifact.Content)!.AsObject();
        switch (mutation)
        {
            case "missing-expected": root.Remove("expected"); break;
            case "expected-scalar": root["expected"] = 1; break;
            case "expected-image-mutable": root["expected"]![0]!["image"] = "us-docker.pkg.dev/demo/jobs/schema-preparation:latest"; break;
            case "expected-image-missing": root["expected"]![0]!.AsObject().Remove("image"); break;
            default: throw new InvalidOperationException();
        }

        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(
            Replace(rendered, artifact.FileName, Encoding.UTF8.GetBytes(root.ToJsonString()))));

        Assert.Equal(mutation is "expected-image-mutable" or "expected-image-missing" ? "ASDEPLOY172" : "ASDEPLOY171", error.Diagnostic.Code);
    }

    [Theory]
    [InlineData("missing-expected-job")]
    [InlineData("extra-job")]
    [InlineData("job-args-scalar")]
    [InlineData("job-task-count-string")]
    [InlineData("job-timeout-mismatch")]
    [InlineData("environment-value-number")]
    [InlineData("multiple-volumes")]
    [InlineData("multiple-containers")]
    [InlineData("multiple-network-interfaces")]
    [InlineData("multiple-cloudsql-instances")]
    [InlineData("secret-environment-value-mismatch")]
    [InlineData("dynamic-env-expression-mismatch")]
    [InlineData("resource-name-expression-mismatch")]
    [InlineData("template-parallelism-expression-mismatch")]
    [InlineData("task-template-service-account-expression-mismatch")]
    [InlineData("task-template-max-retries-expression-mismatch")]
    [InlineData("multiple-secret-environment-entries")]
    [InlineData("multiple-secret-value-sources")]
    [InlineData("multiple-secret-key-refs")]
    [InlineData("secret-version-expression-mismatch")]
    [InlineData("dynamic-content-name-mismatch")]
    [InlineData("dynamic-content-value-mismatch")]
    [InlineData("locals-without-project-number")]
    public async Task CoordinatedTerraformProjectionMutationsAreRejected(string mutation)
    {
        // protects=closed Terraform projection and plan hash linkage; fails_when=changed effective execution shape is admitted; no_claim=provider defaults were inspected.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var terraform = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.tf.json");
        var root = System.Text.Json.Nodes.JsonNode.Parse(terraform.Content)!.AsObject();
        var resource = root["resource"]!["google_cloud_run_v2_job"]!["appsurface_migration"]!;
        var job = resource["for_each"]!["schema-preparation"]!;
        var task = resource["template"]!["template"]!;
        var container = task["containers"]![0]!;
        switch (mutation)
        {
            case "missing-expected-job": resource["for_each"]!.AsObject().Remove("schema-preparation"); break;
            case "extra-job": resource["for_each"]!["unexpected"] = System.Text.Json.Nodes.JsonNode.Parse(job.ToJsonString()); break;
            case "job-args-scalar": job["args"] = "SchemaPreparation.dll"; break;
            case "job-task-count-string": job["task_count"] = "1"; break;
            case "job-timeout-mismatch": job["timeout"] = "601s"; break;
            case "environment-value-number": job["environment"]!["ADDED_ENVIRONMENT"] = 7; break;
            case "multiple-volumes": task["volumes"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(task["volumes"]![0]!.ToJsonString())); break;
            case "multiple-containers": task["containers"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(container.ToJsonString())); break;
            case "multiple-network-interfaces": task["vpc_access"]!["network_interfaces"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(task["vpc_access"]!["network_interfaces"]![0]!.ToJsonString())); break;
            case "multiple-cloudsql-instances": task["volumes"]![0]!["cloud_sql_instance"]!["instances"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(task["volumes"]![0]!["cloud_sql_instance"]!["instances"]![0]!.ToJsonString())); break;
            case "secret-environment-value-mismatch": job["secret_environment"] = "OTHER_SECRET"; break;
            case "dynamic-env-expression-mismatch": container["dynamic"]!["env"]!["for_each"] = "${each.value.arguments}"; break;
            case "resource-name-expression-mismatch": resource["name"] = "schema-preparation"; break;
            case "template-parallelism-expression-mismatch": resource["template"]!["parallelism"] = "${each.value.task_count}"; break;
            case "task-template-service-account-expression-mismatch": task["service_account"] = "${each.value.name}"; break;
            case "task-template-max-retries-expression-mismatch": task["max_retries"] = "${each.value.parallelism}"; break;
            case "multiple-secret-environment-entries": container["env"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(container["env"]![0]!.ToJsonString())); break;
            case "multiple-secret-value-sources": container["env"]![0]!["value_source"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(container["env"]![0]!["value_source"]![0]!.ToJsonString())); break;
            case "multiple-secret-key-refs": container["env"]![0]!["value_source"]![0]!["secret_key_ref"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(container["env"]![0]!["value_source"]![0]!["secret_key_ref"]![0]!.ToJsonString())); break;
            case "secret-version-expression-mismatch": container["env"]![0]!["value_source"]![0]!["secret_key_ref"]![0]!["version"] = "${each.value.secret_id}"; break;
            case "dynamic-content-name-mismatch": container["dynamic"]!["env"]!["content"]!["name"] = "${each.value.secret_environment}"; break;
            case "dynamic-content-value-mismatch": container["dynamic"]!["env"]!["content"]!["value"] = "${each.value.image}"; break;
            case "locals-without-project-number": root["locals"] = new System.Text.Json.Nodes.JsonObject(); break;
            default: throw new InvalidOperationException();
        }

        var changed = WithTerraform(rendered, Encoding.UTF8.GetString(terraform.Content), root.ToJsonString());
        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(changed));

        Assert.Equal("ASDEPLOY152", error.Diagnostic.Code);
    }

    [Theory]
    [InlineData("missing-identity-object")]
    [InlineData("identity-scalar")]
    [InlineData("project-name-mismatch")]
    [InlineData("project-number-mismatch")]
    public async Task ExecutionIdentityLocalsMustMatchNumberedPlan(string mutation)
    {
        // protects=numbered plan to Terraform identity projection; fails_when=optional locals are absent or disagree; no_claim=GCP project ownership is independently attested.
        using var fixture = new ProfileFixture("123456789");
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var artifact = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.tf.json");
        var root = System.Text.Json.Nodes.JsonNode.Parse(artifact.Content)!.AsObject();
        switch (mutation)
        {
            case "missing-identity-object": root["locals"]!.AsObject().Remove("appsurface_execution_identity"); break;
            case "identity-scalar": root["locals"]!["appsurface_execution_identity"] = 7; break;
            case "project-name-mismatch": root["locals"]!["appsurface_execution_identity"]!["project"] = "other-project"; break;
            case "project-number-mismatch": root["locals"]!["appsurface_execution_identity"]!["project_number"] = "987654321"; break;
            default: throw new InvalidOperationException();
        }

        var changed = WithTerraform(rendered, Encoding.UTF8.GetString(artifact.Content), root.ToJsonString());
        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(changed));

        Assert.Equal("ASDEPLOY152", error.Diagnostic.Code);
    }

    [Theory]
    [InlineData("noncanonical-project")]
    [InlineData("noncanonical-region")]
    [InlineData("noncanonical-job-name")]
    [InlineData("project-number-leading-zero")]
    [InlineData("project-number-overflow")]
    public async Task CoordinatedIdentityProjectionRejectsNoncanonicalValues(string mutation)
    {
        // protects=canonical Cloud Run identity values after all artifact references agree; fails_when=malformed project/region/job/number is normalized; no_claim=project ownership is attested.
        using var fixture = new ProfileFixture(mutation.StartsWith("project-number-", StringComparison.Ordinal) ? "123456789" : null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var terraform = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.tf.json");
        var plan = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.plan.json");
        var terraformRoot = System.Text.Json.Nodes.JsonNode.Parse(terraform.Content)!.AsObject();
        var planRoot = System.Text.Json.Nodes.JsonNode.Parse(plan.Content)!.AsObject();
        var resource = terraformRoot["resource"]!["google_cloud_run_v2_job"]!["appsurface_migration"]!;
        switch (mutation)
        {
            case "noncanonical-project":
                resource["project"] = "demo_project";
                planRoot["project"] = "demo_project";
                break;
            case "noncanonical-region":
                resource["location"] = "us_central1";
                planRoot["region"] = "us_central1";
                break;
            case "noncanonical-job-name":
                resource["for_each"]!["schema-preparation"]!["name"] = "bad_name";
                planRoot["expected"]![0]!["physicalJobName"] = "bad_name";
                break;
            case "project-number-leading-zero":
                terraformRoot["locals"]!["appsurface_execution_identity"]!["project_number"] = "0123456789";
                planRoot["projectNumber"] = "0123456789";
                break;
            case "project-number-overflow":
                terraformRoot["locals"]!["appsurface_execution_identity"]!["project_number"] = "9223372036854775808";
                planRoot["projectNumber"] = "9223372036854775808";
                break;
            default: throw new InvalidOperationException();
        }

        var changedTerraform = DeploymentArtifact.Create(terraform.FileName, Encoding.UTF8.GetBytes(terraformRoot.ToJsonString()));
        planRoot["terraformSha256"] = changedTerraform.Sha256;
        var changedPlan = DeploymentArtifact.Create(plan.FileName, Encoding.UTF8.GetBytes(planRoot.ToJsonString()));
        var artifacts = rendered.Artifacts.Select(item => item.FileName switch
        {
            "gcp-cloud-run-migration.tf.json" => changedTerraform,
            "gcp-cloud-run-migration.plan.json" => changedPlan,
            _ => item,
        }).ToArray();

        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunBundleValidator.ValidateForObservation(new DeploymentRenderResult(rendered.Target, artifacts)));

        Assert.Equal(mutation.StartsWith("project-number-", StringComparison.Ordinal) ? "ASDEPLOY170" : "ASDEPLOY171", error.Diagnostic.Code);
    }

    [Fact]
    public void LegacyBundleGuardRejectsNullRenderResult()
    {
        // protects=internal legacy verifier null boundary; fails_when=null dereference escapes the documented diagnostic; no_claim=observation limits apply to legacy verification.
        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunDeploymentTarget.ValidateBundleLegacy(null!));

        Assert.Equal("ASDEPLOY163", error.Diagnostic.Code);
    }

    [Fact]
    public async Task LegacyBundleGuardRejectsWrongTargetBeforeArtifactParsing()
    {
        // protects=legacy target binding; fails_when=another target's bundle is interpreted as Cloud Run; no_claim=the artifacts are otherwise authentic.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunDeploymentTarget.ValidateBundleLegacy(new DeploymentRenderResult("other-target", rendered.Artifacts)));

        Assert.Equal("ASDEPLOY150", error.Diagnostic.Code);
    }

    [Fact]
    public async Task LegacyBundleGuardRejectsMalformedPlanJson()
    {
        // protects=legacy plan parse failure mapping; fails_when=invalid serialized plan leaks JSON internals; no_claim=legacy JSON gets finite observer admission.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var artifacts = rendered.Artifacts.Select(item => item.FileName == "gcp-cloud-run-migration.plan.json"
            ? DeploymentArtifact.Create(item.FileName, "{"u8.ToArray())
            : item).ToArray();

        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunDeploymentTarget.ValidateBundleLegacy(new DeploymentRenderResult(rendered.Target, artifacts)));

        Assert.Equal("ASDEPLOY151", error.Diagnostic.Code);
    }

    [Fact]
    public async Task LegacyBundleGuardRejectsDuplicateExpectedLogicalId()
    {
        // protects=legacy plan duplicate logical identity; fails_when=ambiguous jobs overwrite in provider comparison; no_claim=observation schema validation was skipped.
        using var fixture = new ProfileFixture(null);
        var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(fixture.Request());
        var artifact = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.plan.json");
        var root = System.Text.Json.Nodes.JsonNode.Parse(artifact.Content)!.AsObject();
        root["expected"]![0]!["logicalId"] = "one";
        var second = System.Text.Json.Nodes.JsonNode.Parse(root["expected"]![0]!.ToJsonString());
        root["expected"]!.AsArray().Add(second);
        var changedPlan = DeploymentArtifact.Create(artifact.FileName, Encoding.UTF8.GetBytes(root.ToJsonString()));
        var artifacts = rendered.Artifacts.Select(item => item.FileName == artifact.FileName ? changedPlan : item).ToArray();

        var error = Assert.Throws<DeploymentValidationException>(() => GcpCloudRunDeploymentTarget.ValidateBundleLegacy(new DeploymentRenderResult(rendered.Target, artifacts)));

        Assert.Equal("ASDEPLOY152", error.Diagnostic.Code);
    }

    private static DeploymentRenderResult WithTerraform(DeploymentRenderResult rendered, string original, string replacement)
    {
        var terraform = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.tf.json");
        var terraformText = Encoding.UTF8.GetString(terraform.Content).Replace(original, replacement, StringComparison.Ordinal);
        Assert.NotEqual(Encoding.UTF8.GetString(terraform.Content), terraformText);
        var changedTerraform = DeploymentArtifact.Create(terraform.FileName, Encoding.UTF8.GetBytes(terraformText));
        var plan = rendered.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.plan.json");
        var planText = Encoding.UTF8.GetString(plan.Content).Replace(terraform.Sha256, changedTerraform.Sha256, StringComparison.Ordinal);
        var changedPlan = DeploymentArtifact.Create(plan.FileName, Encoding.UTF8.GetBytes(planText));
        return new DeploymentRenderResult(rendered.Target, rendered.Artifacts.Select(item => item.FileName == terraform.FileName ? changedTerraform : item.FileName == plan.FileName ? changedPlan : item).ToArray());
    }
}
