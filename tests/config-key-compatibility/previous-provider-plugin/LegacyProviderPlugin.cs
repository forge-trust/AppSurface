using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Config.GoogleSecretManager;
using ForgeTrust.AppSurface.Config.LocalSecrets;
using ForgeTrust.AppSurface.Core;

namespace PreviousProviderPlugin;

public sealed class LegacyProviderPlugin : IConfigProvider, IEnvironmentProvider, IConfigProviderAuditDiagnostics
{
    public int Priority => 1;
    public string Name => nameof(LegacyProviderPlugin);
    public string Environment => "Development";
    public bool IsDevelopment => true;

    public T? GetValue<T>(string environment, string key) =>
        typeof(T) == typeof(string) ? (T)(object)$"legacy:{environment}:{key}" : default;

    public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;

    public ConfigProviderAuditResolution ResolveForAudit(
        string environment,
        string key,
        Type valueType,
        ConfigAuditSourceRole role) =>
        new(key, ConfigAuditEntryState.Resolved, "legacy-audit-value", [], []);

    public IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment) => [];

    // Keep the provider binary's references to the concrete LocalSecrets and Google contracts explicit.
    public static string ConcreteContractNames(
        AppSurfaceLocalSecretIdentityNormalizer normalizer,
        AppSurfaceGoogleSecretManagerOptions options) =>
        $"{normalizer.GetType().FullName}:{options.GetType().FullName}";
}
