namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Configures immutable metadata and request-input requirements for a named canary registration.
/// </summary>
/// <remarks>
/// Registration snapshots these values after the callback completes. Retaining and later mutating this options object
/// or its tag set does not change the registered descriptor.
/// </remarks>
public sealed class AppSurfaceCanaryRegistrationOptions
{
    /// <summary>Initializes registration options with the exact canary name as the default display name.</summary>
    /// <param name="name">The previously validated non-null registration name.</param>
    internal AppSurfaceCanaryRegistrationOptions(string name)
    {
        DisplayName = name;
    }

    /// <summary>Gets or sets the nonblank display name. It defaults to the registered name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Gets or sets an optional description of at most 512 characters.</summary>
    public string? Description { get; set; }

    /// <summary>Gets the mutable registration-time set of lowercase canary metadata tags.</summary>
    public ISet<string> Tags { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Gets the mutable registration-time set of detail keys an evaluator may return.</summary>
    /// <remarks>
    /// Registration snapshots this set. At most sixteen keys may be declared. Keys use the same 1-64 character,
    /// lowercase, dot-separated grammar as <see cref="AppSurfaceCanaryResultOptions.AddDetail"/>. Declaration bounds
    /// response shape; it does not classify, sanitize, or redact the values supplied by an application evaluator.
    /// </remarks>
    public ISet<string> AllowedDetailKeys { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Gets a value indicating whether the marker header is required.</summary>
    public bool MarkerRequired { get; private set; }

    /// <summary>Gets a value indicating whether the freshness header is required.</summary>
    public bool FreshSinceRequired { get; private set; }

    /// <summary>Makes the marker header required. Repeated calls are idempotent.</summary>
    public void RequireMarker() => MarkerRequired = true;

    /// <summary>Makes the freshness header required. Repeated calls are idempotent.</summary>
    public void RequireFreshSince() => FreshSinceRequired = true;
}
