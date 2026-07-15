# Aspire Projects

Local AppHost composition and native deployment-pipeline integration for .NET Aspire applications.

## Contents

- [**ForgeTrust.AppSurface.Aspire**](./ForgeTrust.AppSurface.Aspire/README.md) – Core Aspire integration, including explicit deployment annotations and native publish/verify pipeline steps.
- `ForgeTrust.AppSurface.Aspire.Tests` – Tests for Aspire components.
- [Aspire AppHost example](../examples/aspire-apphost/README.md) – Working local AppHost proof using AppSurface profiles and components.

For application-side logs, traces, and metrics that flow to the Aspire dashboard, use [ForgeTrust.AppSurface.Observability](../Observability/ForgeTrust.AppSurface.Observability/README.md) in the app project. The Aspire package stays focused on AppHost composition and resource graph modeling.

For deployment artifacts, pair this package with a provider such as [`ForgeTrust.AppSurface.Deployment.GcpCloudRun`](../Deployment/ForgeTrust.AppSurface.Deployment.GcpCloudRun/README.md). Aspire owns AppHost evaluation, environment selection, and pipeline ordering; AppSurface owns explicit portable intent, provider compilation, deterministic evidence, and read-only parity verification. Apply, execution, promotion, and rollback remain in the consuming release workflow.

---
[🏠 Back to Root](../README.md)
