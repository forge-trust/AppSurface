namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Source-of-truth registry for AppSurface product-intelligence event contracts.
/// </summary>
public static class AppSurfaceProductEventRegistry
{
    /// <summary>
    /// Stable event name for docs search submissions.
    /// </summary>
    public const string DocsSearchSubmitted = "docs.search.submitted";

    /// <summary>
    /// Stable event name for docs searches with no matching result.
    /// </summary>
    public const string DocsSearchReturnedZeroResults = "docs.search.returned_zero_results";

    /// <summary>
    /// Stable event name for selected docs search results.
    /// </summary>
    public const string DocsSearchResultSelected = "docs.search.result_selected";

    /// <summary>
    /// Stable event name for docs recovery links selected after search or load friction.
    /// </summary>
    public const string DocsRecoveryLinkSelected = "docs.recovery_link.selected";

    /// <summary>
    /// Stable event name for RazorWire form failures.
    /// </summary>
    public const string RazorWireFormFailed = "razorwire.form.failed";

    /// <summary>
    /// Stable event name for RazorWire form failure recovery.
    /// </summary>
    public const string RazorWireFormFailureRecovered = "razorwire.form.failure_recovered";

    /// <summary>
    /// Stable event name for RazorWire stream admission rejections.
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
    private static readonly string[] RazorWireFailureModes = ["handled", "html", "json", "network", "turbo-stream", "unknown"];
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
                Property("query_length", "Normalized character count for the submitted search text.", AppSurfaceProductEventSensitivity.Behavioral, AppSurfaceProductEventCardinality.Medium),
                Property("result_count", "Number of safe docs results returned to the reader.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Medium),
                Property("active_filter_count", "Number of active structured search filters.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low),
                Property("surface", "Docs surface that emitted the event, such as sidebar or search_page.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: DocsSurfaces)
            ],
            ["raw query text", "request body", "full result URL", "reader identity"]),
        new(
            DocsSearchReturnedZeroResults,
            AppSurfaceProductEventLifecycle.Experimental,
            "Find docs topics where readers hit a dead end and need better navigation or content.",
            "ForgeTrust.AppSurface.Docs",
            "Short product-quality retention; aggregate before long-term storage.",
            [
                Property("query_length", "Normalized character count for the submitted search text.", AppSurfaceProductEventSensitivity.Behavioral, AppSurfaceProductEventCardinality.Medium),
                Property("active_filter_count", "Number of active structured search filters.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low),
                Property("surface", "Docs surface that emitted the event, such as sidebar or search_page.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: DocsSurfaces)
            ],
            ["raw query text", "request body", "full filter values that contain user input"]),
        new(
            DocsSearchResultSelected,
            AppSurfaceProductEventLifecycle.Experimental,
            "Measure whether search results lead readers into useful docs areas.",
            "ForgeTrust.AppSurface.Docs",
            "Short product-quality retention; aggregate by result kind rather than full URL when possible.",
            [
                Property("result_rank", "One-based rank of the selected result.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Medium),
                Property("result_kind", "Normalized page type or fallback result kind.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, allowedValues: DocsResultKinds),
                Property("surface", "Docs surface that emitted the event, such as sidebar or search_page.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: DocsSurfaces)
            ],
            ["raw query text", "full URL with query string", "document body"]),
        new(
            DocsRecoveryLinkSelected,
            AppSurfaceProductEventLifecycle.Experimental,
            "Understand which recovery paths help readers continue after search friction.",
            "ForgeTrust.AppSurface.Docs",
            "Short product-quality retention; aggregate by link kind and source state.",
            [
                Property("link_kind", "Normalized recovery-link kind, such as guide, api-reference, example, or fallback.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: DocsRecoveryLinkKinds),
                Property("source_state", "State that rendered the recovery path, such as no_results, loading, or unavailable.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: DocsRecoverySourceStates),
                Property("surface", "Docs surface that emitted the event.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: DocsSurfaces)
            ],
            ["raw query text", "full URL with query string", "reader identity"]),
        new(
            RazorWireFormFailed,
            AppSurfaceProductEventLifecycle.Experimental,
            "Find form failure modes that deserve better framework recovery or host guidance.",
            "ForgeTrust.RazorWire",
            "Short framework-quality retention; avoid retaining host form identifiers.",
            [
                Property("failure_mode", "Normalized failure mode such as handled, network, html, json, turbo-stream, or unknown.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: RazorWireFailureModes),
                Property("http_status", "HTTP status family or status code when available.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low),
                Property("response_kind", "Normalized response kind such as turbo-stream, html, json, unknown, or network.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, allowedValues: RazorWireResponseKinds),
                Property("failure_ui", "Failure UX outcome such as generated, handled, suppressed, or disabled.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, allowedValues: RazorWireFailureUi)
            ],
            ["form field values", "request body", "anti-forgery token", "stack trace"]),
        new(
            RazorWireFormFailureRecovered,
            AppSurfaceProductEventLifecycle.Experimental,
            "Measure whether generated RazorWire failure UX leads to a later successful submit.",
            "ForgeTrust.RazorWire",
            "Short framework-quality retention; aggregate by recovery action.",
            [
                Property("recovery_action", "Normalized recovery action such as retry_submit or next_success.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: RazorWireRecoveryActions),
                Property("attempt_count", "Small count of attempts observed by the runtime.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low)
            ],
            ["form field values", "request body", "anti-forgery token", "stack trace"]),
        new(
            RazorWireStreamAdmissionRejected,
            AppSurfaceProductEventLifecycle.Experimental,
            "Understand stream pressure and channel validation failures without exposing channel names.",
            "ForgeTrust.RazorWire",
            "Short framework-quality retention; aggregate by rejection reason and limit name.",
            [
                Property("rejection_reason", "RazorWire stream admission rejection reason.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: RazorWireAdmissionReasons),
                Property("limit_name", "Normalized limit that caused the rejection.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, required: true, allowedValues: RazorWireAdmissionLimits),
                Property("current_count", "Current count or input length that tripped the limit.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Medium),
                Property("authorization_mode", "Configured stream authorization mode.", AppSurfaceProductEventSensitivity.Operational, AppSurfaceProductEventCardinality.Low, allowedValues: RazorWireAuthorizationModes)
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
    public static IReadOnlyList<AppSurfaceProductEventContract> All { get; } = Contracts;

    /// <summary>
    /// Gets globally forbidden property names that are dropped unless the registry explicitly classifies them.
    /// </summary>
    public static IReadOnlySet<string> ForbiddenProperties { get; } = ForbiddenPropertyNames;

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

        if (!ContractsByName.TryGetValue(productEvent.Name, out var contract))
        {
            return new AppSurfaceProductEventValidationResult(
                null,
                isValid: false,
                sanitizedProperties: new Dictionary<string, string>(),
                rejectedProperties: [],
                diagnostics: ["Event name is not registered in the AppSurface product-intelligence registry."]);
        }

        var allowedProperties = contract.Properties.ToDictionary(property => property.Name, StringComparer.Ordinal);
        var sanitized = new Dictionary<string, string>(StringComparer.Ordinal);
        var rejected = new List<string>();
        var diagnostics = new List<string>();

        foreach (var (key, value) in productEvent.Properties)
        {
            if (!allowedProperties.TryGetValue(key, out var property))
            {
                rejected.Add(key);
                diagnostics.Add($"Property '{key}' is not registered for event '{contract.Name}'.");
                continue;
            }

            if (ForbiddenPropertyNames.Contains(key))
            {
                rejected.Add(key);
                diagnostics.Add($"Property '{key}' uses a globally forbidden property name.");
                continue;
            }

            if (!TrySanitizePropertyValue(property, value, out var sanitizedValue, out var diagnostic))
            {
                rejected.Add(key);
                diagnostics.Add(diagnostic);
                continue;
            }

            sanitized[key] = sanitizedValue;
        }

        var missingRequired = contract.Properties
            .Where(property => property.Required && !sanitized.ContainsKey(property.Name))
            .Select(property => property.Name)
            .ToArray();

        foreach (var property in missingRequired)
        {
            diagnostics.Add($"Required property '{property}' is missing for event '{contract.Name}'.");
        }

        return new AppSurfaceProductEventValidationResult(
            contract,
            isValid: missingRequired.Length == 0,
            sanitizedProperties: sanitized,
            rejectedProperties: rejected,
            diagnostics: diagnostics);
    }

    private static AppSurfaceProductEventPropertyContract Property(
        string name,
        string description,
        AppSurfaceProductEventSensitivity sensitivity,
        AppSurfaceProductEventCardinality cardinality,
        bool required = false,
        IEnumerable<string>? allowedValues = null,
        int maxLength = 64)
    {
        return new AppSurfaceProductEventPropertyContract(
            name,
            description,
            sensitivity,
            cardinality,
            required,
            allowedValues,
            maxLength);
    }

    private static bool TrySanitizePropertyValue(
        AppSurfaceProductEventPropertyContract property,
        string rawValue,
        out string sanitizedValue,
        out string diagnostic)
    {
        sanitizedValue = string.Empty;
        diagnostic = string.Empty;
        var value = AppSurfaceProductEventMetadata.NormalizeOptionalText(rawValue);
        if (value is null)
        {
            diagnostic = $"Property '{property.Name}' has an empty value.";
            return false;
        }

        if (AppSurfaceProductEventMetadata.ContainsForbiddenValueShape(value))
        {
            diagnostic = $"Property '{property.Name}' contains a forbidden value shape.";
            return false;
        }

        if (IsNonNegativeIntegerProperty(property.Name))
        {
            if (!int.TryParse(value, out var parsed) || parsed < 0)
            {
                diagnostic = $"Property '{property.Name}' must be a non-negative integer.";
                return false;
            }

            sanitizedValue = parsed.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        if (value.Length > property.MaxLength)
        {
            diagnostic = $"Property '{property.Name}' exceeds the maximum allowed value length.";
            return false;
        }

        if (property.AllowedValues.Count > 0
            && !property.AllowedValues.Contains(value, StringComparer.Ordinal))
        {
            diagnostic = $"Property '{property.Name}' value is not in the registered allowed-value set.";
            return false;
        }

        if (property.Cardinality == AppSurfaceProductEventCardinality.Low && !IsSafeTokenValue(value))
        {
            diagnostic = $"Property '{property.Name}' must be a bounded token value.";
            return false;
        }

        sanitizedValue = value;
        return true;
    }

    private static bool IsNonNegativeIntegerProperty(string propertyName)
    {
        return propertyName is "active_filter_count"
            or "attempt_count"
            or "current_count"
            or "http_status"
            or "query_length"
            or "result_count"
            or "result_rank";
    }

    private static bool IsSafeTokenValue(string value)
    {
        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.' or ':')
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
