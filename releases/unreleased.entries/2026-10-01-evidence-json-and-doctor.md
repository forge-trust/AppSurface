<!-- appsurface:unreleased-entry section="migration-watch" -->

- [EvidenceHost planning and verification](../../start-here/evidencehost.md) now read policy, plan and manifest
  JSON under a counted **20 MiB** limit per input. Duplicate or case-colliding properties, numeric or unknown
  enum values, missing or null required members, null contract collection items, and plan or manifest versions
  other than `1.0` fail with a safe diagnostic. Regenerate unsupported
  evidence files and use unique properties and named enum values in policies; see the
  [bounded JSON reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md#bounded-json-input).
  `evidence doctor` reports protected execution facts as `unverified` for every profile. Its
  `ready_with_external_prerequisites` status describes structural readiness and grants no runtime admission.
  Consult the [consumer acceptance requirements](../../docs/evidence/issue779-consumer-acceptance.md) before
  adopting protected execution; the Linux mechanism fixture is provisional and does not enable Trusted support.
