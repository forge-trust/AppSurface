<!-- appsurface:unreleased-entry section="included" -->

### Conflict-free release-note composition

- [`appsurface release compose`](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-release-compose) lets any consumer project keep concurrent change descriptions in isolated, filename-sorted Markdown entries and preview or explicitly write one deterministic release note. It validates template-owned sections and bounded paths, never overwrites the template or source entries, and leaves changelog rollover, tags, package publication, and AppSurface's repository-owned [`./eng/release`](../../tools/ForgeTrust.AppSurface.Release/README.md) cockpit outside the public command.
