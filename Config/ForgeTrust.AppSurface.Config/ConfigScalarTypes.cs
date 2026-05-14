namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Provides the canonical scalar-type classification shared by configuration validation paths.
/// </summary>
internal static class ConfigScalarTypes
{
    /// <summary>
    /// Returns whether <paramref name="type"/> is treated as a scalar configuration value.
    /// Nullable wrappers are unwrapped before inspection. Supported scalar types are primitives,
    /// enums, <see cref="string"/>, <see cref="decimal"/>, <see cref="DateTime"/>,
    /// <see cref="DateTimeOffset"/>, <see cref="TimeSpan"/>, <see cref="Guid"/>, and
    /// <see cref="Uri"/>.
    /// </summary>
    /// <param name="type">The declared or runtime value type to inspect.</param>
    /// <returns><see langword="true"/> when <paramref name="type"/> is a supported scalar type.</returns>
    public static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        return type.IsPrimitive
               || type.IsEnum
               || type == typeof(string)
               || type == typeof(decimal)
               || type == typeof(DateTime)
               || type == typeof(DateTimeOffset)
               || type == typeof(TimeSpan)
               || type == typeof(Guid)
               || type == typeof(Uri);
    }
}
