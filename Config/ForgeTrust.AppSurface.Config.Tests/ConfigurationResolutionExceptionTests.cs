namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigurationResolutionExceptionTests
{
    [Fact]
    public void Constructor_Should_ThrowArgumentNullException_WhenProviderNameIsNull()
    {
        var diagnostic = CreateDiagnostic();

        var error = Assert.Throws<ArgumentNullException>(() =>
            new ConfigurationResolutionException("Development", "Stripe:ApiKey", null!, diagnostic));

        Assert.Equal("providerName", error.ParamName);
    }

    [Fact]
    public void Constructor_Should_ThrowArgumentNullException_WhenDiagnosticIsNull()
    {
        var error = Assert.Throws<ArgumentNullException>(() =>
            new ConfigurationResolutionException("Development", "Stripe:ApiKey", "LocalSecrets", null!));

        Assert.Equal("diagnostic", error.ParamName);
    }

    [Fact]
    public void ToString_Should_IncludeDisplaySafeResolutionContext()
    {
        var exception = new ConfigurationResolutionException(
            "Development",
            "Stripe:ApiKey",
            "LocalSecrets",
            CreateDiagnostic());

        var text = exception.ToString();

        Assert.Contains("Environment: Development", text, StringComparison.Ordinal);
        Assert.Contains("Key: Stripe:ApiKey", text, StringComparison.Ordinal);
        Assert.Contains("Configuration provider LocalSecrets stopped resolution.", text, StringComparison.Ordinal);
    }

    private static ConfigProviderTerminalDiagnostic CreateDiagnostic() =>
        new(
            "local-secret-store-locked",
            "Local secret store is locked.",
            "The local OS secret store rejected access.",
            "Unlock the local store and retry.",
            "local-secrets-without-a-remote-vault",
            retryable: true);
}
