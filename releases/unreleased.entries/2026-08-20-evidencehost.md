<!-- appsurface:unreleased-entry section="included" -->

### Package and docs surface

- AppSurface now includes [EvidenceHost](../../start-here/evidencehost.md): contract-first, explicit-diff policy planning for CI evidence. The public [`appsurface evidence`](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-evidence) command family creates a safe starter, diagnoses consumer prerequisites, explains selected obligations before execution, emits canonical plan/manifest/summary artifacts, and verifies their digest binding. Explicit `no-evidence` profiles can close only reviewed low-risk rules; skipped or filtered tests, unavailable capabilities, failed producers, and incomplete profiles never become a gate-eligible claim.
- [`ForgeTrust.AppSurface.Evidence.Aspire`](../../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md) provides a separate explicit lifecycle for consumer-owned Aspire readiness and E2E producers. It requires direct registrations, enforces deadlines and cleanup, keeps test composition out of normal application startup, and labels accepted v1 release envelopes as validated but not independently attested.
