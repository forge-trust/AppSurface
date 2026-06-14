namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Source-of-truth registry for AppSurface product-intelligence event contracts.
/// </summary>
public static class AppSurfaceProductEventRegistry
{
    /// <summary>
    /// Event name for docs search submissions.
    /// </summary>
    public const string DocsSearchSubmitted = "docs.search.submitted";

    /// <summary>
    /// Event name for docs searches with no matching result.
    /// </summary>
    public const string DocsSearchReturnedZeroResults = "docs.search.returned_zero_results";

    /// <summary>
    /// Event name for selected docs search results.
    /// </summary>
    public const string DocsSearchResultSelected = "docs.search.result_selected";

    /// <summary>
    /// Event name for docs recovery links selected after search or load friction.
    /// </summary>
    public const string DocsRecoveryLinkSelected = "docs.recovery_link.selected";

    /// <summary>
    /// Event name for docs search filter changes.
    /// </summary>
    public const string DocsSearchFilterChanged = "docs.search.filter_changed";

    /// <summary>
    /// Event name for reader feedback on docs search friction recovery.
    /// </summary>
    public const string DocsSearchFrictionFeedbackSubmitted = "docs.search.friction_feedback_submitted";

    /// <summary>
    /// Event name for RazorWire form failures.
    /// </summary>
    public const string RazorWireFormFailed = "razorwire.form.failed";

    /// <summary>
    /// Event name for RazorWire form failure recovery.
    /// </summary>
    public const string RazorWireFormFailureRecovered = "razorwire.form.failure_recovered";

    /// <summary>
    /// Event name for RazorWire stream admission rejections.
    /// </summary>
    public const string RazorWireStreamAdmissionRejected = "razorwire.stream.admission_rejected";

    private static readonly string[] GlobalForbiddenPropertyNames =
    [
        "body",
        "config",
        "connection_string",
        "connectionstring",
        "cookie",
        "exception",
        "password",
        "query",
        "raw_query",
        "request_body",
        "requestbody",
        "secret",
        "stack",
        "stack_trace",
        "token"
    ];

    private static readonly string[] DocsSurfaces = ["search_page", "sidebar"];
    private static readonly string[] DocsResultKinds =
    [
        "api-reference",
        "concept",
        "example",
        "explanation",
        "glossary",
        "guide",
        "how-to",
        "javascript-api",
        "javascript-attribute",
        "javascript-config",
        "javascript-css-custom-property",
        "javascript-css-hook",
        "javascript-event",
        "javascript-global",
        "javascript-module-contract",
        "release",
        "release-log",
        "start-here",
        "troubleshooting",
        "tutorial",
        "unknown"
    ];

    private static readonly string[] DocsRecoveryLinkKinds = ["api-reference", "example", "fallback", "guide", "server_fallback"];
    private static readonly string[] DocsRecoverySourceStates = ["loading", "no_results", "unavailable"];
    private static readonly string[] DocsFilterKeys = ["audience", "component", "language", "pageType", "status"];
    private static readonly string[] DocsFilterActions = ["cleared", "cleared_all", "selected"];
    private static readonly string[] DocsFrictionFeedbackValues = ["not_useful", "useful"];
    private static readonly string[] RazorWireFailureModes = ["handled", "unhandled"];
    private static readonly string[] RazorWireResponseKinds = ["html", "json", "network", "turbo-stream", "unknown"];
    private static readonly string[] RazorWireFailureUi = ["disabled", "generated", "handled", "suppressed"];
    private static readonly string[] RazorWireRecoveryActions = ["next_success", "retry_submit"];
    private static readonly string[] RazorWireAdmissionReasons =
    [
        "ChannelNameTooLong",
        "InvalidChannelName",
        "TooManyLiveChannels",
        "TooManyLiveSubscriptions",
        "TooManyLiveSubscriptionsPerChannel"
    ];

    private static readonly string[] RazorWireAdmissionLimits =
    [
        "channel_name",
        "channel_name_length",
        "max_live_channels",
        "max_live_subscriptions",
        "max_live_subscriptions_per_channel",
        "unknown"
    ];

    private static readonly string[] RazorWireAuthorizationModes = ["AllowAll", "DenyAll"];

    private static readonly AppSurfaceProductEventContract[] Contracts =
    [
        new(
            DocsSearchSubmitted,
            AppSurfaceProductEventLifecycle.Experimental,
            "Understand whether docs search queries are finding useful result sets without storing the query text.",
            "ForgeTrust.AppSurface.Docs",
            "Short product-quality retention; aggregate before long-term storage.",
            [
                Property("query_length", "Normalized character count for the submitted search text.", AppSurfaceProductEventSensitivity.Behavioral, AppSurfaceProductEventCardinality.Medium, valueShape: AppSurfaceProductEventValueShape.NonNegativeInteger),
                Property("result_count", "Number of safe docs results returned to the reader.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Medium, valueShape: AppSurfaceProductEventValueShape.NonNegativeInteger),
                Property("active_filter_count", "Number of active structured search filters.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, valueShape: AppSurfaceProductEventValueShape.NonNegativeInteger),
                Property("surface", "Docs surface that emitted the event, such as sidebar or search_page.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: DocsSurfaces, valueShape: AppSurfaceProductEventValueShape.AllowedValue)
            ],
            ["raw query text", "request body", "full result URL", "reader identity"]),
        new(
            DocsSearchReturnedZeroResults,
            AppSurfaceProductEventLifecycle.Experimental,
            "Find docs topics where readers hit a dead end and need better navigation or content.",
            "ForgeTrust.AppSurface.Docs",
            "Short product-quality retention; aggregate before long-term storage.",
            [
                Property("query_length", "Normalized character count for the submitted search text.", AppSurfaceProductEventSensitivity.Behavioral, AppSurfaceProductEventCardinality.Medium, valueShape: AppSurfaceProductEventValueShape.NonNegativeInteger),
                Property("active_filter_count", "Number of active structured search filters.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, valueShape: AppSurfaceProductEventValueShape.NonNegativeInteger),
                Property("surface", "Docs surface that emitted the event, such as sidebar or search_page.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: DocsSurfaces, valueShape: AppSurfaceProductEventValueShape.AllowedValue)
            ],
            ["raw query text", "request body", "full filter values that contain user input"]),
        new(
            DocsSearchResultSelected,
            AppSurfaceProductEventLifecycle.Experimental,
            "Measure whether search results lead readers into useful docs areas.",
            "ForgeTrust.AppSurface.Docs",
            "Short product-quality retention; aggregate by result kind rather than full URL when possible.",
            [
                Property("result_rank", "One-based rank of the selected result.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Medium, valueShape: AppSurfaceProductEventValueShape.NonNegativeInteger),
                Property("result_kind", "Normalized page type or fallback result kind.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, allowedValues: DocsResultKinds, valueShape: AppSurfaceProductEventValueShape.AllowedValue),
                Property("surface", "Docs surface that emitted the event, such as sidebar or search_page.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: DocsSurfaces, valueShape: AppSurfaceProductEventValueShape.AllowedValue)
            ],
            ["raw query text", "full URL with query string", "document body"]),
        new(
            DocsRecoveryLinkSelected,
            AppSurfaceProductEventLifecycle.Experimental,
            "Understand which recovery paths help readers continue after search friction.",
            "ForgeTrust.AppSurface.Docs",
            "Short product-quality retention; aggregate by link kind and source state.",
            [
                Property("link_kind", "Normalized recovery-link kind, such as guide, api-reference, example, or fallback.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: DocsRecoveryLinkKinds, valueShape: AppSurfaceProductEventValueShape.AllowedValue),
                Property("source_state", "State that rendered the recovery path, such as no_results, loading, or unavailable.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: DocsRecoverySourceStates, valueShape: AppSurfaceProductEventValueShape.AllowedValue),
                Property("surface", "Docs surface that emitted the event.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: DocsSurfaces, valueShape: AppSurfaceProductEventValueShape.AllowedValue)
            ],
            ["raw query text", "full URL with query string", "reader identity"]),
        new(
            DocsSearchFilterChanged,
            AppSurfaceProductEventLifecycle.Experimental,
            "Understand which structured docs filters readers use without storing filter values or query text.",
            "ForgeTrust.AppSurface.Docs",
            "Short product-quality retention; aggregate by filter key and action.",
            [
                Property("surface", "Docs surface that emitted the event.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: DocsSurfaces, valueShape: AppSurfaceProductEventValueShape.AllowedValue),
                Property("filter_key", "Structured filter key changed by the reader.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: DocsFilterKeys, valueShape: AppSurfaceProductEventValueShape.AllowedValue),
                Property("filter_action", "Low-cardinality filter action such as selected, cleared, or cleared_all.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: DocsFilterActions, valueShape: AppSurfaceProductEventValueShape.AllowedValue),
                Property("active_filter_count", "Number of active structured search filters after the change.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, valueShape: AppSurfaceProductEventValueShape.NonNegativeInteger),
                Property("query_length", "Normalized character count for the active search text.", AppSurfaceProductEventSensitivity.Behavioral, AppSurfaceProductEventCardinality.Medium, valueShape: AppSurfaceProductEventValueShape.NonNegativeInteger)
            ],
            ["raw query text", "filter values", "full URL", "reader identity"]),
        new(
            DocsSearchFrictionFeedbackSubmitted,
            AppSurfaceProductEventLifecycle.Experimental,
            "Measure whether no-results and recovery affordances helped readers continue.",
            "ForgeTrust.AppSurface.Docs",
            "Short product-quality retention; aggregate by source state and feedback value.",
            [
                Property("surface", "Docs surface that emitted the event.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: DocsSurfaces, valueShape: AppSurfaceProductEventValueShape.AllowedValue),
                Property("source_state", "Search-friction state that rendered the feedback control.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: DocsRecoverySourceStates, valueShape: AppSurfaceProductEventValueShape.AllowedValue),
                Property("feedback_value", "Reader feedback value for the recovery affordance.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: DocsFrictionFeedbackValues, valueShape: AppSurfaceProductEventValueShape.AllowedValue),
                Property("active_filter_count", "Number of active structured search filters at feedback time.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, valueShape: AppSurfaceProductEventValueShape.NonNegativeInteger),
                Property("query_length", "Normalized character count for the active search text.", AppSurfaceProductEventSensitivity.Behavioral, AppSurfaceProductEventCardinality.Medium, valueShape: AppSurfaceProductEventValueShape.NonNegativeInteger),
                Property("link_kind", "Optional normalized recovery-link kind shown with the feedback control.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, allowedValues: DocsRecoveryLinkKinds, valueShape: AppSurfaceProductEventValueShape.AllowedValue)
            ],
            ["raw query text", "free-form comments", "full URL", "reader identity"]),
        new(
            RazorWireFormFailed,
            AppSurfaceProductEventLifecycle.Experimental,
            "Find form failure modes that deserve better framework recovery or host guidance.",
            "ForgeTrust.RazorWire",
            "Short framework-quality retention; avoid retaining host form identifiers.",
            [
                Property("failure_mode", "Whether RazorWire handled the failure response before surfacing failure UX.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: RazorWireFailureModes, valueShape: AppSurfaceProductEventValueShape.AllowedValue),
                Property("http_status", "HTTP status code when available.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, valueShape: AppSurfaceProductEventValueShape.NonNegativeInteger),
                Property("response_kind", "Normalized response kind such as turbo-stream, html, json, unknown, or network.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, allowedValues: RazorWireResponseKinds, valueShape: AppSurfaceProductEventValueShape.AllowedValue),
                Property("failure_ui", "Failure UX outcome such as generated, handled, suppressed, or disabled.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, allowedValues: RazorWireFailureUi, valueShape: AppSurfaceProductEventValueShape.AllowedValue)
            ],
            ["form field values", "request body", "anti-forgery token", "stack trace"]),
        new(
            RazorWireFormFailureRecovered,
            AppSurfaceProductEventLifecycle.Experimental,
            "Measure whether generated RazorWire failure UX leads to a later successful submit.",
            "ForgeTrust.RazorWire",
            "Short framework-quality retention; aggregate by recovery action.",
            [
                Property("recovery_action", "Normalized recovery action such as retry_submit or next_success.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: RazorWireRecoveryActions, valueShape: AppSurfaceProductEventValueShape.AllowedValue),
                Property("attempt_count", "Small count of attempts observed by the runtime.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, valueShape: AppSurfaceProductEventValueShape.NonNegativeInteger)
            ],
            ["form field values", "request body", "anti-forgery token", "stack trace"]),
        new(
            RazorWireStreamAdmissionRejected,
            AppSurfaceProductEventLifecycle.Experimental,
            "Understand stream pressure and channel validation failures without exposing channel names.",
            "ForgeTrust.RazorWire",
            "Short framework-quality retention; aggregate by rejection reason and limit name.",
            [
                Property("rejection_reason", "RazorWire stream admission rejection reason.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: RazorWireAdmissionReasons, valueShape: AppSurfaceProductEventValueShape.AllowedValue),
                Property("limit_name", "Normalized limit that caused the rejection.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: RazorWireAdmissionLimits, valueShape: AppSurfaceProductEventValueShape.AllowedValue),
                Property("current_count", "Current count or input length that tripped the limit.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Medium, valueShape: AppSurfaceProductEventValueShape.NonNegativeInteger),
                Property("authorization_mode", "Configured stream authorization mode.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, allowedValues: RazorWireAuthorizationModes, valueShape: AppSurfaceProductEventValueShape.AllowedValue)
            ],
            ["channel name", "route value", "user id", "email", "authorization token"])
    ];

    private static readonly IReadOnlyDictionary<string, AppSurfaceProductEventContract> ContractsByName =
        Contracts.ToDictionary(contract => contract.Name, StringComparer.Ordinal);

    private static readonly HashSet<string> ForbiddenPropertyNames =
        GlobalForbiddenPropertyNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets every registered AppSurface product event contract.
    /// </summary>
    public static IReadOnlyList<AppSurfaceProductEventContract> All { get; } = Array.AsReadOnly(Contracts);

    /// <summary>
    /// Gets globally forbidden property names that are always dropped from emitted payloads.
    /// </summary>
    public static IReadOnlySet<string> ForbiddenProperties =>
        new HashSet<string>(ForbiddenPropertyNames, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Finds a registered contract by event name.
    /// </summary>
    /// <param name="name">Event name to look up.</param>
    /// <returns>The matching contract, or <see langword="null"/> when no contract is registered.</returns>
    public static AppSurfaceProductEventContract? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return ContractsByName.GetValueOrDefault(name);
    }

    /// <summary>
    /// Validates and sanitizes a product event against the typed registry.
    /// </summary>
    /// <param name="productEvent">Event instance to validate.</param>
    /// <returns>Validation result with safe diagnostics and sanitized properties.</returns>
    public static AppSurfaceProductEventValidationResult Validate(AppSurfaceProductEvent productEvent)
    {
        ArgumentNullException.ThrowIfNull(productEvent);

        return AppSurfaceProductEventValidationEngine.Validate(
            productEvent,
            ContractsByName,
            ForbiddenPropertyNames);
    }

    private static AppSurfaceProductEventPropertyContract Property(
        string name,
        string description,
        AppSurfaceProductEventSensitivity sensitivity,
        AppSurfaceProductEventCardinality cardinality,
        bool required = false,
        IEnumerable<string>? allowedValues = null,
        int maxLength = 64,
        AppSurfaceProductEventValueShape valueShape = AppSurfaceProductEventValueShape.Token)
    {
        return new AppSurfaceProductEventPropertyContract(
            name,
            description,
            sensitivity,
            cardinality,
            valueShape,
            required: required,
            allowedValues: allowedValues,
            maxLength: maxLength);
    }
}
