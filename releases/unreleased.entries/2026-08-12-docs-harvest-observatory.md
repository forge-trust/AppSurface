<!-- appsurface:unreleased-entry section="included" -->
### AppSurface Docs harvest observability

- [`ForgeTrust.AppSurface.Docs`](../../Web/ForgeTrust.AppSurface.Docs/README.md#live-harvest-observatory) now gives authorized operators a fine-grained, redacted live view of package-owned Markdown, C#, and JavaScript harvesters. The `_harvest` surface shows parser phase, source units inspected, documents produced, and a rolling built-in documents-per-second rate without exposing source identities; custom `IDocHarvester` implementations remain status-only and need no migration or configuration.
