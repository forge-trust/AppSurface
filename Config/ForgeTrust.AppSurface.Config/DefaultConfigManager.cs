using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config;

/// <summary>Orchestrates one parsed identity and one environment snapshot through ordered sources and patching.</summary>
internal sealed class DefaultConfigManager : IConfigManager
{
    private readonly IEnvironmentConfigProvider _environmentProvider;
    private readonly IReadOnlyList<IConfigProvider> _otherProviders;
    private readonly ILogger<DefaultConfigManager> _logger;
    private readonly IConfigKeyInputParser _parser;
    private readonly ConfigResourceOptions _limits;

    /// <summary>Captures provider order and finalized parser/resource options without activating wrappers.</summary>
    public DefaultConfigManager(
        IEnvironmentConfigProvider environmentProvider,
        IEnumerable<IConfigProvider>? otherProviders,
        ILogger<DefaultConfigManager> logger,
        IConfigKeyInputParser? parser = null,
        IOptions<ConfigResourceOptions>? resourceOptions = null,
        ConfigDeclarationRegistry? declarations = null)
    {
        ArgumentNullException.ThrowIfNull(environmentProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _environmentProvider = environmentProvider;
        _otherProviders = otherProviders?.Where(provider => provider is not IEnvironmentConfigProvider)
            .OrderByDescending(provider => provider.Priority).ToArray() ?? [];
        _logger = logger;
        _parser = parser ?? new ConfigKeyInputParser(Options.Create(new AppSurfaceConfigKeyOptions()));
        _limits = (resourceOptions?.Value ?? new ConfigResourceOptions()).Snapshot();
        // DI constructs the complete registry before manager use, even when no wrapper is requested.
        _ = declarations?.Entries;
    }

    /// <inheritdoc />
    public T? GetValue<T>(string environment, string key) => GetValue<T>(environment, _parser.Parse(key));

    /// <inheritdoc />
    public T? GetValue<T>(string environment, AppSurfaceConfigKey key)
    {
        using var scope = new ConfigResolutionScope();
        var request = new ConfigProviderRequest(environment, key, scope);
        if (key.InputOrigin == ConfigKeyInputOrigin.TranslatedDot)
        {
            ReportNotice(request, "Application", ConfigDiagnosticCatalog.LegacyDot(key));
        }

        var result = Resolve<T>(_environmentProvider, request);
        if (result.Status == ConfigProviderValueStatus.Found)
        {
            ReportFound(request, _environmentProvider.Name, result.Notices);
            return result.Value;
        }

        var providerName = _environmentProvider.Name;
        if (result.Status == ConfigProviderValueStatus.Missing)
        {
            foreach (var provider in _otherProviders)
            {
                result = Resolve<T>(provider, request);
                providerName = provider.Name;
                if (result.Status != ConfigProviderValueStatus.Missing)
                {
                    break;
                }
            }
        }

        if (_environmentProvider is IConfigValuePatcher patcher)
        {
            var noticeOffset = scope.NoticeCount;
            var patch = Patch(patcher, request, result.Status == ConfigProviderValueStatus.Found ? result.Value : default);
            if (patch.Status == ConfigPatchStatus.Applied)
            {
                foreach (var collected in scope.Notices.Skip(noticeOffset))
                {
                    EmitNotice(new ConfigProviderRequest(environment, collected.Key, scope), collected.Provider, collected.Notice);
                }

                if (result.Status == ConfigProviderValueStatus.Found)
                {
                    ReportFound(request, providerName, result.Notices);
                }

                SafeLog(LogLevel.Debug, "config-key-found", request, _environmentProvider.Name);
                return patch.Value;
            }

            if (patch.Status == ConfigPatchStatus.Terminal)
            {
                ConfigDiagnosticMetrics.Terminal(patch.Diagnostic!.Code, _environmentProvider.Name);
                throw new ConfigurationResolutionException(environment, key, _environmentProvider.Name, patch.Diagnostic!);
            }
        }

        if (result.Status == ConfigProviderValueStatus.Terminal)
        {
            ConfigDiagnosticMetrics.Terminal(result.Diagnostic!.Code, providerName);
            throw new ConfigurationResolutionException(environment, key, providerName, result.Diagnostic!);
        }

        if (result.Status == ConfigProviderValueStatus.Found)
        {
            ReportFound(request, providerName, result.Notices);
            return result.Value;
        }

        SafeLog(LogLevel.Debug, "config-key-missing", request, providerName);
        return default;
    }

    private static ConfigProviderValueResult<T> Resolve<T>(IConfigProvider provider, ConfigProviderRequest request)
    {
        try
        {
            return provider.Resolve<T>(request) ?? ConfigProviderValueResult<T>.Terminal(
                ConfigDiagnosticCatalog.Terminal("config-provider-invalid-result"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ConfigProviderValueResult<T>.Terminal(ConfigDiagnosticCatalog.Terminal("config-provider-failed"));
        }
    }

    /// <summary>Contains unexpected patch failures without retaining raw exception text or publishing a partial value.</summary>
    private static ConfigPatchResult<T> Patch<T>(IConfigValuePatcher patcher, ConfigProviderRequest request, T? value)
    {
        try
        {
            return patcher.Patch(request, value) ?? ConfigPatchResult<T>.Terminal(
                ConfigDiagnosticCatalog.Terminal("config-patch-failed"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ConfigPatchResult<T>.Terminal(ConfigDiagnosticCatalog.Terminal("config-patch-failed"));
        }
    }

    private void ReportFound(ConfigProviderRequest request, string provider, IReadOnlyList<ConfigProviderNotice> notices)
    {
        foreach (var notice in notices)
        {
            ReportNotice(request, provider, notice);
        }

        SafeLog(LogLevel.Debug, "config-key-found", request, provider);
    }

    private void ReportNotice(ConfigProviderRequest request, string provider, ConfigProviderNotice notice)
    {
        request.Scope.AddNotice(provider, request.Key, notice, _limits.MaxNoticeIdentities);
        EmitNotice(request, provider, notice);
    }

    /// <summary>Logs a committed notice once without recollecting notices already published by an environment patch.</summary>
    private void EmitNotice(ConfigProviderRequest request, string provider, ConfigProviderNotice notice)
    {
        ConfigDiagnosticMetrics.Notice(notice.Code, provider);
        var code = ConfigDiagnosticCatalog.SafeCode(notice.Code, "config-provider-notice");
        if (ConfigNoticeHistory.TryRemember(code, provider, request.Environment, request.Key, notice.SafeSourceIdentifier,
                _limits.MaxNoticeIdentities))
        {
            SafeLog(LogLevel.Warning, code, request, provider);
        }
    }

    private void SafeLog(LogLevel level, string code, ConfigProviderRequest request, string provider)
    {
        try
        {
            _logger.Log(level, "Configuration {Code}: provider '{Provider}', environment '{Environment}', key '{Key}'.",
                code, ConfigDiagnosticText.Identifier(provider, _limits.MaxRenderedIdentifierCharacters),
                ConfigDiagnosticText.Identifier(request.Environment, _limits.MaxRenderedIdentifierCharacters),
                ConfigDiagnosticText.Identifier(request.Key.Value, _limits.MaxRenderedIdentifierCharacters));
        }
        catch (Exception)
        {
            // Logging is observational. It must never change configuration resolution.
        }
    }
}
