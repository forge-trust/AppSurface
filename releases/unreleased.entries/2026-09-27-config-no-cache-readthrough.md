<!-- appsurface:unreleased-entry section="included" -->

### Google Secret Manager uncached reads

- [Google Secret Manager configuration](../../Config/ForgeTrust.AppSurface.Config.GoogleSecretManager/README.md#cache-behavior) now starts a fresh client read after a previous uncached lookup completes. Hosts using `CacheTtl = null` can observe the next secret version on the next sequential lookup; overlapping lookups may still share one in-flight request.
