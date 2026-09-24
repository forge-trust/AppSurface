using System.Diagnostics;
using System.Text;
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Config.GoogleSecretManager;
using ForgeTrust.AppSurface.Config.LocalSecrets;
using ForgeTrust.AppSurface.Config.Testing;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Core.Defaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var watch = Stopwatch.StartNew();
var root = Path.Combine(Path.GetTempPath(), "appsurface-config-key-contract-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var stages = new List<(string Name, long Milliseconds)>();
try
{
    Run("case-identity", () =>
    {
        foreach (var provider in new[] { "file", "environment", "local", "google" })
        {
            using var proof = Create(provider);
            var declared = proof.Manager.GetValue<string>("Production", AppSurfaceConfigKey.Parse("Payments:ApiKey"));
            var variant = proof.Manager.GetValue<string>("Production", AppSurfaceConfigKey.Parse("payments:apikey"));
            if (declared is null || !StringComparer.Ordinal.Equals(declared, variant))
            {
                throw new ProofFailure($"case-identity:{provider}");
            }
            var wrapper = new PaymentKey();
            ((IConfig)wrapper).Init(proof.Manager, proof.Environment, ConfigKeyAttribute.GetLogicalKey(typeof(PaymentKey)));
            if (!StringComparer.Ordinal.Equals("proof-primary", wrapper.Value))
            {
                throw new ProofFailure($"attributed-case-identity:{provider}");
            }
        }
    });
    Run("literal-dotted-segment", () =>
    {
        foreach (var provider in new[] { "file", "environment" })
        {
            using var proof = Create(provider);
            var key = AppSurfaceConfigKey.Parse("Logging:LogLevel:Microsoft.Hosting.Lifetime");
            Require(key.Segments.Length == 3);
            Equal("proof-literal", proof.Manager.GetValue<string>("Production", key));
        }
    });
    Run("hyphen-distinction", () =>
    {
        foreach (var provider in new[] { "file", "environment", "local", "google" })
        {
            using var proof = Create(provider);
            Equal("proof-hyphen", proof.Manager.GetValue<string>("Production", AppSurfaceConfigKey.Parse("Payments:Api-Key")));
            Equal("proof-nested", proof.Manager.GetValue<string>("Production", AppSurfaceConfigKey.Parse("Payments:Api:Key")));
        }
    });
    Run("same-layer-collision", () =>
    {
        using var proof = Create("collision");
        Terminal("config-key-collision", () => proof.Manager.GetValue<string>("Production", AppSurfaceConfigKey.Parse("Payments:ApiKey")));
    });
    Run("environment-and-provider-precedence", () =>
    {
        using var proof = Create("all");
        Equal("proof-environment", proof.Manager.GetValue<string>("Production", AppSurfaceConfigKey.Parse("Payments:ApiKey")));
        proof.Environment.Values.Clear();
        Equal("proof-google", proof.Manager.GetValue<string>("Production", AppSurfaceConfigKey.Parse("Payments:ApiKey")));
    });
    Run("audit-provenance", () =>
    {
        using var proof = Create("all");
        var report = proof.Services.GetRequiredService<IConfigAuditReporter>().GetReport("Production");
        var entry = report.Entries.Single(entry => entry.Key == "Payments:ApiKey");
        Require(entry.Sources.Any(source => source.ProviderName == "EnvironmentConfigProvider"));
        foreach (var provider in new[] { nameof(GoogleSecretManagerConfigProvider), nameof(AppSurfaceLocalSecretProvider), nameof(FileBasedConfigProvider) })
        {
            Require(entry.Sources.Any(source => source.ProviderName == provider));
        }
        proof.Environment.Values.Clear();
        var remoteReport = proof.Services.GetRequiredService<IConfigAuditReporter>().GetReport("Production");
        var remoteEntry = remoteReport.Entries.Single(candidate => candidate.Key == "Payments:ApiKey");
        foreach (var provider in new[] { nameof(GoogleSecretManagerConfigProvider), nameof(AppSurfaceLocalSecretProvider), nameof(FileBasedConfigProvider) })
        {
            Require(remoteEntry.Sources.Any(source => source.ProviderName == provider));
        }
        using var fileProof = Create("file");
        var fileReport = fileProof.Services.GetRequiredService<IConfigAuditReporter>().GetReport("Production");
        var fileEntry = fileReport.Entries.Single(candidate => candidate.Key == "Payments:ApiKey");
        Require(fileEntry.Sources.Any(source => source.ProviderName == nameof(FileBasedConfigProvider)));
    });
    Run("segment-prefix-boundary", () =>
    {
        using var proof = Create("convention");
        Equal("proof-convention", proof.Manager.GetValue<string>("Production", AppSurfaceConfigKey.Parse("Payments:ApiKey")));
        Require(proof.Manager.GetValue<string>("Production", AppSurfaceConfigKey.Parse("PaymentsArchive:ApiKey")) is null);
    });
    Run("explicit-mapping-for-unsupported-convention", () =>
    {
        using var proof = Create("convention");
        Terminal("config-key-unrepresentable", () => proof.Manager.GetValue<string>("Production", AppSurfaceConfigKey.Parse("Payments:Key.With.Dot")));
        using var mapped = Create("google");
        Equal("proof-literal", mapped.Manager.GetValue<string>("Production", AppSurfaceConfigKey.Parse("Logging:LogLevel:Microsoft.Hosting.Lifetime")));
    });
    Run("legacy-translation-and-declaration-conflict", () =>
    {
        using var proof = Create("file");
        Equal("proof-primary", proof.Manager.GetValue<string>("Production", "Payments.ApiKey"));
        var services = BaseServices(new ProofEnvironment(), EmptyDirectory(), out _);
        services.AddConfigAuditKey<string>("Payments.ApiKey");
        services.AddConfigAuditKey<string>("Payments:ApiKey");
        using var provider = services.BuildServiceProvider();
        try
        {
            provider.GetRequiredService<IConfigAuditReporter>().GetReport("Production");
            throw new ProofFailure();
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("config-key-collision", StringComparison.Ordinal))
        {
        }
    });

    Run("shared-counterexample-and-missing-fallback", () =>
    {
        var harness = new ProofContractHarness(row =>
        {
            var unsupported = row.Scenario == ConfigProviderContractScenario.Unrepresentable;
            var proof = Create(unsupported ? "convention" : "fallback");
            return new ConfigProviderContractSession(proof.Manager, "Production", "proof-primary", "proof-distinct",
                proof.Dispose, unsupported ? AppSurfaceConfigKey.Parse("Payments:Unsupported.Dot") : null);
        });
        foreach (var row in ConfigProviderContractCases.All.Where(row => row.Scenario is
                     ConfigProviderContractScenario.Unrepresentable or ConfigProviderContractScenario.MissingToLowerFallback))
            ConfigProviderContractAssert.Case(harness, row);
    });

    Console.WriteLine("AppSurface Config logical-key contract");
    Console.WriteLine("PASS 10/10");
    Console.WriteLine("Logical identity: Payments:ApiKey");
    Console.WriteLine("Providers: FileBasedConfigProvider, EnvironmentConfigProvider, AppSurfaceLocalSecretProvider, GoogleSecretManagerConfigProvider");
    Console.WriteLine("Values: [not displayed]");
    Console.WriteLine($"Total: {watch.ElapsedMilliseconds} ms");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL contract proof after {stages.Count}/10 completed stages. Values and exception details withheld.");
    Console.Error.WriteLine($"Failure category: {exception.GetType().Name}");
    if (exception is ProofFailure proofFailure) { Console.Error.WriteLine($"Failed check: {proofFailure.Check}"); }
    if (exception is ConfigurationResolutionException resolution)
    {
        Console.Error.WriteLine($"Provider: {resolution.ProviderName}; code: {resolution.Diagnostic.Code}; key: {resolution.Key}");
    }
    return 1;
}
finally
{
    Directory.Delete(root, recursive: true);
}

void Run(string name, Action action)
{
    var stage = Stopwatch.StartNew();
    action();
    stages.Add((name, stage.ElapsedMilliseconds));
    Console.WriteLine($"PASS {name} ({stage.ElapsedMilliseconds} ms)");
}

string EmptyDirectory()
{
    var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    return directory;
}

Proof Create(string provider)
{
    var directory = EmptyDirectory();
    var environment = new ProofEnvironment();
    if (provider is "file" or "all" or "collision" or "fallback" or "convention")
    {
        File.WriteAllText(Path.Combine(directory, "appsettings.json"), provider == "collision"
            ? """{"Payments":{"ApiKey":"proof-primary","apikey":"proof-primary"}}"""
            : """{"Payments":{"ApiKey":"proof-primary","Api-Key":"proof-hyphen","Api":{"Key":"proof-nested"},"Unsupported.Dot":"proof-lower"},"Logging":{"LogLevel":{"Microsoft.Hosting.Lifetime":"proof-literal"}}}""");
    }

    if (provider is "environment" or "all")
    {
        environment.Values["PAYMENTS__APIKEY"] = provider == "all" ? "proof-environment" : "proof-primary";
        environment.Values["PAYMENTS__API-KEY"] = "proof-hyphen";
        environment.Values["PAYMENTS__API__KEY"] = "proof-nested";
        environment.Values["LOGGING__LOGLEVEL__MICROSOFT.HOSTING.LIFETIME"] = "proof-literal";
    }

    var services = BaseServices(environment, directory, out _);
    services.AddConfigAuditKey<string>(AppSurfaceConfigKey.Parse("Payments:ApiKey"));
    if (provider is "local" or "all")
    {
        var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
        var store = new FileAppSurfaceLocalSecretStore(Path.Combine(EmptyDirectory(), "secrets.json"));
        foreach (var (key, marker) in new[] { ("Payments:ApiKey", "proof-primary"), ("Payments:Api-Key", "proof-hyphen"), ("Payments:Api:Key", "proof-nested") })
        {
            var identity = normalizer.Normalize("Proof", "Production", null, key);
            if (!identity.Succeeded) { throw new ProofFailure("local-fixture-identity"); }
            var result = store.Set(identity.Identity!, marker);
            if (result.Status != LocalSecretResultStatus.Found)
            {
                throw new ProofFailure($"local-fixture-write:{result.Status}");
            }
        }

        services.AddSingleton<IConfigProvider>(new AppSurfaceLocalSecretProvider(Options.Create(new AppSurfaceLocalSecretsOptions
        { ApplicationName = "Proof", Posture = LocalSecretsPostureMode.SingleMachineSelfHosted }), store, normalizer));
    }

    if (provider is "google" or "all" or "convention" or "fallback")
    {
        var options = new AppSurfaceGoogleSecretManagerOptions { ProjectId = "proof-project", DefaultVersion = "1" };
        var payloads = new Dictionary<string, string>(StringComparer.Ordinal);
        if (provider == "convention")
        {
            options.EnableConventionResolver(AppSurfaceConfigKey.Parse("Payments"), "proof-");
            payloads["projects/proof-project/secrets/proof-payments--apikey/versions/1"] = "proof-convention";
        }
        else if (provider != "fallback")
        {
            foreach (var (key, id, marker) in new[]
                     {
                         ("Payments:ApiKey", "primary", provider == "all" ? "proof-google" : "proof-primary"),
                         ("Payments:Api-Key", "hyphen", "proof-hyphen"),
                         ("Payments:Api:Key", "nested", "proof-nested"),
                         ("Logging:LogLevel:Microsoft.Hosting.Lifetime", "literal", "proof-literal")
                     })
            {
                options.MapSecret(AppSurfaceConfigKey.Parse(key), id);
                payloads[$"projects/proof-project/secrets/{id}/versions/1"] = marker;
            }
        }

        services.AddSingleton<IConfigProvider>(new GoogleSecretManagerConfigProvider(Options.Create(options), new ProofGoogleClient(payloads)));
    }

    var serviceProvider = services.BuildServiceProvider();
    return new Proof(serviceProvider, environment);
}

static IServiceCollection BaseServices(ProofEnvironment environment, string directory, out StartupContext context)
{
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IEnvironmentProvider>(environment);
    context = new StartupContext([], new NoHostModule());
    new AppSurfaceConfigModule().ConfigureServices(context, services);
    foreach (var registration in context.CustomRegistrations) { registration(services); }
    services.AddSingleton<IConfigFileLocationProvider>(new ProofFileLocation(directory));
    return services;
}

static void Equal(string? expected, string? actual) => Require(expected is not null && StringComparer.Ordinal.Equals(expected, actual));
static void Require(bool condition) { if (!condition) { throw new ProofFailure(); } }
static void Terminal(string code, Action action)
{
    try { action(); }
    catch (ConfigurationResolutionException exception) when (exception.Diagnostic.Code == code) { return; }
    throw new ProofFailure();
}

[ConfigKey("Payments:ApiKey", root: true)]
internal sealed class PaymentKey : Config<string>;
internal sealed class ProofFailure(string check = "contract-assertion") : Exception
{
    public string Check { get; } = check;
}
internal sealed class ProofFileLocation(string directory) : IConfigFileLocationProvider { public string Directory => directory; }
internal sealed class ProofEnvironment : IEnvironmentProvider
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
    public string Environment => "Production";
    public bool IsDevelopment => false;
    public string? GetEnvironmentVariable(string name, string? defaultValue = null) =>
        Values.TryGetValue(name, out var value) ? value : defaultValue;
    public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() => new Dictionary<string, string>(Values, StringComparer.Ordinal);
}
internal sealed class ProofGoogleClient(IReadOnlyDictionary<string, string> values) : IAppSurfaceGoogleSecretManagerClient
{
    public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout) =>
        new(Encoding.UTF8.GetBytes(values[resourceName]), resourceName);
}
internal sealed class Proof(ServiceProvider services, ProofEnvironment environment) : IDisposable
{
    public ServiceProvider Services => services;
    public ProofEnvironment Environment => environment;
    public IConfigManager Manager => services.GetRequiredService<IConfigManager>();
    public void Dispose() => services.Dispose();
}
internal sealed class ProofContractHarness(Func<ConfigProviderContractCase, ConfigProviderContractSession> create)
    : IConfigProviderContractHarness
{
    public ConfigProviderContractSession Create(ConfigProviderContractCase contractCase) => create(contractCase);
}
