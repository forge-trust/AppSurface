<!-- appsurface:unreleased-entry section="included" -->
### Prerelease publication retries

- A protected prerelease publish can retry after required source checks skip its producer and publisher before either job starts. The retry still requires [tag-bound artifact provenance](../../docs/tailwind-artifact-provenance.md) before packages are published; package APIs are unchanged.
