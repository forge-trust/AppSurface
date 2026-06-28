# ForgeTrust.AppSurface.Intelligence

`ForgeTrust.AppSurface.Intelligence` defines privacy-checked product-intelligence contracts for AppSurface and host/package contract packs. It is not an analytics vendor package and it does not configure transport, storage, dashboards, retention, access control, browser autocapture, session replay, or OpenTelemetry exporters.

Use it when you want semantic product events to be validated before a host forwards them to PostHog, a warehouse, an internal analytics service, or another product analytics system.

## Quickstart

```csharp
using ForgeTrust.AppSurface.Intelligence;

builder.Services.AddAppSurfaceProductIntelligence(options =>
{
    options.EnableExperimentalEvents();
});

builder.Services.AddScoped<IAppSurfaceProductIntelligenceSink, MyProductAnalyticsSink>();
```

```csharp
using ForgeTrust.AppSurface.Intelligence;

public sealed class MyProductAnalyticsSink : IAppSurfaceProductIntelligenceSink
{
    public ValueTask CaptureAsync(
        AppSurfaceProductEvent productEvent,
        CancellationToken cancellationToken = default)
    {
        // Forward productEvent.Name and productEvent.Properties to your host-owned analytics transport.
        // The event envelope and properties have already been filtered through the composed registry.
        return ValueTask.CompletedTask;
    }
}
```

Capture only registered events:

```csharp
await intelligence.CaptureAsync(
    new AppSurfaceProductEvent(
        AppSurfaceProductEventRegistry.RazorWireStreamAdmissionRejected,
        DateTimeOffset.UtcNow,
        new Dictionary<string, string>
        {
            ["rejection_reason"] = "TooManyLiveSubscriptions",
            ["limit_name"] = "max_live_subscriptions",
            ["current_count"] = "32"
        },
        correlationId: Activity.Current?.Id,
        route: "/_rw/streams/{channel}"),
    cancellationToken);
```

Experimental events are disabled by default. Without `EnableExperimentalEvents()`, capture is a safe no-op even when a sink is registered.

Register sinks with the lifetime their transport needs. The default dispatcher is scoped so host-owned sinks may depend on scoped queues, tenant context, or request-aware services without being resolved from the root service provider.

## Custom Event Contracts In 5 Minutes

Use `RegisterEventContracts` when a host app or package owns product events that should use the same privacy boundary as AppSurface built-ins.

```csharp
using ForgeTrust.AppSurface.Intelligence;

public static class SkoolieLaunchIntelligenceContracts
{
    public const string CardGenerated = "skoolie.card.generated";

    public static IReadOnlyList<AppSurfaceProductEventContract> All { get; } =
    [
        new(
            CardGenerated,
            AppSurfaceProductEventLifecycle.Experimental,
            "Measure whether launch-card generation moves safely into send review.",
            "Skoolie",
            "Short launch-quality retention; aggregate before long-term storage.",
            [
                new AppSurfaceProductEventPropertyContract(
                    "launch_surface",
                    "Host-owned surface that generated the card.",
                    AppSurfaceProductEventSensitivity.Operational,
                    AppSurfaceProductEventCardinality.Low,
                    required: true,
                    valueShape: AppSurfaceProductEventValueShape.Token),
                new AppSurfaceProductEventPropertyContract(
                    "attachment_count",
                    "Number of safe launch artifacts attached to the generated card.",
                    AppSurfaceProductEventSensitivity.Operational,
                    AppSurfaceProductEventCardinality.Medium,
                    valueShape: AppSurfaceProductEventValueShape.NonNegativeInteger),
                new AppSurfaceProductEventPropertyContract(
                    "delivery_state",
                    "Normalized downstream state.",
                    AppSurfaceProductEventSensitivity.Operational,
                    AppSurfaceProductEventCardinality.Low,
                    required: true,
                    allowedValues: ["queued", "sent"],
                    valueShape: AppSurfaceProductEventValueShape.AllowedValue)
            ],
            ["child identity", "email body", "raw attachment"])
    ];
}
```

Register the contract pack, allowlist the experimental event, and add a sink:

```csharp
builder.Services.AddAppSurfaceProductIntelligence(options =>
{
    options.RegisterEventContracts(SkoolieLaunchIntelligenceContracts.All);
    options.EnableExperimentalEvents(SkoolieLaunchIntelligenceContracts.CardGenerated);

    // Useful in development and tests; production capture remains best-effort by default.
    options.ThrowOnInvalidEvents();
});

builder.Services.AddSingleton<RecordingProductIntelligenceSink>();
builder.Services.AddSingleton<IAppSurfaceProductIntelligenceSink>(sp =>
    sp.GetRequiredService<RecordingProductIntelligenceSink>());
```

Capture the event through the normal dispatcher:

```csharp
await intelligence.CaptureAsync(
    new AppSurfaceProductEvent(
        SkoolieLaunchIntelligenceContracts.CardGenerated,
        DateTimeOffset.UtcNow,
        new Dictionary<string, string>
        {
            ["launch_surface"] = "dashboard",
            ["attachment_count"] = "3",
            ["delivery_state"] = "queued"
        }),
    cancellationToken);
```

A minimal recording sink can prove the event arrived after validation:

```csharp
public sealed class RecordingProductIntelligenceSink : IAppSurfaceProductIntelligenceSink
{
    public List<AppSurfaceProductEvent> Events { get; } = [];

    public ValueTask CaptureAsync(
        AppSurfaceProductEvent productEvent,
        CancellationToken cancellationToken = default)
    {
        Events.Add(productEvent);
        return ValueTask.CompletedTask;
    }
}

Assert.Contains(
    recordingSink.Events,
    captured => captured.Name == SkoolieLaunchIntelligenceContracts.CardGenerated
        && captured.Properties["attachment_count"] == "3");
```

If an event is not registered, `ThrowOnInvalidEvents()` raises a safe `AppSurfaceProductEventValidationException` with the event name, stable reason codes, rejected property names, and a fix hint such as calling `RegisterEventContracts(...)`. Exception messages, validation results, and dispatcher diagnostics must not include raw property values.

## Event Registry

`AppSurfaceProductEventRegistry` is the built-in AppSurface contract catalog and compatibility facade. The runtime dispatcher uses the DI-composed `IAppSurfaceProductEventRegistry`, which combines built-in AppSurface contracts with host/package contracts registered through `AppSurfaceProductIntelligenceOptions.RegisterEventContracts(...)`. Each contract declares:

- event name
- lifecycle state: `Experimental`, `Recommended`, `Stable`, or `Deprecated`
- purpose
- owner
- allowed property schema
- sensitivity class
- cardinality budget
- value shape: `Token`, `BoundedText`, `NonNegativeInteger`, `Boolean`, or `AllowedValue`
- retention expectation
- forbidden examples

The first dogfood events are all `Experimental`:

- `docs.search.submitted`
- `docs.search.returned_zero_results`
- `docs.search.result_selected`
- `docs.recovery_link.selected`
- `docs.search.filter_changed`
- `docs.search.friction_feedback_submitted`
- `razorwire.form.failed`
- `razorwire.form.failure_recovered`
- `razorwire.stream.admission_rejected`

Treat event names like browser event names or data attributes: once promoted beyond experimental, they become public contracts.

Hosts can enable every experimental contract with `EnableExperimentalEvents()`, or allow only selected experimental event
names:

```csharp
builder.Services.AddAppSurfaceProductIntelligence(options =>
{
    options.EnableExperimentalEvents(
        AppSurfaceProductEventRegistry.DocsSearchSubmitted,
        AppSurfaceProductEventRegistry.DocsSearchReturnedZeroResults,
        AppSurfaceProductEventRegistry.DocsSearchResultSelected,
        AppSurfaceProductEventRegistry.DocsRecoveryLinkSelected,
        AppSurfaceProductEventRegistry.DocsSearchFilterChanged,
        AppSurfaceProductEventRegistry.DocsSearchFrictionFeedbackSubmitted);
});
```

Use the selected-event allowlist when a package integration, such as AppSurface Docs search-quality metrics, should emit
only its own experimental product area without enabling unrelated dogfood events.

Host/package contracts follow the same lifecycle rule: `Experimental` custom contracts require `EnableExperimentalEvents(...)`; `Recommended`, `Stable`, and `Deprecated` custom contracts are eligible for capture by registration. Deprecated contracts remain valid for compatibility, but new instrumentation should move to a non-deprecated contract.

Identical semantic duplicate registrations are idempotent so repeated module registration is safe. Incompatible duplicate event names fail registry construction with a safe `AppSurfaceProductEventContractRegistrationException` that names the event and owners without referring to payload values.

## Privacy Defaults

The dispatcher validates against the composed registry before invoking host sinks. Unregistered properties are dropped. Globally risky property names are always blocked in this package version, even if a property schema accidentally registers one of those names. Use `IAppSurfaceProductEventRegistry.ForbiddenProperties` or `AppSurfaceProductEventRegistry.ForbiddenProperties` as the source of truth; it includes names such as `token`, `cookie`, `secret`, `password`, `body`, `config`, `connection_string`, `exception`, `raw_query`, `request_body`, `stack_trace`, and raw `query`.

Registered property values are also normalized before emission according to each property's `AppSurfaceProductEventValueShape`. Token properties use bounded ASCII token values, bounded text properties keep short non-secret text, numeric properties must parse as non-negative integers, boolean properties normalize to `true` or `false`, and allowed-value properties must match their registered set. Values that look like secrets, bearer headers, cookies, connection strings, or stack traces are rejected before sinks run.

Custom contract packs cannot register `Sensitive` or `High` properties by default. They also cannot bypass forbidden property names, forbidden value-shape filtering, lifecycle gating, or safe diagnostics through options. Supported escape hatches are custom sinks, replacing `IAppSurfaceProductEventRegistry` with an implementation that preserves the privacy boundary, direct `Validate(...)` calls in tests, and `ThrowOnInvalidEvents()` for development feedback.

Envelope fields are filtered too. `ActorId`, `SessionId`, and `CorrelationId` must be short bounded tokens; values with whitespace, PII-shaped characters, bearer headers, cookies, secrets, or other forbidden shapes are dropped. `Route` must be a short route template or surface name; full URLs, query strings, fragments, and unsafe characters are dropped before sinks run.

Do not capture:

- raw search text
- request bodies
- form field values
- tokens, cookies, passwords, secrets, or connection strings
- exception stack traces
- full config values
- high-cardinality route values such as channel names, object IDs, or email addresses

Safe diagnostics describe rejected property names without echoing rejected values.

## Browser Event Bridge

AppSurface Docs and RazorWire dogfood the same registry through a browser `CustomEvent` bridge when experimental product intelligence is enabled. Hosts can listen for the event and forward it to a product analytics SDK:

```javascript
document.addEventListener('appsurface:product-intelligence:event', (event) => {
  const { name, properties } = event.detail;
  // Forward name and properties to a host-owned analytics SDK.
});
```

The browser event detail has this shape:

- `detail.name`: one registered event name from `AppSurfaceProductEventRegistry`
- `detail.properties`: string-valued properties that mirror the registry schema

The browser bridge must not include raw search text, form field values, request bodies, tokens, cookies, stack traces, or full URLs with query strings. Treat it as an experimental transport bridge for dogfooding, not as a replacement for backend validation when events cross into a trusted host transport. Custom contract schema export to browser clients is not part of this package version; server-side validation remains authoritative.

## PostHog Recipe

PostHog is the recommended first product analytics UI for dogfooding funnels, cohorts, search quality, feature impact, and session replay links. This package intentionally does not reference PostHog.

Map AppSurface events to PostHog custom events in a host sink:

- `productEvent.Name` -> PostHog event name
- `productEvent.ActorId` -> `distinct_id`
- `productEvent.Properties` -> PostHog properties
- `productEvent.CorrelationId` -> a property such as `correlation_id`
- host tenant/team/workspace -> PostHog groups, if you have reviewed pricing and retention

Dashboard/query recipes:

- Docs search quality: chart `docs.search.submitted` by `result_count`, then inspect the ratio of `docs.search.returned_zero_results`.
- Result usefulness: funnel `docs.search.submitted` -> `docs.search.result_selected`, grouped by `surface` and `result_kind`.
- Recovery usefulness: chart `docs.recovery_link.selected` by `source_state` and `link_kind`.
- Form recovery: funnel `razorwire.form.failed` -> `razorwire.form.failure_recovered`, grouped by `failure_mode`.
- Stream pressure: chart `razorwire.stream.admission_rejected` by `rejection_reason` and `limit_name`.

PostHog identity sharp edges:

- Backend and frontend capture must use the same `distinct_id` strategy, or funnels split into separate users.
- Feature flag attribution requires the evaluated flag snapshot or equivalent host-side metadata.
- Group analytics can change pricing and retention expectations.
- Cloud and self-hosted PostHog use different host/API-key configuration and operational responsibilities.

## Product Intelligence vs PostHog vs OpenTelemetry

Product intelligence answers product questions: Which docs paths fail readers? Which framework recovery paths work? Which limits produce customer friction?

PostHog is one possible product analytics UI and transport. Use it after the AppSurface event contract is clear.

OpenTelemetry remains the operational telemetry path for logs, metrics, and traces. Use `ForgeTrust.AppSurface.Observability` to register AppSurface's OpenTelemetry logging, tracing, metrics, and OTLP exporter defaults for Aspire or another collector. Product-intelligence events may carry correlation IDs so a host can join analytics back to traces, but AppSurface does not force product analytics through OTel.

Use AppSurface.Intelligence when you want reusable semantic contracts, lifecycle metadata, privacy-shape validation, safe diagnostics, and freedom to forward to any product analytics backend. Use a local validator when a host app is iterating before the package surface is available or before a contract pack is ready to publish. Use PostHog directly only when you intentionally accept a vendor-specific event shape and a separate privacy-review process.

## Release Guidance

`ForgeTrust.AppSurface.Intelligence` follows the AppSurface public-preview compatibility policy. The current package family release guidance is tracked in the [package chooser](../../packages/README.md) and [release hub](../../releases/README.md), while the event contracts in this package remain `Experimental` until dogfood usage promotes them.
