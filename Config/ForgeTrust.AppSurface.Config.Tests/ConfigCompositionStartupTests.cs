using FakeItEasy;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.Tests;

/// <summary>Verifies eager local validation through the module's real host and DI registrations.</summary>
/// <remarks>
/// Known entries are registered explicitly. Running assembly discovery here would include unrelated test wrappers.
/// Real file providers supply descriptor policy; raw value providers cannot declare inline references.
/// </remarks>
public sealed class ConfigCompositionStartupTests
{
    private const string EnabledDeclaration =
        """{"Service":{"ApiKey":{"key":"opaque-resource","provider":"provider-a"}}}""";

    /// <summary>Both default and custom positive limits allow startup while enabled payload reads stay lazy.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_PositiveOptionsValidateKnownPlanWithoutActivatingWrapper(bool configureLimits)
    {
        using var files = new FileFixture(EnabledDeclaration);
        var provider = new CountingSecretProvider();
        var environment = new TestEnvironmentProvider();
        using var host = CreateHost(files.Location, [provider], services =>
        {
            RegisterKnownConfig(services);
            if (configureLimits)
            {
                services.Configure<AppSurfaceConfigOptions>(options =>
                {
                    options.ProviderlessResolutionBudget = TimeSpan.FromSeconds(1);
                    options.MaxCompositionGraphDepth = 8;
                    options.MaxCompositionGraphNodes = 32;
                    options.MaxSecretDestinationsPerRoot = 8;
                });
            }
        }, environment);
        var tracker = host.Services.GetRequiredService<ConstructionTracker>();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        await host.StartAsync();

        Assert.True(lifetime.ApplicationStarted.IsCancellationRequested);
        Assert.Equal(1, provider.ValidateCalls);
        Assert.Equal(0, provider.ResolveCalls);
        Assert.Equal(0, tracker.Count);
        Assert.Contains("PRODUCTION_SERVICE", environment.Lookups);

        // Resolve the registered wrapper only after readiness to prove the activation counter is live.
        var wrapper = host.Services.GetRequiredService<LazyStartupConfig<StartupOptions>>();
        Assert.Equal("resolved-payload", Assert.IsType<StartupOptions>(wrapper.Value).ApiKey.Value);
        Assert.Equal(1, tracker.Count);
        Assert.Equal(1, provider.ResolveCalls);
        Assert.Equal(1, provider.ValidateCalls);
        await host.StopAsync();
    }

    /// <summary>An earlier hosted service may stop a fast host before ordinary startup services receive their turn.</summary>
    [Fact]
    public async Task StartAsync_EarlierHostedServiceStoppingDuringStartStillValidatesKnownPlan()
    {
        using var files = new FileFixture(EnabledDeclaration);
        var provider = new CountingSecretProvider();
        using var host = CreateHost(files.Location, [provider], services =>
        {
            RegisterKnownConfig(services);
            services.Insert(0, ServiceDescriptor.Singleton<IHostedService, StopApplicationOnStartService>());
        });

        await host.StartAsync();

        Assert.True(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        Assert.Equal(1, provider.ValidateCalls);
        Assert.Equal(0, provider.ResolveCalls);
        await host.StopAsync();
    }

    /// <summary>Every nonpositive host limit is rejected even when no application config is activated.</summary>
    [Theory]
    [InlineData(nameof(AppSurfaceConfigOptions.ProviderlessResolutionBudget), 0)]
    [InlineData(nameof(AppSurfaceConfigOptions.ProviderlessResolutionBudget), -1)]
    [InlineData(nameof(AppSurfaceConfigOptions.MaxCompositionGraphDepth), 0)]
    [InlineData(nameof(AppSurfaceConfigOptions.MaxCompositionGraphDepth), -1)]
    [InlineData(nameof(AppSurfaceConfigOptions.MaxCompositionGraphNodes), 0)]
    [InlineData(nameof(AppSurfaceConfigOptions.MaxCompositionGraphNodes), -1)]
    [InlineData(nameof(AppSurfaceConfigOptions.MaxSecretDestinationsPerRoot), 0)]
    [InlineData(nameof(AppSurfaceConfigOptions.MaxSecretDestinationsPerRoot), -1)]
    public async Task StartAsync_NonpositiveOptionsFailBeforeReadiness(string optionName, int value)
    {
        using var files = new FileFixture("{}");
        var provider = new CountingSecretProvider();
        using var host = CreateHost(files.Location, [provider], services =>
            services.Configure<AppSurfaceConfigOptions>(options => SetLimit(options, optionName, value)));
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains(exception.Failures, failure => failure.Contains(optionName, StringComparison.Ordinal));
        Assert.False(lifetime.ApplicationStarted.IsCancellationRequested);
        Assert.Equal(0, provider.ValidateCalls);
        Assert.Equal(0, provider.ResolveCalls);
    }

    /// <summary>Provider registration errors are eager, including hosts with no known configuration entries.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Provider-A")]
    [InlineData("provider--a")]
    [InlineData("provider-")]
    public async Task StartAsync_InvalidProviderIdFailsBeforeReadiness(string id)
    {
        using var files = new FileFixture("{}");
        var provider = new CountingSecretProvider(id);
        using var host = CreateHost(files.Location, [provider]);

        var exception = await Assert.ThrowsAsync<ConfigurationCompositionException>(() => host.StartAsync());

        Assert.Equal("configuration", exception.RootKey);
        Assert.Equal("secret-provider-id-invalid", Assert.Single(exception.Failures).Code);
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
        Assert.Equal(0, provider.ValidateCalls);
        Assert.Equal(0, provider.ResolveCalls);
    }

    /// <summary>Duplicate provider identities fail before validating any reference or fetching a payload.</summary>
    [Fact]
    public async Task StartAsync_DuplicateProviderIdsFailBeforeKnownPlansOrRemoteCalls()
    {
        using var files = new FileFixture(EnabledDeclaration);
        var first = new CountingSecretProvider();
        var second = new CountingSecretProvider();
        using var host = CreateHost(files.Location, [first, second], RegisterKnownConfig);

        var exception = await Assert.ThrowsAsync<ConfigurationCompositionException>(() => host.StartAsync());

        Assert.Equal("secret-provider-id-duplicate", Assert.Single(exception.Failures).Code);
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
        Assert.Equal(0, first.ValidateCalls + second.ValidateCalls);
        Assert.Equal(0, first.ResolveCalls + second.ResolveCalls);
        Assert.Equal(0, host.Services.GetRequiredService<ConstructionTracker>().Count);
    }

    /// <summary>Malformed file policy cannot be postponed until the corresponding Config&lt;T&gt; is requested.</summary>
    [Theory]
    [InlineData("""{"Service":{"ApiKey":{}}}""", "secret-descriptor-incomplete")]
    [InlineData("""{"Service":{"ApiKey":"plaintext"}}""", "secret-descriptor-invalid")]
    public async Task StartAsync_InvalidKnownFilePlanFailsBeforeReadiness(string document, string code)
    {
        using var files = new FileFixture(document);
        var provider = new CountingSecretProvider();
        using var host = CreateHost(files.Location, [provider], RegisterKnownConfig);

        var exception = await Assert.ThrowsAsync<ConfigurationCompositionException>(() => host.StartAsync());

        Assert.Equal("Production", exception.EnvironmentName);
        Assert.Equal("Service", exception.RootKey);
        var failure = Assert.Single(exception.Failures);
        Assert.Equal(code, failure.Code);
        Assert.Equal("Service:ApiKey", failure.Path);
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
        Assert.Equal(0, provider.ValidateCalls);
        Assert.Equal(0, provider.ResolveCalls);
        Assert.Equal(0, host.Services.GetRequiredService<ConstructionTracker>().Count);
    }

    /// <summary>Disabled references still require valid local provider syntax, but never fetch payloads.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartAsync_DisabledReferenceUsesOnlyLocalProviderValidation(bool valid)
    {
        using var files = new FileFixture(
            """{"Service":{"ApiKey":{"key":"opaque-resource","provider":"provider-a","enabled":false}}}""");
        var provider = new CountingSecretProvider
        {
            Validation = _ => valid
                ? ConfigSecretReferenceValidation.Supported()
                : ConfigSecretReferenceValidation.Invalid()
        };
        using var host = CreateHost(files.Location, [provider], RegisterKnownConfig);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        if (valid)
        {
            await host.StartAsync();
            Assert.True(lifetime.ApplicationStarted.IsCancellationRequested);
            Assert.Equal(0, host.Services.GetRequiredService<ConstructionTracker>().Count);

            var value = host.Services.GetRequiredService<IConfigManager>().GetValue<StartupOptions>("Production", "Service");
            var secret = Assert.IsType<StartupOptions>(value).ApiKey;
            Assert.False(secret.Enabled);
            Assert.False(secret.HasValue);
            await host.StopAsync();
        }
        else
        {
            var exception = await Assert.ThrowsAsync<ConfigurationCompositionException>(() => host.StartAsync());
            Assert.Equal("secret-reference-invalid", Assert.Single(exception.Failures).Code);
            Assert.False(lifetime.ApplicationStarted.IsCancellationRequested);
        }

        Assert.Equal(1, provider.ValidateCalls);
        Assert.Equal(0, provider.ResolveCalls);
        Assert.Equal(0, host.Services.GetRequiredService<ConstructionTracker>().Count);
    }

    /// <summary>A locally rejected enabled reference also fails startup without contacting the remote store.</summary>
    [Fact]
    public async Task StartAsync_InvalidEnabledReferenceFailsBeforeReadiness()
    {
        using var files = new FileFixture(EnabledDeclaration);
        var provider = new CountingSecretProvider { Validation = _ => ConfigSecretReferenceValidation.Invalid() };
        using var host = CreateHost(files.Location, [provider], RegisterKnownConfig);

        var exception = await Assert.ThrowsAsync<ConfigurationCompositionException>(() => host.StartAsync());

        Assert.Equal("secret-reference-invalid", Assert.Single(exception.Failures).Code);
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
        Assert.Equal(1, provider.ValidateCalls);
        Assert.Equal(0, provider.ResolveCalls);
        Assert.Equal(0, host.Services.GetRequiredService<ConstructionTracker>().Count);
    }

    /// <summary>A complete environment root bypasses file initialization and all secret provider callbacks.</summary>
    [Fact]
    public async Task StartAsync_DirectEnvironmentRootBypassesFileAndSecretReads()
    {
        var provider = new CountingSecretProvider();
        var environment = new TestEnvironmentProvider(new Dictionary<string, string?>
        {
            ["PRODUCTION_SERVICE"] = """{"ApiKey":"from-environment"}"""
        });
        var location = A.Fake<IConfigFileLocationProvider>();
        A.CallTo(() => location.Directory).Throws(new InvalidOperationException("File provider must remain lazy."));
        using var host = CreateHost(location, [provider], RegisterKnownConfig, environment);

        await host.StartAsync();
        var value = host.Services.GetRequiredService<IConfigManager>().GetValue<StartupOptions>("Production", "Service");

        Assert.True(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
        Assert.Equal("from-environment", Assert.IsType<StartupOptions>(value).ApiKey.Value);
        A.CallTo(() => location.Directory).MustNotHaveHappened();
        Assert.Equal(0, provider.ValidateCalls);
        Assert.Equal(0, provider.ResolveCalls);
        Assert.Equal(0, host.Services.GetRequiredService<ConstructionTracker>().Count);
        await host.StopAsync();
    }

    /// <summary>Discovery records a ConfigStruct&lt;T&gt; entry's struct value type, which composition rejects.</summary>
    [Fact]
    public async Task StartAsync_ConfigStructValueTypeIsRejectedLocally()
    {
        using var files = new FileFixture(EnabledDeclaration);
        var provider = new CountingSecretProvider();
        using var host = CreateHost(files.Location, [provider], services =>
            services.AddSingleton(new ConfigAuditKnownEntry(
                "Service", typeof(ConfigStruct<StartupStruct>), typeof(StartupStruct))));

        var exception = await Assert.ThrowsAsync<ConfigurationCompositionException>(() => host.StartAsync());

        var failure = Assert.Single(exception.Failures);
        Assert.Equal("secret-destination-type-unsupported", failure.Code);
        Assert.Equal("Service", failure.Path);
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
        Assert.Equal(0, provider.ValidateCalls);
        Assert.Equal(0, provider.ResolveCalls);
    }

    /// <summary>One cached plan serves startup, runtime and audit, while each effective read fetches its own payload.</summary>
    [Fact]
    public async Task Services_RuntimeAndAuditReuseStartupCompositionPlan()
    {
        using var files = new FileFixture(EnabledDeclaration);
        var provider = new CountingSecretProvider();
        using var host = CreateHost(files.Location, [provider], services =>
            services.AddSingleton(new ConfigAuditKnownEntry("Service", null, typeof(StartupOptions))));

        await host.StartAsync();
        Assert.Equal(1, provider.ValidateCalls);
        Assert.Equal(0, provider.ResolveCalls);

        var engine = host.Services.GetRequiredService<ConfigCompositionEngine>();
        using var scope = host.Services.CreateScope();
        Assert.Same(engine, scope.ServiceProvider.GetRequiredService<ConfigCompositionEngine>());
        Assert.Same(provider, Assert.Single(engine.Registry.Providers).Provider);

        // A second compiler would revalidate and reject this reference, exposing a broken DI alias.
        provider.Validation = _ => ConfigSecretReferenceValidation.Invalid();
        var value = host.Services.GetRequiredService<IConfigManager>().GetValue<StartupOptions>("Production", "Service");
        Assert.Equal("resolved-payload", Assert.IsType<StartupOptions>(value).ApiKey.Value);
        Assert.Equal(1, provider.ResolveCalls);

        var report = scope.ServiceProvider.GetRequiredService<IConfigAuditReporter>().GetReport("Production");
        var entry = Assert.Single(report.Entries);
        // Audit calls a file base with a provider patch partially resolved, even when every slot has a value.
        Assert.Equal(ConfigAuditEntryState.PartiallyResolved, entry.State);
        var slot = Assert.Single(entry.Children);
        Assert.Equal("Service.ApiKey", slot.Key);
        Assert.Equal(ConfigAuditEntryState.Resolved, slot.State);
        Assert.Equal(provider.Id, Assert.Single(slot.Sources).ProviderName);
        Assert.DoesNotContain(entry.Diagnostics, diagnostic => diagnostic.Severity == ConfigAuditDiagnosticSeverity.Error);
        Assert.Equal(2, provider.ResolveCalls);
        Assert.Equal(1, provider.ValidateCalls);
        await host.StopAsync();
    }

    /// <summary>Only module registration runs here; selected known entries replace the deferred assembly scan.</summary>
    private static IHost CreateHost(
        IConfigFileLocationProvider location,
        IEnumerable<IConfigSecretProvider> providers,
        Action<IServiceCollection>? configure = null,
        IEnvironmentProvider? environment = null) =>
        new HostBuilder()
            .UseEnvironment("Production")
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices((_, services) =>
            {
                var context = new StartupContext([], A.Fake<IAppSurfaceHostModule>());
                new AppSurfaceConfigModule().ConfigureServices(context, services);
                services.AddSingleton<IEnvironmentProvider>(environment ?? new TestEnvironmentProvider());
                services.AddSingleton(location);
                foreach (var provider in providers)
                {
                    services.AddSingleton(provider);
                }

                configure?.Invoke(services);
            })
            .Build();

    /// <summary>Mirrors the module's wrapper factory with an instance counter isolated to this host.</summary>
    private static void RegisterKnownConfig(IServiceCollection services)
    {
        services.AddSingleton<ConstructionTracker>();
        services.AddSingleton(new ConfigAuditKnownEntry(
            "Service", typeof(LazyStartupConfig<StartupOptions>), typeof(StartupOptions)));
        services.AddSingleton(sp =>
        {
            var wrapper = new LazyStartupConfig<StartupOptions>(sp.GetRequiredService<ConstructionTracker>());
            ((IConfig)wrapper).Init(
                sp.GetRequiredService<IConfigManager>(), sp.GetRequiredService<IEnvironmentProvider>(), "Service");
            return wrapper;
        });
    }

    private static void SetLimit(AppSurfaceConfigOptions options, string name, int value)
    {
        switch (name)
        {
            case nameof(AppSurfaceConfigOptions.ProviderlessResolutionBudget):
                options.ProviderlessResolutionBudget = TimeSpan.FromMilliseconds(value);
                break;
            case nameof(AppSurfaceConfigOptions.MaxCompositionGraphDepth):
                options.MaxCompositionGraphDepth = value;
                break;
            case nameof(AppSurfaceConfigOptions.MaxCompositionGraphNodes):
                options.MaxCompositionGraphNodes = value;
                break;
            case nameof(AppSurfaceConfigOptions.MaxSecretDestinationsPerRoot):
                options.MaxSecretDestinationsPerRoot = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(name));
        }
    }

    private sealed class StartupOptions
    {
        public Secret<string> ApiKey { get; init; } = new();
    }

    private struct StartupStruct
    {
        public Secret<string> ApiKey { get; init; }
    }

    private sealed class ConstructionTracker
    {
        public int Count { get; set; }
    }

    private sealed class StopApplicationOnStartService(IHostApplicationLifetime lifetime) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            lifetime.StopApplication();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // Keep this generic definition undiscoverable by unrelated tests that scan the entire test assembly.
    private sealed class LazyStartupConfig<T> : Config<T> where T : class
    {
        public LazyStartupConfig(ConstructionTracker tracker)
        {
            tracker.Count++;
        }
    }

    /// <summary>Owns real on-disk declaration policy and cleans up only its uniquely named directory.</summary>
    private sealed class FileFixture : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("appsurface-startup-").FullName;

        public IConfigFileLocationProvider Location { get; } = A.Fake<IConfigFileLocationProvider>();

        public FileFixture(string document)
        {
            File.WriteAllText(Path.Combine(_directory, "appsettings.json"), document);
            A.CallTo(() => Location.Directory).Returns(_directory);
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private sealed class TestEnvironmentProvider(
        IReadOnlyDictionary<string, string?>? values = null) : IEnvironmentProvider
    {
        public string Environment => "Production";
        public bool IsDevelopment => false;
        public List<string> Lookups { get; } = [];

        public string? GetEnvironmentVariable(string name, string? defaultValue = null)
        {
            Lookups.Add(name);
            return values is not null && values.TryGetValue(name, out var value) ? value : defaultValue;
        }
    }

    /// <summary>Separates local syntax checks from the remote-read boundary without accessing private state.</summary>
    private sealed class CountingSecretProvider(string id = "provider-a") : IConfigSecretProvider
    {
        public string Id => id;
        public int ValidateCalls { get; private set; }
        public int ResolveCalls { get; private set; }
        public Func<ConfigSecretReference, ConfigSecretReferenceValidation> Validation { get; set; } =
            _ => ConfigSecretReferenceValidation.Supported();

        public ConfigSecretReferenceValidation ValidateReference(ConfigSecretReference reference)
        {
            ValidateCalls++;
            return Validation(reference);
        }

        public ConfigSecretProviderResolution Resolve(ConfigSecretReference reference, ConfigSecretResolutionContext context)
        {
            ResolveCalls++;
            return ConfigSecretProviderResolution.Resolved("resolved-payload", ConfigSecretSourceMetadata.Create(Id));
        }
    }
}
