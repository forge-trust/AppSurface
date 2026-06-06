namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Describes the expected cardinality budget for a product-event property.
/// </summary>
public enum AppSurfaceProductEventCardinality
{
    /// <summary>
    /// The property should have a short, bounded set of possible values.
    /// </summary>
    Low,

    /// <summary>
    /// The property may have a wider set of values, but should still be normalized.
    /// </summary>
    Medium,

    /// <summary>
    /// The property risks creating high-cardinality analytics dimensions and should be avoided unless essential.
    /// </summary>
    High
}
