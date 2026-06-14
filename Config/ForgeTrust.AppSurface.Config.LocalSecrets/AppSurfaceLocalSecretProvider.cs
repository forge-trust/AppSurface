using System.Collections.Concurrent;
using System.Text.Json;
using ForgeTrust.AppSurface.Config;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>
/// AppSurface configuration provider that resolves values from the local secret store.
/// </summary>
/// <remarks>
/// The provider sits above file configuration and below environment variables. Only true missing secrets fall through;
/// store, posture, identity, and conversion failures are terminal when fail-closed behavior is enabled.
/// </remarks>
public sealed class AppSurfaceLocalSecretProvider : IConfigProvider, IConfigProviderTerminalDiagnosticProvider
{
    private readonly AppSurfaceLocalSecretsOptions _options;
    private readonly IAppSurfaceLocalSecretStore _store;
    private readonly AppSurfaceLocalSecretIdentityNormalizer _normalizer;
    private readonly ConcurrentDictionary<string, AppSurfaceLocalSecretDiagnostic> _terminalDiagnostics = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="AppSurfaceLocalSecretProvider"/> class.
    /// </summary>
    /// <param name="options">LocalSecrets options.</param>
    /// <param name="store">The local secret store.</param>
    /// <param name="normalizer">The identity normalizer.</param>
    public AppSurfaceLocalSecretProvider(
        IOptions<AppSurfaceLocalSecretsOptions> options,
        IAppSurfaceLocalSecretStore store,
        AppSurfaceLocalSecretIdentityNormalizer normalizer)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _store = store;
        _normalizer = normalizer;
    }

    /// <inheritdoc />
    public int Priority => 5;

    /// <inheritdoc />
    public string Name => nameof(AppSurfaceLocalSecretProvider);

    /// <inheritdoc />
    public T? GetValue<T>(string environment, string key)
    {
        var resolution = ResolveValue<T>(environment, key);
        return resolution.Status == LocalSecretResultStatus.Found
            ? resolution.Value
            : default;
    }

    /// <summary>
    /// Resolves a local secret and returns the structured LocalSecrets status before config-provider adaptation.
    /// </summary>
    /// <typeparam name="T">The requested configuration value type.</typeparam>
    /// <param name="environment">The AppSurface environment being resolved.</param>
    /// <param name="key">The logical AppSurface configuration key.</param>
    /// <returns>The typed LocalSecrets resolution.</returns>
    public AppSurfaceLocalSecretResolution<T> ResolveValue<T>(string environment, string key)
    {
        _terminalDiagnostics.TryRemove(CacheKey(environment, key), out _);

        if (!IsPostureAllowed(environment, out var postureDiagnostic))
        {
            var resolution = AppSurfaceLocalSecretResolution<T>.NotFound(
                LocalSecretResultStatus.DisabledByPosture,
                postureDiagnostic,
                Name);
            RememberTerminalIfNeeded(environment, key, resolution);
            return resolution;
        }

        var identityResult = _normalizer.Normalize(_options.ApplicationName, environment, _options.KeyPrefix, key);
        if (!identityResult.Succeeded)
        {
            var resolution = AppSurfaceLocalSecretResolution<T>.NotFound(
                LocalSecretResultStatus.InvalidIdentity,
                identityResult.Diagnostic!,
                Name);
            RememberTerminalIfNeeded(environment, key, resolution);
            return resolution;
        }

        AppSurfaceLocalSecretResult result;
        try
        {
            result = _store.Get(identityResult.Identity!);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            result = AppSurfaceLocalSecretResult.NotFound(
                LocalSecretResultStatus.ProviderFailed,
                new AppSurfaceLocalSecretDiagnostic(
                    "local-secret-provider-threw",
                    "Local secret provider failed unexpectedly.",
                    $"The local secret store threw {ex.GetType().Name}.",
                    "Run `appsurface secrets doctor` and inspect application logs; do not print raw secret values.",
                    _options.DocsHint,
                    retryable: true),
                _store.Name);
        }

        if (result.Status == LocalSecretResultStatus.Missing)
        {
            return AppSurfaceLocalSecretResolution<T>.NotFound(
                LocalSecretResultStatus.Missing,
                result.Diagnostic!,
                result.Source);
        }

        if (result.Status != LocalSecretResultStatus.Found)
        {
            var resolution = AppSurfaceLocalSecretResolution<T>.NotFound(
                result.Status,
                result.Diagnostic!,
                result.Source);
            RememberTerminalIfNeeded(environment, key, resolution);
            return resolution;
        }

        if (TryConvert<T>(result.Value ?? string.Empty, out var converted, out var diagnostic))
        {
            return AppSurfaceLocalSecretResolution<T>.Found(converted, result.Source);
        }

        var conversionResolution = AppSurfaceLocalSecretResolution<T>.NotFound(
            LocalSecretResultStatus.ConversionFailed,
            diagnostic,
            result.Source);
        RememberTerminalIfNeeded(environment, key, conversionResolution);
        return conversionResolution;
    }

    /// <inheritdoc />
    public bool TryGetTerminalDiagnostic(
        string environment,
        string key,
        out ConfigProviderTerminalDiagnostic diagnostic)
    {
        if (_terminalDiagnostics.TryGetValue(CacheKey(environment, key), out var localDiagnostic)
            && _options.FailClosedOnStoreFailure)
        {
            diagnostic = localDiagnostic.ToTerminalDiagnostic();
            return true;
        }

        diagnostic = null!;
        return false;
    }

    private bool IsPostureAllowed(string environment, out AppSurfaceLocalSecretDiagnostic diagnostic)
    {
        if (_options.Posture == LocalSecretsPostureMode.Disabled)
        {
            diagnostic = new AppSurfaceLocalSecretDiagnostic(
                "local-secret-posture-disabled",
                "LocalSecrets is disabled.",
                "The LocalSecrets posture mode is Disabled.",
                "Remove the LocalSecrets module or choose DevelopmentOnly/SingleMachineSelfHosted deliberately.",
                _options.DocsHint);
            return false;
        }

        if (_options.Posture == LocalSecretsPostureMode.DevelopmentOnly
            && !_options.DevelopmentEnvironmentNames.Contains(environment))
        {
            diagnostic = new AppSurfaceLocalSecretDiagnostic(
                "local-secret-posture-disabled",
                "LocalSecrets is not enabled for this environment.",
                "The default DevelopmentOnly posture prevents local machine secrets from acting like a production vault.",
                "Use environment variables, key-per-file, or a remote vault; choose SingleMachineSelfHosted only for explicit single-machine hosting.",
                _options.DocsHint);
            return false;
        }

        diagnostic = null!;
        return true;
    }

    private static bool TryConvert<T>(string raw, out T? value, out AppSurfaceLocalSecretDiagnostic diagnostic)
    {
        try
        {
            if (typeof(T) == typeof(string))
            {
                value = (T)(object)raw;
                diagnostic = null!;
                return true;
            }

            var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            if (targetType.IsEnum)
            {
                value = (T)Enum.Parse(targetType, raw, ignoreCase: true);
                diagnostic = null!;
                return true;
            }

            if (targetType == typeof(Guid))
            {
                value = (T)(object)Guid.Parse(raw);
                diagnostic = null!;
                return true;
            }

            if (targetType.IsPrimitive || targetType == typeof(decimal))
            {
                value = (T)Convert.ChangeType(raw, targetType, System.Globalization.CultureInfo.InvariantCulture);
                diagnostic = null!;
                return true;
            }

            value = JsonSerializer.Deserialize<T>(raw);
            diagnostic = null!;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidCastException or OverflowException or JsonException)
        {
            value = default;
            diagnostic = new AppSurfaceLocalSecretDiagnostic(
                "local-secret-conversion-failed",
                "Local secret value could not be converted.",
                $"The local secret text could not bind to {typeof(T).Name}.",
                "Replace the secret with the expected scalar text or JSON object shape.",
                "local-secrets-troubleshooting");
            return false;
        }
    }

    private void RememberTerminal(string environment, string key, AppSurfaceLocalSecretDiagnostic diagnostic) =>
        _terminalDiagnostics[CacheKey(environment, key)] = diagnostic;

    private void RememberTerminalIfNeeded<T>(
        string environment,
        string key,
        AppSurfaceLocalSecretResolution<T> resolution)
    {
        if (resolution.Status != LocalSecretResultStatus.Found
            && resolution.Status != LocalSecretResultStatus.Missing
            && resolution.Diagnostic != null)
        {
            RememberTerminal(environment, key, resolution.Diagnostic);
        }
    }

    private static string CacheKey(string environment, string key) => $"{environment}\0{key}";
}
