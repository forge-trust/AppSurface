using ForgeTrust.AppSurface.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class AppSurfaceLocalSecretProviderTests
{
    [Fact]
    public void GetValue_Should_ReturnSecretWhenStoreFindsValue()
    {
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
        var identity = normalizer.Normalize("MyApp", "Development", null, "Stripe:ApiKey").Identity!;
        store.Set(identity, "sk_test_secret");
        var provider = CreateProvider(store);

        var value = provider.GetValue<string>("Development", "Stripe:ApiKey");

        Assert.Equal("sk_test_secret", value);
        Assert.False(provider.TryGetTerminalDiagnostic("Development", "Stripe:ApiKey", out _));
    }

    [Fact]
    public void GetValue_Should_FallThroughOnlyForTrueMissing()
    {
        var provider = CreateProvider(new InMemoryAppSurfaceLocalSecretStore());

        var value = provider.GetValue<string>("Development", "Stripe:ApiKey");
        var resolution = provider.ResolveValue<string>("Development", "Stripe:ApiKey");

        Assert.Null(value);
        Assert.Equal(LocalSecretResultStatus.Missing, resolution.Status);
        Assert.False(provider.TryGetTerminalDiagnostic("Development", "Stripe:ApiKey", out _));
    }

    [Theory]
    [InlineData(LocalSecretResultStatus.Unavailable)]
    [InlineData(LocalSecretResultStatus.Locked)]
    [InlineData(LocalSecretResultStatus.UnsupportedPlatform)]
    [InlineData(LocalSecretResultStatus.ProviderFailed)]
    public void GetValue_Should_StopResolutionForTerminalStoreStates(LocalSecretResultStatus status)
    {
        var store = new FixedResultStore(AppSurfaceLocalSecretResult.NotFound(
            status,
            new AppSurfaceLocalSecretDiagnostic(
                "local-secret-terminal",
                "Local secret failed.",
                "The store failed without exposing the value.",
                "Run doctor.",
                "docs",
                retryable: true),
            "Fixed"));
        var provider = CreateProvider(store);

        var value = provider.GetValue<string>("Development", "Stripe:ApiKey");

        Assert.Null(value);
        Assert.True(provider.TryGetTerminalDiagnostic("Development", "Stripe:ApiKey", out var diagnostic));
        Assert.Equal("local-secret-terminal", diagnostic.Code);
        Assert.DoesNotContain("raw-secret", diagnostic.ToDisplayString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GetValue_Should_StopResolutionWhenPostureDisallowsEnvironment()
    {
        var provider = CreateProvider(new InMemoryAppSurfaceLocalSecretStore());

        var value = provider.GetValue<string>("Production", "Stripe:ApiKey");
        var resolution = provider.ResolveValue<string>("Production", "Stripe:ApiKey");

        Assert.Null(value);
        Assert.Equal(LocalSecretResultStatus.DisabledByPosture, resolution.Status);
        Assert.True(provider.TryGetTerminalDiagnostic("Production", "Stripe:ApiKey", out var diagnostic));
        Assert.Equal("local-secret-posture-disabled", diagnostic.Code);
    }

    [Fact]
    public void GetValue_Should_ConvertScalarValues()
    {
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
        store.Set(normalizer.Normalize("MyApp", "Development", null, "Port").Identity!, "443");
        var provider = CreateProvider(store);

        var value = provider.GetValue<int?>("Development", "Port");

        Assert.Equal(443, value);
    }

    [Fact]
    public void GetValue_Should_StopResolutionWhenConversionFailsWithoutLeakingRawValue()
    {
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
        store.Set(normalizer.Normalize("MyApp", "Development", null, "Port").Identity!, "not-the-port-secret");
        var provider = CreateProvider(store);

        var value = provider.GetValue<int?>("Development", "Port");
        var resolution = provider.ResolveValue<int?>("Development", "Port");

        Assert.Null(value);
        Assert.Equal(LocalSecretResultStatus.ConversionFailed, resolution.Status);
        Assert.True(provider.TryGetTerminalDiagnostic("Development", "Port", out var diagnostic));
        Assert.Equal("local-secret-conversion-failed", diagnostic.Code);
        Assert.DoesNotContain("not-the-port-secret", diagnostic.ToDisplayString(), StringComparison.Ordinal);
        Assert.DoesNotContain("not-the-port-secret", diagnostic.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveValue_Should_ReturnInvalidIdentityStatus()
    {
        var provider = CreateProvider(new InMemoryAppSurfaceLocalSecretStore());

        var resolution = provider.ResolveValue<string>("Development", "");

        Assert.Equal(LocalSecretResultStatus.InvalidIdentity, resolution.Status);
        Assert.Equal("local-secret-key-empty", resolution.Diagnostic?.Code);
        Assert.True(provider.TryGetTerminalDiagnostic("Development", "", out var diagnostic));
        Assert.Equal("local-secret-key-empty", diagnostic.Code);
    }

    [Fact]
    public void DefaultConfigManager_Should_Not_QueryLowerPriorityProviderAfterTerminalDiagnostic()
    {
        var store = new FixedResultStore(AppSurfaceLocalSecretResult.NotFound(
            LocalSecretResultStatus.Locked,
            new AppSurfaceLocalSecretDiagnostic(
                "local-secret-store-locked",
                "Local secret store is locked.",
                "The store rejected access.",
                "Unlock the store."),
            "Fixed"));
        var localSecrets = CreateProvider(store);
        var fileProvider = new StaticProvider(priority: 1, value: "from-file");
        var environmentProvider = new NullEnvironmentProvider();
        var manager = new DefaultConfigManager(
            environmentProvider,
            [fileProvider, localSecrets],
            NullLogger<DefaultConfigManager>.Instance);

        var exception = Assert.Throws<ConfigurationResolutionException>(() =>
            manager.GetValue<string>("Development", "Stripe:ApiKey"));

        Assert.Equal("local-secret-store-locked", exception.Diagnostic.Code);
        Assert.False(fileProvider.WasCalled);
    }

    [Fact]
    public void DefaultConfigManager_Should_RenderInvalidIdentityDiagnosticForEmptyKey()
    {
        var localSecrets = CreateProvider(new InMemoryAppSurfaceLocalSecretStore());
        var fileProvider = new StaticProvider(priority: 1, value: "from-file");
        var environmentProvider = new NullEnvironmentProvider();
        var manager = new DefaultConfigManager(
            environmentProvider,
            [fileProvider, localSecrets],
            NullLogger<DefaultConfigManager>.Instance);

        var exception = Assert.Throws<ConfigurationResolutionException>(() =>
            manager.GetValue<string>("Development", ""));

        Assert.Equal("local-secret-key-empty", exception.Diagnostic.Code);
        Assert.Equal("", exception.Key);
        Assert.False(fileProvider.WasCalled);
    }

    private static AppSurfaceLocalSecretProvider CreateProvider(
        IAppSurfaceLocalSecretStore store,
        Action<AppSurfaceLocalSecretsOptions>? configure = null)
    {
        var options = new AppSurfaceLocalSecretsOptions
        {
            ApplicationName = "MyApp"
        };
        configure?.Invoke(options);

        return new AppSurfaceLocalSecretProvider(
            Options.Create(options),
            store,
            new AppSurfaceLocalSecretIdentityNormalizer());
    }

    private sealed class FixedResultStore(AppSurfaceLocalSecretResult result) : IAppSurfaceLocalSecretStore
    {
        public string Name => "Fixed";

        public AppSurfaceLocalSecretResult Get(AppSurfaceLocalSecretIdentity identity) => result;

        public AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value) => result;

        public AppSurfaceLocalSecretResult Delete(AppSurfaceLocalSecretIdentity identity) => result;

        public AppSurfaceLocalSecretListResult List(string applicationName, string environment, string? keyPrefix) =>
            AppSurfaceLocalSecretListResult.Failed(result.Status, result.Diagnostic!, Name);

        public AppSurfaceLocalSecretResult Doctor(string applicationName, string environment, string? keyPrefix) => result;
    }

    private sealed class StaticProvider(int priority, string value) : IConfigProvider
    {
        public int Priority { get; } = priority;

        public string Name => nameof(StaticProvider);

        public bool WasCalled { get; private set; }

        public T? GetValue<T>(string environment, string key)
        {
            WasCalled = true;
            return typeof(T) == typeof(string) ? (T)(object)value : default;
        }
    }

    private sealed class NullEnvironmentProvider : IEnvironmentConfigProvider
    {
        public string Environment => "Development";

        public bool IsDevelopment => true;

        public int Priority => 100;

        public string Name => nameof(NullEnvironmentProvider);

        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;

        public T? GetValue<T>(string environment, string key) => default;
    }
}
