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
