using System.Collections.Frozen;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config;

/// <summary>Validates complete native names and atomically publishes bounded ad-hoc reverse claims.</summary>
/// <remarks>Each environment has its own collision domain. All names for one request are checked before any are added.</remarks>
internal sealed class EnvironmentNativeClaims(AppSurfaceConfigKey[] knownKeys,
    IReadOnlyDictionary<AppSurfaceConfigKey, string> mappings, int capacity)
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Environment, string Name), AppSurfaceConfigKey> _claims = [];
    private readonly HashSet<(string Environment, AppSurfaceConfigKey Key)> _requests = [];
    private readonly Dictionary<string, FrozenDictionary<string, AppSurfaceConfigKey[]>> _knownClaims = new(StringComparer.Ordinal);

    /// <summary>Checks frozen mappings against canonical and explicit full names for all finalized declarations.</summary>
    internal void Validate(string environment)
    {
        var canonical = new Dictionary<string, HashSet<AppSurfaceConfigKey>>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in knownKeys)
        {
            if (!EnvironmentConfigCodec.TryEncode(key, out var suffix)) continue;
            foreach (var name in FullNames(environment, suffix))
            {
                if (!canonical.TryGetValue(name, out var owners)) canonical[name] = owners = [];
                owners.Add(key);
            }
        }

        var explicitNames = new Dictionary<string, AppSurfaceConfigKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, suffix) in mappings)
        {
            foreach (var name in FullNames(environment, suffix))
            {
                if ((canonical.TryGetValue(name, out var owners) && owners.Any(owner => !owner.Equals(key)))
                    || (explicitNames.TryGetValue(name, out var owner) && !owner.Equals(key)))
                {
                    throw new OptionsValidationException(nameof(AppSurfaceEnvironmentConfigOptions), typeof(AppSurfaceEnvironmentConfigOptions),
                        ["An explicit environment mapping claims another logical key's native name."]);
                }
                explicitNames[name] = key;
            }
        }

        var raw = knownKeys.Where(key => IsRepresentable(environment, key))
            .SelectMany(key => EnvironmentConfigCodec.Candidates(new(environment, key), mappings)
                .Select(candidate => (candidate.Name, Key: key)));
        _knownClaims[environment] = raw.GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(group => group.Key, group => group.Select(entry => entry.Key).Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Checks representability even for supplied child names, then atomically claims every candidate.</summary>
    /// <remarks>Only an exact mapping exempts a key from convention grammar and environment-prefix restrictions.</remarks>
    internal string? Claim(ConfigProviderRequest request, IEnumerable<string>? nativeNames = null)
    {
        if (!IsRepresentable(request.Environment, request.Key))
            return "config-key-unrepresentable";

        var names = (nativeNames ?? EnvironmentConfigCodec.Candidates(request, mappings).Select(candidate => candidate.Name))
            .Select(name => name.ToUpperInvariant()).Distinct().ToArray();
        lock (_gate)
        {
            if (!_knownClaims.TryGetValue(request.Environment, out var known))
            {
                if (_knownClaims.Count >= capacity) return "config-environment-claim-limit";
                try { Validate(request.Environment); }
                catch (OptionsValidationException) { return "config-key-unrepresentable"; }
                known = _knownClaims[request.Environment];
            }

            foreach (var name in names)
                if (known.TryGetValue(name, out var owners) && owners.Any(owner => !owner.Equals(request.Key)))
                    return "config-key-unrepresentable";

            foreach (var name in names)
                if (_claims.TryGetValue((request.Environment, name), out var owner) && !owner.Equals(request.Key))
                    return "config-key-unrepresentable";

            var identity = (request.Environment, request.Key);
            if (!_requests.Contains(identity) && _requests.Count >= capacity) return "config-environment-claim-limit";
            foreach (var name in names) _claims[(request.Environment, name)] = request.Key;
            _requests.Add(identity);
        }

        return null;
    }

    private bool IsRepresentable(string environment, AppSurfaceConfigKey key) => mappings.ContainsKey(key)
        || (EnvironmentConfigCodec.TryEncode(key, out var suffix)
            && !suffix.StartsWith(EnvironmentConfigCodec.EncodeEnvironment(environment) + "__", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> FullNames(string environment, string suffix)
    {
        yield return suffix;
        yield return EnvironmentConfigCodec.EncodeEnvironment(environment) + "__" + suffix;
    }
}
