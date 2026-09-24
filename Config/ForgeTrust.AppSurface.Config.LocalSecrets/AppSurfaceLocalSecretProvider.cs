using ForgeTrust.AppSurface.Config;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>Resolves typed AppSurface keys from the configured LocalSecrets store.</summary>
/// <remarks>Resolution is request based. Raw whole-root resolution also supports file-declared secret references.</remarks>
public sealed class AppSurfaceLocalSecretProvider : IConfigProvider, IConfigCompositionValueProvider, IConfigProviderClaimInspector
{
    private readonly AppSurfaceLocalSecretsOptions _options;
    private readonly IAppSurfaceLocalSecretStore _store;
    private readonly AppSurfaceLocalSecretIdentityNormalizer _normalizer;

    /// <summary>Initializes the provider.</summary>
    public AppSurfaceLocalSecretProvider(IOptions<AppSurfaceLocalSecretsOptions> options,
        IAppSurfaceLocalSecretStore store, AppSurfaceLocalSecretIdentityNormalizer normalizer)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(normalizer);
        _options = options.Value;
        _store = store;
        _normalizer = normalizer;
    }

    /// <inheritdoc />
    public int Priority => 5;

    /// <inheritdoc />
    public string Name => nameof(AppSurfaceLocalSecretProvider);

    /// <inheritdoc />
    public ConfigProviderClaim InspectClaim(string environment, string logicalKey) => ConfigProviderClaim.MayClaim;

    /// <inheritdoc />
    public ConfigCompositionValueResolution ResolveRaw(string environment, string logicalKey)
    {
        var key = AppSurfaceConfigKey.Parse(logicalKey).WithInput(ConfigKeyInputOrigin.StrictString, logicalKey);
        var resolution = ResolveTyped<string>(new ConfigProviderRequest(environment, key), out _);
        return resolution.Status switch
        {
            LocalSecretResultStatus.Found => ConfigCompositionValueResolution.Resolved(
                resolution.Value ?? string.Empty, Name, Priority, isSensitive: true),
            LocalSecretResultStatus.Missing => ConfigCompositionValueResolution.Missing(Name, Priority, isSensitive: true),
            _ when !_options.FailClosedOnStoreFailure => ConfigCompositionValueResolution.Missing(Name, Priority, isSensitive: true),
            _ => ConfigCompositionValueResolution.TerminalFailure(Name, Priority, isSensitive: true,
                retryable: resolution.Diagnostic?.Retryable ?? false)
        };
    }

    /// <inheritdoc />
    public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resolution = ResolveTyped<T>(request, out var aliasNotice);
        if (resolution.Status == LocalSecretResultStatus.Missing)
        {
            return ConfigProviderValueResult<T>.Missing();
        }

        if (resolution.Status == LocalSecretResultStatus.Found && resolution.Value is not null)
        {
            return aliasNotice is null
                ? ConfigProviderValueResult<T>.Found(resolution.Value)
                : ConfigProviderValueResult<T>.Found(resolution.Value, aliasNotice);
        }

        return ConfigProviderValueResult<T>.Terminal(ToTerminal(resolution.Diagnostic!, request.Key));
    }

    /// <summary>Resolves a strict string key and returns LocalSecrets status details.</summary>
    [Obsolete("Use AppSurfaceConfigKey and IConfigProvider.Resolve instead.", error: false)]
    public AppSurfaceLocalSecretResolution<T> ResolveValue<T>(string environment, string key) =>
        ResolveTyped<T>(new ConfigProviderRequest(environment,
            AppSurfaceConfigKey.Parse(key).WithInput(ConfigKeyInputOrigin.StrictString, key)), out _);

    /// <summary>Legacy helper retained as a strict terminal boundary.</summary>
    [Obsolete("Use IConfigProvider.Resolve with ConfigProviderRequest.", error: false)]
    public T? GetValue<T>(string environment, string key)
    {
        var parsed = AppSurfaceConfigKey.Parse(key);
        var resolution = ResolveTyped<T>(new ConfigProviderRequest(environment,
            parsed.WithInput(ConfigKeyInputOrigin.StrictString, key)), out _);
        if (resolution.Status == LocalSecretResultStatus.Found)
        {
            return resolution.Value;
        }

        if (resolution.Status != LocalSecretResultStatus.Missing && resolution.Diagnostic is not null)
        {
            throw new ConfigurationResolutionException(environment, parsed, Name, ToTerminal(resolution.Diagnostic, parsed));
        }

        return default;
    }

    private AppSurfaceLocalSecretResolution<T> ResolveTyped<T>(ConfigProviderRequest request,
        out ConfigProviderNotice? aliasNotice)
    {
        aliasNotice = null;
        if (!IsPostureAllowed(request.Environment, out var posture))
        {
            return AppSurfaceLocalSecretResolution<T>.NotFound(LocalSecretResultStatus.DisabledByPosture, posture, Name);
        }

        var identityResult = _normalizer.Normalize(_options.ApplicationName, request.Environment, _options.KeyPrefix, request.Key.Value);
        if (!identityResult.Succeeded)
        {
            return AppSurfaceLocalSecretResolution<T>.NotFound(LocalSecretResultStatus.InvalidIdentity,
                identityResult.Diagnostic!, Name);
        }

        var candidates = BuildCandidates(request);
        AppSurfaceLocalSecretResult? firstFound = null;
        foreach (var candidate in candidates)
        {
            var candidateResult = Read(candidate.Identity);
            if (candidateResult.Status == LocalSecretResultStatus.Found)
            {
                if (firstFound is not null)
                {
                    return AppSurfaceLocalSecretResolution<T>.NotFound(
                        LocalSecretResultStatus.ProviderFailed,
                        new AppSurfaceLocalSecretDiagnostic(
                            "local-secret-key-collision",
                            "Local secret identities collide.",
                            "The canonical identity and a historical alias both exist in the same lookup domain.",
                            "Keep one stored identifier and remove or migrate the duplicate.",
                            _options.DocsHint), Name);
                }

                firstFound = candidateResult;
                if (candidate.IsAlias)
                {
                    aliasNotice = ConfigDiagnosticCatalog.LegacyAlias(
                        candidate.Identity.StorageName,
                        identityResult.Identity!.StorageName);
                }
            }
            else if (candidateResult.Status != LocalSecretResultStatus.Missing)
            {
                return AppSurfaceLocalSecretResolution<T>.NotFound(
                    candidateResult.Status, candidateResult.Diagnostic!, candidateResult.Source);
            }
        }

        if (firstFound is not null)
        {
            if (ConfigValueConverter.TryConvert<T>(firstFound.Value ?? string.Empty, out var converted) && converted is not null)
            {
                return AppSurfaceLocalSecretResolution<T>.Found(converted, firstFound.Source);
            }

            return AppSurfaceLocalSecretResolution<T>.NotFound(LocalSecretResultStatus.ConversionFailed,
                CreateConversionDiagnostic<T>(), firstFound.Source);
        }

        return AppSurfaceLocalSecretResolution<T>.NotFound(LocalSecretResultStatus.Missing,
            AppSurfaceLocalSecretResult.Missing(_store.Name).Diagnostic!, _store.Name);
    }

    private AppSurfaceLocalSecretResult Read(AppSurfaceLocalSecretIdentity identity)
    {
        try
        {
            return _store.Get(identity);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return AppSurfaceLocalSecretResult.NotFound(LocalSecretResultStatus.ProviderFailed,
                new AppSurfaceLocalSecretDiagnostic("local-secret-provider-threw", "Local secret provider failed unexpectedly.",
                    $"The local secret store threw {ex.GetType().Name}.", "Run `appsurface secrets doctor`; raw values are never printed.",
                    _options.DocsHint, retryable: true), _store.Name);
        }
    }

    private IReadOnlyList<(AppSurfaceLocalSecretIdentity Identity, bool IsAlias)> BuildCandidates(ConfigProviderRequest request)
    {
        var candidates = new List<(AppSurfaceLocalSecretIdentity, bool)>();
        var canonical = _normalizer.Normalize(_options.ApplicationName, request.Environment, _options.KeyPrefix, request.Key.Value);
        candidates.Add((canonical.Identity!, false));

        if (request.InputOrigin is ConfigKeyInputOrigin.StrictString or ConfigKeyInputOrigin.TranslatedDot
            && request.OriginalInput is { } original)
        {
            var aliases = request.InputOrigin == ConfigKeyInputOrigin.TranslatedDot
                ? new[] { original }
                : new[] { original.Replace("__", ":", StringComparison.Ordinal), original.Replace('\\', '/') };
            foreach (var alias in aliases.Distinct(StringComparer.Ordinal))
            {
                if (StringComparer.Ordinal.Equals(alias, request.Key.Value)
                    || !_normalizer.Normalize(_options.ApplicationName, request.Environment, _options.KeyPrefix, alias).Succeeded)
                {
                    continue;
                }

                var normalized = _normalizer.Normalize(_options.ApplicationName, request.Environment, _options.KeyPrefix, alias);
                candidates.Add((normalized.Identity!, true));
            }
        }

        return candidates;
    }

    private ConfigProviderTerminalDiagnostic ToTerminal(AppSurfaceLocalSecretDiagnostic diagnostic, AppSurfaceConfigKey key) =>
        new(diagnostic.Code, diagnostic.Problem, $"{diagnostic.Cause} Key: {key.Value}.", diagnostic.Fix,
            diagnostic.Docs ?? _options.DocsHint, diagnostic.Retryable);

    private bool IsPostureAllowed(string environment, out AppSurfaceLocalSecretDiagnostic diagnostic)
    {
        if (_options.Posture == LocalSecretsPostureMode.Disabled
            || (_options.Posture == LocalSecretsPostureMode.DevelopmentOnly
                && !_options.DevelopmentEnvironmentNames.Contains(environment)))
        {
            diagnostic = new AppSurfaceLocalSecretDiagnostic("local-secret-posture-disabled",
                "LocalSecrets is disabled for this environment.",
                "The configured LocalSecrets posture does not permit this environment.",
                "Use an approved environment or an explicit non-LocalSecrets provider.", _options.DocsHint);
            return false;
        }

        diagnostic = null!;
        return true;
    }

    private AppSurfaceLocalSecretDiagnostic CreateConversionDiagnostic<T>() => new(
        "local-secret-conversion-failed", "Local secret value could not be converted.",
        $"The local secret text could not bind to {typeof(T).Name}.",
        "Replace the secret with the expected scalar text or JSON object shape.", _options.DocsHint);
}
