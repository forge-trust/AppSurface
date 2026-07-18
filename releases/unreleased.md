# Unreleased

This is the living release note for the fix-forward AppSurface preview after `0.2.0-preview.3`. The preview.3 tag stopped before NuGet publication, so this version carries its publishable package surface forward without moving the existing tag.

## What is taking shape

- The coordinated package surface described in [`0.2.0-preview.3`](./v0.2.0-preview.3.md) is being republished under a new immutable preview tag because the preview.3 publisher correctly stopped before its first NuGet push.
- [`ForgeTrust.AppSurface.Aspire.Testing`](../Aspire/ForgeTrust.AppSurface.Aspire.Testing/README.md) remains a source-preview package and is not published. Pinned Aspire 13.4.4 can leak partial host state when host construction fails after creating its service provider, so the package stays held behind its readiness blocker.

## Included in the next coordinated version

### Release and docs surface

- Public packages marked `publish` receive the same adoption surface and migration guidance recorded in the [`0.2.0-preview.3` release note](./v0.2.0-preview.3.md); no preview.3 package reached NuGet before this fix-forward release.
- The [release cockpit](../tools/ForgeTrust.AppSurface.Release/README.md#check) now rejects a public package that combines `publish_decision: publish` with a `readiness_blocker`, catching the inconsistent package policy before a release tag can be created. Held packages must use `publish_decision: do_not_publish` with a documented reason.

## Migration watch

- Follow the package and host migration guidance in the [`0.2.0-preview.3` release note](./v0.2.0-preview.3.md); this fix-forward preview does not add another runtime behavior change.
- Do not add `ForgeTrust.AppSurface.Aspire.Testing` from NuGet yet. Continue using its source-level proof only inside this repository until its readiness blocker is resolved and a later coordinated release explicitly publishes it.
