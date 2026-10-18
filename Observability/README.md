# Observability

Application-side logging, tracing, and metrics for AppSurface apps.

## Contents

- [**ForgeTrust.AppSurface.Observability**](./ForgeTrust.AppSurface.Observability/README.md) – OpenTelemetry registration for AppSurface hosts.
- `ForgeTrust.AppSurface.Observability.Tests` – Tests for exporter mode, endpoint, service identity, and registration behavior.

Durable external activation emits a bounded activity through the canonical AppSurface source. The
[activation telemetry contract](../Durable/external-activation-v1.md#telemetry-contract-v1) is authoritative for its
operation, tag allowlist, omission rules, and the copyable
[listener/sampler/test-exporter/`ForceFlush` proof](../Durable/external-activation-v1.md#activation-activity-export-proof).
An emitted or listener-observed activity does not establish exporter delivery.

---
[🏠 Back to Root](../README.md)
