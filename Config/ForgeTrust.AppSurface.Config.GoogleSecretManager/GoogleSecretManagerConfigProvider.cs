using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
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
public sealed class GoogleSecretManagerConfigProvider : IConfigProvider, IConfigProviderTerminalDiagnosticProvider, IConfigProviderAuditDiagnostics,
    IConfigSecretProvider, IConfigSecretDeclarationSource, IConfigCompositionValueProvider, IConfigProviderClaimInspector
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);
    /// <summary>The stable provider id used by file-declared references.</summary>
    public const string ProviderId = "google-secret-manager";

    private readonly AppSurfaceGoogleSecretManagerOptions _options;
    private readonly IAppSurfaceGoogleSecretManagerClient _client;
    private readonly ConcurrentDictionary<string, ConfigProviderTerminalDiagnostic> _terminalDiagnostics = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CachedSecret> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CachedSecret> _childCache = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly string _optionsFingerprint;

    /// <summary>
    /// Initializes a new instance of the <see cref="GoogleSecretManagerConfigProvider"/> class.
    /// </summary>
    /// <param name="options">Google Secret Manager options.</param>
    /// <param name="client">The Secret Manager client seam.</param>
    public GoogleSecretManagerConfigProvider(
        IOptions<AppSurfaceGoogleSecretManagerOptions> options,
        IAppSurfaceGoogleSecretManagerClient client)
        : this(options, client, null)
    {
    }

    /// <summary>Initializes a provider with a private options snapshot and an optional cache clock.</summary>
    /// <param name="options">Options copied once, including mappings and conventions.</param>
    /// <param name="client">The synchronous Secret Manager client seam.</param>
    /// <param name="timeProvider">Optional clock used for cache TTL measurement; defaults to the system clock.</param>
    /// <remarks>Later host-option mutations do not reconfigure this singleton or its cache. Rebuild the provider
    /// to adopt new options. The original two-argument constructor remains available for compiled consumers.</remarks>
    public GoogleSecretManagerConfigProvider(
        IOptions<AppSurfaceGoogleSecretManagerOptions> options,
        IAppSurfaceGoogleSecretManagerClient client,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(client);

        _options = options.Value.CreateSnapshot();
        var validation = new AppSurfaceGoogleSecretManagerOptionsValidator().Validate(null, _options);
        if (validation.Failed)
        {
            throw new OptionsValidationException(
                nameof(AppSurfaceGoogleSecretManagerOptions),
                typeof(AppSurfaceGoogleSecretManagerOptions),
                validation.Failures);
        }

        _client = client;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _optionsFingerprint = CreateOptionsFingerprint(_options);
    }

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <summary>Checks descriptor syntax and the configured version policy without remote I/O.</summary>
    /// <remarks>Short ids use the snapshot's project and default version. Full resources must contain exactly one
    /// project, secret and version and cannot carry a separate version. Named aliases remain supported, and
    /// latest requires explicit opt-in in every environment, as with legacy mappings.</remarks>
    /// <param name="reference">The opaque resource input and destination metadata.</param>
    /// <returns>Supported for a locally valid reference; otherwise Invalid.</returns>
    public ConfigSecretReferenceValidation ValidateReference(ConfigSecretReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return TryCreateReference(reference.LogicalPath, reference.Key, reference.Version, out _)
            ? ConfigSecretReferenceValidation.Supported()
            : ConfigSecretReferenceValidation.Invalid();
    }

    /// <inheritdoc />
    public ConfigSecretProviderResolution Resolve(ConfigSecretReference reference, ConfigSecretResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(context);

        if (!TryCreateReference(reference.LogicalPath, reference.Key, reference.Version, out var secretReference))
            return ConfigSecretProviderResolution.InvalidReference(ProviderId);

        var timeout = MinTimeout(_options.LookupTimeout, context.Remaining);
        if (timeout <= TimeSpan.Zero)
            return ConfigSecretProviderResolution.Unavailable(ProviderId);

        var payloadResult = TryGetPayload(reference.Environment, secretReference, timeout, useChildCache: true);
        return ToSecretResolution(payloadResult);
    }

    /// <summary>Reports intersecting explicit mappings and an effective convention on the requested root.</summary>
    /// <param name="rootLogicalPath">The original requested spelling, preserved for the legacy convention predicate.</param>
    /// <param name="secretDestinationPaths">Canonical secret destinations; plain mapped siblings are also reported.</param>
    /// <returns>Value-free claims with original mapping spellings for the compiler to compare canonically.</returns>
    /// <remarks>Only explicit mapping comparisons normalize dots/colons and ignore case. Convention matching
    /// remains ordinal on the original root and never probes descendants. No remote I/O occurs.</remarks>
    public IReadOnlyList<ConfigSecretConfiguredClaim> InspectClaims(
        string rootLogicalPath,
        IReadOnlyList<string> secretDestinationPaths)
    {
        ArgumentNullException.ThrowIfNull(rootLogicalPath);
        ArgumentNullException.ThrowIfNull(secretDestinationPaths);

        var claims = new List<ConfigSecretConfiguredClaim>();
        var rootSegments = SplitLogicalPath(rootLogicalPath);
        foreach (var mapping in _options.Mappings)
        {
            var mappingSegments = SplitLogicalPath(mapping.LogicalKey);
            if (!IntersectsRoot(rootSegments, mappingSegments))
                continue;

            claims.Add(new ConfigSecretConfiguredClaim(
                rootSegments.Length == mappingSegments.Length
                    ? ConfigSecretConfiguredClaimKind.RootMapping
                    : ConfigSecretConfiguredClaimKind.ExactMapping,
                mapping.LogicalKey,
                ProviderId,
                mapping.SecretIdOrResourceName,
                mapping.Version));
        }

        var convention = _options.Conventions.SingleOrDefault(convention =>
            rootLogicalPath.StartsWith(convention.LogicalKeyPrefix, StringComparison.Ordinal));
        if (convention != null
            && !_options.Mappings.Any(mapping => string.Equals(mapping.LogicalKey, rootLogicalPath, StringComparison.Ordinal)))
        {
            var conventionReference = GoogleSecretManagerSecretReference.FromConvention(_options, convention, rootLogicalPath);
            claims.Add(new ConfigSecretConfiguredClaim(
                ConfigSecretConfiguredClaimKind.RootConvention,
                rootLogicalPath,
                ProviderId,
                conventionReference.ResourceName,
                null));
        }

        return claims;
    }

    /// <summary>Resolves a mapped or convention-owned root as strict UTF-8 text before object binding.</summary>
    /// <param name="environment">The requested environment.</param>
    /// <param name="logicalKey">The original legacy lookup key.</param>
    /// <returns>A sensitive raw result with the legacy priority and fail-closed policy.</returns>
    /// <remarks>Shares the legacy mapping cache and diagnostic path. Every claimed failure, including missing
    /// and invalid UTF-8, is terminal when FailClosedOnProviderFailure is true; otherwise it permits fallback.
    /// Textual JSON null remains a resolved contribution. The composition core owns object conversion.</remarks>
    public ConfigCompositionValueResolution ResolveRaw(string environment, string logicalKey)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logicalKey);

        var resolution = ResolveValue<string>(environment, logicalKey);
        return resolution.Status switch
        {
            GoogleSecretManagerResultStatus.Found =>
                ConfigCompositionValueResolution.Resolved(resolution.Value!, Name, Priority, isSensitive: true),
            GoogleSecretManagerResultStatus.Unclaimed =>
                ConfigCompositionValueResolution.Unclaimed(Name, Priority, isSensitive: true),
            _ when !_options.FailClosedOnProviderFailure =>
                ConfigCompositionValueResolution.Missing(Name, Priority, isSensitive: true),
            _ => ConfigCompositionValueResolution.TerminalFailure(Name, Priority, isSensitive: true,
                retryable: resolution.Diagnostic?.Retryable ?? false)
        };
    }

    /// <inheritdoc />
    public ConfigProviderClaim InspectClaim(string environment, string logicalKey)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logicalKey);
        return TryResolveReference(logicalKey, out _) ? ConfigProviderClaim.MayClaim : ConfigProviderClaim.Unclaimed;
    }

    /// <inheritdoc />
    public int Priority => 10;

    /// <inheritdoc />
    public string Name => nameof(GoogleSecretManagerConfigProvider);

    /// <inheritdoc />
    public T? GetValue<T>(string environment, string key)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(key);

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
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(key);

        _terminalDiagnostics.TryRemove(CacheKey(environment, key), out _);

        if (!TryResolveReference(key, out var secretReference))
        {
            return GoogleSecretManagerConfigResolution<T>.Unclaimed();
        }

        var payloadResult = TryGetPayload(environment, secretReference, _options.LookupTimeout, useChildCache: false, legacyKey: key);
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
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(key);

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
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(valueType);

        if (!TryResolveReference(key, out _))
        {
            return ConfigProviderAuditResolution.Missing(key);
        }

        var method = typeof(GoogleSecretManagerConfigProvider)
            .GetMethod(nameof(ResolveValue))!
            .MakeGenericMethod(valueType);
        object resolution;
        try
        {
            resolution = method.Invoke(this, [environment, key])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }

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
        GoogleSecretManagerSecretReference secretReference,
        TimeSpan timeout,
        bool useChildCache,
        string? legacyKey = null)
    {
        var cacheKey = useChildCache
            ? ChildCacheKey(environment, secretReference.ResourceName)
            : CacheKey(environment, legacyKey!);
        var cache = useChildCache ? _childCache : _cache;
        if (_options.CacheTtl is { } cacheTtl && cache.TryGetValue(cacheKey, out var cached))
        {
            if (_timeProvider.GetUtcNow() - cached.CachedAt <= cacheTtl)
            {
                return PayloadResult.Found(cached.Payload);
            }

            cache.TryRemove(cacheKey, out _);
        }

        try
        {
            var payload = _client.AccessSecretVersion(secretReference.ResourceName, timeout).Data;
            if (_options.CacheTtl != null)
            {
                cache[cacheKey] = new CachedSecret(payload, _timeProvider.GetUtcNow());
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
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
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

    /// <summary>Reuses local options policy and resource construction after declaration-only shape validation.</summary>
    private bool TryCreateReference(string logicalPath, string key, string? version, out GoogleSecretManagerSecretReference reference)
    {
        reference = null!;
        if (string.IsNullOrWhiteSpace(logicalPath)
            || !AppSurfaceGoogleSecretManagerOptionsValidator.IsValidDeclarationReference(_options, key, version))
            return false;

        reference = GoogleSecretManagerSecretReference.FromMapping(_options,
            new AppSurfaceGoogleSecretMapping(logicalPath, key, version));
        return true;
    }

    /// <summary>Splits only logical mappings, preserving empty segments for the compiler's shape diagnostics.</summary>
    private static string[] SplitLogicalPath(string path) => path.Split(['.', ':']);

    /// <summary>Tests equal/ancestor/descendant segment identity without matching partial sibling names.</summary>
    private static bool IntersectsRoot(string[] root, string[] path)
    {
        for (var index = 0; index < Math.Min(root.Length, path.Length); index++)
        {
            if (!string.Equals(root[index], path[index], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static TimeSpan MinTimeout(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private ConfigSecretProviderResolution ToSecretResolution(PayloadResult result)
    {
        return result.Status switch
        {
            GoogleSecretManagerResultStatus.Found => DecodeSecret(result.Payload),
            GoogleSecretManagerResultStatus.Unclaimed => ConfigSecretProviderResolution.Unclaimed(ProviderId),
            GoogleSecretManagerResultStatus.Missing => ConfigSecretProviderResolution.Missing(ProviderId),
            GoogleSecretManagerResultStatus.AccessDenied => ConfigSecretProviderResolution.AccessDenied(ProviderId),
            GoogleSecretManagerResultStatus.Unavailable => ConfigSecretProviderResolution.Unavailable(ProviderId),
            GoogleSecretManagerResultStatus.InvalidResource => ConfigSecretProviderResolution.InvalidReference(ProviderId),
            GoogleSecretManagerResultStatus.InvalidPayload => ConfigSecretProviderResolution.ProviderFailed(ProviderId),
            GoogleSecretManagerResultStatus.ConversionFailed => ConfigSecretProviderResolution.ProviderFailed(ProviderId),
            GoogleSecretManagerResultStatus.Cancelled => ConfigSecretProviderResolution.Unavailable(ProviderId),
            _ => ConfigSecretProviderResolution.ProviderFailed(ProviderId, retryable: true)
        };
    }

    private static ConfigSecretProviderResolution DecodeSecret(byte[] payload)
    {
        try
        {
            return ConfigSecretProviderResolution.Resolved(StrictUtf8.GetString(payload), ConfigSecretSourceMetadata.Create(ProviderId, "remote"));
        }
        catch (DecoderFallbackException)
        {
            return ConfigSecretProviderResolution.ProviderFailed(ProviderId);
        }
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

    private string ChildCacheKey(string environment, string resourceName) =>
        $"{ProviderId}\0{environment}\0{resourceName}\0{_optionsFingerprint}";

    private static string CreateOptionsFingerprint(AppSurfaceGoogleSecretManagerOptions options) =>
        string.Join("\0", options.ProjectId, options.DefaultVersion, options.AllowLatestVersion,
            options.LookupTimeout, options.CacheTtl, options.FailClosedOnProviderFailure);

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
