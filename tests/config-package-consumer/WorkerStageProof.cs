using ConfigPackageConsumer.Domain;
using ConfigPackageConsumer.WorkerRoot;
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Core.Defaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;

internal static class WorkerStageProof
{
    private static readonly (Type Type, string Key, Action<IServiceCollection> Select)[] Selections =
    [
        (typeof(ProductFeatureCatalogConfig), "Skoolit:Features", services => services.AddAppSurfaceConfig<ProductFeatureCatalogConfig>()),
        (typeof(ProductFeatureWorkerParityReceiptConfig), "Skoolit:FeatureWorkerParity", services => services.AddAppSurfaceConfig<ProductFeatureWorkerParityReceiptConfig>()),
        (typeof(DurableWorkerPublicOptionsConfig), "Skoolit:DurableWorkers", services => services.AddAppSurfaceConfig<DurableWorkerPublicOptionsConfig>()),
        (typeof(AlphaEvidenceSourceOnlyPublicOptionsConfig), "Skoolit:AlphaEvidence:SourceOnly", services => services.AddAppSurfaceConfig<AlphaEvidenceSourceOnlyPublicOptionsConfig>()),
        (typeof(ForwardingExtractionPublicOptionsConfig), "Skoolit:Forwarding:Extraction", services => services.AddAppSurfaceConfig<ForwardingExtractionPublicOptionsConfig>()),
        (typeof(SharedDataProtectionPublicOptionsConfig), "Skoolit:DataProtection", services => services.AddAppSurfaceConfig<SharedDataProtectionPublicOptionsConfig>())
    ];

    public static object Run()
    {
        var scenarios = new[]
        {
            new Scenario("other-lane", false, true, true, true),
            new Scenario("evidence-disabled", true, false, false, false),
            new Scenario("admission-disabled", true, true, false, false),
            new Scenario("evidence-standby", true, true, true, false),
            new Scenario("evidence-active", true, true, true, true)
        };
        return new
        {
            domainWrappersOutsideDiscovery = 2,
            discoveredWorkerWrappers = 4,
            repeatedWorkerHelperWrappers = ProveRepeatedRegistration(),
            selections = Selections.Select(selection => new
            {
                wrapper = selection.Type.Name,
                key = selection.Key,
                discovery = selection.Type.Assembly == typeof(WorkerStageProof).Assembly ? "worker-root" : "packed-domain"
            }),
            scenarios = scenarios.Select(RunScenario).ToArray()
        };
    }

    private static int ProveRepeatedRegistration()
    {
        ResetConstructors();
        var services = new ServiceCollection();
        ConfigureModule(services, new FixtureEnvironment());
        foreach (var selection in Selections)
        {
            selection.Select(services);
            selection.Select(services);
        }
        AssertDescriptors(services, Selections.Select(selection => selection.Type));
        if (ConstructorCounts().Values.Any(count => count != 0) || DomainFixtureCounters.Total != 0)
            throw new InvalidOperationException("worker-repeat-registration-was-eager");
        return 4; // Four selections repeat the discovery registration; the two Domain selections are explicit only.
    }

    private static object RunScenario(Scenario scenario)
    {
        ResetConstructors();
        var counters = new Counters();
        var secret = new UnselectedSecretProvider();
        var services = new ServiceCollection();
        var environment = new FixtureEnvironment();
        services.AddSingleton<IEnvironmentProvider>(environment);
        services.AddSingleton<IConfigSecretProvider>(secret);
        services.AddSingleton(scenario);
        services.AddSingleton(counters);
        services.AddSingleton<IConfigProvider>(provider => new WorkerProvider(
            provider.GetRequiredService<Counters>(), provider.GetRequiredService<Scenario>()));
        var publicServices = Clone(services);
        var beforeRegistration = Snapshot(counters);
        var discoveryInputs = ConfigureModule(publicServices, environment);
        var originalManagerType = publicServices.Last(descriptor => descriptor.ServiceType == typeof(IConfigManager)).ImplementationType
            ?? throw new InvalidOperationException("worker-manager-registration-unexpected");
        InstallManager(publicServices, originalManagerType);
        Selections[0].Select(publicServices);
        Selections[1].Select(publicServices);
        Selections[2].Select(publicServices);
        if (scenario.EvidenceSupport)
            Selections[3].Select(publicServices);
        AssertDescriptors(publicServices, Selections.Select(selection => selection.Type));
        var registration = AssertPhase("first-registration", beforeRegistration, counters, secret, []);
        var beforeStartup = Snapshot(counters);
        using var firstProvider = publicServices.BuildServiceProvider();
        StartProvider(firstProvider);
        var startup = AssertPhase("first-startup-before-resolution", beforeStartup, counters, secret, []);

        var beforeRuntime = Snapshot(counters);
        var catalog = firstProvider.GetRequiredService<ProductFeatureCatalogConfig>();
        var parity = firstProvider.GetRequiredService<ProductFeatureWorkerParityReceiptConfig>();
        var options = firstProvider.GetRequiredService<DurableWorkerPublicOptionsConfig>();
        if (catalog.Value != WorkerProvider.Marker || parity.Value != WorkerProvider.Marker
            || options.Value != new WorkerPublicOptions(scenario.Enabled, scenario.AdmissionEnabled, scenario.Active))
            throw new InvalidOperationException("worker-first-runtime-values-failed");
        var evidenceMayRun = scenario.EvidenceSupport && options.Value is
        { EvidenceSupportEnabled: true, WorkerAdmissionEnabled: true };
        if (evidenceMayRun && firstProvider.GetRequiredService<AlphaEvidenceSourceOnlyPublicOptionsConfig>().Value != WorkerProvider.Marker)
            throw new InvalidOperationException("worker-source-only-value-failed");
        var firstIndices = evidenceMayRun ? new[] { 0, 1, 2, 3 } : [0, 1, 2];
        var runtime = AssertPhase("first-runtime", beforeRuntime, counters, secret, firstIndices);
        var firstAudit = Audit(firstProvider, counters, secret, "first-audit");
        var firstFrozen = Snapshot(counters);
        var secondStageBuilt = scenario.EvidenceSupport && options.Value!.PlanModeActive;
        object? secondEvidence = null;
        if (secondStageBuilt)
        {
            var secondCounters = new Counters();
            var evidenceServices = Clone(publicServices);
            evidenceServices.RemoveAll<Counters>();
            evidenceServices.AddSingleton(secondCounters);
            var beforeSecondRegistration = Snapshot(secondCounters);
            Selections[4].Select(evidenceServices);
            Selections[5].Select(evidenceServices);
            AssertDescriptors(evidenceServices, Selections.Select(selection => selection.Type));
            var secondRegistration = AssertPhase("second-registration", beforeSecondRegistration, secondCounters, secret, []);
            var beforeSecondStartup = Snapshot(secondCounters);
            using var secondProvider = evidenceServices.BuildServiceProvider();
            StartProvider(secondProvider);
            var secondStartup = AssertPhase("second-startup-before-resolution", beforeSecondStartup, secondCounters, secret, []);
            var beforeSecondRuntime = Snapshot(secondCounters);
            if (secondProvider.GetRequiredService<ForwardingExtractionPublicOptionsConfig>().Value != WorkerProvider.Marker
                || secondProvider.GetRequiredService<SharedDataProtectionPublicOptionsConfig>().Value != WorkerProvider.Marker)
                throw new InvalidOperationException("worker-second-runtime-values-failed");
            var secondRuntime = AssertPhase("second-runtime", beforeSecondRuntime, secondCounters, secret, [4, 5]);
            var secondAudit = Audit(secondProvider, secondCounters, secret, "second-audit");
            secondEvidence = new
            {
                provider = "second",
                registration = secondRegistration,
                startupBeforeResolution = secondStartup,
                runtime = secondRuntime,
                audit = secondAudit
            };
            if (!ReferenceEquals(catalog, firstProvider.GetRequiredService<ProductFeatureCatalogConfig>())
                || !ReferenceEquals(parity, firstProvider.GetRequiredService<ProductFeatureWorkerParityReceiptConfig>())
                || !ReferenceEquals(options, firstProvider.GetRequiredService<DurableWorkerPublicOptionsConfig>())
                || !EqualCounts(firstFrozen.InitCallsByKey, counters.InitCallsByKey)
                || !EqualCounts(firstFrozen.ProviderReadsByKey, counters.ProviderReadsByKey))
                throw new InvalidOperationException("worker-first-provider-was-mutated-by-second-stage");
        }
        return new
        {
            scenario = scenario.Name,
            lane = scenario.EvidenceSupport ? "EvidenceSupport" : "Other",
            sourceOnlyHelperSelected = scenario.EvidenceSupport,
            evidenceMayRun,
            secondStageBuilt,
            discoveryInputs,
            firstProvider = new
            {
                provider = "first",
                registration,
                startupBeforeResolution = startup,
                runtime,
                audit = firstAudit
            },
            secondProvider = secondEvidence,
            secretProviderReads = secret.Reads,
            unselectedSecretWrapperConstructors = DomainFixtureCounters.For(nameof(UnselectedSecretTripwireConfig))
        };
    }

    private static object Audit(IServiceProvider provider, Counters counters, UnselectedSecretProvider secret, string phase)
    {
        var before = Snapshot(counters);
        var report = provider.GetRequiredService<IConfigAuditReporter>().GetReport("Production");
        foreach (var selection in Selections)
        {
            var entries = report.Entries.Where(entry => entry.Key == selection.Key).ToArray();
            var expectedType = selection.Type == typeof(DurableWorkerPublicOptionsConfig)
                ? typeof(WorkerPublicOptions).FullName : typeof(string).FullName;
            if (entries.Length != 1 || entries[0].State != ConfigAuditEntryState.Resolved || entries[0].DeclaredType != expectedType)
                throw new InvalidOperationException("worker-audit-key-type-state-failed");
        }
        if (report.Entries.Count != Selections.Length)
            throw new InvalidOperationException("worker-audit-inventory-mismatch");
        return AssertPhase(phase, before, counters, secret, [0, 1, 2, 3, 4, 5], inspection: true);
    }

    private static Phase AssertPhase(string phase, Phase before, Counters counters,
        UnselectedSecretProvider secret, int[] indices, bool inspection = false)
    {
        var after = Snapshot(counters);
        var result = new Phase(
            Difference(after.ConstructorsByType, before.ConstructorsByType),
            Difference(after.InitCallsByKey, before.InitCallsByKey),
            Difference(after.ProviderReadsByKey, before.ProviderReadsByKey));
        var types = Selections.ToDictionary(selection => selection.Type.Name, _ => 0, StringComparer.Ordinal);
        var reads = EmptyKeys();
        var inits = EmptyKeys();
        foreach (var index in indices)
        {
            types[Selections[index].Type.Name] = 1;
            reads[Selections[index].Key] = 1;
            if (!inspection)
                inits[Selections[index].Key] = 1;
        }
        if (!EqualCounts(result.ConstructorsByType, types) || !EqualCounts(result.InitCallsByKey, inits)
            || !EqualCounts(result.ProviderReadsByKey, reads) || secret.Reads != 0
            || DomainFixtureCounters.For(nameof(UnselectedSecretTripwireConfig)) != 0)
            throw new InvalidOperationException(phase + "-counter-contract-failed");
        return result;
    }

    private static string[] ConfigureModule(IServiceCollection services, IEnvironmentProvider? environment)
    {
        services.AddLogging();
        var context = new StartupContext([], new NoHostModule(), EnvironmentProvider: environment)
        { OverrideEntryPointAssembly = typeof(WorkerStageProof).Assembly };
        new AppSurfaceConfigModule().ConfigureServices(context, services);
        var inputs = context.GetDependencies().Select(module => module.GetType().Assembly)
            .Append(context.EntryPointAssembly).Append(context.RootModuleAssembly).Distinct().ToArray();
        if (inputs.Contains(typeof(ProductFeatureCatalogConfig).Assembly))
            throw new InvalidOperationException("packed-domain-entered-discovery");
        foreach (var registration in context.CustomRegistrations)
            registration(services);
        return inputs.Select(assembly => assembly.GetName().Name!).Order(StringComparer.Ordinal).ToArray();
    }

    private static void StartProvider(IServiceProvider provider)
    {
        provider.GetService<IStartupValidator>()?.Validate();
        var hosted = provider.GetServices<IHostedService>().ToArray();
        foreach (var lifecycle in hosted.OfType<IHostedLifecycleService>())
            lifecycle.StartingAsync(CancellationToken.None).GetAwaiter().GetResult();
        foreach (var service in hosted)
            service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        foreach (var lifecycle in hosted.OfType<IHostedLifecycleService>())
            lifecycle.StartedAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void InstallManager(IServiceCollection services, Type originalManagerType)
    {
        services.RemoveAll<IConfigManager>();
        services.AddSingleton<IConfigManager>(provider => new CountingManager(
            (IConfigManager)ActivatorUtilities.CreateInstance(provider, originalManagerType),
            provider.GetRequiredService<Counters>()));
    }

    private static void AssertDescriptors(IServiceCollection services, IEnumerable<Type> types)
    {
        foreach (var type in types)
            if (services.Count(descriptor => descriptor.ServiceType == type && !descriptor.IsKeyedService) != 1)
                throw new InvalidOperationException("worker-descriptor-count-failed");
    }

    private static IServiceCollection Clone(IServiceCollection source)
    {
        IServiceCollection copy = new ServiceCollection();
        foreach (var descriptor in source)
            copy.Add(descriptor);
        return copy;
    }

    private static void ResetConstructors()
    {
        DomainFixtureCounters.Reset();
        WorkerRootFixtureCounters.Reset();
    }

    private static Dictionary<string, int> ConstructorCounts() => Selections.ToDictionary(
        selection => selection.Type.Name,
        selection => selection.Type.Assembly == typeof(ProductFeatureCatalogConfig).Assembly
            ? DomainFixtureCounters.For(selection.Type.Name) : WorkerRootFixtureCounters.For(selection.Type.Name),
        StringComparer.Ordinal);

    private static Dictionary<string, int> EmptyKeys() =>
        Selections.ToDictionary(selection => selection.Key, _ => 0, StringComparer.Ordinal);

    private static Phase Snapshot(Counters counters) => new(ConstructorCounts(),
        new Dictionary<string, int>(counters.InitCallsByKey), new Dictionary<string, int>(counters.ProviderReadsByKey));

    private static Dictionary<string, int> Difference(IReadOnlyDictionary<string, int> after, IReadOnlyDictionary<string, int> before) =>
        after.ToDictionary(pair => pair.Key, pair => pair.Value - before.GetValueOrDefault(pair.Key), StringComparer.Ordinal);

    private static bool EqualCounts(IReadOnlyDictionary<string, int> actual, IReadOnlyDictionary<string, int> expected) =>
        actual.Count == expected.Count && expected.All(pair => actual.GetValueOrDefault(pair.Key) == pair.Value);

    private sealed record Scenario(string Name, bool EvidenceSupport, bool Enabled, bool AdmissionEnabled, bool Active);
    private sealed record Phase(IReadOnlyDictionary<string, int> ConstructorsByType,
        IReadOnlyDictionary<string, int> InitCallsByKey, IReadOnlyDictionary<string, int> ProviderReadsByKey);

    private sealed class Counters
    {
        public Dictionary<string, int> InitCallsByKey { get; } = EmptyKeys();
        public Dictionary<string, int> ProviderReadsByKey { get; } = EmptyKeys();
        public static void Record(Dictionary<string, int> target, string key) => target[key] = target.GetValueOrDefault(key) + 1;
    }

    private sealed class CountingManager(IConfigManager inner, Counters counters) : IConfigManager
    {
        public T? GetValue<T>(string environment, AppSurfaceConfigKey key)
        {
            Counters.Record(counters.InitCallsByKey, key.Value);
            return inner.GetValue<T>(environment, key);
        }
        public T? GetValue<T>(string environment, string key) =>
            throw new InvalidOperationException("worker-init-used-handwritten-key");
    }

    private sealed class FixtureEnvironment : IEnvironmentProvider
    {
        public string Environment => "Production";
        public bool IsDevelopment => false;
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() => new Dictionary<string, string>();
    }

    private sealed class UnselectedSecretProvider : IConfigSecretProvider
    {
        public string Id => "fixture-secret-tripwire";
        public int Reads { get; private set; }
        public ConfigSecretReferenceValidation ValidateReference(ConfigSecretReference reference) =>
            throw new InvalidOperationException("public-worker-unexpected-secret-reference");
        public ConfigSecretProviderResolution Resolve(ConfigSecretReference reference, ConfigSecretResolutionContext context)
        {
            Reads++;
            throw new InvalidOperationException("public-worker-read-unselected-secret-provider");
        }
    }

    private sealed class WorkerProvider(Counters counters, Scenario scenario) : IConfigProvider
    {
        public const string Marker = "worker-fixture-marker";
        public int Priority => 1000;
        public string Name => "WorkerStageProofProvider";
        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request)
        {
            Counters.Record(counters.ProviderReadsByKey, request.Key.Value);
            object? value = request.Key.Value == "Skoolit:DurableWorkers"
                ? new WorkerPublicOptions(scenario.Enabled, scenario.AdmissionEnabled, scenario.Active)
                : Selections.Any(selection => selection.Key == request.Key.Value) ? Marker : null;
            return value is T typed ? ConfigProviderValueResult<T>.Found(typed) : ConfigProviderValueResult<T>.Missing();
        }
    }
}
