namespace ForgeTrust.AppSurface.Config;

/// <summary>An immutable segment path; logical identity is ordinal case-insensitive, never a raw prefix.</summary>
internal sealed class ConfigLogicalPath : IEquatable<ConfigLogicalPath>
{
    /// <summary>Gets the original segment spellings.</summary>
    internal IReadOnlyList<string> Segments { get; }
    /// <summary>Gets colon-delimited logical spelling.</summary>
    internal string Canonical => string.Join(':', Segments);
    /// <summary>Gets dotted spelling for legacy file origins and environment normalization.</summary>
    internal string Dotted => string.Join('.', Segments);
    private ConfigLogicalPath(string[] segments)
    {
        Segments = Array.AsReadOnly(segments);
    }

    /// <summary>Parses either supported separator without changing external provider keys.</summary>
    internal static ConfigLogicalPath Parse(string path)
    {
        var segments = path.Split(['.', ':']);
        if (segments.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Logical paths require nonempty segments.", nameof(path));
        return new(segments);
    }

    /// <summary>Creates a path from an already parsed key, preserving literal dots in each segment.</summary>
    internal static ConfigLogicalPath FromKey(AppSurfaceConfigKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new(key.Segments.ToArray());
    }
    /// <summary>Appends one literal serialized member name; dots remain part of that segment.</summary>
    internal ConfigLogicalPath Append(string member)
    {
        if (string.IsNullOrWhiteSpace(member) || member.Contains(':'))
            throw new ArgumentException("Serialized member names must be nonempty and cannot contain a colon separator.", nameof(member));
        return new([.. Segments, member]);
    }
    /// <summary>Tests equality or a complete-segment ancestor relationship.</summary>
    internal bool IsAncestorOrEqual(ConfigLogicalPath other) => Segments.Count <= other.Segments.Count
        && Segments.Select((s, i) => string.Equals(s, other.Segments[i], StringComparison.OrdinalIgnoreCase)).All(match => match);
    /// <summary>Tests intersecting paths, excluding siblings.</summary>
    internal bool Overlaps(ConfigLogicalPath other) => IsAncestorOrEqual(other) || other.IsAncestorOrEqual(this);
    /// <inheritdoc />
    public bool Equals(ConfigLogicalPath? other) => other is not null && Segments.Count == other.Segments.Count && IsAncestorOrEqual(other);
    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ConfigLogicalPath path && Equals(path);
    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Canonical);
    /// <inheritdoc />
    public override string ToString() => Canonical;
}
