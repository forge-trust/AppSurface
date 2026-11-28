<!-- appsurface:unreleased-entry section="migration-watch" -->

- [`appsurface evidence run` and `verify`](../../Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md#verifying-evidence)
  print an additional line reminding readers to check manifest provenance and gate requirements.
  The result lines and exit codes are unchanged. Automation can read `ClaimKind` and `Eligibility`
  from the manifest JSON.
