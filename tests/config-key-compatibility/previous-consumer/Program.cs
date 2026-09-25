using System.Text.Json;
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Config.GoogleSecretManager;
using ForgeTrust.AppSurface.Config.LocalSecrets;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Logging.Abstractions;

const string key = "Payments:ApiKey";
var checks = new List<string>();

var provider = new LegacyProvider();
IConfigProvider providerContract = provider;
Require(providerContract.GetValue<string>("Development", key) == "provider-value", "IConfigProvider string helper");
checks.Add("IConfigProvider");

IConfigManager manager = new LegacyManager();
IConfigProvider inheritedProvider = manager;
Require(inheritedProvider.GetValue<string>("Development", key) == "manager-value", "IConfigManager inheritance");
checks.Add("IConfigManager inheritance");

var environment = new LegacyEnvironmentProvider();
Require(environment.Environment == "Development" && environment.IsDevelopment, "IEnvironmentProvider shape");
Require(environment.GetEnvironmentVariable("missing", "fallback") == "fallback", "IEnvironmentProvider lookup");
checks.Add("IEnvironmentProvider");

IConfig initialized = new StringConfig();
initialized.Init(manager, environment, key);
Require(((StringConfig)initialized).Value == "manager-value", "IConfig.Init string key");
checks.Add("IConfig.Init");

var auditProvider = new LegacyAuditProvider();
var audit = ((IConfigProviderAuditDiagnostics)auditProvider).ResolveForAudit(
    "Development", key, typeof(string), ConfigAuditSourceRole.Base);
var discovered = new ConfigProviderAuditDiscoveredKey(
    key,
    audit.Value,
    ConfigAuditDiscoveredValueKind.Scalar,
    audit.Sources,
    audit.Diagnostics);
var auditJson = JsonSerializer.Serialize(discovered);
Require(auditJson.Contains("\"Key\":\"Payments:ApiKey\"", StringComparison.Ordinal), "audit extension and DTO");
checks.Add("audit extension and DTO");

var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
var identityResult = normalizer.Normalize("fixture-app", "Development", "payments", key);
Require(identityResult.Succeeded, "local identity normalization");
Require(identityResult.Identity!.StorageName == "appsurface:fixture-app:Development:payments:Payments:ApiKey", "persisted local identity");

var localStore = new InMemoryAppSurfaceLocalSecretStore();
localStore.Set(identityResult.Identity, "fixture-local-value");
var localProvider = new AppSurfaceLocalSecretProvider(
    new StaticOptions<AppSurfaceLocalSecretsOptions>(new AppSurfaceLocalSecretsOptions
    {
        Posture = LocalSecretsPostureMode.SingleMachineSelfHosted,
        ApplicationName = "fixture-app",
        KeyPrefix = "payments"
    }),
    localStore,
    normalizer);
Require(localProvider.GetValue<string>("Development", key) == "fixture-local-value", "LocalSecrets concrete string helper");
checks.Add("LocalSecrets concrete string helper");

var googleOptions = new AppSurfaceGoogleSecretManagerOptions
{
    ProjectId = "fixture-project",
    DefaultVersion = "1"
};
googleOptions.MapSecret(key, "fixture-secret");
var googleClient = new FixtureGoogleClient();
var googleProvider = new GoogleSecretManagerConfigProvider(
    new StaticOptions<AppSurfaceGoogleSecretManagerOptions>(googleOptions),
    googleClient);
Require(googleProvider.GetValue<string>("Production", key) == "fixture-google-value", "Google concrete string helper");
Require(googleClient.LastResourceName == "projects/fixture-project/secrets/fixture-secret/versions/1", "persisted Google identity");
checks.Add("Google concrete string helper");

ProbeOldLogicalKeyDivergence();
checks.Add("old logical-key divergence probe");

Console.WriteLine($"BASELINE PASS: {string.Join(", ", checks)}");

static void ProbeOldLogicalKeyDivergence()
{
    const string exactKey = "Payments:ApiKey";
    const string lowerKey = "payments:apikey";
    var rows = new List<OldProbeRow>();

    var fileDirectory = Path.Combine(Path.GetTempPath(), "appsurface-old-config-probe", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(fileDirectory);
    try
    {
        File.WriteAllText(
            Path.Combine(fileDirectory, "appsettings.json"),
            "{\"Payments\":{\"ApiKey\":\"old-file-sentinel\"}}");
        File.WriteAllText(
            Path.Combine(fileDirectory, "appsettings.Development.json"),
            "{\"Payments\":{\"ApiKey\":\"old-file-sentinel\"}}");
        var fileProvider = new FileBasedConfigProvider(
            new FixtureFileLocationProvider(fileDirectory),
            NullLogger<FileBasedConfigProvider>.Instance);
        var exactFile = fileProvider.GetValue<string>("Development", exactKey);
        var lowerFile = fileProvider.GetValue<string>("Development", lowerKey);
        var oldDotFile = fileProvider.GetValue<string>("Development", "Payments.ApiKey");
        Require(exactFile == null, "old file colon logical key divergence");
        Require(lowerFile == null, "old file case-sensitive logical key divergence");
        Require(oldDotFile != null, "old file dotted logical key");
        rows.Add(new("file", "Payments:ApiKey", "missing", "EXPECTED OLD: old file provider uses dotted paths"));
        rows.Add(new("file", "payments:apikey", "missing", "EXPECTED OLD: case variant diverges"));
        rows.Add(new("file", "Payments.ApiKey", "found", "EXPECTED OLD: dotted path is the old representation"));
    }
    finally
    {
        if (Directory.Exists(fileDirectory))
        {
            Directory.Delete(fileDirectory, recursive: true);
        }
    }

    var environment = new LegacyEnvironmentProvider(
        ("Development__Payments__ApiKey", "old-env-sentinel"));
    Require(environment.GetEnvironmentVariable("Development__Payments__ApiKey") != null, "old environment exact candidate");
    Require(environment.GetEnvironmentVariable("development__payments__apikey") == null, "old environment case variant divergence");
    rows.Add(new("env", "Development__Payments__ApiKey", "found", "EXPECTED OLD: configured candidate only"));
    rows.Add(new("env", "development__payments__apikey", "missing", "EXPECTED OLD: case variant diverges"));

    var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
    var exactIdentity = normalizer.Normalize("fixture-app", "Development", "payments", exactKey);
    var lowerIdentity = normalizer.Normalize("fixture-app", "Development", "payments", lowerKey);
    Require(exactIdentity.Succeeded && lowerIdentity.Succeeded, "old local identity variants normalize");
    Require(!string.Equals(exactIdentity.Identity!.StorageName, lowerIdentity.Identity!.StorageName, StringComparison.Ordinal), "old local case variant persisted identity divergence");
    var localStore = new InMemoryAppSurfaceLocalSecretStore();
    localStore.Set(exactIdentity.Identity!, "old-local-sentinel");
    var localProvider = new AppSurfaceLocalSecretProvider(
        new StaticOptions<AppSurfaceLocalSecretsOptions>(new AppSurfaceLocalSecretsOptions
        {
            Posture = LocalSecretsPostureMode.SingleMachineSelfHosted,
            ApplicationName = "fixture-app",
            KeyPrefix = "payments"
        }),
        localStore,
        normalizer);
    Require(localProvider.GetValue<string>("Development", exactKey) != null, "old local exact logical key");
    Require(localProvider.GetValue<string>("Development", lowerKey) == null, "old local case variant divergence");
    rows.Add(new("local", "Payments:ApiKey", "found", "EXPECTED OLD: exact persisted identity"));
    rows.Add(new("local", "payments:apikey", "missing", "EXPECTED OLD: distinct persisted identity"));

    var googleOptions = new AppSurfaceGoogleSecretManagerOptions { ProjectId = "fixture-project", DefaultVersion = "1" };
    googleOptions.MapSecret(exactKey, "fixture-google-secret");
    var googleClient = new FixtureGoogleClient();
    var googleProvider = new GoogleSecretManagerConfigProvider(
        new StaticOptions<AppSurfaceGoogleSecretManagerOptions>(googleOptions),
        googleClient);
    var exactGoogle = googleProvider.ResolveValue<string>("Production", exactKey);
    var lowerGoogle = googleProvider.ResolveValue<string>("Production", lowerKey);
    Require(exactGoogle.Status == GoogleSecretManagerResultStatus.Found, "old Google exact logical key");
    Require(lowerGoogle.Status == GoogleSecretManagerResultStatus.Unclaimed, "old Google case variant divergence");
    rows.Add(new("google", "Payments:ApiKey", exactGoogle.Status.ToString(), "EXPECTED OLD: exact mapping only"));
    rows.Add(new("google", "payments:apikey", lowerGoogle.Status.ToString(), "EXPECTED OLD: case variant unclaimed"));

    var duplicateOptions = new AppSurfaceGoogleSecretManagerOptions { ProjectId = "fixture-project", DefaultVersion = "1" };
    duplicateOptions.MapSecret(exactKey, "first");
    duplicateOptions.MapSecret(exactKey, "second");
    var duplicateResult = "none";
    try
    {
        _ = new GoogleSecretManagerConfigProvider(
            new StaticOptions<AppSurfaceGoogleSecretManagerOptions>(duplicateOptions),
            new FixtureGoogleClient());
        Require(false, "old Google duplicate mapping divergence");
    }
    catch (Microsoft.Extensions.Options.OptionsValidationException)
    {
        duplicateResult = "throws OptionsValidationException";
    }
    rows.Add(new("google", "duplicate Payments:ApiKey", duplicateResult, "EXPECTED OLD: duplicate mapping is rejected during provider construction"));

    var invalidGoogle = new GoogleSecretManagerConfigProvider(
        new StaticOptions<AppSurfaceGoogleSecretManagerOptions>(new AppSurfaceGoogleSecretManagerOptions
        {
            ProjectId = "fixture-project",
            DefaultVersion = "1"
        }.MapSecret(exactKey, "invalid-payload")),
        new FixtureGoogleClient(invalidUtf8: true)).ResolveValue<string>("Production", exactKey);
    Require(invalidGoogle.Status == GoogleSecretManagerResultStatus.InvalidPayload, "old Google codec failure");
    rows.Add(new("google", "codec invalid UTF-8", invalidGoogle.Status.ToString(), "EXPECTED OLD: invalid payload diagnostic"));

    var invalidLocal = new AppSurfaceLocalSecretProvider(
        new StaticOptions<AppSurfaceLocalSecretsOptions>(new AppSurfaceLocalSecretsOptions
        {
            Posture = LocalSecretsPostureMode.SingleMachineSelfHosted,
            ApplicationName = "fixture-app",
            KeyPrefix = "payments"
        }),
        new InMemoryAppSurfaceLocalSecretStoreWithValue(exactIdentity.Identity!, "not-an-int"),
        normalizer).ResolveValue<int>("Development", exactKey);
    Require(invalidLocal.Status == LocalSecretResultStatus.ConversionFailed, "old LocalSecrets codec failure");
    rows.Add(new("local", "codec string-to-int", invalidLocal.Status.ToString(), "EXPECTED OLD: conversion failure diagnostic"));

    Console.WriteLine("OLD DIVERGENCE TABLE (value-free; archived 0.1.0 packages)");
    Console.WriteLine("SOURCE | CASE | OLD RESULT | EXPECTED OLD DIVERGENCE");
    foreach (var row in rows)
    {
        Console.WriteLine($"{row.Source} | {row.Case} | {row.Result} | {row.Expected}");
    }
}

static void Require(bool condition, string check)
{
    if (!condition)
    {
        throw new InvalidOperationException($"Compatibility fixture failed: {check}.");
    }
}

file sealed record OldProbeRow(string Source, string Case, string Result, string Expected);

file sealed class FixtureFileLocationProvider(string directory) : IConfigFileLocationProvider
{
    public string Directory { get; } = directory;
}

file sealed class InMemoryAppSurfaceLocalSecretStoreWithValue : IAppSurfaceLocalSecretStore
{
    private readonly AppSurfaceLocalSecretIdentity _identity;
    private readonly string _value;

    public InMemoryAppSurfaceLocalSecretStoreWithValue(AppSurfaceLocalSecretIdentity identity, string value)
    {
        _identity = identity;
        _value = value;
    }

    public string Name => "FixtureLocalStore";
    public AppSurfaceLocalSecretResult Get(AppSurfaceLocalSecretIdentity identity) =>
        identity.StorageName == _identity.StorageName
            ? AppSurfaceLocalSecretResult.Found(_value, Name)
            : AppSurfaceLocalSecretResult.Missing(Name);
    public AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value) => throw new NotSupportedException();
    public AppSurfaceLocalSecretResult Delete(AppSurfaceLocalSecretIdentity identity) => throw new NotSupportedException();
    public AppSurfaceLocalSecretListResult List(string applicationName, string environment, string? keyPrefix) =>
        AppSurfaceLocalSecretListResult.Found([], Name);
    public AppSurfaceLocalSecretResult Doctor(string applicationName, string environment, string? keyPrefix) =>
        AppSurfaceLocalSecretResult.Missing(Name);
}

file sealed class LegacyProvider : IConfigProvider
{
    public int Priority => 1;
    public string Name => nameof(LegacyProvider);
    public T? GetValue<T>(string environment, string key) =>
        key == "Payments:ApiKey" && typeof(T) == typeof(string) ? (T)(object)"provider-value" : default;
}

file sealed class LegacyManager : IConfigManager
{
    public int Priority => 0;
    public string Name => nameof(LegacyManager);
    public T? GetValue<T>(string environment, string key) =>
        key == "Payments:ApiKey" && typeof(T) == typeof(string) ? (T)(object)"manager-value" : default;
}

file sealed class LegacyEnvironmentProvider : IEnvironmentProvider
{
    private readonly Dictionary<string, string> _values;

    public LegacyEnvironmentProvider(params (string Name, string Value)[] values)
    {
        _values = values.ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);
    }

    public string Environment => "Development";
    public bool IsDevelopment => true;
    public string? GetEnvironmentVariable(string name, string? defaultValue = null) =>
        _values.TryGetValue(name, out var value) ? value : defaultValue;
}

file sealed class StringConfig : Config<string>
{
}

file sealed class LegacyAuditProvider : IConfigProviderAuditDiagnostics
{
    public ConfigProviderAuditResolution ResolveForAudit(
        string environment,
        string key,
        Type valueType,
        ConfigAuditSourceRole role) =>
        new(
            key,
            ConfigAuditEntryState.Resolved,
            "audit-value",
            [new ConfigAuditSourceRecord
            {
                Kind = ConfigAuditSourceKind.Provider,
                ProviderName = nameof(LegacyAuditProvider),
                ConfigPath = key,
                AppliedToPath = key,
                Role = role
            }],
            []);

    public IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment) => [];
}

file sealed class StaticOptions<T>(T value) : Microsoft.Extensions.Options.IOptions<T>
    where T : class
{
    public T Value { get; } = value;
}

file sealed class FixtureGoogleClient : IAppSurfaceGoogleSecretManagerClient
{
    private readonly bool _invalidUtf8;

    public FixtureGoogleClient(bool invalidUtf8 = false)
    {
        _invalidUtf8 = invalidUtf8;
    }

    public string? LastResourceName { get; private set; }

    public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout)
    {
        LastResourceName = resourceName;
        return new AppSurfaceGoogleSecretPayload(
            _invalidUtf8 ? [0xC3, 0x28] : "fixture-google-value"u8.ToArray(),
            resourceName);
    }
}
