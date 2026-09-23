<!-- appsurface:unreleased-entry section="included" -->
### Tailwind release artifact provenance

- Release automation now packs [`ForgeTrust.AppSurface.Web.Tailwind`](../../Web/ForgeTrust.AppSurface.Web.Tailwind/README.md) once and passes the immutable package artifact to every native host. Each host checks the restored archive and extracted build payload against the producer's package and release manifest before it reports success.
- Stable and prerelease publishing require one complete five-host evidence set for that same artifact, plus a frozen publication-start receipt. A failed or incomplete host proof blocks publication; retry the full native invocation or resume the original candidate as described in the [release operations guide](../../.github/release-ops.md).
- The [PackageIndex maintainer tool](../../tools/ForgeTrust.AppSurface.PackageIndex/README.md) provides typed consumer and evidence verification commands. Local proof output is useful for development but does not authorize a release.
