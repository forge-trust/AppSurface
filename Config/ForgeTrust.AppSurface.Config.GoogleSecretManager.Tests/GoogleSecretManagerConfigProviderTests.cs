using System.Text;
using ForgeTrust.AppSurface.Config;
using Google.Api.Gax.Grpc;
using Google.Cloud.SecretManager.V1;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
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
    public void Resolve_Should_ReturnMappedSecretAndConvertType()
    {
        var provider = CreateProvider(
            new FakeSecretManagerClient(("projects/project/secrets/port/versions/5", "443")),
            options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Port", "port", version: "5");
            });

        var resolution = provider.Resolve<int>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Port")));

        Assert.Equal(ConfigProviderValueStatus.Found, resolution.Status);
        Assert.Equal(443, resolution.Value);
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

        Assert.Equal("config-provider-failed", exception.Diagnostic.Code);
        ValueSafeAssert.DoesNotExpose("raw-secret", exception.ToString());
        Assert.False(fileProvider.WasCalled);
    }

    [Fact]
    public void Resolve_Should_ReturnTerminalWhenClaimedSecretIsUnavailable()
    {
        var provider = CreateProvider(
            new ThrowingSecretManagerClient(new RpcException(new Status(StatusCode.Unavailable, "raw-secret should not leak"))),
            options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
            });

        var resolution = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Stripe:ApiKey")));

        Assert.Equal(ConfigProviderValueStatus.Terminal, resolution.Status);
        Assert.Equal("config-provider-failed", resolution.Diagnostic!.Code);
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

        var resolution = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Stripe:ApiKey")));

        Assert.Equal(ConfigProviderValueStatus.Terminal, resolution.Status);
        Assert.Equal("config-provider-failed", resolution.Diagnostic!.Code);
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

        var resolution = provider.Resolve<int>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Port")));

        Assert.Equal(ConfigProviderValueStatus.Terminal, resolution.Status);
        Assert.Equal("config-provider-failed", resolution.Diagnostic!.Code);
        ValueSafeAssert.DoesNotExpose("not-the-port-secret", resolution.Diagnostic.ToDisplayString());
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

        Assert.Equal("config-provider-failed", exception.Diagnostic.Code);
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

        Assert.Equal("config-provider-failed", exception.Diagnostic.Code);
        ValueSafeAssert.DoesNotExpose("raw-secret", exception.ToString());
        Assert.False(fileProvider.WasCalled);
    }

    [Fact]
    public void Resolve_Should_LeaveUnmappedKeysMissing()
    {
        var provider = CreateProvider(
            new FakeSecretManagerClient(("projects/project/secrets/api-key/versions/5", "from-gcp")),
            options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
            });
        var resolution = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Other:Key")));

        Assert.Equal(ConfigProviderValueStatus.Missing, resolution.Status);
    }

    [Fact]
    public void Resolve_Should_ClaimOnlyScopedConventionKeys()
    {
        var provider = CreateProvider(
            new FakeSecretManagerClient(("projects/project/secrets/billing-billing--stripe--apikey/versions/5", "from-gcp")),
            options =>
            {
                options.ProjectId = "project";
                options.EnableConventionResolver("Billing", secretIdPrefix: "billing-", version: "5");
            });

        var claimed = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Billing:Stripe:ApiKey")));
        var unclaimed = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Other:Stripe:ApiKey")));

        Assert.Equal(ConfigProviderValueStatus.Found, claimed.Status);
        Assert.Equal("from-gcp", claimed.Value);
        Assert.Equal(ConfigProviderValueStatus.Missing, unclaimed.Status);
    }

    [Fact]
    public void OptionsValidator_Should_RequireProjectIdForConventionSecretIds()
    {
        var options = new AppSurfaceGoogleSecretManagerOptions();
        options.EnableConventionResolver("Billing", secretIdPrefix: "billing-", version: "5");

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
        options.EnableConventionResolver("Billing", secretIdPrefix: "shared-", version: "5");
        options.EnableConventionResolver("Billing:Stripe", secretIdPrefix: "shared-", version: "5");

        var result = new AppSurfaceGoogleSecretManagerOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("overlap", StringComparison.Ordinal));
    }

    [Fact]
    public void OptionsValidator_Should_ReportNullConventionPrefixWithoutThrowing()
    {
        var options = new AppSurfaceGoogleSecretManagerOptions
        {
            ProjectId = "project",
            DefaultVersion = "5"
        };
        options.EnableConventionResolver((string)null!, secretIdPrefix: "", version: "5");
        options.EnableConventionResolver("Billing", secretIdPrefix: "billing-", version: "5");

        var result = new AppSurfaceGoogleSecretManagerOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("valid logical key", StringComparison.Ordinal));
    }

    [Fact]
    public void OptionsValidator_Should_ReportAllInvalidOptionBranches()
    {
        var options = new AppSurfaceGoogleSecretManagerOptions
        {
            LookupTimeout = TimeSpan.Zero,
            CacheTtl = TimeSpan.Zero,
            CacheCapacity = 0,
            MaxAdHocClaims = 0
        };
        options.MapSecret("Stripe:ApiKey", "stripe-api-key", version: "5");
        options.MapSecret("Stripe:ApiKey", "stripe-api-key-duplicate", version: "5");
        options.MapSecret("", "", version: "5");
        options.MapSecret("Full:WithVersion", "projects/prod/secrets/full/versions/5", version: "6");
        options.EnableConventionResolver((string)"", secretIdPrefix: "", version: null);
        options.EnableConventionResolver("Duplicate", secretIdPrefix: "duplicate-", version: "5");
        options.EnableConventionResolver("Duplicate", secretIdPrefix: "duplicate-", version: "5");

        var result = new AppSurfaceGoogleSecretManagerOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("LookupTimeout", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("CacheTtl", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("CacheCapacity", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("MaxAdHocClaims", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("mapped more than once", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("valid logical key", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("secret id or resource name", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("requires ProjectId", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("must not also specify Version", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("valid logical key", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("configured more than once", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("must specify a secret version", StringComparison.Ordinal));
    }

    [Fact]
    public void Constructor_Should_ThrowOptionsValidationExceptionForInvalidOptions()
    {
        var options = Options.Create(new AppSurfaceGoogleSecretManagerOptions
        {
            LookupTimeout = TimeSpan.Zero
        });

        var exception = Assert.Throws<OptionsValidationException>(() =>
            new GoogleSecretManagerConfigProvider(options, new ThrowingSecretManagerClient(new InvalidOperationException())));

        Assert.Contains(exception.Failures, failure => failure.Contains("LookupTimeout", StringComparison.Ordinal));
    }

    [Fact]
    public void AdapterConstructors_Should_BeLazyAndValidateExplicitClient()
    {
        var adapter = new GoogleSecretManagerClientAdapter();
        var explicitClientAdapter = new GoogleSecretManagerClientAdapter(
            new PayloadSecretManagerServiceClient("explicit-client"));
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new GoogleSecretManagerClientAdapter((SecretManagerServiceClient)null!));
        var payload = explicitClientAdapter.AccessSecretVersion(
            "projects/project/secrets/api-key/versions/5",
            TimeSpan.FromSeconds(1));

        Assert.NotNull(adapter);
        Assert.Equal("explicit-client", Encoding.UTF8.GetString(payload.Data));
        Assert.Equal("client", exception.ParamName);
    }

    [Fact]
    public void Adapter_Should_RejectSecretManagerResponsesWithoutPayload()
    {
        var adapter = new GoogleSecretManagerClientAdapter(() => new MissingPayloadSecretManagerServiceClient());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            adapter.AccessSecretVersion("projects/project/secrets/api-key/versions/5", TimeSpan.FromSeconds(1)));

        Assert.Contains("without a payload", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Adapter_Should_ReturnSecretManagerPayload()
    {
        var adapter = new GoogleSecretManagerClientAdapter(() => new PayloadSecretManagerServiceClient("from-gcp"));

        var payload = adapter.AccessSecretVersion("projects/project/secrets/api-key/versions/5", TimeSpan.FromSeconds(1));

        Assert.Equal("from-gcp", Encoding.UTF8.GetString(payload.Data));
        Assert.Equal("projects/project/secrets/api-key/versions/5", payload.ResolvedResourceName);
    }

    [Theory]
    [InlineData(StatusCode.NotFound)]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.InvalidArgument)]
    [InlineData(StatusCode.Cancelled)]
    [InlineData(StatusCode.DeadlineExceeded)]
    [InlineData(StatusCode.Unknown)]
    public void ResolveValue_Should_MapRpcStatusToDisplaySafeDiagnostics(StatusCode statusCode)
    {
        var provider = CreateProvider(
            new ThrowingSecretManagerClient(new RpcException(new Status(statusCode, "raw-secret should not leak"))),
            options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
            });

        var resolution = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Stripe:ApiKey")));

        Assert.Equal(ConfigProviderValueStatus.Terminal, resolution.Status);
        Assert.Equal("config-provider-failed", resolution.Diagnostic?.Code);
        ValueSafeAssert.DoesNotExpose("raw-secret", resolution.Diagnostic?.ToDisplayString());
    }

    [Fact]
    public void ResolveValue_Should_MapTimeoutToRetryableUnavailableDiagnostic()
    {
        var provider = CreateProvider(
            new ThrowingSecretManagerClient(new TimeoutException("raw-secret should not leak")),
            options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
            });

        var resolution = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Stripe:ApiKey")));

        Assert.Equal(ConfigProviderValueStatus.Terminal, resolution.Status);
        Assert.Equal("config-provider-failed", resolution.Diagnostic?.Code);
        ValueSafeAssert.DoesNotExpose("raw-secret", resolution.Diagnostic?.ToDisplayString());
    }

    [Fact]
    public void ResolveValue_Should_NotConvertCriticalProviderExceptions()
    {
        var provider = CreateProvider(
            new ThrowingSecretManagerClient(new AccessViolationException("critical provider failure")),
            options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
            });

#pragma warning disable CS0618
        Assert.Throws<AccessViolationException>(() => provider.ResolveValue<string>("Production", "Stripe:ApiKey"));
#pragma warning restore CS0618
    }

    [Fact]
    public void ObsoleteHelpers_Should_PreserveStrictFoundMissingAndTerminalResults()
    {
        var provider = CreateProvider(
            new FakeSecretManagerClient(("projects/project/secrets/api-key/versions/5", "from-gcp")),
            options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
            });
        var failingProvider = CreateProvider(
            new ThrowingSecretManagerClient(new RpcException(new Status(StatusCode.Unavailable, "raw-secret"))),
            options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
            });

#pragma warning disable CS0618
        Assert.Equal("from-gcp", provider.GetValue<string>("Production", "Stripe:ApiKey"));
        Assert.Null(provider.GetValue<string>("Production", "Other:Key"));
        Assert.Equal("from-gcp", provider.ResolveValue<string>("Production", "Stripe:ApiKey").Value);
        Assert.Equal(GoogleSecretManagerResultStatus.Unclaimed, provider.ResolveValue<string>("Production", "Other:Key").Status);
        Assert.Throws<ConfigurationResolutionException>(() => failingProvider.GetValue<string>("Production", "Stripe:ApiKey"));
#pragma warning restore CS0618
    }

    [Fact]
    public void ResolveForAudit_Should_NotWrapCriticalProviderExceptions()
    {
        var provider = CreateProvider(
            new ThrowingSecretManagerClient(new AccessViolationException("critical provider failure")),
            options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
            });

        Assert.Throws<AccessViolationException>(() =>
            provider.ResolveForAudit(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Stripe:ApiKey")), typeof(string), ConfigAuditSourceRole.Base));
    }

    [Fact]
    public void Resolve_Should_CacheSuccessfulPayloadWithinConfiguredTtl()
    {
        var client = new CountingSecretManagerClient("projects/project/secrets/api-key/versions/5", "from-gcp");
        var provider = CreateProvider(
            client,
            options =>
            {
                options.ProjectId = "project";
                options.CacheTtl = TimeSpan.FromMinutes(5);
                options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
            });

        var request = new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Stripe:ApiKey"));
        var first = provider.Resolve<string>(request);
        var second = provider.Resolve<string>(request);

        Assert.Equal(ConfigProviderValueStatus.Found, first.Status);
        Assert.Equal(ConfigProviderValueStatus.Found, second.Status);
        Assert.Equal("from-gcp", first.Value);
        Assert.Equal("from-gcp", second.Value);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public void Resolve_Should_RejectPayloadResolvedToADifferentResource()
    {
        var provider = CreateProvider(
            new MismatchedPayloadSecretManagerClient("projects/project/secrets/other/versions/5"),
            options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
            });

        var result = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Stripe:ApiKey")));

        Assert.Equal(ConfigProviderValueStatus.Terminal, result.Status);
        Assert.Equal("config-provider-failed", result.Diagnostic!.Code);
    }

    [Fact]
    public void Resolve_Should_PropagateCallerCancellation()
    {
        var provider = CreateProvider(
            new FakeSecretManagerClient(("projects/project/secrets/api-key/versions/5", "from-gcp")),
            options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
            });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var scope = new ConfigResolutionScope(cancellation.Token);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Stripe:ApiKey"), scope)));
    }

    [Fact]
    public async Task Resolve_Should_SingleflightConcurrentRequestsWhenCacheIsDisabled()
    {
        var client = new BlockingCountingSecretManagerClient("from-gcp");
        var provider = CreateProvider(client, options =>
        {
            options.ProjectId = "project";
            options.CacheTtl = null;
            options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
        });

        var pending = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() =>
            {
                return provider.Resolve<string>(new ConfigProviderRequest(
                    "Production", AppSurfaceConfigKey.Parse("Stripe:ApiKey")));
            }))
            .ToArray();
        await client.Started.Task;
        await Task.Delay(25);
        client.Release.Set();
        var results = await Task.WhenAll(pending);

        Assert.All(results, result =>
        {
            Assert.Equal(ConfigProviderValueStatus.Found, result.Status);
            Assert.Equal("from-gcp", result.Value);
        });
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public void Resolve_Should_EvictFailedSingleflightForRetry()
    {
        var client = new FailOnceSecretManagerClient("from-gcp");
        var provider = CreateProvider(client, options =>
        {
            options.ProjectId = "project";
            options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
        });
        var request = new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Stripe:ApiKey"));

        var first = provider.Resolve<string>(request);
        var second = provider.Resolve<string>(request);

        Assert.Equal(ConfigProviderValueStatus.Terminal, first.Status);
        Assert.Equal(ConfigProviderValueStatus.Found, second.Status);
        Assert.Equal("from-gcp", second.Value);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public void Resolve_Should_EvictExpiredPayloadsFromCache()
    {
        var client = new RollingSecretManagerClient("projects/project/secrets/api-key/versions/5", "first", "second");
        var provider = CreateProvider(
            client,
            options =>
            {
                options.ProjectId = "project";
                options.CacheTtl = TimeSpan.FromTicks(1);
                options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
            });

        var request = new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Stripe:ApiKey"));
        var first = provider.Resolve<string>(request);
        var second = provider.Resolve<string>(request);

        Assert.Equal(ConfigProviderValueStatus.Found, first.Status);
        Assert.Equal(ConfigProviderValueStatus.Found, second.Status);
        Assert.Equal("first", first.Value);
        Assert.Equal("second", second.Value);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public void Resolve_Should_EnforceAdHocClaimCapacityBeforeNetworkAccess()
    {
        var client = new CountingConventionSecretManagerClient(
            ("projects/project/secrets/shared-payments--one/versions/5", "one"));
        var provider = CreateProvider(client, options =>
        {
            options.ProjectId = "project";
            options.MaxAdHocClaims = 1;
            options.EnableConventionResolver("Payments", secretIdPrefix: "shared-", version: "5");
        });

        var first = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Payments:One")));
        var second = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Payments:Two")));

        Assert.Equal(ConfigProviderValueStatus.Found, first.Status);
        Assert.Equal("one", first.Value);
        Assert.Equal(ConfigProviderValueStatus.Terminal, second.Status);
        Assert.Equal("config-provider-failed", second.Diagnostic!.Code);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public void Constructor_Should_SeedClaimsFromFinalizedDeclarationRegistry()
    {
        var parser = new ConfigKeyInputParser(Options.Create(new AppSurfaceConfigKeyOptions()));
        var registry = new ConfigDeclarationRegistry(
            [
                new ConfigAuditKnownEntry(AppSurfaceConfigKey.Parse("Payments:Declared"), null, typeof(string)),
                new ConfigAuditKnownEntry(AppSurfaceConfigKey.Parse("Unmapped:Declared"), null, typeof(string)),
                new ConfigAuditKnownEntry(AppSurfaceConfigKey.Parse("Stripe:ApiKey"), null, typeof(string))
            ],
            [],
            parser);
        using var services = new ServiceCollection().AddSingleton(registry).BuildServiceProvider();
        var providerOptions = new AppSurfaceGoogleSecretManagerOptions
        {
            ProjectId = "project",
            MaxAdHocClaims = 1
        };
        providerOptions.EnableConventionResolver("Payments", "shared-", "5");
        providerOptions.MapSecret("Stripe:ApiKey", "api-key", "5");
        var provider = new GoogleSecretManagerConfigProvider(
            Options.Create(providerOptions),
            new CountingConventionSecretManagerClient(
                ("projects/project/secrets/shared-payments--declared/versions/5", "declared"),
                ("projects/project/secrets/api-key/versions/5", "explicit")),
            services);

        var declared = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Payments:Declared")));
        var adhoc = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Payments:Adhoc")));
        var explicitMapping = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Stripe:ApiKey")));

        Assert.Equal(ConfigProviderValueStatus.Found, declared.Status);
        Assert.Equal(ConfigProviderValueStatus.Terminal, adhoc.Status);
        Assert.Equal("config-provider-failed", adhoc.Diagnostic!.Code);
        Assert.Equal(ConfigProviderValueStatus.Found, explicitMapping.Status);
    }

    [Fact]
    public void Constructor_Should_RejectDistinctExplicitKeysThatShareAnExactResource()
    {
        var options = new AppSurfaceGoogleSecretManagerOptions { ProjectId = "project" };
        options.MapSecret("Payments:One", "shared-resource", version: "5");
        options.MapSecret("Payments:Two", "shared-resource", version: "5");

        var exception = Assert.Throws<OptionsValidationException>(() =>
            new GoogleSecretManagerConfigProvider(
                Options.Create(options),
                new FakeSecretManagerClient(("projects/project/secrets/shared-resource/versions/5", "one"))));

        Assert.Contains("config-key-collision", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_Should_EnforceCacheCapacityByExactResource()
    {
        var client = new CountingConventionSecretManagerClient(
            ("projects/project/secrets/shared-payments--one/versions/5", "one"),
            ("projects/project/secrets/shared-payments--two/versions/5", "two"));
        var provider = CreateProvider(client, options =>
        {
            options.ProjectId = "project";
            options.CacheTtl = TimeSpan.FromMinutes(5);
            options.CacheCapacity = 1;
            options.MapSecret("Payments:One", "shared-payments--one", version: "5");
            options.MapSecret("Payments:Two", "shared-payments--two", version: "5");
        });

        var one = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Payments:One")));
        var two = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Payments:Two")));
        var oneAgain = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Payments:One")));

        Assert.Equal(ConfigProviderValueStatus.Found, one.Status);
        Assert.Equal(ConfigProviderValueStatus.Found, two.Status);
        Assert.Equal(ConfigProviderValueStatus.Found, oneAgain.Status);
        Assert.Equal("one", oneAgain.Value);
        Assert.Equal(3, client.Calls);
    }

    [Fact]
    public async Task Resolve_Should_ReturnAuditDeadlineAndReleaseSharedFetch()
    {
        var client = new BlockingCountingSecretManagerClient("from-gcp");
        var provider = CreateProvider(client, options =>
        {
            options.ProjectId = "project";
            options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
        });
        using var scope = new ConfigResolutionScope(
            auditOptions: new ConfigResourceOptions
            {
                AuditTimeout = TimeSpan.FromMilliseconds(100),
                MaxAuditRemoteLookups = 1,
                MaxAuditConcurrency = 1
            });
        var request = new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Stripe:ApiKey"), scope);

        try
        {
            var pending = Task.Run(() => provider.Resolve<string>(request));
            await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await pending;

            Assert.Equal(ConfigProviderValueStatus.Terminal, result.Status);
            Assert.Equal("config-audit-deadline", result.Diagnostic!.Code);
            Assert.Equal("config-audit-deadline", scope.IncompleteAuditDiagnostic!.Code);
            Assert.Equal(1, client.Calls);
        }
        finally
        {
            client.Release.Set();
        }
    }

    [Fact]
    public void Resolve_Should_EnforceAuditRemoteLookupLimitBeforeNetworkAccess()
    {
        var client = new CountingSecretManagerClient("projects/project/secrets/api-key/versions/5", "from-gcp");
        var provider = CreateProvider(client, options =>
        {
            options.ProjectId = "project";
            options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
            options.MapSecret("Stripe:Other", "other", version: "5");
        });
        using var scope = new ConfigResolutionScope(
            auditOptions: new ConfigResourceOptions
            {
                AuditTimeout = TimeSpan.FromSeconds(5),
                MaxAuditRemoteLookups = 1,
                MaxAuditConcurrency = 1
            });

        var first = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Stripe:ApiKey"), scope));
        var second = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Stripe:Other"), scope));

        Assert.Equal(ConfigProviderValueStatus.Found, first.Status);
        Assert.Equal(ConfigProviderValueStatus.Terminal, second.Status);
        Assert.Equal("config-audit-remote-lookup-limit", second.Diagnostic!.Code);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public void PublicEntryPoints_Should_RejectNullArguments()
    {
        var provider = CreateProvider(
            new FakeSecretManagerClient(("projects/project/secrets/api-key/versions/5", "from-gcp")),
            options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
            });

        Assert.Throws<ArgumentNullException>(() => provider.Resolve<string>(null!));
        Assert.Throws<ArgumentNullException>(() => new ConfigProviderRequest(null!, AppSurfaceConfigKey.Parse("Stripe:ApiKey")));
        Assert.Throws<FormatException>(() => AppSurfaceConfigKey.Parse(null!));
        var environmentException = Assert.Throws<ArgumentNullException>(() =>
            provider.ResolveForAudit(new ConfigProviderRequest(null!, AppSurfaceConfigKey.Parse("Stripe:ApiKey")), typeof(string), ConfigAuditSourceRole.Base));
        Assert.Equal("environment", environmentException.ParamName);
        Assert.Equal("valueType", Assert.Throws<ArgumentNullException>(() =>
            provider.ResolveForAudit(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Stripe:ApiKey")), null!, ConfigAuditSourceRole.Base)).ParamName);
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

        var resolution = provider.ResolveForAudit(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Stripe:ApiKey")), typeof(string), ConfigAuditSourceRole.Base);

        Assert.Equal(ConfigAuditEntryState.Resolved, resolution.State);
        Assert.Equal("sk_live_secret", resolution.Value);
        var source = Assert.Single(resolution.Sources);
        Assert.Equal(ConfigAuditSourceKind.Provider, source.Kind);
        Assert.Equal(nameof(GoogleSecretManagerConfigProvider), source.ProviderName);
        Assert.Equal(ConfigAuditSensitivity.Sensitive, source.Sensitivity);
    }

    [Fact]
    public void ResolveForAudit_Should_ReturnMissingWhenKeyIsUnclaimed()
    {
        var provider = CreateProvider(
            new FakeSecretManagerClient(("projects/project/secrets/api-key/versions/5", "from-gcp")),
            options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
            });

        var resolution = provider.ResolveForAudit(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Other:Key")), typeof(string), ConfigAuditSourceRole.Base);

        Assert.Equal(ConfigAuditEntryState.Missing, resolution.State);
        Assert.Empty(resolution.Sources);
        Assert.Empty(resolution.Diagnostics);
        Assert.Empty(provider.GetReportDiagnostics("Production"));
    }

    [Fact]
    public void ResolveForAudit_Should_ReturnInvalidDiagnosticWhenAdHocClaimCapacityIsExceeded()
    {
        var provider = CreateProvider(
            new FakeSecretManagerClient(("projects/project/secrets/shared-payments--one/versions/5", "one")),
            options =>
            {
                options.ProjectId = "project";
                options.MaxAdHocClaims = 1;
                options.EnableConventionResolver("Payments", secretIdPrefix: "shared-", version: "5");
            });

        var first = provider.ResolveForAudit(
            new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Payments:One")),
            typeof(string),
            ConfigAuditSourceRole.Base);
        var second = provider.ResolveForAudit(
            new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Payments:Two")),
            typeof(string),
            ConfigAuditSourceRole.Base);

        Assert.Equal(ConfigAuditEntryState.Resolved, first.State);
        Assert.Equal(ConfigAuditEntryState.Invalid, second.State);
        Assert.Equal("config-provider-failed", Assert.Single(second.Diagnostics).Code);
    }

    [Fact]
    public void Resolve_Should_ReturnCollisionAfterAResourceIsPoisoned()
    {
        var provider = CreateProvider(
            new FakeSecretManagerClient(("projects/project/secrets/shared-payments--one/versions/5", "one")),
            options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Stripe:ApiKey", "shared-payments--one", version: "5");
                options.EnableConventionResolver("Payments", secretIdPrefix: "shared-", version: "5");
            });

        var first = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Payments:One")));
        var second = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Payments:One")));
        var explicitMapping = provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Stripe:ApiKey")));

        Assert.Equal(ConfigProviderValueStatus.Terminal, first.Status);
        Assert.Equal("config-key-collision", first.Diagnostic!.Code);
        Assert.Equal(ConfigProviderValueStatus.Terminal, second.Status);
        Assert.Equal("config-key-collision", second.Diagnostic!.Code);
        Assert.Equal(ConfigProviderValueStatus.Terminal, explicitMapping.Status);
        Assert.Equal("config-key-collision", explicitMapping.Diagnostic!.Code);
    }

    [Fact]
    public void SecretReference_Should_PreserveFullResourceNameWithoutRequestedVersion()
    {
        var options = new AppSurfaceGoogleSecretManagerOptions();
        var mapping = new AppSurfaceGoogleSecretMapping(
            "Stripe:ApiKey",
            "projects/prod/secrets/stripe-api-key/versions/5",
            null);

        var reference = GoogleSecretManagerSecretReference.FromMapping(options, mapping);

        Assert.Equal("Stripe:ApiKey", reference.LogicalKey);
        Assert.Equal("projects/prod/secrets/stripe-api-key/versions/5", reference.ResourceName);
        Assert.Null(reference.RequestedVersion);
    }

    [Fact]
    public void ResolveForAudit_Should_ReturnInvalidDiagnosticForClaimedFailure()
    {
        var provider = CreateProvider(
            new ThrowingSecretManagerClient(new RpcException(new Status(StatusCode.NotFound, "raw-secret should not leak"))),
            options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Stripe:ApiKey", "api-key", version: "5");
            });

        var resolution = provider.ResolveForAudit(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Stripe:ApiKey")), typeof(string), ConfigAuditSourceRole.Base);

        Assert.Equal(ConfigAuditEntryState.Invalid, resolution.State);
        var diagnostic = Assert.Single(resolution.Diagnostics);
        Assert.Equal("config-provider-failed", diagnostic.Code);
        ValueSafeAssert.DoesNotExpose("raw-secret", diagnostic.Message);
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

    private sealed class MismatchedPayloadSecretManagerClient(string resolvedResourceName) : IAppSurfaceGoogleSecretManagerClient
    {
        public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout) =>
            new(Encoding.UTF8.GetBytes("from-wrong-resource"), resolvedResourceName);
    }

    private sealed class BlockingCountingSecretManagerClient(string payload) : IAppSurfaceGoogleSecretManagerClient
    {
        private readonly byte[] _payload = Encoding.UTF8.GetBytes(payload);
        public int Calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);

        public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            Release.Wait();
            return new AppSurfaceGoogleSecretPayload(_payload, resourceName);
        }
    }

    private sealed class FailOnceSecretManagerClient(string payload) : IAppSurfaceGoogleSecretManagerClient
    {
        private readonly byte[] _payload = Encoding.UTF8.GetBytes(payload);
        public int Calls;

        public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout)
        {
            if (Interlocked.Increment(ref Calls) == 1)
            {
                throw new InvalidOperationException("transient fixture failure");
            }

            return new AppSurfaceGoogleSecretPayload(_payload, resourceName);
        }
    }

    private sealed class CountingSecretManagerClient(string resourceName, string payload) : IAppSurfaceGoogleSecretManagerClient
    {
        private readonly byte[] _payload = Encoding.UTF8.GetBytes(payload);

        public int Calls { get; private set; }

        public AppSurfaceGoogleSecretPayload AccessSecretVersion(string requestedResourceName, TimeSpan timeout)
        {
            Calls++;
            Assert.Equal(resourceName, requestedResourceName);
            return new AppSurfaceGoogleSecretPayload(_payload, requestedResourceName);
        }
    }

    private sealed class CountingConventionSecretManagerClient(params (string Resource, string Payload)[] payloads) : IAppSurfaceGoogleSecretManagerClient
    {
        private readonly IReadOnlyDictionary<string, byte[]> _payloads = payloads.ToDictionary(
            item => item.Resource,
            item => Encoding.UTF8.GetBytes(item.Payload),
            StringComparer.Ordinal);

        public int Calls { get; private set; }

        public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout)
        {
            Calls++;
            return _payloads.TryGetValue(resourceName, out var payload)
                ? new AppSurfaceGoogleSecretPayload(payload, resourceName)
                : throw new RpcException(new Status(StatusCode.NotFound, "missing raw-secret"));
        }
    }

    private sealed class RollingSecretManagerClient(string resourceName, params string[] payloads) : IAppSurfaceGoogleSecretManagerClient
    {
        public int Calls { get; private set; }

        public AppSurfaceGoogleSecretPayload AccessSecretVersion(string requestedResourceName, TimeSpan timeout)
        {
            Assert.Equal(resourceName, requestedResourceName);
            var payload = payloads[Math.Min(Calls, payloads.Length - 1)];
            Calls++;
            return new AppSurfaceGoogleSecretPayload(Encoding.UTF8.GetBytes(payload), requestedResourceName);
        }
    }

    private sealed class MissingPayloadSecretManagerServiceClient : SecretManagerServiceClient
    {
        public override AccessSecretVersionResponse AccessSecretVersion(
            AccessSecretVersionRequest request,
            CallSettings? callSettings = null) =>
            new() { Name = request.Name };
    }

    private sealed class PayloadSecretManagerServiceClient(string payload) : SecretManagerServiceClient
    {
        public override AccessSecretVersionResponse AccessSecretVersion(
            AccessSecretVersionRequest request,
            CallSettings? callSettings = null) =>
            new()
            {
                Name = request.Name,
                Payload = new Google.Cloud.SecretManager.V1.SecretPayload
                {
                    Data = ByteString.CopyFromUtf8(payload)
                }
            };
    }

    private sealed record SecretPayload(string Name, int Retries);

    private sealed class StaticEnvironmentProvider(string? value) : IEnvironmentConfigProvider
    {
        public int Priority => -1;

        public string Name => nameof(StaticEnvironmentProvider);

        public string Environment => "Production";

        public bool IsDevelopment => false;

        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request) =>
            value is T typed ? ConfigProviderValueResult<T>.Found(typed) : ConfigProviderValueResult<T>.Missing();

        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;

        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() => new Dictionary<string, string>();
    }

    private sealed class StaticProvider(int priority, object value) : IConfigProvider
    {
        public int Priority { get; } = priority;

        public string Name => nameof(StaticProvider);

        public bool WasCalled { get; private set; }

        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request)
        {
            WasCalled = true;
            return value is T typed ? ConfigProviderValueResult<T>.Found(typed) : ConfigProviderValueResult<T>.Missing();
        }
    }
}
