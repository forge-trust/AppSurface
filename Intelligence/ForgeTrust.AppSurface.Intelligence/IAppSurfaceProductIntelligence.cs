namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Captures AppSurface-owned product-intelligence events.
/// </summary>
/// <remarks>
/// Implementations validate against <see cref="AppSurfaceProductEventRegistry"/> before forwarding events to any
/// host-owned transport. The interface is intentionally vendor-neutral and must not require PostHog, OpenTelemetry, or a
/// browser analytics SDK.
/// </remarks>
public interface IAppSurfaceProductIntelligence
{
    /// <summary>
    /// Captures a product-intelligence event if the registry and options allow it.
    /// </summary>
    /// <param name="productEvent">Product event to capture.</param>
    /// <param name="cancellationToken">Cancellation token for the capture path.</param>
    /// <returns>A task that completes when registered sinks have accepted or skipped the event.</returns>
    ValueTask CaptureAsync(AppSurfaceProductEvent productEvent, CancellationToken cancellationToken = default);
}
