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
    public void Apply_TamperedPlanResource_FailsBeforeReadingOrWritingASecret()
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
        plan["rows"]!.AsArray()[0]!["destinationResource"] = "projects/staging/secrets/unreviewed-target";
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

        public AppSurfaceGoogleSecretProbeResult ProbeSecret(string secretResourceName, TimeSpan timeout) =>
            Secrets.TryGetValue(secretResourceName, out var enabled)
                ? AppSurfaceGoogleSecretProbeResult.Ready(secretResourceName, enabled)
                : AppSurfaceGoogleSecretProbeResult.Failed(GoogleSecretManagerTransferStatus.Missing, secretResourceName, Diagnostic());

        public AppSurfaceGoogleSecretProbeResult ProbeSecretVersion(string versionResourceName, TimeSpan timeout) =>
            Versions.ContainsKey(versionResourceName)
                ? AppSurfaceGoogleSecretProbeResult.Ready(versionResourceName)
                : AppSurfaceGoogleSecretProbeResult.Failed(GoogleSecretManagerTransferStatus.Missing, versionResourceName, Diagnostic());

        public AppSurfaceGoogleSecretAccessResult AccessSecretVersion(string versionResourceName, TimeSpan timeout)
        {
            AccessCalls++;
            return Versions.TryGetValue(versionResourceName, out var value)
                ? AppSurfaceGoogleSecretAccessResult.Accessed(versionResourceName, new AppSurfaceGoogleSecretPayload(value, versionResourceName))
                : AppSurfaceGoogleSecretAccessResult.Failed(GoogleSecretManagerTransferStatus.Missing, versionResourceName, Diagnostic());
        }

        public AppSurfaceGoogleSecretWriteResult AddSecretVersion(string secretResourceName, string value, TimeSpan timeout)
        {
            Writes.Add((secretResourceName, value));
            return AppSurfaceGoogleSecretWriteResult.Written(secretResourceName, $"{secretResourceName}/versions/{Writes.Count}");
        }

        private static AppSurfaceGoogleSecretTransferDiagnostic Diagnostic() =>
            new("test", "Test failure.", "Test cause.", "Test fix.", "test", false);
    }
}
