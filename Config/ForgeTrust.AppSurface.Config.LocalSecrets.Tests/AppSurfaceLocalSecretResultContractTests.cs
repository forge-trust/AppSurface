namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class AppSurfaceLocalSecretResultContractTests
{
    [Fact]
    public void PublicEnums_Should_KeepStableNumericValues()
    {
        Assert.Equal(0, (int)LocalSecretsPostureMode.DevelopmentOnly);
        Assert.Equal(1, (int)LocalSecretsPostureMode.Disabled);
        Assert.Equal(2, (int)LocalSecretsPostureMode.SingleMachineSelfHosted);

        Assert.Equal(0, (int)LocalSecretResultStatus.Found);
        Assert.Equal(1, (int)LocalSecretResultStatus.Missing);
        Assert.Equal(2, (int)LocalSecretResultStatus.Unavailable);
        Assert.Equal(3, (int)LocalSecretResultStatus.Locked);
        Assert.Equal(4, (int)LocalSecretResultStatus.UnsupportedPlatform);
        Assert.Equal(5, (int)LocalSecretResultStatus.DisabledByPosture);
        Assert.Equal(6, (int)LocalSecretResultStatus.InvalidIdentity);
        Assert.Equal(7, (int)LocalSecretResultStatus.ConversionFailed);
        Assert.Equal(8, (int)LocalSecretResultStatus.ProviderFailed);
    }

    [Fact]
    public void ResultFactories_Should_RedactValuesAndRejectFoundStatusForNotFound()
    {
        var found = AppSurfaceLocalSecretResult.Found("sk_test_secret", "Fixed");
        var nullValue = new AppSurfaceLocalSecretResult(LocalSecretResultStatus.Found, null, null, "Fixed");

        var exception = Assert.Throws<ArgumentException>(() =>
            AppSurfaceLocalSecretResult.NotFound(LocalSecretResultStatus.Found, Diagnostic(), "Fixed"));

        Assert.Contains("[redacted]", found.ToString(), StringComparison.Ordinal);
        ValueSafeAssert.DoesNotExpose("sk_test_secret", found.ToString());
        Assert.Contains("Value: none", nullValue.ToString(), StringComparison.Ordinal);
        Assert.Equal("status", exception.ParamName);
    }

    [Fact]
    public void ResolutionFactories_Should_RedactValuesAndRejectFoundStatusForNotFound()
    {
        var found = AppSurfaceLocalSecretResolution<string>.Found("sk_test_secret", "Fixed");
        var nullValue = AppSurfaceLocalSecretResolution<string>.Found(null, "Fixed");
        var missing = AppSurfaceLocalSecretResolution<string>.NotFound(
            LocalSecretResultStatus.Missing,
            Diagnostic(),
            "Fixed");

        var exception = Assert.Throws<ArgumentException>(() =>
            AppSurfaceLocalSecretResolution<string>.NotFound(LocalSecretResultStatus.Found, Diagnostic(), "Fixed"));

        Assert.Contains("[redacted]", found.ToString(), StringComparison.Ordinal);
        ValueSafeAssert.DoesNotExpose("sk_test_secret", found.ToString());
        Assert.Contains("Value: none", nullValue.ToString(), StringComparison.Ordinal);
        Assert.Contains("Problem:", missing.ToString(), StringComparison.Ordinal);
        Assert.Equal("status", exception.ParamName);
    }

    [Fact]
    public void ListFailureAndDiagnosticToString_Should_RenderPasteSafeDiagnostics()
    {
        var diagnostic = Diagnostic();
        var failed = AppSurfaceLocalSecretListResult.Failed(LocalSecretResultStatus.Locked, diagnostic, "Fixed");

        Assert.Empty(failed.Keys);
        Assert.Same(diagnostic, failed.Diagnostic);
        Assert.Equal("Fixed", failed.Source);
        Assert.Equal(diagnostic.ToDisplayString(), diagnostic.ToString());
        Assert.Contains("local-secret-test", diagnostic.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ListFound_Should_OrderCaseVariantsDeterministically()
    {
        var result = AppSurfaceLocalSecretListResult.Found(
            ["stripe:apikey", "SendGrid:ApiKey", "Stripe:ApiKey"],
            "Fixed");

        Assert.Equal(["SendGrid:ApiKey", "Stripe:ApiKey", "stripe:apikey"], result.Keys);
    }

    [Fact]
    public void IdentityFactories_Should_RejectNullContractInputs()
    {
        var valid = Assert.Throws<ArgumentNullException>(() => AppSurfaceLocalSecretIdentityResult.Valid(null!));
        var invalid = Assert.Throws<ArgumentNullException>(() => AppSurfaceLocalSecretIdentityResult.Invalid(null!));

        Assert.Equal("identity", valid.ParamName);
        Assert.Equal("diagnostic", invalid.ParamName);
    }

    private static AppSurfaceLocalSecretDiagnostic Diagnostic() =>
        new(
            "local-secret-test",
            "Local secret test problem.",
            "The test store reported a safe cause.",
            "Fix the test store.",
            "local-secrets-tests",
            retryable: true);
}
