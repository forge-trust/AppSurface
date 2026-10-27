using System.Collections.Concurrent;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class EnvironmentConfigCandidateLifecycleTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    // protects=shared finalized mappings in both orders; fails_when=second consumer refreezes changed options; no_claim=concurrent options mutation.
    public void InspectorAndProviderShareOneFrozenConfigurationInEitherResolutionOrder(bool inspectorFirst)
    {
        var services = new ServiceCollection();
        var options = new AppSurfaceEnvironmentConfigOptions().MapKey("A", "First");
        services.AddSingleton<IEnvironmentProvider>(new FixedEnvironment("Production", new Dictionary<string, string>
        {
            ["PRODUCTION__First"] = "11",
            ["PRODUCTION__Later"] = "22",
        }));
        services.Configure<AppSurfaceEnvironmentConfigOptions>(configured =>
        {
            foreach (var pair in options.Snapshot()) configured.MapKey(pair.Key, pair.Value);
            configured.MaxAdHocClaims = 1;
        });
        new AppSurfaceConfigModule().ConfigureServices(new StartupContext([], new TestHostModule()), services);
        using var provider = services.BuildServiceProvider();

        IEnvironmentConfigCandidateInspector inspector;
        IEnvironmentConfigProvider environmentProvider;
        if (inspectorFirst)
        {
            inspector = provider.GetRequiredService<IEnvironmentConfigCandidateInspector>();
            provider.GetRequiredService<IOptions<AppSurfaceEnvironmentConfigOptions>>().Value.MapKey("A", "Later");
            environmentProvider = provider.GetRequiredService<IEnvironmentConfigProvider>();
        }
        else
        {
            environmentProvider = provider.GetRequiredService<IEnvironmentConfigProvider>();
            provider.GetRequiredService<IOptions<AppSurfaceEnvironmentConfigOptions>>().Value.MapKey("A", "Later");
            inspector = provider.GetRequiredService<IEnvironmentConfigCandidateInspector>();
        }
        var plan = inspector.InspectNativeKey("Production", "A");
        Assert.Equal(new[] { "PRODUCTION__First", "First" }, plan.Candidates.Select(candidate => candidate.Name));
        Assert.Equal(11, environmentProvider.Resolve<int>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("A"))).Value);
        Assert.Same(inspector, provider.GetRequiredService<IEnvironmentConfigCandidateInspector>());
        Assert.Same(environmentProvider, provider.GetRequiredService<IEnvironmentConfigProvider>());
    }

    [Fact]
    // protects=host isolation and direct seam; fails_when=separate hosts share mappings; no_claim=global identity.
    public void SeparateHostsFreezeIndependentMappingsAndDirectProviderSeamStillWorks()
    {
        using var first = Build("First");
        using var second = Build("Second");
        var firstInspector = first.GetRequiredService<IEnvironmentConfigCandidateInspector>();
        var secondInspector = second.GetRequiredService<IEnvironmentConfigCandidateInspector>();
        Assert.Equal("First", firstInspector.InspectNativeKey("Prod", "A").Candidates[0].Name.Split("__")[1]);
        Assert.Equal("Second", secondInspector.InspectNativeKey("Prod", "A").Candidates[0].Name.Split("__")[1]);

        var options = Options.Create(new AppSurfaceEnvironmentConfigOptions().MapKey("A", "Direct").MapKey("B", "Other"));
        var provider = new EnvironmentConfigProvider(new FixedEnvironment("Prod", new Dictionary<string, string> { ["PROD__Direct"] = "17" }), options);
        Assert.Equal("Prod", provider.Environment);
        Assert.Equal(17, provider.Resolve<int>(new ConfigProviderRequest("Prod", AppSurfaceConfigKey.Parse("A"))).Value);

        var sharedOptions = new AppSurfaceEnvironmentConfigOptions { MaxAdHocClaims = 1 }.MapKey("A", "Shared");
        var sharedSnapshot = new EnvironmentConfigMappingSnapshot(sharedOptions);
        var sharedInspector = new EnvironmentConfigCandidateInspector(sharedSnapshot);
        var sharedEnvironmentProvider = new EnvironmentConfigProvider(
            new FixedEnvironment("Prod", new Dictionary<string, string> { ["PROD__Shared"] = "29" }), sharedSnapshot);
        sharedOptions.MapKey("A", "Mutated");
        Assert.Equal("PROD__Shared", sharedInspector.InspectNativeKey("Prod", "A").Candidates[0].Name);
        Assert.Equal(29, sharedEnvironmentProvider.Resolve<int>(new ConfigProviderRequest("Prod", AppSurfaceConfigKey.Parse("A"))).Value);
    }

    [Fact]
    // protects=unchanged tiny native claim capacity; fails_when=inspection reserves any claim; no_claim=unlimited resolver claims.
    public void RepeatedValidAndRejectedInspectionsDoNotConsumeNativeClaimCapacity()
    {
        var source = new FixedEnvironment("Production");
        var settings = Options.Create(new AppSurfaceEnvironmentConfigOptions { MaxAdHocClaims = 1 });
        var provider = new EnvironmentConfigProvider(source, settings);
        var inspector = new EnvironmentConfigCandidateInspector(new EnvironmentConfigMappingSnapshot(settings.Value));

        for (var index = 0; index < 20; index++)
        {
            Assert.Equal("PRODUCTION__A", inspector.InspectNativeKey("Production", "A").Candidates[0].Name);
            Assert.Throws<FormatException>(() => inspector.InspectNativeKey("Production", "A::B"));
        }

        Assert.Equal(ConfigProviderValueStatus.Missing, provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("A"))).Status);
        Assert.Equal("config-environment-claim-limit", provider.Resolve<string>(new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("B"))).Diagnostic!.Code);
    }

    [Fact]
    // protects=parallel immutable inspection without source calls; fails_when=inspection reads or captures environment; no_claim=arbitrary host callbacks.
    public async Task ConcurrentInspectionIsMaterializedAndDoesNotCallEnvironmentSource()
    {
        var source = new ThrowOnReadEnvironment("Production");
        var services = new ServiceCollection();
        services.AddSingleton<IEnvironmentProvider>(source);
        services.Configure<AppSurfaceEnvironmentConfigOptions>(options => options.MapKey("A", "Mapped"));
        new AppSurfaceConfigModule().ConfigureServices(new StartupContext([], new TestHostModule()), services);
        using var provider = services.BuildServiceProvider();
        var inspector = provider.GetRequiredService<IEnvironmentConfigCandidateInspector>();

        var plans = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => inspector.InspectNativeKey("Production", "A"))));
        Assert.All(plans, plan => Assert.Equal(new[] { "PRODUCTION__Mapped", "Mapped" }, plan.Candidates.Select(candidate => candidate.Name)));
        Assert.Equal(0, source.Reads);
        Assert.Equal(0, source.Captures);
    }

    [Fact]
    // protects=source construction and rejection purity; fails_when=inspector activates unrelated services; no_claim=host construction purity.
    public void InspectorConstructionAndRejectedCallsDoNotResolveSourceServices()
    {
        var constructions = new ConcurrentDictionary<Type, int>();
        var services = new ServiceCollection();
        services.AddSingleton<IEnvironmentProvider>(_ => Poison<IEnvironmentProvider>(constructions));
        services.AddSingleton<IConfigProvider>(_ => Poison<IConfigProvider>(constructions));
        services.AddSingleton<IConfigSecretProvider>(_ => Poison<IConfigSecretProvider>(constructions));
        services.AddSingleton<IConfigFileLocationProvider>(_ => Poison<IConfigFileLocationProvider>(constructions));
        services.Configure<AppSurfaceEnvironmentConfigOptions>(_ => { });
        new AppSurfaceConfigModule().ConfigureServices(new StartupContext([], new TestHostModule()), services);
        using var provider = services.BuildServiceProvider();
        var inspector = provider.GetRequiredService<IEnvironmentConfigCandidateInspector>();

        Assert.Throws<ArgumentNullException>(() => inspector.InspectNativeKey(null!, "A"));
        Assert.Throws<ArgumentNullException>(() => inspector.InspectNativeKey("Prod", (string)null!));
        Assert.Throws<FormatException>(() => inspector.InspectNativeKey("Prod", "A::B"));
        Assert.Throws<ArgumentNullException>(() => inspector.InspectCompositionPath("Prod", (AppSurfaceConfigKey)null!));
        Assert.Equal(0, constructions.Values.Sum());
    }

    private static T Poison<T>(ConcurrentDictionary<Type, int> counts) where T : class
    {
        counts.AddOrUpdate(typeof(T), 1, static (_, current) => current + 1);
        throw new InvalidOperationException($"Forbidden source service was activated: {typeof(T).Name}");
    }

    [Fact]
    // protects=one finalized snapshot under concurrent first resolution; fails_when=callbacks rerun or consumers differ; no_claim=concurrent options mutation.
    public async Task ConcurrentFirstResolutionPublishesOneFrozenSnapshot()
    {
        const int callers = 8;
        var configureCalls = 0;
        var postConfigureCalls = 0;
        var services = new ServiceCollection();
        services.AddSingleton<IEnvironmentProvider>(new FixedEnvironment("Production"));
        services.Configure<AppSurfaceEnvironmentConfigOptions>(options =>
        {
            Interlocked.Increment(ref configureCalls);
            options.MapKey("A", "Finalized");
        });
        services.PostConfigure<AppSurfaceEnvironmentConfigOptions>(options =>
        {
            Interlocked.Increment(ref postConfigureCalls);
            options.MaxAdHocClaims = 1;
        });
        new AppSurfaceConfigModule().ConfigureServices(new StartupContext([], new TestHostModule()), services);
        using var provider = services.BuildServiceProvider();
        using var barrier = new Barrier(callers);

        var readers = Enumerable.Range(0, callers).Select(index => Task.Factory.StartNew(() =>
        {
            barrier.SignalAndWait();
            IEnvironmentConfigCandidateInspector inspector;
            IEnvironmentConfigProvider environmentProvider;
            if (index % 2 == 0)
            {
                inspector = provider.GetRequiredService<IEnvironmentConfigCandidateInspector>();
                environmentProvider = provider.GetRequiredService<IEnvironmentConfigProvider>();
            }
            else
            {
                environmentProvider = provider.GetRequiredService<IEnvironmentConfigProvider>();
                inspector = provider.GetRequiredService<IEnvironmentConfigCandidateInspector>();
            }
            return (inspector, environmentProvider, inspector.InspectNativeKey("Production", "A").Candidates[0].Name);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        var results = await Task.WhenAll(readers);

        Assert.All(results, result => Assert.Equal("PRODUCTION__Finalized", result.Name));
        Assert.All(results, result => Assert.Same(results[0].inspector, result.inspector));
        Assert.All(results, result => Assert.Same(results[0].environmentProvider, result.environmentProvider));
        Assert.Equal(1, configureCalls);
        Assert.Equal(1, postConfigureCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    // protects=failed snapshots expose no consumer; fails_when a later attempt succeeds or loses validation; no_claim=provider-first may activate its existing environment dependency.
    public void InvalidFirstSnapshotCannotPublishEitherConsumerOnRepeatedResolution(bool inspectorFirst)
    {
        var sourceActivations = 0;
        var services = new ServiceCollection();
        services.AddSingleton<IEnvironmentProvider>(_ =>
        {
            Interlocked.Increment(ref sourceActivations);
            return new FixedEnvironment("Production");
        });
        services.Configure<AppSurfaceEnvironmentConfigOptions>(options => options.MaxAdHocClaims = 0);
        new AppSurfaceConfigModule().ConfigureServices(new StartupContext([], new TestHostModule()), services);
        using var provider = services.BuildServiceProvider();

        if (inspectorFirst)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var inspectorError = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IEnvironmentConfigCandidateInspector>());
                AssertInvalidCandidateOptions(inspectorError);
                Assert.Equal(0, sourceActivations);
            }
            var providerError = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IEnvironmentConfigProvider>());
            AssertInvalidCandidateOptions(providerError);
            Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IEnvironmentConfigProvider>());
        }
        else
        {
            var providerError = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IEnvironmentConfigProvider>());
            AssertInvalidCandidateOptions(providerError);
            Assert.Equal(1, sourceActivations); // provider-first activation is an existing provider dependency.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var inspectorError = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IEnvironmentConfigCandidateInspector>());
                AssertInvalidCandidateOptions(inspectorError);
                Assert.Equal(1, sourceActivations);
            }
            Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IEnvironmentConfigProvider>());
        }
    }

    private static void AssertInvalidCandidateOptions(OptionsValidationException error)
    {
        Assert.Contains(error.Failures, failure => failure.Contains("MaxAdHocClaims", StringComparison.Ordinal));
        Assert.Equal("config-candidate-options-invalid", error.Data["Code"]);
        Assert.Equal("https://appsurface.dev/guides/config-logical-keys#config-candidate-options-invalid", error.HelpLink);
    }

    [Fact]
    // protects=known framework failures and host exceptions stay distinct; fails_when=validation disappears or host error is certified pure; no_claim=host callback safety.
    public void SnapshotKeepsOptionsValidationAndApplicationCallbackFailuresDistinct()
    {
        var invalids = new[]
        {
            new AppSurfaceEnvironmentConfigOptions { MaxAdHocClaims = 0 },
            new AppSurfaceEnvironmentConfigOptions().MapKey("A", "X").MapKey("a", "Y"),
            new AppSurfaceEnvironmentConfigOptions().MapKey("A", "X").MapKey("B", "x"),
            new AppSurfaceEnvironmentConfigOptions().MapKey("A", "B").MapKey("B", "C"),
        };
        foreach (var invalid in invalids)
        {
            var validation = Assert.Throws<OptionsValidationException>(() => new EnvironmentConfigMappingSnapshot(invalid));
            Assert.NotEmpty(validation.Failures);
            Assert.Equal("config-candidate-options-invalid", validation.Data["Code"]);
            Assert.Equal("https://appsurface.dev/guides/config-logical-keys#config-candidate-options-invalid", validation.HelpLink);
        }

        var applicationFailure = new InvalidOperationException("application configure failure");
        var services = new ServiceCollection();
        services.AddSingleton<IEnvironmentProvider>(new FixedEnvironment("Production"));
        services.PostConfigure<AppSurfaceEnvironmentConfigOptions>(_ => throw applicationFailure);
        new AppSurfaceConfigModule().ConfigureServices(new StartupContext([], new TestHostModule()), services);
        using var provider = services.BuildServiceProvider();
        var observed = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IEnvironmentConfigCandidateInspector>());
        Assert.Same(applicationFailure, observed);
        Assert.Empty(observed.Data);

        var callbackOptionsException = new OptionsValidationException("Options", typeof(AppSurfaceEnvironmentConfigOptions), ["application validation failure"]);
        var callbackServices = new ServiceCollection();
        callbackServices.AddSingleton<IEnvironmentProvider>(new FixedEnvironment("Production"));
        callbackServices.PostConfigure<AppSurfaceEnvironmentConfigOptions>(_ => throw callbackOptionsException);
        new AppSurfaceConfigModule().ConfigureServices(new StartupContext([], new TestHostModule()), callbackServices);
        using var callbackProvider = callbackServices.BuildServiceProvider();
        var callbackObserved = Assert.Throws<OptionsValidationException>(() => callbackProvider.GetRequiredService<IEnvironmentConfigCandidateInspector>());
        Assert.Same(callbackOptionsException, callbackObserved);
        Assert.Empty(callbackObserved.Data);
    }

    // Protects the direct-construction isolation boundary; no global snapshot sharing is promised.
    [Fact]
    // protects=direct isolated snapshot lifecycle; fails_when=manually created provider shares earlier freeze; no_claim=global snapshot identity.
    public void DirectConstructionCanCaptureLaterOptionsWithoutChangingAnExistingSharedSnapshot()
    {
        var options = new AppSurfaceEnvironmentConfigOptions().MapKey("A", "First");
        var snapshot = new EnvironmentConfigMappingSnapshot(options);
        var inspector = new EnvironmentConfigCandidateInspector(snapshot);
        options.MapKey("B", "Later");
        var source = new FixedEnvironment("Prod", new Dictionary<string, string> { ["PROD__Later"] = "37" });
        var direct = new EnvironmentConfigProvider(source, Options.Create(options));
        var shared = new EnvironmentConfigProvider(source, snapshot);
        var key = AppSurfaceConfigKey.Parse("B");
        Assert.Equal(new[] { "PROD__B", "B" }, inspector.InspectNativeKey("Prod", key).Candidates.Select(x => x.Name));
        Assert.Equal(37, direct.Resolve<int>(new ConfigProviderRequest("Prod", key)).Value);
        Assert.Equal(ConfigProviderValueStatus.Missing, shared.Resolve<int>(new ConfigProviderRequest("Prod", key)).Status);
    }

    [Fact]
    // protects=both provider construction seams preserve null dependency guards; fails_when=validation order or argument identity changes; no_claim=inspector diagnostic metadata.
    public void ProviderConstructorsRetainEnvironmentDependencyNullGuard()
    {
        var snapshot = new EnvironmentConfigMappingSnapshot(new AppSurfaceEnvironmentConfigOptions());
        Assert.Equal("environmentProvider", Assert.Throws<ArgumentNullException>(() => new EnvironmentConfigProvider(null!)).ParamName);
        Assert.Equal("environmentProvider", Assert.Throws<ArgumentNullException>(() => new EnvironmentConfigProvider(null!, snapshot)).ParamName);
    }

    private static ServiceProvider Build(string mappedName)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEnvironmentProvider>(new FixedEnvironment("Prod"));
        services.Configure<AppSurfaceEnvironmentConfigOptions>(options => options.MapKey("A", mappedName));
        new AppSurfaceConfigModule().ConfigureServices(new StartupContext([], new TestHostModule()), services);
        return services.BuildServiceProvider();
    }

    private sealed class FixedEnvironment(string environment, IReadOnlyDictionary<string, string>? values = null) : IEnvironmentProvider
    {
        public string Environment { get; } = environment;
        public bool IsDevelopment => false;
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => values?.GetValueOrDefault(name) ?? defaultValue;
        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() => values ?? new Dictionary<string, string>();
    }

    private sealed class ThrowOnReadEnvironment(string environment) : IEnvironmentProvider
    {
        public int Reads { get; private set; }
        public int Captures { get; private set; }
        public string Environment { get; } = environment;
        public bool IsDevelopment => false;
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) { Reads++; throw new InvalidOperationException("source read"); }
        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() { Captures++; throw new InvalidOperationException("source capture"); }
    }
}
