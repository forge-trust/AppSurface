<!-- appsurface:unreleased-entry section="included" -->
### Retargetable published documentation aliases

`ForgeTrust.AppSurface.Docs` can expose maintainer-configured names such as `stable`, `preview`, or `v1` for verified
exact documentation releases. Alias pages keep alias-local navigation and search while publishing canonical metadata
for the exact target; the existing recommended release and immutable exact-version routes remain independent. The
[package reference](../../Web/ForgeTrust.AppSurface.Docs/README.md#version-aliases) documents configuration, named-host
middleware order, cache and archive ownership, numeric diagnostics, and rollout/rollback. The [consumer walkthrough](../../Web/ForgeTrust.AppSurface.Docs/use-appsurface-docs.md#configure-and-verify-moving-version-aliases)
uses the pinned stable/prerelease fixture and separates verified archive ingestion from alias acceptance evidence.
