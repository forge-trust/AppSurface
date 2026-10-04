<!-- appsurface:unreleased-entry section="included" -->

- The [native Tailwind evidence workflow](../../docs/tailwind-artifact-provenance.md#shared-bundle-and-producer-identity) now reads the original unapproved package inventory used by both protected release publishers. Manual Package Artifacts rehearsal keeps its existing inventory filename. This fixes native proof failures before NuGet publication while retaining the separate PostgreSQL candidate approval gate.
