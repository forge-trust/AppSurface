namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class AppSurfaceLocalSecretResultContractTests
{
    [Fact]
    public void ResultFactories_Should_RedactValuesAndRejectFoundStatusForNotFound()
    {
        var found = AppSurfaceLocalSecretResult.Found("sk_test_secret", "Fixed");
        var nullValue = new AppSurfaceLocalSecretResult(LocalSecretResultStatus.Found, null, null, "Fixed");

        var exception = Assert.Throws<ArgumentException>(() =>
            AppSurfaceLocalSecretResult.NotFound(LocalSecretResultStatus.Found, Diagnostic(), "Fixed"));

        Assert.Contains("[redacted]", found.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sk_test_secret", found.ToString(), StringComparison.Ordinal);
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
        Assert.DoesNotContain("sk_test_secret", found.ToString(), StringComparison.Ordinal);
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

    private static AppSurfaceLocalSecretDiagnostic Diagnostic() =>
        new(
            "local-secret-test",
            "Local secret test problem.",
            "The test store reported a safe cause.",
            "Fix the test store.",
            "local-secrets-tests",
            retryable: true);
}
