using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Deployment;
using ForgeTrust.AppSurface.Deployment.GcpCloudRun;

public sealed class GcpCloudRunDeploymentTargetTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "appsurface-gcp-tests", Guid.NewGuid().ToString("N"));

    public GcpCloudRunDeploymentTargetTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task LoadAsync_ParsesClosedNonSecretProfile()
    {
        var profile = await GcpCloudRunBindingProfile.LoadAsync(await WriteProfileAsync(), "Staging");

        Assert.Equal("Staging", profile.Environment);
        Assert.Equal("skoolit-staging", profile.Project);
        Assert.Equal("skoolit-migrations-staging", profile.Jobs["skoolit-migrations"]);
        Assert.Equal("latest", profile.Secrets["skoolit-connection"].VersionMode);
    }

    [Theory]
    [InlineData("\"unknown\":true,", "ASDEPLOY140")]
    [InlineData("\"passwordValue\":\"do-not-store\",", "ASDEPLOY139")]
    public async Task LoadAsync_RejectsUnknownAndSecretShapedFields(string injected, string code)
    {
        var path = await WriteProfileAsync(injected);
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => GcpCloudRunBindingProfile.LoadAsync(path, "Staging"));
        Assert.Equal(code, error.Diagnostic.Code);
        Assert.DoesNotContain("do-not-store", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_RejectsMalformedLogicalMapKey()
    {
        var path = await WriteProfileAsync();
        await ReplaceAsync(path, "\"migration-runtime\": \"migration@skoolit-staging.iam.gserviceaccount.com\"", "\"INVALID\": \"migration@skoolit-staging.iam.gserviceaccount.com\"");
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => GcpCloudRunBindingProfile.LoadAsync(path, "Staging"));
        Assert.Equal("ASDEPLOY141", error.Diagnostic.Code);
    }

    [Fact]
    public async Task LoadAsync_RejectsEnvironmentMismatch()
    {
        var path = await WriteProfileAsync();
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => GcpCloudRunBindingProfile.LoadAsync(path, "Production"));
        Assert.Equal("ASDEPLOY134", error.Diagnostic.Code);
    }

    [Fact]
    public async Task LoadAsync_RejectsMissingFile()
    {
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => GcpCloudRunBindingProfile.LoadAsync(Path.Join(_root, "missing.json"), "Staging"));
        Assert.Equal("ASDEPLOY130", error.Diagnostic.Code);
    }

    [Theory]
    [InlineData("\"project\": \"invalid/project\"", "\"project\": \"skoolit-staging\"", "ASDEPLOY141")]
    [InlineData("\"project\": \"${var.project}\"", "\"project\": \"skoolit-staging\"", "ASDEPLOY141")]
    [InlineData("\"cloudSqlInstanceConnectionName\": \"bad\"", "\"cloudSqlInstanceConnectionName\": \"skoolit-staging:us-central1:primary\"", "ASDEPLOY135")]
    [InlineData("\"cloudSqlInstanceConnectionName\": \"skoolit-staging:us-central1:${var.instance}\"", "\"cloudSqlInstanceConnectionName\": \"skoolit-staging:us-central1:primary\"", "ASDEPLOY141")]
    [InlineData("\"network\": \"${file(\\\"/tmp/probe\\\")}\"", "\"network\": \"projects/skoolit/global/networks/main\"", "ASDEPLOY141")]
    [InlineData("\"egress\": \"BAD\"", "\"egress\": \"PRIVATE_RANGES_ONLY\"", "ASDEPLOY136")]
    [InlineData("\"migration-runtime\": \"bad\"", "\"migration-runtime\": \"migration@skoolit-staging.iam.gserviceaccount.com\"", "ASDEPLOY142")]
    public async Task LoadAsync_RejectsMalformedPhysicalBindings(string replacement, string original, string code)
    {
        var path = await WriteProfileAsync();
        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace(original, replacement, StringComparison.Ordinal));
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => GcpCloudRunBindingProfile.LoadAsync(path, "Staging"));
        Assert.Equal(code, error.Diagnostic.Code);
    }

    [Fact]
    public async Task LoadAsync_RejectsUnsupportedSecretMode()
    {
        var path = await WriteProfileAsync();
        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("\"latest\"", "\"7\"", StringComparison.Ordinal));
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => GcpCloudRunBindingProfile.LoadAsync(path, "Staging"));
        Assert.Equal("ASDEPLOY137", error.Diagnostic.Code);
    }

    [Fact]
    public async Task LoadAsync_RejectsUnsupportedSchema()
    {
        var path = await WriteProfileAsync();
        await ReplaceAsync(path, "\"schemaVersion\": \"1.0\"", "\"schemaVersion\": \"2.0\"");
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => GcpCloudRunBindingProfile.LoadAsync(path, "Staging"));
        Assert.Equal("ASDEPLOY133", error.Diagnostic.Code);
    }

    [Theory]
    [InlineData("\"jobs\": { \"skoolit-migrations\": 7 }", "\"jobs\": { \"skoolit-migrations\": \"skoolit-migrations-staging\" }")]
    [InlineData("\"jobs\": { \"skoolit-migrations\": \" \" }", "\"jobs\": { \"skoolit-migrations\": \"skoolit-migrations-staging\" }")]
    [InlineData("\"jobs\": \"bad\"", "\"jobs\": { \"skoolit-migrations\": \"skoolit-migrations-staging\" }")]
    [InlineData("\"project\": 7", "\"project\": \"skoolit-staging\"")]
    [InlineData("\"project\": \" \"", "\"project\": \"skoolit-staging\"")]
    public async Task LoadAsync_RejectsWrongJsonKindsAndBlankValues(string replacement, string original)
    {
        var path = await WriteProfileAsync();
        await ReplaceAsync(path, original, replacement);
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => GcpCloudRunBindingProfile.LoadAsync(path, "Staging"));
        Assert.Equal("ASDEPLOY132", error.Diagnostic.Code);
    }

    [Fact]
    public async Task LoadAsync_RejectsMissingRequiredObject()
    {
        var path = await WriteProfileAsync();
        await ReplaceAsync(path, "\"jobs\": { \"skoolit-migrations\": \"skoolit-migrations-staging\" },", "");
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => GcpCloudRunBindingProfile.LoadAsync(path, "Staging"));
        Assert.Equal("ASDEPLOY132", error.Diagnostic.Code);
    }

    [Fact]
    public async Task LoadAsync_RejectsNonObjectRoot()
    {
        var path = Path.Join(_root, "array.json");
        await File.WriteAllTextAsync(path, "[]");
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => GcpCloudRunBindingProfile.LoadAsync(path, "Staging"));
        Assert.Equal("ASDEPLOY132", error.Diagnostic.Code);
    }

    [Fact]
    public async Task LoadAsync_AcceptsAllTrafficEgress()
    {
        var path = await WriteProfileAsync();
        await ReplaceAsync(path, "\"egress\": \"PRIVATE_RANGES_ONLY\"", "\"egress\": \"ALL_TRAFFIC\"");
        var profile = await GcpCloudRunBindingProfile.LoadAsync(path, "Staging");
        Assert.Equal("ALL_TRAFFIC", profile.Network.Egress);
    }

    [Fact]
    public async Task LoadAsync_RejectsFileSymlink()
    {
        var real = await WriteProfileAsync();
        var link = Path.Join(_root, "profile-link.json");
        File.CreateSymbolicLink(link, real);
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => GcpCloudRunBindingProfile.LoadAsync(link, "Staging"));
        Assert.Equal("ASDEPLOY131", error.Diagnostic.Code);
    }

    [Fact]
    public async Task LoadAsync_RejectsImmediateDirectorySymlink()
    {
        var realDirectory = Path.Join(_root, "real-profile-directory");
        Directory.CreateDirectory(realDirectory);
        var real = Path.Join(realDirectory, "profile.json");
        File.Copy(await WriteProfileAsync(), real);
        var linkedDirectory = Path.Join(_root, "linked-profile-directory");
        Directory.CreateSymbolicLink(linkedDirectory, realDirectory);
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => GcpCloudRunBindingProfile.LoadAsync(Path.Join(linkedDirectory, "profile.json"), "Staging"));
        Assert.Equal("ASDEPLOY131", error.Diagnostic.Code);
    }

    [Fact]
    public async Task LoadAsync_RejectsNestedAncestorSymlinkWithinTrustedRoot()
    {
        var outside = Path.Join(_root, "outside");
        var nested = Path.Join(outside, "nested");
        Directory.CreateDirectory(nested);
        File.Copy(await WriteProfileAsync(), Path.Join(nested, "profile.json"));
        var appHost = Path.Join(_root, "apphost");
        Directory.CreateDirectory(appHost);
        Directory.CreateSymbolicLink(Path.Join(appHost, "config"), outside);

        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() =>
            GcpCloudRunBindingProfile.LoadAsync(Path.Join(appHost, "config", "nested", "profile.json"), "Staging", appHost));

        Assert.Equal("ASDEPLOY131", error.Diagnostic.Code);
    }

    [Fact]
    public async Task LoadAsync_RejectsProfileOutsideTrustedRoot()
    {
        var path = await WriteProfileAsync();
        var appHost = Path.Join(_root, "apphost");
        Directory.CreateDirectory(appHost);

        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() =>
            GcpCloudRunBindingProfile.LoadAsync(path, "Staging", appHost));

        Assert.Equal("ASDEPLOY131", error.Diagnostic.Code);
    }

    [Fact]
    public async Task LoadAsync_RejectsDuplicateProperties()
    {
        var path = await DuplicateProfileAsync();
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => GcpCloudRunBindingProfile.LoadAsync(path, "Staging"));
        Assert.Equal("ASDEPLOY143", error.Diagnostic.Code);
    }

    [Fact]
    public async Task LoadAsync_MalformedJsonDoesNotEchoInput()
    {
        const string marker = "private-marker";
        var path = Path.Join(_root, "malformed.json");
        await File.WriteAllTextAsync(path, "{\"" + marker + "\":");
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => GcpCloudRunBindingProfile.LoadAsync(path, "Staging"));
        Assert.Equal("ASDEPLOY132", error.Diagnostic.Code);
        Assert.DoesNotContain(marker, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_ProducesThreeStableCrossHashedArtifacts()
    {
        var target = new GcpCloudRunDeploymentTarget(new RecordingRunner());
        var profile = await WriteProfileAsync();
        var first = await target.RenderAsync(Request(profile, Intent()));
        var second = await target.RenderAsync(Request(profile, Intent()));

        Assert.Equal(["deployment-intent.v1.json", "gcp-cloud-run-migration.tf.json", "gcp-cloud-run-migration.plan.json"], first.Artifacts.Select(item => item.FileName));
        Assert.Equal(first.Artifacts.Select(item => item.Content), second.Artifacts.Select(item => item.Content), ByteArrayComparer.Instance);
        Assert.All(first.Artifacts, item => Assert.False(item.Content.AsSpan().StartsWith(Encoding.UTF8.Preamble)));
        Assert.All(first.Artifacts, item => Assert.Equal((byte)'\n', item.Content[^1]));
        var terraform = Text(first, "gcp-cloud-run-migration.tf.json");
        Assert.Contains("google_cloud_run_v2_job", terraform, StringComparison.Ordinal);
        Assert.Contains("appsurface_migration", terraform, StringComparison.Ordinal);
        Assert.Contains("/cloudsql", terraform, StringComparison.Ordinal);
        Assert.Contains("PRIVATE_RANGES_ONLY", terraform, StringComparison.Ordinal);
        Assert.Contains("\"dynamic\"", terraform, StringComparison.Ordinal);
        Assert.Contains("\"value_source\"", terraform, StringComparison.Ordinal);
        Assert.DoesNotContain("concat(", terraform, StringComparison.Ordinal);
        Assert.DoesNotContain("connection-string-value", terraform, StringComparison.Ordinal);
        var plan = Text(first, "gcp-cloud-run-migration.plan.json");
        Assert.Contains(first.Artifacts.Single(item => item.FileName == "deployment-intent.v1.json").Sha256, plan, StringComparison.Ordinal);
        Assert.Contains(first.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.tf.json").Sha256, plan, StringComparison.Ordinal);
        Assert.Contains("google_cloud_run_v2_job.appsurface_migration", plan, StringComparison.Ordinal);
        Assert.Contains("private-network", plan, StringComparison.Ordinal);
        Assert.Equal("f1e05da61922c2d74374208b9df1fdd544426844e725b4f1638b8a7867749188", first.Artifacts.Single(item => item.FileName == "deployment-intent.v1.json").Sha256);
        Assert.Equal("c2b066c8898ea9535dcdc7d3d68d26da1f4fec335bb37aaba3be34fe4fcdd1ae", first.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.tf.json").Sha256);
        Assert.Equal("83530265b7d6d0b5d27d58dee6ffadfb60762e87834fe378c3192aeb8025abb0", first.Artifacts.Single(item => item.FileName == "gcp-cloud-run-migration.plan.json").Sha256);
    }

    [Fact]
    public async Task RenderAsync_ConfinesProfileToTrustedRoot()
    {
        var profile = await WriteProfileAsync();
        var request = new DeploymentRenderRequest(Intent(), profile, _root, "0.1.0-test", _root);

        var result = await new GcpCloudRunDeploymentTarget(new RecordingRunner()).RenderAsync(request);

        Assert.Equal(3, result.Artifacts.Count);
    }

    [Fact]
    public async Task RenderAndVerify_PreserveAndCompareNonSecretEnvironment()
    {
        var intent = Intent(new Dictionary<string, string> { ["FEATURE_X"] = "on" });
        var describe = DescribeJson(true).Replace("\"env\": [", "\"env\": [{ \"name\": \"FEATURE_X\", \"value\": \"off\" },", StringComparison.Ordinal);
        var target = new GcpCloudRunDeploymentTarget(new RecordingRunner(describe, IamJson()));
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), intent));

        Assert.Contains("FEATURE_X", Text(rendered, "gcp-cloud-run-migration.tf.json"), StringComparison.Ordinal);
        var result = await target.VerifyAsync(new DeploymentVerifyRequest(rendered, DeploymentParityMode.Shadow));
        Assert.False(result.IsMatch);
        Assert.Contains(result.Diagnostics, item => item.Cause.Contains("environment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VerifyAsync_RejectsExpectedPlanDriftFromHashedTerraformBeforeCloudCall()
    {
        var runner = new RecordingRunner();
        var target = new GcpCloudRunDeploymentTarget(runner);
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), Intent()));
        var artifacts = rendered.Artifacts.ToArray();
        var index = Array.FindIndex(artifacts, item => item.FileName == "gcp-cloud-run-migration.plan.json");
        var changed = Encoding.UTF8.GetString(artifacts[index].Content).Replace(new string('b', 64), new string('c', 64), StringComparison.Ordinal);
        artifacts[index] = DeploymentArtifact.Create(artifacts[index].FileName, Encoding.UTF8.GetBytes(changed));

        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => target.VerifyAsync(new DeploymentVerifyRequest(rendered with { Artifacts = artifacts }, DeploymentParityMode.Shadow)));
        Assert.Equal("ASDEPLOY152", error.Diagnostic.Code);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task VerifyAsync_ReportsAdditionalCommandElementsAsDrift()
    {
        var describe = DescribeJson(true).Replace("\"command\": [\"dotnet\"]", "\"command\": [\"dotnet\", \"unexpected\"]", StringComparison.Ordinal);
        var target = new GcpCloudRunDeploymentTarget(new RecordingRunner(describe, IamJson()));
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), Intent()));

        var result = await target.VerifyAsync(new DeploymentVerifyRequest(rendered, DeploymentParityMode.Owned));

        Assert.False(result.IsMatch);
        Assert.Contains(result.Diagnostics, item => item.Cause.Contains("commandElements", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VerifyAsync_ReportsAdditionalSecretEnvironmentAsDrift()
    {
        var additionalSecret = "{ \"name\": \"UNEXPECTED_SECRET\", \"valueSource\": { \"secretKeyRef\": { \"secret\": \"unexpected\", \"version\": \"latest\" } } }, ";
        var describe = DescribeJson(true).Replace("\"env\": [", "\"env\": [" + additionalSecret, StringComparison.Ordinal);
        var target = new GcpCloudRunDeploymentTarget(new RecordingRunner(describe, IamJson()));
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), Intent()));

        var result = await target.VerifyAsync(new DeploymentVerifyRequest(rendered, DeploymentParityMode.Owned));

        Assert.False(result.IsMatch);
        Assert.Contains(result.Diagnostics, item => item.Cause.Contains("secretEnvironments", StringComparison.Ordinal));
    }

    [Fact]
    public void Target_ExposesExpectedIdentityAndCapabilities()
    {
        var target = GcpCloudRunDeploymentTarget.Create();
        Assert.Equal("gcp-cloud-run", target.Name);
        Assert.Equal(3, target.Capabilities.Count);
        Assert.IsNotType<HashSet<DeploymentCapability>>(target.Capabilities);
        Assert.Throws<NotSupportedException>(() => ((ISet<DeploymentCapability>)target.Capabilities).Add((DeploymentCapability)42));
        Assert.Throws<ArgumentNullException>(() => new GcpCloudRunDeploymentTarget(null!));
    }

    [Fact]
    public async Task RenderAsync_RejectsMissingPhysicalBinding()
    {
        var path = await WriteProfileAsync();
        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("\"skoolit-migrations\": \"skoolit-migrations-staging\"", "\"other-job\": \"other\"", StringComparison.Ordinal));
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => new GcpCloudRunDeploymentTarget().RenderAsync(Request(path, Intent())));
        Assert.Equal("ASDEPLOY159", error.Diagnostic.Code);
    }

    [Fact]
    public async Task RenderAsync_RejectsDuplicatePhysicalJobBinding()
    {
        var first = Intent().MigrationJobs.Single();
        var second = new MigrationJobIntent(new DeploymentLogicalId("second-migration"), first.Phase, first.Image, first.Command, first.Arguments, first.Execution, first.ConnectionSecret, first.Database, first.ServiceIdentity);
        var intent = new DeploymentIntent("Staging", new SourceRevision(new string('a', 40)), [first, second]);
        var path = await WriteProfileAsync();
        await ReplaceAsync(path, "\"jobs\": { \"skoolit-migrations\": \"skoolit-migrations-staging\" }", "\"jobs\": { \"skoolit-migrations\": \"skoolit-migrations-staging\", \"second-migration\": \"skoolit-migrations-staging\" }");

        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => new GcpCloudRunDeploymentTarget().RenderAsync(Request(path, intent)));
        Assert.Equal("ASDEPLOY158", error.Diagnostic.Code);
    }

    [Theory]
    [InlineData("\"serviceAccounts\": { \"other\": \"others@skoolit-staging.iam.gserviceaccount.com\" }", "\"serviceAccounts\": { \"migration-runtime\": \"migration@skoolit-staging.iam.gserviceaccount.com\" }")]
    [InlineData("\"secrets\": { \"other\": { \"secretId\": \"other\", \"versionMode\": \"latest\" } }", "\"secrets\": { \"skoolit-connection\": { \"secretId\": \"skoolit-connection\", \"versionMode\": \"latest\" } }")]
    public async Task RenderAsync_RejectsMissingServiceAccountOrSecret(string replacement, string original)
    {
        var path = await WriteProfileAsync();
        await ReplaceAsync(path, original, replacement);
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => new GcpCloudRunDeploymentTarget().RenderAsync(Request(path, Intent())));
        Assert.Equal("ASDEPLOY159", error.Diagnostic.Code);
    }

    [Fact]
    public async Task VerifyAsync_ShadowIgnoresProvenanceButOwnedReportsIt()
    {
        var runner = new RecordingRunner(DescribeJson(labels: false), IamJson(), DescribeJson(labels: false), IamJson());
        var target = new GcpCloudRunDeploymentTarget(runner);
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), Intent()));

        var shadow = await target.VerifyAsync(new DeploymentVerifyRequest(rendered, DeploymentParityMode.Shadow));
        var owned = await target.VerifyAsync(new DeploymentVerifyRequest(rendered, DeploymentParityMode.Owned));

        Assert.True(shadow.IsMatch);
        Assert.False(owned.IsMatch);
        Assert.Equal(4, runner.Calls.Count);
        Assert.All(runner.Calls, args => Assert.Contains("--format=json", args));
        Assert.All(runner.Calls, args => Assert.Contains("--quiet", args));
        Assert.Equal("not-independently-verified", shadow.AuthorizationStatus);
    }

    [Fact]
    public async Task VerifyAsync_OwnedMatchesCanonicalProvenance()
    {
        var target = new GcpCloudRunDeploymentTarget(new RecordingRunner(DescribeJson(labels: true), IamJson()));
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), Intent()));
        var result = await target.VerifyAsync(new DeploymentVerifyRequest(rendered, DeploymentParityMode.Owned));
        Assert.True(result.IsMatch);
        Assert.Equal(21, result.ComparedFields);
    }

    [Theory]
    [InlineData("allUsers")]
    [InlineData("allAuthenticatedUsers")]
    public async Task VerifyAsync_RejectsPublicPrincipal(string principal)
    {
        var target = new GcpCloudRunDeploymentTarget(new RecordingRunner(DescribeJson(true), IamJson(principal)));
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), Intent()));
        var result = await target.VerifyAsync(new DeploymentVerifyRequest(rendered, DeploymentParityMode.Shadow));
        Assert.False(result.IsMatch);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ASDEPLOY153");
    }

    [Fact]
    public async Task VerifyAsync_RejectsTamperingBeforeCloudCall()
    {
        var runner = new RecordingRunner();
        var target = new GcpCloudRunDeploymentTarget(runner);
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), Intent()));
        var artifacts = rendered.Artifacts.ToArray();
        var changed = artifacts[0].Content;
        changed[0] ^= 1;
        artifacts[0] = DeploymentArtifact.Create(artifacts[0].FileName, changed);
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => target.VerifyAsync(new DeploymentVerifyRequest(rendered with { Artifacts = artifacts }, DeploymentParityMode.Shadow)));
        Assert.Equal("ASDEPLOY152", error.Diagnostic.Code);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task VerifyAsync_RejectsWrongTargetAndArtifactCountBeforeCloudCall()
    {
        var runner = new RecordingRunner();
        var target = new GcpCloudRunDeploymentTarget(runner);
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), Intent()));
        var wrongTarget = await Assert.ThrowsAsync<DeploymentValidationException>(() => target.VerifyAsync(new DeploymentVerifyRequest(rendered with { Target = "other" }, DeploymentParityMode.Shadow)));
        Assert.Equal("ASDEPLOY150", wrongTarget.Diagnostic.Code);
        var incomplete = await Assert.ThrowsAsync<DeploymentValidationException>(() => target.VerifyAsync(new DeploymentVerifyRequest(rendered with { Artifacts = rendered.Artifacts.Take(2).ToArray() }, DeploymentParityMode.Shadow)));
        Assert.Equal("ASDEPLOY163", incomplete.Diagnostic.Code);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task VerifyAsync_ConvertsSafeRunnerException()
    {
        var runner = new ThrowingRunner();
        var target = new GcpCloudRunDeploymentTarget(runner);
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), Intent()));
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => target.VerifyAsync(new DeploymentVerifyRequest(rendered, DeploymentParityMode.Shadow)));
        Assert.Equal("ASDEPLOY166", error.Diagnostic.Code);
    }

    [Fact]
    public async Task VerifyAsync_RejectsPlanSchemaBeforeCloudCall()
    {
        var runner = new RecordingRunner();
        var target = new GcpCloudRunDeploymentTarget(runner);
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), Intent()));
        var artifacts = rendered.Artifacts.ToArray();
        var planIndex = Array.FindIndex(artifacts, item => item.FileName == "gcp-cloud-run-migration.plan.json");
        var changed = Encoding.UTF8.GetString(artifacts[planIndex].Content).Replace("gcp-cloud-run-migration-plan.v1.json", "other.v1.json", StringComparison.Ordinal);
        artifacts[planIndex] = DeploymentArtifact.Create(artifacts[planIndex].FileName, Encoding.UTF8.GetBytes(changed));
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => target.VerifyAsync(new DeploymentVerifyRequest(new DeploymentRenderResult(rendered.Target, artifacts), DeploymentParityMode.Shadow)));
        Assert.Equal("ASDEPLOY152", error.Diagnostic.Code);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task VerifyAsync_RejectsPlanProjectMismatchBeforeCloudCall()
    {
        var runner = new RecordingRunner();
        var target = new GcpCloudRunDeploymentTarget(runner);
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), Intent()));
        var artifacts = rendered.Artifacts.ToArray();
        var planIndex = Array.FindIndex(artifacts, item => item.FileName == "gcp-cloud-run-migration.plan.json");
        var changed = Encoding.UTF8.GetString(artifacts[planIndex].Content).Replace("\"project\": \"skoolit-staging\"", "\"project\": \"other-project\"", StringComparison.Ordinal);
        artifacts[planIndex] = DeploymentArtifact.Create(artifacts[planIndex].FileName, Encoding.UTF8.GetBytes(changed));
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => target.VerifyAsync(new DeploymentVerifyRequest(rendered with { Artifacts = artifacts }, DeploymentParityMode.Shadow)));
        Assert.Equal("ASDEPLOY152", error.Diagnostic.Code);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task VerifyAsync_RejectsMalformedCrossArtifactStructureBeforeCloudCall()
    {
        var runner = new RecordingRunner();
        var target = new GcpCloudRunDeploymentTarget(runner);
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), Intent()));
        var artifacts = rendered.Artifacts.ToArray();
        var terraformIndex = Array.FindIndex(artifacts, item => item.FileName == "gcp-cloud-run-migration.tf.json");
        var planIndex = Array.FindIndex(artifacts, item => item.FileName == "gcp-cloud-run-migration.plan.json");
        var priorTerraformHash = artifacts[terraformIndex].Sha256;
        artifacts[terraformIndex] = DeploymentArtifact.Create(artifacts[terraformIndex].FileName, "{}\n"u8.ToArray());
        var changedPlan = Encoding.UTF8.GetString(artifacts[planIndex].Content)
            .Replace(priorTerraformHash, artifacts[terraformIndex].Sha256, StringComparison.Ordinal);
        artifacts[planIndex] = DeploymentArtifact.Create(artifacts[planIndex].FileName, Encoding.UTF8.GetBytes(changedPlan));

        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() =>
            target.VerifyAsync(new DeploymentVerifyRequest(rendered with { Artifacts = artifacts }, DeploymentParityMode.Shadow)));

        Assert.Equal("ASDEPLOY152", error.Diagnostic.Code);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task VerifyAsync_RejectsNullPlanAndWrongArtifactNameBeforeCloudCall()
    {
        var runner = new RecordingRunner();
        var target = new GcpCloudRunDeploymentTarget(runner);
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), Intent()));
        var artifacts = rendered.Artifacts.ToArray();
        var planIndex = Array.FindIndex(artifacts, item => item.FileName == "gcp-cloud-run-migration.plan.json");
        artifacts[planIndex] = DeploymentArtifact.Create(artifacts[planIndex].FileName, "null"u8.ToArray());
        var malformed = await Assert.ThrowsAsync<DeploymentValidationException>(() => target.VerifyAsync(new DeploymentVerifyRequest(rendered with { Artifacts = artifacts }, DeploymentParityMode.Shadow)));
        Assert.Equal("ASDEPLOY151", malformed.Diagnostic.Code);

        artifacts = rendered.Artifacts.ToArray();
        artifacts[0] = DeploymentArtifact.Create("unexpected.json", artifacts[0].Content);
        var missing = await Assert.ThrowsAsync<DeploymentValidationException>(() => target.VerifyAsync(new DeploymentVerifyRequest(rendered with { Artifacts = artifacts }, DeploymentParityMode.Shadow)));
        Assert.Equal("ASDEPLOY163", missing.Diagnostic.Code);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task VerifyAsync_NormalizesStringCountsAndMissingOptionalArrays()
    {
        var describe = DescribeJson(labels: true)
            .Replace("\"taskCount\": 1", "\"taskCount\": \"1\"", StringComparison.Ordinal)
            .Replace("\"parallelism\": 1", "\"parallelism\": \"1\"", StringComparison.Ordinal)
            .Replace(", \"args\": [\"Skoolit.Migrations.dll\"]", "", StringComparison.Ordinal);
        var target = new GcpCloudRunDeploymentTarget(new RecordingRunner(describe, IamJson()));
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), Intent()));
        var result = await target.VerifyAsync(new DeploymentVerifyRequest(rendered, DeploymentParityMode.Shadow));
        Assert.False(result.IsMatch);
        Assert.Contains(result.Diagnostics, item => item.Code == "ASDEPLOY161");
    }

    [Fact]
    public async Task VerifyAsync_NormalizesEmptyCommandArray()
    {
        var describe = DescribeJson(labels: true).Replace("\"command\": [\"dotnet\"]", "\"command\": []", StringComparison.Ordinal);
        var target = new GcpCloudRunDeploymentTarget(new RecordingRunner(describe, IamJson()));
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), Intent()));
        var result = await target.VerifyAsync(new DeploymentVerifyRequest(rendered, DeploymentParityMode.Shadow));
        Assert.False(result.IsMatch);
    }

    [Fact]
    public async Task VerifyAsync_NormalizesMissingLabels()
    {
        var describe = DescribeJson(labels: true).Replace("\"labels\": { \"appsurface-environment\": \"staging\", \"appsurface-source-revision\": \"" + new string('a', 40) + "\" },", "", StringComparison.Ordinal);
        var target = new GcpCloudRunDeploymentTarget(new RecordingRunner(describe, IamJson()));
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), Intent()));
        var result = await target.VerifyAsync(new DeploymentVerifyRequest(rendered, DeploymentParityMode.Owned));
        Assert.False(result.IsMatch);
    }

    [Theory]
    [InlineData("job not found", "ASDEPLOY154")]
    [InlineData("NOT_FOUND: missing", "ASDEPLOY154")]
    [InlineData("PERMISSION_DENIED", "ASDEPLOY155")]
    [InlineData("authentication required", "ASDEPLOY156")]
    [InlineData("other", "ASDEPLOY157")]
    public async Task VerifyAsync_ClassifiesGcloudFailures(string stderr, string code)
    {
        var target = new GcpCloudRunDeploymentTarget(new RecordingRunner(new GcloudCommandResult(1, "", stderr)));
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), Intent()));
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => target.VerifyAsync(new DeploymentVerifyRequest(rendered, DeploymentParityMode.Shadow)));
        Assert.Equal(code, error.Diagnostic.Code);
        Assert.DoesNotContain(stderr, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_ClassifiesMissingGcloudErrorText()
    {
        var target = new GcpCloudRunDeploymentTarget(new RecordingRunner(new GcloudCommandResult(1, "", null!)));
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), Intent()));

        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() =>
            target.VerifyAsync(new DeploymentVerifyRequest(rendered, DeploymentParityMode.Shadow)));

        Assert.Equal("ASDEPLOY157", error.Diagnostic.Code);
    }

    [Fact]
    public async Task VerifyAsync_RejectsMalformedDescribe()
    {
        var target = new GcpCloudRunDeploymentTarget(new RecordingRunner("{}", IamJson()));
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), Intent()));
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => target.VerifyAsync(new DeploymentVerifyRequest(rendered, DeploymentParityMode.Shadow)));
        Assert.Equal("ASDEPLOY160", error.Diagnostic.Code);
    }

    [Fact]
    public async Task VerifyAsync_RejectsMalformedIam()
    {
        var target = new GcpCloudRunDeploymentTarget(new RecordingRunner(DescribeJson(true), "{\"bindings\":{}}"));
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), Intent()));
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => target.VerifyAsync(new DeploymentVerifyRequest(rendered, DeploymentParityMode.Shadow)));
        Assert.Equal("ASDEPLOY162", error.Diagnostic.Code);
    }

    [Fact]
    public async Task VerifyAsync_AcceptsIamPolicyWithNoBindings()
    {
        var target = new GcpCloudRunDeploymentTarget(new RecordingRunner(DescribeJson(true), "{}"));
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), Intent()));
        var result = await target.VerifyAsync(new DeploymentVerifyRequest(rendered, DeploymentParityMode.Shadow));
        Assert.True(result.IsMatch);
    }

    [Fact]
    public async Task VerifyAsync_RejectsUnknownParityModeBeforeCloudCall()
    {
        var runner = new RecordingRunner();
        var target = new GcpCloudRunDeploymentTarget(runner);
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), Intent()));
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => target.VerifyAsync(new DeploymentVerifyRequest(rendered, (DeploymentParityMode)42)));
        Assert.Equal("ASDEPLOY164", error.Diagnostic.Code);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task RenderAsync_RejectsEnvironmentThatIsNotAGcpLabel()
    {
        var profile = await WriteProfileAsync();
        foreach (var environment in new[] { "QA West", new string('a', 64) })
        {
            var invalid = new DeploymentIntent(environment, new SourceRevision(new string('a', 40)), Intent().MigrationJobs);
            var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => new GcpCloudRunDeploymentTarget().RenderAsync(Request(profile, invalid)));
            Assert.Equal("ASDEPLOY167", error.Diagnostic.Code);
        }
    }

    [Fact]
    public async Task RenderAsync_RejectsRuntimeAndCredentialEnvironmentOverrides()
    {
        var profile = await WriteProfileAsync();
        foreach (var name in new[] { "CLOUD_RUN_JOB", "GOOGLE_APPLICATION_CREDENTIALS" })
        {
            var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => new GcpCloudRunDeploymentTarget().RenderAsync(Request(profile, Intent(new Dictionary<string, string> { [name] = "override" }))));
            Assert.Equal("ASDEPLOY168", error.Diagnostic.Code);
        }
    }

    [Fact]
    public async Task PublicRequestsRejectMissingRequiredInputs()
    {
        var target = new GcpCloudRunDeploymentTarget();
        await Assert.ThrowsAsync<ArgumentNullException>(() => target.RenderAsync(new DeploymentRenderRequest(null!, "profile.json", _root, "test")));
        await Assert.ThrowsAsync<ArgumentException>(() => target.RenderAsync(new DeploymentRenderRequest(Intent(), "profile.json", _root, " ")));
        var missingRender = await Assert.ThrowsAsync<DeploymentValidationException>(() => target.VerifyAsync(new DeploymentVerifyRequest(null!, DeploymentParityMode.Shadow)));
        Assert.Equal("ASDEPLOY163", missingRender.Diagnostic.Code);
        var missingArtifacts = await Assert.ThrowsAsync<DeploymentValidationException>(() => target.VerifyAsync(new DeploymentVerifyRequest(new DeploymentRenderResult(target.Name, null!), DeploymentParityMode.Shadow)));
        Assert.Equal("ASDEPLOY163", missingArtifacts.Diagnostic.Code);
    }

    [Fact]
    public async Task VerifyAsync_RejectsPhysicalJobIdentityMismatch()
    {
        var original = DescribeJson(true);
        var describe = "{\"name\":\"projects/p/locations/r/jobs/other\"," + original[1..];
        var target = new GcpCloudRunDeploymentTarget(new RecordingRunner(describe, IamJson()));
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), Intent()));
        var error = await Assert.ThrowsAsync<DeploymentValidationException>(() => target.VerifyAsync(new DeploymentVerifyRequest(rendered, DeploymentParityMode.Shadow)));
        Assert.Equal("ASDEPLOY160", error.Diagnostic.Code);
    }

    [Fact]
    public async Task VerifyAsync_ReportsOperationalDrift()
    {
        var target = new GcpCloudRunDeploymentTarget(new RecordingRunner(DescribeJson(true).Replace("600s", "9s", StringComparison.Ordinal), IamJson()));
        var rendered = await target.RenderAsync(Request(await WriteProfileAsync(), Intent()));
        var result = await target.VerifyAsync(new DeploymentVerifyRequest(rendered, DeploymentParityMode.Shadow));
        Assert.False(result.IsMatch);
        Assert.Contains(result.Diagnostics, item => item.Code == "ASDEPLOY161");
    }

    private DeploymentRenderRequest Request(string profile, DeploymentIntent intent) => new(intent, profile, _root, "0.1.0-test");

    private static DeploymentIntent Intent(IReadOnlyDictionary<string, string>? environment = null) => new("Staging", new SourceRevision(new string('a', 40)), [new MigrationJobIntent(new DeploymentLogicalId("skoolit-migrations"), DeploymentPhase.CandidatePreparation, new ImmutableImageReference($"us-docker.pkg.dev/skoolit/jobs/migrations@sha256:{new string('b', 64)}"), "dotnet", ["Skoolit.Migrations.dll"], new DeploymentExecutionPolicy(1, 1, 3, TimeSpan.FromMinutes(10)), new SecretBinding(new DeploymentLogicalId("skoolit-connection"), new DeploymentLogicalId("skoolit-connection-parameter"), "ConnectionStrings__skoolit"), new DatabaseBinding(new DeploymentLogicalId("skoolit-db"), "ConnectionStrings:skoolit", new DeploymentLogicalId("skoolit-connection")), new DeploymentLogicalId("migration-runtime"), environment: environment)]);

    private async Task<string> WriteProfileAsync(string injected = "")
    {
        var path = Path.Join(_root, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, $$"""
        {
          {{injected}}
          "schemaVersion": "1.0",
          "environment": "Staging",
          "project": "skoolit-staging",
          "region": "us-central1",
          "jobs": { "skoolit-migrations": "skoolit-migrations-staging" },
          "cloudSqlInstanceConnectionName": "skoolit-staging:us-central1:primary",
          "network": { "network": "projects/skoolit/global/networks/main", "subnetwork": "projects/skoolit/regions/us-central1/subnetworks/main", "egress": "PRIVATE_RANGES_ONLY" },
          "serviceAccounts": { "migration-runtime": "migration@skoolit-staging.iam.gserviceaccount.com" },
          "secrets": { "skoolit-connection": { "secretId": "skoolit-connection", "versionMode": "latest" } }
        }
        """);
        return path;
    }

    private async Task<string> DuplicateProfileAsync()
    {
        var path = await WriteProfileAsync();
        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("\"region\": \"us-central1\",", "\"region\": \"us-central1\", \"region\": \"us-east1\",", StringComparison.Ordinal));
        return path;
    }

    private static async Task ReplaceAsync(string path, string original, string replacement) => await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace(original, replacement, StringComparison.Ordinal));

    private static string DescribeJson(bool labels) => $$"""
    {
      "labels": {{(labels ? "{ \"appsurface-environment\": \"staging\", \"appsurface-source-revision\": \"" + new string('a', 40) + "\" }" : "{}")}},
      "template": {
        "taskCount": 1,
        "parallelism": 1,
        "template": {
          "maxRetries": 3,
          "timeout": "600s",
          "serviceAccount": "migration@skoolit-staging.iam.gserviceaccount.com",
          "vpcAccess": { "networkInterfaces": [{ "network": "projects/skoolit/global/networks/main", "subnetwork": "projects/skoolit/regions/us-central1/subnetworks/main" }], "egress": "PRIVATE_RANGES_ONLY" },
          "volumes": [{ "name": "cloudsql", "cloudSqlInstance": { "instances": ["skoolit-staging:us-central1:primary"] } }],
          "containers": [{ "image": "us-docker.pkg.dev/skoolit/jobs/migrations@sha256:{{new string('b', 64)}}", "command": ["dotnet"], "args": ["Skoolit.Migrations.dll"], "env": [{ "name": "ConnectionStrings__skoolit", "valueSource": { "secretKeyRef": { "secret": "skoolit-connection", "version": "latest" } } }] }]
        }
      }
    }
    """;

    private static string IamJson(string principal = "serviceAccount:ci@example.iam.gserviceaccount.com") => $$"""{"bindings":[{"role":"roles/run.invoker","members":["{{principal}}"]}]}""";
    private static string Text(DeploymentRenderResult result, string name) => Encoding.UTF8.GetString(result.Artifacts.Single(item => item.FileName == name).Content);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Could not delete GCP deployment test directory '{_root}': {exception.GetType().Name} (0x{exception.HResult:x8}).");
        }
    }

    private sealed class RecordingRunner : IGcloudCommandRunner
    {
        private readonly Queue<GcloudCommandResult> _results = new();
        public RecordingRunner() { }
        public RecordingRunner(params string[] output) { foreach (var item in output) _results.Enqueue(new GcloudCommandResult(0, item, "")); }
        public RecordingRunner(params GcloudCommandResult[] output) { foreach (var item in output) _results.Enqueue(item); }
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Task<GcloudCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) { Calls.Add(arguments); return Task.FromResult(_results.Count == 0 ? new GcloudCommandResult(0, "{}", "") : _results.Dequeue()); }
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();
        public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] obj) => obj.Length;
    }

    private sealed class ThrowingRunner : IGcloudCommandRunner
    {
        public Task<GcloudCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) => throw new GcloudCommandException("ASDEPLOY166", "Timed out.", "Safe cause.", "Retry.");
    }
}
