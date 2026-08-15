<!-- appsurface:unreleased-entry section="included" -->
### Release-note link preservation

- [Release preparation](../../tools/ForgeTrust.AppSurface.Release/README.md#append-only-unreleased-entries) now rebases relative inline and reference Markdown link destinations from append-only entry files to the composed Unreleased and versioned release notes. Source-relative documentation links therefore remain valid in the generated release archive, while external, rooted, query-only, fragment-only, and code-example content remains unchanged.
