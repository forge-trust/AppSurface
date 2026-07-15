using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CliFx;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Config.GoogleSecretManager;
using ForgeTrust.AppSurface.Config.LocalSecrets;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class SecretPromotionWorkflowTests
{
    [Fact]
    public void ApplicationDefaultProfile_ReturnsInjectedClient()
    {
        var client = new FakeGoogleClient();
        var factory = new DefaultSecretPromotionGoogleClientFactory(client);

        var result = factory.Create(new SecretPromotionEndpoint(
            "staging",
            "google",
            "staging",
            new SecretPromotionCredential("APPLICATIONDEFAULT", null)));

        Assert.Same(client, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unsupported")]
    public void GoogleProfile_RequiresAnExplicitSupportedCredentialMode(string? mode)
    {
        var factory = new DefaultSecretPromotionGoogleClientFactory(new FakeGoogleClient());
        var credential = mode is null ? null : new SecretPromotionCredential(mode, null);

        var exception = Assert.Throws<CommandException>(() => factory.Create(
            new SecretPromotionEndpoint("staging", "google", "staging", credential)));

        Assert.Contains("explicitly select", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialFileProfile_RejectsMissingPathAndDirectory()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var factory = new DefaultSecretPromotionGoogleClientFactory(new FakeGoogleClient());

        var missingPath = Assert.Throws<CommandException>(() => factory.Create(new SecretPromotionEndpoint(
            "production", "google", "production", new SecretPromotionCredential("credentialFile", null))));
        var directory = Assert.Throws<CommandException>(() => factory.Create(new SecretPromotionEndpoint(
            "production", "google", "production", new SecretPromotionCredential("credentialFile", temp.Path))));

        Assert.Contains("requires credential.path", missingPath.Message, StringComparison.Ordinal);
        Assert.Contains("regular file", directory.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanThenApply_LocalToGoogle_UsesMetadataUntilApplyAndNeverSerializesValue()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Stripe:ApiKey"), "sentinel-local-secret").Status);
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var planPath = Path.Join(temp.Path, "promotion.plan.json");
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));

        var planned = workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "local-to-staging", planPath, false, TimeSpan.FromMinutes(10), context));

        Assert.True(planned.Summary.Succeeded, JsonSerializer.Serialize(planned.Summary));
        Assert.Equal(0, google.AccessCalls);
        Assert.Empty(google.Writes);
        Assert.DoesNotContain("sentinel-local-secret", File.ReadAllText(planPath), StringComparison.Ordinal);

        var applied = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));

        Assert.True(applied.Succeeded);
        Assert.Single(google.Writes);
        Assert.Equal("projects/staging/secrets/stripe-api-key", google.Writes[0].Resource);
        Assert.DoesNotContain("sentinel-local-secret", JsonSerializer.Serialize(applied), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_ExistingDestinationWithoutReplace_SkipsTheRowWithoutReadingItsValue()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Stripe:ApiKey"), "sentinel-local-secret").Status);
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var planPath = Path.Join(temp.Path, "promotion.plan.json");
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = true;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));

        var planned = workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "local-to-staging", planPath, false, TimeSpan.FromMinutes(10), context));
        var applied = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));

        Assert.True(planned.Summary.Succeeded);
        Assert.True(applied.Succeeded);
        Assert.Empty(google.Writes);
        Assert.Equal(0, google.AccessCalls);
        var row = Assert.Single(applied.Rows);
        Assert.Equal("Skipped", row.Status);
        Assert.Equal("SkippedExistingDestination", row.Action);
        Assert.DoesNotContain("sentinel-local-secret", JsonSerializer.Serialize(applied), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_ResumeReceipt_SkipsConfirmedWritesBeforeDestinationPreflight()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Stripe:ApiKey"), "sentinel-local-secret").Status);
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var planPath = Path.Join(temp.Path, "promotion.plan.json");
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "local-to-staging", planPath, false, TimeSpan.FromMinutes(10), context));
        workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));
        google.Secrets["projects/staging/secrets/stripe-api-key"] = true;

        var resumed = workflow.Apply(new SecretPromotionApplyRequest(
            configPath,
            planPath,
            true,
            null,
            null,
            $"{planPath}.receipt.json",
            context));

        Assert.True(resumed.Succeeded);
        Assert.Single(google.Writes);
        var row = Assert.Single(resumed.Rows);
        Assert.Equal("Skipped", row.Status);
        Assert.Equal("ResumeSkippedConfirmedWrite", row.Action);
        Assert.DoesNotContain("sentinel-local-secret", JsonSerializer.Serialize(resumed), StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialFileProfile_RejectsRelativePaths()
    {
        var factory = new DefaultSecretPromotionGoogleClientFactory(new FakeGoogleClient());
        var endpoint = new SecretPromotionEndpoint(
            "production",
            "google",
            "production",
            new SecretPromotionCredential("credentialFile", "credentials.json"));

        var exception = Assert.Throws<CommandException>(() => factory.Create(endpoint));

        Assert.Contains("absolute path", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("credentials.json", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialFileProfile_RejectsGroupReadableFiles()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var credentialPath = temp.WriteFile("credentials.json", "{}");
        File.SetUnixFileMode(credentialPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var factory = new DefaultSecretPromotionGoogleClientFactory(new FakeGoogleClient());
        var endpoint = new SecretPromotionEndpoint(
            "production",
            "google",
            "production",
            new SecretPromotionCredential("credentialFile", credentialPath));

        var exception = Assert.Throws<CommandException>(() => factory.Create(endpoint));

        Assert.Contains("group or other users", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(credentialPath, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialFileProfile_HidesInvalidCredentialFileDetails()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var credentialPath = temp.WriteFile("credentials.json", "{ \"private_key\": \"sentinel-credential\" }");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(credentialPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var factory = new DefaultSecretPromotionGoogleClientFactory(new FakeGoogleClient());
        var endpoint = new SecretPromotionEndpoint(
            "production",
            "google",
            "production",
            new SecretPromotionCredential("credentialFile", credentialPath));

        var exception = Assert.Throws<CommandException>(() => factory.Create(endpoint));

        Assert.Contains("could not be loaded", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sentinel-credential", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(credentialPath, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_ProductionDestination_RequiresExactJobConfirmation()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("promotion.json", GoogleToProductionConfiguration());
        var planPath = Path.Join(temp.Path, "promotion.plan.json");
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel-remote-secret");
        google.Secrets["projects/production/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-production", planPath, false, TimeSpan.FromMinutes(10), context));

        var exception = Assert.Throws<CommandException>(() =>
        {
            workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, "not-the-job", null, null, context));
        });

        Assert.Contains("--confirm", exception.Message, StringComparison.Ordinal);
        Assert.Empty(google.Writes);
        Assert.DoesNotContain("sentinel-remote-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_TamperedProductionFlag_StillRequiresExactJobConfirmation()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("promotion.json", GoogleToProductionConfiguration());
        var planPath = Path.Join(temp.Path, "promotion.plan.json");
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel-remote-secret");
        google.Secrets["projects/production/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-production", planPath, false, TimeSpan.FromMinutes(10), context));
        var plan = JsonNode.Parse(File.ReadAllText(planPath))!.AsObject();
        plan["production"] = false;
        File.WriteAllText(planPath, plan.ToJsonString());

        var exception = Assert.Throws<CommandException>(() =>
        {
            workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));
        });

        Assert.Contains("--confirm", exception.Message, StringComparison.Ordinal);
        Assert.Empty(google.Writes);
        Assert.DoesNotContain("sentinel-remote-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_ConfigDigestChanges_FailsBeforeReadingSource()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Stripe:ApiKey"), "sentinel-local-secret").Status);
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var planPath = Path.Join(temp.Path, "promotion.plan.json");
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "local-to-staging", planPath, false, TimeSpan.FromMinutes(10), context));
        File.AppendAllText(configPath, Environment.NewLine);

        var exception = Assert.Throws<CommandException>(() =>
        {
            workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));
        });

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, google.AccessCalls);
        Assert.Empty(google.Writes);
        Assert.DoesNotContain("sentinel-local-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_NullJobRow_IsRejectedAsUsageBeforeAnyProbe()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var context = CreateContext(new InMemoryAppSurfaceLocalSecretStore());
        var configPath = temp.WriteFile(
            "promotion.json",
            """
            {
              "version": 1,
              "endpoints": [
                { "name": "staging", "provider": "google", "environment": "staging", "credential": { "mode": "applicationDefault" } }
              ],
              "jobs": [
                { "name": "local-to-staging", "source": "local", "destination": "staging", "rows": [null] }
              ]
            }
            """);
        var google = new FakeGoogleClient();
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));

        var exception = Assert.Throws<CommandException>(() =>
        {
            workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "local-to-staging", Path.Join(temp.Path, "plan.json"), false, TimeSpan.FromMinutes(10), context));
        });

        Assert.Contains("--config must be", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, google.AccessCalls);
        Assert.Empty(google.Writes);
    }

    [Theory]
    [InlineData("rowNumber")]
    [InlineData("key")]
    [InlineData("sourceEndpoint")]
    [InlineData("sourceResource")]
    [InlineData("destinationEndpoint")]
    [InlineData("destinationResource")]
    [InlineData("localStorageName")]
    public void Apply_TamperedPlanRow_FailsBeforeReadingOrWritingASecret(string propertyName)
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Stripe:ApiKey"), "sentinel-local-secret").Status);
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var planPath = Path.Join(temp.Path, "promotion.plan.json");
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "local-to-staging", planPath, false, TimeSpan.FromMinutes(10), context));
        var plan = JsonNode.Parse(File.ReadAllText(planPath))!.AsObject();
        var row = plan["rows"]!.AsArray()[0]!.AsObject();
        if (propertyName == "rowNumber")
        {
            row[propertyName] = 2;
        }
        else
        {
            row[propertyName] = "tampered";
        }
        File.WriteAllText(planPath, plan.ToJsonString());

        var exception = Assert.Throws<CommandException>(() =>
        {
            workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));
        });

        Assert.Contains("plan rows do not match", exception.Message, StringComparison.Ordinal);
        Assert.Empty(google.Writes);
        Assert.Equal(0, google.AccessCalls);
        Assert.DoesNotContain("sentinel-local-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_GoogleToLocal_WritesOnlyAfterPreflight()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("promotion.json", GoogleToLocalConfiguration());
        var planPath = Path.Join(temp.Path, "promotion.plan.json");
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel-remote-secret");
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));

        var plan = workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", planPath, false, TimeSpan.FromMinutes(10), context));
        var applied = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));

        Assert.True(plan.Summary.Succeeded);
        Assert.True(applied.Succeeded);
        Assert.Equal(LocalSecretResultStatus.Found, store.Get(Normalize(context, "Stripe:ApiKey")).Status);
        Assert.Equal(1, google.AccessCalls);
        Assert.Empty(google.Writes);
        Assert.DoesNotContain("sentinel-remote-secret", JsonSerializer.Serialize(applied), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_GoogleToLocal_RejectsInvalidUtf8WithoutWriting()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("promotion.json", GoogleToLocalConfiguration());
        var planPath = Path.Join(temp.Path, "promotion.plan.json");
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = [0xff];
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", planPath, false, TimeSpan.FromMinutes(10), context));

        var applied = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));

        Assert.False(applied.Succeeded);
        Assert.Equal("secret-promotion-invalid-payload", Assert.Single(applied.Rows).DiagnosticCode);
        Assert.Equal(LocalSecretResultStatus.Missing, store.Get(Normalize(context, "Stripe:ApiKey")).Status);
    }

    [Fact]
    public void Apply_GoogleDestinationChange_BlocksBeforeReadingSource()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Stripe:ApiKey"), "sentinel-local-secret").Status);
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var planPath = Path.Join(temp.Path, "promotion.plan.json");
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "local-to-staging", planPath, false, TimeSpan.FromMinutes(10), context));
        google.Secrets["projects/staging/secrets/stripe-api-key"] = true;

        var applied = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));

        Assert.False(applied.Succeeded);
        Assert.Equal("DestinationChanged", Assert.Single(applied.Rows).Status);
        Assert.Equal(0, google.AccessCalls);
        Assert.Empty(google.Writes);
    }

    [Fact]
    public void Apply_GoogleWriteUnavailable_ReturnsIndeterminateReceiptWithoutRetrying()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Stripe:ApiKey"), "sentinel-local-secret").Status);
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var planPath = Path.Join(temp.Path, "promotion.plan.json");
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "local-to-staging", planPath, false, TimeSpan.FromMinutes(10), context));
        google.WriteOverride = AppSurfaceGoogleSecretWriteResult.Failed(
            GoogleSecretManagerTransferStatus.Unavailable,
            "projects/staging/secrets/stripe-api-key",
            new AppSurfaceGoogleSecretTransferDiagnostic("test", "Test failure.", "Test cause.", "Test fix.", "test", true));

        var applied = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));

        Assert.False(applied.Succeeded);
        Assert.Equal("IndeterminateWrite", Assert.Single(applied.Rows).Status);
        Assert.Empty(google.Writes);
        Assert.True(File.Exists($"{planPath}.receipt.json"));
        Assert.DoesNotContain("sentinel-local-secret", File.ReadAllText($"{planPath}.receipt.json"), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_MissingLocalSource_IsValueSafeAfterCapturingDestinationPrecondition()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var context = CreateContext(new InMemoryAppSurfaceLocalSecretStore());
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));

        var plan = workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "local-to-staging", Path.Join(temp.Path, "plan.json"), false, TimeSpan.FromMinutes(10), context));

        Assert.False(plan.Summary.Succeeded);
        Assert.Equal("SourceMissing", Assert.Single(plan.Summary.Rows).Status);
        Assert.Equal(1, google.SecretProbeCalls);
    }

    [Fact]
    public void Plan_LocalSourceWithoutMetadataProbe_FailsValueSafely()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));

        var plan = workflow.CreatePlan(new SecretPromotionPlanRequest(
            temp.WriteFile("promotion.json", LocalToGoogleConfiguration()),
            "local-to-staging",
            Path.Join(temp.Path, "plan.json"),
            false,
            TimeSpan.FromMinutes(10),
            CreateContext(new MetadataIncapableStore())));

        Assert.False(plan.Summary.Succeeded);
        Assert.Equal("local-secret-metadata-unsupported", Assert.Single(plan.Summary.Rows).DiagnosticCode);
    }

    [Fact]
    public void Plan_LocalDestinationWithoutMetadataProbe_FailsValueSafely()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel");
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));

        var plan = workflow.CreatePlan(new SecretPromotionPlanRequest(
            temp.WriteFile("promotion.json", GoogleToLocalConfiguration()),
            "staging-to-local",
            Path.Join(temp.Path, "plan.json"),
            false,
            TimeSpan.FromMinutes(10),
            CreateContext(new MetadataIncapableStore())));

        Assert.False(plan.Summary.Succeeded);
        Assert.Equal("local-secret-metadata-unsupported", Assert.Single(plan.Summary.Rows).DiagnosticCode);
    }

    [Fact]
    public void Plan_LocalSourceProbeFailureWithoutDiagnostic_IsValueSafe()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new ControlledMetadataStore(
            LocalSecretResultStatus.ProviderFailed,
            LocalSecretResultStatus.Missing,
            LocalSecretResultStatus.Found,
            includeDiagnostic: false);
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));

        var plan = workflow.CreatePlan(new SecretPromotionPlanRequest(
            temp.WriteFile("promotion.json", LocalToGoogleConfiguration()),
            "local-to-staging",
            Path.Join(temp.Path, "plan.json"),
            false,
            TimeSpan.FromMinutes(10),
            CreateContext(store)));

        var row = Assert.Single(plan.Summary.Rows);
        Assert.False(plan.Summary.Succeeded);
        Assert.Equal("Failed", row.Status);
        Assert.Null(row.DiagnosticCode);
    }

    [Fact]
    public void Plan_LocalDestinationProbeFailureWithoutDiagnostic_IsValueSafe()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new ControlledMetadataStore(
            LocalSecretResultStatus.ProviderFailed,
            LocalSecretResultStatus.Missing,
            LocalSecretResultStatus.Found,
            includeDiagnostic: false);
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel");
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));

        var plan = workflow.CreatePlan(new SecretPromotionPlanRequest(
            temp.WriteFile("promotion.json", GoogleToLocalConfiguration()),
            "staging-to-local",
            Path.Join(temp.Path, "plan.json"),
            false,
            TimeSpan.FromMinutes(10),
            CreateContext(store)));

        var row = Assert.Single(plan.Summary.Rows);
        Assert.False(plan.Summary.Succeeded);
        Assert.Equal("Failed", row.Status);
        Assert.Null(row.DiagnosticCode);
        Assert.Equal(0, google.AccessCalls);
    }

    [Fact]
    public void Plan_MissingGoogleSource_ReturnsProviderFailure()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(new FakeGoogleClient()));

        var plan = workflow.CreatePlan(new SecretPromotionPlanRequest(
            temp.WriteFile("promotion.json", GoogleToLocalConfiguration()),
            "staging-to-local",
            Path.Join(temp.Path, "plan.json"),
            false,
            TimeSpan.FromMinutes(10),
            CreateContext(new InMemoryAppSurfaceLocalSecretStore())));

        Assert.False(plan.Summary.Succeeded);
        Assert.Equal("SourceMissing", Assert.Single(plan.Summary.Rows).Status);
    }

    [Theory]
    [InlineData(GoogleSecretManagerTransferStatus.Missing)]
    [InlineData(GoogleSecretManagerTransferStatus.AccessDenied)]
    [InlineData(GoogleSecretManagerTransferStatus.Cancelled)]
    public void Apply_GoogleSourceAccessFailure_IsValueSafe(GoogleSecretManagerTransferStatus status)
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("promotion.json", GoogleToLocalConfiguration());
        var planPath = Path.Join(temp.Path, "plan.json");
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel");
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", planPath, false, TimeSpan.FromMinutes(10), context));
        google.AccessOverride = AppSurfaceGoogleSecretAccessResult.Failed(status, "projects/staging/secrets/stripe-api-key/versions/7", FakeGoogleClient.Diagnostic());

        var result = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));

        Assert.False(result.Succeeded);
        Assert.DoesNotContain("sentinel", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GoogleSecretManagerTransferStatus.AccessDenied, "AccessDenied", true)]
    [InlineData(GoogleSecretManagerTransferStatus.ProviderFailed, "Failed", true)]
    [InlineData(GoogleSecretManagerTransferStatus.Cancelled, "IndeterminateWrite", true)]
    [InlineData(GoogleSecretManagerTransferStatus.Unavailable, "IndeterminateWrite", false)]
    public void Apply_GoogleWriteFailure_IsClassifiedValueSafely(
        GoogleSecretManagerTransferStatus status,
        string expected,
        bool includeDiagnostic)
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Stripe:ApiKey"), "sentinel").Status);
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var planPath = Path.Join(temp.Path, "plan.json");
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "local-to-staging", planPath, false, TimeSpan.FromMinutes(10), context));
        google.WriteOverride = new AppSurfaceGoogleSecretWriteResult(
            status,
            "projects/staging/secrets/stripe-api-key",
            null,
            includeDiagnostic ? FakeGoogleClient.Diagnostic() : null);

        var result = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));

        Assert.False(result.Succeeded);
        Assert.Equal(expected, Assert.Single(result.Rows).Status);
        Assert.DoesNotContain("sentinel", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Apply_LocalSourceDisappearsAfterSuccessfulMetadataPreflight(bool includeDiagnostic)
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new ControlledMetadataStore(
            LocalSecretResultStatus.Found,
            includeDiagnostic ? LocalSecretResultStatus.Missing : LocalSecretResultStatus.ProviderFailed,
            LocalSecretResultStatus.Found,
            includeDiagnostic);
        var context = CreateContext(store);
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var planPath = Path.Join(temp.Path, "plan.json");
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "local-to-staging", planPath, false, TimeSpan.FromMinutes(10), context));

        var result = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));

        Assert.False(result.Succeeded);
        Assert.Equal("ReadLocalSource", Assert.Single(result.Rows).Action);
        Assert.Empty(google.Writes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Apply_LocalDestinationWriteFailure_IsValueSafe(bool includeDiagnostic)
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new ControlledMetadataStore(
            LocalSecretResultStatus.Missing,
            LocalSecretResultStatus.Missing,
            LocalSecretResultStatus.ProviderFailed,
            includeDiagnostic);
        var context = CreateContext(store);
        var configPath = temp.WriteFile("promotion.json", GoogleToLocalConfiguration());
        var planPath = Path.Join(temp.Path, "plan.json");
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel");
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", planPath, false, TimeSpan.FromMinutes(10), context));

        var result = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));

        Assert.False(result.Succeeded);
        Assert.Equal("WriteLocal", Assert.Single(result.Rows).Action);
        Assert.DoesNotContain("sentinel", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_ExistingLocalDestination_CapturesPrecondition()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Stripe:ApiKey"), "existing").Status);
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel");
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));

        var plan = workflow.CreatePlan(new SecretPromotionPlanRequest(
            temp.WriteFile("promotion.json", GoogleToLocalConfiguration()), "staging-to-local",
            Path.Join(temp.Path, "plan.json"), false, TimeSpan.FromMinutes(10), context));

        Assert.True(plan.Summary.Succeeded);
        Assert.Equal("DestinationExists", Assert.Single(plan.Summary.Rows).Status);
    }

    [Fact]
    public void Plan_ExistingLocalDestinationWithReplace_WouldReplaceLocal()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Stripe:ApiKey"), "existing").Status);
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel");
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));

        var plan = workflow.CreatePlan(new SecretPromotionPlanRequest(
            temp.WriteFile("promotion.json", GoogleToLocalConfiguration()),
            "staging-to-local",
            Path.Join(temp.Path, "plan.json"),
            true,
            TimeSpan.FromMinutes(10),
            context));

        Assert.True(plan.Summary.Succeeded);
        Assert.Equal("WouldReplaceLocal", Assert.Single(plan.Summary.Rows).Action);
        Assert.Equal(0, google.AccessCalls);
    }

    [Fact]
    public void Plan_ExistingGoogleDestinationWithReplace_WouldAddVersion()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Stripe:ApiKey"), "sentinel").Status);
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = true;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));

        var plan = workflow.CreatePlan(new SecretPromotionPlanRequest(
            temp.WriteFile("promotion.json", LocalToGoogleConfiguration()),
            "local-to-staging",
            Path.Join(temp.Path, "plan.json"),
            true,
            TimeSpan.FromMinutes(10),
            context));

        Assert.True(plan.Summary.Succeeded);
        Assert.Equal("WouldAddVersion", Assert.Single(plan.Summary.Rows).Action);
        Assert.Empty(google.Writes);
    }

    [Fact]
    public void Apply_SourceDisappearsDuringPreflight_BlocksBeforePayloadRead()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var identity = Normalize(context, "Stripe:ApiKey");
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(identity, "sentinel").Status);
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var planPath = Path.Join(temp.Path, "plan.json");
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "local-to-staging", planPath, false, TimeSpan.FromMinutes(10), context));
        Assert.Equal(LocalSecretResultStatus.Found, store.Delete(identity).Status);

        var result = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));

        Assert.False(result.Succeeded);
        Assert.Equal("SourceMissing", Assert.Single(result.Rows).Status);
        Assert.Empty(google.Writes);
    }

    [Fact]
    public void Apply_LocalDestinationAppearsDuringPreflight_BlocksBeforePayloadRead()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("promotion.json", GoogleToLocalConfiguration());
        var planPath = Path.Join(temp.Path, "plan.json");
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel");
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", planPath, false, TimeSpan.FromMinutes(10), context));
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Stripe:ApiKey"), "existing").Status);

        var result = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));

        Assert.False(result.Succeeded);
        Assert.Equal("DestinationChanged", Assert.Single(result.Rows).Status);
        Assert.Equal(0, google.AccessCalls);
    }

    [Fact]
    public void Plan_MissingGoogleDestination_ReturnsProviderFailure()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Stripe:ApiKey"), "sentinel").Status);
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(new FakeGoogleClient()));

        var plan = workflow.CreatePlan(new SecretPromotionPlanRequest(
            temp.WriteFile("promotion.json", LocalToGoogleConfiguration()), "local-to-staging",
            Path.Join(temp.Path, "plan.json"), false, TimeSpan.FromMinutes(10), context));

        Assert.False(plan.Summary.Succeeded);
        Assert.Equal("SourceMissing", Assert.Single(plan.Summary.Rows).Status);
    }

    [Fact]
    public void Apply_TamperedPlanRowCount_IsRejected()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Stripe:ApiKey"), "sentinel").Status);
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var planPath = Path.Join(temp.Path, "plan.json");
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "local-to-staging", planPath, false, TimeSpan.FromMinutes(10), context));
        var plan = JsonNode.Parse(File.ReadAllText(planPath))!.AsObject();
        plan["rows"] = new JsonArray();
        File.WriteAllText(planPath, plan.ToJsonString());

        var exception = Assert.Throws<CommandException>(() => workflow.Apply(
            new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context)));

        Assert.Contains("plan rows do not match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_BlockedReceiptWriteFailure_ReturnsUsageDiagnostic()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Stripe:ApiKey"), "sentinel").Status);
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var planPath = Path.Join(temp.Path, "plan.json");
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "local-to-staging", planPath, false, TimeSpan.FromMinutes(10), context));
        google.Secrets["projects/staging/secrets/stripe-api-key"] = true;

        var exception = Assert.Throws<CommandException>(() => workflow.Apply(
            new SecretPromotionApplyRequest(configPath, planPath, true, null, temp.Path, null, context)));

        Assert.Contains("--receipt could not be written", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, google.AccessCalls);
    }

    [Fact]
    public void Apply_ExpiredAndUnreadyPlans_AreRejectedBeforePayloadReads()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var context = CreateContext(new InMemoryAppSurfaceLocalSecretStore());
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var planPath = Path.Join(temp.Path, "plan.json");
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "local-to-staging", planPath, false, TimeSpan.FromMinutes(10), context));
        var plan = JsonNode.Parse(File.ReadAllText(planPath))!.AsObject();

        plan["expiresAtUtc"] = DateTimeOffset.UtcNow.AddMinutes(-1);
        File.WriteAllText(planPath, plan.ToJsonString());
        Assert.Contains("expired", Assert.Throws<CommandException>(() => workflow.Apply(
            new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context))).Message, StringComparison.Ordinal);

        plan["expiresAtUtc"] = DateTimeOffset.UtcNow.AddMinutes(10);
        plan["ready"] = false;
        File.WriteAllText(planPath, plan.ToJsonString());
        Assert.Contains("failed preflight", Assert.Throws<CommandException>(() => workflow.Apply(
            new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context))).Message, StringComparison.Ordinal);
        Assert.Equal(0, google.AccessCalls);
    }

    [Fact]
    public void PlanAndApply_FileReadFailures_ReturnUsageDiagnostics()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var context = CreateContext(new InMemoryAppSurfaceLocalSecretStore());
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(new FakeGoogleClient()));

        var configFailure = Assert.Throws<CommandException>(() => workflow.CreatePlan(new SecretPromotionPlanRequest(
            temp.Path, "job", Path.Join(temp.Path, "plan.json"), false, TimeSpan.FromMinutes(10), context)));
        var planFailure = Assert.Throws<CommandException>(() => workflow.Apply(new SecretPromotionApplyRequest(
            temp.WriteFile("promotion.json", LocalToGoogleConfiguration()),
            Path.Join(temp.Path, "missing-plan.json"),
            true, null, null, null, context)));

        Assert.Contains("--config could not be read", configFailure.Message, StringComparison.Ordinal);
        Assert.Contains("--plan could not be read", planFailure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"version\":1,\"jobName\":\"\",\"rows\":[]}")]
    [InlineData("{\"version\":1,\"jobName\":\"job\",\"rows\":null}")]
    public void Apply_SemanticallyInvalidPlan_IsRejected(string planJson)
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(new FakeGoogleClient()));

        var exception = Assert.Throws<CommandException>(() => workflow.Apply(new SecretPromotionApplyRequest(
            temp.WriteFile("promotion.json", LocalToGoogleConfiguration()),
            temp.WriteFile("plan.json", planJson),
            true, null, null, null,
            CreateContext(new InMemoryAppSurfaceLocalSecretStore()))));

        Assert.Contains("version 1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_NormalizedKeyCollision_IsRejectedBeforeProviderWork()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var configuration = """
            {"version":1,"endpoints":[{"name":"staging","provider":"google","environment":"staging","credential":{"mode":"applicationDefault"}}],"jobs":[{"name":"job","source":"local","destination":"staging","rows":[{"key":"A__B","destination":"projects/p/secrets/a"},{"key":"A:B","destination":"projects/p/secrets/b"}]}]}
            """;
        var google = new FakeGoogleClient();
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));

        var exception = Assert.Throws<CommandException>(() => workflow.CreatePlan(new SecretPromotionPlanRequest(
            temp.WriteFile("promotion.json", configuration), "job", Path.Join(temp.Path, "plan.json"), false,
            TimeSpan.FromMinutes(10), CreateContext(new InMemoryAppSurfaceLocalSecretStore()))));

        Assert.Contains("duplicate normalized", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, google.SecretProbeCalls);
    }

    [Fact]
    public void Plan_OutputWriteFailure_ReturnsUsageDiagnostic()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Stripe:ApiKey"), "sentinel").Status);
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));

        var exception = Assert.Throws<CommandException>(() => workflow.CreatePlan(new SecretPromotionPlanRequest(
            temp.WriteFile("promotion.json", LocalToGoogleConfiguration()), "local-to-staging", temp.Path, false,
            TimeSpan.FromMinutes(10), context)));

        Assert.Contains("--out could not be written", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_MalformedPlanAndResumeReceipt_ReturnUsageDiagnostics()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Stripe:ApiKey"), "sentinel").Status);
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var planPath = Path.Join(temp.Path, "plan.json");
        var malformedPath = temp.WriteFile("malformed.json", "{");
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));

        var malformedPlan = Assert.Throws<CommandException>(() => workflow.Apply(new SecretPromotionApplyRequest(
            configPath, malformedPath, true, null, null, null, context)));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "local-to-staging", planPath, false, TimeSpan.FromMinutes(10), context));
        var malformedResume = Assert.Throws<CommandException>(() => workflow.Apply(new SecretPromotionApplyRequest(
            configPath, planPath, true, null, null, malformedPath, context)));
        var missingResume = Assert.Throws<CommandException>(() => workflow.Apply(new SecretPromotionApplyRequest(
            configPath, planPath, true, null, null, Path.Join(temp.Path, "missing-receipt.json"), context)));

        Assert.Contains("--plan must be valid", malformedPlan.Message, StringComparison.Ordinal);
        Assert.Contains("--resume must be a valid", malformedResume.Message, StringComparison.Ordinal);
        Assert.Contains("--resume could not be read", missingResume.Message, StringComparison.Ordinal);
        Assert.Equal(0, google.AccessCalls);
    }

    [Fact]
    public void Apply_MismatchedResumeReceipt_IsRejectedBeforePayloadRead()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Stripe:ApiKey"), "sentinel").Status);
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var planPath = Path.Join(temp.Path, "plan.json");
        var resumePath = temp.WriteFile("receipt.json", "{\"planJob\":\"different\",\"configDigest\":\"different\",\"rows\":[]}");
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "local-to-staging", planPath, false, TimeSpan.FromMinutes(10), context));

        var exception = Assert.Throws<CommandException>(() => workflow.Apply(new SecretPromotionApplyRequest(
            configPath, planPath, true, null, null, resumePath, context)));

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, google.AccessCalls);
    }

    [Theory]
    [MemberData(nameof(InvalidPlanConfigurations))]
    public void Plan_InvalidDeclaredConfiguration_ReturnsUsageBeforeAnySecretRead(string configuration, string expectedMessage)
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var google = new FakeGoogleClient();
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        var configPath = temp.WriteFile("promotion.json", configuration);

        var exception = Assert.Throws<CommandException>(() => workflow.CreatePlan(
            new SecretPromotionPlanRequest(
                configPath,
                "job",
                Path.Join(temp.Path, "plan.json"),
                false,
                TimeSpan.FromMinutes(10),
                CreateContext(new InMemoryAppSurfaceLocalSecretStore()))));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, google.AccessCalls);
        Assert.Empty(google.Writes);
    }

    [Fact]
    public void Plan_BlankDeclaredJobName_IsRejectedBeforeProviderWork()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var google = new FakeGoogleClient();
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        var configuration =
            "{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\",\"credential\":{\"mode\":\"applicationDefault\"}}],\"jobs\":[{\"name\":\"\",\"source\":\"local\",\"destination\":\"staging\",\"rows\":[{\"key\":\"Key\",\"destination\":\"projects/p/secrets/s\"}]}]}";

        var exception = Assert.Throws<CommandException>(() => workflow.CreatePlan(new SecretPromotionPlanRequest(
            temp.WriteFile("promotion.json", configuration),
            string.Empty,
            Path.Join(temp.Path, "plan.json"),
            false,
            TimeSpan.FromMinutes(10),
            CreateContext(new InMemoryAppSurfaceLocalSecretStore()))));

        Assert.Contains("must have a name", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, google.SecretProbeCalls);
    }

    public static IEnumerable<object[]> InvalidPlanConfigurations()
    {
        yield return ["{", "valid secret-promotion JSON"];
        yield return ["null", "--config must be"];
        yield return ["{\"version\":2,\"endpoints\":[],\"jobs\":[]}", "--config must be"];
        yield return ["{\"version\":1,\"endpoints\":null,\"jobs\":[]}", "--config must be"];
        yield return ["{\"version\":1,\"endpoints\":[],\"jobs\":null}", "--config must be"];
        yield return ["{\"version\":1,\"endpoints\":[null],\"jobs\":[]}", "--config must be"];
        yield return ["{\"version\":1,\"endpoints\":[],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"local\",\"rows\":null}]}", "--config must be"];
        yield return ["{\"version\":1,\"endpoints\":[],\"jobs\":[]}", "No declared promotion job"];
        yield return ["{\"version\":1,\"endpoints\":[],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"missing\",\"rows\":[]},{\"name\":\"job\",\"source\":\"local\",\"destination\":\"missing\",\"rows\":[]}]}", "declared more than once"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\",\"credential\":{\"mode\":\"applicationDefault\"}}],\"jobs\":[{\"name\":\"job\",\"source\":\"\",\"destination\":\"staging\",\"rows\":[{\"key\":\"Key\",\"destination\":\"projects/p/secrets/s\"}]}]}", "must name source"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"azure\",\"environment\":\"staging\",\"credential\":{\"mode\":\"applicationDefault\"}}],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"staging\",\"rows\":[{\"key\":\"Key\",\"destination\":\"projects/p/secrets/s\"}]}]}", "V1 supports only"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"azure\",\"environment\":\"staging\",\"credential\":{\"mode\":\"applicationDefault\"}}],\"jobs\":[{\"name\":\"job\",\"source\":\"staging\",\"destination\":\"local\",\"rows\":[{\"key\":\"Key\",\"source\":\"projects/p/secrets/s/versions/1\"}]}]}", "V1 supports only"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\",\"credential\":{\"mode\":\"applicationDefault\"}}],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"staging\",\"rows\":[{\"key\":\"Key\",\"destination\":\"projects/p/secrets/s\"},{\"key\":\"Key\",\"destination\":\"projects/p/secrets/t\"}]}]}", "unique non-empty keys"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\",\"credential\":{\"mode\":\"applicationDefault\"}}],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"staging\",\"rows\":[{\"key\":\"\",\"destination\":\"projects/p/secrets/s\"}]}]}", "unique non-empty keys"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"production\",\"provider\":\"google\",\"environment\":\"production\",\"credential\":{\"mode\":\"applicationDefault\"}}],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"production\",\"rows\":[{\"key\":\"Key\",\"destination\":\"projects/p/secrets/s\"}]}]}", "allowMutableLocalSource"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\",\"credential\":{\"mode\":\"applicationDefault\"}},{\"name\":\"production\",\"provider\":\"google\",\"environment\":\"production\",\"credential\":{\"mode\":\"applicationDefault\"}}],\"jobs\":[{\"name\":\"job\",\"source\":\"staging\",\"destination\":\"production\",\"rows\":[{\"key\":\"Key\",\"source\":\"projects/p/secrets/s/versions/latest\",\"destination\":\"projects/q/secrets/t\"}]}]}", "explicit numeric version"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\",\"credential\":{\"mode\":\"applicationDefault\"}}],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"staging\",\"rows\":[{\"key\":\"Key\",\"source\":\"unexpected\",\"destination\":\"projects/p/secrets/s\"}]}]}", "Local source rows"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\",\"credential\":{\"mode\":\"applicationDefault\"}}],\"jobs\":[{\"name\":\"job\",\"source\":\"staging\",\"destination\":\"staging\",\"rows\":[{\"key\":\"Key\",\"source\":\"projects/p/secrets/s/versions/1\",\"destination\":\"projects/p/secrets/t\"}]}]}", "same source and destination"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\",\"credential\":{\"mode\":\"applicationDefault\"}}],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"staging\",\"rows\":[]}]}", "at least one row"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\",\"credential\":{\"mode\":\"applicationDefault\"}}],\"jobs\":[{\"name\":\"job\",\"source\":\"staging\",\"destination\":\"local\",\"rows\":[{\"key\":\"Key\",\"source\":\"bad\"}]}]}", "Google source rows require"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\",\"credential\":{\"mode\":\"applicationDefault\"}}],\"jobs\":[{\"name\":\"job\",\"source\":\"staging\",\"destination\":\"local\",\"rows\":[{\"key\":\"Key\"}]}]}", "Google source rows require"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\",\"credential\":{\"mode\":\"applicationDefault\"}}],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"staging\",\"rows\":[{\"key\":\"Key\",\"destination\":\"bad\"}]}]}", "Google destination rows require"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\",\"credential\":{\"mode\":\"applicationDefault\"}}],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"staging\",\"rows\":[{\"key\":\"Key\"}]}]}", "Google destination rows require"];
        yield return ["{\"version\":1,\"endpoints\":[],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"missing\",\"rows\":[{\"key\":\"Key\",\"destination\":\"projects/p/secrets/s\"}]}]}", "must be declared once"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\"},{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\"}],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"staging\",\"rows\":[{\"key\":\"Key\",\"destination\":\"projects/p/secrets/s\"}]}]}", "must be declared once"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"remote\",\"provider\":\"local\",\"environment\":\"staging\"}],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"remote\",\"rows\":[{\"key\":\"Key\"}]}]}", "supported remote provider"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\"}],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"staging\",\"rows\":[{\"key\":\"A\",\"destination\":\"projects/p/secrets/s\"},{\"key\":\"B\",\"destination\":\"projects/p/secrets/s\"}]}]}", "duplicate destination"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\"}],\"jobs\":[{\"name\":\"job\",\"source\":\"staging\",\"destination\":\"local\",\"rows\":[{\"key\":\"Key\",\"source\":\"projects/p/secrets/s/versions/1\",\"destination\":\"unexpected\"}]}]}", "Local destination rows"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\"}],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"staging\",\"rows\":[{\"key\":\"Bad\\nKey\",\"destination\":\"projects/p/secrets/s\"}]}]}", "unsupported characters"];
    }

    [Theory]
    [InlineData(GoogleSecretManagerTransferStatus.Missing)]
    [InlineData(GoogleSecretManagerTransferStatus.AccessDenied)]
    [InlineData(GoogleSecretManagerTransferStatus.Unavailable)]
    [InlineData(GoogleSecretManagerTransferStatus.Cancelled)]
    [InlineData(GoogleSecretManagerTransferStatus.InvalidResource)]
    [InlineData(GoogleSecretManagerTransferStatus.NotEnabled)]
    [InlineData(GoogleSecretManagerTransferStatus.ProviderFailed)]
    public void GoogleFailure_MapsEveryProviderStatusValueSafely(GoogleSecretManagerTransferStatus status)
    {
        var row = new SecretPromotionPlanRow(1, "Key", "staging", "projects/p/secrets/s/versions/1", "production", "projects/q/secrets/t", "storage", false, null);
        var result = row.GoogleFailure("ProbeGoogleSource", status, new AppSurfaceGoogleSecretTransferDiagnostic("diagnostic", "Problem", "Cause", "Fix", "docs", false));

        Assert.False(string.IsNullOrWhiteSpace(result.Status));
        Assert.Equal("ProbeGoogleSource", result.Action);
    }

    private static SecretsCommandContext CreateContext(IAppSurfaceLocalSecretStore store) =>
        new(new AppSurfaceLocalSecretIdentityNormalizer(), store, "AppSurfaceApp", "Development", null);

    private static AppSurfaceLocalSecretIdentity Normalize(SecretsCommandContext context, string key) =>
        context.Normalizer.Normalize(context.ApplicationName, context.Environment, context.KeyPrefix, key).Identity!;

    private static string LocalToGoogleConfiguration() =>
        """
        {
          "version": 1,
          "endpoints": [
            { "name": "staging", "provider": "google", "environment": "staging", "credential": { "mode": "applicationDefault" } }
          ],
          "jobs": [
            {
              "name": "local-to-staging",
              "source": "local",
              "destination": "staging",
              "rows": [
                { "key": "Stripe:ApiKey", "destination": "projects/staging/secrets/stripe-api-key" }
              ]
            }
          ]
        }
        """;

    private static string GoogleToProductionConfiguration() =>
        """
        {
          "version": 1,
          "endpoints": [
            { "name": "staging", "provider": "google", "environment": "staging", "credential": { "mode": "applicationDefault" } },
            { "name": "production", "provider": "google", "environment": "production", "credential": { "mode": "applicationDefault" } }
          ],
          "jobs": [
            {
              "name": "staging-to-production",
              "source": "staging",
              "destination": "production",
              "rows": [
                {
                  "key": "Stripe:ApiKey",
                  "source": "projects/staging/secrets/stripe-api-key/versions/7",
                  "destination": "projects/production/secrets/stripe-api-key"
                }
              ]
            }
          ]
        }
        """;

    private static string GoogleToLocalConfiguration() =>
        """
        {
          "version": 1,
          "endpoints": [
            { "name": "staging", "provider": "google", "environment": "staging", "credential": { "mode": "applicationDefault" } }
          ],
          "jobs": [
            {
              "name": "staging-to-local",
              "source": "staging",
              "destination": "local",
              "rows": [
                { "key": "Stripe:ApiKey", "source": "projects/staging/secrets/stripe-api-key/versions/7" }
              ]
            }
          ]
        }
        """;

    private sealed class FakeGoogleFactory(FakeGoogleClient client) : ISecretPromotionGoogleClientFactory
    {
        public IAppSurfaceGoogleSecretTransferClient Create(SecretPromotionEndpoint endpoint) => client;
    }

    private sealed class TestTempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TestTempDirectory Create(string prefix)
        {
            var path = System.IO.Path.Join(System.IO.Path.GetTempPath(), $"{prefix}{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TestTempDirectory(path);
        }

        public string WriteFile(string name, string value)
        {
            var path = System.IO.Path.Join(Path, name);
            File.WriteAllText(path, value);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class FakeGoogleClient : IAppSurfaceGoogleSecretTransferClient
    {
        public Dictionary<string, bool> Secrets { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, byte[]> Versions { get; } = new(StringComparer.Ordinal);
        public List<(string Resource, string Value)> Writes { get; } = [];
        public int AccessCalls { get; private set; }
        public int SecretProbeCalls { get; private set; }
        public AppSurfaceGoogleSecretWriteResult? WriteOverride { get; set; }
        public AppSurfaceGoogleSecretAccessResult? AccessOverride { get; set; }

        public AppSurfaceGoogleSecretProbeResult ProbeSecret(string secretResourceName, TimeSpan timeout)
        {
            SecretProbeCalls++;
            return Secrets.TryGetValue(secretResourceName, out var enabled)
                ? AppSurfaceGoogleSecretProbeResult.Ready(secretResourceName, enabled)
                : AppSurfaceGoogleSecretProbeResult.Failed(GoogleSecretManagerTransferStatus.Missing, secretResourceName, Diagnostic());
        }

        public AppSurfaceGoogleSecretProbeResult ProbeSecretVersion(string versionResourceName, TimeSpan timeout) =>
            Versions.ContainsKey(versionResourceName)
                ? AppSurfaceGoogleSecretProbeResult.Ready(versionResourceName)
                : AppSurfaceGoogleSecretProbeResult.Failed(GoogleSecretManagerTransferStatus.Missing, versionResourceName, Diagnostic());

        public AppSurfaceGoogleSecretAccessResult AccessSecretVersion(string versionResourceName, TimeSpan timeout)
        {
            AccessCalls++;
            if (AccessOverride is { } result)
            {
                return result;
            }

            return Versions.TryGetValue(versionResourceName, out var value)
                ? AppSurfaceGoogleSecretAccessResult.Accessed(versionResourceName, new AppSurfaceGoogleSecretPayload(value, versionResourceName))
                : AppSurfaceGoogleSecretAccessResult.Failed(GoogleSecretManagerTransferStatus.Missing, versionResourceName, Diagnostic());
        }

        public AppSurfaceGoogleSecretWriteResult AddSecretVersion(string secretResourceName, string value, TimeSpan timeout)
        {
            if (WriteOverride is { } result)
            {
                return result;
            }

            Writes.Add((secretResourceName, value));
            return AppSurfaceGoogleSecretWriteResult.Written(secretResourceName, $"{secretResourceName}/versions/{Writes.Count}");
        }

        public static AppSurfaceGoogleSecretTransferDiagnostic Diagnostic() =>
            new("test", "Test failure.", "Test cause.", "Test fix.", "test", false);
    }

    private sealed class MetadataIncapableStore : IAppSurfaceLocalSecretStore
    {
        public string Name => "metadata-incapable";
        public AppSurfaceLocalSecretResult Get(AppSurfaceLocalSecretIdentity identity) => AppSurfaceLocalSecretResult.Missing(Name);
        public AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value) => AppSurfaceLocalSecretResult.Found(string.Empty, Name);
        public AppSurfaceLocalSecretResult Delete(AppSurfaceLocalSecretIdentity identity) => AppSurfaceLocalSecretResult.Missing(Name);
        public AppSurfaceLocalSecretListResult List(string applicationName, string environment, string? keyPrefix) => AppSurfaceLocalSecretListResult.Found([], Name);
        public AppSurfaceLocalSecretResult Doctor(string applicationName, string environment, string? keyPrefix) => AppSurfaceLocalSecretResult.Found(string.Empty, Name);
    }

    private sealed class ControlledMetadataStore(
        LocalSecretResultStatus probeStatus,
        LocalSecretResultStatus getStatus,
        LocalSecretResultStatus setStatus,
        bool includeDiagnostic = true) : IAppSurfaceLocalSecretStore, IAppSurfaceLocalSecretMetadataStore
    {
        public string Name => "controlled";
        public AppSurfaceLocalSecretResult Probe(AppSurfaceLocalSecretIdentity identity) => Result(probeStatus);
        public AppSurfaceLocalSecretResult Get(AppSurfaceLocalSecretIdentity identity) => Result(getStatus);
        public AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value) => Result(setStatus);
        public AppSurfaceLocalSecretResult Delete(AppSurfaceLocalSecretIdentity identity) => AppSurfaceLocalSecretResult.Missing(Name);
        public AppSurfaceLocalSecretListResult List(string applicationName, string environment, string? keyPrefix) => AppSurfaceLocalSecretListResult.Found([], Name);
        public AppSurfaceLocalSecretResult Doctor(string applicationName, string environment, string? keyPrefix) => AppSurfaceLocalSecretResult.Found(string.Empty, Name);

        private AppSurfaceLocalSecretResult Result(LocalSecretResultStatus status) => status switch
        {
            LocalSecretResultStatus.Found => AppSurfaceLocalSecretResult.Found("sentinel", Name),
            LocalSecretResultStatus.Missing => AppSurfaceLocalSecretResult.Missing(Name),
            _ => new AppSurfaceLocalSecretResult(
                status,
                null,
                includeDiagnostic
                    ? new AppSurfaceLocalSecretDiagnostic("controlled-failure", "Controlled failure.", "Controlled cause.", "Controlled fix.", "test")
                    : null,
                Name)
        };
    }
}
