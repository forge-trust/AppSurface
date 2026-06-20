# ForgeTrust.AppSurface.Observability

`ForgeTrust.AppSurface.Observability` registers application-side OpenTelemetry logging, tracing, and metrics for AppSurface apps. Use it when an app should send operational telemetry to Aspire or another OTLP collector without making the app depend on `ForgeTrust.AppSurface.Aspire`.

It does not create Aspire resources, own dashboards, capture request bodies, define product analytics, add package-specific Flow/Auth/Docs spans, or choose non-OTLP exporters.

## Installation

```bash
dotnet add package ForgeTrust.AppSurface.Observability
```

## Module Activation

Register the module from your root AppSurface module:

```csharp
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Observability;

public sealed class MyAppModule : IAppSurfaceHostModule
{
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        builder.AddModule<AppSurfaceObservabilityModule>();
    }

    // Other IAppSurfaceHostModule members omitted.
}
```

Aspire AppHosts usually provide `OTEL_EXPORTER_OTLP_ENDPOINT` to project resources. With the default `WhenEndpointConfigured` mode, AppSurface registers logs, traces, and metrics immediately, but adds OTLP exporters only when that endpoint or an AppSurface-specific endpoint exists.

## Direct Host Registration

Use the host-builder extension when a custom host bypasses module discovery:

```csharp
builder.ConfigureAppSurfaceObservability(
    startupContext,
    options =>
    {
        options.ServiceName = "orders-api";
    });
```

Pass code configuration on the first AppSurface observability provider-registration path. Repeated provider-registration calls are safe and first registration wins, so options do not drift from the OpenTelemetry resource and exporter setup captured during registration. The options-only `services.ConfigureAppSurfaceObservability(...)` helper is for consumers that read `IOptions<AppSurfaceObservabilityOptions>` directly; values that should affect AppSurface-owned OpenTelemetry setup belong on the first provider-registration call.

## Configuration

| Key | Default | Behavior |
| --- | --- | --- |
| `AppSurfaceObservability:ExporterMode` | `WhenEndpointConfigured` | `WhenEndpointConfigured` exports only when AppSurface or OTLP endpoint config exists. `Always` registers exporters even without an endpoint. `Never` disables AppSurface-owned exporters. |
| `AppSurfaceObservability:OtlpEndpoint` | unset | AppSurface-owned OTLP endpoint. Environment variable form: `AppSurfaceObservability__OtlpEndpoint`. Wins over `OTEL_EXPORTER_OTLP_ENDPOINT`. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | unset | Standard OpenTelemetry endpoint used when no AppSurface endpoint is configured. Aspire commonly provides this for app resources. |
| `AppSurfaceObservability:ServiceName` | `StartupContext.ApplicationName` | OpenTelemetry resource `service.name`. Falls back to the root module assembly name through `StartupContext.ApplicationName`. |
| `AppSurfaceObservability:ServiceVersion` | unset | Optional OpenTelemetry resource `service.version`. Emitted only when configured. |

Example non-Aspire configuration:

```json
{
  "AppSurfaceObservability": {
    "OtlpEndpoint": "http://localhost:4317",
    "ServiceName": "orders-api",
    "ServiceVersion": "2026.6.19"
  }
}
```

Example test or host-owned exporter configuration:

```json
{
  "AppSurfaceObservability": {
    "ExporterMode": "Never"
  }
}
```

## First Aspire Success Path

1. Add `ForgeTrust.AppSurface.Observability` to the app project.
2. Add `AppSurfaceObservabilityModule` to the app module dependency graph.
3. Run the app through an Aspire AppHost.
4. Hit an endpoint so the app emits logs and runtime activity.
5. In the Aspire dashboard, check the app resource for logs, traces, and metrics under the configured service name.

The repository's [`examples/aspire-apphost`](../../examples/aspire-apphost/README.md) launches `examples/web-app`, which registers this module as the local proof path.

## Diagnostics And Pitfalls

- With `WhenEndpointConfigured` and no endpoint, AppSurface emits one startup log explaining that OTLP export was skipped and naming the config/env keys to set.
- `StartupContext.ApplicationName` is a display label used for OpenTelemetry service identity. It is intentionally separate from the Generic Host application name used for static web assets.
- `Always` may use OpenTelemetry exporter defaults when no endpoint is configured; prefer `WhenEndpointConfigured` for local safety.
- This package registers AppSurface-owned OpenTelemetry providers and exporters once. Later AppSurface provider-registration calls are ignored; they do not replace captured service identity, exporter mode, or endpoint settings.
- It does not remove or replace host-owned OpenTelemetry configuration.
- Product-intelligence events belong in `ForgeTrust.AppSurface.Intelligence`; operational logs, traces, and metrics belong here.

---
[📂 Back to Observability List](../README.md) | [🏠 Back to Root](../../README.md)
