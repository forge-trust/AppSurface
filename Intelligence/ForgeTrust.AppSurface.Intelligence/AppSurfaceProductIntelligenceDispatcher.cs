using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Default product-intelligence dispatcher registered by AppSurface.
/// </summary>
public sealed class AppSurfaceProductIntelligenceDispatcher : IAppSurfaceProductIntelligence
{
    private readonly IOptions<AppSurfaceProductIntelligenceOptions> _options;
    private readonly IAppSurfaceProductEventRegistry _registry;
    private readonly IEnumerable<IAppSurfaceProductIntelligenceSink> _sinks;

    /// <summary>
    /// Creates the default dispatcher.
    /// </summary>
    /// <param name="options">Product-intelligence options.</param>
    /// <param name="sinks">Optional host-owned sinks.</param>
    public AppSurfaceProductIntelligenceDispatcher(
        IOptions<AppSurfaceProductIntelligenceOptions> options,
        IEnumerable<IAppSurfaceProductIntelligenceSink> sinks)
        : this(options, new BuiltInAppSurfaceProductEventRegistry(), sinks)
    {
    }

    /// <summary>
    /// Creates the default dispatcher with a composed product-event registry.
    /// </summary>
    /// <param name="options">Product-intelligence options.</param>
    /// <param name="registry">Composed product-event registry.</param>
    /// <param name="sinks">Optional host-owned sinks.</param>
    public AppSurfaceProductIntelligenceDispatcher(
        IOptions<AppSurfaceProductIntelligenceOptions> options,
        IAppSurfaceProductEventRegistry registry,
        IEnumerable<IAppSurfaceProductIntelligenceSink> sinks)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(sinks);

        _options = options;
        _registry = registry;
        _sinks = sinks;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Invalid events are discarded silently after <see cref="IAppSurfaceProductEventRegistry.Validate(AppSurfaceProductEvent)" />.
    /// Experimental contracts are also ignored unless <see cref="AppSurfaceProductIntelligenceOptions.ExperimentalEventsEnabled" />
    /// enables the whole experimental surface or <see cref="AppSurfaceProductIntelligenceOptions.IsExperimentalEventEnabled(string)" />
    /// enables that specific event name through the per-event allowlist. Registered sinks run sequentially, and
    /// non-cancellation sink failures are swallowed so product-intelligence capture cannot break request paths. Callers
    /// should treat this method as best-effort delivery and should not rely on it to surface sink errors or guarantee
    /// that a downstream analytics provider accepted the event.
    /// </remarks>
    public async ValueTask CaptureAsync(AppSurfaceProductEvent productEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(productEvent);
        cancellationToken.ThrowIfCancellationRequested();

        var validation = _registry.Validate(productEvent);
        if (!validation.IsValid || validation.Contract is null)
        {
            ThrowIfEnabled(productEvent.Name, validation);
            return;
        }

        if (validation.Contract.Lifecycle == AppSurfaceProductEventLifecycle.Experimental
            && !_options.Value.IsExperimentalEventEnabled(validation.Contract.Name))
        {
            ThrowIfEnabled(
                productEvent.Name,
                new AppSurfaceProductEventValidationResult(
                    validation.Contract,
                    isValid: false,
                    validation.SanitizedProperties,
                    validation.RejectedProperties,
                    validation.Diagnostics,
                    [AppSurfaceProductEventValidationFailureReason.ExperimentalEventNotEnabled],
                    "Call options.EnableExperimentalEvents(...) for this experimental event contract."));
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

    private void ThrowIfEnabled(string eventName, AppSurfaceProductEventValidationResult validation)
    {
        if (!_options.Value.ThrowOnInvalidEventsEnabled)
        {
            return;
        }

        throw new AppSurfaceProductEventValidationException(
            eventName,
            validation,
            validation.FixHint ?? "Register the event contract and send only properties declared by that contract.");
    }
}
