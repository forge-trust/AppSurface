namespace ForgeTrust.AppSurface.Observability;

/// <summary>
/// Marks that AppSurface tracing, metrics, resource, and startup-diagnostic services have already been registered.
/// </summary>
/// <remarks>
/// The service-collection extension uses this marker to make provider registration idempotent. First registration wins
/// because OpenTelemetry captures resource and exporter setup when providers are configured; later calls should not
/// silently replace the plan or create duplicate providers.
/// </remarks>
internal sealed class AppSurfaceObservabilityServicesRegistrationMarker;

/// <summary>
/// Marks that AppSurface OpenTelemetry logging has already been registered.
/// </summary>
/// <remarks>
/// The logging extension uses this marker to preserve first-registration-wins behavior for log exporter setup. Keep this
/// marker separate from <see cref="AppSurfaceObservabilityServicesRegistrationMarker"/> because logging is registered
/// through <c>ILoggingBuilder</c> before the tracing and metrics service path in module-based hosts.
/// </remarks>
internal sealed class AppSurfaceObservabilityLoggingRegistrationMarker;
