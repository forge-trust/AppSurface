namespace ForgeTrust.AppSurface.Config;

/// <summary>Encodes logical keys and generates the bounded train-1 environment aliases.</summary>
internal static class EnvironmentConfigCodec
{
    /// <summary>Encodes portable ASCII segments with double underscores only at logical boundaries.</summary>
    internal static string Encode(AppSurfaceConfigKey key) =>
        TryEncode(key, out var encoded)
            ? encoded
            : throw new ArgumentException("The logical key is not representable by the canonical environment grammar.", nameof(key));

    /// <summary>Reports convention representability without rewriting literal separators or Unicode.</summary>
    internal static bool TryEncode(AppSurfaceConfigKey key, out string encoded)
    {
        var segments = new string[key.Segments.Length];
        for (var index = 0; index < key.Segments.Length; index++)
        {
            if (!TryEncodeSegment(key.Segments[index], out segments[index]))
            {
                encoded = string.Empty;
                return false;
            }
        }

        encoded = string.Join("__", segments);
        return true;
    }

    /// <summary>Encodes one literal segment, rejecting edge/double underscores and unsupported characters.</summary>
    internal static string EncodeSegment(string segment)
    {
        if (!TryEncodeSegment(segment, out var encoded))
        {
            throw new ArgumentException("The logical segment is not representable by the canonical environment grammar.", nameof(segment));
        }

        return encoded;
    }

    /// <summary>Returns exact unique native candidates and their scope domains using the approved train-1 table.</summary>
    /// <remarks>Typed requests have no aliases. Explicit mappings replace all convention candidates.</remarks>
    internal static IEnumerable<(string Layer, string Name, bool Legacy)> Candidates(
        ConfigProviderRequest request, IReadOnlyDictionary<AppSurfaceConfigKey, string> mappings)
    {
        string suffix;
        if (mappings.TryGetValue(request.Key, out var explicitSuffix))
        {
            suffix = explicitSuffix;
        }
        else if (TryEncode(request.Key, out var canonical))
        {
            suffix = canonical;
        }
        else
        {
            throw new InvalidOperationException("The logical key is not representable by the canonical environment grammar.");
        }

        var scoped = EncodeEnvironment(request.Environment) + "__" + suffix;
        var emitted = new HashSet<string>([scoped, suffix], StringComparer.Ordinal);
        yield return ("scoped", scoped, false);
        yield return ("unscoped", suffix, false);

        if (explicitSuffix is not null)
        {
            yield break;
        }

        if (request.InputOrigin is ConfigKeyInputOrigin.StrictString or ConfigKeyInputOrigin.TranslatedDot
            && request.OriginalInput is { } original)
        {
            foreach (var (layer, alias) in LegacyAliases(request.Environment, original, request.InputOrigin))
            {
                if (emitted.Add(alias))
                {
                    yield return (layer, alias, true);
                }
            }
        }
    }

    /// <summary>Retains the existing environment-name normalization independently of logical-key grammar.</summary>
    internal static string EncodeEnvironment(string environment) =>
        environment.ToUpperInvariant().Replace(':', '_').Replace('.', '_').Replace('-', '_');

    private static bool TryEncodeSegment(string segment, out string encoded)
    {
        if (string.IsNullOrEmpty(segment) || segment[0] == '_' || segment[^1] == '_' || segment.Contains("__", StringComparison.Ordinal))
        {
            encoded = string.Empty;
            return false;
        }

        foreach (var c in segment)
        {
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '/' or '\\' or '_'))
            {
                encoded = string.Empty;
                return false;
            }
        }

        encoded = segment.ToUpperInvariant();
        return true;
    }

    private static IEnumerable<(string Layer, string Name)> LegacyAliases(
        string environment, string original, ConfigKeyInputOrigin origin)
    {
        var env = EncodeEnvironment(environment);
        var key = origin == ConfigKeyInputOrigin.TranslatedDot
            ? original.Replace('.', '_').ToUpperInvariant()
            : LegacyStrictKey(original);
        var aliases = origin == ConfigKeyInputOrigin.TranslatedDot
            ? new[] { ("scoped", $"{env}_{key}"), ("unscoped", key) }
            : new[] { ("scoped", $"{env}_{key}"), ("scoped", $"{env}__{key}"), ("unscoped", key) };
        return aliases;
    }

    private static string LegacyStrictKey(string value) =>
        value.ToUpperInvariant().Replace('.', '_').Replace('-', '_');
}
