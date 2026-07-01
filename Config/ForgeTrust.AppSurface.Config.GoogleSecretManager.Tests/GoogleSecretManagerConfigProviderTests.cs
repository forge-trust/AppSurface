using System.Text;
using ForgeTrust.AppSurface.Config;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.GoogleSecretManager.Tests;

public sealed class GoogleSecretManagerConfigProviderTests
{
    [Fact]
    public void OptionsValidator_Should_RejectLatestUnlessExplicitlyAllowed()
    {
        var options = new AppSurfaceGoogleSecretManagerOptions
        {
            ProjectId = "project",
            DefaultVersion = AppSurfaceGoogleSecretManagerOptions.LatestVersion
        };
        options.MapSecret("Stripe:ApiKey", "stripe-api-key");

        var result = new AppSurfaceGoogleSecretManagerOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("AllowLatest", StringComparison.Ordinal));
    }

    [Fact]
    public void OptionsValidator_Should_RejectLatestInFullResourceUnlessExplicitlyAllowed()
    {
        var options = new AppSurfaceGoogleSecretManagerOptions();
        options.MapSecret("Stripe:ApiKey", "projects/prod/secrets/stripe-api-key/versions/latest");

        var result = new AppSurfaceGoogleSecretManagerOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("AllowLatest", StringComparison.Ordinal));
    }

    [Fact]
    public void OptionsValidator_Should_AllowLatestInFullResourceWhenExplicitlyAllowed()
    {
        var options = new AppSurfaceGoogleSecretManagerOptions();
        options.AllowLatest();
        options.MapSecret("Stripe:ApiKey", "projects/prod/secrets/stripe-api-key/versions/latest");

        var result = new AppSurfaceGoogleSecretManagerOptionsValidator().Validate(null, options);

        Assert.False(result.Failed);
    }

    [Fact]
    public void OptionsValidator_Should_AllowFullResourceWithoutProjectId()
    {
        var options = new AppSurfaceGoogleSecretManagerOptions();
        options.MapSecret("Stripe:ApiKey", "projects/prod/secrets/stripe-api-key/versions/5");

        var result = new AppSurfaceGoogleSecretManagerOptionsValidator().Validate(null, options);

        Assert.False(result.Failed);
    }

    [Fact]
    public void GetValue_Should_ReturnMappedSecretAndConvertType()
    {
        var provider = CreateProvider(
            new FakeSecretManagerClient(("projects/project/secrets/port/versions/5", "443")),
            options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Port", "port", version: "5");
            });

        var value = provider.GetValue<int>("Production", "Port");

        Assert.Equal(443, value);
        Assert.False(provider.TryGetTerminalDiagnostic("Production", "Port", out _));
    }

    [Fact]
    public void DefaultConfigManager_Should_ResolveEnvironmentBeforeGoogleSecretManager()
    {
        var google = CreateProvider(
            new FakeSecretManagerClient(("projects/project/secrets/api-key/versions/5", "from-gcp")),
            options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
            });
        var manager = new DefaultConfigManager(
            new StaticEnvironmentProvider("from-env"),
            [google, new StaticProvider(priority: 1, value: "from-file")],
            NullLogger<DefaultConfigManager>.Instance);

        var value = manager.GetValue<string>("Production", "Stripe:ApiKey");

        Assert.Equal("from-env", value);
    }

    [Fact]
    public void DefaultConfigManager_Should_Not_QueryLowerProviderWhenMappedSecretIsDenied()
    {
        var google = CreateProvider(
            new ThrowingSecretManagerClient(new RpcException(new Status(StatusCode.PermissionDenied, "raw-secret should not leak"))),
            options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
            });
        var fileProvider = new StaticProvider(priority: 1, value: "from-file");
        var manager = new DefaultConfigManager(
            new StaticEnvironmentProvider(null),
            [fileProvider, google],
            NullLogger<DefaultConfigManager>.Instance);

        var exception = Assert.Throws<ConfigurationResolutionException>(() =>
            manager.GetValue<string>("Production", "Stripe:ApiKey"));

        Assert.Equal("google-secret-manager-access-denied", exception.Diagnostic.Code);
        Assert.DoesNotContain("raw-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.False(fileProvider.WasCalled);
    }

    [Fact]
    public void TryGetTerminalDiagnostic_Should_ReturnFalseWhenFailClosedIsDisabled()
    {
        var provider = CreateProvider(
            new ThrowingSecretManagerClient(new RpcException(new Status(StatusCode.Unavailable, "raw-secret should not leak"))),
            options =>
            {
                options.ProjectId = "project";
                options.FailClosedOnProviderFailure = false;
                options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
            });

        var resolution = provider.ResolveValue<string>("Production", "Stripe:ApiKey");

        Assert.Equal(GoogleSecretManagerResultStatus.Unavailable, resolution.Status);
        Assert.False(provider.TryGetTerminalDiagnostic("Production", "Stripe:ApiKey", out _));
    }

    [Fact]
    public void ResolveValue_Should_StopWhenPayloadIsInvalidUtf8()
    {
        var provider = CreateProvider(
            new FakeSecretManagerClient(("projects/project/secrets/api-key/versions/5", [0xff, 0xfe])),
            options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
            });

        var resolution = provider.ResolveValue<string>("Production", "Stripe:ApiKey");

        Assert.Equal(GoogleSecretManagerResultStatus.InvalidPayload, resolution.Status);
        Assert.True(provider.TryGetTerminalDiagnostic("Production", "Stripe:ApiKey", out var diagnostic));
        Assert.Equal("google-secret-manager-invalid-secret-payload", diagnostic.Code);
    }

    [Fact]
    public void ResolveValue_Should_StopWhenConversionFailsWithoutLeakingPayload()
    {
        var provider = CreateProvider(
            new FakeSecretManagerClient(("projects/project/secrets/port/versions/5", "not-the-port-secret")),
            options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Port", "port", version: "5");
            });

        var resolution = provider.ResolveValue<int>("Production", "Port");

        Assert.Equal(GoogleSecretManagerResultStatus.ConversionFailed, resolution.Status);
        Assert.True(provider.TryGetTerminalDiagnostic("Production", "Port", out var diagnostic));
        Assert.Equal("google-secret-manager-conversion-failed", diagnostic.Code);
        Assert.DoesNotContain("not-the-port-secret", diagnostic.ToDisplayString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultConfigManager_Should_Not_QueryLowerProviderWhenMappedSecretConvertsToNull()
    {
        var google = CreateProvider(
            new FakeSecretManagerClient(("projects/project/secrets/payload/versions/5", "null")),
            options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Payload", "payload", version: "5");
            });
        var fileProvider = new StaticProvider(priority: 1, value: new SecretPayload("from-file", 1));
        var manager = new DefaultConfigManager(
            new StaticEnvironmentProvider(null),
            [fileProvider, google],
            NullLogger<DefaultConfigManager>.Instance);

        var exception = Assert.Throws<ConfigurationResolutionException>(() =>
            manager.GetValue<SecretPayload>("Production", "Payload"));

        Assert.Equal("google-secret-manager-conversion-failed", exception.Diagnostic.Code);
        Assert.False(fileProvider.WasCalled);
    }

    [Fact]
    public void DefaultConfigManager_Should_ReportLazyClientCreationFailuresAsTerminalDiagnostics()
    {
        var google = CreateProvider(
            new GoogleSecretManagerClientAdapter(() => throw new InvalidOperationException("raw-secret should not leak")),
            options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
            });
        var fileProvider = new StaticProvider(priority: 1, value: "from-file");
        var manager = new DefaultConfigManager(
            new StaticEnvironmentProvider(null),
            [fileProvider, google],
            NullLogger<DefaultConfigManager>.Instance);

        var exception = Assert.Throws<ConfigurationResolutionException>(() =>
            manager.GetValue<string>("Production", "Stripe:ApiKey"));

        Assert.Equal("google-secret-manager-unavailable", exception.Diagnostic.Code);
        Assert.DoesNotContain("raw-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.False(fileProvider.WasCalled);
    }

    [Fact]
    public void ResolveValue_Should_LeaveUnmappedKeysUnclaimed()
    {
        var provider = CreateProvider(
            new FakeSecretManagerClient(("projects/project/secrets/api-key/versions/5", "from-gcp")),
            options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
            });

        var resolution = provider.ResolveValue<string>("Production", "Other:Key");

        Assert.Equal(GoogleSecretManagerResultStatus.Unclaimed, resolution.Status);
    }

    [Fact]
    public void ResolveValue_Should_ClaimOnlyScopedConventionKeys()
    {
        var provider = CreateProvider(
            new FakeSecretManagerClient(("projects/project/secrets/stripe-apikey/versions/5", "from-gcp")),
            options =>
            {
                options.ProjectId = "project";
                options.EnableConventionResolver("Billing:", secretIdPrefix: "", version: "5");
            });

        var claimed = provider.ResolveValue<string>("Production", "Billing:Stripe:ApiKey");
        var unclaimed = provider.ResolveValue<string>("Production", "Other:Stripe:ApiKey");

        Assert.Equal("from-gcp", claimed.Value);
        Assert.Equal(GoogleSecretManagerResultStatus.Unclaimed, unclaimed.Status);
    }

    [Fact]
    public void OptionsValidator_Should_RequireProjectIdForConventionSecretIds()
    {
        var options = new AppSurfaceGoogleSecretManagerOptions();
        options.EnableConventionResolver("Billing:", secretIdPrefix: "billing-", version: "5");

        var result = new AppSurfaceGoogleSecretManagerOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("requires ProjectId", StringComparison.Ordinal));
    }

    [Fact]
    public void OptionsValidator_Should_RejectOverlappingConventionPrefixes()
    {
        var options = new AppSurfaceGoogleSecretManagerOptions
        {
            ProjectId = "project"
        };
        options.EnableConventionResolver("Billing:", version: "5");
        options.EnableConventionResolver("Billing:Stripe:", version: "5");

        var result = new AppSurfaceGoogleSecretManagerOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("overlap", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveForAudit_Should_ReturnProviderSourceWithoutPayloadInDiagnostics()
    {
        var provider = CreateProvider(
            new FakeSecretManagerClient(("projects/project/secrets/api-key/versions/5", "sk_live_secret")),
            options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
            });

        var resolution = provider.ResolveForAudit("Production", "Stripe:ApiKey", typeof(string), ConfigAuditSourceRole.Base);

        Assert.Equal(ConfigAuditEntryState.Resolved, resolution.State);
        Assert.Equal("sk_live_secret", resolution.Value);
        var source = Assert.Single(resolution.Sources);
        Assert.Equal(ConfigAuditSourceKind.Provider, source.Kind);
        Assert.Equal(nameof(GoogleSecretManagerConfigProvider), source.ProviderName);
        Assert.Equal(ConfigAuditSensitivity.Sensitive, source.Sensitivity);
    }

    private static GoogleSecretManagerConfigProvider CreateProvider(
        IAppSurfaceGoogleSecretManagerClient client,
        Action<AppSurfaceGoogleSecretManagerOptions> configure)
    {
        var options = new AppSurfaceGoogleSecretManagerOptions();
        configure(options);
        return new GoogleSecretManagerConfigProvider(Options.Create(options), client);
    }

    private sealed class FakeSecretManagerClient : IAppSurfaceGoogleSecretManagerClient
    {
        private readonly Dictionary<string, byte[]> _payloads = new(StringComparer.Ordinal);

        public FakeSecretManagerClient(params (string Resource, string Payload)[] payloads)
        {
            foreach (var (resource, payload) in payloads)
            {
                _payloads[resource] = Encoding.UTF8.GetBytes(payload);
            }
        }

        public FakeSecretManagerClient(params (string Resource, byte[] Payload)[] payloads)
        {
            foreach (var (resource, payload) in payloads)
            {
                _payloads[resource] = payload;
            }
        }

        public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout) =>
            _payloads.TryGetValue(resourceName, out var payload)
                ? new AppSurfaceGoogleSecretPayload(payload, resourceName)
                : throw new RpcException(new Status(StatusCode.NotFound, "missing raw-secret"));
    }

    private sealed class ThrowingSecretManagerClient(Exception exception) : IAppSurfaceGoogleSecretManagerClient
    {
        public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout) => throw exception;
    }

    private sealed record SecretPayload(string Name, int Retries);

    private sealed class StaticEnvironmentProvider(string? value) : IEnvironmentConfigProvider
    {
        public int Priority => -1;

        public string Name => nameof(StaticEnvironmentProvider);

        public string Environment => "Production";

        public bool IsDevelopment => false;

        public T? GetValue<T>(string environment, string key) => value is T typed ? typed : default;

        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
    }

    private sealed class StaticProvider(int priority, object value) : IConfigProvider
    {
        public int Priority { get; } = priority;

        public string Name => nameof(StaticProvider);

        public bool WasCalled { get; private set; }

        public T? GetValue<T>(string environment, string key)
        {
            WasCalled = true;
            return value is T typed ? typed : default;
        }
    }
}
