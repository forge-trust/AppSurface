namespace ForgeTrust.AppSurface.Auth;

/// <summary>
/// Identifies a durable app-owned user record after an external authenticated subject has been resolved.
/// </summary>
/// <remarks>
/// <see cref="AppUserId"/> belongs to the consuming application. AppSurface does not allocate ids, prescribe storage,
/// or treat the id as a permission source. The value is intentionally omitted from <see cref="ToString"/> so accidental
/// logs and diagnostics do not disclose user identifiers by default.
/// </remarks>
public readonly struct AppUserId : IEquatable<AppUserId>
{
    /// <summary>
    /// Creates an app-owned user id.
    /// </summary>
    /// <param name="value">Stable app-owned user id. The value must be non-empty and is preserved exactly.</param>
    public AppUserId(string value)
    {
        Value = AppSurfaceAuthMetadata.RequireIdentifier(value, nameof(value));
    }

    /// <summary>
    /// Gets the stable app-owned user id value.
    /// </summary>
    public string Value { get; }

    /// <inheritdoc />
    public bool Equals(AppUserId other)
    {
        return string.Equals(Value, other.Value, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is AppUserId other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return Value is null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
    }

    /// <summary>
    /// Compares two app-owned user ids with ordinal semantics.
    /// </summary>
    /// <param name="left">The left id.</param>
    /// <param name="right">The right id.</param>
    /// <returns><see langword="true"/> when both ids contain the same value.</returns>
    public static bool operator ==(AppUserId left, AppUserId right)
    {
        return left.Equals(right);
    }

    /// <summary>
    /// Compares two app-owned user ids with ordinal semantics.
    /// </summary>
    /// <param name="left">The left id.</param>
    /// <param name="right">The right id.</param>
    /// <returns><see langword="true"/> when the ids contain different values.</returns>
    public static bool operator !=(AppUserId left, AppUserId right)
    {
        return !left.Equals(right);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return "AppUserId { Value = <redacted> }";
    }

    internal void EnsureInitialized(string parameterName)
    {
        _ = AppSurfaceAuthMetadata.RequireIdentifier(Value, parameterName);
    }
}
