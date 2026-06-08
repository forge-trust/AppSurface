using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Default product-intelligence dispatcher registered by AppSurface.
/// </summary>
public sealed class AppSurfaceProductIntelligenceDispatcher : IAppSurfaceProductIntelligence
{
    private readonly IOptions<AppSurfaceProductIntelligenceOptions> _options;
    private readonly IEnumerable<IAppSurfaceProductIntelligenceSink> _sinks;

    /// <summary>
    /// Creates the default dispatcher.
    /// </summary>
    /// <param name="options">Product-intelligence options.</param>
    /// <param name="sinks">Optional host-owned sinks.</param>
    public AppSurfaceProductIntelligenceDispatcher(
        IOptions<AppSurfaceProductIntelligenceOptions> options,
        IEnumerable<IAppSurfaceProductIntelligenceSink> sinks)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sinks);

        _options = options;
        _sinks = sinks;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Invalid events are discarded silently after <see cref="AppSurfaceProductEventRegistry.Validate(AppSurfaceProductEvent)" />.
    /// Experimental contracts are also ignored unless <see cref="AppSurfaceProductIntelligenceOptions.ExperimentalEventsEnabled" />
    /// is enabled for the current options instance. Registered sinks run sequentially, and non-cancellation sink failures
    /// are swallowed so product-intelligence capture cannot break request paths. Callers should treat this method as
    /// best-effort delivery and should not rely on it to surface sink errors or guarantee that a downstream analytics
    /// provider accepted the event.
    /// </remarks>
    public async ValueTask CaptureAsync(AppSurfaceProductEvent productEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(productEvent);
        cancellationToken.ThrowIfCancellationRequested();

        var validation = AppSurfaceProductEventRegistry.Validate(productEvent);
        if (!validation.IsValid || validation.Contract is null)
        {
            return;
        }

        if (validation.Contract.Lifecycle == AppSurfaceProductEventLifecycle.Experimental
            && !_options.Value.IsExperimentalEventEnabled(validation.Contract.Name))
        {
            return;
        }

        var sanitizedEvent = productEvent.WithSanitizedEnvelope(validation.SanitizedProperties);
        foreach (var sink in _sinks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await sink.CaptureAsync(sanitizedEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Product-intelligence sinks are host-owned and must not break request paths.
            }
        }
    }
}
