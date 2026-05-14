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
        Key = GetKeyPath(t);
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
    /// <param name="type">The type to compute the path for.</param>
    /// <returns>The computed configuration key path.</returns>
    public static string GetKeyPath(
        Type type)
    {
        var attribute = GetAttribute(type);
        var isRoot = attribute?.Root ?? false;
        var thisMember = attribute?.Key ?? type.Name;
        if (isRoot || type.DeclaringType == null)
        {
            return thisMember;
        }

        var parentPath = GetKeyPath(type.DeclaringType);

        return $"{parentPath}.{thisMember}";
    }
}
