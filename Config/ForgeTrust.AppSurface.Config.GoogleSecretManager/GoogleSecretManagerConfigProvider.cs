using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using ForgeTrust.AppSurface.Config;
using Google.Cloud.SecretManager.V1;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.GoogleSecretManager;

/// <summary>Resolves explicitly claimed AppSurface keys from Google Secret Manager.</summary>
public sealed class GoogleSecretManagerConfigProvider : IConfigProvider, IConfigProviderAuditDiagnostics,
    IConfigSecretProvider, IConfigSecretDeclarationSource, IConfigCompositionValueProvider, IConfigProviderClaimInspector
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    /// <summary>Stable provider identifier used by file declarations and audit claims.</summary>
    public const string ProviderId = "google-secret-manager";
    private readonly AppSurfaceGoogleSecretManagerOptions _options;
    private readonly IAppSurfaceGoogleSecretManagerClient _client;
    private readonly FrozenDictionary<AppSurfaceConfigKey, GoogleSecretManagerSecretReference> _explicit;
    private readonly IReadOnlyList<AppSurfaceGoogleSecretConvention> _conventions;
    private readonly ConcurrentDictionary<string, NativeClaim> _claims = new(StringComparer.Ordinal);
    private readonly object _claimGate = new();
    private readonly object _cacheGate = new();
    private int _adHocClaimCount;
    private readonly ConcurrentDictionary<string, Lazy<Task<PayloadResult>>> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CachedSecret> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CachedSecret> _childCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConfigProviderTerminalDiagnostic> _terminalDiagnostics = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a provider using the registered Google Secret Manager client.</summary>
    /// <param name="options">Provider configuration.</param>
    /// <param name="client">The read-only Secret Manager client seam.</param>
    public GoogleSecretManagerConfigProvider(
        IOptions<AppSurfaceGoogleSecretManagerOptions> options,
        IAppSurfaceGoogleSecretManagerClient client)
        : this(options, client, serviceProvider: null, timeProvider: null)
    {
    }

    /// <summary>Creates a provider and optionally seeds claims from the finalized declaration registry.</summary>
    /// <param name="options">Provider configuration.</param>
    /// <param name="client">The read-only Secret Manager client seam.</param>
    /// <param name="serviceProvider">The service provider used to resolve the declaration registry.</param>
    public GoogleSecretManagerConfigProvider(
        IOptions<AppSurfaceGoogleSecretManagerOptions> options,
        IAppSurfaceGoogleSecretManagerClient client,
        IServiceProvider? serviceProvider)
        : this(options, client, serviceProvider, timeProvider: null)
    {
    }

    /// <summary>Creates a provider with a testable clock for file-reference deadline and cache behavior.</summary>
    public GoogleSecretManagerConfigProvider(
        IOptions<AppSurfaceGoogleSecretManagerOptions> options,
        IAppSurfaceGoogleSecretManagerClient client,
        TimeProvider timeProvider)
        : this(options, client, serviceProvider: null, timeProvider)
    {
    }

    internal GoogleSecretManagerConfigProvider(
        IOptions<AppSurfaceGoogleSecretManagerOptions> options,
        IAppSurfaceGoogleSecretManagerClient client,
        IServiceProvider? serviceProvider,
        TimeProvider? timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(client);
        _options = options.Value.Snapshot();
        var validation = new AppSurfaceGoogleSecretManagerOptionsValidator().Validate(null, _options);
        if (validation.Failed)
        {
            throw new OptionsValidationException(nameof(AppSurfaceGoogleSecretManagerOptions), typeof(AppSurfaceGoogleSecretManagerOptions), validation.Failures);
        }

        _client = client;
        _timeProvider = timeProvider ?? TimeProvider.System;
        var entries = _options.Mappings.Select(mapping =>
        {
            var reference = GoogleSecretManagerSecretReference.FromMapping(_options, mapping);
            return new ConfigSourceEntry<GoogleSecretManagerSecretReference>(
                reference.Key, mapping.LogicalKey, "explicit", reference.ResourceName, reference);
        }).ToArray();
        var projection = new ConfigSourceProjection<GoogleSecretManagerSecretReference>(entries, ["explicit"], StringComparer.Ordinal);
        var projectionFailure = projection.Entries.FirstOrDefault(entry => entry.Status is not ConfigSourceProjectionStatus.Unique);
        if (projectionFailure != null)
        {
            var identifiers = projectionFailure.Entries
                .Select(entry => entry.NativeIdentifier)
                .Select(identifier => ConfigDiagnosticText.Identifier(identifier))
                .ToArray();
            throw new OptionsValidationException(nameof(AppSurfaceGoogleSecretManagerOptions), typeof(AppSurfaceGoogleSecretManagerOptions),
                [ConfigDiagnosticCatalog.Terminal("config-key-collision", identifiers).ToDisplayString()]);
        }

        _explicit = entries.ToFrozenDictionary(entry => entry.Key, entry => entry.Metadata);
        _conventions = _options.Conventions.ToArray();
        foreach (var reference in _explicit.Values)
        {
            Claim(reference, known: true);
        }

        var registry = serviceProvider?.GetService<ConfigDeclarationRegistry>();
        if (registry != null)
        {
            foreach (var known in registry.Entries)
            {
                if (_explicit.ContainsKey(known.LogicalKey))
                {
                    continue;
                }

                var convention = FindConvention(known.LogicalKey);
                if (convention != null)
                {
                    try
                    {
                        Claim(GoogleSecretManagerSecretReference.FromConvention(_options, convention, known.LogicalKey.Value), known: true);
                    }
                    catch (FormatException)
                    {
                        throw new OptionsValidationException(
                            nameof(AppSurfaceGoogleSecretManagerOptions),
                            typeof(AppSurfaceGoogleSecretManagerOptions),
                            [ConfigDiagnosticCatalog.Terminal("config-key-unrepresentable", ConfigDiagnosticText.Identifier(known.LogicalKey.Value)).ToDisplayString()]);
                    }
                }
            }
        }
    }

    /// <summary>Gets this provider's precedence.</summary>
    public int Priority => 10;
    /// <summary>Gets this provider's stable name.</summary>
    public string Name => nameof(GoogleSecretManagerConfigProvider);

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <inheritdoc />
    public ConfigSecretReferenceValidation ValidateReference(ConfigSecretReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return TryCreateReference(reference.LogicalPath, reference.Key, reference.Version, out _)
            ? ConfigSecretReferenceValidation.Supported() : ConfigSecretReferenceValidation.Invalid();
    }

    /// <inheritdoc />
    public ConfigSecretProviderResolution Resolve(ConfigSecretReference reference, ConfigSecretResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(context);
        if (!TryCreateReference(reference.LogicalPath, reference.Key, reference.Version, out var secret))
            return ConfigSecretProviderResolution.InvalidReference(ProviderId);
        if (context.Remaining <= TimeSpan.Zero) return ConfigSecretProviderResolution.Unavailable(ProviderId);
        try
        {
            var timeout = context.Remaining < _options.LookupTimeout ? context.Remaining : _options.LookupTimeout;
            AppSurfaceGoogleSecretPayload response;
            var cacheKey = ChildCacheKey(reference.Environment, secret.ResourceName);
            if (_options.CacheTtl is { } ttl && _childCache.TryGetValue(cacheKey, out var cached))
            {
                if (_timeProvider.GetElapsedTime(cached.CachedAtTimestamp) <= ttl)
                    response = new AppSurfaceGoogleSecretPayload(cached.Payload, cached.ResolvedResourceName);
                else
                {
                    _childCache.TryRemove(new KeyValuePair<string, CachedSecret>(cacheKey, cached));
                    response = _client.AccessSecretVersion(secret.ResourceName, timeout);
                }
            }
            else
            {
                response = _client.AccessSecretVersion(secret.ResourceName, timeout);
            }
            if (!IsCompatibleResolvedResource(secret.ResourceName, response.ResolvedResourceName))
                return ConfigSecretProviderResolution.ProviderFailed(ProviderId);
            // Reject malformed payloads before adding them to the child cache.
            var value = StrictUtf8.GetString(response.Data);
            if (_options.CacheTtl != null && !_childCache.ContainsKey(cacheKey))
            {
                lock (_cacheGate)
                {
                    _childCache[cacheKey] = new CachedSecret(response.Data, response.ResolvedResourceName!, _timeProvider.GetTimestamp());
                    while (_childCache.Count > _options.CacheCapacity)
                    {
                        var oldest = _childCache.OrderBy(pair => pair.Value.CachedAtTimestamp).ThenBy(pair => pair.Key, StringComparer.Ordinal).First();
                        _childCache.TryRemove(oldest.Key, out _);
                    }
                }
            }
            return ConfigSecretProviderResolution.Resolved(value, ConfigSecretSourceMetadata.Create(ProviderId, "remote"));
        }
        catch (DecoderFallbackException) { return ConfigSecretProviderResolution.ProviderFailed(ProviderId); }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound) { return ConfigSecretProviderResolution.Missing(ProviderId); }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.PermissionDenied or StatusCode.Unauthenticated)
        { return ConfigSecretProviderResolution.AccessDenied(ProviderId); }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Cancelled)
        { return ConfigSecretProviderResolution.Unavailable(ProviderId); }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
        { return ConfigSecretProviderResolution.InvalidReference(ProviderId); }
        catch (TimeoutException)
        { return ConfigSecretProviderResolution.Unavailable(ProviderId); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { return ConfigSecretProviderResolution.ProviderFailed(ProviderId, retryable: true); }
    }

    /// <inheritdoc />
    public IReadOnlyList<ConfigSecretConfiguredClaim> InspectClaims(string rootLogicalPath, IReadOnlyList<string> secretDestinationPaths)
    {
        ArgumentNullException.ThrowIfNull(rootLogicalPath);
        ArgumentNullException.ThrowIfNull(secretDestinationPaths);
        var root = AppSurfaceConfigKey.Parse(rootLogicalPath);
        var claims = _options.Mappings.Where(mapping => IntersectsRoot(root, AppSurfaceConfigKey.Parse(mapping.LogicalKey)))
            .Select(mapping => new ConfigSecretConfiguredClaim(root.Equals(AppSurfaceConfigKey.Parse(mapping.LogicalKey))
                ? ConfigSecretConfiguredClaimKind.RootMapping : ConfigSecretConfiguredClaimKind.ExactMapping,
                mapping.LogicalKey, ProviderId, mapping.SecretIdOrResourceName, mapping.Version)).ToList();
        var convention = _options.Conventions.SingleOrDefault(item => root.IsSameOrDescendantOf(AppSurfaceConfigKey.Parse(item.LogicalKeyPrefix)));
        if (convention != null && !_options.Mappings.Any(mapping => root.Equals(AppSurfaceConfigKey.Parse(mapping.LogicalKey))))
        {
            var reference = GoogleSecretManagerSecretReference.FromConvention(_options, convention, rootLogicalPath);
            claims.Add(new ConfigSecretConfiguredClaim(ConfigSecretConfiguredClaimKind.RootConvention, rootLogicalPath,
                ProviderId, reference.ResourceName, null));
        }
        return claims;
    }

    /// <inheritdoc />
    public ConfigCompositionValueResolution ResolveRaw(string environment, string logicalKey)
    {
        var request = new ConfigProviderRequest(environment, AppSurfaceConfigKey.Parse(logicalKey));
        if (_terminalDiagnostics.TryRemove(DiagnosticKey(environment, request.Key.Value), out var previousDiagnostic))
            return _options.FailClosedOnProviderFailure
                ? ConfigCompositionValueResolution.TerminalFailure(Name, Priority, isSensitive: true, retryable: previousDiagnostic.Retryable)
                : ConfigCompositionValueResolution.Missing(Name, Priority, isSensitive: true);

        if (CheckCancellation(request) is { } cancellation)
            return ConfigCompositionValueResolution.TerminalFailure(Name, Priority, isSensitive: true, retryable: cancellation.Retryable);
        if (!TryResolveReference(request.Key, out var reference, out var lookupDiagnostic))
        {
            if (lookupDiagnostic == null)
                return ConfigCompositionValueResolution.Unclaimed(Name, Priority, isSensitive: true);
            return _options.FailClosedOnProviderFailure
                ? ConfigCompositionValueResolution.TerminalFailure(Name, Priority, isSensitive: true, retryable: lookupDiagnostic.Retryable)
                : ConfigCompositionValueResolution.Missing(Name, Priority, isSensitive: true);
        }

        var payload = GetPayload(request, reference);
        if (payload.Diagnostic != null)
            return _options.FailClosedOnProviderFailure
                ? ConfigCompositionValueResolution.TerminalFailure(Name, Priority, isSensitive: true, retryable: payload.Diagnostic.Retryable)
                : ConfigCompositionValueResolution.Missing(Name, Priority, isSensitive: true);
        try
        {
            return ConfigCompositionValueResolution.Resolved(StrictUtf8.GetString(payload.Payload), Name, Priority, isSensitive: true);
        }
        catch (DecoderFallbackException)
        {
            EvictRejectedPayload(reference, payload);
            return _options.FailClosedOnProviderFailure
                ? ConfigCompositionValueResolution.TerminalFailure(Name, Priority, isSensitive: true)
                : ConfigCompositionValueResolution.Missing(Name, Priority, isSensitive: true);
        }
    }

    /// <inheritdoc />
    public ConfigProviderClaim InspectClaim(string environment, string logicalKey) =>
        TryResolveReference(AppSurfaceConfigKey.Parse(logicalKey), out _, out _) ? ConfigProviderClaim.MayClaim : ConfigProviderClaim.Unclaimed;

    private bool TryCreateReference(string logicalPath, string key, string? version, out GoogleSecretManagerSecretReference reference)
    {
        reference = null!;
        if (string.IsNullOrWhiteSpace(logicalPath) || !AppSurfaceGoogleSecretManagerOptionsValidator.IsValidDeclarationReference(_options, key, version)) return false;
        reference = GoogleSecretManagerSecretReference.FromMapping(_options, new AppSurfaceGoogleSecretMapping(logicalPath, key, version));
        return true;
    }

    private static bool IntersectsRoot(AppSurfaceConfigKey root, AppSurfaceConfigKey path) =>
        root.IsSameOrDescendantOf(path) || path.IsSameOrDescendantOf(root);

    /// <inheritdoc />
    /// <remarks>
    /// Rechecks the resource claim before publishing a value so a collision discovered during a shared fetch
    /// or conversion remains terminal for the pending resolution, including cached values.
    /// </remarks>
    public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (CheckCancellation(request) is { } cancellation) return ConfigProviderValueResult<T>.Terminal(cancellation);
        if (!TryResolveReference(request.Key, out var reference, out var diagnostic))
        {
            return diagnostic == null ? ConfigProviderValueResult<T>.Missing() : ConfigProviderValueResult<T>.Terminal(diagnostic);
        }

        var payload = GetPayload(request, reference);
        var result = ConvertPayload<T>(request, reference, payload);
        if (result.Status == ConfigProviderValueStatus.Terminal)
            _terminalDiagnostics[DiagnosticKey(request.Environment, request.Key.Value)] = result.Diagnostic!;
        else
            _terminalDiagnostics.TryRemove(DiagnosticKey(request.Environment, request.Key.Value), out _);
        return result;
    }

    // Runtime and audit convert the same fetched payload together with its exact resolved provenance.
    private ConfigProviderValueResult<T> ConvertPayload<T>(
        ConfigProviderRequest request, GoogleSecretManagerSecretReference reference, PayloadResult payload)
    {
        if (payload.Diagnostic != null)
        {
            return ConfigProviderValueResult<T>.Terminal(payload.Diagnostic);
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(payload.Payload);
        }
        catch (DecoderFallbackException)
        {
            EvictRejectedPayload(reference, payload);
            return ConfigProviderValueResult<T>.Terminal(ConfigDiagnosticCatalog.Terminal("config-provider-failed", reference.ResourceName));
        }

        if (!ConfigValueConverter.TryConvert<T>(text, out var converted) || converted == null)
        {
            EvictRejectedPayload(reference, payload);
            return ConfigProviderValueResult<T>.Terminal(ConfigDiagnosticCatalog.Terminal("config-provider-failed", reference.ResourceName));
        }

        // A distinct ad-hoc key can poison this claim while the fetch or conversion is in flight.
        return _claims.TryGetValue(reference.ResourceName, out var claim) && claim.Poisoned
            ? ConfigProviderValueResult<T>.Terminal(ConfigDiagnosticCatalog.Terminal("config-key-collision", request.Key.Value))
            : ConfigProviderValueResult<T>.Found(converted);
    }

    /// <summary>Resolves a strict string key through the compatibility surface.</summary>
    /// <param name="environment">The environment name.</param>
    /// <param name="key">The colon-delimited logical key.</param>
    [Obsolete("Use Resolve with ConfigProviderRequest. This compatibility helper uses strict logical-key semantics and throws terminal failures.")]
    public T? GetValue<T>(string environment, string key)
    {
        var parsed = AppSurfaceConfigKey.Parse(key);
        var result = Resolve<T>(new ConfigProviderRequest(environment, parsed));
        return result.Status switch
        {
            ConfigProviderValueStatus.Found => result.Value,
            ConfigProviderValueStatus.Missing => default,
            _ => throw new ConfigurationResolutionException(environment, parsed, Name, result.Diagnostic!)
        };
    }

    /// <summary>Resolves a strict string key through the compatibility result surface.</summary>
    /// <param name="environment">The environment name.</param>
    /// <param name="key">The colon-delimited logical key.</param>
    [Obsolete("Use Resolve with ConfigProviderRequest. This compatibility helper uses strict logical-key semantics and throws terminal failures.")]
    public GoogleSecretManagerConfigResolution<T> ResolveValue<T>(string environment, string key)
    {
        var parsed = AppSurfaceConfigKey.Parse(key);
        var request = new ConfigProviderRequest(environment, parsed);
        var result = Resolve<T>(request);
        return result.Status switch
        {
            ConfigProviderValueStatus.Missing => GoogleSecretManagerConfigResolution<T>.Unclaimed(),
            ConfigProviderValueStatus.Found => GoogleSecretManagerConfigResolution<T>.Found(result.Value!, Name),
            _ when result.Diagnostic?.Code == "config-key-collision" =>
                throw new ConfigurationResolutionException(environment, parsed, Name, result.Diagnostic),
            _ => GoogleSecretManagerConfigResolution<T>.Failed(GoogleSecretManagerResultStatus.ProviderFailed, result.Diagnostic!, Name)
        };
    }

    /// <summary>Resolves a typed value while returning source provenance for audit.</summary>
    /// <param name="request">The scoped typed request.</param>
    /// <param name="valueType">The requested value type.</param>
    /// <param name="role">The audit source role.</param>
    /// <remarks>
    /// Source provenance identifies the version returned by Google, including when a requested alias resolves to
    /// a numeric version or a project ID resolves to a project number. Cached bytes retain that same source name.
    /// </remarks>
    public ConfigProviderAuditResolution ResolveForAudit(ConfigProviderRequest request, Type valueType, ConfigAuditSourceRole role)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(valueType);
        if (CheckCancellation(request) is { } cancellation) return InvalidAudit(request.Key, cancellation);
        if (!TryResolveReference(request.Key, out var reference, out var lookupDiagnostic))
        {
            return lookupDiagnostic == null
                ? ConfigProviderAuditResolution.Missing(request.Key)
                : InvalidAudit(request.Key, lookupDiagnostic);
        }

        var payload = GetPayload(request, reference);
        var method = typeof(GoogleSecretManagerConfigProvider)
            .GetMethod(nameof(ConvertPayload), BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(valueType);
        object rawResult;
        try
        {
            rawResult = method.Invoke(this, [request, reference, payload])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }

        var resultType = rawResult.GetType();
        var status = (ConfigProviderValueStatus)resultType.GetProperty(nameof(ConfigProviderValueResult<string>.Status))!.GetValue(rawResult)!;
        var diagnostic = (ConfigProviderTerminalDiagnostic?)resultType.GetProperty(nameof(ConfigProviderValueResult<string>.Diagnostic))!.GetValue(rawResult);
        var value = resultType.GetProperty(nameof(ConfigProviderValueResult<string>.Value))!.GetValue(rawResult);
        if (status == ConfigProviderValueStatus.Terminal)
        {
            return InvalidAudit(request.Key, diagnostic!);
        }

        return new ConfigProviderAuditResolution(request.Key, ConfigAuditEntryState.Resolved, value,
            [new ConfigAuditSourceRecord
            {
                Kind = ConfigAuditSourceKind.Provider, ProviderName = Name, ProviderPriority = Priority,
                ConfigPath = payload.ResolvedResourceName, AppliedToPath = request.Key.Value, Role = role,
                Sensitivity = ConfigAuditSensitivity.Sensitive
            }], []);
    }

    /// <summary>Gets provider report diagnostics for the supplied environment.</summary>
    /// <param name="environment">The environment being reported.</param>
    public IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment) => [];

    // Explicit caller cancellation throws even for cached reads. An elapsed aggregate audit deadline instead
    // records its stable incomplete-audit diagnostic, before any native identity is claimed or fetched.
    private static ConfigProviderTerminalDiagnostic? CheckCancellation(ConfigProviderRequest request)
    {
        if (request.Scope.IsAudit && request.Scope.CancellationToken.IsCancellationRequested)
            return request.Scope.MarkAuditDeadline();
        request.Scope.CancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    // A slow conversion may reject an older payload after another resolution has refreshed this resource.
    // Remove only the cache object carried by that rejected result, never a newer successful generation.
    private void EvictRejectedPayload(GoogleSecretManagerSecretReference reference, PayloadResult payload)
    {
        if (payload.CacheEntry is { } cached)
            _cache.TryRemove(new KeyValuePair<string, CachedSecret>(reference.ResourceName, cached));
    }

    private static ConfigProviderAuditResolution InvalidAudit(AppSurfaceConfigKey key, ConfigProviderTerminalDiagnostic diagnostic) =>
        new(key, ConfigAuditEntryState.Invalid, null, [], [new ConfigAuditDiagnostic
        {
            Severity = ConfigAuditDiagnosticSeverity.Error, Code = diagnostic.Code, Key = key.Value,
            ConfigPath = key.Value, Message = diagnostic.ToDisplayString()
        }]);

    private PayloadResult GetPayload(
        ConfigProviderRequest request,
        GoogleSecretManagerSecretReference reference)
    {
        if (_options.CacheTtl is { } ttl && _cache.TryGetValue(reference.ResourceName, out var cached))
        {
            if (_timeProvider.GetElapsedTime(cached.CachedAtTimestamp) <= ttl)
            {
                return PayloadResult.Found(cached.Payload, cached.ResolvedResourceName, cached);
            }

            _cache.TryRemove(new KeyValuePair<string, CachedSecret>(reference.ResourceName, cached));
        }

        IDisposable? remoteLease = null;
        if (request.Scope.IsAudit &&
            !request.Scope.TryAcquireRemoteLookup(out remoteLease, out var auditDiagnostic))
        {
            return PayloadResult.Failed(auditDiagnostic!);
        }

        using (remoteLease)
        {
            var lazy = _inFlight.GetOrAdd(reference.ResourceName, _ => new Lazy<Task<PayloadResult>>(
                () => Task.Run(() => Fetch(reference), CancellationToken.None), LazyThreadSafetyMode.ExecutionAndPublication));
            var sharedTask = lazy.Value;
            _ = sharedTask.ContinueWith(
                _ => _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<PayloadResult>>>(reference.ResourceName, lazy)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            try
            {
                return sharedTask.WaitAsync(request.Scope.CancellationToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (request.Scope.IsAudit)
            {
                return PayloadResult.Failed(request.Scope.MarkAuditDeadline());
            }
        }
    }

    private static string ChildCacheKey(string environment, string resourceName) =>
        $"{environment}\0{resourceName}";

    private static string DiagnosticKey(string environment, string logicalKey) =>
        $"{environment}\0{logicalKey}";

    private PayloadResult Fetch(GoogleSecretManagerSecretReference reference)
    {
        try
        {
            var payload = _client.AccessSecretVersion(reference.ResourceName, _options.LookupTimeout);
            if (!IsCompatibleResolvedResource(reference.ResourceName, payload.ResolvedResourceName))
            {
                return PayloadResult.Failed(ConfigDiagnosticCatalog.Terminal("config-provider-failed", reference.ResourceName));
            }

            ClaimResolvedResource(reference, payload.ResolvedResourceName!);
            var bytes = payload.Data;
            CachedSecret? cacheEntry = null;
            if (_options.CacheTtl != null)
            {
                lock (_cacheGate)
                {
                    cacheEntry = new CachedSecret(bytes, payload.ResolvedResourceName!, _timeProvider.GetTimestamp());
                    _cache[reference.ResourceName] = cacheEntry;
                    while (_cache.Count > _options.CacheCapacity)
                    {
                        var oldest = _cache
                            .OrderBy(pair => pair.Value.CachedAtTimestamp)
                            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                            .FirstOrDefault();
                        _cache.TryRemove(oldest.Key, out _);
                    }
                }
            }

            return PayloadResult.Found(bytes, payload.ResolvedResourceName!, cacheEntry);
        }
        catch (OptionsValidationException)
        {
            return PayloadResult.Failed(ConfigDiagnosticCatalog.Terminal("config-key-collision", reference.ResourceName));
        }
        catch (RpcException ex)
        {
            return PayloadResult.Failed(DiagnosticFor(ex, reference.ResourceName));
        }
        catch (TimeoutException)
        {
            return PayloadResult.Failed(RetryableDiagnostic(reference.ResourceName));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            return PayloadResult.Failed(RetryableDiagnostic(reference.ResourceName));
        }
    }

    private static ConfigProviderTerminalDiagnostic DiagnosticFor(RpcException exception, string resourceName)
    {
        if (exception.StatusCode == StatusCode.InvalidArgument)
            return ConfigDiagnosticCatalog.Terminal("config-provider-failed", resourceName);

        var diagnostic = ConfigDiagnosticCatalog.Terminal("config-provider-failed", resourceName);
        var retryable = exception.StatusCode is StatusCode.Cancelled or StatusCode.Unavailable or StatusCode.DeadlineExceeded
            || exception.StatusCode is not (StatusCode.NotFound or StatusCode.PermissionDenied or StatusCode.Unauthenticated);
        return WithRetryability(diagnostic, retryable);
    }

    private static ConfigProviderTerminalDiagnostic RetryableDiagnostic(string resourceName) =>
        WithRetryability(ConfigDiagnosticCatalog.Terminal("config-provider-failed", resourceName), retryable: true);

    private static ConfigProviderTerminalDiagnostic WithRetryability(ConfigProviderTerminalDiagnostic diagnostic, bool retryable) =>
        new(diagnostic.Code, diagnostic.Problem, diagnostic.Cause, diagnostic.Fix, diagnostic.Docs, retryable);

    // AccessSecretVersion returns an authoritative resolved name, not an echo of its request. Numeric versions
    // and secret/location identifiers remain exact. Only version aliases and project ID/number pairs may differ.
    private static bool IsCompatibleResolvedResource(string requested, string? resolved)
    {
        if (StringComparer.Ordinal.Equals(requested, resolved)) return true;
        if (!SecretVersionName.TryParse(requested, out var requestName)
            || !SecretVersionName.TryParse(resolved, out var responseName)) return false;
        return StringComparer.Ordinal.Equals(requestName.SecretId, responseName.SecretId)
            && StringComparer.Ordinal.Equals(requestName.LocationId, responseName.LocationId)
            && (StringComparer.Ordinal.Equals(requestName.ProjectId, responseName.ProjectId)
                || IsNumericIdentifier(requestName.ProjectId) != IsNumericIdentifier(responseName.ProjectId))
            && (StringComparer.Ordinal.Equals(requestName.SecretVersionId, responseName.SecretVersionId)
                || (!IsNumericIdentifier(requestName.SecretVersionId) && IsNumericIdentifier(responseName.SecretVersionId)));
    }

    private static bool IsNumericIdentifier(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);

    // Requested aliases and concrete names share one claim object. Poisoning either name therefore also stops
    // cached and in-flight resolutions of the other name. Resolved aliases consume the bounded runtime claim budget.
    private void ClaimResolvedResource(GoogleSecretManagerSecretReference reference, string resolved)
    {
        lock (_claimGate)
        {
            var requestedClaim = _claims[reference.ResourceName];
            if (_claims.TryGetValue(resolved, out var existing))
            {
                if (existing.Poisoned || requestedClaim.Poisoned
                    || !StringComparer.OrdinalIgnoreCase.Equals(existing.LogicalKey, reference.LogicalKey))
                {
                    existing.Poisoned = requestedClaim.Poisoned = true;
                    throw new OptionsValidationException(nameof(AppSurfaceGoogleSecretManagerOptions), typeof(AppSurfaceGoogleSecretManagerOptions),
                        [ConfigDiagnosticCatalog.Terminal("config-key-collision", resolved).ToDisplayString()]);
                }

                return;
            }

            if (_adHocClaimCount >= _options.MaxAdHocClaims) throw new AdHocClaimCapacityExceededException();
            _claims.TryAdd(resolved, requestedClaim);
            _adHocClaimCount++;
        }
    }

    private bool TryResolveReference(AppSurfaceConfigKey key, out GoogleSecretManagerSecretReference reference, out ConfigProviderTerminalDiagnostic? diagnostic)
    {
        diagnostic = null;
        if (_explicit.TryGetValue(key, out reference!))
        {
            if (_claims.TryGetValue(reference.ResourceName, out var explicitClaim) && explicitClaim.Poisoned)
            {
                reference = null!;
                diagnostic = ConfigDiagnosticCatalog.Terminal("config-key-collision", key.Value);
                return false;
            }

            return true;
        }

        var convention = FindConvention(key);
        if (convention == null)
        {
            reference = null!;
            return false;
        }

        try
        {
            reference = GoogleSecretManagerSecretReference.FromConvention(_options, convention, key.Value);
            Claim(reference, known: false);
            return true;
        }
        catch (OptionsValidationException)
        {
            reference = null!;
            diagnostic = ConfigDiagnosticCatalog.Terminal("config-key-collision", key.Value);
            return false;
        }
        catch (AdHocClaimCapacityExceededException)
        {
            reference = null!;
            diagnostic = ConfigDiagnosticCatalog.Terminal("config-provider-failed", key.Value);
            return false;
        }
        catch (FormatException)
        {
            reference = null!;
            diagnostic = ConfigDiagnosticCatalog.Terminal("config-key-unrepresentable", key.Value);
            return false;
        }
    }

    private AppSurfaceGoogleSecretConvention? FindConvention(AppSurfaceConfigKey key)
    {
        AppSurfaceGoogleSecretConvention? found = null;
        foreach (var convention in _conventions)
        {
            var prefix = AppSurfaceConfigKey.Parse(convention.LogicalKeyPrefix);
            if (!key.IsSameOrDescendantOf(prefix))
            {
                continue;
            }

            found = convention;
        }

        return found;
    }

    private void Claim(GoogleSecretManagerSecretReference reference, bool known)
    {
        lock (_claimGate)
        {
            if (_claims.TryGetValue(reference.ResourceName, out var existing))
            {
                if (existing.Poisoned)
                {
                    throw new OptionsValidationException(nameof(AppSurfaceGoogleSecretManagerOptions), typeof(AppSurfaceGoogleSecretManagerOptions),
                        [ConfigDiagnosticCatalog.Terminal("config-key-collision", reference.ResourceName).ToDisplayString()]);
                }

                if (!StringComparer.OrdinalIgnoreCase.Equals(existing.LogicalKey, reference.LogicalKey))
                {
                    existing.Poisoned = true;
                    throw new OptionsValidationException(nameof(AppSurfaceGoogleSecretManagerOptions), typeof(AppSurfaceGoogleSecretManagerOptions),
                        [ConfigDiagnosticCatalog.Terminal("config-key-collision", reference.ResourceName).ToDisplayString()]);
                }

                return;
            }

            if (!known && _adHocClaimCount >= _options.MaxAdHocClaims)
            {
                throw new AdHocClaimCapacityExceededException();
            }

            _claims.TryAdd(reference.ResourceName, new NativeClaim(reference.LogicalKey));
            if (!known)
            {
                _adHocClaimCount++;
            }
        }
    }

    private sealed class NativeClaim(string logicalKey)
    {
        public string LogicalKey { get; } = logicalKey;
        public volatile bool Poisoned;
    }

    private sealed class AdHocClaimCapacityExceededException : Exception;

    private sealed class CachedSecret
    {
        private readonly byte[] _payload;
        public CachedSecret(byte[] payload, string resolvedResourceName, long cachedAtTimestamp)
        { _payload = (byte[])payload.Clone(); ResolvedResourceName = resolvedResourceName; CachedAtTimestamp = cachedAtTimestamp; }
        public byte[] Payload => (byte[])_payload.Clone();
        public string ResolvedResourceName { get; }
        public long CachedAtTimestamp { get; }
    }

    private sealed class PayloadResult
    {
        private PayloadResult(byte[] payload, string? resolvedResourceName, ConfigProviderTerminalDiagnostic? diagnostic, CachedSecret? cacheEntry)
        { Payload = (byte[])payload.Clone(); ResolvedResourceName = resolvedResourceName; Diagnostic = diagnostic; CacheEntry = cacheEntry; }
        public byte[] Payload { get; }
        public string? ResolvedResourceName { get; }
        public ConfigProviderTerminalDiagnostic? Diagnostic { get; }
        public CachedSecret? CacheEntry { get; }
        public static PayloadResult Found(byte[] payload, string resolvedResourceName, CachedSecret? cacheEntry) => new(payload, resolvedResourceName, null, cacheEntry);
        public static PayloadResult Failed(ConfigProviderTerminalDiagnostic diagnostic) => new([], null, diagnostic, null);
    }
}
