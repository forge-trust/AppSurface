using System.Diagnostics.Metrics;

namespace ForgeTrust.AppSurface.Config;

/// <summary>Emits diagnostic counters without key, environment, source, value, or exception tags.</summary>
/// <remarks>
/// The meter is process-owned. Unknown providers and codes collapse to reviewed fallback tags.
/// Listener failures cannot change resolution. Notice counts include repeated use before log suppression.
/// </remarks>
internal static class ConfigDiagnosticMetrics
{
    internal const string MeterName = "ForgeTrust.AppSurface.Config";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> TerminalFailures = Meter.CreateCounter<long>(
        "appsurface.config.terminal", "{failure}", "Terminal configuration resolution failures.");
    private static readonly Counter<long> CompatibilityUses = Meter.CreateCounter<long>(
        "appsurface.config.notice", "{notice}", "Configuration notices before log suppression.");

    /// <summary>Records one terminal outcome using reviewed category tags.</summary>
    internal static void Terminal(string code, string provider) => Record(TerminalFailures, code, provider, "config-provider-failed");

    /// <summary>Records a compatibility or provider notice using reviewed category tags.</summary>
    internal static void Notice(string code, string provider) => Record(CompatibilityUses, code, provider, "config-provider-notice");

    private static void Record(Counter<long> counter, string code, string provider, string fallback)
    {
        var providerCategory = provider switch
        {
            "Application" or nameof(EnvironmentConfigProvider) or nameof(FileBasedConfigProvider)
                or "AppSurfaceLocalSecretProvider" or "GoogleSecretManagerConfigProvider" => provider,
            _ => "custom"
        };
        try
        {
            counter.Add(1, new KeyValuePair<string, object?>("code", ConfigDiagnosticCatalog.SafeCode(code, fallback)),
                new KeyValuePair<string, object?>("provider", providerCategory));
        }
        catch (Exception)
        {
            // Metric listeners are observers; instrumentation cannot alter configuration behavior.
        }
    }
}
