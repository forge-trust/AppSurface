namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Describes the sanitized value shape accepted for a product-event property.
/// </summary>
/// <remarks>
/// Value shapes are part of the privacy boundary for built-in and host-registered contract packs. They tell the
/// registry how to validate a property value without relying on property-name conventions. New values should be
/// appended without changing the numeric values documented here because this public enum may be serialized or used in
/// generated registry documentation.
/// </remarks>
public enum AppSurfaceProductEventValueShape
{
    /// <summary>
    /// A short identifier-like token containing only ASCII letters, digits, dash, underscore, period, or colon.
    /// </summary>
    Token = 0,

    /// <summary>
    /// A bounded string that may contain spaces but still passes forbidden-value and maximum-length checks.
    /// </summary>
    BoundedText = 1,

    /// <summary>
    /// A base-10 integer greater than or equal to zero, normalized without leading zeroes.
    /// </summary>
    NonNegativeInteger = 2,

    /// <summary>
    /// A lowercase boolean-like value normalized to <c>true</c> or <c>false</c>.
    /// </summary>
    Boolean = 3,

    /// <summary>
    /// A value that must be present in the property's registered allowed-value set.
    /// </summary>
    AllowedValue = 4
}
