<!-- appsurface:unreleased-entry section="included" -->
### Published CLI smoke verification

- Published CLI packages now reach the same payload and PostgreSQL proof checks as library packages after a
  successful tool installation. The [release verifier](../../tools/ForgeTrust.AppSurface.PackageIndex/README.md#durable-preflight-artifact-proof-845)
  reads the archive retained by the SDK's isolated tool store, preserves its signed bytes, and rejects missing,
  changed, or linked archives before accepting public proof. Existing immutable release tags require reviewed
  recovery or a new coordinated release to use the corrected verifier.
