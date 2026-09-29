<!-- appsurface:unreleased-entry section="included" -->
### Native Tailwind release proof

- The protected prerelease workflow now runs the packed Tailwind consumer's restore and build proof on each supported native host before NuGet publication. This repairs the [five-host provenance gate](../../docs/tailwind-artifact-provenance.md) for the carried preview candidate; package APIs are unchanged.
