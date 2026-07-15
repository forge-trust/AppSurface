# ForgeTrust.AppSurface.Deployment

Provider-neutral deployment contracts for AppSurface. The package describes explicit run-to-completion migration jobs, validates immutable image and source evidence, emits canonical JSON, and defines render and verification seams without depending on Aspire, a cloud SDK, Terraform, or a secret provider.

## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package from a prerelease feed, check the [package chooser](../../packages/README.md) and [release hub](../../releases/README.md) for current release risk, migration guidance, and readiness.

Most AppHost consumers install `ForgeTrust.AppSurface.Aspire` and a provider package instead of installing this package directly. Install it directly when authoring a provider or inspecting portable intent.

Publishing through these contracts is artifact generation only. It must not resolve secret values, call a cloud API, apply infrastructure, execute a job, or change traffic.

See the [deployment overview](../README.md) for ownership and adoption guidance.
