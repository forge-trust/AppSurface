# ForgeTrust.AppSurface.Intelligence

`ForgeTrust.AppSurface.Intelligence` defines AppSurface-owned product-intelligence contracts. It is not an analytics vendor package and it does not configure transport, storage, dashboards, retention, access control, browser autocapture, session replay, or OpenTelemetry exporters.

Use it when you want AppSurface semantic events to be validated before a host forwards them to PostHog, a warehouse, an internal analytics service, or another product analytics system.

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
        // The event envelope and properties have already been filtered through AppSurfaceProductEventRegistry.
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

## Event Registry

`AppSurfaceProductEventRegistry` is the source of truth for product-intelligence contracts. Each contract declares:

- event name
- lifecycle state: `Experimental`, `Recommended`, `Stable`, or `Deprecated`
- purpose
- owner
- allowed property schema
- sensitivity class
- cardinality budget
- retention expectation
- forbidden examples

The first dogfood events are all `Experimental`:

- `docs.search.submitted`
- `docs.search.returned_zero_results`
- `docs.search.result_selected`
- `docs.recovery_link.selected`
- `razorwire.form.failed`
- `razorwire.form.failure_recovered`
- `razorwire.stream.admission_rejected`

Treat event names like browser event names or data attributes: once promoted beyond experimental, they become public contracts.

## Privacy Defaults

The dispatcher validates against the registry before invoking host sinks. Unregistered properties are dropped. Globally risky property names are always blocked in this package version, even if a property schema accidentally registers one of those names. Use `AppSurfaceProductEventRegistry.ForbiddenProperties` as the source of truth; it includes names such as `token`, `cookie`, `secret`, `password`, `body`, `config`, `connection_string`, `exception`, `raw_query`, `request_body`, `stack_trace`, and raw `query`.

Registered property values are also normalized before emission. Low-cardinality dimensions use bounded token values or explicit allowed-value sets, numeric properties must parse as non-negative integers, and values that look like secrets, bearer headers, cookies, connection strings, or stack traces are rejected before sinks run.

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

The browser bridge must not include raw search text, form field values, request bodies, tokens, cookies, stack traces, or full URLs with query strings. Treat it as an experimental transport bridge for dogfooding, not as a replacement for backend validation when events cross into a trusted host transport.

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

OpenTelemetry remains the operational telemetry path for logs, metrics, and traces. Use `Meter` and `ActivitySource` for performance, health, and debugging. Product-intelligence events may carry correlation IDs so a host can join analytics back to traces, but AppSurface does not force product analytics through OTel.

## Release Guidance

`ForgeTrust.AppSurface.Intelligence` follows the AppSurface public-preview compatibility policy. The current package family release guidance is tracked in the [v0.1.0 RC 2 release note](../../releases/v0.1.0-rc.2.md), while the event contracts in this package remain `Experimental` until dogfood usage promotes them.
