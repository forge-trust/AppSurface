namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Provides the composed product-intelligence event-contract registry used by dispatchers and diagnostics.
/// </summary>
/// <remarks>
/// The default implementation is immutable after construction and composes AppSurface built-in contracts with
/// host/package contract packs registered through <see cref="AppSurfaceProductIntelligenceOptions.RegisterEventContracts(IEnumerable{AppSurfaceProductEventContract})"/>.
/// Replacing this service is a supported advanced escape hatch, but replacement registries must preserve the same
/// privacy boundary: forbidden property names, forbidden value shapes, lifecycle gating, and safe diagnostics.
/// </remarks>
public interface IAppSurfaceProductEventRegistry
{
    /// <summary>
    /// Gets every composed event contract available for validation and diagnostics.
    /// </summary>
    IReadOnlyList<AppSurfaceProductEventContract> All { get; }

    /// <summary>
    /// Gets globally forbidden property names that are always dropped from emitted payloads.
    /// </summary>
    IReadOnlySet<string> ForbiddenProperties { get; }

    /// <summary>
    /// Finds a registered contract by event name.
    /// </summary>
    /// <param name="name">Event name to look up.</param>
    /// <returns>The matching contract, or <see langword="null"/> when no contract is registered.</returns>
    AppSurfaceProductEventContract? Find(string name);

    /// <summary>
    /// Validates and sanitizes a product event against the composed registry.
    /// </summary>
    /// <param name="productEvent">Event instance to validate.</param>
    /// <returns>Validation result with safe diagnostics and sanitized properties.</returns>
    AppSurfaceProductEventValidationResult Validate(AppSurfaceProductEvent productEvent);
}
