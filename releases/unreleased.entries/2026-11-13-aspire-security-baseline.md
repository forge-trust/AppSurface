<!-- appsurface:unreleased-entry section="included" -->
- **Update the coordinated Aspire baseline to 13.6.5** to address [GHSA-f7vh-h5g3-7jrh](https://github.com/advisories/GHSA-f7vh-h5g3-7jrh). The [Aspire integration](../../Aspire/ForgeTrust.AppSurface.Aspire/README.md#dependency-security), AppHost examples, package minimums and strict consumer fixtures use the patched graph. AppHost examples select the SDK-pinned CLI bundle; NuGet auditing, failed-build cleanup assertions, required checks and coverage gates remain enabled. Both minimum and explicit-selection consumer journeys verify 13.6.5; later-runtime compatibility remains unverified.

The [CLI bundle trust boundary](../../Aspire/ForgeTrust.AppSurface.Aspire/README.md#apphost-project-references)
is separate from the application NuGet graph: DCP and Dashboard are version-pinned through the SDK's
CLI selection, rather than hash-pinned and audited as RID-specific application NuGet packages.

The [Durable worker template](../../Durable/ForgeTrust.AppSurface.Durable.Templates/README.md) also coordinates its OpenTelemetry pins with the upgraded package graph, preserving strict restore of generated consumers.

The [historical compatibility consumers](../../Durable/compatibility/README.md) retain their exact published dependency graph through local central pins, so current dependency upgrades do not change the old-version proof. Their locked restore, audit and compatibility assertions remain enabled.

Full-solution CI workers prepare the SDK-paired CLI bundle before parallel builds, with visible setup diagnostics and unchanged build, formatting, native-readiness and coverage requirements.
