using System.Collections.Concurrent;
using System.Text.Json;
using ConfigPackageConsumer.Domain;
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Config.Testing;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Core.Defaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

ConfigPackageCompatibility.ValidateAssemblies([typeof(Program).Assembly]);
var proofMode = Environment.GetEnvironmentVariable("CONFIG_PACKAGE_CONSUMER_MODE") ?? "candidate";
if (proofMode is not ("candidate" or "baseline"))
    throw new InvalidOperationException("consumer-proof-mode-invalid");
var candidate = proofMode == "candidate";
DomainFixtureCounters.Reset();
var packageProofProvider = new PackageProofProvider();
var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Host:Independent"] = "host-marker" });
var hostConfiguration = builder.Configuration;
var environment = new DefaultEnvironmentProvider(["--environment", "Production"]);
builder.Services.AddSingleton<IEnvironmentProvider>(environment);
builder.Services.AddSingleton<IConfigProvider>(packageProofProvider);
var context = new StartupContext([], new NoHostModule(), EnvironmentProvider: environment)
{ OverrideEntryPointAssembly = typeof(NoHostModule).Assembly };
var discoveryInputs = context.GetDependencies().Select(module => module.GetType().Assembly)
    .Append(context.EntryPointAssembly).Append(context.RootModuleAssembly).Distinct().ToArray();
if (discoveryInputs.Contains(typeof(PaymentsEndpointConfig).Assembly))
    throw new InvalidOperationException("web-packed-domain-entered-discovery");
new AppSurfaceConfigModule().ConfigureServices(context, builder.Services);
foreach (var registration in context.CustomRegistrations) { registration(builder.Services); }
if (candidate)
{
    builder.Services.AddAppSurfaceConfig<ConfigPackageConsumer.Domain.ProductFeatureCatalogConfig>();
    builder.Services.AddAppSurfaceConfig<ConfigPackageConsumer.Domain.ProductFeatureWorkerParityReceiptConfig>();
}
ExplicitRegistrationExample.Register(builder.Services, candidate);
builder.Services.AddSingleton<IConfigFileLocationProvider>(new ConsumerFileLocation(Directory.GetCurrentDirectory()));
builder.Services.AddConfigAuditKey<string>(AppSurfaceConfigKey.Parse("Payments:ApiKey"));
builder.Services.AddSingleton<IConfigProvider, PublicOnlyProvider>();
var selectedRegistrationTypes = candidate
    ? new[]
    {
        typeof(ConfigPackageConsumer.Domain.ProductFeatureCatalogConfig),
        typeof(ConfigPackageConsumer.Domain.ProductFeatureWorkerParityReceiptConfig),
        typeof(PaymentsEndpointConfig)
    }
    : [];
foreach (var configType in selectedRegistrationTypes)
{
    if (builder.Services.Count(descriptor => descriptor.ServiceType == configType && !descriptor.IsKeyedService) != 1)
        throw new InvalidOperationException("consumer-selected-wrapper-descriptor-count-failed");
}
var registrationConstructors = DomainFixtureCounters.Total;
var registrationProviderReads = packageProofProvider.TotalReads;
if (registrationConstructors != 0 || registrationProviderReads != 0
    || DomainFixtureCounters.For(nameof(UnselectedSecretTripwireConfig)) != 0
    || packageProofProvider.ReadsFor("Consumer:SecretTripwire") != 0)
    throw new InvalidOperationException("consumer-registration-phase-was-eager");
using var host = builder.Build();
await host.StartAsync();
if (DomainFixtureCounters.Total != 0 || packageProofProvider.TotalReads != 0)
    throw new InvalidOperationException("consumer-startup-phase-was-eager");
var manager = host.Services.GetRequiredService<IConfigManager>();
if (candidate)
{
    var domainWrappers = new Config<string>[]
    {
        host.Services.GetRequiredService<ConfigPackageConsumer.Domain.ProductFeatureCatalogConfig>(),
        host.Services.GetRequiredService<ConfigPackageConsumer.Domain.ProductFeatureWorkerParityReceiptConfig>()
    };
    if (domainWrappers.Any(wrapper => wrapper.Value != PackageProofProvider.SelectedValue))
        throw new InvalidOperationException("consumer-selected-wrapper-runtime-failed");
    if (DomainFixtureCounters.For(nameof(ConfigPackageConsumer.Domain.ProductFeatureCatalogConfig)) == 0
        || DomainFixtureCounters.For(nameof(ConfigPackageConsumer.Domain.ProductFeatureWorkerParityReceiptConfig)) == 0
        )
        throw new InvalidOperationException("consumer-selected-wrapper-activation-missing");
}
else
{
    var baselineWrappers = new Config<string>[]
    {
        new ConfigPackageConsumer.Domain.ProductFeatureCatalogConfig(),
        new ConfigPackageConsumer.Domain.ProductFeatureWorkerParityReceiptConfig()
    };
    foreach (var baselineWrapper in baselineWrappers)
    {
        ((IConfig)baselineWrapper).Init(manager, environment, ConfigKeyAttribute.GetLogicalKey(baselineWrapper.GetType()));
        if (baselineWrapper.Value != PackageProofProvider.SelectedValue)
            throw new InvalidOperationException("consumer-baseline-manual-domain-resolution-failed");
    }
}
ExplicitRegistrationExample.VerifyRuntime(host.Services, candidate);
var wrapper = new ConsumerPaymentConfig();
((IConfig)wrapper).Init(manager, environment, ConfigKeyAttribute.GetLogicalKey(typeof(ConsumerPaymentConfig)));
var expected = Environment.GetEnvironmentVariable("PAYMENTS__APIKEY") is null ? "file demo" : "environment override";
if (wrapper.Value != expected || manager.GetValue<string>("Production", "payments:apikey") != expected)
    throw new InvalidOperationException("consumer-value-selection-failed");
if (!ReferenceEquals(hostConfiguration, builder.Configuration)
    || host.Services.GetRequiredService<IConfiguration>()["Host:Independent"] != "host-marker")
    throw new InvalidOperationException("consumer-host-configuration-changed");

var runtimeConstructors = DomainFixtureCounters.Total;
var runtimeProviderReads = packageProofProvider.TotalReads;
var runtimeCountsByType = WebConstructors();
var runtimeReadsByKey = WebReads(packageProofProvider);
if (runtimeCountsByType.Values.Any(count => count != 1) || runtimeReadsByKey.Values.Any(count => count != 1))
    throw new InvalidOperationException("web-runtime-counter-contract-failed");
var auditConstructorsBefore = runtimeConstructors;
var auditProviderReadsBefore = packageProofProvider.TotalReads;
var report = host.Services.GetRequiredService<IConfigAuditReporter>().GetReport("Production");
ExplicitRegistrationExample.VerifyAudit(report, candidate);
var registrationKeys = new[]
{
    "Skoolit:Features", "Skoolit:FeatureWorkerParity"
};
foreach (var key in registrationKeys)
{
    var entries = report.Entries.Where(item => item.Key == key).ToArray();
    if (candidate
        ? entries.Length != 1 || entries[0].State != ConfigAuditEntryState.Resolved || entries[0].DeclaredType != typeof(string).FullName
        : entries.Length != 0)
        throw new InvalidOperationException("consumer-domain-audit-declaration-contract-failed");
}
var auditConstructorDelta = DomainFixtureCounters.Total - auditConstructorsBefore;
var auditProviderReadDelta = packageProofProvider.TotalReads - auditProviderReadsBefore;
var auditCountsByType = WebConstructors().ToDictionary(pair => pair.Key, pair => pair.Value - runtimeCountsByType[pair.Key]);
var auditReadsByKey = WebReads(packageProofProvider).ToDictionary(pair => pair.Key, pair => pair.Value - runtimeReadsByKey[pair.Key]);
if (auditCountsByType.Values.Any(count => count != (candidate ? 1 : 0))
    || auditReadsByKey.Values.Any(count => count != (candidate ? 1 : 0)))
    throw new InvalidOperationException("web-audit-counter-contract-failed");
var tripwireConstructors = DomainFixtureCounters.For(nameof(UnselectedSecretTripwireConfig));
var tripwireProviderReads = packageProofProvider.ReadsFor("Consumer:SecretTripwire");
if (tripwireConstructors != 0 || tripwireProviderReads != 0)
    throw new InvalidOperationException("consumer-unselected-tripwire-activated");
Console.WriteLine($"Package proof counters: registration constructors={registrationConstructors} provider-reads={registrationProviderReads}; runtime constructors={runtimeConstructors} provider-reads={runtimeProviderReads}; audit constructor-delta={auditConstructorDelta} provider-read-delta={auditProviderReadDelta}; tripwire constructors={tripwireConstructors} provider-reads={tripwireProviderReads}");
Console.WriteLine("Web phase evidence: " + JsonSerializer.Serialize(new
{
    mode = proofMode,
    discoveryInputs = discoveryInputs.Select(assembly => assembly.GetName().Name).Order(StringComparer.Ordinal),
    registration = new { constructors = registrationConstructors, providerReads = registrationProviderReads },
    startupBeforeResolution = new { constructors = 0, providerReads = 0 },
    runtime = new { constructorsByType = runtimeCountsByType, providerReadsByKey = runtimeReadsByKey },
    audit = new { constructorsByType = auditCountsByType, providerReadsByKey = auditReadsByKey },
    tripwireConstructors,
    tripwireProviderReads
}));
var entry = report.Entries.Single(item => item.Key == "Payments:ApiKey");
var source = expected == "file demo" ? nameof(FileBasedConfigProvider) : "EnvironmentConfigProvider";
if (!entry.Sources.Any(item => item.ProviderName == source))
    throw new InvalidOperationException("consumer-source-provenance-failed");
if (manager.GetValue<string>("Production", "External:Missing") is not null)
    throw new InvalidOperationException("consumer-missing-failed");
if (manager.GetValue<string>("Production", "External:Notice") != "external-marker")
    throw new InvalidOperationException("consumer-notice-failed");
foreach (var key in new[] { "External:Terminal", "External:Collision" })
{
    try { manager.GetValue<string>("Production", key); throw new InvalidOperationException("consumer-terminal-missed"); }
    catch (ConfigurationResolutionException failure) when (failure.ProviderName == nameof(PublicOnlyProvider)) { }
}
var repeat = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => manager.GetValue<string>("Production", "External:Notice"))));
if (repeat.Any(value => value != "external-marker")) throw new InvalidOperationException("consumer-concurrency-failed");
foreach (var row in ConfigProviderContractCases.All.Where(row => row.Scenario is
             ConfigProviderContractScenario.Unrepresentable or ConfigProviderContractScenario.MissingToLowerFallback))
    ConfigProviderContractAssert.Case(new ConsumerContractHarness(), row);
Console.WriteLine($"Payments:ApiKey = {expected} (source: {source})");
Console.WriteLine("Public provider: missing, found-with-notice, terminal, collision, concurrent PASS");
Console.WriteLine("IConfiguration coexistence: PASS");
Console.WriteLine("Public conformance: provider counterexample and missing-to-lower fallback PASS 2/2");
if (candidate)
    Console.WriteLine("Worker stage evidence: " + JsonSerializer.Serialize(WorkerStageProof.Run()));
await host.StopAsync();


static Dictionary<string, int> WebConstructors() => new()
{
    [nameof(ProductFeatureCatalogConfig)] = DomainFixtureCounters.For(nameof(ProductFeatureCatalogConfig)),
    [nameof(ProductFeatureWorkerParityReceiptConfig)] = DomainFixtureCounters.For(nameof(ProductFeatureWorkerParityReceiptConfig)),
    [nameof(PaymentsEndpointConfig)] = DomainFixtureCounters.For(nameof(PaymentsEndpointConfig))
};
static Dictionary<string, int> WebReads(PackageProofProvider provider) =>
    new[] { "Skoolit:Features", "Skoolit:FeatureWorkerParity", "Payments:Endpoint" }
        .ToDictionary(key => key, provider.ReadsFor, StringComparer.Ordinal);

internal sealed class ConsumerFileLocation(string directory) : IConfigFileLocationProvider
{
    public string Directory => directory;
}
public sealed class PublicOnlyProvider : IConfigProvider
{
    public int Priority => 10;
    public string Name => nameof(PublicOnlyProvider);
    public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request)
    {
        if (request.Key.Equals(AppSurfaceConfigKey.Parse("External:Notice")))
            return ConfigProviderValueResult<T>.Found((T)(object)"external-marker", new ConfigProviderNotice(
                "external-example-notice", "A fixture marker was returned.", "The provider is a package-consumer fixture.",
                "Use a native source in an application.", "https://appsurface.dev/guides/config-provider-authors", false));
        var collision = request.Key.Equals(AppSurfaceConfigKey.Parse("External:Collision"));
        if (collision || request.Key.Equals(AppSurfaceConfigKey.Parse("External:Terminal")))
            return ConfigProviderValueResult<T>.Terminal(new ConfigProviderTerminalDiagnostic(
                collision ? "config-key-collision" : "external-terminal", "Fixture resolution stopped.",
                "The fixture deliberately exercises terminal behavior.", "Repair the fixture source before continuing.",
                "https://appsurface.dev/guides/config-provider-authors", false));
        return ConfigProviderValueResult<T>.Missing();
    }
}

internal sealed class PackageProofProvider : IConfigProvider
{
    private static readonly HashSet<string> SelectedStringKeys = new(StringComparer.Ordinal)
    {
        "Skoolit:Features", "Skoolit:FeatureWorkerParity",
        "Skoolit:DurableWorkers", "Skoolit:AlphaEvidence:SourceOnly",
        "Skoolit:Forwarding:Extraction", "Skoolit:DataProtection"
    };

    private readonly ConcurrentDictionary<string, int> _reads = new(StringComparer.Ordinal);

    public const string SelectedValue = "fixture-marker";
    public int Priority => 50;
    public string Name => nameof(PackageProofProvider);
    public int TotalReads => _reads.Values.Sum();
    public int ReadsFor(string key) => _reads.TryGetValue(key, out var count) ? count : 0;

    public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request)
    {
        var key = request.Key.Value.Contains('.') && !request.Key.Value.Contains(':')
            ? request.Key.Value.Replace('.', ':')
            : request.Key.Value;
        _reads.AddOrUpdate(key, 1, static (_, count) => count + 1);
        if (key == "Payments:Endpoint" && typeof(T) == typeof(PaymentsEndpointValue))
            return ConfigProviderValueResult<T>.Found((T)(object)new PaymentsEndpointValue(ExplicitRegistrationExample.SafeEndpoint));
        if (SelectedStringKeys.Contains(key) && typeof(T) == typeof(string))
            return ConfigProviderValueResult<T>.Found((T)(object)SelectedValue);
        return ConfigProviderValueResult<T>.Missing();
    }
}

internal sealed class ConsumerContractHarness : IConfigProviderContractHarness
{
    public ConfigProviderContractSession Create(ConfigProviderContractCase row)
    {
        var directory = Path.Combine(Path.GetTempPath(), "consumer-conformance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEnvironmentProvider>(new ContractEnvironment());
        var context = new StartupContext([], new NoHostModule());
        new AppSurfaceConfigModule().ConfigureServices(context, services);
        foreach (var registration in context.CustomRegistrations) registration(services);
        services.AddSingleton<IConfigFileLocationProvider>(new ConsumerFileLocation(directory));
        var unsupported = row.Scenario == ConfigProviderContractScenario.Unrepresentable;
        var counterexample = AppSurfaceConfigKey.Parse("External:Unsupported.Dot");
        var lowerKey = unsupported ? counterexample : row.Key;
        var high = new ContractProvider(10, rejectDots: true, new Dictionary<AppSurfaceConfigKey, string>());
        var lower = new ContractProvider(5, rejectDots: false, new Dictionary<AppSurfaceConfigKey, string> { [lowerKey] = "lower-marker" });
        services.AddSingleton<IConfigProvider>(high);
        services.AddSingleton<IConfigProvider>(lower);
        var provider = services.BuildServiceProvider();
        return new ConfigProviderContractSession(provider.GetRequiredService<IConfigManager>(), "Production",
            "lower-marker", "distinct-marker", () =>
            {
                provider.Dispose();
                Directory.Delete(directory, recursive: true);
                if (high.Reads == 0 || lower.Reads != (unsupported ? 0 : 2))
                    throw new InvalidOperationException("consumer-conformance-traversal-failed");
            }, unsupported ? counterexample : null);
    }

    private sealed class ContractEnvironment : IEnvironmentProvider
    {
        public string Environment => "Production";
        public bool IsDevelopment => false;
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() => new Dictionary<string, string>();
    }

    // This external example provider's native codec accepts undotted segments only; the lower source accepts both.
    private sealed class ContractProvider(int priority, bool rejectDots, IReadOnlyDictionary<AppSurfaceConfigKey, string> values)
        : IConfigProvider
    {
        public int Priority => priority;
        public string Name => "ConsumerContractProvider";
        public int Reads { get; private set; }
        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request)
        {
            Reads++;
            if (rejectDots && request.Key.Segments.Any(segment => segment.Contains('.')))
                return ConfigProviderValueResult<T>.Terminal(new ConfigProviderTerminalDiagnostic(
                    "config-key-unrepresentable", "The native codec cannot represent a dotted segment.",
                    "The fixture codec requires undotted native segments.", "Use an explicit native mapping.",
                    "https://appsurface.dev/guides/config-provider-authors", false));
            return values.TryGetValue(request.Key, out var marker) && marker is T value
                ? ConfigProviderValueResult<T>.Found(value) : ConfigProviderValueResult<T>.Missing();
        }
    }
}
