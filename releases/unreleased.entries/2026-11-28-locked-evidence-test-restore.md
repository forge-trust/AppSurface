<!-- appsurface:unreleased-entry section="migration-watch" -->

- The [built-in Evidence test producer](../../Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md#build-configuration-and-locked-restore)
  now builds and tests in Release by default. Pass `appsurface evidence run --configuration Debug`
  if a project relies on Debug. In CI, add `--locked-mode` to restore against the committed NuGet
  lockfiles and fail the run if dependencies diverge from them.
