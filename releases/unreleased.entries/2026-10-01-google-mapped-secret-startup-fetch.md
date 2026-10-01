<!-- appsurface:unreleased-entry section="included" -->

### Inline mapped Google secret startup reads

- [Google Secret Manager configuration](../../Config/ForgeTrust.AppSurface.Config.GoogleSecretManager/README.md#mapped-lookup-scheduling) now performs a cold, no-token mapped secret fetch on the first caller instead of scheduling that synchronous client call on a worker. Concurrent reads of the same exact resource still share one fetch; audited or cancellable reads retain their bounded worker path and per-caller cancellation. Apps using mapped Google secrets need no code change. The package guide explains how a slow synchronous client call can still hold the first caller until the configured lookup timeout.
