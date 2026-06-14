using ForgeTrust.AppSurface.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class AppSurfaceLocalSecretProviderTests
{
    [Fact]
    public void Constructor_Should_RejectNullStoreAndNormalizer()
    {
        var options = Options.Create(new AppSurfaceLocalSecretsOptions());
        var storeError = Assert.Throws<ArgumentNullException>(() =>
            new AppSurfaceLocalSecretProvider(options, null!, new AppSurfaceLocalSecretIdentityNormalizer()));
        var normalizerError = Assert.Throws<ArgumentNullException>(() =>
            new AppSurfaceLocalSecretProvider(options, new InMemoryAppSurfaceLocalSecretStore(), null!));

        Assert.Equal("store", storeError.ParamName);
        Assert.Equal("normalizer", normalizerError.ParamName);
    }

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
    public void GetValue_Should_StopResolutionWhenPostureIsDisabled()
    {
        var provider = CreateProvider(
            new InMemoryAppSurfaceLocalSecretStore(),
            options => options.Posture = LocalSecretsPostureMode.Disabled);

        var resolution = provider.ResolveValue<string>("Development", "Stripe:ApiKey");

        Assert.Equal(LocalSecretResultStatus.DisabledByPosture, resolution.Status);
        Assert.Equal("local-secret-posture-disabled", resolution.Diagnostic?.Code);
        Assert.Contains("Disabled", resolution.Diagnostic?.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGetTerminalDiagnostic_Should_ReturnFalse_WhenFailClosedIsDisabled()
    {
        var store = new FixedResultStore(AppSurfaceLocalSecretResult.NotFound(
            LocalSecretResultStatus.Locked,
            new AppSurfaceLocalSecretDiagnostic(
                "local-secret-store-locked",
                "Local secret store is locked.",
                "The store rejected access.",
                "Unlock the store."),
            "Fixed"));
        var provider = CreateProvider(store, options => options.FailClosedOnStoreFailure = false);

        var resolution = provider.ResolveValue<string>("Development", "Stripe:ApiKey");

        Assert.Equal(LocalSecretResultStatus.Locked, resolution.Status);
        Assert.False(provider.TryGetTerminalDiagnostic("Development", "Stripe:ApiKey", out _));
    }

    [Fact]
    public void ResolveValue_Should_AllowProduction_WhenSingleMachineSelfHostedIsExplicit()
    {
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
        store.Set(normalizer.Normalize("MyApp", "Production", null, "Stripe:ApiKey").Identity!, "sk_test_secret");
        var provider = CreateProvider(
            store,
            options => options.Posture = LocalSecretsPostureMode.SingleMachineSelfHosted);

        var resolution = provider.ResolveValue<string>("Production", "Stripe:ApiKey");

        Assert.Equal(LocalSecretResultStatus.Found, resolution.Status);
        Assert.Equal("sk_test_secret", resolution.Value);
    }

    [Fact]
    public void ResolveValue_Should_TreatNullFoundValueAsEmptyStringWithoutLeaking()
    {
        var provider = CreateProvider(new FixedResultStore(new AppSurfaceLocalSecretResult(
            LocalSecretResultStatus.Found,
            null,
            null,
            "Fixed")));

        var resolution = provider.ResolveValue<string>("Development", "Stripe:ApiKey");

        Assert.Equal(LocalSecretResultStatus.Found, resolution.Status);
        Assert.Equal(string.Empty, resolution.Value);
        Assert.DoesNotContain("raw-secret", resolution.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveValue_Should_ReturnProviderFailed_WhenStoreThrowsWithoutLeakingValue()
    {
        var provider = CreateProvider(new ThrowingStore());

        var resolution = provider.ResolveValue<string>("Development", "Stripe:ApiKey");

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, resolution.Status);
        Assert.Equal("local-secret-provider-threw", resolution.Diagnostic?.Code);
        Assert.Contains(nameof(InvalidOperationException), resolution.Diagnostic?.Cause, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-secret", resolution.ToString(), StringComparison.Ordinal);
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
    public void GetValue_Should_ConvertEnumGuidAndJsonValues()
    {
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
        store.Set(normalizer.Normalize("MyApp", "Development", null, "Mode").Identity!, "singlemachineselfhosted");
        store.Set(normalizer.Normalize("MyApp", "Development", null, "Tenant").Identity!, "47f6ca0f-5fdc-45d4-87d0-f5d9a69195f4");
        store.Set(normalizer.Normalize("MyApp", "Development", null, "Payload").Identity!, """{"Name":"Stripe","Retries":3}""");
        var provider = CreateProvider(store);

        var mode = provider.GetValue<LocalSecretsPostureMode>("Development", "Mode");
        var tenant = provider.GetValue<Guid>("Development", "Tenant");
        var payload = provider.GetValue<SecretPayload>("Development", "Payload");

        Assert.Equal(LocalSecretsPostureMode.SingleMachineSelfHosted, mode);
        Assert.Equal(Guid.Parse("47f6ca0f-5fdc-45d4-87d0-f5d9a69195f4"), tenant);
        Assert.Equal(new SecretPayload("Stripe", 3), payload);
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
    public void GetValue_Should_StopResolutionWhenConversionOverflowsWithoutLeakingRawValue()
    {
        var overflowingSecret = "999999999999999999999999999999";
        var store = new InMemoryAppSurfaceLocalSecretStore();
        var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
        store.Set(normalizer.Normalize("MyApp", "Development", null, "Port").Identity!, overflowingSecret);
        var provider = CreateProvider(store);

        var value = provider.GetValue<int?>("Development", "Port");
        var resolution = provider.ResolveValue<int?>("Development", "Port");

        Assert.Null(value);
        Assert.Equal(LocalSecretResultStatus.ConversionFailed, resolution.Status);
        Assert.True(provider.TryGetTerminalDiagnostic("Development", "Port", out var diagnostic));
        Assert.Equal("local-secret-conversion-failed", diagnostic.Code);
        Assert.DoesNotContain(overflowingSecret, diagnostic.ToDisplayString(), StringComparison.Ordinal);
        Assert.DoesNotContain(overflowingSecret, resolution.ToString(), StringComparison.Ordinal);
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
        Assert.Equal("Development", exception.EnvironmentName);
        Assert.Equal(nameof(AppSurfaceLocalSecretProvider), exception.ProviderName);
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

    private sealed class ThrowingStore : IAppSurfaceLocalSecretStore
    {
        public string Name => nameof(ThrowingStore);

        public AppSurfaceLocalSecretResult Get(AppSurfaceLocalSecretIdentity identity) =>
            throw new InvalidOperationException("raw-secret must never appear in diagnostics");

        public AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value) =>
            throw new NotSupportedException();

        public AppSurfaceLocalSecretResult Delete(AppSurfaceLocalSecretIdentity identity) =>
            throw new NotSupportedException();

        public AppSurfaceLocalSecretListResult List(string applicationName, string environment, string? keyPrefix) =>
            throw new NotSupportedException();

        public AppSurfaceLocalSecretResult Doctor(string applicationName, string environment, string? keyPrefix) =>
            throw new NotSupportedException();
    }

    private sealed record SecretPayload(string Name, int Retries);

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
