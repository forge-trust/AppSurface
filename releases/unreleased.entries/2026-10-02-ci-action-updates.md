<!-- appsurface:unreleased-entry section="included" -->

### CI dependency validation

- The [build workflow](../../.github/workflows/build.yml) updates its pinned package-manager, coverage-reporting, and documentation-deployment actions, and the [benchmark workflow](../../.github/workflows/benchmarks.yml) updates its results publisher. The [coverage evidence workflow](../../.github/workflows/coverage-efficiency.yml) retains restore-only caches with full commit pins; its contract tests allow dependency updates without requiring an obsolete action version. Package consumers need no code changes.
- [Tailwind runtime package builds](../../Web/ForgeTrust.AppSurface.Web.Tailwind/README.md#offline-and-ci-behavior) keep native payload downloads in a separate cache directory so parallel solution builds cannot contend with app build or watch executables. Existing app caches continue to work; the first runtime package build downloads its payloads into the new directory.
