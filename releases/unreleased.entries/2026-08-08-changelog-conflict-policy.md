<!-- appsurface:unreleased-entry section="included" -->
### Release-note workflow

- Public release-note changes now use independently named append-only entries under `releases/unreleased.entries/`. AppSurface Docs and [release preparation](../tools/ForgeTrust.AppSurface.Release/README.md#append-only-unreleased-entries) assemble them at the bottom of their declared section, and preparation archives only the entries it consumed. This avoids silently merging a release reset with concurrent feature notes.
