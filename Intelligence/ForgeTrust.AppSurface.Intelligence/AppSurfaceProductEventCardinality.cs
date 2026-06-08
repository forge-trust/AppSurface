namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Describes the expected cardinality budget for a product-event property.
/// </summary>
/// <remarks>
/// The numeric values are explicit because this public enum may be serialized, persisted, or used in generated
/// registry documentation. New values should be appended without changing the values documented here.
/// </remarks>
public enum AppSurfaceProductEventCardinality
{
    /// <summary>
    /// The property should have a short, bounded set of possible values.
    /// </summary>
    Low = 0,

    /// <summary>
    /// The property may have a wider set of values, but should still be normalized.
    /// </summary>
    Medium = 1,

    /// <summary>
    /// The property risks creating high-cardinality analytics dimensions and should be avoided unless essential.
    /// </summary>
    High = 2
}
