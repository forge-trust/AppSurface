namespace ForgeTrust.RazorWire;

/// <summary>
/// Represents configuration options for the RazorWire real-time streaming and caching system.
/// </summary>
public class RazorWireOptions
{
    /// <summary>
    /// Gets a default instance of <see cref="RazorWireOptions"/> with default configuration settings.
    /// </summary>
    public static RazorWireOptions Default { get; } = new();

    /// <summary>
    /// Gets configuration options for real-time streams, such as the base path for stream connections.
    /// </summary>
    public RazorWireStreamOptions Streams { get; } = new();

    /// <summary>
    /// Gets configuration options for output caching policies used by RazorWire.
    /// </summary>
    public RazorWireCacheOptions Caching { get; } = new();

    /// <summary>
    /// Gets configuration options for RazorWire-enhanced form submissions.
    /// </summary>
    public RazorWireFormOptions Forms { get; } = new();

    /// <summary>
    /// Gets configuration options for split-origin hybrid deployments.
    /// </summary>
    public RazorWireHybridOptions Hybrid { get; } = new();
}

/// <summary>
/// Represents split-origin hybrid deployment options for RazorWire-managed live interactions.
/// </summary>
public class RazorWireHybridOptions
{
    /// <summary>
    /// Gets or sets the absolute live origin used by exported static pages for RazorWire-managed dynamic calls.
    /// </summary>
    /// <remarks>
    /// The value must be an origin only, such as <c>https://api.example.com</c>, without a path, query string,
    /// fragment, or user information. When unset, RazorWire preserves same-origin runtime behavior.
    /// </remarks>
    public string? LiveOrigin { get; set; }

    /// <summary>
    /// Gets or sets how RazorWire-managed live calls include browser credentials in hybrid deployments.
    /// Defaults to <see cref="RazorWireHybridCredentialsMode.Auto"/>.
    /// </summary>
    public RazorWireHybridCredentialsMode CredentialsMode { get; set; } = RazorWireHybridCredentialsMode.Auto;

    /// <summary>
    /// Gets or sets the optional ASP.NET Core CORS policy applied to RazorWire-owned hybrid endpoints.
    /// </summary>
    public string? CorsPolicyName { get; set; }
}

/// <summary>
/// Defines credential behavior for RazorWire-managed live calls from exported hybrid pages.
/// </summary>
/// <remarks>
/// The numeric values are explicit because this public enum may be bound from configuration or serialized in export
/// manifests. New values should be appended without changing existing values.
/// </remarks>
public enum RazorWireHybridCredentialsMode
{
    /// <summary>
    /// Preserve same-origin behavior unless a split live origin is configured; split-origin managed calls include
    /// credentials by default.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Include credentials for RazorWire-managed live calls.
    /// </summary>
    Include = 1,

    /// <summary>
    /// Omit credentials for RazorWire-managed live calls. Use only for public live surfaces that do not use cookies,
    /// sessions, anti-forgery, tenant context, or personalization.
    /// </summary>
    Omit = 2
}

/// <summary>
/// Represents configuration options for RazorWire real-time streams.
/// </summary>
public class RazorWireStreamOptions
{
    /// <summary>
    /// Gets or sets the base path used for establishing stream connections.
    /// Defaults to <c>"/_rw/streams"</c>.
    /// </summary>
    public string BasePath { get; set; } = "/_rw/streams";

    /// <summary>
    /// Gets or sets the built-in authorization behavior for stream subscriptions.
    /// Defaults to <see cref="RazorWireStreamAuthorizationMode.DenyAll"/>.
    /// </summary>
    /// <remarks>
    /// RazorWire streams are safe by default because channel names frequently encode user, tenant, or workflow context.
    /// Use <see cref="RazorWireStreamAuthorizationMode.AllowAll"/> only for public, demo, or otherwise non-sensitive
    /// channels. Register <see cref="Streams.IRazorWireChannelAuthorizer"/> when subscription decisions must inspect
    /// the current <c>HttpContext</c>, user, claims, route data, or tenant state.
    /// </remarks>
    public RazorWireStreamAuthorizationMode AuthorizationMode { get; set; } = RazorWireStreamAuthorizationMode.DenyAll;
}

/// <summary>
/// Defines RazorWire's built-in stream subscription authorization behavior.
/// </summary>
/// <remarks>
/// The numeric values are explicit because this public enum may be persisted, serialized, or bound by applications.
/// New values should be appended without changing the values documented here.
/// </remarks>
public enum RazorWireStreamAuthorizationMode
{
    /// <summary>
    /// Deny every stream subscription unless the app registers a custom <see cref="Streams.IRazorWireChannelAuthorizer"/>.
    /// </summary>
    /// <remarks>
    /// This is the default and is appropriate for apps that have not yet made an explicit channel authorization decision.
    /// </remarks>
    DenyAll = 0,

    /// <summary>
    /// Allow every stream subscription.
    /// </summary>
    /// <remarks>
    /// Use this only for public, demo, or local-development streams that do not expose user-specific, tenant-specific,
    /// or workflow-specific data. Production streams should usually register a custom
    /// <see cref="Streams.IRazorWireChannelAuthorizer"/> instead.
    /// </remarks>
    AllowAll = 1
}

/// <summary>
/// Represents configuration options for RazorWire output caching.
/// </summary>
public class RazorWireCacheOptions
{
    /// <summary>
    /// Gets or sets the name of the output cache policy for full pages.
    /// Defaults to <c>"rw-page"</c>.
    /// </summary>
    public string PagePolicyName { get; set; } = "rw-page";

    /// <summary>
    /// Gets or sets the name of the output cache policy for individual islands.
    /// Defaults to <c>"rw-island"</c>.
    /// </summary>
    public string IslandPolicyName { get; set; } = "rw-island";
}

/// <summary>
/// Represents configuration options for failed <c>rw-active</c> form submissions.
/// </summary>
/// <remarks>
/// These options control the package convention for server failures from enhanced forms. The global
/// <see cref="EnableFailureUx"/> switch has highest precedence: when it is <see langword="false"/>, RazorWire skips
/// request markers, runtime lifecycle events, default fallback rendering, and development anti-forgery diagnostics even
/// if <see cref="FailureMode"/> or a form-level attribute asks for them. Leave the global switch enabled and use
/// <see cref="FailureMode"/> or per-form <c>data-rw-form-failure</c> values when an app wants more targeted behavior.
/// </remarks>
public class RazorWireFormOptions
{
    private const string DefaultFailureMessageFallback = "We could not submit this form. Check your input and try again.";
    private string _defaultFailureMessage = DefaultFailureMessageFallback;

    /// <summary>
    /// Gets or sets a value indicating whether RazorWire emits failed-form request markers,
    /// lifecycle hooks, and default failure behavior.
    /// Defaults to <c>true</c>.
    /// </summary>
    /// <remarks>
    /// Set this to <see langword="false"/> only when the host app owns all failed-form UX. It is a hard kill switch and
    /// overrides <see cref="FailureMode"/> plus any per-form <c>data-rw-form-failure</c> setting.
    /// </remarks>
    public bool EnableFailureUx { get; set; } = true;

    /// <summary>
    /// Gets or sets the package-level failed-form behavior.
    /// Defaults to <see cref="RazorWireFormFailureMode.Auto"/>.
    /// </summary>
    /// <remarks>
    /// Use <see cref="RazorWireFormFailureMode.Auto"/> for convention-over-configuration fallback UI,
    /// <see cref="RazorWireFormFailureMode.Manual"/> when the app listens to events and renders its own UI, and
    /// <see cref="RazorWireFormFailureMode.Off"/> when forms should opt into failure handling one at a time.
    /// </remarks>
    public RazorWireFormFailureMode FailureMode { get; set; } = RazorWireFormFailureMode.Auto;

    /// <summary>
    /// Gets or sets a value indicating whether development-only diagnostics may be shown.
    /// Defaults to <c>true</c>; diagnostics are still emitted only when the host is running in Development.
    /// </summary>
    /// <remarks>
    /// Diagnostics appear only when failed-form UX is enabled, the app runs in Development, and the failure path can be
    /// identified as a RazorWire form request. Production responses stay generic even when this property is
    /// <see langword="true"/>.
    /// </remarks>
    public bool EnableDevelopmentDiagnostics { get; set; } = true;

    /// <summary>
    /// Gets or sets the safe default message used for generic failed form submissions.
    /// </summary>
    /// <remarks>
    /// Null, empty, and whitespace-only assignments are normalized back to RazorWire's safe fallback copy. Use a
    /// non-empty value for product-specific recovery language.
    /// </remarks>
    public string DefaultFailureMessage
    {
        get => _defaultFailureMessage;
        set => _defaultFailureMessage = string.IsNullOrWhiteSpace(value)
            ? DefaultFailureMessageFallback
            : value;
    }

    /// <summary>
    /// Gets configuration options for RazorWire anti-forgery token refresh behavior.
    /// </summary>
    public RazorWireFormAntiforgeryOptions Antiforgery { get; } = new();
}

/// <summary>
/// Represents lazy anti-forgery token refresh options for RazorWire forms.
/// </summary>
public class RazorWireFormAntiforgeryOptions
{
    /// <summary>
    /// Gets or sets the anti-forgery token refresh endpoint path.
    /// Defaults to <c>"/_rw/antiforgery/token"</c>.
    /// </summary>
    public string TokenEndpointPath { get; set; } = "/_rw/antiforgery/token";
}

/// <summary>
/// Defines the package-level failed-form behavior for <c>rw-active</c> forms.
/// </summary>
/// <remarks>
/// This enum only applies when <see cref="RazorWireFormOptions.EnableFailureUx"/> is enabled. Per-form
/// <c>data-rw-form-failure</c> values may narrow behavior for a specific form, but the global kill switch always wins.
/// The numeric values are explicit because this public enum may be persisted, serialized, or bound by
/// applications. New values should be appended without changing the values documented here.
/// </remarks>
public enum RazorWireFormFailureMode
{
    /// <summary>
    /// Render RazorWire's default form-local fallback UI when the server did not handle the failure.
    /// </summary>
    /// <remarks>
    /// Choose this when the app wants package-owned recovery UI for unhandled <c>400</c>, <c>401</c>, <c>403</c>,
    /// <c>422</c>, and <c>500</c> form responses, while still allowing controllers to render handled errors with
    /// <see cref="Bridge.RazorWireStreamBuilder.FormError(string,string,string)"/> or
    /// <see cref="Bridge.RazorWireStreamBuilder.FormValidationErrors(string,Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary,string,int,string)"/>.
    /// </remarks>
    Auto = 0,

    /// <summary>
    /// Emit request markers and lifecycle events, but do not render fallback UI.
    /// </summary>
    /// <remarks>
    /// Choose this when the app needs RazorWire request classification and cancelable failure events but wants to own
    /// every visible error surface.
    /// </remarks>
    Manual = 1,

    /// <summary>
    /// Disable RazorWire's failed-form request markers, events, and fallback UI by default.
    /// </summary>
    /// <remarks>
    /// Choose this when most forms should behave like plain enhanced Turbo forms and only selected forms should opt back
    /// into RazorWire failure handling with per-form attributes.
    /// </remarks>
    Off = 2
}
