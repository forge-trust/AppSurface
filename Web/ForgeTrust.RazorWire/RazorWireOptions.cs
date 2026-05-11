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
