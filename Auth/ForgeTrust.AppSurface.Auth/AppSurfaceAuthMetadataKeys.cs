namespace ForgeTrust.AppSurface.Auth;

/// <summary>
/// Defines reserved metadata keys used by AppSurface auth contracts.
/// </summary>
/// <remarks>
/// Metadata is context for diagnostics, display, and adapter hand-off. It is not an authorization source of truth unless
/// a host-owned adapter validates the value against the host security system. The <c>appsurface.</c> prefix is reserved
/// for AppSurface-owned keys so future typed properties can migrate existing metadata without key collisions.
/// </remarks>
public static class AppSurfaceAuthMetadataKeys
{
    /// <summary>
    /// Metadata key for a host-validated tenant identifier.
    /// </summary>
    public const string TenantId = "appsurface.tenant_id";

    /// <summary>
    /// Metadata key for host-validated permission or scope hints.
    /// </summary>
    public const string PermissionHints = "appsurface.permission_hints";

    /// <summary>
    /// Metadata key for the authentication scheme that produced the current auth context.
    /// </summary>
    public const string AuthenticationScheme = "appsurface.authentication_scheme";

    /// <summary>
    /// Metadata key for the host-authenticated subject identifier.
    /// </summary>
    public const string SubjectId = "appsurface.subject_id";

    /// <summary>
    /// Metadata key for a host-generated auth correlation identifier.
    /// </summary>
    public const string CorrelationId = "appsurface.correlation_id";
}
