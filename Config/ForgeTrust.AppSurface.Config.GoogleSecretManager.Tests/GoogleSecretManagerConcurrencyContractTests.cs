using System.Collections.Concurrent;
using System.Text;
using ForgeTrust.AppSurface.Core;
using Google.Api.Gax.Grpc;
using Google.Cloud.SecretManager.V1;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.GoogleSecretManager.Tests;

/// <summary>Exercises native resource identity and overlapping resolutions without cloud credentials.</summary>
[Collection(nameof(GoogleSecretManagerConcurrencyContractTests))]
public sealed class GoogleSecretManagerConcurrencyContractTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const string Resource = "projects/project/secrets/shared-payments--one/versions/stable";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LatestResponse_PreservesConcreteVersionThroughRuntimeAuditAndCache(bool cache, bool auditFirst)
    {
        const string requested = "projects/project/secrets/Exact_Id/versions/latest";
        const string resolved = "projects/123456789/secrets/Exact_Id/versions/7";
        var service = new ResolvingServiceClient((requested, resolved, "version-seven"));
        var provider = CreateProvider(new GoogleSecretManagerClientAdapter(service), options =>
        {
            options.AllowLatestVersion = true;
            options.CacheTtl = cache ? TimeSpan.FromMinutes(5) : null;
            options.MapSecret("Payments:One", requested);
        });

        if (auditFirst) AssertAudit();
        else AssertFound("version-seven", provider.Resolve<string>(Request("Payments:One")));
        AssertAudit();
        AssertFound("version-seven", provider.Resolve<string>(Request("payments:one")));
        Assert.Equal(cache ? 1 : 3, service.Calls);
        Assert.All(service.Requested, name => Assert.Equal(requested, name));

        void AssertAudit()
        {
            var audit = provider.ResolveForAudit(Request("Payments:One"), typeof(string), ConfigAuditSourceRole.Override);
            Assert.Equal(ConfigAuditEntryState.Resolved, audit.State);
            Assert.Equal("version-seven", audit.Value);
            Assert.Empty(audit.Diagnostics);
            var source = Assert.Single(audit.Sources);
            Assert.Equal(resolved, source.ConfigPath);
            Assert.Equal("Payments:One", source.AppliedToPath);
            Assert.Equal(ConfigAuditSourceRole.Override, source.Role);
            Assert.Equal(ConfigAuditSensitivity.Sensitive, source.Sensitivity);
        }
    }

    [Theory]
    [InlineData("project", "project", "latest", "7")]
    [InlineData("project", "123456789", "7", "7")]
    [InlineData("project", "123456789", "stable", "7")]
    [InlineData("123456789", "project", "7", "7")]
    public void ResolvedAliases_PreserveExactResponseWithoutChangingLogicalIdentity(
        string requestProject, string responseProject, string requestedVersion, string resolvedVersion)
    {
        var requested = $"projects/{requestProject}/secrets/Exact_Id/versions/{requestedVersion}";
        var resolved = $"projects/{responseProject}/secrets/Exact_Id/versions/{resolvedVersion}";
        var client = new ResolvingServiceClient((requested, resolved, "resolved"));
        var provider = CreateProvider(new GoogleSecretManagerClientAdapter(client), options =>
        {
            options.AllowLatestVersion = true;
            options.MapSecret("Payments:One", requested);
        });
        var audit = provider.ResolveForAudit(Request("payments:one"), typeof(string), ConfigAuditSourceRole.Base);
        Assert.Equal(ConfigAuditEntryState.Resolved, audit.State);
        Assert.Equal("resolved", audit.Value);
        Assert.Equal(resolved, Assert.Single(audit.Sources).ConfigPath);
        Assert.Equal("payments:one", Assert.Single(audit.Sources).AppliedToPath);
        Assert.Equal(1, client.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DistinctKeysResolvingToOneConcreteResource_PoisonBothRequestedClaims(bool latestFirst)
    {
        const string requested = "projects/project/secrets/Exact_Id/versions/latest";
        const string resolved = "projects/123456789/secrets/Exact_Id/versions/7";
        var service = new ResolvingServiceClient((requested, resolved, "must-not-be-selected"), (resolved, resolved, "must-not-be-selected"));
        var provider = CreateProvider(new GoogleSecretManagerClientAdapter(service), options =>
        {
            options.AllowLatestVersion = true;
            options.MapSecret("Payments:Alias", requested);
            options.MapSecret("Payments:Pinned", resolved);
        });
        if (!latestFirst) AssertFound("must-not-be-selected", provider.Resolve<string>(Request("Payments:Pinned")));
        AssertCollision(provider.Resolve<string>(Request("Payments:Alias")));
        var callsAtCollision = service.Calls;
        foreach (var key in new[] { "Payments:Alias", "Payments:Pinned" })
        {
            AssertCollision(provider.Resolve<string>(Request(key)));
            var audit = provider.ResolveForAudit(Request(key), typeof(string), ConfigAuditSourceRole.Base);
            Assert.Equal(ConfigAuditEntryState.Invalid, audit.State);
            Assert.Equal("config-key-collision", Assert.Single(audit.Diagnostics).Code);
            Assert.Null(audit.Value);
            Assert.Empty(audit.Sources);
        }
        Assert.Equal(callsAtCollision, service.Calls);
    }

    [Fact]
    public async Task CaseOnlyLogicalMappings_AreRejectedAtHostStartupBeforeClientAccess()
    {
        var client = new ResolvingServiceClient();
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddConfigAuditKey<string>("Payments:ApiKey");
            new AppSurfaceGoogleSecretManagerModule().ConfigureServices(new StartupContext([], new TestHostModule()), services);
            services.UseAppSurfaceGoogleSecretManagerClient(new GoogleSecretManagerClientAdapter(client));
            services.ConfigureAppSurfaceGoogleSecretManager(options =>
            {
                options.ProjectId = "project";
                options.MapSecret("Payments:ApiKey", "first", "7");
                options.MapSecret("payments:apikey", "second", "7");
            });
        }).Build();

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains("mapped more than once", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.Calls);
    }

    [Theory]
    [InlineData("projects/project/secrets/Exact_Id/versions/7", null)]
    [InlineData("projects/project/secrets/Exact_Id/versions/7", "")]
    [InlineData("projects/project/secrets/Exact_Id/versions/7", "invalid")]
    [InlineData("projects/project/secrets//versions/7", "projects/project/secrets/Exact_Id/versions/7")]
    [InlineData("projects/project/secrets/Exact_Id/versions/7", "projects/project/secrets/exact_id/versions/7")]
    [InlineData("projects/project/secrets/Exact_Id/versions/7", "projects/other/secrets/Exact_Id/versions/7")]
    [InlineData("projects/123/secrets/Exact_Id/versions/7", "projects/456/secrets/Exact_Id/versions/7")]
    [InlineData("projects/project/secrets/Exact_Id/versions/7", "projects/project/secrets/Exact_Id/versions/8")]
    [InlineData("projects/project/secrets/Exact_Id/versions/stable", "projects/project/secrets/Exact_Id/versions/Stable")]
    [InlineData("projects/project/locations/us-east1/secrets/Exact_Id/versions/latest", "projects/project/locations/us-west1/secrets/Exact_Id/versions/7")]
    public void UnrelatedResolvedNames_RemainTerminalAndAreNeverCached(string requested, string? resolved)
    {
        var client = new MutableClient(new AppSurfaceGoogleSecretPayload(Encoding.UTF8.GetBytes("value-sentinel"), resolved));
        var provider = CreateProvider(client, options =>
        {
            options.AllowLatestVersion = true;
            options.MapSecret("Payments:One", requested);
        });
        var runtime = provider.Resolve<string>(Request("Payments:One"));
        Assert.Equal(ConfigProviderValueStatus.Terminal, runtime.Status);
        Assert.Equal("config-provider-failed", runtime.Diagnostic!.Code);
        Assert.Null(runtime.Value);
        Assert.DoesNotContain("value-sentinel", runtime.Diagnostic.ToDisplayString());
        var audit = provider.ResolveForAudit(Request("Payments:One"), typeof(string), ConfigAuditSourceRole.Base);
        Assert.Equal(ConfigAuditEntryState.Invalid, audit.State);
        Assert.Null(audit.Value);
        Assert.Empty(audit.Sources);
        Assert.Equal("config-provider-failed", Assert.Single(audit.Diagnostics).Code);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public void RotatingAlias_AuditAssociatesEachValueWithItsOwnVersionWithoutASecondFetch()
    {
        var client = new RotatingAliasClient();
        var provider = CreateProvider(client, options =>
        {
            options.AllowLatestVersion = true;
            options.CacheTtl = null;
            options.MapSecret("Payments:One", "Exact_Id", "latest");
        });
        for (var version = 7; version <= 8; version++)
        {
            var audit = provider.ResolveForAudit(Request("Payments:One"), typeof(string), ConfigAuditSourceRole.Base);
            Assert.Equal(ConfigAuditEntryState.Resolved, audit.State);
            Assert.Equal($"value-{version}", audit.Value);
            Assert.Equal($"projects/123456789/secrets/Exact_Id/versions/{version}", Assert.Single(audit.Sources).ConfigPath);
        }
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public void ResolvedAliasClaims_StopBeforePublishingAnUnboundedVersionHistory()
    {
        var client = new RotatingAliasClient();
        var provider = CreateProvider(client, options =>
        {
            options.AllowLatestVersion = true;
            options.CacheTtl = null;
            options.MaxAdHocClaims = 1;
            options.MapSecret("Payments:One", "Exact_Id", "latest");
        });
        AssertFound("value-7", provider.Resolve<string>(Request("Payments:One")));
        var exhausted = provider.Resolve<string>(Request("Payments:One"));
        Assert.Equal(ConfigProviderValueStatus.Terminal, exhausted.Status);
        Assert.Equal("config-provider-failed", exhausted.Diagnostic!.Code);
        Assert.Null(exhausted.Value);
        Assert.DoesNotContain("value-8", exhausted.Diagnostic.ToDisplayString());
        Assert.Equal(2, client.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ClaimPoisonedDuringConversion_CannotPublishRuntimeOrAuditValue(bool cache, bool audit)
    {
        var client = new MutableClient(new AppSurfaceGoogleSecretPayload(Encoding.UTF8.GetBytes("{}"), Resource));
        var provider = CreateProvider(client, options =>
        {
            options.CacheTtl = cache ? TimeSpan.FromMinutes(5) : null;
            options.MapSecret("Explicit:Key", Resource);
            options.EnableConventionResolver("Payments", "shared-", "stable");
        });
        AssertFound("{}", provider.Resolve<string>(Request("Explicit:Key")));
        ConvertingValue.OnConstruct = () => AssertCollision(provider.Resolve<string>(Request("Payments:One")));
        try
        {
            if (audit)
            {
                var result = provider.ResolveForAudit(Request("Explicit:Key"), typeof(ConvertingValue), ConfigAuditSourceRole.Base);
                Assert.Equal(ConfigAuditEntryState.Invalid, result.State);
                Assert.Null(result.Value);
                Assert.Empty(result.Sources);
                Assert.Equal("config-key-collision", Assert.Single(result.Diagnostics).Code);
            }
            else
            {
                var result = provider.Resolve<ConvertingValue>(Request("Explicit:Key"));
                Assert.Equal(ConfigProviderValueStatus.Terminal, result.Status);
                Assert.Null(result.Value);
                Assert.Equal("config-key-collision", result.Diagnostic!.Code);
            }
            Assert.Equal(cache ? 1 : 2, client.Calls);
#pragma warning disable CS0618 // A terminal resource collision must remain terminal through the compatibility result surface.
            Assert.Throws<ConfigurationResolutionException>(() => provider.ResolveValue<string>("Production", "Explicit:Key"));
#pragma warning restore CS0618
        }
        finally { ConvertingValue.OnConstruct = null; }
    }

    [Fact]
    public void AuditConversion_PropagatesFatalConstructorFailureWithoutReflectionWrapper()
    {
        var client = new MutableClient(new AppSurfaceGoogleSecretPayload(Encoding.UTF8.GetBytes("{}"), Resource));
        var provider = CreateProvider(client, options => options.MapSecret("Explicit:Key", Resource));
        var failure = new AccessViolationException("simulated-conversion-failure");
        ConvertingValue.OnConstruct = () => throw failure;
        try
        {
            Assert.Same(failure, Assert.Throws<AccessViolationException>(() =>
                provider.ResolveForAudit(Request("Explicit:Key"), typeof(ConvertingValue), ConfigAuditSourceRole.Base)));
        }
        finally { ConvertingValue.OnConstruct = null; }
    }

    [Fact]
    public void KnownUnrepresentableConvention_IsRejectedBeforeAnyFetch()
    {
        using var services = new ServiceCollection().AddConfigAuditKey<string>(AppSurfaceConfigKey.Parse("Payments:Bad.Key")).BuildServiceProvider();
        var client = new ResolvingServiceClient();
        var options = new AppSurfaceGoogleSecretManagerOptions { ProjectId = "project" };
        options.EnableConventionResolver("Payments", "shared-", "7");
        var failure = Assert.Throws<OptionsValidationException>(() =>
            new GoogleSecretManagerConfigProvider(Options.Create(options), new GoogleSecretManagerClientAdapter(client), services));
        Assert.Contains("config-key-unrepresentable", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.Calls);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void PreCancelledRequest_StopsBeforeClaimsCacheOrClient(bool warm, bool audit, bool auditScope)
    {
        var client = new MutableClient(new AppSurfaceGoogleSecretPayload(Encoding.UTF8.GetBytes("original"), Resource));
        var provider = CreateProvider(client, options =>
        {
            options.MapSecret("Explicit:Key", Resource);
            options.EnableConventionResolver("Payments", "shared-", "stable");
        });
        if (warm) AssertFound("original", provider.Resolve<string>(Request("Explicit:Key")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var scope = new ConfigResolutionScope(cancellation.Token, auditScope ? new ConfigResourceOptions() : null);
        foreach (var key in new[] { "Explicit:Key", "Payments:One" })
        {
            var exception = Assert.ThrowsAny<OperationCanceledException>(() =>
            {
                // The first key has a warm cache entry; the second would poison its claim if checked too late.
                if (audit) provider.ResolveForAudit(Request(key, scope), typeof(string), ConfigAuditSourceRole.Base);
                else provider.Resolve<string>(Request(key, scope));
            });
            Assert.Equal(cancellation.Token, exception.CancellationToken);
        }
        Assert.Equal(warm ? 1 : 0, client.Calls);
        AssertFound("original", provider.Resolve<string>(Request("Explicit:Key")));
        Assert.Equal(1, client.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ExpiredAuditDeadline_IsIncompleteBeforeClaimsCacheOrClient(bool warm, bool audit)
    {
        var client = new MutableClient(new AppSurfaceGoogleSecretPayload(Encoding.UTF8.GetBytes("original"), Resource));
        var provider = CreateProvider(client, options =>
        {
            options.MapSecret("Explicit:Key", Resource);
            options.EnableConventionResolver("Payments", "shared-", "stable");
        });
        if (warm) AssertFound("original", provider.Resolve<string>(Request("Explicit:Key")));
        using var scope = new ConfigResolutionScope(auditOptions: new ConfigResourceOptions { AuditTimeout = TimeSpan.FromMilliseconds(1) });
        Assert.True(SpinWait.SpinUntil(() => scope.CancellationToken.IsCancellationRequested, Timeout));
        foreach (var key in new[] { "Explicit:Key", "Payments:One" })
        {
            if (audit)
            {
                var result = provider.ResolveForAudit(Request(key, scope), typeof(string), ConfigAuditSourceRole.Base);
                Assert.Equal(ConfigAuditEntryState.Invalid, result.State);
                Assert.Null(result.Value);
                Assert.Empty(result.Sources);
                Assert.Equal("config-audit-deadline", Assert.Single(result.Diagnostics).Code);
            }
            else
            {
                var result = provider.Resolve<string>(Request(key, scope));
                Assert.Equal(ConfigProviderValueStatus.Terminal, result.Status);
                Assert.Null(result.Value);
                Assert.Equal("config-audit-deadline", result.Diagnostic!.Code);
            }
        }
        Assert.Equal("config-audit-deadline", scope.IncompleteAuditDiagnostic!.Code);
        Assert.Equal(warm ? 1 : 0, client.Calls);
        AssertFound("original", provider.Resolve<string>(Request("Explicit:Key")));
        Assert.Equal(1, client.Calls);
    }

    [Theory]
    [InlineData("utf8", false)]
    [InlineData("utf8", true)]
    [InlineData("scalar", false)]
    [InlineData("scalar", true)]
    [InlineData("json", false)]
    [InlineData("json", true)]
    [InlineData("null", false)]
    [InlineData("null", true)]
    public void RejectedPayload_IsEvictedForRepairedAliasRetryWithNewVersion(string failure, bool audit)
    {
        var bad = failure switch
        {
            "utf8" => new byte[] { 0xc3, 0x28 },
            "null" => [],
            "json" => Encoding.UTF8.GetBytes("{"),
            _ => Encoding.UTF8.GetBytes("invalid-integer-sentinel")
        };
        var good = Encoding.UTF8.GetBytes(failure == "json" ? """{"Name":"repaired","Numbers":[42]}""" : "42");
        var calls = 0;
        var client = new CallbackClient(_ =>
        {
            var version = 6 + Interlocked.Increment(ref calls);
            return new AppSurfaceGoogleSecretPayload(version == 7 ? bad : good, $"projects/123456789/secrets/Exact_Id/versions/{version}");
        });
        var provider = CreateProvider(client, options =>
        {
            options.AllowLatestVersion = true;
            options.MapSecret("Payments:One", "Exact_Id", "latest");
        });
        if (failure == "json") Check<MutableSettings>(value => Assert.Equal("repaired", value!.Name));
        else Check<int?>(value => Assert.Equal(42, value));

        void Check<T>(Action<T?> assertValue)
        {
            if (audit)
            {
                var rejected = provider.ResolveForAudit(Request("Payments:One"), typeof(T), ConfigAuditSourceRole.Base);
                Assert.Equal(ConfigAuditEntryState.Invalid, rejected.State);
                Assert.Null(rejected.Value);
                Assert.Equal("config-provider-failed", Assert.Single(rejected.Diagnostics).Code);
            }
            else
            {
                var rejected = provider.Resolve<T>(Request("Payments:One"));
                Assert.Equal(ConfigProviderValueStatus.Terminal, rejected.Status);
                Assert.Null(rejected.Value);
                Assert.Equal("config-provider-failed", rejected.Diagnostic!.Code);
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var repaired = provider.ResolveForAudit(Request("Payments:One"), typeof(T), ConfigAuditSourceRole.Base);
                Assert.Equal(ConfigAuditEntryState.Resolved, repaired.State);
                assertValue((T?)repaired.Value);
                Assert.Equal("projects/123456789/secrets/Exact_Id/versions/8", Assert.Single(repaired.Sources).ConfigPath);
                var runtime = provider.Resolve<T>(Request("Payments:One"));
                Assert.Equal(ConfigProviderValueStatus.Found, runtime.Status);
                assertValue(runtime.Value);
            }
            Assert.Equal(2, calls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OldConversionFailure_CannotEvictANewerSuccessfulCacheGeneration(bool audit)
    {
        const string otherResource = "projects/project/secrets/other/versions/7";
        const string replacement = """{"Name":"replacement"}""";
        var resourceCalls = 0;
        var calls = 0;
        var client = new CallbackClient(resource =>
        {
            Interlocked.Increment(ref calls);
            var value = resource == Resource ? Interlocked.Increment(ref resourceCalls) == 1 ? "{}" : replacement : "other";
            return new AppSurfaceGoogleSecretPayload(Encoding.UTF8.GetBytes(value), resource);
        });
        var provider = CreateProvider(client, options =>
        {
            options.CacheCapacity = 1;
            options.MapSecret("Explicit:Key", Resource);
            options.MapSecret("Other:Key", otherResource);
        });
        AssertFound("{}", provider.Resolve<string>(Request("Explicit:Key")));
        ConvertingValue.OnConstruct = () =>
        {
            // A second resolver evicts the old entry and publishes a newer one while this conversion is pending.
            var refresher = new PendingResolution(() =>
            {
                AssertFound("other", provider.Resolve<string>(Request("Other:Key")));
                return provider.Resolve<string>(Request("Explicit:Key"));
            });
            AssertFound(replacement, refresher.Result.WaitAsync(Timeout).GetAwaiter().GetResult());
            throw new FormatException("rejected-old-generation");
        };
        try
        {
            if (audit)
                Assert.Equal(ConfigAuditEntryState.Invalid, provider.ResolveForAudit(Request("Explicit:Key"), typeof(ConvertingValue), ConfigAuditSourceRole.Base).State);
            else Assert.Equal(ConfigProviderValueStatus.Terminal, provider.Resolve<ConvertingValue>(Request("Explicit:Key")).Status);
        }
        finally { ConvertingValue.OnConstruct = null; }
        Assert.Equal(3, calls);
        var retained = provider.ResolveForAudit(Request("Explicit:Key"), typeof(string), ConfigAuditSourceRole.Base);
        Assert.Equal(ConfigAuditEntryState.Resolved, retained.State);
        Assert.Equal(replacement, retained.Value);
        Assert.Equal(Resource, Assert.Single(retained.Sources).ConfigPath);
        AssertFound(replacement, provider.Resolve<string>(Request("Explicit:Key")));
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData("other-project", "shared-payments--one", "stable")]
    [InlineData("project", "other-secret", "stable")]
    [InlineData("project", "shared-payments--one", "other-version")]
    [InlineData("Project", "shared-payments--one", "stable")]
    [InlineData("project", "Shared-payments--one", "stable")]
    [InlineData("project", "shared-payments--one", "Stable")]
    public async Task DistinctOrdinalResources_PreserveIndependentClaimsCacheAndAuditSources(
        string project, string secretId, string version)
    {
        var otherResource = $"projects/{project}/secrets/{secretId}/versions/{version}";
        using var client = new GatedClient((Resource, "convention"), (otherResource, "explicit"));
        var provider = CreateProvider(client, options =>
        {
            options.EnableConventionResolver("Payments", "shared-", "stable");
            options.MapSecret("Explicit:Key", otherResource);
        });
        var convention = Start(provider, "Payments:One");
        var explicitMapping = Start(provider, "Explicit:Key");
        try
        {
            await client.AllStarted.Task.WaitAsync(Timeout);
            Assert.Equal(2, client.Calls);
            client.Release.Set();
            AssertFound("convention", await convention.Result.WaitAsync(Timeout));
            AssertFound("explicit", await explicitMapping.Result.WaitAsync(Timeout));

            AssertFound("convention", provider.Resolve<string>(Request("payments:one")));
            AssertFound("explicit", provider.Resolve<string>(Request("explicit:key")));
            Assert.Equal(2, client.Calls);
            Assert.Equal(new[] { Resource, otherResource }.Order(StringComparer.Ordinal),
                client.Requested.Order(StringComparer.Ordinal));

            var firstAudit = provider.ResolveForAudit(Request("Payments:One"), typeof(string), ConfigAuditSourceRole.Base);
            var secondAudit = provider.ResolveForAudit(Request("Explicit:Key"), typeof(string), ConfigAuditSourceRole.Base);
            Assert.Equal(ConfigAuditEntryState.Resolved, firstAudit.State);
            Assert.Equal(ConfigAuditEntryState.Resolved, secondAudit.State);
            Assert.Equal(Resource, Assert.Single(firstAudit.Sources).ConfigPath);
            Assert.Equal(otherResource, Assert.Single(secondAudit.Sources).ConfigPath);
            Assert.Equal(2, client.Calls);
        }
        finally
        {
            client.Release.Set();
            await Task.WhenAll(convention.Result, explicitMapping.Result).WaitAsync(Timeout);
        }
    }

    [Fact]
    public void IdenticalOrdinalResource_RejectsDistinctExplicitKeysBeforeClientAccess()
    {
        using var client = new GatedClient((Resource, "unused"));
        var exception = Assert.Throws<OptionsValidationException>(() => CreateProvider(client, options =>
        {
            options.MapSecret("Payments:One", Resource);
            options.MapSecret("Other:Key", Resource);
        }));

        Assert.Contains("config-key-collision", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OverlappingDistinctKeyClaim_PoisonsThePendingFetchAndEverySubsequentResolution(bool cache)
    {
        using var client = new GatedClient((Resource, "must-not-be-selected"));
        var provider = CreateProvider(client, options =>
        {
            options.CacheTtl = cache ? TimeSpan.FromMinutes(5) : null;
            options.MapSecret("Explicit:Key", Resource);
            options.EnableConventionResolver("Payments", "shared-", "stable");
        });
        // The exact explicit claim is already registered. Its fetch stays blocked while a distinct
        // convention key attempts to register the same native resource through the public Resolve path.
        var pending = Start(provider, "Explicit:Key");
        try
        {
            await client.AllStarted.Task.WaitAsync(Timeout);
            pending.WaitUntilBlocked();
            AssertCollision(provider.Resolve<string>(Request("Payments:One")));
            Assert.False(pending.Result.IsCompleted);
            AssertCollision(provider.Resolve<string>(Request("explicit:key")));
            AssertCollision(provider.Resolve<string>(Request("payments:one")));
            Assert.Equal(1, client.Calls);

            client.Release.Set();
            AssertCollision(await pending.Result.WaitAsync(Timeout));
            AssertCollision(provider.Resolve<string>(Request("Explicit:Key")));
            AssertCollision(provider.Resolve<string>(Request("Payments:One")));
            var audit = provider.ResolveForAudit(Request("Explicit:Key"), typeof(string), ConfigAuditSourceRole.Base);
            Assert.Equal(ConfigAuditEntryState.Invalid, audit.State);
            Assert.Equal("config-key-collision", Assert.Single(audit.Diagnostics).Code);
            Assert.Empty(audit.Sources);
            Assert.Equal(1, client.Calls);
        }
        finally
        {
            client.Release.Set();
            await pending.Result.WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task ConcurrentConventionClaims_KeepDistinctKeysAndCoalesceCaseVariants()
    {
        const string other = "projects/project/secrets/shared-payments--two/versions/stable";
        using var client = new GatedClient((Resource, "one"), (other, "two"));
        var provider = CreateProvider(client, options =>
        {
            options.CacheTtl = null;
            options.EnableConventionResolver("Payments", "shared-", "stable");
        });
        using var start = new ManualResetEventSlim(false);
        var workers = new[] { "Payments:One", "payments:one", "Payments:Two", "payments:two" }
            .Select(key => new PendingResolution(() => provider.Resolve<string>(Request(key)), start)).ToArray();
        try
        {
            start.Set();
            await client.AllStarted.Task.WaitAsync(Timeout);
            foreach (var worker in workers) worker.WaitUntilBlocked();
            Assert.Equal(2, client.Calls);
            client.Release.Set();
            var results = await Task.WhenAll(workers.Select(worker => worker.Result)).WaitAsync(Timeout);
            AssertFound("one", results[0]);
            AssertFound("one", results[1]);
            AssertFound("two", results[2]);
            AssertFound("two", results[3]);
            Assert.Equal(2, client.Calls);
        }
        finally
        {
            start.Set();
            client.Release.Set();
            await Task.WhenAll(workers.Select(worker => worker.Result)).WaitAsync(Timeout);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellingOneWaiter_DoesNotCancelTheOtherWaiterOrSharedFetch(bool cancelInitiator)
    {
        using var client = new GatedClient((Resource, "live-result"));
        var provider = CreateProvider(client, options =>
        {
            options.CacheTtl = null;
            options.MapSecret("Payments:One", Resource);
        });
        using var cancellation = new CancellationTokenSource();
        using var cancelledScope = new ConfigResolutionScope(cancellation.Token);
        using var liveScope = new ConfigResolutionScope();
        var first = Start(provider, "Payments:One", cancelInitiator ? cancelledScope : liveScope);
        PendingResolution? second = null;
        try
        {
            await client.AllStarted.Task.WaitAsync(Timeout);
            second = Start(provider, "payments:one", cancelInitiator ? liveScope : cancelledScope);
            first.WaitUntilBlocked();
            second.WaitUntilBlocked();
            var cancelled = cancelInitiator ? first : second;
            var live = cancelInitiator ? second : first;

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.Result.WaitAsync(Timeout));
            Assert.False(live.Result.IsCompleted);
            Assert.False(liveScope.CancellationToken.IsCancellationRequested);
            Assert.Equal(1, client.Calls);

            client.Release.Set();
            AssertFound("live-result", await live.Result.WaitAsync(Timeout));
            Assert.Equal(1, client.Calls);
        }
        finally
        {
            client.Release.Set();
            // Observe both completions before disposing the scopes or the client gate.
            foreach (var worker in new[] { first, second })
            {
                if (worker is null) continue;
                try { await worker.Result.WaitAsync(Timeout); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
        }
    }

    [Fact]
    public async Task SharedFetchFailure_ReachesEveryWaiterAndIsEvictedBeforeRetry()
    {
        using var client = new GatedClient((Resource, "retry-result")) { FailFirstFetch = true };
        var provider = CreateProvider(client, options => options.MapSecret("Payments:One", Resource));
        var workers = Enumerable.Range(0, 4).Select(_ => Start(provider, "Payments:One")).ToArray();
        try
        {
            await client.AllStarted.Task.WaitAsync(Timeout);
            foreach (var worker in workers) worker.WaitUntilBlocked();
            Assert.Equal(1, client.Calls);
            client.Release.Set();
            var results = await Task.WhenAll(workers.Select(worker => worker.Result)).WaitAsync(Timeout);
            Assert.All(results, result =>
            {
                Assert.Equal(ConfigProviderValueStatus.Terminal, result.Status);
                Assert.Null(result.Value);
                Assert.Equal("config-provider-failed", result.Diagnostic!.Code);
                Assert.DoesNotContain("client-failure-sentinel", result.Diagnostic.ToDisplayString(), StringComparison.Ordinal);
            });
            Assert.Equal(1, client.Calls);

            AssertFound("retry-result", provider.Resolve<string>(Request("Payments:One")));
            AssertFound("retry-result", provider.Resolve<string>(Request("payments:one")));
            Assert.Equal(2, client.Calls);
        }
        finally
        {
            client.Release.Set();
            await Task.WhenAll(workers.Select(worker => worker.Result)).WaitAsync(Timeout);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MutableClientPayloadAndReturnedValues_CannotAlterLaterResolutions(bool cache)
    {
        var bytes = Encoding.UTF8.GetBytes("""{"Name":"original","Numbers":[1,2]}""");
        var payload = new AppSurfaceGoogleSecretPayload(bytes, Resource);
        var exposed = payload.Data;
        Array.Fill(bytes, (byte)'!');
        Array.Fill(exposed, (byte)'?');
        var client = new MutableClient(payload);
        var provider = CreateProvider(client, options =>
        {
            options.CacheTtl = cache ? TimeSpan.FromMinutes(5) : null;
            options.MapSecret("Payments:One", Resource);
        });

        var first = provider.Resolve<MutableSettings>(Request("Payments:One"));
        Assert.Equal(ConfigProviderValueStatus.Found, first.Status);
        Assert.Equal("original", first.Value!.Name);
        Assert.Equal(new[] { 1, 2 }, first.Value.Numbers);
        first.Value.Name = "caller-changed";
        first.Value.Numbers[0] = 99;
        Array.Fill(payload.Data, (byte)'#');

        var second = provider.Resolve<MutableSettings>(Request("payments:one"));
        Assert.Equal(ConfigProviderValueStatus.Found, second.Status);
        Assert.NotSame(first.Value, second.Value);
        Assert.NotSame(first.Value.Numbers, second.Value!.Numbers);
        Assert.Equal("original", second.Value.Name);
        Assert.Equal(new[] { 1, 2 }, second.Value.Numbers);
        Assert.Equal(cache ? 1 : 2, client.Calls);
        Assert.Equal("""{"Name":"original","Numbers":[1,2]}""", Encoding.UTF8.GetString(payload.Data));
    }

    private static GoogleSecretManagerConfigProvider CreateProvider(
        IAppSurfaceGoogleSecretManagerClient client, Action<AppSurfaceGoogleSecretManagerOptions> configure)
    {
        var options = new AppSurfaceGoogleSecretManagerOptions { ProjectId = "project", CacheTtl = TimeSpan.FromMinutes(5) };
        configure(options);
        return new GoogleSecretManagerConfigProvider(Options.Create(options), client);
    }

    private static ConfigProviderRequest Request(string key, ConfigResolutionScope? scope = null) =>
        new("Production", AppSurfaceConfigKey.Parse(key), scope);

    private static PendingResolution Start(GoogleSecretManagerConfigProvider provider, string key, ConfigResolutionScope? scope = null) =>
        new(() => provider.Resolve<string>(Request(key, scope)));

    private static void AssertFound(string expected, ConfigProviderValueResult<string> result)
    {
        Assert.Equal(ConfigProviderValueStatus.Found, result.Status);
        Assert.Equal(expected, result.Value);
    }

    private static void AssertCollision(ConfigProviderValueResult<string> result)
    {
        Assert.Equal(ConfigProviderValueStatus.Terminal, result.Status);
        Assert.Null(result.Value);
        Assert.Equal("config-key-collision", result.Diagnostic!.Code);
        Assert.DoesNotContain("must-not-be-selected", result.Diagnostic.ToDisplayString(), StringComparison.Ordinal);
    }

    // Dedicated threads avoid pool starvation from the synchronous provider contract. After the
    // client gate is entered, WaitSleepJoin observes the resolver actually blocked, without sleeps
    // or private provider reflection. Every wait is bounded and every worker completion is observed.
    private sealed class PendingResolution
    {
        private readonly Thread _thread;
        private int _enteredResolution;
        private readonly TaskCompletionSource<ConfigProviderValueResult<string>> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal PendingResolution(Func<ConfigProviderValueResult<string>> resolve, ManualResetEventSlim? start = null)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    if (start is not null) Assert.True(start.Wait(Timeout));
                    Volatile.Write(ref _enteredResolution, 1);
                    _completion.TrySetResult(resolve());
                }
                catch (Exception exception) { _completion.TrySetException(exception); }
            })
            { IsBackground = true };
            _thread.Start();
        }

        internal Task<ConfigProviderValueResult<string>> Result => _completion.Task;

        internal void WaitUntilBlocked()
        {
            Assert.True(SpinWait.SpinUntil(() => Result.IsCompleted
                || (Volatile.Read(ref _enteredResolution) != 0
                    && (_thread.ThreadState & ThreadState.WaitSleepJoin) != 0), Timeout), "Resolution did not reach its bounded wait.");
            Assert.False(Result.IsCompleted);
        }
    }

    private sealed class GatedClient(params (string Resource, string Value)[] values) : IAppSurfaceGoogleSecretManagerClient, IDisposable
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal ConcurrentQueue<string> Requested { get; } = new();
        internal TaskCompletionSource AllStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new(false);
        internal bool FailFirstFetch { get; init; }

        public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout)
        {
            Requested.Enqueue(resourceName);
            var call = Interlocked.Increment(ref _calls);
            if (call >= values.Length) AllStarted.TrySetResult();
            if (!Release.Wait(Timeout)) throw new TimeoutException("The test did not release its client gate.");
            if (FailFirstFetch && call == 1) throw new IOException("client-failure-sentinel");
            var value = values.Single(item => StringComparer.Ordinal.Equals(item.Resource, resourceName)).Value;
            return new AppSurfaceGoogleSecretPayload(Encoding.UTF8.GetBytes(value), resourceName);
        }

        public void Dispose() => Release.Dispose();
    }

    private sealed class MutableClient(AppSurfaceGoogleSecretPayload payload) : IAppSurfaceGoogleSecretManagerClient
    {
        internal int Calls { get; private set; }
        public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout)
        {
            Calls++;
            return payload;
        }
    }

    private sealed class ResolvingServiceClient(params (string Requested, string Resolved, string Value)[] responses) : SecretManagerServiceClient
    {
        internal int Calls { get; private set; }
        internal List<string> Requested { get; } = [];
        public override AccessSecretVersionResponse AccessSecretVersion(AccessSecretVersionRequest request, CallSettings? callSettings = null)
        {
            Calls++;
            Requested.Add(request.Name);
            var response = responses.Single(item => StringComparer.Ordinal.Equals(item.Requested, request.Name));
            return new AccessSecretVersionResponse
            {
                Name = response.Resolved,
                Payload = new SecretPayload { Data = ByteString.CopyFromUtf8(response.Value) }
            };
        }
    }

    private sealed class RotatingAliasClient : IAppSurfaceGoogleSecretManagerClient
    {
        internal int Calls { get; private set; }
        public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout)
        {
            Assert.Equal("projects/project/secrets/Exact_Id/versions/latest", resourceName);
            var version = 6 + ++Calls;
            return new AppSurfaceGoogleSecretPayload(Encoding.UTF8.GetBytes($"value-{version}"),
                $"projects/123456789/secrets/Exact_Id/versions/{version}");
        }
    }

    private sealed class CallbackClient(Func<string, AppSurfaceGoogleSecretPayload> access) : IAppSurfaceGoogleSecretManagerClient
    {
        public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout) => access(resourceName);
    }

    private sealed class MutableSettings
    {
        public string Name { get; set; } = string.Empty;
        public int[] Numbers { get; set; } = [];
    }

    private sealed class ConvertingValue
    {
        internal static Action? OnConstruct { get; set; }
        public ConvertingValue()
        {
            OnConstruct?.Invoke();
        }
    }

    private sealed class TestHostModule : IAppSurfaceHostModule
    {
        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder) { }
        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder) { }
        public void ConfigureServices(StartupContext context, IServiceCollection services) { }
        public void RegisterDependentModules(ModuleDependencyBuilder builder) { }
    }
}

/// <summary>Keeps bounded client gates independent of other tests that synchronously occupy pool workers.</summary>
[CollectionDefinition(nameof(GoogleSecretManagerConcurrencyContractTests), DisableParallelization = true)]
public sealed class GoogleSecretManagerConcurrencyContractCollection;
