using System.Security.Cryptography;
using System.Text;

namespace ForgeTrust.AppSurface.Config;

/// <summary>Reviewed value-free diagnostic templates shared by built-in provider boundaries.</summary>
internal static class ConfigDiagnosticCatalog
{
    internal const string Reference = "https://appsurface.dev/guides/config-logical-keys";
    private static readonly IReadOnlyDictionary<string, (string Problem, string Cause, string Fix)> Templates =
        new Dictionary<string, (string, string, string)>(StringComparer.Ordinal)
        {
            ["config-key-invalid"] = ("The logical key is invalid.", "A segment violates colon-path grammar.", "Correct the path or use valid literal segments."),
            ["config-key-collision"] = ("Configuration identities collide.", "Distinct source spellings or aliases identify one logical key.", "Keep exactly one spelling per collision domain."),
            ["config-key-unrepresentable"] = ("The key cannot be represented by this provider.", "Native encoding would lose logical identity.", "Register an explicit native mapping or rename the key."),
            ["config-key-prefix-overlap"] = ("Provider conventions overlap.", "More than one convention can claim the same logical subtree.", "Narrow or remove the overlapping convention."),
            ["config-key-environment-name-case"] = ("An environment variable has noncanonical casing.", "The native name differs from its expected exact spelling.", "Rename the variable to the displayed expected name."),
            ["config-environment-snapshot-failed"] = ("The environment snapshot could not be captured.", "Override availability cannot be determined safely.", "Repair environment access and retry."),
            ["config-environment-entry-limit"] = ("The environment entry limit was exceeded.", "No partial environment snapshot was published.", "Reduce environment entries or raise the validated limit."),
            ["config-environment-snapshot-limit"] = ("The environment byte limit was exceeded.", "No partial environment snapshot was published.", "Reduce environment size or raise the validated limit."),
            ["config-environment-claim-limit"] = ("The environment native-claim limit was exceeded.", "No partial reverse claim was published.", "Reduce distinct environment/key requests or raise MaxAdHocClaims."),
            ["config-file-byte-limit"] = ("The configuration file limit was exceeded.", "The source layer exceeds its permitted size.", "Reduce file size or raise the validated limit."),
            ["config-file-count-limit"] = ("The configuration file count limit was exceeded.", "The environment contains too many source layers.", "Reduce source layers or raise the validated limit."),
            ["config-provider-invalid-result"] = ("The provider returned an invalid outcome.", "A provider returned no structured result.", "Update the provider to the coordinated package contract."),
            ["config-provider-failed"] = ("The provider could not complete resolution.", "An unexpected provider failure occurred; exception details are withheld.", "Inspect the provider through its supported diagnostics and retry after repair."),
            ["config-patch-failed"] = ("The environment patch could not be applied.", "A present child could not be bound safely.", "Correct every invalid child and retry the complete transaction."),
            ["config-audit-remote-lookup-limit"] = ("The configuration audit is incomplete.", "The aggregate remote lookup budget was exhausted.", "Narrow the audited declarations or raise the validated lookup limit."),
            ["config-audit-deadline"] = ("The configuration audit is incomplete.", "The aggregate remote deadline was reached.", "Narrow the audited declarations or raise the validated audit timeout."),
            ["config-audit-notice-limit"] = ("The configuration audit is incomplete.", "The operation notice limit was reached; additional notices were omitted.", "Narrow the audited declarations or raise the validated notice limit."),
            ["config-package-version-mismatch"] = ("Configuration package contracts do not match.", "A component was built against an incompatible provider or wrapper contract.", "Upgrade and rebuild all AppSurface packages and external providers together.")
        };

    /// <summary>Builds complete guidance using only known templates and classified source identifiers.</summary>
    internal static ConfigProviderTerminalDiagnostic Terminal(string code, params string[] safeIdentifiers)
    {
        var safeCode = Templates.ContainsKey(code) ? code : "config-provider-failed";
        var template = Templates[safeCode];
        var cause = template.Cause;
        if (safeIdentifiers.Length != 0)
        {
            cause += " Sources: " + string.Join(", ", safeIdentifiers.Select(identifier => ConfigDiagnosticText.Identifier(identifier)));
        }

        return new ConfigProviderTerminalDiagnostic(safeCode, template.Problem, cause, template.Fix, Reference,
            safeCode == "config-environment-snapshot-failed");
    }

    /// <summary>Returns a bounded catalogued automatic-log code, never arbitrary external-provider text.</summary>
    internal static string SafeCode(string code, string fallback) =>
        Templates.ContainsKey(code) || code is "config-key-legacy-dot-path" or "config-key-legacy-provider-alias"
            ? code
            : fallback;

    /// <summary>Explains one automatic train-1 translation without including any configuration value.</summary>
    internal static ConfigProviderNotice LegacyDot(AppSurfaceConfigKey key) =>
        new("config-key-legacy-dot-path", "A legacy dot path was translated.",
            $"Application input '{ConfigDiagnosticText.Identifier(key.OriginalInput)}' used historical hierarchy dots.",
            $"Replace it with '{ConfigDiagnosticText.Identifier(key.Value)}'.", Reference, false)
        { SafeSourceIdentifier = key.OriginalInput };

    /// <summary>Explains the single historical native identifier that supplied a successful value.</summary>
    internal static ConfigProviderNotice LegacyAlias(string source, string replacement) =>
        new("config-key-legacy-provider-alias", "A historical provider alias supplied this key.",
            $"Source '{ConfigDiagnosticText.Identifier(source)}' is a migration alias.",
            $"Move it to '{ConfigDiagnosticText.Identifier(replacement)}'.", Reference, false)
        { SafeSourceIdentifier = source };
}

/// <summary>Escapes and bounds untrusted identifiers independently of logical equality and native storage identity.</summary>
internal static class ConfigDiagnosticText
{
    /// <summary>Renders at most limit characters plus a stable digest, escaping controls before display.</summary>
    internal static string Identifier(string? value, int limit = 256) => Render(value, limit, includeDigest: true);

    /// <summary>Escapes and caps explicit diagnostic prose without creating a fingerprint of untrusted text.</summary>
    internal static string Prose(string? value, int limit = 4096) => Render(value, limit, includeDigest: false);

    private static string Render(string? value, int limit, bool includeDigest)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (value is null)
        {
            return "(none)";
        }

        var output = new StringBuilder(Math.Min(value.Length, limit));
        var truncated = false;
        foreach (var character in value)
        {
            var escaped = char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format
                || character is '\u2028' or '\u2029'
                ? $"\\u{(int)character:x4}"
                : character.ToString();
            if (output.Length + escaped.Length > limit)
            {
                truncated = true;
                break;
            }

            output.Append(escaped);
        }

        if (truncated)
        {
            output.Append('…');
            if (includeDigest)
            {
                output.Append('#').Append(Digest(value)[..16]);
            }
        }

        return output.ToString();
    }

    /// <summary>Creates an identifier-only digest; callers must never pass configuration values.</summary>
    internal static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>Bounded process-level notice suppression. Saturation drops new identities rather than evicting old ones.</summary>
internal static class ConfigNoticeHistory
{
    private static readonly ConfigNoticeHistoryStore History = new();

    /// <summary>Records a stable value-free identity once; never retains unbounded caller text.</summary>
    internal static bool TryRemember(string code, string provider, string environment, AppSurfaceConfigKey key,
        string? source, int capacity) => History.TryRemember(code, provider, environment, key, source, capacity);
}

/// <summary>
/// Owns one bounded notice history. The manager shares the process instance; isolated instances let tests
/// verify capacity and concurrency without resetting or saturating another host's diagnostic history.
/// </summary>
internal sealed class ConfigNoticeHistoryStore
{
    private readonly object _gate = new();
    private readonly HashSet<string> _identities = new(StringComparer.Ordinal);

    /// <summary>Admits a previously unseen identity while capacity remains; saturation retains existing identities.</summary>
    internal bool TryRemember(string code, string provider, string environment, AppSurfaceConfigKey key,
        string? source, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        var identity = CreateIdentity(code, provider, environment, key, source);
        lock (_gate)
        {
            if (_identities.Count >= capacity)
            {
                return false;
            }

            return _identities.Add(identity);
        }
    }

    /// <summary>Hashes framed value-free components; logical casing folds while exact source spelling remains distinct.</summary>
    internal static string CreateIdentity(string code, string provider, string environment, AppSurfaceConfigKey key, string? source)
    {
        var parts = new[] { code, provider, environment, key.Value.ToUpperInvariant(), source ?? "" };
        return ConfigDiagnosticText.Digest(string.Concat(parts.Select(part => $"{part.Length}:{part}")));
    }
}
