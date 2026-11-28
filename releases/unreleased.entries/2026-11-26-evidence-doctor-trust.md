<!-- appsurface:unreleased-entry section="migration-watch" -->

- [`appsurface evidence doctor`](../../Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md#doctor-preflight)
  now reports `unverified trusted-envelope` for every profile. A valid policy and diff return exit code 0
  with `ready_with_external_prerequisites`, including release profiles run outside GitHub Actions.
  Use the [registered verifier in the protected workflow](../../guides/evidencehost-cookbook.md#release-evidence)
  to validate the release envelope.
