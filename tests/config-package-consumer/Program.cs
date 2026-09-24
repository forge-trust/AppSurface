using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Config.Testing;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Core.Defaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

ConfigPackageCompatibility.ValidateAssemblies([typeof(Program).Assembly]);
var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Host:Independent"] = "host-marker" });
var hostConfiguration = builder.Configuration;
var environment = new DefaultEnvironmentProvider(["--environment", "Production"]);
builder.Services.AddSingleton<IEnvironmentProvider>(environment);
var context = new StartupContext([], new NoHostModule());
new AppSurfaceConfigModule().ConfigureServices(context, builder.Services);
foreach (var registration in context.CustomRegistrations) { registration(builder.Services); }
builder.Services.AddSingleton<IConfigFileLocationProvider>(new ConsumerFileLocation(Directory.GetCurrentDirectory()));
builder.Services.AddConfigAuditKey<string>(AppSurfaceConfigKey.Parse("Payments:ApiKey"));
builder.Services.AddSingleton<IConfigProvider, PublicOnlyProvider>();
using var host = builder.Build();
await host.StartAsync();
var manager = host.Services.GetRequiredService<IConfigManager>();
var wrapper = new ConsumerPaymentConfig();
((IConfig)wrapper).Init(manager, environment, ConfigKeyAttribute.GetLogicalKey(typeof(ConsumerPaymentConfig)));
var expected = Environment.GetEnvironmentVariable("PAYMENTS__APIKEY") is null ? "file demo" : "environment override";
if (wrapper.Value != expected || manager.GetValue<string>("Production", "payments:apikey") != expected)
    throw new InvalidOperationException("consumer-value-selection-failed");
if (!ReferenceEquals(hostConfiguration, builder.Configuration)
    || host.Services.GetRequiredService<IConfiguration>()["Host:Independent"] != "host-marker")
    throw new InvalidOperationException("consumer-host-configuration-changed");

var report = host.Services.GetRequiredService<IConfigAuditReporter>().GetReport("Production");
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
await host.StopAsync();

[ConfigKey("Payments:ApiKey", root: true)]
internal sealed class ConsumerPaymentConfig : Config<string>;
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
