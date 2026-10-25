namespace ForgeTrust.AppSurface.Aspire.Testing;

/// <summary>Identifies one Aspire resource endpoint and its HTTP readiness path.</summary>
/// <param name="ResourceName">The exact Aspire resource name.</param>
/// <param name="EndpointName">The exact endpoint name allocated by Aspire.</param>
/// <param name="ProbePath">A relative or root-relative HTTP path that preserves the endpoint origin.</param>
/// <remarks>
/// This target checks native Aspire resource health followed by one declared status-only HTTP condition. The path is
/// validated before the builder factory is invoked. The probe does not read a response body or constrain redirects
/// selected by the supplied client.
/// </remarks>
public sealed record AspireHttpReadinessTarget(string ResourceName, string EndpointName, string ProbePath);
