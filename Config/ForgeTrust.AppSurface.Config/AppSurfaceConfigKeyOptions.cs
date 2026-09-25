using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config;

/// <summary>Controls the bounded migration of historical dot-delimited application strings.</summary>
public enum LegacyDotPathBehavior
{
    /// <summary>Uses colon grammar and preserves literal dots.</summary>
    Strict = 0,
    /// <summary>Translates dot-only strings once and reports migration guidance (train-1 default).</summary>
    TranslateDotOnlyWithDiagnostic = 1
}

/// <summary>Configures application string parsing through the normal options registration pipeline.</summary>
/// <remarks>Typed keys always use strict grammar. Configure options before building the host.</remarks>
public sealed class AppSurfaceConfigKeyOptions
{
    /// <summary>Gets or sets the string migration behavior; train 1 defaults to dot-only translation.</summary>
    public LegacyDotPathBehavior LegacyDotPathBehavior { get; set; } =
        LegacyDotPathBehavior.TranslateDotOnlyWithDiagnostic;
}

/// <summary>The sole translation boundary for application strings and deferred declaration fragments.</summary>
internal interface IConfigKeyInputParser
{
    /// <summary>Parses once and retains immutable input origin for aliases and audit notices.</summary>
    AppSurfaceConfigKey Parse(string key);
}

/// <summary>Snapshots finalized options; provider internals never invoke this application adapter.</summary>
internal sealed class ConfigKeyInputParser : IConfigKeyInputParser
{
    private readonly LegacyDotPathBehavior _behavior;

    /// <summary>Captures and validates the finalized migration policy.</summary>
    public ConfigKeyInputParser(IOptions<AppSurfaceConfigKeyOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _behavior = options.Value.LegacyDotPathBehavior;
        if (!Enum.IsDefined(_behavior))
        {
            throw new OptionsValidationException(nameof(AppSurfaceConfigKeyOptions),
                typeof(AppSurfaceConfigKeyOptions), ["LegacyDotPathBehavior must name a supported mode."]);
        }
    }

    /// <inheritdoc />
    public AppSurfaceConfigKey Parse(string key)
    {
        var translated = _behavior == LegacyDotPathBehavior.TranslateDotOnlyWithDiagnostic
                         && key is not null && key.Contains('.') && !key.Contains(':');
        var input = translated ? key!.Replace('.', ':') : key;
        if (!AppSurfaceConfigKey.TryParse(input, out var result))
        {
            throw new ArgumentException(
                "config-key-invalid: Use nonempty colon-delimited segments without controls or edge whitespace. " +
                "See https://appsurface.dev/guides/config-logical-keys.", nameof(key));
        }

        return result.WithInput(translated ? ConfigKeyInputOrigin.TranslatedDot : ConfigKeyInputOrigin.StrictString, key);
    }
}
