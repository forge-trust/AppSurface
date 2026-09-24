using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class LocalSecretProviderAliasTableTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(4, 1)]
    [InlineData(3, 2)]
    [InlineData(5, 2)]
    [InlineData(6, 2)]
    [InlineData(7, 2)]
    public void StrictInput_ShouldApplyCompleteThreeCandidateTable(int selected, int status)
    {
        const string input = @"Payments__Api\Key";
        var candidates = new[] { input, @"Payments:Api\Key", "Payments__Api/Key" };
        Check(AppSurfaceConfigKey.Parse(input).WithInput(ConfigKeyInputOrigin.StrictString, input), candidates, selected, status);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    public void TranslatedInput_ShouldApplyCompleteTwoCandidateTable(int selected, int status) =>
        Check(AppSurfaceConfigKey.Parse("Payments:ApiKey").WithInput(ConfigKeyInputOrigin.TranslatedDot, "Payments.ApiKey"),
            ["Payments:ApiKey", "Payments.ApiKey"], selected, status);

    private static void Check(AppSurfaceConfigKey key, string[] candidates, int selected, int status)
    {
        const string marker = "identical-payload-marker";
        var directory = Path.Combine(Path.GetTempPath(), "local-alias-table-" + Guid.NewGuid().ToString("N"));
        var store = new FileAppSurfaceLocalSecretStore(Path.Combine(directory, "records.json"));
        try
        {
            var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
            for (var index = 0; index < candidates.Length; index++)
                if ((selected & (1 << index)) != 0)
                    Assert.Equal(LocalSecretResultStatus.Found, store.Set(normalizer.Normalize("App", "Development", null, candidates[index]).Identity!, marker).Status);
            var provider = new AppSurfaceLocalSecretProvider(Options.Create(new AppSurfaceLocalSecretsOptions { ApplicationName = "App" }), store, normalizer);
            var result = provider.Resolve<string>(new ConfigProviderRequest("Development", key));
            Assert.Equal((ConfigProviderValueStatus)status, result.Status);
            if (status == 1)
            {
                Assert.Equal(marker, result.Value);
                if (selected == 1) Assert.Empty(result.Notices);
                else
                {
                    var notice = Assert.Single(result.Notices);
                    Assert.Equal("config-key-legacy-provider-alias", notice.Code);
                    var chosen = Array.FindIndex(candidates, candidate => (selected & (1 << Array.IndexOf(candidates, candidate))) != 0);
                    Assert.Contains(candidates[chosen], notice.Cause);
                    Assert.Contains(key.Value, notice.Fix);
                    Assert.DoesNotContain(marker, notice.Cause + notice.Fix);
                }
            }
            else
            {
                Assert.Null(result.Value);
                Assert.Empty(result.Notices);
                if (status == 2)
                {
                    Assert.Equal("local-secret-key-collision", result.Diagnostic?.Code);
                    Assert.DoesNotContain(marker, result.Diagnostic!.Cause);
                }
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
