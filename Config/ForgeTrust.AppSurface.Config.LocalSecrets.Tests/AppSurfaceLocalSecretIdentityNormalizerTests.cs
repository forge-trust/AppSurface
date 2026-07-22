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
    [InlineData("MyApp", "Development", "Bad/Prefix", "Stripe:ApiKey", "local-secret-keyPrefix-invalid-character")]
    [InlineData("MyApp", "Prod.Blue", null, "Stripe:ApiKey", "local-secret-environment-invalid-character")]
    [InlineData("MyApp", "", null, "Stripe:ApiKey", "local-secret-environment-empty")]
    [InlineData("MyApp", "Development", null, "", "local-secret-key-empty")]
    [InlineData("MyApp", "Development", null, "Stripe\rApiKey", "local-secret-key-invalid-character")]
    [InlineData("MyApp", "Development", null, "Stripe\0ApiKey", "local-secret-key-invalid-character")]
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
        ValueSafeAssert.DoesNotExpose("secret-value", result.Diagnostic.ToDisplayString());
    }

    [Fact]
    public void Normalize_Should_CanonicalizeDoubleUnderscoreKeySeparators()
    {
        var result = _normalizer.Normalize("MyApp", "Development", null, "Stripe__ApiKey");

        Assert.True(result.Succeeded);
        Assert.Equal("Stripe:ApiKey", result.Identity!.Key);
    }

    [Fact]
    public void Normalize_Should_AllowDotsInApplicationAndPrefixSegments()
    {
        var result = _normalizer.Normalize("My.App", "Development", "Payments.V1", "Stripe\\ApiKey");

        Assert.True(result.Succeeded);
        Assert.Equal("My.App", result.Identity!.ApplicationName);
        Assert.Equal("Payments.V1", result.Identity.KeyPrefix);
        Assert.Equal("Stripe/ApiKey", result.Identity.Key);
    }

    [Fact]
    public void Normalize_Should_ReturnDiagnosticForLongKey()
    {
        var result = _normalizer.Normalize("MyApp", "Development", null, new string('K', 257));

        Assert.False(result.Succeeded);
        Assert.Equal("local-secret-key-too-long", result.Diagnostic!.Code);
    }

    [Fact]
    public void Normalize_Should_ReturnDiagnosticForLongIdentitySegment()
    {
        var result = _normalizer.Normalize(new string('A', 129), "Development", null, "Stripe:ApiKey");

        Assert.False(result.Succeeded);
        Assert.Equal("local-secret-applicationName-too-long", result.Diagnostic!.Code);
    }

    [Fact]
    public void Normalize_Should_ReturnDiagnosticWhenCombinedIdentityIsTooLong()
    {
        var result = _normalizer.Normalize(
            new string('A', 128),
            new string('E', 128),
            new string('P', 128),
            new string('K', 256));

        Assert.False(result.Succeeded);
        Assert.Equal("local-secret-identity-too-long", result.Diagnostic!.Code);
    }

    [Fact]
    public void Normalize_Should_InferApplicationNameWhenOverrideIsBlank()
    {
        var result = _normalizer.Normalize("   ", "Development", null, "Stripe:ApiKey");

        Assert.True(result.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(result.Identity!.ApplicationName));
    }
}
