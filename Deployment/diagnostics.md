# Deployment diagnostics

Every deployment validation failure uses an `ASDEPLOY1xx` or Aspire-adapter `ASDEPLOY2xx` code and safe Problem, Cause, Fix, and Docs fields. Diagnostics contain identifiers when useful but must not contain parameter values, inherited environment contents, connection strings, raw secret material, or unsanitized tool output.

## Intent and artifact diagnostics

- `ASDEPLOY101`–`ASDEPLOY103`: malformed logical id, mutable image, or abbreviated source revision. Use canonical lowercase values.
- `ASDEPLOY104`–`ASDEPLOY107`: invalid task, parallelism, retry, or timeout bounds. Capture deployed defaults and declare them explicitly.
- `ASDEPLOY108`–`ASDEPLOY118`: invalid secret/database/job relationships, plaintext secret-shaped environment input, duplicate job, or empty target.
- `ASDEPLOY119`–`ASDEPLOY125`: unsafe artifact name or directory, duplicate or reserved artifact, symlink, ownership mismatch, or unexpected mixed file. Artifact names are portable single segments, case-insensitively unique, and may not claim the ownership marker.
- `ASDEPLOY128`–`ASDEPLOY129`: missing version 1 private/socket connectivity or a plaintext setting that collides with the connection-secret environment name. Keep the explicit private Cloud SQL contract and one secret-backed value source.

## GCP input and verification diagnostics

- `ASDEPLOY130`–`ASDEPLOY142`: missing, malformed, unsupported, environment-mismatched, secret-bearing, unknown, duplicate, or invalid binding-profile input. Validate the checked-in non-secret profile and selected Aspire environment.
- `ASDEPLOY150`–`ASDEPLOY164`: wrong or incomplete bundle, identity/hash mismatch, missing Job, permission/auth failure, malformed GCP response, unsupported parity mode or capability, missing binding, drift, or forbidden public IAM principal.
- `ASDEPLOY165`–`ASDEPLOY166`: `gcloud` is unavailable or exceeded the bounded timeout. Publish does not need `gcloud`; only read-only verification does.
- `ASDEPLOY167`: the Aspire environment cannot be represented as a Google Cloud label. Use at most 63 ASCII letters, digits, underscores, or hyphens.
- `ASDEPLOY168`: a declared environment setting attempts to replace Cloud Run runtime metadata or application-default credentials. Use the assigned service identity and runtime-owned variables.

## Aspire adapter diagnostics

- `ASDEPLOY201`–`ASDEPLOY207`: parameters come from another AppHost builder, have the wrong secret classification, duplicate an annotation, or leave required authoring fields unset.
- `ASDEPLOY208`–`ASDEPLOY213`: unsupported endpoint projection, missing target capability, parity failure, duplicate provider artifact, or missing/non-secret metadata resolution failure.
- `ASDEPLOY214` and later adapter codes protect output ownership, artifact hashes, binding-path confinement, and explicit target assignment.

Start with the code. Correct the cause and rerun the same native Aspire command. Do not bypass a validation by copying sensitive data into a non-secret parameter or by editing generated artifacts.
