using System.Reflection;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Specifies the configuration key or path for a type.
/// </summary>
public class ConfigKeyAttribute : Attribute
{
    /// <summary>
    /// Gets the configuration key or path for this type.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Gets a value indicating whether this key should be treated as a root key, ignoring the declaring type hierarchy.
    /// </summary>
    public bool Root { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigKeyAttribute"/> class with a specific key.
    /// </summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="root">Whether this is a root key.</param>
    public ConfigKeyAttribute(string key, bool root = false)
    {
        Key = key;
        Root = root;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigKeyAttribute"/> class for a specific type.
    /// </summary>
    /// <param name="t">The type to derive the key from.</param>
    public ConfigKeyAttribute(Type t)
    {
        ArgumentNullException.ThrowIfNull(t);
        Key = GetLogicalKey(t).Value;
        var foundAttr = GetAttribute(t);
        Root = foundAttr?.Root ?? false;
    }

    /// <summary>
    /// Extracts the configuration key from an object's type attribute.
    /// </summary>
    /// <param name="obj">The object to extract the key from.</param>
    /// <returns>The configuration key, or null if not specified.</returns>
    public static string? ExtractKey(object obj)
    {
        return ExtractKey(obj.GetType());
    }

    /// <summary>
    /// Extracts the configuration key from a type's attribute.
    /// </summary>
    /// <param name="type">The type to extract the key from.</param>
    /// <returns>The configuration key, or null if not specified.</returns>
    public static string? ExtractKey(Type type)
    {
        var attribute = GetAttribute(type);

        return attribute?.Key;
    }

    private static ConfigKeyAttribute? GetAttribute(
        Type type) =>
        type.GetCustomAttribute<ConfigKeyAttribute>(false);

    /// <summary>
    /// Computes the full configuration key path for a type, recursively including declaring types unless <see cref="Root"/> is true.
    /// </summary>
    /// <remarks>
    /// This public helper always uses strict colon grammar: literal dots remain inside segments. Host discovery uses
    /// the finalized input parser instead, as described by the
    /// <see href="https://appsurface.dev/guides/config-logical-keys">logical-key contract</see>.
    /// </remarks>
    /// <param name="type">The type to compute the path for.</param>
    /// <returns>The computed configuration key path.</returns>
    public static AppSurfaceConfigKey GetLogicalKey(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var fragments = GetFragments(type);
        return AppSurfaceConfigKey.FromSegments(
            fragments.SelectMany(fragment => AppSurfaceConfigKey.Parse(fragment).Segments).ToArray());
    }

    /// <summary>
    /// Computes a logical key using the finalized application input parser for compatibility declarations.
    /// </summary>
    /// <param name="type">The type to compute.</param>
    /// <param name="parser">The finalized application string parser.</param>
    /// <returns>The parsed logical key, including immutable input provenance.</returns>
    /// <remarks>
    /// Each attribute fragment is parsed exactly once. If any fragment is translated, the composed key retains
    /// translated origin and the original dot-joined declaring-type spelling for provider aliases and diagnostics.
    /// </remarks>
    internal static AppSurfaceConfigKey GetLogicalKey(Type type, IConfigKeyInputParser parser)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(parser);

        var fragments = GetFragments(type);
        var parsed = fragments.Select(parser.Parse).ToArray();
        var segments = parsed.SelectMany(key => key.Segments).ToArray();
        var translated = parsed.Any(key => key.InputOrigin == ConfigKeyInputOrigin.TranslatedDot);
        var originalInput = string.Join('.', fragments);
        var result = AppSurfaceConfigKey.FromSegments(segments);
        return result.WithInput(
            translated ? ConfigKeyInputOrigin.TranslatedDot : ConfigKeyInputOrigin.StrictString,
            originalInput);
    }

    /// <summary>Returns the colon-delimited compatibility rendering of <see cref="GetLogicalKey(Type)"/>.</summary>
    /// <param name="type">The type to compute.</param>
    /// <returns>The rendered logical key.</returns>
    [Obsolete("Use GetLogicalKey(Type).Value.")]
    public static string GetKeyPath(Type type) => GetLogicalKey(type).Value;

    private static IReadOnlyList<string> GetFragments(Type type)
    {
        var attribute = GetAttribute(type);
        var isRoot = attribute?.Root ?? false;
        var thisMember = attribute?.Key ?? type.Name;
        if (isRoot || type.DeclaringType == null)
        {
            return [thisMember];
        }

        return [.. GetFragments(type.DeclaringType), thisMember];
    }
}
