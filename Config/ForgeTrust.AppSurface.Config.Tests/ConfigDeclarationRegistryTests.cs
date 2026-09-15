using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigDeclarationRegistryTests
{
    private sealed class WrappedConfig : Config<string>
    {
    }

    private sealed class SecondWrappedConfig : Config<string>
    {
    }

    [Fact]
    public void Parser_RecordsTranslatedDotInputWithoutChangingIdentity()
    {
        var parser = CreateParser(LegacyDotPathBehavior.TranslateDotOnlyWithDiagnostic);

        var key = parser.Parse("Service.Endpoint");

        Assert.Equal("Service:Endpoint", key.Value);
        Assert.Equal(ConfigKeyInputOrigin.TranslatedDot, key.InputOrigin);
        Assert.Equal("Service.Endpoint", key.OriginalInput);
    }

    [Fact]
    public void Parser_StrictModePreservesDotsAsLiteralSegments()
    {
        var parser = CreateParser(LegacyDotPathBehavior.Strict);

        var key = parser.Parse("Service.Endpoint");

        Assert.Equal("Service.Endpoint", key.Value);
        Assert.Equal(ConfigKeyInputOrigin.StrictString, key.InputOrigin);
        Assert.Equal("Service.Endpoint", key.OriginalInput);
    }

    [Fact]
    public void Registry_MergesExactCanonicalDuplicatesAndKeepsWrapperMetadata()
    {
        var key = AppSurfaceConfigKey.FromSegments("Service", "Endpoint");
        var wrapper = new ConfigAuditKnownEntry(key, typeof(WrappedConfig), typeof(string));
        var manualOptions = new ConfigAuditEntryOptions { Sensitivity = ConfigAuditSensitivity.Sensitive };
        var manual = new ConfigAuditRawDeclaration(
            RawKey: "Service:Endpoint",
            ConfigType: null,
            ValueType: typeof(string),
            Options: manualOptions,
            IsAttributeDeclaration: false);

        var registry = new ConfigDeclarationRegistry(
            [wrapper],
            [manual],
            CreateParser(LegacyDotPathBehavior.TranslateDotOnlyWithDiagnostic));

        var entry = Assert.Single(registry.Entries);
        Assert.Equal(typeof(WrappedConfig), entry.ConfigType);
        Assert.Equal(ConfigAuditSensitivity.Sensitive, entry.Options.Sensitivity);
        Assert.Same(key, registry.Keys[0]);
        Assert.Same(entry, registry.GetForConfigType(typeof(WrappedConfig)));
    }

    [Fact]
    public void Registry_MapsEveryWrapperSharingAnExactKeyToTheMergedEntry()
    {
        var key = AppSurfaceConfigKey.FromSegments("Shared", "Value");
        var first = new ConfigAuditKnownEntry(key, typeof(WrappedConfig), typeof(string));
        var second = new ConfigAuditKnownEntry(key, typeof(SecondWrappedConfig), typeof(string));

        var registry = new ConfigDeclarationRegistry([first, second], [], CreateParser(LegacyDotPathBehavior.Strict));

        Assert.Single(registry.Entries);
        Assert.Same(registry.Entries[0], registry.GetForConfigType(typeof(WrappedConfig)));
        Assert.Same(registry.Entries[0], registry.GetForConfigType(typeof(SecondWrappedConfig)));
    }

    [Fact]
    public void Registry_LaterWrapperPreservesEarlierManualOverrides()
    {
        var key = AppSurfaceConfigKey.Parse("Shared:Value");
        var manual = new ConfigAuditKnownEntry(key, null, typeof(string),
            new ConfigAuditEntryOptions { MaxCollectionElements = 1, Sensitivity = ConfigAuditSensitivity.Sensitive });
        var wrapper = new ConfigAuditKnownEntry(key, typeof(WrappedConfig), typeof(string),
            new ConfigAuditEntryOptions { TraverseCollectionElements = true, MaxCollectionElements = 4 });
        var registry = new ConfigDeclarationRegistry([manual, wrapper], [], CreateParser(LegacyDotPathBehavior.Strict));

        var merged = Assert.Single(registry.Entries);
        Assert.Equal(typeof(WrappedConfig), merged.ConfigType);
        Assert.True(merged.Options.TraverseCollectionElements);
        Assert.Equal(1, merged.Options.MaxCollectionElements);
        Assert.Equal(ConfigAuditSensitivity.Sensitive, merged.Options.Sensitivity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Registry_RejectsLegacyAndStrictCollisionRegardlessOfDeclarationOrder(bool legacyFirst)
    {
        var parser = CreateParser(LegacyDotPathBehavior.TranslateDotOnlyWithDiagnostic);
        var strict = new ConfigAuditKnownEntry(parser.Parse("Service:Endpoint"), null, typeof(string));
        var legacy = new ConfigAuditKnownEntry(parser.Parse("Service.Endpoint"), null, typeof(string));

        Assert.Throws<InvalidOperationException>(() => new ConfigDeclarationRegistry(
            legacyFirst ? [legacy, strict] : [strict, legacy], [], parser));
    }

    [Fact]
    public void KnownEntry_DeprecatedStringConstructorUsesStrictIdentity()
    {
#pragma warning disable CS0618 // The constructor compatibility contract intentionally uses the deprecated string API.
        var entry = new ConfigAuditKnownEntry("Literal.Dot:Leaf", null, typeof(string));
#pragma warning restore CS0618

        Assert.Equal("Literal.Dot:Leaf", entry.LogicalKey.Value);
        Assert.Equal(ConfigKeyInputOrigin.StrictString, entry.LogicalKey.InputOrigin);
        Assert.Equal("Literal.Dot:Leaf", entry.LogicalKey.OriginalInput);
        Assert.Equal<string>(["Literal.Dot", "Leaf"], entry.LogicalKey.Segments);
    }

    [Fact]
    public void Registry_RejectsCaseOnlyCollision()
    {
        var first = new ConfigAuditKnownEntry(
            AppSurfaceConfigKey.Parse("Service:Endpoint"), null, typeof(string));
        var second = new ConfigAuditKnownEntry(
            AppSurfaceConfigKey.Parse("service:endpoint"), null, typeof(string));

        var exception = Assert.Throws<InvalidOperationException>(() => new ConfigDeclarationRegistry(
            [first, second],
            [],
            CreateParser(LegacyDotPathBehavior.Strict)));

        Assert.Contains("config-key-collision", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Service:Endpoint", exception.Message, StringComparison.Ordinal);
        Assert.Contains("service:endpoint", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_RejectsLegacyCanonicalCollision()
    {
        var canonical = new ConfigAuditKnownEntry(
            AppSurfaceConfigKey.Parse("Service:Endpoint"), null, typeof(string));
        var legacy = new ConfigAuditRawDeclaration(
            RawKey: "Service.Endpoint",
            ConfigType: null,
            ValueType: typeof(string),
            Options: new ConfigAuditEntryOptions(),
            IsAttributeDeclaration: false);

        var exception = Assert.Throws<InvalidOperationException>(() => new ConfigDeclarationRegistry(
            [canonical],
            [legacy],
            CreateParser(LegacyDotPathBehavior.TranslateDotOnlyWithDiagnostic)));

        Assert.Contains("config-key-collision", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Service.Endpoint", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Service:Endpoint", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_RejectsDistinctTranslatedSpellingsForOneIdentity()
    {
        var first = new ConfigAuditRawDeclaration(
            RawKey: "Service.Endpoint",
            ConfigType: null,
            ValueType: typeof(string),
            Options: new ConfigAuditEntryOptions(),
            IsAttributeDeclaration: false);
        var second = new ConfigAuditRawDeclaration(
            RawKey: "service.endpoint",
            ConfigType: null,
            ValueType: typeof(string),
            Options: new ConfigAuditEntryOptions(),
            IsAttributeDeclaration: false);

        var exception = Assert.Throws<InvalidOperationException>(() => new ConfigDeclarationRegistry(
            [],
            [first, second],
            CreateParser(LegacyDotPathBehavior.TranslateDotOnlyWithDiagnostic)));

        Assert.Contains("config-key-collision", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_ResolvesRawDeclarationsWithOptionsFinalizedAfterCapture()
    {
        var services = new ServiceCollection();
        services.AddConfigAuditKey<string>("Late.Option");
        services.PostConfigure<AppSurfaceConfigKeyOptions>(options =>
            options.LegacyDotPathBehavior = LegacyDotPathBehavior.Strict);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<ConfigDeclarationRegistry>();

        var entry = Assert.Single(registry.Entries);
        Assert.Equal("Late.Option", entry.Key);
        Assert.Equal(ConfigKeyInputOrigin.StrictString, entry.LogicalKey.InputOrigin);
    }

    [Fact]
    public void AddConfigAuditKey_RegistersStandaloneDeclarationInfrastructure()
    {
        var services = new ServiceCollection();
        services.AddConfigAuditKey<string>("Manual.Option");
        services.AddConfigAuditKey<string>(AppSurfaceConfigKey.Parse("Typed:Option"));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<ConfigDeclarationRegistry>();

        Assert.Equal(["Manual:Option", "Typed:Option"], registry.Keys.Select(key => key.Value));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IConfigKeyInputParser));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ConfigDeclarationRegistry));
    }

    [Fact]
    public void Registry_IsolatedBetweenHostsWithDifferentStringPolicies()
    {
        using var strict = CreateHost("Host.Option", LegacyDotPathBehavior.Strict);
        using var translated = CreateHost("Host.Option", LegacyDotPathBehavior.TranslateDotOnlyWithDiagnostic);

        Assert.Equal("Host.Option", strict.GetRequiredService<ConfigDeclarationRegistry>().Keys[0].Value);
        Assert.Equal("Host:Option", translated.GetRequiredService<ConfigDeclarationRegistry>().Keys[0].Value);
    }

    [Fact]
    public async Task Registry_ConcurrentFirstAccessReturnsOneImmutableInstance()
    {
        var services = new ServiceCollection();
        var parser = new CountingParser();
        services.AddSingleton<IConfigKeyInputParser>(parser);
        var expected = new List<string>();
        for (var index = 0; index < 32; index++)
        {
            services.AddConfigAuditKey<string>($"Concurrent.Raw{index}");
            var typed = AppSurfaceConfigKey.FromSegments("Concurrent", $"Typed{index}");
            services.AddConfigAuditKey<string>(typed);
            expected.Add($"Concurrent:Raw{index}");
            expected.Add(typed.Value);
        }

        using var provider = services.BuildServiceProvider();

        var registries = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => provider.GetRequiredService<ConfigDeclarationRegistry>())));

        Assert.All(registries, registry => Assert.Same(registries[0], registry));
        Assert.All(registries, registry => Assert.Equal(
            expected.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ThenBy(key => key, StringComparer.Ordinal),
            registry.Keys.Select(key => key.Value)));
        Assert.Equal(32, parser.ParseCalls);
        Assert.Single(provider.GetServices<IConfigKeyInputParser>());
    }

    private sealed class CountingParser : IConfigKeyInputParser
    {
        private readonly IConfigKeyInputParser _inner = CreateParser(LegacyDotPathBehavior.TranslateDotOnlyWithDiagnostic);
        private int _parseCalls;
        public int ParseCalls => _parseCalls;

        public AppSurfaceConfigKey Parse(string key)
        {
            Interlocked.Increment(ref _parseCalls);
            return _inner.Parse(key);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupMarker_RejectsManualOnlyCollisionBeforeProviderTraversal(bool typed)
    {
        var providerActivated = false;
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                if (typed)
                {
                    services.AddConfigAuditKey<string>(AppSurfaceConfigKey.Parse("Manual:Option"));
                    services.AddConfigAuditKey<string>(AppSurfaceConfigKey.Parse("manual:option"));
                }
                else
                {
                    services.AddConfigAuditKey<string>("Manual.Option");
                    services.AddConfigAuditKey<string>("manual.option");
                }
                services.AddSingleton<IHostedService>(_ => new ProviderTraversalProbe(() => providerActivated = true));
            })
            .Build();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync());
        Assert.Contains("config-key-collision", exception.ToString(), StringComparison.Ordinal);
        Assert.False(providerActivated);
    }

    [Fact]
    public async Task StartupMarker_AcceptsManualOnlyDeclarationsWithoutModuleServices()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services => services.AddConfigAuditKey<string>("Manual.Option"))
            .Build();

        await host.StartAsync();

        Assert.Equal("Manual:Option", Assert.Single(host.Services
            .GetRequiredService<ConfigDeclarationRegistry>().Keys).Value);
        await host.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Registry_ManualOptionOverridesFollowRegistrationOrderAcrossTypedAndStringDeclarations(bool typedFirst)
    {
        var services = new ServiceCollection();
        var key = AppSurfaceConfigKey.Parse("Ordered:Value");
        if (typedFirst)
        {
            services.AddConfigAuditKey<string>(key, options => options.MaxCollectionElements = 2);
            services.AddConfigAuditKey<string>(key.Value, options => options.MaxCollectionElements = 3);
        }
        else
        {
            services.AddConfigAuditKey<string>(key.Value, options => options.MaxCollectionElements = 2);
            services.AddConfigAuditKey<string>(key, options => options.MaxCollectionElements = 3);
        }

        using var provider = services.BuildServiceProvider();

        Assert.Equal(3, Assert.Single(provider.GetRequiredService<ConfigDeclarationRegistry>().Entries)
            .Options.MaxCollectionElements);
    }

    [Fact]
    public void Registry_SortsAndExposesReadOnlySnapshots()
    {
        var services = new ServiceCollection();
        services.AddConfigAuditKey<string>(AppSurfaceConfigKey.Parse("Zulu"));
        services.AddConfigAuditKey<string>(AppSurfaceConfigKey.Parse("alpha"));
        services.AddConfigAuditKey<string>(AppSurfaceConfigKey.Parse("Beta"));
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<ConfigDeclarationRegistry>();

        Assert.Equal(["alpha", "Beta", "Zulu"], registry.Keys.Select(key => key.Value));
        Assert.Throws<NotSupportedException>(() => ((IList<AppSurfaceConfigKey>)registry.Keys).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ConfigAuditKnownEntry>)registry.Entries).Clear());
        Assert.Throws<InvalidOperationException>(() => registry.GetForConfigType(typeof(WrappedConfig)));
    }

    [Fact]
    public void Registry_SnapshotsManualOptionsAtRegistration()
    {
        var services = new ServiceCollection();
        ConfigAuditEntryOptionsBuilder? captured = null;
        services.AddConfigAuditKey<string>("Snapshot", options =>
        {
            captured = options;
            options.MaxCollectionElements = 2;
        });
        captured!.MaxCollectionElements = 99;
        using var provider = services.BuildServiceProvider();

        Assert.Equal(2, Assert.Single(provider.GetRequiredService<ConfigDeclarationRegistry>().Entries)
            .Options.MaxCollectionElements);
    }

    [Fact]
    public void AddConfigAuditKey_RejectsInvalidArgumentsAndDefersGrammarValidation()
    {
        var services = new ServiceCollection();
        var key = AppSurfaceConfigKey.Parse("Key");
        Assert.Throws<ArgumentNullException>(() => ConfigAuditServiceCollectionExtensions
            .AddConfigAuditKey<string>(null!, key));
        Assert.Throws<ArgumentNullException>(() => ConfigAuditServiceCollectionExtensions
            .AddConfigAuditKey<string>(null!, "Key"));
        Assert.Throws<ArgumentNullException>(() => services.AddConfigAuditKey<string>((AppSurfaceConfigKey)null!));
        Assert.Throws<ArgumentException>(() => services.AddConfigAuditKey<string>(" "));
        services.AddConfigAuditKey<string>("Invalid::Key");
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<ArgumentException>(() => provider.GetRequiredService<ConfigDeclarationRegistry>());
        Assert.Equal("key", exception.ParamName);
    }

    [Fact]
    public void Registry_RejectsDifferentTranslatedOriginalsEvenWhenCanonicalSpellingMatches()
    {
        var key = AppSurfaceConfigKey.Parse("Service:Endpoint:Other:Leaf");
        var first = key.WithInput(ConfigKeyInputOrigin.TranslatedDot, "Service.Endpoint.Other:Leaf");
        var second = key.WithInput(ConfigKeyInputOrigin.TranslatedDot, "Service.Endpoint.Other.Leaf");

        var exception = Assert.Throws<InvalidOperationException>(() => new ConfigDeclarationRegistry(
            [new(first, null, typeof(string)), new(second, null, typeof(string))],
            [], CreateParser(LegacyDotPathBehavior.Strict)));

        Assert.Contains("config-key-collision", exception.Message, StringComparison.Ordinal);
        Assert.Contains(ConfigDiagnosticCatalog.Reference, exception.Message, StringComparison.Ordinal);
    }

    private sealed class ProviderTraversalProbe : IHostedService
    {
        private readonly Action _onStart;

        public ProviderTraversalProbe(Action onStart) => _onStart = onStart;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _onStart();
            throw new Xunit.Sdk.XunitException("Provider traversal ran before declaration validation.");
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static IConfigKeyInputParser CreateParser(LegacyDotPathBehavior behavior) =>
        new ConfigKeyInputParser(Options.Create(new AppSurfaceConfigKeyOptions
        {
            LegacyDotPathBehavior = behavior
        }));

    private static ServiceProvider CreateHost(string rawKey, LegacyDotPathBehavior behavior)
    {
        var services = new ServiceCollection();
        services.AddOptions<AppSurfaceConfigKeyOptions>()
            .Configure(options => options.LegacyDotPathBehavior = behavior);
        services.AddSingleton<IConfigKeyInputParser, ConfigKeyInputParser>();
        services.AddSingleton(new ConfigAuditRawDeclaration(
            RawKey: rawKey,
            ConfigType: null,
            ValueType: typeof(string),
            Options: new ConfigAuditEntryOptions(),
            IsAttributeDeclaration: false));
        services.AddSingleton<ConfigDeclarationRegistry>();
        return services.BuildServiceProvider();
    }
}
