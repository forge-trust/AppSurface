using System.Text;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.GoogleSecretManager.Tests;

public sealed class GoogleSecretManagerDeclarationValidationTests
{
    [Theory]
    [InlineData("api-key", "4", "project", null, false, true)]
    [InlineData("API_key-7", null, "123456789", "4", false, true)]
    [InlineData("api-key", "stable", "project", null, false, true)]
    [InlineData("api-key", "Release_2026-09", "project", "4", false, true)]
    [InlineData("api-key", null, "project", "stable", false, true)]
    [InlineData("api-key", "latest", "project", null, false, false)]
    [InlineData("api-key", "LATEST", "project", null, false, false)]
    [InlineData("api-key", "latest", "project", null, true, true)]
    [InlineData("api-key", null, "project", "latest", false, false)]
    [InlineData("api-key", null, "project", "latest", true, true)]
    [InlineData("api-key", "4", null, null, false, false)]
    [InlineData("api-key", "4", " ", null, false, false)]
    [InlineData("api-key", "4", "project/other", null, false, false)]
    [InlineData("api-key", "4", "project other", null, false, false)]
    [InlineData("api-key", "4", "project%2fother", null, false, false)]
    [InlineData("api-key", "4", "example.com:project", null, false, true)]
    [InlineData("api-key", null, "project", null, false, false)]
    [InlineData("api-key", "", "project", "4", false, false)]
    [InlineData("api-key", " ", "project", "4", false, false)]
    [InlineData("api-key", "4/extra", "project", null, false, false)]
    [InlineData("api-key", "4.0", "project", null, false, false)]
    [InlineData("api-key", "4x", "project", null, false, false)]
    [InlineData("api-key", "_alias", "project", null, false, false)]
    [InlineData("api-key", "NEW", "project", null, false, false)]
    [InlineData("api-key", "-1", "project", null, false, false)]
    [InlineData("api-key", "a alias", "project", null, false, false)]
    [InlineData("api-key", "alias\n", "project", null, false, false)]
    [InlineData("api-key", "٤", "project", null, false, false)]
    [InlineData("api/key", "4", "project", null, false, false)]
    [InlineData("api%2fkey", "4", "project", null, false, false)]
    [InlineData("api.key", "4", "project", null, false, false)]
    [InlineData("api key", "4", "project", null, false, false)]
    [InlineData("..", "4", "project", null, false, false)]
    [InlineData("", "4", "project", null, false, false)]
    [InlineData(null, "4", "project", null, false, false)]
    [InlineData("projects/prod/secrets/api-key/versions/4", null, null, null, false, true)]
    [InlineData("projects/prod/secrets/api-key/versions/stable", null, null, null, false, true)]
    [InlineData("projects/prod/secrets/api-key/versions/latest", null, null, null, false, false)]
    [InlineData("projects/prod/secrets/api-key/versions/latest", null, null, null, true, true)]
    [InlineData("projects/prod/secrets/api-key/versions/4", "4", "project", "5", false, false)]
    [InlineData("projects/prod/secrets/api-key/versions/4", "", "project", "5", false, false)]
    [InlineData("projects/prod/secrets/api-key/versions/4", " ", "project", "5", false, false)]
    [InlineData("projects/prod/secrets/api-key", null, "project", "4", false, false)]
    [InlineData("projects/prod/secrets/api-key/versions/", null, "project", "4", false, false)]
    [InlineData("projects//secrets/api-key/versions/4", null, "project", null, false, false)]
    [InlineData("projects/prod/secrets//versions/4", null, "project", null, false, false)]
    [InlineData("projects/prod/extra/secrets/api-key/versions/4", null, "project", null, false, false)]
    [InlineData("projects/prod/secrets/api-key/versions/4/extra", null, "project", null, false, false)]
    [InlineData("projects/prod/secrets/api-key/versions/4/versions/5", null, "project", null, false, false)]
    [InlineData("projects/prod/versions/4/secrets/api-key", null, "project", null, false, false)]
    [InlineData("projects/prod/wrong/api-key/versions/4", null, "project", null, false, false)]
    [InlineData("projects/prod/secrets/api-key/wrong/4", null, "project", null, false, false)]
    [InlineData("projects/prod/secrets/api-key/versions/bad.alias", null, "project", null, false, false)]
    [InlineData("projects/prod/secrets/api-key/versions/NEW", null, "project", null, false, false)]
    [InlineData("projects/prod/secrets/api-key/versions/4?x=1", null, "project", null, false, false)]
    [InlineData("projects/../secrets/api-key/versions/4", null, "project", null, false, false)]
    [InlineData("/projects/prod/secrets/api-key/versions/4", null, "project", "4", false, false)]
    public void DeclarationValidation_UsesSharedPolicyAndRejectsMalformedInputsLocally(
        string? key, string? version, string? project, string? defaultVersion, bool allowLatest, bool valid)
    {
        var options = new AppSurfaceGoogleSecretManagerOptions
        {
            ProjectId = project,
            DefaultVersion = defaultVersion,
            AllowLatestVersion = allowLatest
        };
        var client = new RecordingClient();
        var provider = new GoogleSecretManagerConfigProvider(Options.Create(options), client);

        foreach (var environment in new[] { "Production", "Development" })
        {
            var reference = new ConfigSecretReference(environment, "Service:ApiKey", key!, version);
            Assert.Equal(valid ? ConfigSecretReferenceValidationStatus.Supported : ConfigSecretReferenceValidationStatus.Invalid,
                provider.ValidateReference(reference).Status);
            Assert.Empty(client.Resources); // Includes disabled-plan validation: no remote reads.
            if (!valid)
            {
                Assert.Equal(ConfigSecretProviderResolutionStatus.InvalidReference,
                    provider.Resolve(reference, new ConfigSecretResolutionContext(TimeProvider.System, TimeSpan.FromSeconds(5))).Status);
                Assert.Empty(client.Resources);
            }
        }

        if (valid)
        {
            var mappingOptions = new AppSurfaceGoogleSecretManagerOptions
            {
                ProjectId = project,
                DefaultVersion = defaultVersion,
                AllowLatestVersion = allowLatest
            };
            mappingOptions.MapSecret("Service:ApiKey", key!, version);
            Assert.True(new AppSurfaceGoogleSecretManagerOptionsValidator().Validate(null, mappingOptions).Succeeded);
            var expected = GoogleSecretManagerSecretReference.FromMapping(mappingOptions, mappingOptions.Mappings[0]);
            var result = provider.Resolve(new("Production", "Service:ApiKey", key!, version),
                new ConfigSecretResolutionContext(TimeProvider.System, TimeSpan.FromSeconds(5)));
            Assert.Equal(ConfigSecretProviderResolutionStatus.Resolved, result.Status);
            Assert.Equal(expected.ResourceName, Assert.Single(client.Resources));
        }
    }

    [Theory]
    [InlineData(255, 63, true)]
    [InlineData(256, 63, false)]
    [InlineData(255, 64, false)]
    public void DeclarationValidation_BoundsSecretIdsAndAliases(int keyLength, int aliasLength, bool valid)
    {
        var provider = new GoogleSecretManagerConfigProvider(
            Options.Create(new AppSurfaceGoogleSecretManagerOptions { ProjectId = "project" }), new RecordingClient());
        var key = new string('k', keyLength);
        var alias = new string('v', aliasLength);
        Assert.Equal(valid ? ConfigSecretReferenceValidationStatus.Supported : ConfigSecretReferenceValidationStatus.Invalid,
            provider.ValidateReference(new("Production", "Service:ApiKey", key, alias)).Status);
        Assert.Equal(valid ? ConfigSecretReferenceValidationStatus.Supported : ConfigSecretReferenceValidationStatus.Invalid,
            provider.ValidateReference(new("Production", "Service:ApiKey", $"projects/project/secrets/{key}/versions/{alias}", null)).Status);
    }

    [Theory]
    [InlineData("legacy/slash", "strange.version")]
    [InlineData("projects/prod/secrets/api-key/versions/4/extra", null)]
    public void LegacyDirectMappings_RetainHistoricalValidationAndResourceConstruction(string key, string? version)
    {
        var options = new AppSurfaceGoogleSecretManagerOptions { ProjectId = "project" };
        options.MapSecret("Service.ApiKey", key, version);
        Assert.True(new AppSurfaceGoogleSecretManagerOptionsValidator().Validate(null, options).Succeeded);
        var client = new RecordingClient();
        var provider = new GoogleSecretManagerConfigProvider(Options.Create(options), client);

        Assert.Equal("value", provider.GetValue<string>("Production", "Service.ApiKey"));
        Assert.Equal(GoogleSecretManagerSecretReference.FromMapping(options, options.Mappings[0]).ResourceName,
            Assert.Single(client.Resources));
        Assert.Equal(ConfigSecretReferenceValidationStatus.Invalid,
            provider.ValidateReference(new("Production", "Service:ApiKey", key, version)).Status);
    }

    private sealed class RecordingClient : IAppSurfaceGoogleSecretManagerClient
    {
        public List<string> Resources { get; } = [];
        public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout)
        {
            Resources.Add(resourceName);
            return new(Encoding.UTF8.GetBytes("value"), resourceName);
        }
    }
}
