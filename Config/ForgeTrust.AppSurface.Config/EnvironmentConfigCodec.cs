using System.Collections.Immutable;

namespace ForgeTrust.AppSurface.Config;

/// <summary>Encodes logical keys and generates the bounded train-1 environment aliases.</summary>
internal static class EnvironmentConfigCodec
{
    /// <summary>The existing native generation failure message, used to bound inspection enrichment.</summary>
    internal const string UnencodableMessage = "The logical key is not representable by the canonical environment grammar.";
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
        => GetCandidates(request.Environment, request.Key, request.InputOrigin, request.OriginalInput, mappings)
            .Select(candidate => (candidate.Scope == EnvironmentCandidateScope.Scoped ? "scoped" : "unscoped",
                candidate.Name, candidate.IsLegacyAlias));

    /// <summary>Materializes native names with producer metadata without resolving values or claims.</summary>
    /// <remarks>Mapping replacement and strict/translated compatibility aliases retain resolver precedence.</remarks>
    /// <param name="environment">Non-null environment spelling; uses native normalization.</param>
    /// <param name="key">Validated logical identity.</param>
    /// <param name="inputOrigin">Actual resolver origin, or explicitly typed/strict for public inspection.</param>
    /// <param name="originalInput">Compatibility spelling for strict/translated origins; null adds no aliases.</param>
    /// <param name="mappings">Frozen exact-key replacements; suffix casing is preserved.</param>
    /// <returns>Immutable scoped/unscoped canonical or mapped names, followed by distinct aliases.</returns>
    /// <exception cref="InvalidOperationException">An unmapped key has no supported canonical encoding.</exception>
    internal static ImmutableArray<EnvironmentNativeCandidate> GetCandidates(
        string environment, AppSurfaceConfigKey key, ConfigKeyInputOrigin inputOrigin, string? originalInput,
        IReadOnlyDictionary<AppSurfaceConfigKey, string> mappings)
    {
        string suffix;
        if (mappings.TryGetValue(key, out var explicitSuffix))
        {
            suffix = explicitSuffix;
        }
        else if (TryEncode(key, out var canonical))
        {
            suffix = canonical;
        }
        else
        {
            throw new InvalidOperationException(UnencodableMessage);
        }

        var scoped = EncodeEnvironment(environment) + "__" + suffix;
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var candidates = ImmutableArray.CreateBuilder<EnvironmentNativeCandidate>();
        Add(scoped, EnvironmentCandidateScope.Scoped, false);
        Add(suffix, EnvironmentCandidateScope.Unscoped, false);

        if (explicitSuffix is not null)
        {
            return candidates.ToImmutable();
        }

        if (inputOrigin is ConfigKeyInputOrigin.StrictString or ConfigKeyInputOrigin.TranslatedDot
            && originalInput is { } original)
        {
            foreach (var (layer, alias) in LegacyAliases(environment, original, inputOrigin))
            {
                Add(alias, layer == "scoped" ? EnvironmentCandidateScope.Scoped : EnvironmentCandidateScope.Unscoped, true);
            }
        }

        return candidates.ToImmutable();

        void Add(string name, EnvironmentCandidateScope scope, bool legacy)
        {
            if (emitted.Add(name))
            {
                candidates.Add(new(name, scope, legacy, explicitSuffix is not null));
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
