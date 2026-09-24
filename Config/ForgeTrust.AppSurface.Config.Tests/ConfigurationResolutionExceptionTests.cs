namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigurationResolutionExceptionTests
{
    [Fact]
    public void Constructor_Should_ThrowArgumentNullException_WhenProviderNameIsNull()
    {
        var diagnostic = CreateDiagnostic();

        var error = Assert.Throws<ArgumentNullException>(() =>
            new ConfigurationResolutionException("Development", AppSurfaceConfigKey.Parse("Stripe:ApiKey"), null!, diagnostic));

        Assert.Equal("providerName", error.ParamName);
    }

    [Fact]
    public void Constructor_Should_ThrowArgumentNullException_WhenDiagnosticIsNull()
    {
        var error = Assert.Throws<ArgumentNullException>(() =>
            new ConfigurationResolutionException("Development", AppSurfaceConfigKey.Parse("Stripe:ApiKey"), "LocalSecrets", null!));

        Assert.Equal("diagnostic", error.ParamName);
    }

    [Fact]
    public void ToString_Should_IncludeDisplaySafeResolutionContext()
    {
        var exception = new ConfigurationResolutionException(
            "Development",
            AppSurfaceConfigKey.Parse("Stripe:ApiKey"),
            "LocalSecrets",
            CreateDiagnostic());

        var text = exception.ToString();

        Assert.Contains("Environment: Development", text, StringComparison.Ordinal);
        Assert.Contains("Key: Stripe:ApiKey", text, StringComparison.Ordinal);
        Assert.Contains("Configuration provider LocalSecrets stopped resolution.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_Should_KeepTheDefaultIdentifierLimitForStandaloneUse()
    {
        var provider = new string('p', 300);
        var diagnostic = new ConfigProviderTerminalDiagnostic(
            new string('c', 300), "Problem.", "Cause.", "Fix.", new string('d', 300), retryable: false);
        var exception = new ConfigurationResolutionException(
            new string('e', 300), AppSurfaceConfigKey.Parse(new string('k', 300)), provider, diagnostic);

        Assert.Contains(ConfigDiagnosticText.Identifier(provider), exception.Message, StringComparison.Ordinal);
        Assert.Contains(ConfigDiagnosticText.Identifier(diagnostic.Code), exception.Message, StringComparison.Ordinal);
        Assert.Contains(ConfigDiagnosticText.Identifier(diagnostic.Docs), exception.Message, StringComparison.Ordinal);
        Assert.Contains(ConfigDiagnosticText.Identifier(exception.EnvironmentName), exception.ToString(), StringComparison.Ordinal);
        Assert.Contains(ConfigDiagnosticText.Identifier(exception.Key), exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_WithCustomLimit_ShouldBoundRenderingAndPreserveStructuredFields()
    {
        const int limit = 12;
        var environment = "\n" + new string('e', 30);
        var keyText = new string('k', 30);
        var provider = new string('p', 30);
        var code = new string('c', 30);
        var docs = new string('d', 30);
        var key = AppSurfaceConfigKey.Parse(keyText);
        var diagnostic = new ConfigProviderTerminalDiagnostic(code, "Problem.", "Cause.", "Fix.", docs, retryable: false);
        var exception = new ConfigurationResolutionException(environment, key, provider, diagnostic, limit);

        Assert.Contains("Configuration provider " + ConfigDiagnosticText.Identifier(provider, limit), exception.Message, StringComparison.Ordinal);
        Assert.Contains("Code: " + ConfigDiagnosticText.Identifier(code, limit), exception.Message, StringComparison.Ordinal);
        Assert.Contains("Docs: " + ConfigDiagnosticText.Identifier(docs, limit), exception.Message, StringComparison.Ordinal);
        Assert.Contains("Environment: " + ConfigDiagnosticText.Identifier(environment, limit), exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("\\u000a", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("Key: " + ConfigDiagnosticText.Identifier(keyText, limit), exception.ToString(), StringComparison.Ordinal);

        Assert.Equal(environment, exception.EnvironmentName);
        Assert.Equal(keyText, exception.Key);
        Assert.Equal(key, exception.LogicalKey);
        Assert.Equal(provider, exception.ProviderName);
        Assert.Same(diagnostic, exception.Diagnostic);
        Assert.Equal(code, exception.Diagnostic.Code);
        Assert.Equal(docs, exception.Diagnostic.Docs);
    }

    [Fact]
    public void ToDisplayString_WithNonPositiveIdentifierLimit_ShouldThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateDiagnostic().ToDisplayString(0));
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
