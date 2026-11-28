namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Tracks the exact spelling of every portable file path and its parent prefixes.</summary>
/// <remarks>Callers validate paths before adding them and enforce their own duplicate-file policy.</remarks>
internal sealed class EvidencePathPrefixes
{
    private readonly Dictionary<string, string> _spellings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the number of distinct file and parent prefixes tracked so far.</summary>
    internal int Count => _spellings.Count;

    /// <summary>Tracks a validated path, returning false if a prefix collides with a different spelling.</summary>
    /// <param name="path">A slash-delimited path that already passed the caller's path checks.</param>
    /// <remarks>Reusing an identical spelling is allowed, including for a shared parent directory.</remarks>
    internal bool TryAdd(string path)
    {
        var prefix = string.Empty;
        foreach (var component in path.Split('/'))
        {
            prefix = prefix.Length == 0 ? component : prefix + "/" + component;
            if (_spellings.TryGetValue(prefix, out var existing) && existing != prefix)
                return false;
            _spellings[prefix] = prefix;
        }
        return true;
    }
}
