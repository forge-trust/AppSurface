using System.Text;
using System.Text.Json;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.GoogleSecretManager.Tests;

public sealed class GoogleSecretManagerCompositionRegressionTests
{
    [Theory]
    [InlineData("Billing.Service")]
    [InlineData("billing:service")]
    [InlineData("BILLING:Service")]
    public void InspectClaims_NormalizesOnlyExplicitMappingSegments(string root)
    {
        var client = new TestClient();
        var options = OptionsWithDefaults();
        options.MapSecret("billing", "ancestor", "1");
        options.MapSecret("BILLING.service", "root", "2");
        options.MapSecret("Billing:Service.ApiKey", "secret", "3");
        options.MapSecret("billing.service.PLAIN", "plain", "4");
        options.MapSecret("Billing.Service.Nested.Token", "nested", "5");
        options.MapSecret("Billing.ServiceOther", "sibling", "6");
        options.MapSecret("BillingOther.Service", "other", "7");
        var provider = CreateProvider(options, client);

        var claims = provider.InspectClaims(root, ["Billing:Service:ApiKey"]);

        Assert.Equal(5, claims.Count);
        Assert.Equal("BILLING.service", Assert.Single(claims,
            claim => claim.Kind == ConfigSecretConfiguredClaimKind.RootMapping).LogicalPath);
        Assert.Equal(new[] { "billing", "Billing:Service.ApiKey", "billing.service.PLAIN", "Billing.Service.Nested.Token" },
            claims.Where(claim => claim.Kind == ConfigSecretConfiguredClaimKind.ExactMapping).Select(claim => claim.LogicalPath));
        Assert.All(claims, claim => Assert.Equal(GoogleSecretManagerConfigProvider.ProviderId, claim.ProviderId));
        Assert.Empty(client.Calls);
        // Inspection does not alter the existing ordinal lookup predicate.
        Assert.Equal(ConfigProviderClaim.Unclaimed, provider.InspectClaim("Production", "billing:service"));
        Assert.Equal(ConfigProviderClaim.MayClaim, provider.InspectClaim("Production", "BILLING.service"));
    }

    [Theory]
    [InlineData("Billing.Service", "Billing.", true)]
    [InlineData("Billing:Service", "Billing.", false)]
    [InlineData("billing.Service", "Billing.", false)]
    [InlineData("Billing.Service", "Billing.Service.", false)]
    [InlineData("Billing", "Billing.", false)]
    public void InspectClaims_PreservesOriginalConventionPredicate(string root, string prefix, bool claimsRoot)
    {
        var options = OptionsWithDefaults();
        options.EnableConventionResolver(prefix, "prefix-", "stable");
        var client = new TestClient();
        var provider = CreateProvider(options, client);

        var claims = provider.InspectClaims(root, ["Billing:Service:ApiKey"]);

        Assert.Equal(claimsRoot ? 1 : 0, claims.Count);
        Assert.Equal(claimsRoot ? ConfigProviderClaim.MayClaim : ConfigProviderClaim.Unclaimed,
            provider.InspectClaim("Production", root));
        if (claimsRoot)
        {
            var claim = Assert.Single(claims);
            Assert.Equal(ConfigSecretConfiguredClaimKind.RootConvention, claim.Kind);
            Assert.Equal(root, claim.LogicalPath);
            Assert.Equal("projects/project/secrets/prefix-service/versions/stable", claim.Key);
            Assert.Null(claim.Version); // A full resource must not also carry Version.
        }
        Assert.Empty(client.Calls);
    }

    [Fact]
    public void InspectClaims_DoesNotReportConventionSuppressedByExactLegacyMapping()
    {
        var options = OptionsWithDefaults();
        options.MapSecret("Billing.Service", "mapped", "4");
        options.EnableConventionResolver("Billing.", "convention-", "5");
        var client = new TestClient();
        var provider = CreateProvider(options, client);

        var claim = Assert.Single(provider.InspectClaims("Billing.Service", []));

        Assert.Equal(ConfigSecretConfiguredClaimKind.RootMapping, claim.Kind);
        Assert.Equal("mapped", claim.Key);
        provider.ResolveRaw("Production", "Billing.Service");
        Assert.Equal("projects/project/secrets/mapped/versions/4", Assert.Single(client.Calls).Resource);
    }

    public static IEnumerable<object[]> FailurePolicies()
    {
        foreach (var failure in Enum.GetValues<StatusCode>().Where(code => code != StatusCode.OK).Select(code => code.ToString())
                     .Concat(["timeout", "unexpected", "utf8"]))
        {
            yield return [failure, true];
            yield return [failure, false];
        }
    }

    [Theory]
    [MemberData(nameof(FailurePolicies))]
    public void ResolveRaw_PreservesAllLegacyFailurePoliciesAndDiagnostics(string failure, bool failClosed)
    {
        var options = OptionsWithDefaults();
        options.FailClosedOnProviderFailure = failClosed;
        options.MapSecret("Service", "root");
        var provider = CreateProvider(options, new TestClient((_, _) => Fail(failure)));

        var legacy = provider.ResolveValue<string>("Production", "Service");
        var raw = provider.ResolveRaw("Production", "Service");

        Assert.NotEqual(GoogleSecretManagerResultStatus.Found, legacy.Status);
        Assert.NotNull(legacy.Diagnostic);
        Assert.Equal(failClosed ? ConfigCompositionValueResolutionStatus.TerminalFailure : ConfigCompositionValueResolutionStatus.Missing,
            raw.Status);
        Assert.Equal(failClosed && legacy.Diagnostic.Retryable, raw.Retryable);
        Assert.Equal(failClosed, provider.TryGetTerminalDiagnostic("Production", "Service", out var diagnostic));
        if (failClosed)
            Assert.Equal(legacy.Diagnostic.Code, diagnostic.Code);
        Assert.Null(raw.ReadRaw());
        Assert.True(raw.IsSensitive);
        Assert.Equal(provider.Name, raw.ProviderName);
        Assert.Equal(provider.Priority, raw.Priority);
        ValueSafeAssert.DoesNotExpose("sentinel-secret", raw.ToString());
        ValueSafeAssert.DoesNotExpose("sentinel-secret", JsonSerializer.Serialize(raw));
    }

    [Theory]
    [InlineData("{\"ApiKey\":\"sentinel-secret\"}")]
    [InlineData("null")]
    [InlineData("")]
    public void ResolveRaw_ReturnsUnboundTextAndClearsPreviousDiagnostic(string text)
    {
        var options = OptionsWithDefaults();
        options.MapSecret("Service", "root");
        var client = new TestClient((_, call) => call == 1 ? Fail("NotFound") : Encoding.UTF8.GetBytes(text));
        var provider = CreateProvider(options, client);

        Assert.Equal(ConfigCompositionValueResolutionStatus.TerminalFailure, provider.ResolveRaw("Production", "Service").Status);
        var raw = provider.ResolveRaw("Production", "Service");

        Assert.Equal(ConfigCompositionValueResolutionStatus.Resolved, raw.Status);
        Assert.Equal(text, raw.ReadRaw());
        Assert.False(provider.TryGetTerminalDiagnostic("Production", "Service", out _));
        Assert.Equal(ConfigCompositionValueResolutionStatus.Unclaimed, provider.ResolveRaw("Production", "Other").Status);
        Assert.Equal(2, client.Calls.Count);
    }

    [Fact]
    public void Cache_SeparatesChildFromLegacyAndRawButSharesEquivalentChildResources()
    {
        var options = OptionsWithDefaults();
        options.CacheTtl = TimeSpan.FromMinutes(1);
        options.MapSecret("Service:ApiKey", "api-key", "4");
        var client = new TestClient();
        var time = new ManualTimeProvider();
        var provider = CreateProvider(options, client, time);
        var reference = Reference("api-key", "4");

        Assert.Equal("payload-1", provider.GetValue<string>("Production", reference.LogicalPath));
        Assert.Equal("payload-1", provider.ResolveRaw("Production", reference.LogicalPath).ReadRaw());
        Assert.Equal("payload-2", Resolve(provider, reference, time).ReadSensitiveValue());
        Assert.Equal("payload-2", Resolve(provider, reference with
        {
            LogicalPath = "Other:ApiKey",
            Key = "projects/project/secrets/api-key/versions/4",
            Version = null
        }, time).ReadSensitiveValue());
        Assert.Equal(2, client.Calls.Count);

        Assert.Equal("payload-3", Resolve(provider, reference with { Version = "5" }, time).ReadSensitiveValue());
        Assert.Equal("payload-4", Resolve(provider, reference with { Key = "different-key" }, time).ReadSensitiveValue());
        Assert.Equal("payload-5", Resolve(provider, reference with { Environment = "Staging" }, time).ReadSensitiveValue());
        Assert.Equal("payload-2", Resolve(provider, reference, time).ReadSensitiveValue());
        Assert.Equal(5, client.Calls.Count);
    }

    [Fact]
    public void Cache_ExpiresChildEntriesAndDoesNotReviveExpiredPayloadAfterFailedRefresh()
    {
        var options = OptionsWithDefaults();
        options.CacheTtl = TimeSpan.FromMinutes(1);
        var time = new ManualTimeProvider();
        var client = new TestClient((_, call) => call == 2 ? Fail("Unavailable") : Encoding.UTF8.GetBytes($"payload-{call}"));
        var provider = CreateProvider(options, client, time);
        var reference = Reference("api-key", "4");

        Assert.Equal("payload-1", Resolve(provider, reference, time).ReadSensitiveValue());
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("payload-1", Resolve(provider, reference, time).ReadSensitiveValue());
        time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(ConfigSecretProviderResolutionStatus.Unavailable, Resolve(provider, reference, time).Status);
        // Simulate a wall-clock correction after expiry. The evicted entry must not become usable again.
        time.AdjustUtc(TimeSpan.FromSeconds(-30));
        Assert.Equal("payload-3", Resolve(provider, reference, time).ReadSensitiveValue());
        Assert.Equal("payload-3", Resolve(provider, reference, time).ReadSensitiveValue());
        Assert.Equal(3, client.Calls.Count);
    }

    [Theory]
    [InlineData(false, -10)]
    [InlineData(false, 10)]
    [InlineData(true, -10)]
    [InlineData(true, 10)]
    public void Cache_WallClockCorrectionDoesNotChangeLiveEntryLifetime(bool childCache, int correctionMinutes)
    {
        var options = OptionsWithDefaults();
        options.CacheTtl = TimeSpan.FromMinutes(1);
        options.MapSecret("Service:ApiKey", "api-key", "4");
        var time = new ManualTimeProvider();
        var client = new TestClient();
        var provider = CreateProvider(options, client, time);
        var reference = Reference("api-key", "4");

        string? Read() => childCache
            ? Resolve(provider, reference, time).ReadSensitiveValue()
            : provider.GetValue<string>("Production", reference.LogicalPath);

        Assert.Equal("payload-1", Read());
        time.Advance(TimeSpan.FromSeconds(30));
        time.AdjustUtc(TimeSpan.FromMinutes(correctionMinutes));
        Assert.Equal("payload-1", Read());
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal("payload-2", Read());
        Assert.Equal(2, client.Calls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Options_AreCapturedIncludingCollectionsAndAllAccessSettings(bool cached)
    {
        var options = OptionsWithDefaults();
        options.CacheTtl = cached ? TimeSpan.FromMinutes(1) : null;
        options.LookupTimeout = TimeSpan.FromSeconds(2);
        options.MapSecret("Service:ApiKey", "mapped-key");
        options.EnableConventionResolver("Original.", "prefix-");
        var client = new TestClient();
        var time = new ManualTimeProvider();
        var provider = CreateProvider(options, client, time);

        options.ProjectId = "other-project";
        options.DefaultVersion = "8";
        options.AllowLatestVersion = true;
        options.FailClosedOnProviderFailure = false;
        options.LookupTimeout = TimeSpan.FromHours(1);
        options.CacheTtl = cached ? null : TimeSpan.FromMinutes(1);
        options.MapSecret("Added", "added-key");
        options.EnableConventionResolver("Added.", "added-");
        ((IList<AppSurfaceGoogleSecretMapping>)options.Mappings)[0] = new("Service:ApiKey", "replacement", "9");
        ((IList<AppSurfaceGoogleSecretConvention>)options.Conventions)[0] = new("Original.", "replacement-", "9");

        Assert.Equal(ConfigProviderClaim.Unclaimed, provider.InspectClaim("Production", "Added"));
        Assert.Equal(ConfigProviderClaim.Unclaimed, provider.InspectClaim("Production", "Added.Child"));
        Assert.Equal("mapped-key", Assert.Single(provider.InspectClaims("Service", [])).Key);
        Assert.Equal("projects/project/secrets/prefix-child/versions/4", Assert.Single(provider.InspectClaims("Original.Child", [])).Key);
        Assert.Equal(ConfigSecretReferenceValidationStatus.Invalid, provider.ValidateReference(Reference("api-key", "latest")).Status);

        Resolve(provider, Reference("api-key", null), time);
        Resolve(provider, Reference("api-key", null), time);
        Assert.Equal(cached ? 1 : 2, client.Calls.Count);
        Assert.All(client.Calls, call =>
        {
            Assert.Equal("projects/project/secrets/api-key/versions/4", call.Resource);
            Assert.Equal(TimeSpan.FromSeconds(2), call.Timeout);
        });
        client.Handler = (_, _) => Fail("NotFound");
        Assert.Equal(ConfigCompositionValueResolutionStatus.TerminalFailure, provider.ResolveRaw("Production", "Service:ApiKey").Status);
        Assert.Equal("projects/project/secrets/mapped-key/versions/4", client.Calls[^1].Resource);

        // Rebuilding adopts the changed snapshot without reusing the previous instance's cache.
        var replacement = CreateProvider(options, new TestClient(), time);
        Assert.Equal(ConfigSecretReferenceValidationStatus.Supported, replacement.ValidateReference(Reference("api-key", "latest")).Status);
        Assert.Equal("replacement", Assert.Single(replacement.InspectClaims("Service", [])).Key);
    }

    [Theory]
    [InlineData("NotFound", ConfigSecretProviderResolutionStatus.Missing, false)]
    [InlineData("PermissionDenied", ConfigSecretProviderResolutionStatus.AccessDenied, false)]
    [InlineData("Unauthenticated", ConfigSecretProviderResolutionStatus.AccessDenied, false)]
    [InlineData("InvalidArgument", ConfigSecretProviderResolutionStatus.InvalidReference, false)]
    [InlineData("Cancelled", ConfigSecretProviderResolutionStatus.Unavailable, true)]
    [InlineData("DeadlineExceeded", ConfigSecretProviderResolutionStatus.Unavailable, true)]
    [InlineData("Unavailable", ConfigSecretProviderResolutionStatus.Unavailable, true)]
    [InlineData("timeout", ConfigSecretProviderResolutionStatus.Unavailable, true)]
    [InlineData("unexpected", ConfigSecretProviderResolutionStatus.ProviderFailed, true)]
    [InlineData("Unknown", ConfigSecretProviderResolutionStatus.ProviderFailed, true)]
    [InlineData("utf8", ConfigSecretProviderResolutionStatus.ProviderFailed, false)]
    public void ChildResolution_MapsFailuresIndependentlyOfLegacyFailOpenPolicy(
        string failure, ConfigSecretProviderResolutionStatus expected, bool retryable)
    {
        var options = OptionsWithDefaults();
        options.FailClosedOnProviderFailure = false;
        var provider = CreateProvider(options, new TestClient((_, _) => Fail(failure)));
        var result = Resolve(provider, Reference("api-key", null));

        Assert.Equal(expected, result.Status);
        Assert.Equal(retryable, result.Retryable);
        Assert.Null(result.ReadSensitiveValue());
        ValueSafeAssert.DoesNotExpose("sentinel-secret", JsonSerializer.Serialize(result));
        ValueSafeAssert.DoesNotExpose("sentinel-secret", result.ToString());
    }

    [Theory]
    [InlineData(10, 5, 5)]
    [InlineData(2, 5, 2)]
    [InlineData(2, 0, 0)]
    public void ChildResolution_UsesMonotonicRemainingBudget(int timeout, int remaining, int expected)
    {
        var options = OptionsWithDefaults();
        options.LookupTimeout = TimeSpan.FromSeconds(timeout);
        var time = new ManualTimeProvider();
        var client = new TestClient();
        var provider = CreateProvider(options, client, time);
        var context = new ConfigSecretResolutionContext(time, TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromSeconds(10 - remaining));

        var result = provider.Resolve(Reference("api-key", null), context);

        if (expected == 0)
        {
            Assert.Equal(ConfigSecretProviderResolutionStatus.Unavailable, result.Status);
            Assert.Empty(client.Calls);
        }
        else
        {
            Assert.Equal(ConfigSecretProviderResolutionStatus.Resolved, result.Status);
            Assert.Equal(TimeSpan.FromSeconds(expected), Assert.Single(client.Calls).Timeout);
        }
    }

    private static byte[] Fail(string failure) => failure switch
    {
        "utf8" => [0xc3, 0x28],
        "timeout" => throw new TimeoutException("sentinel-secret"),
        "unexpected" => throw new InvalidOperationException("sentinel-secret"),
        _ => throw new RpcException(new Status(Enum.Parse<StatusCode>(failure), "sentinel-secret"))
    };

    private static AppSurfaceGoogleSecretManagerOptions OptionsWithDefaults() => new() { ProjectId = "project", DefaultVersion = "4" };

    private static ConfigSecretReference Reference(string key, string? version) => new("Production", "Service:ApiKey", key, version);

    private static GoogleSecretManagerConfigProvider CreateProvider(
        AppSurfaceGoogleSecretManagerOptions options, TestClient client, TimeProvider? time = null) =>
        new(Options.Create(options), client, time);

    private static ConfigSecretProviderResolution Resolve(
        GoogleSecretManagerConfigProvider provider, ConfigSecretReference reference, TimeProvider? time = null) =>
        provider.Resolve(reference, new ConfigSecretResolutionContext(time ?? TimeProvider.System, TimeSpan.FromMinutes(1)));

    private sealed class TestClient(Func<string, int, byte[]>? handler = null) : IAppSurfaceGoogleSecretManagerClient
    {
        public List<(string Resource, TimeSpan Timeout)> Calls { get; } = [];
        public Func<string, int, byte[]> Handler { get; set; } = handler ?? ((_, call) => Encoding.UTF8.GetBytes($"payload-{call}"));

        public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout)
        {
            Calls.Add((resourceName, timeout));
            return new(Handler(resourceName, Calls.Count), resourceName);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utc = DateTimeOffset.UnixEpoch;
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => _utc;

        public void Advance(TimeSpan duration)
        {
            _timestamp += duration.Ticks;
            _utc += duration;
        }

        public void AdjustUtc(TimeSpan correction) => _utc += correction;
    }
}
