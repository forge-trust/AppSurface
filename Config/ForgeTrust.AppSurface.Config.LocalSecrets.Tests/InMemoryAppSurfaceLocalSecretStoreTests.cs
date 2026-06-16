namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class InMemoryAppSurfaceLocalSecretStoreTests
{
    [Fact]
    public void Delete_Should_RemoveExistingSecretAndReturnMissingAfterward()
    {
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var identity = Normalize("Stripe:ApiKey");
        store.Set(identity, "sk_test_secret");

        var deleted = store.Delete(identity);
        var missing = store.Get(identity);

        Assert.Equal(LocalSecretResultStatus.Found, deleted.Status);
        Assert.Equal(LocalSecretResultStatus.Missing, missing.Status);
        Assert.DoesNotContain("sk_test_secret", deleted.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_Should_ReturnMissing_WhenSecretDoesNotExist()
    {
        var store = new InMemoryAppSurfaceLocalSecretStore();

        var result = store.Delete(Normalize("Stripe:ApiKey"));

        Assert.Equal(LocalSecretResultStatus.Missing, result.Status);
    }

    [Fact]
    public void List_Should_ReturnOnlyMatchingNamespaceAndPrefix()
    {
        var store = new InMemoryAppSurfaceLocalSecretStore();
        store.Set(Normalize("Stripe:ApiKey", prefix: "Payments"), "stripe");
        store.Set(Normalize("SendGrid:ApiKey", prefix: "Messaging"), "sendgrid");
        store.Set(Normalize("Stripe:WebhookSecret", environment: "Staging", prefix: "Payments"), "webhook");

        var payments = store.List("MyApp", "Development", "Payments");
        var unprefixed = store.List("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.Found, payments.Status);
        Assert.Equal(["Stripe:ApiKey"], payments.Keys);
        Assert.Empty(unprefixed.Keys);
    }

    [Fact]
    public void Doctor_Should_ReturnReadyDiagnosticWithoutSecretValues()
    {
        var store = new InMemoryAppSurfaceLocalSecretStore();

        var result = store.Doctor("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.Missing, result.Status);
        Assert.Equal("local-secret-store-ready", result.Diagnostic?.Code);
        Assert.DoesNotContain("sk_test_secret", result.ToString(), StringComparison.Ordinal);
    }

    private static AppSurfaceLocalSecretIdentity Normalize(
        string key,
        string environment = "Development",
        string? prefix = null) =>
        new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", environment, prefix, key)
            .Identity!;
}
