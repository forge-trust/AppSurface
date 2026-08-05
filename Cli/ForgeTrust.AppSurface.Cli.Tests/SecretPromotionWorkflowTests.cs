using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CliFx;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Config.GoogleSecretManager;
using ForgeTrust.AppSurface.Config.LocalSecrets;
using ForgeTrust.AppSurface.Core;

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
    public void CredentialFileProfile_RejectsWindowsWhenRestrictiveAclCannotBeVerified()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var credentialPath = temp.WriteFile("credentials.json", "{}");

        var exception = Assert.Throws<CommandException>(() =>
            DefaultSecretPromotionGoogleClientFactory.ValidateCredentialFile(credentialPath, isWindows: true));

        Assert.Contains("not supported on Windows", exception.Message, StringComparison.Ordinal);
        ValueSafeAssert.DoesNotExpose(credentialPath, exception.ToString());
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
        ValueSafeAssert.DoesNotExpose("sentinel-local-secret", File.ReadAllText(planPath));

        var applied = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));

        Assert.True(applied.Succeeded);
        Assert.Equal($"{planPath}.receipt.json", applied.ReceiptPath);
        Assert.Single(google.Writes);
        Assert.Equal("projects/staging/secrets/stripe-api-key", google.Writes[0]);
        ValueSafeAssert.DoesNotExpose("sentinel-local-secret", JsonSerializer.Serialize(applied));
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
        ValueSafeAssert.DoesNotExpose("sentinel-local-secret", JsonSerializer.Serialize(applied));
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
        ValueSafeAssert.DoesNotExpose("sentinel-local-secret", JsonSerializer.Serialize(resumed));

        var resumedAgain = workflow.Apply(new SecretPromotionApplyRequest(
            configPath,
            planPath,
            true,
            null,
            null,
            $"{planPath}.receipt.json",
            context));
        Assert.True(resumedAgain.Succeeded);
        Assert.Single(google.Writes);
        Assert.Equal("ResumeSkippedConfirmedWrite", Assert.Single(resumedAgain.Rows).Action);
    }

    [Fact]
    public void Apply_ResumeReceipt_RejectsUnverifiedWrittenVersionEvidence()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Stripe:ApiKey"), "sentinel-local-secret").Status);
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var planPath = Path.Join(temp.Path, "promotion.plan.json");
        var receiptPath = $"{planPath}.receipt.json";
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "local-to-staging", planPath, false, TimeSpan.FromMinutes(10), context));
        workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));

        var receipt = JsonNode.Parse(File.ReadAllText(receiptPath))!.AsObject();
        receipt["rows"]!.AsArray()[0]!["destinationResource"] = "projects/staging/secrets/stripe-api-key/versions/999";
        File.WriteAllText(receiptPath, receipt.ToJsonString());

        var exception = Assert.Throws<CommandException>(() => workflow.Apply(new SecretPromotionApplyRequest(
            configPath,
            planPath,
            true,
            null,
            null,
            receiptPath,
            context)));

        Assert.Contains("written-version evidence could not be verified", exception.Message, StringComparison.Ordinal);
        Assert.Single(google.Writes);
        Assert.Equal(0, google.AccessCalls);
    }

    [Fact]
    public void Apply_ResumeReceipt_RejectsMutableWrittenVersionAliasBeforeProviderWork()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Stripe:ApiKey"), "sentinel-local-secret").Status);
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var planPath = Path.Join(temp.Path, "promotion.plan.json");
        var receiptPath = $"{planPath}.receipt.json";
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "local-to-staging", planPath, false, TimeSpan.FromMinutes(10), context));
        workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));
        var latest = "projects/staging/secrets/stripe-api-key/versions/latest";
        google.Versions[latest] = Encoding.UTF8.GetBytes("sentinel-alias-value");
        var receipt = JsonNode.Parse(File.ReadAllText(receiptPath))!.AsObject();
        receipt["rows"]!.AsArray()[0]!["destinationResource"] = latest;
        File.WriteAllText(receiptPath, receipt.ToJsonString());

        var exception = Assert.Throws<CommandException>(() => workflow.Apply(new SecretPromotionApplyRequest(
            configPath,
            planPath,
            true,
            null,
            null,
            receiptPath,
            context)));

        Assert.Contains("rows that do not match", exception.Message, StringComparison.Ordinal);
        Assert.Single(google.Writes);
        Assert.Equal(0, google.AccessCalls);
        ValueSafeAssert.DoesNotExpose("sentinel-alias-value", exception.ToString());
    }

    [Fact]
    public void Apply_RepeatedResume_PreservesSkippedAndWrittenRowOrder()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "First"), "sentinel-first").Status);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Second"), "sentinel-second").Status);
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleTwoRowConfiguration());
        var planPath = Path.Join(temp.Path, "promotion.plan.json");
        var receiptPath = $"{planPath}.receipt.json";
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/first"] = true;
        google.Secrets["projects/staging/secrets/second"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "local-to-staging", planPath, false, TimeSpan.FromMinutes(10), context));
        workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));

        var resumed = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, receiptPath, context));
        var resumedAgain = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, receiptPath, context));

        Assert.True(resumed.Succeeded);
        Assert.True(resumedAgain.Succeeded);
        Assert.Single(google.Writes);
        Assert.Equal([1, 2], resumedAgain.Rows.Select(static row => row.RowNumber));
        Assert.Equal(["SkippedExistingDestination", "ResumeSkippedConfirmedWrite"], resumedAgain.Rows.Select(static row => row.Action));
    }

    [Fact]
    public void Apply_ResumeWithBlockedRemainingRow_PreservesWrittenEvidence()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "First"), "sentinel-first").Status);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Second"), "sentinel-second").Status);
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleTwoRowConfiguration());
        var planPath = PathUtils.PathUnder(temp.Path, "promotion.plan.json");
        var receiptPath = $"{planPath}.receipt.json";
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/first"] = false;
        google.Secrets["projects/staging/secrets/second"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "local-to-staging", planPath, false, TimeSpan.FromMinutes(10), context));
        workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));
        var receipt = JsonNode.Parse(File.ReadAllText(receiptPath))!.AsObject();
        receipt["rows"]!.AsArray().RemoveAt(1);
        File.WriteAllText(receiptPath, receipt.ToJsonString());
        google.Secrets["projects/staging/secrets/second"] = true;

        var resumed = workflow.Apply(new SecretPromotionApplyRequest(
            configPath,
            planPath,
            true,
            null,
            null,
            receiptPath,
            context));

        Assert.False(resumed.Succeeded);
        Assert.Equal(receiptPath, resumed.ReceiptPath);
        Assert.Equal(2, google.Writes.Count);
        var journalRows = JsonNode.Parse(File.ReadAllText(receiptPath))!["rows"]!.AsArray();
        Assert.Equal("Written", journalRows[0]!["status"]!.GetValue<string>());
        Assert.Equal("DestinationChanged", journalRows[1]!["status"]!.GetValue<string>());
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
        ValueSafeAssert.DoesNotExpose(credentialPath, exception.ToString());
    }

    [Fact]
    public void CredentialFileProfile_RejectsSymlinkedParentDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var targetDirectory = Path.Join(temp.Path, "target");
        Directory.CreateDirectory(targetDirectory);
        var credentialPath = Path.Join(targetDirectory, "credentials.json");
        File.WriteAllText(credentialPath, "{}");
        File.SetUnixFileMode(credentialPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var linkedDirectory = Path.Join(temp.Path, "linked");
        Directory.CreateSymbolicLink(linkedDirectory, targetDirectory);

        var exception = Assert.Throws<CommandException>(() =>
            DefaultSecretPromotionGoogleClientFactory.ValidateCredentialFile(
                Path.Join(linkedDirectory, "credentials.json")));

        Assert.Contains("must not use symbolic links", exception.Message, StringComparison.Ordinal);
        ValueSafeAssert.DoesNotExpose(credentialPath, exception.ToString());
    }

    [Fact]
    public void CredentialFileProfile_RejectsSharedWritableNonStickyParentDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var sharedDirectory = Path.Join(temp.Path, "shared");
        Directory.CreateDirectory(sharedDirectory);
        File.SetUnixFileMode(
            sharedDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        var credentialPath = Path.Join(sharedDirectory, "credentials.json");
        File.WriteAllText(credentialPath, "{}");
        File.SetUnixFileMode(credentialPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var exception = Assert.Throws<CommandException>(() =>
            DefaultSecretPromotionGoogleClientFactory.ValidateCredentialFile(credentialPath));

        Assert.Contains("must not be writable by group or other users unless sticky", exception.Message, StringComparison.Ordinal);
        ValueSafeAssert.DoesNotExpose(credentialPath, exception.ToString());
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
        ValueSafeAssert.DoesNotExpose("sentinel-credential", exception.ToString());
        ValueSafeAssert.DoesNotExpose(credentialPath, exception.ToString());
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
        ValueSafeAssert.DoesNotExpose("sentinel-remote-secret", exception.ToString());
    }

    [Fact]
    public void Apply_ProductionSource_RequiresPinnedVersionAndExactJobConfirmation()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var context = CreateContext(new InMemoryAppSurfaceLocalSecretStore());
        var configPath = temp.WriteFile("promotion.json", ProductionToStagingConfiguration());
        var planPath = Path.Join(temp.Path, "promotion.plan.json");
        var google = new FakeGoogleClient();
        google.Versions["projects/production/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel-remote-secret");
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));

        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "production-to-staging", planPath, false, TimeSpan.FromMinutes(10), context));

        Assert.True(JsonNode.Parse(File.ReadAllText(planPath))!["production"]!.GetValue<bool>());
        var exception = Assert.Throws<CommandException>(() => workflow.Apply(
            new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context)));
        Assert.Contains("--confirm", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, google.AccessCalls);
        Assert.Empty(google.Writes);
        ValueSafeAssert.DoesNotExpose("sentinel-remote-secret", exception.ToString());
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

        Assert.Contains("plan identity is invalid", exception.Message, StringComparison.Ordinal);
        Assert.Empty(google.Writes);
        ValueSafeAssert.DoesNotExpose("sentinel-remote-secret", exception.ToString());
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
        ValueSafeAssert.DoesNotExpose("sentinel-local-secret", exception.ToString());
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
        row[propertyName] = propertyName == "rowNumber" ? 2 : "tampered";
        File.WriteAllText(planPath, plan.ToJsonString());

        var exception = Assert.Throws<CommandException>(() =>
        {
            workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));
        });

        Assert.Contains("plan identity is invalid", exception.Message, StringComparison.Ordinal);
        Assert.Empty(google.Writes);
        Assert.Equal(0, google.AccessCalls);
        ValueSafeAssert.DoesNotExpose("sentinel-local-secret", exception.ToString());
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("production")]
    [InlineData("ready")]
    [InlineData("destinationHasEnabledVersions")]
    public void Apply_TamperedPlanSafetyField_FailsBeforeReadingOrWritingASecret(string propertyName)
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
        var target = propertyName == "destinationHasEnabledVersions"
            ? plan["rows"]!.AsArray()[0]!.AsObject()
            : plan;
        target[propertyName] = propertyName != "ready";
        File.WriteAllText(planPath, plan.ToJsonString());

        var exception = Assert.Throws<CommandException>(() => workflow.Apply(
            new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context)));

        Assert.Contains("plan identity is invalid", exception.Message, StringComparison.Ordinal);
        Assert.Empty(google.Writes);
        Assert.Equal(0, google.AccessCalls);
    }

    [Fact]
    public void Plan_GoogleToLocal_IsRejectedBeforeProviderWork()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("promotion.json", GoogleToLocalConfiguration());
        var planPath = Path.Join(temp.Path, "promotion.plan.json");
        var google = new FakeGoogleClient();
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));

        var exception = Assert.Throws<CommandException>(() => workflow.CreatePlan(
            new SecretPromotionPlanRequest(configPath, "staging-to-local", planPath, false, TimeSpan.FromMinutes(10), context)));

        Assert.Contains("destinations must be declared Google endpoints", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, google.AccessCalls);
        Assert.Empty(google.Writes);
    }

    [Fact]
    public void Apply_GoogleToGoogle_RejectsInvalidUtf8WithoutWriting()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("promotion.json", GoogleToProductionConfiguration());
        var planPath = Path.Join(temp.Path, "promotion.plan.json");
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = [0xff];
        google.Secrets["projects/production/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-production", planPath, false, TimeSpan.FromMinutes(10), context));

        var applied = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, "staging-to-production", null, null, context));

        Assert.False(applied.Succeeded);
        Assert.Equal("secret-promotion-invalid-payload", Assert.Single(applied.Rows).DiagnosticCode);
        Assert.Empty(google.Writes);
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
    public void Apply_GoogleWriteUnavailable_PreservesDefinitiveRetryableFailure()
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
        var row = Assert.Single(applied.Rows);
        Assert.Equal("Unavailable", row.Status);
        Assert.True(row.Retryable);
        Assert.Empty(google.Writes);
        Assert.True(File.Exists($"{planPath}.receipt.json"));
        ValueSafeAssert.DoesNotExpose("sentinel-local-secret", File.ReadAllText($"{planPath}.receipt.json"));
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
    public void Plan_MissingGoogleSource_ReturnsProviderFailure()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var google = new FakeGoogleClient();
        google.Secrets["projects/production/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));

        var plan = workflow.CreatePlan(new SecretPromotionPlanRequest(
            temp.WriteFile("promotion.json", GoogleToProductionConfiguration()),
            "staging-to-production",
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
        var configPath = temp.WriteFile("promotion.json", GoogleToProductionConfiguration());
        var planPath = PathUtils.PathUnder(temp.Path, "plan.json");
        var receiptPath = PathUtils.PathUnder(temp.Path, "receipt.json");
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel");
        google.Secrets["projects/production/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-production", planPath, false, TimeSpan.FromMinutes(10), context));
        google.AccessOverride = AppSurfaceGoogleSecretAccessResult.Failed(status, "projects/staging/secrets/stripe-api-key/versions/7", FakeGoogleClient.Diagnostic());

        var result = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, "staging-to-production", receiptPath, null, context));

        Assert.False(result.Succeeded);
        Assert.Empty(google.Writes);
        Assert.DoesNotContain("IndeterminateWrite", File.ReadAllText(receiptPath), StringComparison.Ordinal);
        ValueSafeAssert.DoesNotExpose("sentinel", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(GoogleSecretManagerTransferStatus.Missing, "DestinationMissing", true)]
    [InlineData(GoogleSecretManagerTransferStatus.AccessDenied, "AccessDenied", true)]
    [InlineData(GoogleSecretManagerTransferStatus.ProviderFailed, "Failed", true)]
    [InlineData(GoogleSecretManagerTransferStatus.Cancelled, "Cancelled", true)]
    [InlineData(GoogleSecretManagerTransferStatus.Unavailable, "Unavailable", false)]
    [InlineData(GoogleSecretManagerTransferStatus.IndeterminateWrite, "IndeterminateWrite", false)]
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
        var receiptPath = PathUtils.PathUnder(temp.Path, "receipt.json");
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "local-to-staging", planPath, false, TimeSpan.FromMinutes(10), context));
        google.WriteOverride = new AppSurfaceGoogleSecretWriteResult(
            status,
            "projects/staging/secrets/stripe-api-key",
            null,
            includeDiagnostic ? FakeGoogleClient.Diagnostic() : null);

        var result = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, receiptPath, null, context));

        Assert.False(result.Succeeded);
        Assert.Equal(expected, Assert.Single(result.Rows).Status);
        Assert.Equal(expected, JsonNode.Parse(File.ReadAllText(receiptPath))!["rows"]![0]!["status"]!.GetValue<string>());
        ValueSafeAssert.DoesNotExpose("sentinel", JsonSerializer.Serialize(result));
        ValueSafeAssert.DoesNotExpose("sentinel", File.ReadAllText(receiptPath));
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

    [Fact]
    public void Apply_LocalFoundNullValue_WritesAnEmptyGooglePayload()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var context = CreateContext(new NullValueMetadataStore());
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var planPath = PathUtils.PathUnder(temp.Path, "plan.json");
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(
            configPath,
            "local-to-staging",
            planPath,
            false,
            TimeSpan.FromMinutes(10),
            context));

        var result = workflow.Apply(new SecretPromotionApplyRequest(
            configPath,
            planPath,
            true,
            null,
            null,
            null,
            context));

        Assert.True(result.Succeeded);
        Assert.Equal(string.Empty, Assert.Single(google.WrittenValues));
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
    public void Apply_DryRunBlockedPreflight_DoesNotWriteReceipt()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var identity = Normalize(context, "Stripe:ApiKey");
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(identity, "sentinel").Status);
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var planPath = PathUtils.PathUnder(temp.Path, "plan.json");
        var receiptPath = PathUtils.PathUnder(temp.Path, "dry-run-receipt.json");
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(
            configPath,
            "local-to-staging",
            planPath,
            false,
            TimeSpan.FromMinutes(10),
            context));
        Assert.Equal(LocalSecretResultStatus.Found, store.Delete(identity).Status);

        var result = workflow.Apply(new SecretPromotionApplyRequest(
            configPath,
            planPath,
            false,
            null,
            receiptPath,
            null,
            context));

        Assert.False(result.Apply);
        Assert.False(result.Succeeded);
        Assert.False(File.Exists(receiptPath));
        Assert.False(File.Exists($"{planPath}.receipt.json"));
        Assert.Empty(google.Writes);
    }

    [Fact]
    public void Apply_ReadyDryRun_DoesNotReadPayloadOrWriteReceipt()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new ControlledMetadataStore(
            LocalSecretResultStatus.Found,
            LocalSecretResultStatus.ProviderFailed,
            LocalSecretResultStatus.Found);
        var context = CreateContext(store);
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var planPath = PathUtils.PathUnder(temp.Path, "plan.json");
        var receiptPath = PathUtils.PathUnder(temp.Path, "dry-run-receipt.json");
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(
            configPath,
            "local-to-staging",
            planPath,
            false,
            TimeSpan.FromMinutes(10),
            context));

        var result = workflow.Apply(new SecretPromotionApplyRequest(
            configPath,
            planPath,
            false,
            null,
            receiptPath,
            null,
            context));

        Assert.True(result.Succeeded);
        Assert.Null(result.ReceiptPath);
        Assert.False(File.Exists(receiptPath));
        Assert.False(File.Exists($"{planPath}.receipt.json"));
        Assert.Empty(google.Writes);
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
        Assert.Equal("DestinationMissing", Assert.Single(plan.Summary.Rows).Status);
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

        Assert.Contains("plan identity is invalid", exception.Message, StringComparison.Ordinal);
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
    public void Apply_InitialReceiptWriteFailure_BlocksBeforePayloadReadOrMutation()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var context = CreateContext(new InMemoryAppSurfaceLocalSecretStore());
        var configPath = temp.WriteFile("promotion.json", GoogleToProductionConfiguration());
        var planPath = PathUtils.PathUnder(temp.Path, "plan.json");
        var receiptPath = PathUtils.PathUnder(temp.Path, "receipt.json");
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel");
        google.Secrets["projects/production/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(
            new FakeGoogleFactory(google),
            new FailOnWriteReceiptWriter(failOnWrite: 1));
        workflow.CreatePlan(new SecretPromotionPlanRequest(
            configPath,
            "staging-to-production",
            planPath,
            false,
            TimeSpan.FromMinutes(10),
            context));

        var exception = Assert.Throws<CommandException>(() => workflow.Apply(new SecretPromotionApplyRequest(
            configPath,
            planPath,
            true,
            "staging-to-production",
            receiptPath,
            null,
            context)));

        Assert.Contains("--receipt could not be written", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, google.AccessCalls);
        Assert.Empty(google.Writes);
        Assert.False(File.Exists(receiptPath));
    }

    [Fact]
    public void Apply_DoesNotRewriteCompletedReceiptAfterLastRow()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Stripe:ApiKey"), "sentinel").Status);
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var planPath = PathUtils.PathUnder(temp.Path, "plan.json");
        var receiptPath = PathUtils.PathUnder(temp.Path, "receipt.json");
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var receiptWriter = new FailOnWriteReceiptWriter(failOnWrite: 4);
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google), receiptWriter);
        workflow.CreatePlan(new SecretPromotionPlanRequest(
            configPath,
            "local-to-staging",
            planPath,
            false,
            TimeSpan.FromMinutes(10),
            context));

        var result = workflow.Apply(new SecretPromotionApplyRequest(
            configPath,
            planPath,
            true,
            null,
            receiptPath,
            null,
            context));

        Assert.True(result.Succeeded);
        Assert.Equal(receiptPath, result.ReceiptPath);
        Assert.Equal(3, receiptWriter.WriteCount);
        Assert.Single(google.Writes);
    }

    [Fact]
    public void Apply_PostWriteReceiptFailure_LeavesIndeterminateJournalAndBlocksResume()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Stripe:ApiKey"), "sentinel-local-secret").Status);
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var planPath = PathUtils.PathUnder(temp.Path, "plan.json");
        var receiptPath = PathUtils.PathUnder(temp.Path, "receipt.json");
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(
            new FakeGoogleFactory(google),
            new FailOnWriteReceiptWriter(failOnWrite: 3));
        workflow.CreatePlan(new SecretPromotionPlanRequest(
            configPath,
            "local-to-staging",
            planPath,
            false,
            TimeSpan.FromMinutes(10),
            context));

        var exception = Assert.Throws<CommandException>(() => workflow.Apply(
            new SecretPromotionApplyRequest(configPath, planPath, true, null, receiptPath, null, context)));

        Assert.Contains("--receipt could not be written", exception.Message, StringComparison.Ordinal);
        Assert.Single(google.Writes);
        var receiptText = File.ReadAllText(receiptPath);
        Assert.Contains("IndeterminateWrite", receiptText, StringComparison.Ordinal);
        ValueSafeAssert.DoesNotExpose("sentinel-local-secret", receiptText);

        var resumeException = Assert.Throws<CommandException>(() => new SecretPromotionWorkflow(new FakeGoogleFactory(google)).Apply(
            new SecretPromotionApplyRequest(configPath, planPath, true, null, null, receiptPath, context)));
        Assert.Contains("indeterminate write", resumeException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(google.Writes);
    }

    [Fact]
    public void Apply_SourceReadFailureCannotLeaveIndeterminateReceipt()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new ControlledMetadataStore(
            LocalSecretResultStatus.Found,
            LocalSecretResultStatus.ProviderFailed,
            LocalSecretResultStatus.Found,
            includeDiagnostic: true);
        var context = CreateContext(store);
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var planPath = PathUtils.PathUnder(temp.Path, "plan.json");
        var receiptPath = PathUtils.PathUnder(temp.Path, "receipt.json");
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(
            new FakeGoogleFactory(google),
            new FailOnWriteReceiptWriter(failOnWrite: 2));
        workflow.CreatePlan(new SecretPromotionPlanRequest(
            configPath,
            "local-to-staging",
            planPath,
            false,
            TimeSpan.FromMinutes(10),
            context));

        var exception = Assert.Throws<CommandException>(() => workflow.Apply(
            new SecretPromotionApplyRequest(configPath, planPath, true, null, receiptPath, null, context)));

        Assert.Contains("--receipt could not be written", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("IndeterminateWrite", File.ReadAllText(receiptPath), StringComparison.Ordinal);
        Assert.Empty(google.Writes);
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
        var originalExpiry = plan["expiresAtUtc"]!.DeepClone();

        plan["expiresAtUtc"] = DateTimeOffset.UtcNow.AddMinutes(-1);
        File.WriteAllText(planPath, plan.ToJsonString());
        Assert.Contains("expired", Assert.Throws<CommandException>(() => workflow.Apply(
            new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context))).Message, StringComparison.Ordinal);

        plan["expiresAtUtc"] = originalExpiry;
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
    [InlineData("{\"version\":1,\"jobName\":\"job\",\"rows\":[null]}")]
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

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    public void Apply_SemanticallyInvalidResumeRows_AreRejectedBeforePayloadRead(string rowsJson)
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-promotion-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Stripe:ApiKey"), "sentinel-local-secret").Status);
        var configPath = temp.WriteFile("promotion.json", LocalToGoogleConfiguration());
        var planPath = Path.Join(temp.Path, "plan.json");
        var google = new FakeGoogleClient();
        google.Secrets["projects/staging/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));
        workflow.CreatePlan(new SecretPromotionPlanRequest(
            configPath,
            "local-to-staging",
            planPath,
            false,
            TimeSpan.FromMinutes(10),
            context));
        var plan = JsonNode.Parse(File.ReadAllText(planPath))!.AsObject();
        var receipt = new JsonObject
        {
            ["planJob"] = plan["jobName"]!.DeepClone(),
            ["configDigest"] = plan["configDigest"]!.DeepClone(),
            ["planIdentity"] = plan["planIdentity"]!.DeepClone(),
            ["rows"] = JsonNode.Parse(rowsJson)
        };
        var receiptPath = temp.WriteFile("receipt.json", receipt.ToJsonString());

        var exception = Assert.Throws<CommandException>(() => workflow.Apply(new SecretPromotionApplyRequest(
            configPath,
            planPath,
            true,
            null,
            null,
            receiptPath,
            context)));

        Assert.Contains("rows that do not match", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, google.AccessCalls);
        Assert.Empty(google.Writes);
        ValueSafeAssert.DoesNotExpose("sentinel-local-secret", exception.ToString());
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
        yield return ["{", "valid secret-transfer JSON"];
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
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"production\",\"provider\":\"google\",\"environment\":\"production\",\"credential\":{\"mode\":\"applicationDefault\"}},{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\",\"credential\":{\"mode\":\"applicationDefault\"}}],\"jobs\":[{\"name\":\"job\",\"source\":\"production\",\"destination\":\"staging\",\"rows\":[{\"key\":\"Key\",\"source\":\"projects/p/secrets/s/versions/latest\",\"destination\":\"projects/q/secrets/t\"}]}]}", "explicit numeric version"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\",\"credential\":{\"mode\":\"applicationDefault\"}}],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"staging\",\"rows\":[{\"key\":\"Key\",\"source\":\"unexpected\",\"destination\":\"projects/p/secrets/s\"}]}]}", "Local source rows"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\",\"credential\":{\"mode\":\"applicationDefault\"}}],\"jobs\":[{\"name\":\"job\",\"source\":\"staging\",\"destination\":\"staging\",\"rows\":[{\"key\":\"Key\",\"source\":\"projects/p/secrets/s/versions/1\",\"destination\":\"projects/p/secrets/t\"}]}]}", "same source and destination"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"source\",\"provider\":\"google\",\"environment\":\"staging\",\"credential\":{\"mode\":\"applicationDefault\"}},{\"name\":\"destination\",\"provider\":\"google\",\"environment\":\"staging\",\"credential\":{\"mode\":\"applicationDefault\"}}],\"jobs\":[{\"name\":\"job\",\"source\":\"source\",\"destination\":\"destination\",\"rows\":[{\"key\":\"Key\",\"source\":\"projects/p/secrets/s/versions/1\",\"destination\":\"projects/p/secrets/s\"}]}]}", "same Google secret"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\",\"credential\":{\"mode\":\"applicationDefault\"}}],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"staging\",\"rows\":[]}]}", "at least one row"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\",\"credential\":{\"mode\":\"applicationDefault\"}}],\"jobs\":[{\"name\":\"job\",\"source\":\"staging\",\"destination\":\"local\",\"rows\":[{\"key\":\"Key\",\"source\":\"bad\"}]}]}", "destinations must be declared Google endpoints"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\",\"credential\":{\"mode\":\"applicationDefault\"}}],\"jobs\":[{\"name\":\"job\",\"source\":\"staging\",\"destination\":\"local\",\"rows\":[{\"key\":\"Key\"}]}]}", "destinations must be declared Google endpoints"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\",\"credential\":{\"mode\":\"applicationDefault\"}}],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"staging\",\"rows\":[{\"key\":\"Key\",\"destination\":\"bad\"}]}]}", "Google destination rows require"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\",\"credential\":{\"mode\":\"applicationDefault\"}}],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"staging\",\"rows\":[{\"key\":\"Key\"}]}]}", "Google destination rows require"];
        yield return ["{\"version\":1,\"endpoints\":[],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"missing\",\"rows\":[{\"key\":\"Key\",\"destination\":\"projects/p/secrets/s\"}]}]}", "must be declared once"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\"},{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\"}],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"staging\",\"rows\":[{\"key\":\"Key\",\"destination\":\"projects/p/secrets/s\"}]}]}", "must be declared once"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"remote\",\"provider\":\"local\",\"environment\":\"staging\"}],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"remote\",\"rows\":[{\"key\":\"Key\"}]}]}", "supported remote provider"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\"}],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"staging\",\"rows\":[{\"key\":\"A\",\"destination\":\"projects/p/secrets/s\"},{\"key\":\"B\",\"destination\":\"projects/p/secrets/s\"}]}]}", "duplicate destination"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\"}],\"jobs\":[{\"name\":\"job\",\"source\":\"staging\",\"destination\":\"local\",\"rows\":[{\"key\":\"Key\",\"source\":\"projects/p/secrets/s/versions/1\",\"destination\":\"unexpected\"}]}]}", "destinations must be declared Google endpoints"];
        yield return ["{\"version\":2,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\"}],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"staging\",\"rows\":[{\"key\":\"Key\",\"destination\":\"projects/p/secrets/s\"}]}]}", "V2 is reserved"];
        yield return ["{\"version\":2,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\"}],\"jobs\":[{\"name\":\"job\",\"source\":\"staging\",\"destination\":\"local\",\"rows\":[{\"key\":\"Key\",\"source\":\"projects/p/secrets/s/versions/1\",\"destination\":\"unexpected\"}]}]}", "V2 LocalSecrets destination rows must omit"];
        yield return ["{\"version\":1,\"endpoints\":[{\"name\":\"staging\",\"provider\":\"google\",\"environment\":\"staging\"}],\"jobs\":[{\"name\":\"job\",\"source\":\"local\",\"destination\":\"staging\",\"rows\":[{\"key\":\"Bad\\nKey\",\"destination\":\"projects/p/secrets/s\"}]}]}", "unsupported characters"];
    }

    [Fact]
    public void GoogleToLocalV2_PlanThenApply_MaterializesPinnedValueWithoutValueLeak()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = new SecretsCommandContext(new AppSurfaceLocalSecretIdentityNormalizer(), store, "AppSurfaceApp", "Production", null);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var planPath = Path.Join(temp.Path, "transfer.plan.json");
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel-remote-secret");
        var workflow = CreateV2Workflow(google, temp.Path);

        var planned = workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", planPath, false, TimeSpan.FromMinutes(10), context));
        var applied = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));

        Assert.True(planned.Summary.Succeeded, JsonSerializer.Serialize(planned.Summary));
        Assert.True(applied.Succeeded, JsonSerializer.Serialize(applied));
        Assert.Equal("CreatedLocalSecret", Assert.Single(applied.Rows).Action);
        Assert.Equal("sentinel-remote-secret", store.Get(Normalize(context, "Stripe:ApiKey")).Value);
        Assert.Empty(google.Writes);
        var planJson = File.ReadAllText(planPath);
        Assert.Contains("\"version\": 2", planJson, StringComparison.Ordinal);
        Assert.Contains("\"destinationKind\": \"local\"", planJson, StringComparison.Ordinal);
        ValueSafeAssert.DoesNotExpose("sentinel-remote-secret", planJson);
        ValueSafeAssert.DoesNotExpose("sentinel-remote-secret", JsonSerializer.Serialize(applied));
    }

    [Fact]
    public void GoogleToLocalV2_ExistingUnattestedTarget_IsConflictBeforeSourceAccess()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        store.Set(Normalize(context, "Stripe:ApiKey"), "sentinel-existing-secret");
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel-remote-secret");
        var workflow = CreateV2Workflow(google, temp.Path);

        var planned = workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", Path.Join(temp.Path, "plan.json"), false, TimeSpan.FromMinutes(10), context));

        Assert.False(planned.Summary.Succeeded);
        var row = Assert.Single(planned.Summary.Rows);
        Assert.Equal("Conflict", row.Status);
        Assert.Equal("local-secret-transfer-destination-exists", row.DiagnosticCode);
        Assert.Equal(0, google.AccessCalls);
        Assert.Equal("sentinel-existing-secret", store.Get(Normalize(context, "Stripe:ApiKey")).Value);
    }

    [Fact]
    public void GoogleToLocalV2_StaleAttestationAfterOutOfBandDeleteIsConflictBeforeSourceAccess()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var initialPlanPath = Path.Join(temp.Path, "initial.plan.json");
        var stalePlanPath = Path.Join(temp.Path, "stale.plan.json");
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel-remote-secret");
        var workflow = CreateV2Workflow(google, temp.Path);

        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", initialPlanPath, false, TimeSpan.FromMinutes(10), context));
        workflow.Apply(new SecretPromotionApplyRequest(configPath, initialPlanPath, true, null, null, null, context));
        Assert.Equal(LocalSecretResultStatus.Found, store.Delete(Normalize(context, "Stripe:ApiKey")).Status);

        var planned = workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", stalePlanPath, false, TimeSpan.FromMinutes(10), context));

        Assert.False(planned.Summary.Succeeded);
        Assert.Equal("Conflict", Assert.Single(planned.Summary.Rows).Status);
        Assert.Equal("local-secret-transfer-attestation-stale", Assert.Single(planned.Summary.Rows).DiagnosticCode);
        Assert.Equal(1, google.AccessCalls);
    }

    [Fact]
    public void GoogleToLocalV2_OutOfBandDestinationMutationAfterPlanFailsBeforeSourceAccess()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var planPath = Path.Join(temp.Path, "plan.json");
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel-remote-secret");
        var workflow = CreateV2Workflow(google, temp.Path);

        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", planPath, false, TimeSpan.FromMinutes(10), context));
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(Normalize(context, "Stripe:ApiKey"), "sentinel-manual-secret").Status);

        var applied = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));

        Assert.False(applied.Succeeded);
        Assert.Equal("Conflict", Assert.Single(applied.Rows).Status);
        Assert.Equal("local-secret-transfer-destination-changed", Assert.Single(applied.Rows).DiagnosticCode);
        Assert.Equal(0, google.AccessCalls);
        Assert.Equal("sentinel-manual-secret", store.Get(Normalize(context, "Stripe:ApiKey")).Value);
    }

    [Fact]
    public void GoogleToLocalV2_ReplaceRequiresExactConfirmationAndMatchingAttestation()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var firstPlanPath = Path.Join(temp.Path, "first.plan.json");
        var replacementPlanPath = Path.Join(temp.Path, "replacement.plan.json");
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel-first-secret");
        var workflow = CreateV2Workflow(google, temp.Path);

        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", firstPlanPath, false, TimeSpan.FromMinutes(10), context));
        workflow.Apply(new SecretPromotionApplyRequest(configPath, firstPlanPath, true, null, null, null, context));
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel-replacement-secret");
        var replacement = workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", replacementPlanPath, true, TimeSpan.FromMinutes(10), context));

        var missingConfirmation = Assert.Throws<CommandException>(() => workflow.Apply(
            new SecretPromotionApplyRequest(configPath, replacementPlanPath, true, null, null, null, context)));
        var applied = workflow.Apply(new SecretPromotionApplyRequest(
            configPath,
            replacementPlanPath,
            true,
            "staging-to-local",
            null,
            null,
            context));

        Assert.True(replacement.Summary.Succeeded);
        Assert.Contains("--confirm", missingConfirmation.Message, StringComparison.Ordinal);
        Assert.True(applied.Succeeded);
        Assert.Equal("ReplacedLocalSecret", Assert.Single(applied.Rows).Action);
        Assert.Equal("sentinel-replacement-secret", store.Get(Normalize(context, "Stripe:ApiKey")).Value);
    }

    [Fact]
    public void GoogleToLocalV2_TrimmedProductionSourceLabelStillRequiresExactConfirmation()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile(
            "transfer.json",
            GoogleToLocalV2Configuration().Replace("\"environment\": \"staging\"", "\"environment\": \" production \"", StringComparison.Ordinal));
        var planPath = Path.Join(temp.Path, "plan.json");
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel-remote-secret");
        var workflow = CreateV2Workflow(google, temp.Path);
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", planPath, false, TimeSpan.FromMinutes(10), context));

        var exception = Assert.Throws<CommandException>(() => workflow.Apply(
            new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context)));

        Assert.Contains("--confirm", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, google.AccessCalls);
        Assert.Equal(LocalSecretResultStatus.Missing, store.Get(Normalize(context, "Stripe:ApiKey")).Status);
    }

    [Fact]
    public void GoogleToLocalV2_ResumeUsesCommittedAttestationWithoutGoogleDestinationProbe()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var planPath = Path.Join(temp.Path, "plan.json");
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel-remote-secret");
        var workflow = CreateV2Workflow(google, temp.Path);
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", planPath, false, TimeSpan.FromMinutes(10), context));
        workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));
        google.Versions.Clear();

        var resumed = workflow.Apply(new SecretPromotionApplyRequest(
            configPath,
            planPath,
            true,
            null,
            null,
            $"{planPath}.receipt.json",
            context));

        Assert.True(resumed.Succeeded);
        Assert.Equal("ResumeSkippedConfirmedWrite", Assert.Single(resumed.Rows).Action);
        Assert.Equal(1, google.AccessCalls);
    }

    [Fact]
    public void GoogleToLocalV2_ResumeRejectsAReceiptWhoseCommittedAttestationWasInvalidated()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var stateRoot = Path.Join(temp.Path, "transfer-state");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var planPath = Path.Join(temp.Path, "plan.json");
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel-remote-secret");
        var coordinator = new LocalSecretsTransferCoordinator(stateRoot);
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google), null, coordinator);
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", planPath, false, TimeSpan.FromMinutes(10), context));
        workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));
        var identity = Normalize(context, "Stripe:ApiKey");
        coordinator.InvalidateBeforeMutation(store, identity, () => store.Set(identity, "sentinel-manual-secret"));

        var exception = Assert.Throws<CommandException>(() => workflow.Apply(new SecretPromotionApplyRequest(
            configPath,
            planPath,
            true,
            null,
            null,
            $"{planPath}.receipt.json",
            context)));

        Assert.Contains("local transfer evidence could not be verified", exception.Message, StringComparison.Ordinal);
        Assert.Equal("sentinel-manual-secret", store.Get(identity).Value);
        Assert.Equal(1, google.AccessCalls);
    }

    [Fact]
    public void GoogleToLocalV2_CanonicalProbeAndAccessMismatchesFailBeforeLocalWrite()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel-remote-secret");
        google.ProbeVersionOverride = AppSurfaceGoogleSecretProbeResult.Ready("projects/staging/secrets/stripe-api-key/versions/8");
        var workflow = CreateV2Workflow(google, temp.Path);

        var probeMismatch = workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", Path.Join(temp.Path, "mismatch.plan.json"), false, TimeSpan.FromMinutes(10), context));

        Assert.False(probeMismatch.Summary.Succeeded);
        Assert.Equal("secret-transfer-version-mismatch", Assert.Single(probeMismatch.Summary.Rows).DiagnosticCode);
        google.ProbeVersionOverride = null;
        var planPath = Path.Join(temp.Path, "plan.json");
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", planPath, false, TimeSpan.FromMinutes(10), context));
        google.AccessOverride = AppSurfaceGoogleSecretAccessResult.Accessed(
            "projects/staging/secrets/stripe-api-key/versions/7",
            new AppSurfaceGoogleSecretPayload(Encoding.UTF8.GetBytes("sentinel-remote-secret"), "projects/staging/secrets/stripe-api-key/versions/8"));

        var accessMismatch = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));

        Assert.False(accessMismatch.Succeeded);
        Assert.Equal("secret-transfer-version-mismatch", Assert.Single(accessMismatch.Rows).DiagnosticCode);
        Assert.Equal(LocalSecretResultStatus.Missing, store.Get(Normalize(context, "Stripe:ApiKey")).Status);
        ValueSafeAssert.DoesNotExpose("sentinel-remote-secret", JsonSerializer.Serialize(accessMismatch));
    }

    [Fact]
    public void GoogleToGoogleV1_NonProductionAliasSourceAcceptsCanonicalProviderResponses()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToGoogleNonProductionAliasConfiguration());
        var planPath = Path.Join(temp.Path, "plan.json");
        const string canonical = "projects/staging/secrets/stripe-api-key/versions/7";
        var google = new FakeGoogleClient
        {
            ProbeVersionOverride = AppSurfaceGoogleSecretProbeResult.Ready(canonical),
            AccessOverride = AppSurfaceGoogleSecretAccessResult.Accessed(
                canonical,
                new AppSurfaceGoogleSecretPayload(Encoding.UTF8.GetBytes("sentinel-remote-secret"), canonical))
        };
        google.Secrets["projects/testing/secrets/stripe-api-key"] = false;
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(google));

        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-testing", planPath, false, TimeSpan.FromMinutes(10), context));
        var applied = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));

        Assert.True(applied.Succeeded);
        Assert.Equal("AddedGoogleVersion", Assert.Single(applied.Rows).Action);
        Assert.Equal("projects/testing/secrets/stripe-api-key", Assert.Single(google.Writes));
        ValueSafeAssert.DoesNotExpose("sentinel-remote-secret", JsonSerializer.Serialize(applied));
    }

    [Fact]
    public void GoogleToLocalV2_PreparedRecordResumesOnlyAfterInMemoryValueEquality()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var planPath = Path.Join(temp.Path, "plan.json");
        var receiptPath = Path.Join(temp.Path, "receipt.json");
        var google = new FakeGoogleClient();
        const string sourceResource = "projects/staging/secrets/stripe-api-key/versions/7";
        google.Versions[sourceResource] = Encoding.UTF8.GetBytes("sentinel-remote-secret");
        var workflow = CreateV2Workflow(google, temp.Path);
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", planPath, false, TimeSpan.FromMinutes(10), context));
        var plan = JsonSerializer.Deserialize<SecretPromotionPlanArtifact>(File.ReadAllText(planPath), SecretPromotionWorkflow.JsonOptions)!;
        var row = Assert.Single(plan.Rows);
        var identity = Normalize(context, "Stripe:ApiKey");
        store.Set(identity, "sentinel-remote-secret");
        var stateRoot = Path.Join(temp.Path, "transfer-state");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity.StorageName))).ToLowerInvariant();
        WritePreparedCoordinatorJournal(stateRoot, plan, row, identity);
        File.WriteAllText(
            receiptPath,
            JsonSerializer.Serialize(
                new SecretPromotionReceipt(
                    plan.JobName,
                    plan.ConfigDigest,
                    plan.PlanIdentity,
                    [row.Result(
                        "IndeterminateWrite",
                        "WritePending",
                        "secret-promotion-write-pending",
                        "The destination write must be reconciled if this operation is interrupted.",
                        false)]),
                SecretPromotionWorkflow.JsonOptions));

        var preflight = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, false, null, null, receiptPath, context));

        Assert.True(preflight.Succeeded);
        Assert.Equal("RecoverPreparedLocalSecret", Assert.Single(preflight.Rows).Action);
        Assert.Equal(0, google.AccessCalls);

        var resumed = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, receiptPath, context));

        Assert.True(resumed.Succeeded);
        Assert.Equal("RecoveredLocalSecret", Assert.Single(resumed.Rows).Action);
        var committed = JsonSerializer.Deserialize<LocalTransferJournal>(File.ReadAllText(Path.Join(stateRoot, $"{hash}.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(LocalTransferJournalState.Committed, committed.State);
    }

    [Fact]
    public void GoogleToLocalV2_PreparedRecordWithDifferentLocalValueRemainsAConflict()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var planPath = Path.Join(temp.Path, "plan.json");
        var receiptPath = Path.Join(temp.Path, "receipt.json");
        var google = new FakeGoogleClient();
        const string sourceResource = "projects/staging/secrets/stripe-api-key/versions/7";
        google.Versions[sourceResource] = Encoding.UTF8.GetBytes("sentinel-remote-secret");
        var workflow = CreateV2Workflow(google, temp.Path);
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", planPath, false, TimeSpan.FromMinutes(10), context));
        var plan = JsonSerializer.Deserialize<SecretPromotionPlanArtifact>(File.ReadAllText(planPath), SecretPromotionWorkflow.JsonOptions)!;
        var row = Assert.Single(plan.Rows);
        var identity = Normalize(context, "Stripe:ApiKey");
        store.Set(identity, "sentinel-different-local-secret");
        var stateRoot = Path.Join(temp.Path, "transfer-state");
        WritePreparedCoordinatorJournal(stateRoot, plan, row, identity);
        File.WriteAllText(
            receiptPath,
            JsonSerializer.Serialize(
                new SecretPromotionReceipt(
                    plan.JobName,
                    plan.ConfigDigest,
                    plan.PlanIdentity,
                    [row.Result(
                        "IndeterminateWrite",
                        "WritePending",
                        "secret-promotion-write-pending",
                        "The destination write must be reconciled if this operation is interrupted.",
                        false)]),
                SecretPromotionWorkflow.JsonOptions));

        var resumed = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, receiptPath, context));

        Assert.False(resumed.Succeeded);
        Assert.Equal("Conflict", Assert.Single(resumed.Rows).Status);
        Assert.Equal("sentinel-different-local-secret", store.Get(identity).Value);
        var stillPrepared = JsonSerializer.Deserialize<LocalTransferJournal>(File.ReadAllText(CoordinatorJournalPath(stateRoot, identity)), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(LocalTransferJournalState.Prepared, stillPrepared.State);
    }

    [Fact]
    public void GoogleToLocalV2_PreparedRecordWithoutLocalTargetFailsPreflightBeforePayloadAccess()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var planPath = Path.Join(temp.Path, "plan.json");
        var receiptPath = Path.Join(temp.Path, "receipt.json");
        var google = new FakeGoogleClient();
        const string sourceResource = "projects/staging/secrets/stripe-api-key/versions/7";
        google.Versions[sourceResource] = Encoding.UTF8.GetBytes("sentinel-remote-secret");
        var workflow = CreateV2Workflow(google, temp.Path);
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", planPath, false, TimeSpan.FromMinutes(10), context));
        var plan = JsonSerializer.Deserialize<SecretPromotionPlanArtifact>(File.ReadAllText(planPath), SecretPromotionWorkflow.JsonOptions)!;
        var row = Assert.Single(plan.Rows);
        var identity = Normalize(context, "Stripe:ApiKey");
        var stateRoot = Path.Join(temp.Path, "transfer-state");
        WritePreparedCoordinatorJournal(stateRoot, plan, row, identity);
        File.WriteAllText(
            receiptPath,
            JsonSerializer.Serialize(
                new SecretPromotionReceipt(
                    plan.JobName,
                    plan.ConfigDigest,
                    plan.PlanIdentity,
                    [row.Result(
                        "IndeterminateWrite",
                        "WritePending",
                        "secret-promotion-write-pending",
                        "The destination write must be reconciled if this operation is interrupted.",
                        false)]),
                SecretPromotionWorkflow.JsonOptions));

        var resumed = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, receiptPath, context));

        Assert.False(resumed.Succeeded);
        Assert.Equal("Conflict", Assert.Single(resumed.Rows).Status);
        Assert.Equal(0, google.AccessCalls);
        Assert.Equal(LocalSecretResultStatus.Missing, store.Get(identity).Status);
    }

    [Fact]
    public void GoogleToLocalV2_PreparedRecordCannotProceedWithoutExplicitResume()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var planPath = Path.Join(temp.Path, "plan.json");
        var google = new FakeGoogleClient();
        const string sourceResource = "projects/staging/secrets/stripe-api-key/versions/7";
        google.Versions[sourceResource] = Encoding.UTF8.GetBytes("sentinel-remote-secret");
        var workflow = CreateV2Workflow(google, temp.Path);
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", planPath, false, TimeSpan.FromMinutes(10), context));
        var plan = JsonSerializer.Deserialize<SecretPromotionPlanArtifact>(File.ReadAllText(planPath), SecretPromotionWorkflow.JsonOptions)!;
        var row = Assert.Single(plan.Rows);
        var identity = Normalize(context, "Stripe:ApiKey");
        store.Set(identity, "sentinel-remote-secret");
        var stateRoot = Path.Join(temp.Path, "transfer-state");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity.StorageName))).ToLowerInvariant();
        WritePreparedCoordinatorJournal(stateRoot, plan, row, identity);

        var blocked = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));

        Assert.False(blocked.Succeeded);
        Assert.Equal("IndeterminateWrite", Assert.Single(blocked.Rows).Status);
        Assert.Equal("local-secret-transfer-indeterminate-write", Assert.Single(blocked.Rows).DiagnosticCode);
        Assert.Equal(0, google.AccessCalls);
        Assert.Equal("sentinel-remote-secret", store.Get(identity).Value);
        var stillPrepared = JsonSerializer.Deserialize<LocalTransferJournal>(File.ReadAllText(Path.Join(stateRoot, $"{hash}.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(LocalTransferJournalState.Prepared, stillPrepared.State);
    }

    [Fact]
    public void GoogleToLocalV2_ReplacementRowsCannotBypassReplaceAuthorization()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var initialPlanPath = Path.Join(temp.Path, "initial.plan.json");
        var replacementPlanPath = Path.Join(temp.Path, "replacement.plan.json");
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel-first-secret");
        var workflow = CreateV2Workflow(google, temp.Path);
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", initialPlanPath, false, TimeSpan.FromMinutes(10), context));
        workflow.Apply(new SecretPromotionApplyRequest(configPath, initialPlanPath, true, null, null, null, context));
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel-replacement-secret");
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", replacementPlanPath, true, TimeSpan.FromMinutes(10), context));
        var replacement = JsonSerializer.Deserialize<SecretPromotionPlanArtifact>(File.ReadAllText(replacementPlanPath), SecretPromotionWorkflow.JsonOptions)!;
        var forged = replacement with { Replace = false };
        forged = forged with { PlanIdentity = SecretPromotionWorkflow.ComputePlanIdentity(forged) };
        File.WriteAllText(replacementPlanPath, JsonSerializer.Serialize(forged, SecretPromotionWorkflow.JsonOptions));
        var accessCallsBeforeApply = google.AccessCalls;

        var exception = Assert.Throws<CommandException>(() => workflow.Apply(
            new SecretPromotionApplyRequest(configPath, replacementPlanPath, true, null, null, null, context)));

        Assert.Contains("created with --replace", exception.Message, StringComparison.Ordinal);
        Assert.Equal(accessCallsBeforeApply, google.AccessCalls);
        Assert.Equal("sentinel-first-secret", store.Get(Normalize(context, "Stripe:ApiKey")).Value);
    }

    [Fact]
    public void GoogleToLocalV2_HeldCoordinatorLockFailsPlanBeforePayloadAccess()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var stateRoot = Path.Join(temp.Path, "transfer-state");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel-remote-secret");
        var workflow = new SecretPromotionWorkflow(
            new FakeGoogleFactory(google),
            null,
            new LocalSecretsTransferCoordinator(stateRoot));
        var identity = Normalize(context, "Stripe:ApiKey");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity.StorageName))).ToLowerInvariant();
        workflow.CreatePlan(new SecretPromotionPlanRequest(
            configPath,
            "staging-to-local",
            Path.Join(temp.Path, "initial.plan.json"),
            false,
            TimeSpan.FromMinutes(10),
            context));

        using var heldLock = new FileStream(Path.Join(stateRoot, $"{hash}.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var planned = workflow.CreatePlan(new SecretPromotionPlanRequest(
            configPath,
            "staging-to-local",
            Path.Join(temp.Path, "plan.json"),
            false,
            TimeSpan.FromMinutes(10),
            context));

        Assert.False(planned.Summary.Succeeded);
        Assert.Equal("local-secret-transfer-locked", Assert.Single(planned.Summary.Rows).DiagnosticCode);
        Assert.Equal(0, google.AccessCalls);
    }

    [Fact]
    public void GoogleToLocalV2_CorruptCoordinatorJournalFailsPlanBeforePayloadAccess()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var stateRoot = Path.Join(temp.Path, "transfer-state");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel-remote-secret");
        var workflow = new SecretPromotionWorkflow(
            new FakeGoogleFactory(google),
            null,
            new LocalSecretsTransferCoordinator(stateRoot));
        var identity = Normalize(context, "Stripe:ApiKey");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity.StorageName))).ToLowerInvariant();
        workflow.CreatePlan(new SecretPromotionPlanRequest(
            configPath,
            "staging-to-local",
            Path.Join(temp.Path, "initial.plan.json"),
            false,
            TimeSpan.FromMinutes(10),
            context));
        File.WriteAllText(Path.Join(stateRoot, $"{hash}.json"), "{");

        var planned = workflow.CreatePlan(new SecretPromotionPlanRequest(
            configPath,
            "staging-to-local",
            Path.Join(temp.Path, "plan.json"),
            false,
            TimeSpan.FromMinutes(10),
            context));

        Assert.False(planned.Summary.Succeeded);
        Assert.Equal("local-secret-transfer-journal-corrupt", Assert.Single(planned.Summary.Rows).DiagnosticCode);
        Assert.Equal(0, google.AccessCalls);
    }

    [Fact]
    public void GoogleToLocalV2_CoordinatorStateRootCreationFailureFailsClosedBeforePayloadAccess()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var google = CreateGoogleWithPinnedSource();
        var hooks = new LocalSecretsTransferCoordinatorTestHooks
        {
            BeforeEnsureStateRoot = _ => throw new UnauthorizedAccessException()
        };
        var workflow = CreateV2Workflow(google, temp.Path, hooks);

        var planned = workflow.CreatePlan(new SecretPromotionPlanRequest(
            configPath,
            "staging-to-local",
            Path.Join(temp.Path, "plan.json"),
            false,
            TimeSpan.FromMinutes(10),
            context));

        Assert.False(planned.Summary.Succeeded);
        Assert.Equal("local-secret-transfer-state-root-unavailable", Assert.Single(planned.Summary.Rows).DiagnosticCode);
        Assert.Equal(0, google.AccessCalls);
    }

    [Fact]
    public void GoogleToLocalV2_CoordinatorLockOpenFailureFailsClosedBeforePayloadAccess()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var google = CreateGoogleWithPinnedSource();
        var hooks = new LocalSecretsTransferCoordinatorTestHooks
        {
            BeforeAcquireLock = _ => throw new UnauthorizedAccessException()
        };
        var workflow = CreateV2Workflow(google, temp.Path, hooks);

        var planned = workflow.CreatePlan(new SecretPromotionPlanRequest(
            configPath,
            "staging-to-local",
            Path.Join(temp.Path, "plan.json"),
            false,
            TimeSpan.FromMinutes(10),
            context));

        Assert.False(planned.Summary.Succeeded);
        Assert.Equal("local-secret-transfer-state-root-unavailable", Assert.Single(planned.Summary.Rows).DiagnosticCode);
        Assert.Equal(0, google.AccessCalls);
    }

    [Fact]
    public void GoogleToLocalV2_CoordinatorLockHardeningFailureFailsClosedBeforePayloadAccess()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var google = CreateGoogleWithPinnedSource();
        var workflow = CreateV2Workflow(
            google,
            temp.Path,
            new LocalSecretsTransferCoordinatorTestHooks
            {
                BeforeSecureLockFile = static _ => throw new PlatformNotSupportedException()
            });

        var planned = workflow.CreatePlan(new SecretPromotionPlanRequest(
            configPath,
            "staging-to-local",
            Path.Join(temp.Path, "plan.json"),
            false,
            TimeSpan.FromMinutes(10),
            context));

        Assert.False(planned.Summary.Succeeded);
        Assert.Equal("local-secret-transfer-state-root-unavailable", Assert.Single(planned.Summary.Rows).DiagnosticCode);
        Assert.Equal(0, google.AccessCalls);
    }

    [Fact]
    public void Coordinator_DefaultStateRootUsesTheProfileFallbackWhenLocalApplicationDataIsUnavailable()
    {
        var profile = Path.Join("test", "profile");

        var stateRoot = LocalSecretsTransferCoordinator.GetDefaultStateRoot(string.Empty, profile);

        Assert.Equal(Path.Join(profile, ".appsurface", "AppSurface", "secret-transfer"), stateRoot);
    }

    [Fact]
    public void Coordinator_TightensAStateRootThatLacksUserExecutePermission()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip("Unix file-mode enforcement is not available on Windows.");
        }

        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var stateRoot = Directory.CreateDirectory(Path.Join(temp.Path, "transfer-state")).FullName;
        File.SetUnixFileMode(stateRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var coordinator = new LocalSecretsTransferCoordinator(stateRoot);

        var result = coordinator.CapturePrecondition(Normalize(context, "Stripe:ApiKey"), store, replace: false);

        Assert.Equal(LocalCoordinatorPreconditionKind.Missing, result.Kind);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(stateRoot));
    }

    [Fact]
    public void GoogleToLocalV2_CoordinatorJournalReadFailureFailsPlanBeforePayloadAccess()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var google = CreateGoogleWithPinnedSource();
        var stateRoot = Path.Join(temp.Path, "transfer-state");
        var identity = Normalize(context, "Stripe:ApiKey");
        PrepareCoordinatorStateRoot(stateRoot, store, identity);
        File.WriteAllText(CoordinatorJournalPath(stateRoot, identity), "{}");
        var hooks = new LocalSecretsTransferCoordinatorTestHooks
        {
            BeforeReadJournal = _ => throw new IOException()
        };
        var workflow = new SecretPromotionWorkflow(
            new FakeGoogleFactory(google),
            null,
            new LocalSecretsTransferCoordinator(stateRoot, hooks));

        var planned = workflow.CreatePlan(new SecretPromotionPlanRequest(
            configPath,
            "staging-to-local",
            Path.Join(temp.Path, "plan.json"),
            false,
            TimeSpan.FromMinutes(10),
            context));

        Assert.False(planned.Summary.Succeeded);
        Assert.Equal("local-secret-transfer-journal-corrupt", Assert.Single(planned.Summary.Rows).DiagnosticCode);
        Assert.Equal(0, google.AccessCalls);
    }

    [Fact]
    public void GoogleToLocalV2_UnreadableCoordinatorJournalFailsPlanBeforePayloadAccess()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var google = CreateGoogleWithPinnedSource();
        var stateRoot = Path.Join(temp.Path, "transfer-state");
        var identity = Normalize(context, "Stripe:ApiKey");
        PrepareCoordinatorStateRoot(stateRoot, store, identity);
        File.WriteAllText(CoordinatorJournalPath(stateRoot, identity), "{");
        var workflow = new SecretPromotionWorkflow(
            new FakeGoogleFactory(google),
            null,
            new LocalSecretsTransferCoordinator(stateRoot));

        var planned = workflow.CreatePlan(new SecretPromotionPlanRequest(
            configPath,
            "staging-to-local",
            Path.Join(temp.Path, "plan.json"),
            false,
            TimeSpan.FromMinutes(10),
            context));

        Assert.False(planned.Summary.Succeeded);
        Assert.Equal("local-secret-transfer-journal-corrupt", Assert.Single(planned.Summary.Rows).DiagnosticCode);
        Assert.Equal(0, google.AccessCalls);
    }

    [Fact]
    public void GoogleToLocalV2_CoordinatorJournalWriteFailureDoesNotMutateTheTarget()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var google = CreateGoogleWithPinnedSource();
        var hooks = new LocalSecretsTransferCoordinatorTestHooks
        {
            AfterWriteTemporaryJournal = _ => throw new IOException(),
            BeforeDeleteTemporaryJournal = _ => throw new IOException()
        };
        var workflow = CreateV2Workflow(google, temp.Path, hooks);
        var planPath = Path.Join(temp.Path, "plan.json");

        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", planPath, false, TimeSpan.FromMinutes(10), context));
        var applied = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));

        Assert.False(applied.Succeeded);
        Assert.Equal("Failed", Assert.Single(applied.Rows).Status);
        Assert.Equal("local-secret-transfer-journal-write-failed", Assert.Single(applied.Rows).DiagnosticCode);
        Assert.Equal(LocalSecretResultStatus.Missing, store.Get(Normalize(context, "Stripe:ApiKey")).Status);
    }

    [Fact]
    public void GoogleToLocalV2_TemporaryJournalHardeningFailureDoesNotMutateTheTarget()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var google = CreateGoogleWithPinnedSource();
        var workflow = CreateV2Workflow(
            google,
            temp.Path,
            new LocalSecretsTransferCoordinatorTestHooks
            {
                BeforeSecureTemporaryJournal = static _ => throw new PlatformNotSupportedException()
            });
        var planPath = Path.Join(temp.Path, "plan.json");

        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", planPath, false, TimeSpan.FromMinutes(10), context));
        var applied = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));

        Assert.False(applied.Succeeded);
        Assert.Equal("local-secret-transfer-journal-write-failed", Assert.Single(applied.Rows).DiagnosticCode);
        Assert.Equal(LocalSecretResultStatus.Missing, store.Get(Normalize(context, "Stripe:ApiKey")).Status);
    }

    [Fact]
    public void Coordinator_AttestationClearFailurePreventsTheOrdinaryMutation()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var stateRoot = Path.Join(temp.Path, "transfer-state");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var google = CreateGoogleWithPinnedSource();
        var workflow = new SecretPromotionWorkflow(
            new FakeGoogleFactory(google),
            null,
            new LocalSecretsTransferCoordinator(stateRoot));
        var planPath = Path.Join(temp.Path, "plan.json");

        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", planPath, false, TimeSpan.FromMinutes(10), context));
        workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));
        var identity = Normalize(context, "Stripe:ApiKey");
        var coordinator = new LocalSecretsTransferCoordinator(
            stateRoot,
            new LocalSecretsTransferCoordinatorTestHooks { BeforeDeleteJournal = _ => throw new UnauthorizedAccessException() });
        var result = coordinator.InvalidateBeforeMutation(
            store,
            identity,
            () => store.Set(identity, "sentinel-ordinary-mutation"));

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, result.Status);
        Assert.Equal("local-secret-transfer-attestation-clear-failed", result.Diagnostic?.Code);
        Assert.Equal("sentinel-remote-secret", store.Get(identity).Value);
    }

    [Fact]
    public void CoordinatorOutcomeFactories_PreserveKindsAndFailureDetails()
    {
        var failure = new LocalCoordinatorFailure("test-failure", "Test failure.", Retryable: true);

        Assert.Equal(LocalCoordinatorCheckKind.Unsupported, LocalCoordinatorCheck.Unsupported().Kind);
        Assert.Equal("local-secret-transfer-unsupported-store", LocalCoordinatorCheck.Unsupported().Failure?.Code);
        Assert.Same(failure, LocalCoordinatorCheck.Failed(failure).Failure);

        Assert.Equal(LocalCoordinatorWriteKind.Indeterminate, LocalCoordinatorWriteResult.Indeterminate().Kind);
        Assert.Same(failure, LocalCoordinatorWriteResult.Indeterminate(failure).Failure);
        Assert.Equal(LocalCoordinatorWriteKind.Unsupported, LocalCoordinatorWriteResult.Unsupported().Kind);
        Assert.Equal("local-secret-transfer-unsupported-store", LocalCoordinatorWriteResult.Unsupported().Failure?.Code);
        Assert.Same(failure, LocalCoordinatorWriteResult.Failed(failure).Failure);
    }

    [Fact]
    public void Coordinator_UnsupportedStoreFailsClosedExceptForOrdinaryMutations()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var supportedStore = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(supportedStore);
        var stateRoot = Path.Join(temp.Path, "transfer-state");
        var coordinator = new LocalSecretsTransferCoordinator(stateRoot);
        var (plan, row) = CreateCoordinatorPlan(temp, context, coordinator);
        var unsupportedStore = new MetadataIncapableStore();
        var identity = Normalize(context, "Stripe:ApiKey");
        var mutated = false;

        Assert.Equal(LocalCoordinatorPreconditionKind.Unsupported, coordinator.CapturePrecondition(identity, unsupportedStore, replace: false).Kind);
        Assert.Equal(LocalCoordinatorCheckKind.Unsupported, coordinator.Recheck(plan, row, identity, unsupportedStore, allowPreparedRecovery: false).Kind);
        Assert.Equal(LocalCoordinatorWriteKind.Unsupported, coordinator.WriteOrRecover(plan, row, identity, unsupportedStore, "sentinel-value", allowPreparedRecovery: false).Kind);

        Assert.Equal(LocalCoordinatorCheckKind.Unsupported, coordinator.VerifyCommitted(plan, row, identity, unsupportedStore).Kind);
        var result = coordinator.InvalidateBeforeMutation(
            unsupportedStore,
            identity,
            () =>
            {
                mutated = true;
                return AppSurfaceLocalSecretResult.Found(string.Empty, unsupportedStore.Name);
            });

        Assert.True(mutated);
        Assert.Equal(LocalSecretResultStatus.Found, result.Status);
    }

    [Fact]
    public void Coordinator_AcquisitionFailuresAreReportedAcrossGuardedOperations()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var stateRoot = Path.Join(temp.Path, "transfer-state");
        var (plan, row) = CreateCoordinatorPlan(temp, context, new LocalSecretsTransferCoordinator(stateRoot));
        var coordinator = new LocalSecretsTransferCoordinator(
            stateRoot,
            new LocalSecretsTransferCoordinatorTestHooks { BeforeAcquireLock = _ => throw new UnauthorizedAccessException() });
        var identity = Normalize(context, "Stripe:ApiKey");
        var mutated = false;

        Assert.Equal("local-secret-transfer-state-root-unavailable", coordinator.Recheck(plan, row, identity, store, allowPreparedRecovery: false).Failure?.Code);
        Assert.Equal("local-secret-transfer-state-root-unavailable", coordinator.WriteOrRecover(plan, row, identity, store, "sentinel-value", allowPreparedRecovery: false).Failure?.Code);
        Assert.Equal("local-secret-transfer-state-root-unavailable", coordinator.VerifyCommitted(plan, row, identity, store).Failure?.Code);

        var result = coordinator.InvalidateBeforeMutation(
            store,
            identity,
            () =>
            {
                mutated = true;
                return store.Set(identity, "sentinel-value");
            });

        Assert.False(mutated);
        Assert.Equal(LocalSecretResultStatus.ProviderFailed, result.Status);
        Assert.Equal("local-secret-transfer-state-root-unavailable", result.Diagnostic?.Code);
    }

    [Fact]
    public void Coordinator_JournalReadFailuresAreReportedAcrossGuardedOperations()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var stateRoot = Path.Join(temp.Path, "transfer-state");
        var (plan, row) = CreateCoordinatorPlan(temp, context, new LocalSecretsTransferCoordinator(stateRoot));
        var identity = Normalize(context, "Stripe:ApiKey");
        File.WriteAllText(CoordinatorJournalPath(stateRoot, identity), "{}");
        var coordinator = new LocalSecretsTransferCoordinator(
            stateRoot,
            new LocalSecretsTransferCoordinatorTestHooks { BeforeReadJournal = _ => throw new IOException() });
        var mutated = false;

        Assert.Equal("local-secret-transfer-journal-corrupt", coordinator.Recheck(plan, row, identity, store, allowPreparedRecovery: false).Failure?.Code);
        Assert.Equal("local-secret-transfer-journal-corrupt", coordinator.WriteOrRecover(plan, row, identity, store, "sentinel-value", allowPreparedRecovery: false).Failure?.Code);
        Assert.Equal("local-secret-transfer-journal-corrupt", coordinator.VerifyCommitted(plan, row, identity, store).Failure?.Code);

        var result = coordinator.InvalidateBeforeMutation(
            store,
            identity,
            () =>
            {
                mutated = true;
                return store.Set(identity, "sentinel-value");
            });

        Assert.False(mutated);
        Assert.Equal(LocalSecretResultStatus.ProviderFailed, result.Status);
        Assert.Equal("local-secret-transfer-journal-corrupt", result.Diagnostic?.Code);
    }

    [Fact]
    public void GoogleToLocalV2_CommittedJournalWriteFailureReturnsIndeterminateAfterTheLocalWrite()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var google = CreateGoogleWithPinnedSource();
        var journalWrites = 0;
        var workflow = CreateV2Workflow(
            google,
            temp.Path,
            new LocalSecretsTransferCoordinatorTestHooks
            {
                AfterWriteTemporaryJournal = _ =>
                {
                    journalWrites++;
                    if (journalWrites == 2)
                    {
                        throw new IOException();
                    }
                }
            });
        var planPath = Path.Join(temp.Path, "plan.json");

        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", planPath, false, TimeSpan.FromMinutes(10), context));
        var applied = workflow.Apply(new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context));

        Assert.False(applied.Succeeded);
        Assert.Equal("IndeterminateWrite", Assert.Single(applied.Rows).Status);
        Assert.Equal("local-secret-transfer-journal-write-failed", Assert.Single(applied.Rows).DiagnosticCode);
        Assert.Equal(2, journalWrites);
        Assert.Equal("sentinel-remote-secret", store.Get(Normalize(context, "Stripe:ApiKey")).Value);
    }

    [Fact]
    public void Coordinator_PreparedJournalWithoutResumeAuthorizationIsIndeterminate()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var stateRoot = Path.Join(temp.Path, "transfer-state");
        var coordinator = new LocalSecretsTransferCoordinator(stateRoot);
        var (plan, row) = CreateCoordinatorPlan(temp, context, coordinator);
        var identity = Normalize(context, "Stripe:ApiKey");
        store.Set(identity, "sentinel-value");
        var prepared = new LocalTransferJournal(
            1,
            LocalTransferJournalState.Prepared,
            "0123456789abcdef0123456789abcdef",
            plan.PlanIdentity,
            row.SourceResource!,
            identity.StorageName,
            null);
        File.WriteAllText(
            CoordinatorJournalPath(stateRoot, identity),
            JsonSerializer.Serialize(prepared, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var result = coordinator.WriteOrRecover(plan, row, identity, store, "sentinel-value", allowPreparedRecovery: false);

        Assert.Equal(LocalCoordinatorWriteKind.Indeterminate, result.Kind);
        Assert.Equal(LocalTransferJournalState.Prepared, JsonSerializer.Deserialize<LocalTransferJournal>(File.ReadAllText(CoordinatorJournalPath(stateRoot, identity)), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.State);
    }

    [Fact]
    public void Coordinator_CommittedJournalWithMissingTargetCannotVerifyReceipt()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var stateRoot = Path.Join(temp.Path, "transfer-state");
        var coordinator = new LocalSecretsTransferCoordinator(stateRoot);
        var (plan, row) = CreateCoordinatorPlan(temp, context, coordinator);
        var identity = Normalize(context, "Stripe:ApiKey");
        var prepared = new LocalTransferJournal(
            1,
            LocalTransferJournalState.Committed,
            "0123456789abcdef0123456789abcdef",
            plan.PlanIdentity,
            row.SourceResource!,
            identity.StorageName,
            null);
        File.WriteAllText(
            CoordinatorJournalPath(stateRoot, identity),
            JsonSerializer.Serialize(prepared, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var result = coordinator.VerifyCommitted(plan, row, identity, store);

        Assert.Equal(LocalCoordinatorCheckKind.Conflict, result.Kind);
    }

    [Fact]
    public void Coordinator_StoreDoctorFailuresFailEveryOperationThatRequiresAReadyStore()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var stateRoot = Path.Join(temp.Path, "transfer-state");
        var (plan, row) = CreateCoordinatorPlan(temp, context, new LocalSecretsTransferCoordinator(stateRoot));
        var identity = Normalize(context, "Stripe:ApiKey");
        var coordinator = new LocalSecretsTransferCoordinator(
            stateRoot,
            new LocalSecretsTransferCoordinatorTestHooks { StoreDoctor = static (_, _) => StoreFailure("doctor-failure") });

        Assert.Equal("doctor-failure", coordinator.CapturePrecondition(identity, store, replace: false).Failure?.Code);
        Assert.Equal("doctor-failure", coordinator.Recheck(plan, row, identity, store, allowPreparedRecovery: false).Failure?.Code);
        Assert.Equal("doctor-failure", coordinator.WriteOrRecover(plan, row, identity, store, "sentinel-value", allowPreparedRecovery: false).Failure?.Code);
    }

    [Fact]
    public void Coordinator_StoreProbeFailuresFailEveryOperationThatRequiresCurrentState()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var stateRoot = Path.Join(temp.Path, "transfer-state");
        var (plan, row) = CreateCoordinatorPlan(temp, context, new LocalSecretsTransferCoordinator(stateRoot));
        var identity = Normalize(context, "Stripe:ApiKey");
        var coordinator = new LocalSecretsTransferCoordinator(
            stateRoot,
            new LocalSecretsTransferCoordinatorTestHooks { StoreProbe = static (_, _) => StoreFailure("probe-failure") });

        Assert.Equal("probe-failure", coordinator.CapturePrecondition(identity, store, replace: false).Failure?.Code);
        Assert.Equal("probe-failure", coordinator.Recheck(plan, row, identity, store, allowPreparedRecovery: false).Failure?.Code);
        Assert.Equal("probe-failure", coordinator.WriteOrRecover(plan, row, identity, store, "sentinel-value", allowPreparedRecovery: false).Failure?.Code);
        Assert.Equal("probe-failure", coordinator.VerifyCommitted(plan, row, identity, store).Failure?.Code);
    }

    [Fact]
    public void Coordinator_RecoveryReadAndWriteFaultsNeverReportAConfirmedMutation()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var stateRoot = Path.Join(temp.Path, "transfer-state");
        var coordinator = new LocalSecretsTransferCoordinator(stateRoot);
        var (plan, row) = CreateCoordinatorPlan(temp, context, coordinator);
        var identity = Normalize(context, "Stripe:ApiKey");
        WritePreparedCoordinatorJournal(stateRoot, plan, row, identity);

        var missing = new LocalSecretsTransferCoordinator(
            stateRoot,
            new LocalSecretsTransferCoordinatorTestHooks { StoreGet = static (_, _) => AppSurfaceLocalSecretResult.Missing("test-store") });
        var missingResult = missing.WriteOrRecover(plan, row, identity, store, "sentinel-value", allowPreparedRecovery: true);

        Assert.Equal(LocalCoordinatorWriteKind.Conflict, missingResult.Kind);

        var readFailure = new LocalSecretsTransferCoordinator(
            stateRoot,
            new LocalSecretsTransferCoordinatorTestHooks { StoreGet = static (_, _) => throw new IOException() });
        var readFailureResult = readFailure.WriteOrRecover(plan, row, identity, store, "sentinel-value", allowPreparedRecovery: true);

        Assert.Equal(LocalCoordinatorWriteKind.Failed, readFailureResult.Kind);
        Assert.Equal("local-secret-transfer-recovery-read-failed", readFailureResult.Failure?.Code);

        File.Delete(CoordinatorJournalPath(stateRoot, identity));
        var writeFailure = new LocalSecretsTransferCoordinator(
            stateRoot,
            new LocalSecretsTransferCoordinatorTestHooks { StoreSet = static (_, _, _) => throw new IOException() });
        var writeFailureResult = writeFailure.WriteOrRecover(plan, row, identity, store, "sentinel-value", allowPreparedRecovery: false);

        Assert.Equal(LocalCoordinatorWriteKind.Indeterminate, writeFailureResult.Kind);
        Assert.Equal("local-secret-transfer-write-indeterminate", writeFailureResult.Failure?.Code);
        Assert.Equal(LocalSecretResultStatus.Missing, store.Get(identity).Status);
    }

    [Fact]
    public void Coordinator_ExistingGroupReadableStateRootFailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var stateRoot = Directory.CreateDirectory(Path.Join(temp.Path, "transfer-state")).FullName;
        File.SetUnixFileMode(
            stateRoot,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
        var coordinator = new LocalSecretsTransferCoordinator(stateRoot);
        var identity = Normalize(context, "Stripe:ApiKey");

        var result = coordinator.CapturePrecondition(identity, store, replace: false);

        Assert.Equal(LocalCoordinatorPreconditionKind.Failed, result.Kind);
        Assert.Equal("local-secret-transfer-state-root-unsafe", result.Failure?.Code);
    }

    [Fact]
    public void Coordinator_SymbolicLinkJournalFailsClosed()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var stateRoot = Path.Join(temp.Path, "transfer-state");
        var coordinator = new LocalSecretsTransferCoordinator(stateRoot);
        CreateCoordinatorPlan(temp, context, coordinator);
        var identity = Normalize(context, "Stripe:ApiKey");
        var journalPath = CoordinatorJournalPath(stateRoot, identity);
        var journalTarget = temp.WriteFile("journal-target.json", "{}");
        if (!TryCreateFileSymlink(journalPath, journalTarget))
        {
            throw Xunit.Sdk.SkipException.ForSkip("Symbolic link creation is not available in this environment.");
        }

        var result = coordinator.CapturePrecondition(identity, store, replace: false);

        Assert.Equal(LocalCoordinatorPreconditionKind.Failed, result.Kind);
        Assert.Equal("local-secret-transfer-journal-unsafe", result.Failure?.Code);
    }

    [Fact]
    public void Coordinator_UnsupportedJournalShapeFailsClosed()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var stateRoot = Path.Join(temp.Path, "transfer-state");
        var coordinator = new LocalSecretsTransferCoordinator(stateRoot);
        CreateCoordinatorPlan(temp, context, coordinator);
        var identity = Normalize(context, "Stripe:ApiKey");
        File.WriteAllText(CoordinatorJournalPath(stateRoot, identity), "{}");

        var result = coordinator.CapturePrecondition(identity, store, replace: false);

        Assert.Equal(LocalCoordinatorPreconditionKind.Failed, result.Kind);
        Assert.Equal("local-secret-transfer-journal-corrupt", result.Failure?.Code);
    }

    [Fact]
    public void Coordinator_ReplacementPreconditionMismatchIsAConflict()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var stateRoot = Path.Join(temp.Path, "transfer-state");
        var coordinator = new LocalSecretsTransferCoordinator(stateRoot);
        var (plan, row) = CreateCoordinatorPlan(temp, context, coordinator);
        var identity = Normalize(context, "Stripe:ApiKey");
        store.Set(identity, "sentinel-value");
        var replacementRow = row with
        {
            DestinationExists = true,
            LocalAttestationOperationId = "0123456789abcdef0123456789abcdef"
        };
        var committed = new LocalTransferJournal(
            1,
            LocalTransferJournalState.Committed,
            "fedcba9876543210fedcba9876543210",
            plan.PlanIdentity,
            row.SourceResource!,
            identity.StorageName,
            null);
        File.WriteAllText(
            CoordinatorJournalPath(stateRoot, identity),
            JsonSerializer.Serialize(committed, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var result = coordinator.Recheck(plan, replacementRow, identity, store, allowPreparedRecovery: false);

        Assert.Equal(LocalCoordinatorCheckKind.Conflict, result.Kind);
    }

    [Fact]
    public void Coordinator_PreparedRecoveryCommitFailureIsIndeterminate()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var stateRoot = Path.Join(temp.Path, "transfer-state");
        var (plan, row) = CreateCoordinatorPlan(temp, context, new LocalSecretsTransferCoordinator(stateRoot));
        var identity = Normalize(context, "Stripe:ApiKey");
        store.Set(identity, "sentinel-value");
        WritePreparedCoordinatorJournal(stateRoot, plan, row, identity);
        var coordinator = new LocalSecretsTransferCoordinator(
            stateRoot,
            new LocalSecretsTransferCoordinatorTestHooks { AfterWriteTemporaryJournal = _ => throw new IOException() });

        var result = coordinator.WriteOrRecover(plan, row, identity, store, "sentinel-value", allowPreparedRecovery: true);

        Assert.Equal(LocalCoordinatorWriteKind.Indeterminate, result.Kind);
        Assert.Equal("sentinel-value", store.Get(identity).Value);
    }

    [Fact]
    public void GoogleToLocalV2_ApplyRejectsAPlanWithADifferentVersion()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var planPath = Path.Join(temp.Path, "plan.json");
        var workflow = CreateV2Workflow(CreateGoogleWithPinnedSource(), temp.Path);
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", planPath, false, TimeSpan.FromMinutes(10), context));
        var plan = JsonSerializer.Deserialize<SecretPromotionPlanArtifact>(File.ReadAllText(planPath), SecretPromotionWorkflow.JsonOptions)!;
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan with { Version = 1 }, SecretPromotionWorkflow.JsonOptions));

        var exception = Assert.Throws<CommandException>(() => workflow.Apply(
            new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context)));

        Assert.Contains("plan version does not match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GoogleToLocalV2_ApplyMapsAnUnsupportedLocalStoreToAValueSafeFailure()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var planPath = Path.Join(temp.Path, "plan.json");
        var google = CreateGoogleWithPinnedSource();
        var workflow = CreateV2Workflow(google, temp.Path);
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", planPath, false, TimeSpan.FromMinutes(10), context));
        var unsupportedContext = new SecretsCommandContext(
            context.Normalizer,
            new MetadataIncapableStore(),
            context.ApplicationName,
            context.Environment,
            context.KeyPrefix);

        var applied = workflow.Apply(new SecretPromotionApplyRequest(
            configPath,
            planPath,
            true,
            null,
            null,
            null,
            unsupportedContext));

        Assert.False(applied.Succeeded);
        Assert.Equal("Failed", Assert.Single(applied.Rows).Status);
        Assert.Equal("local-secret-transfer-unsupported-store", Assert.Single(applied.Rows).DiagnosticCode);
        Assert.Equal(0, google.AccessCalls);
    }

    [Fact]
    public void GoogleToLocalV2_ApplyMapsCoordinatorPreflightFailureToAValueSafeFailure()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var planPath = Path.Join(temp.Path, "plan.json");
        var google = CreateGoogleWithPinnedSource();
        CreateV2Workflow(google, temp.Path).CreatePlan(
            new SecretPromotionPlanRequest(configPath, "staging-to-local", planPath, false, TimeSpan.FromMinutes(10), context));
        var failingWorkflow = CreateV2Workflow(
            google,
            temp.Path,
            new LocalSecretsTransferCoordinatorTestHooks { StoreDoctor = static (_, _) => StoreFailure("doctor-failure") });

        var applied = failingWorkflow.Apply(new SecretPromotionApplyRequest(
            configPath,
            planPath,
            true,
            null,
            null,
            null,
            context));

        Assert.False(applied.Succeeded);
        Assert.Equal("Failed", Assert.Single(applied.Rows).Status);
        Assert.Equal("doctor-failure", Assert.Single(applied.Rows).DiagnosticCode);
        Assert.Equal(0, google.AccessCalls);
    }

    [Fact]
    public void GoogleToLocalV2_ApplyRejectsAnInvalidLocalPrecondition()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var planPath = Path.Join(temp.Path, "plan.json");
        var workflow = CreateV2Workflow(CreateGoogleWithPinnedSource(), temp.Path);
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", planPath, false, TimeSpan.FromMinutes(10), context));
        var plan = JsonSerializer.Deserialize<SecretPromotionPlanArtifact>(File.ReadAllText(planPath), SecretPromotionWorkflow.JsonOptions)!;
        var forged = plan with { Rows = [Assert.Single(plan.Rows) with { LocalPreconditionKind = "unexpected" }] };
        forged = forged with { PlanIdentity = SecretPromotionWorkflow.ComputePlanIdentity(forged) };
        File.WriteAllText(planPath, JsonSerializer.Serialize(forged, SecretPromotionWorkflow.JsonOptions));

        var exception = Assert.Throws<CommandException>(() => workflow.Apply(
            new SecretPromotionApplyRequest(configPath, planPath, true, null, null, null, context)));

        Assert.Contains("valid LocalSecrets transfer precondition", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GoogleToLocalV2_InvalidCapturedPreconditionMapsToAValueSafePlanFailure()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var coordinator = new LocalSecretsTransferCoordinator(Path.Join(temp.Path, "transfer-state"));
        var (_, row) = CreateCoordinatorPlan(temp, context, coordinator);

        var result = SecretPromotionWorkflow.ProbeLocalDestination(row with { LocalPreconditionKind = "unexpected" }, replace: false);

        Assert.Equal("Failed", result.Status);
        Assert.Equal("local-secret-transfer-precondition-invalid", result.DiagnosticCode);
        Assert.False(result.Retryable);
    }

    [Fact]
    public void GoogleToLocalV2_UnsupportedWriteMapsToAValueSafeFailure()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var coordinator = new LocalSecretsTransferCoordinator(Path.Join(temp.Path, "transfer-state"));
        var (_, row) = CreateCoordinatorPlan(temp, context, coordinator);

        var result = SecretPromotionWorkflow.MapLocalWriteResult(row, LocalCoordinatorWriteResult.Unsupported());

        Assert.Equal("Failed", result.Status);
        Assert.Equal("local-secret-transfer-unsupported-store", result.DiagnosticCode);
        Assert.False(result.Retryable);
    }

    [Fact]
    public void GoogleToLocalV2_SymbolicLinkStateRootFailsPlanBeforePayloadAccess()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var stateRoot = Path.Join(temp.Path, "transfer-state");
        var stateTarget = Directory.CreateDirectory(Path.Join(temp.Path, "transfer-state-target")).FullName;
        if (!TryCreateDirectorySymlink(stateRoot, stateTarget))
        {
            throw Xunit.Sdk.SkipException.ForSkip("Symbolic link creation is not available in this environment.");
        }

        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel-remote-secret");
        var workflow = new SecretPromotionWorkflow(
            new FakeGoogleFactory(google),
            null,
            new LocalSecretsTransferCoordinator(stateRoot));

        var planned = workflow.CreatePlan(new SecretPromotionPlanRequest(
            configPath,
            "staging-to-local",
            Path.Join(temp.Path, "plan.json"),
            false,
            TimeSpan.FromMinutes(10),
            context));

        Assert.False(planned.Summary.Succeeded);
        Assert.Equal("local-secret-transfer-state-root-unsafe", Assert.Single(planned.Summary.Rows).DiagnosticCode);
        Assert.Equal(0, google.AccessCalls);
    }

    [Fact]
    public void GoogleToLocalV2_CustomStoreIsRejectedWithoutPayloadAccess()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var context = CreateContext(new MetadataIncapableStore());
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel-remote-secret");
        var workflow = CreateV2Workflow(google, temp.Path);

        var planned = workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", Path.Join(temp.Path, "plan.json"), false, TimeSpan.FromMinutes(10), context));

        Assert.False(planned.Summary.Succeeded);
        Assert.Equal("local-secret-transfer-unsupported-store", Assert.Single(planned.Summary.Rows).DiagnosticCode);
        Assert.Equal(0, google.AccessCalls);
    }

    [Fact]
    public void GoogleToLocalV2_RejectsVersionAliasesBeforeAPlanCanBeCreated()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var context = CreateContext(new InMemoryAppSurfaceLocalSecretStore());
        var configPath = temp.WriteFile(
            "transfer.json",
            GoogleToLocalV2Configuration().Replace("/versions/7", "/versions/latest", StringComparison.Ordinal));
        var workflow = CreateV2Workflow(new FakeGoogleClient(), temp.Path);

        var exception = Assert.Throws<CommandException>(() => workflow.CreatePlan(new SecretPromotionPlanRequest(
            configPath,
            "staging-to-local",
            Path.Join(temp.Path, "plan.json"),
            false,
            TimeSpan.FromMinutes(10),
            context)));

        Assert.Contains("explicit numeric version", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GoogleToLocalV2_OrdinaryMutationClearsCommittedAttestationBeforeReplacementPlanning()
    {
        using var temp = TestTempDirectory.Create("appsurface-secret-transfer-");
        var stateRoot = Path.Join(temp.Path, "transfer-state");
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var context = CreateContext(store);
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var initialPlanPath = Path.Join(temp.Path, "initial.plan.json");
        var replacementPlanPath = Path.Join(temp.Path, "replacement.plan.json");
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel-transfer-secret");
        var workflow = new SecretPromotionWorkflow(
            new FakeGoogleFactory(google),
            null,
            new LocalSecretsTransferCoordinator(stateRoot));
        workflow.CreatePlan(new SecretPromotionPlanRequest(configPath, "staging-to-local", initialPlanPath, false, TimeSpan.FromMinutes(10), context));
        workflow.Apply(new SecretPromotionApplyRequest(configPath, initialPlanPath, true, null, null, null, context));
        var identity = Normalize(context, "Stripe:ApiKey");
        var mutation = new LocalSecretsTransferCoordinator(stateRoot).InvalidateBeforeMutation(
            store,
            identity,
            () => store.Set(identity, "sentinel-manual-secret"));

        var replacement = workflow.CreatePlan(new SecretPromotionPlanRequest(
            configPath,
            "staging-to-local",
            replacementPlanPath,
            true,
            TimeSpan.FromMinutes(10),
            context));

        Assert.Equal(LocalSecretResultStatus.Found, mutation.Status);
        Assert.False(replacement.Summary.Succeeded);
        Assert.Equal("local-secret-transfer-unattested-destination", Assert.Single(replacement.Summary.Rows).DiagnosticCode);
        Assert.Equal("sentinel-manual-secret", store.Get(identity).Value);
    }

    [Theory]
    [InlineData(GoogleSecretManagerTransferStatus.Missing)]
    [InlineData(GoogleSecretManagerTransferStatus.AccessDenied)]
    [InlineData(GoogleSecretManagerTransferStatus.Unavailable)]
    [InlineData(GoogleSecretManagerTransferStatus.Cancelled)]
    [InlineData(GoogleSecretManagerTransferStatus.InvalidResource)]
    [InlineData(GoogleSecretManagerTransferStatus.NotEnabled)]
    [InlineData(GoogleSecretManagerTransferStatus.ProviderFailed)]
    public void GoogleSourceFailure_MapsEveryProviderStatusValueSafely(GoogleSecretManagerTransferStatus status)
    {
        var row = new SecretPromotionPlanRow(1, "Key", "staging", "projects/p/secrets/s/versions/1", "production", "projects/q/secrets/t", "storage", false, null);
        var result = row.GoogleSourceFailure("ProbeGoogleSource", status, new AppSurfaceGoogleSecretTransferDiagnostic("diagnostic", "Problem", "Cause", "Fix", "docs", false));

        Assert.False(string.IsNullOrWhiteSpace(result.Status));
        Assert.Equal("ProbeGoogleSource", result.Action);
    }

    private static SecretsCommandContext CreateContext(IAppSurfaceLocalSecretStore store) =>
        new(new AppSurfaceLocalSecretIdentityNormalizer(), store, "AppSurfaceApp", "Development", null);

    private static SecretPromotionWorkflow CreateV2Workflow(FakeGoogleClient google, string temporaryRoot) =>
        new(new FakeGoogleFactory(google), null, new LocalSecretsTransferCoordinator(Path.Join(temporaryRoot, "transfer-state")));

    private static SecretPromotionWorkflow CreateV2Workflow(
        FakeGoogleClient google,
        string temporaryRoot,
        LocalSecretsTransferCoordinatorTestHooks hooks) =>
        new(
            new FakeGoogleFactory(google),
            null,
            new LocalSecretsTransferCoordinator(Path.Join(temporaryRoot, "transfer-state"), hooks));

    private static FakeGoogleClient CreateGoogleWithPinnedSource()
    {
        var google = new FakeGoogleClient();
        google.Versions["projects/staging/secrets/stripe-api-key/versions/7"] = Encoding.UTF8.GetBytes("sentinel-remote-secret");
        return google;
    }

    private static string CoordinatorJournalPath(string stateRoot, AppSurfaceLocalSecretIdentity identity)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity.StorageName))).ToLowerInvariant();
        return Path.Join(stateRoot, $"{hash}.json");
    }

    private static void PrepareCoordinatorStateRoot(
        string stateRoot,
        IAppSurfaceLocalSecretStore store,
        AppSurfaceLocalSecretIdentity identity) =>
        Assert.Equal(
            LocalCoordinatorPreconditionKind.Missing,
            new LocalSecretsTransferCoordinator(stateRoot).CapturePrecondition(identity, store, replace: false).Kind);

    private static (SecretPromotionPlanArtifact Plan, SecretPromotionPlanRow Row) CreateCoordinatorPlan(
        TestTempDirectory temp,
        SecretsCommandContext context,
        LocalSecretsTransferCoordinator coordinator)
    {
        var configPath = temp.WriteFile("transfer.json", GoogleToLocalV2Configuration());
        var planPath = Path.Join(temp.Path, "plan.json");
        var workflow = new SecretPromotionWorkflow(new FakeGoogleFactory(CreateGoogleWithPinnedSource()), null, coordinator);

        var planned = workflow.CreatePlan(new SecretPromotionPlanRequest(
            configPath,
            "staging-to-local",
            planPath,
            false,
            TimeSpan.FromMinutes(10),
            context));
        Assert.True(planned.Summary.Succeeded, JsonSerializer.Serialize(planned.Summary));
        var plan = JsonSerializer.Deserialize<SecretPromotionPlanArtifact>(File.ReadAllText(planPath), SecretPromotionWorkflow.JsonOptions)!;

        return (plan, Assert.Single(plan.Rows));
    }

    private static void WritePreparedCoordinatorJournal(
        string stateRoot,
        SecretPromotionPlanArtifact plan,
        SecretPromotionPlanRow row,
        AppSurfaceLocalSecretIdentity identity)
    {
        var prepared = new LocalTransferJournal(
            1,
            LocalTransferJournalState.Prepared,
            "0123456789abcdef0123456789abcdef",
            plan.PlanIdentity,
            row.SourceResource!,
            identity.StorageName,
            null);
        File.WriteAllText(
            CoordinatorJournalPath(stateRoot, identity),
            JsonSerializer.Serialize(prepared, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static AppSurfaceLocalSecretResult StoreFailure(string code) =>
        new(
            LocalSecretResultStatus.ProviderFailed,
            null,
            new AppSurfaceLocalSecretDiagnostic(code, "Test failure.", "Test cause.", "Test fix.", "test", retryable: true),
            "test-store");

    private static AppSurfaceLocalSecretIdentity Normalize(SecretsCommandContext context, string key) =>
        context.Normalizer.Normalize(context.ApplicationName, context.Environment, context.KeyPrefix, key).Identity!;

    private static bool TryCreateDirectorySymlink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateFileSymlink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

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

    private static string LocalToGoogleTwoRowConfiguration() =>
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
                { "key": "First", "destination": "projects/staging/secrets/first" },
                { "key": "Second", "destination": "projects/staging/secrets/second" }
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

    private static string GoogleToLocalV2Configuration() =>
        """
        {
          "version": 2,
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

    private static string GoogleToGoogleNonProductionAliasConfiguration() =>
        """
        {
          "version": 1,
          "endpoints": [
            { "name": "staging", "provider": "google", "environment": "staging", "credential": { "mode": "applicationDefault" } },
            { "name": "testing", "provider": "google", "environment": "testing", "credential": { "mode": "applicationDefault" } }
          ],
          "jobs": [
            {
              "name": "staging-to-testing",
              "source": "staging",
              "destination": "testing",
              "rows": [
                {
                  "key": "Stripe:ApiKey",
                  "source": "projects/staging/secrets/stripe-api-key/versions/latest",
                  "destination": "projects/testing/secrets/stripe-api-key"
                }
              ]
            }
          ]
        }
        """;

    private static string ProductionToStagingConfiguration() =>
        """
        {
          "version": 1,
          "endpoints": [
            { "name": "production", "provider": "google", "environment": "production", "credential": { "mode": "applicationDefault" } },
            { "name": "staging", "provider": "google", "environment": "staging", "credential": { "mode": "applicationDefault" } }
          ],
          "jobs": [
            {
              "name": "production-to-staging",
              "source": "production",
              "destination": "staging",
              "rows": [
                {
                  "key": "Stripe:ApiKey",
                  "source": "projects/production/secrets/stripe-api-key/versions/7",
                  "destination": "projects/staging/secrets/stripe-api-key"
                }
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
        public HashSet<string> WrittenVersions { get; } = new(StringComparer.Ordinal);
        public List<string> Writes { get; } = [];
        public List<string> WrittenValues { get; } = [];
        public int AccessCalls { get; private set; }
        public int SecretProbeCalls { get; private set; }
        public AppSurfaceGoogleSecretWriteResult? WriteOverride { get; set; }
        public AppSurfaceGoogleSecretAccessResult? AccessOverride { get; set; }
        public AppSurfaceGoogleSecretProbeResult? ProbeVersionOverride { get; set; }

        public AppSurfaceGoogleSecretProbeResult ProbeSecret(string secretResourceName, TimeSpan timeout)
        {
            SecretProbeCalls++;
            return Secrets.TryGetValue(secretResourceName, out var enabled)
                ? AppSurfaceGoogleSecretProbeResult.Ready(secretResourceName, enabled)
                : AppSurfaceGoogleSecretProbeResult.Failed(GoogleSecretManagerTransferStatus.Missing, secretResourceName, Diagnostic());
        }

        public AppSurfaceGoogleSecretProbeResult ProbeSecretVersion(string versionResourceName, TimeSpan timeout) =>
            ProbeVersionOverride ??
            (Versions.ContainsKey(versionResourceName) || WrittenVersions.Contains(versionResourceName)
                ? AppSurfaceGoogleSecretProbeResult.Ready(versionResourceName)
                : AppSurfaceGoogleSecretProbeResult.Failed(GoogleSecretManagerTransferStatus.Missing, versionResourceName, Diagnostic()));

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

            Writes.Add(secretResourceName);
            WrittenValues.Add(value);
            var versionResourceName = $"{secretResourceName}/versions/{Writes.Count}";
            WrittenVersions.Add(versionResourceName);
            return AppSurfaceGoogleSecretWriteResult.Written(secretResourceName, versionResourceName);
        }

        public static AppSurfaceGoogleSecretTransferDiagnostic Diagnostic() =>
            new("test", "Test failure.", "Test cause.", "Test fix.", "test", false);
    }

    private sealed class FailOnWriteReceiptWriter(int failOnWrite) : ISecretPromotionReceiptWriter
    {
        private readonly AtomicSecretPromotionReceiptWriter _inner = new();
        private int _writeCount;

        public int WriteCount => _writeCount;

        public void Write(string path, SecretPromotionReceipt receipt)
        {
            _writeCount++;
            if (_writeCount == failOnWrite)
            {
                throw SecretPromotionCommandExtensions.Usage("--receipt could not be written.");
            }

            _inner.Write(path, receipt);
        }
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

    private sealed class NullValueMetadataStore : IAppSurfaceLocalSecretStore, IAppSurfaceLocalSecretMetadataStore
    {
        public string Name => "null-value";
        public AppSurfaceLocalSecretResult Probe(AppSurfaceLocalSecretIdentity identity) =>
            AppSurfaceLocalSecretResult.Found(string.Empty, Name);

        public AppSurfaceLocalSecretResult Get(AppSurfaceLocalSecretIdentity identity) =>
            new(LocalSecretResultStatus.Found, null, null, Name);

        public AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value) =>
            AppSurfaceLocalSecretResult.Found(string.Empty, Name);

        public AppSurfaceLocalSecretResult Delete(AppSurfaceLocalSecretIdentity identity) =>
            AppSurfaceLocalSecretResult.Missing(Name);

        public AppSurfaceLocalSecretListResult List(string applicationName, string environment, string? keyPrefix) =>
            AppSurfaceLocalSecretListResult.Found([], Name);

        public AppSurfaceLocalSecretResult Doctor(string applicationName, string environment, string? keyPrefix) =>
            AppSurfaceLocalSecretResult.Found(string.Empty, Name);
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
