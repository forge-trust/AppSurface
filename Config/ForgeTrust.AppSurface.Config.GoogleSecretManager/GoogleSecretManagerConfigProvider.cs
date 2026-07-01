using System.Collections.Concurrent;
using System.Text;
using ForgeTrust.AppSurface.Config;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.GoogleSecretManager;

/// <summary>
/// AppSurface configuration provider that resolves claimed keys from Google Secret Manager.
/// </summary>
/// <remarks>
/// The provider sits above LocalSecrets and file configuration but below environment variables. Only explicitly mapped or
/// convention-claimed keys are owned by this provider; unclaimed keys fall through. Claimed-key failures are terminal when
/// fail-closed behavior is enabled.
/// </remarks>
public sealed class GoogleSecretManagerConfigProvider : IConfigProvider, IConfigProviderTerminalDiagnosticProvider, IConfigProviderAuditDiagnostics
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    private readonly AppSurfaceGoogleSecretManagerOptions _options;
    private readonly IAppSurfaceGoogleSecretManagerClient _client;
    private readonly ConcurrentDictionary<string, ConfigProviderTerminalDiagnostic> _terminalDiagnostics = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CachedSecret> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="GoogleSecretManagerConfigProvider"/> class.
    /// </summary>
    /// <param name="options">Google Secret Manager options.</param>
    /// <param name="client">The Secret Manager client seam.</param>
    public GoogleSecretManagerConfigProvider(
        IOptions<AppSurfaceGoogleSecretManagerOptions> options,
        IAppSurfaceGoogleSecretManagerClient client)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(client);

        _options = options.Value;
        var validation = new AppSurfaceGoogleSecretManagerOptionsValidator().Validate(null, _options);
        if (validation.Failed)
        {
            throw new OptionsValidationException(
                nameof(AppSurfaceGoogleSecretManagerOptions),
                typeof(AppSurfaceGoogleSecretManagerOptions),
                validation.Failures);
        }

        _client = client;
    }

    /// <inheritdoc />
    public int Priority => 10;

    /// <inheritdoc />
    public string Name => nameof(GoogleSecretManagerConfigProvider);

    /// <inheritdoc />
    public T? GetValue<T>(string environment, string key)
    {
        var resolution = ResolveValue<T>(environment, key);
        return resolution.Status == GoogleSecretManagerResultStatus.Found ? resolution.Value : default;
    }

    /// <summary>
    /// Resolves a value and returns the structured provider status before config-provider adaptation.
    /// </summary>
    /// <typeparam name="T">The requested configuration value type.</typeparam>
    /// <param name="environment">The AppSurface environment being resolved.</param>
    /// <param name="key">The logical AppSurface configuration key.</param>
    /// <returns>The typed Google Secret Manager resolution.</returns>
    public GoogleSecretManagerConfigResolution<T> ResolveValue<T>(string environment, string key)
    {
        _terminalDiagnostics.TryRemove(CacheKey(environment, key), out _);

        if (!TryResolveReference(key, out var secretReference))
        {
            return GoogleSecretManagerConfigResolution<T>.Unclaimed();
        }

        var payloadResult = TryGetPayload(environment, key, secretReference);
        if (payloadResult.Diagnostic != null)
        {
            return RememberFailure<T>(environment, key, payloadResult.Status, payloadResult.Diagnostic);
        }

        string raw;
        try
        {
            raw = StrictUtf8.GetString(payloadResult.Payload);
        }
        catch (DecoderFallbackException)
        {
            return RememberFailure<T>(
                environment,
                key,
                GoogleSecretManagerResultStatus.InvalidPayload,
                CreateDiagnostic(
                    "google-secret-manager-invalid-secret-payload",
                    "Secret Manager payload is not UTF-8 text.",
                    "The mapped secret version returned bytes that cannot be decoded as UTF-8 text.",
                    "Store AppSurface config secrets as UTF-8 scalar text or JSON."));
        }

        if (ConfigValueConverter.TryConvert<T>(raw, out var converted))
        {
            if (converted == null)
            {
                return RememberFailure<T>(
                    environment,
                    key,
                    GoogleSecretManagerResultStatus.ConversionFailed,
                    CreateDiagnostic(
                        "google-secret-manager-conversion-failed",
                        "Secret Manager value could not be converted.",
                        $"The secret text resolved to null and could not bind to required claimed key {typeof(T).Name}.",
                        "Store a non-null UTF-8 scalar text value or JSON object shape for claimed Google Secret Manager config keys."));
            }

            return GoogleSecretManagerConfigResolution<T>.Found(converted, Name);
        }

        return RememberFailure<T>(
            environment,
            key,
            GoogleSecretManagerResultStatus.ConversionFailed,
            CreateDiagnostic(
                "google-secret-manager-conversion-failed",
                "Secret Manager value could not be converted.",
                $"The secret text could not bind to {typeof(T).Name}.",
                "Replace the secret with the expected scalar text or JSON object shape."));
    }

    /// <inheritdoc />
    public bool TryGetTerminalDiagnostic(
        string environment,
        string key,
        out ConfigProviderTerminalDiagnostic diagnostic)
    {
        if (_options.FailClosedOnProviderFailure
            && _terminalDiagnostics.TryGetValue(CacheKey(environment, key), out diagnostic!))
        {
            return true;
        }

        diagnostic = null!;
        return false;
    }

    /// <inheritdoc />
    public ConfigProviderAuditResolution ResolveForAudit(
        string environment,
        string key,
        Type valueType,
        ConfigAuditSourceRole role)
    {
        if (!TryResolveReference(key, out _))
        {
            return ConfigProviderAuditResolution.Missing(key);
        }

        var method = typeof(GoogleSecretManagerConfigProvider)
            .GetMethod(nameof(ResolveValue))!
            .MakeGenericMethod(valueType);
        var resolution = method.Invoke(this, [environment, key])!;
        var status = (GoogleSecretManagerResultStatus)resolution.GetType().GetProperty(nameof(GoogleSecretManagerConfigResolution<object>.Status))!.GetValue(resolution)!;
        var diagnostic = (ConfigProviderTerminalDiagnostic?)resolution.GetType().GetProperty(nameof(GoogleSecretManagerConfigResolution<object>.Diagnostic))!.GetValue(resolution);
        if (status != GoogleSecretManagerResultStatus.Found)
        {
            return new ConfigProviderAuditResolution(
                key,
                status == GoogleSecretManagerResultStatus.Unclaimed ? ConfigAuditEntryState.Missing : ConfigAuditEntryState.Invalid,
                null,
                [],
                diagnostic == null ? [] : [ToAuditDiagnostic(key, diagnostic)]);
        }

        var value = resolution.GetType().GetProperty(nameof(GoogleSecretManagerConfigResolution<object>.Value))!.GetValue(resolution);
        return new ConfigProviderAuditResolution(
            key,
            ConfigAuditEntryState.Resolved,
            value,
            [
                new ConfigAuditSourceRecord
                {
                    Kind = ConfigAuditSourceKind.Provider,
                    ProviderName = Name,
                    ProviderPriority = Priority,
                    ConfigPath = key,
                    AppliedToPath = key,
                    Role = role,
                    Sensitivity = ConfigAuditSensitivity.Sensitive
                }
            ],
            []);
    }

    /// <inheritdoc />
    public IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment) => [];

    private GoogleSecretManagerConfigResolution<T> RememberFailure<T>(
        string environment,
        string key,
        GoogleSecretManagerResultStatus status,
        ConfigProviderTerminalDiagnostic diagnostic)
    {
        _terminalDiagnostics[CacheKey(environment, key)] = diagnostic;
        return GoogleSecretManagerConfigResolution<T>.Failed(status, diagnostic, Name);
    }

    private PayloadResult TryGetPayload(
        string environment,
        string key,
        GoogleSecretManagerSecretReference secretReference)
    {
        var cacheKey = CacheKey(environment, key);
        if (_options.CacheTtl is { } cacheTtl
            && _cache.TryGetValue(cacheKey, out var cached)
            && DateTimeOffset.UtcNow - cached.CachedAt <= cacheTtl)
        {
            return PayloadResult.Found(cached.Payload);
        }

        try
        {
            var payload = _client.AccessSecretVersion(secretReference.ResourceName, _options.LookupTimeout).Data;
            if (_options.CacheTtl != null)
            {
                _cache[cacheKey] = new CachedSecret(payload, DateTimeOffset.UtcNow);
            }

            return PayloadResult.Found(payload);
        }
        catch (RpcException ex)
        {
            return FromRpcException(ex);
        }
        catch (TimeoutException)
        {
            return PayloadResult.Failed(
                GoogleSecretManagerResultStatus.Unavailable,
                CreateDiagnostic(
                    "google-secret-manager-unavailable",
                    "Secret Manager lookup timed out.",
                    "The configured lookup timeout elapsed before a secret value was returned.",
                    "Check Google Cloud connectivity and increase LookupTimeout only after verifying the provider is healthy.",
                    retryable: true));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return PayloadResult.Failed(
                GoogleSecretManagerResultStatus.ProviderFailed,
                CreateDiagnostic(
                    "google-secret-manager-unavailable",
                    "Secret Manager provider failed unexpectedly.",
                    $"The provider threw {ex.GetType().Name}.",
                    "Check application logs and Google Cloud client configuration; do not print raw secret values.",
                    retryable: true));
        }
    }

    private static PayloadResult FromRpcException(RpcException exception) =>
        exception.StatusCode switch
        {
            StatusCode.NotFound => PayloadResult.Failed(
                GoogleSecretManagerResultStatus.Missing,
                CreateDiagnostic(
                    "google-secret-manager-secret-missing",
                    "Secret Manager secret version was not found.",
                    "The mapped secret or version does not exist or is not visible to the current identity.",
                    "Create the secret version, correct the mapping, or use an environment variable as a temporary emergency override.")),
            StatusCode.PermissionDenied or StatusCode.Unauthenticated => PayloadResult.Failed(
                GoogleSecretManagerResultStatus.AccessDenied,
                CreateDiagnostic(
                    "google-secret-manager-access-denied",
                    "Secret Manager access was denied.",
                    $"Google Secret Manager returned {exception.StatusCode}.",
                    "Grant the runtime identity secretmanager.versions.access for the mapped secret version.")),
            StatusCode.InvalidArgument => PayloadResult.Failed(
                GoogleSecretManagerResultStatus.InvalidResource,
                CreateDiagnostic(
                    "google-secret-manager-invalid-secret-resource",
                    "Secret Manager resource mapping is invalid.",
                    "Google Secret Manager rejected the configured secret resource name.",
                    "Use a full projects/{project}/secrets/{secret}/versions/{version} resource name or configure ProjectId, secret id, and version separately.")),
            StatusCode.Cancelled => PayloadResult.Failed(
                GoogleSecretManagerResultStatus.Cancelled,
                CreateDiagnostic(
                    "google-secret-manager-cancelled",
                    "Secret Manager lookup was cancelled.",
                    "The underlying Google Cloud call was cancelled before it returned a value.",
                    "Retry after checking application shutdown and request cancellation paths.",
                    retryable: true)),
            StatusCode.Unavailable or StatusCode.DeadlineExceeded => PayloadResult.Failed(
                GoogleSecretManagerResultStatus.Unavailable,
                CreateDiagnostic(
                    "google-secret-manager-unavailable",
                    "Secret Manager is unavailable.",
                    $"Google Secret Manager returned {exception.StatusCode}.",
                    "Check Google Cloud service health, networking, and runtime credentials.",
                    retryable: true)),
            _ => PayloadResult.Failed(
                GoogleSecretManagerResultStatus.ProviderFailed,
                CreateDiagnostic(
                    "google-secret-manager-unavailable",
                    "Secret Manager provider failed unexpectedly.",
                    $"Google Secret Manager returned {exception.StatusCode}.",
                    "Check application logs and Google Cloud client configuration; do not print raw secret values.",
                    retryable: true))
        };

    private bool TryResolveReference(string key, out GoogleSecretManagerSecretReference secretReference)
    {
        var explicitMappings = _options.Mappings
            .Where(mapping => string.Equals(mapping.LogicalKey, key, StringComparison.Ordinal))
            .ToList();
        if (explicitMappings.Count == 1)
        {
            secretReference = GoogleSecretManagerSecretReference.FromMapping(_options, explicitMappings[0]);
            return true;
        }

        var conventions = _options.Conventions
            .Where(convention => key.StartsWith(convention.LogicalKeyPrefix, StringComparison.Ordinal))
            .ToList();
        if (conventions.Count == 1)
        {
            secretReference = GoogleSecretManagerSecretReference.FromConvention(_options, conventions[0], key);
            return true;
        }

        secretReference = null!;
        return false;
    }

    private static ConfigProviderTerminalDiagnostic CreateDiagnostic(
        string code,
        string problem,
        string cause,
        string fix,
        bool retryable = false) =>
        new(code, problem, cause, fix, "google-secret-manager-troubleshooting", retryable);

    private static ConfigAuditDiagnostic ToAuditDiagnostic(string key, ConfigProviderTerminalDiagnostic diagnostic) =>
        new()
        {
            Severity = ConfigAuditDiagnosticSeverity.Error,
            Code = diagnostic.Code,
            Key = key,
            ConfigPath = key,
            Message = diagnostic.ToDisplayString()
        };

    private static string CacheKey(string environment, string key) => $"{environment}\0{key}";

    private sealed record CachedSecret(byte[] Payload, DateTimeOffset CachedAt);

    private sealed record PayloadResult(
        GoogleSecretManagerResultStatus Status,
        byte[] Payload,
        ConfigProviderTerminalDiagnostic? Diagnostic)
    {
        public static PayloadResult Found(byte[] payload) =>
            new(GoogleSecretManagerResultStatus.Found, payload, null);

        public static PayloadResult Failed(
            GoogleSecretManagerResultStatus status,
            ConfigProviderTerminalDiagnostic diagnostic) =>
            new(status, [], diagnostic);
    }
}
