namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Receives validated AppSurface product-intelligence events for host-owned transport.
/// </summary>
/// <remarks>
/// Sinks are optional. AppSurface does not configure persistence, retention, access control, dashboards, or vendor
/// libraries. Register a sink only after deciding which host-owned analytics system should receive the sanitized event
/// stream.
/// </remarks>
public interface IAppSurfaceProductIntelligenceSink
{
    /// <summary>
    /// Receives one sanitized product-intelligence event.
    /// </summary>
    /// <param name="productEvent">Sanitized product event with only registry-allowed properties and safe envelope fields.</param>
    /// <param name="cancellationToken">Cancellation token for the capture path.</param>
    /// <returns>A task that completes when the sink has accepted the event.</returns>
    ValueTask CaptureAsync(AppSurfaceProductEvent productEvent, CancellationToken cancellationToken = default);
}
