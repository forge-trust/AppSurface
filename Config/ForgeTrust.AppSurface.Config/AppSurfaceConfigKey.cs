using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// An immutable, case-insensitive logical configuration path. Only a colon separates segments.
/// </summary>
/// <remarks>
/// Equality and hashing use ordinal ignore-case comparison without Unicode normalization.
/// <see cref="Value"/> preserves spelling; dots, hyphens, underscores, and slashes are literal.
/// See <see href="https://appsurface.dev/guides/config-logical-keys">the logical-key reference</see>.
/// </remarks>
public sealed class AppSurfaceConfigKey : IEquatable<AppSurfaceConfigKey>
{
    private AppSurfaceConfigKey(
        ImmutableArray<string> segments,
        ConfigKeyInputOrigin inputOrigin = ConfigKeyInputOrigin.Typed,
        string? originalInput = null)
    {
        Segments = segments;
        Value = string.Join(':', segments);
        InputOrigin = inputOrigin;
        OriginalInput = originalInput;
    }

    /// <summary>Gets the colon-delimited path with its original segment spelling.</summary>
    public string Value { get; }

    /// <summary>Gets the immutable literal segments; no caller-owned array is retained.</summary>
    public ImmutableArray<string> Segments { get; }

    /// <summary>Gets parser provenance, excluded from logical identity and public serialization.</summary>
    internal ConfigKeyInputOrigin InputOrigin { get; }

    /// <summary>Gets the original application spelling used only for compatibility aliases.</summary>
    internal string? OriginalInput { get; }

    /// <summary>Parses strict colon syntax; dots always remain literal in this API.</summary>
    /// <param name="value">The complete logical path.</param>
    /// <returns>A key preserving the supplied spelling.</returns>
    /// <exception cref="FormatException">Input is null, empty, or contains an invalid segment.</exception>
    public static AppSurfaceConfigKey Parse(string value)
    {
        if (!TryParse(value, out var key))
        {
            throw new FormatException(
                "config-key-invalid: Use nonempty colon-delimited segments without controls or edge whitespace. " +
                "See https://appsurface.dev/guides/config-logical-keys.");
        }

        return key;
    }

    /// <summary>Attempts strict parsing without trimming, rewriting, or returning partial output.</summary>
    /// <param name="value">The complete logical path, or null.</param>
    /// <param name="key">The parsed key on success; null on failure.</param>
    /// <returns>Whether every segment satisfies the logical grammar.</returns>
    public static bool TryParse(string? value, [NotNullWhen(true)] out AppSurfaceConfigKey? key)
    {
        key = null;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var segments = value.Split(':');
        if (segments.Any(segment => !IsValidSegment(segment)))
        {
            return false;
        }

        key = new AppSurfaceConfigKey(ImmutableArray.CreateRange(segments));
        return true;
    }

    /// <summary>Constructs a strict key by copying and joining literal segments with colons.</summary>
    /// <param name="segments">One or more literal segments; a segment cannot contain a colon.</param>
    /// <returns>A key independent of later changes to the supplied array.</returns>
    /// <exception cref="ArgumentNullException">The array is null.</exception>
    /// <exception cref="ArgumentException">The array is empty or an indexed member violates the grammar.</exception>
    public static AppSurfaceConfigKey FromSegments(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Length == 0)
        {
            throw new ArgumentException("At least one segment is required.", nameof(segments));
        }

        for (var index = 0; index < segments.Length; index++)
        {
            if (!IsValidSegment(segments[index]))
            {
                throw new ArgumentException(
                    $"Segment at index {index} is invalid. See https://appsurface.dev/guides/config-logical-keys.",
                    nameof(segments));
            }
        }

        return new AppSurfaceConfigKey(ImmutableArray.CreateRange(segments));
    }

    /// <summary>Tests equality or ancestry using complete segments and ordinal ignore-case comparison.</summary>
    /// <param name="prefix">The logical ancestor, including the same key.</param>
    /// <returns>True for the prefix itself and its descendants, but not text-prefix siblings.</returns>
    /// <exception cref="ArgumentNullException">The prefix is null.</exception>
    public bool IsSameOrDescendantOf(AppSurfaceConfigKey prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (Segments.Length < prefix.Segments.Length)
        {
            return false;
        }

        for (var index = 0; index < prefix.Segments.Length; index++)
        {
            if (!StringComparer.OrdinalIgnoreCase.Equals(Segments[index], prefix.Segments[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public bool Equals(AppSurfaceConfigKey? other) =>
        other is not null && StringComparer.OrdinalIgnoreCase.Equals(Value, other.Value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AppSurfaceConfigKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Copies input provenance without changing segments, rendering, equality, or hashing.</summary>
    internal AppSurfaceConfigKey WithInput(ConfigKeyInputOrigin origin, string? originalInput) =>
        new(Segments, origin, originalInput);

    /// <summary>Validates one literal segment without applying boundary encoding or normalization.</summary>
    internal static bool IsValidSegment(string? segment) =>
        !string.IsNullOrEmpty(segment)
        && !char.IsWhiteSpace(segment[0])
        && !char.IsWhiteSpace(segment[^1])
        && !segment.Any(character => character == ':' || char.IsControl(character));
}

/// <summary>Distinguishes strict construction from train-1 application input for native alias selection.</summary>
internal enum ConfigKeyInputOrigin
{
    Typed,
    StrictString,
    TranslatedDot
}
