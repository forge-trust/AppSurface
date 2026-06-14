namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class AppSurfaceLocalSecretIdentityNormalizerTests
{
    private readonly AppSurfaceLocalSecretIdentityNormalizer _normalizer = new();

    [Fact]
    public void Normalize_Should_BuildStableStorageName()
    {
        var result = _normalizer.Normalize("My App", "Development", "Payments", "Stripe:ApiKey");

        Assert.True(result.Succeeded);
        Assert.Equal("My-App", result.Identity!.ApplicationName);
        Assert.Equal("Development", result.Identity.Environment);
        Assert.Equal("Payments", result.Identity.KeyPrefix);
        Assert.Equal("Stripe:ApiKey", result.Identity.Key);
        Assert.Equal("appsurface:My-App:Development:Payments:Stripe:ApiKey", result.Identity.StorageName);
    }

    [Theory]
    [InlineData("My/App", "Development", null, "Stripe:ApiKey", "local-secret-applicationName-invalid-character")]
    [InlineData("MyApp", "", null, "Stripe:ApiKey", "local-secret-environment-empty")]
    [InlineData("MyApp", "Development", null, "", "local-secret-key-empty")]
    [InlineData("MyApp", "Development", null, "Stripe\nApiKey", "local-secret-key-invalid-character")]
    public void Normalize_Should_ReturnDiagnosticForInvalidIdentity(
        string app,
        string environment,
        string? prefix,
        string key,
        string expectedCode)
    {
        var result = _normalizer.Normalize(app, environment, prefix, key);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedCode, result.Diagnostic!.Code);
        Assert.DoesNotContain("secret-value", result.Diagnostic.ToDisplayString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_Should_CanonicalizeDoubleUnderscoreKeySeparators()
    {
        var result = _normalizer.Normalize("MyApp", "Development", null, "Stripe__ApiKey");

        Assert.True(result.Succeeded);
        Assert.Equal("Stripe:ApiKey", result.Identity!.Key);
    }
}
