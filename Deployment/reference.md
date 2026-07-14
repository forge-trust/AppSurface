# Deployment reference

## Portable intent

`DeploymentIntent` is a schema-versioned, provider-neutral snapshot of one evaluated Aspire environment and an explicit full source revision. Version 1 accepts one or more `MigrationJobIntent` values. Jobs are sorted by `DeploymentLogicalId`; duplicate ids and empty targets fail before rendering.

A migration job contains an immutable `registry/repository@sha256:<64 lowercase hex>` image, candidate-preparation phase, executable and ordered arguments, explicit task/parallelism/retry/timeout bounds, logical connection-secret and database bindings, logical service identity, non-secret environment settings, and required run-to-completion, relational-connection, and private-network capabilities.

`SecretBinding` is a reference, never a secret container. Version 1 supports environment-variable reference delivery only. Plaintext secret-shaped environment fields are rejected. Provider adapters must not evaluate the parameter named by the binding.

## Provider contract

`IDeploymentTarget.RenderAsync` receives validated intent, a non-secret binding-profile path, output directory, generator version, and optional trusted authoring root. The GCP target uses that root to reject every symlink or reparse point between the AppHost and profile before reading. It returns named `DeploymentArtifact` values with exact SHA-256 hashes. Rendering must be deterministic and must not call cloud APIs, apply infrastructure, or execute work.

`IDeploymentTarget.VerifyAsync` receives the freshly rendered result and either `Shadow` or `Owned` parity mode. Verification may inspect deployed state read-only. It must not accept or start an execution or alter traffic.

`DeploymentArtifact.Create` accepts one portable file name only. It rejects directory segments, control characters, Windows-invalid punctuation and device names, and trailing dots or spaces before any filesystem write. `DeploymentArtifactBundleWriter` validates the complete file set, rejects portable case collisions and the reserved ownership-marker name, rejects a non-owned or mixed directory, writes to a same-parent staging directory, and swaps the complete bundle only after all bytes have been written. Give each target a dedicated output directory; do not place unrelated files there or edit generated artifacts.

Migration-job `Environment` entries are serialized into review artifacts and must contain non-secret configuration only. Secret-shaped names, including password, token, credential, connection-string, database-URL, API-key, private-key, and access-key variants, fail with `ASDEPLOY115`; use `SecretBinding` instead. Treat command arguments and environment values as artifact-visible inputs even when their names look harmless.

## GCP binding profile v1

The closed JSON object contains:

| Field | Meaning |
| --- | --- |
| `schemaVersion` | Must be `1.0`. |
| `environment` | Exact Aspire environment selected with `--environment`. |
| `project`, `region` | Existing GCP location; AppSurface does not create it. |
| `jobs` | Logical job id to existing physical Cloud Run Job name. |
| `cloudSqlInstanceConnectionName` | Existing `project:region:instance` attachment. |
| `network` | Existing Direct VPC network, subnetwork, and `PRIVATE_RANGES_ONLY` or `ALL_TRAFFIC` egress. |
| `serviceAccounts` | Logical identity id to existing runtime service-account email. |
| `secrets` | Logical secret id to physical Secret Manager id and explicit `latest` version mode. |

Unknown, duplicate, secret-bearing, malformed, environment-mismatched, symbolic-link, and Terraform-template-shaped inputs are rejected. Logical map keys must be canonical `DeploymentLogicalId` values. Project, region, Job, Cloud SQL, network, subnet, service-account, and secret identifiers use anchored provider-specific formats rather than a generic token allowlist. The profile stores identifiers only. It must never contain a connection string, password, token, secret value, `${...}`, or `%{...}` expression.

## Generated artifacts

- `deployment-intent.v1.json` is the portable review contract. The Aspire adapter owns its canonical bytes: a provider may echo the exact artifact or omit it, but a contradictory provider copy is rejected before any bundle is written.
- `gcp-cloud-run-migration.tf.json` declares only `google_cloud_run_v2_job.appsurface_migration` and references externally provisioned foundations.
- `gcp-cloud-run-migration.plan.json` records project, region, exact Terraform resource addresses, required capabilities, logical-to-physical mapping, normalized expected parity, source revision, generator version, and hashes linking the intent and Terraform bytes. Verification cross-checks every operational expected field against the hashed Terraform before any cloud command.

The consuming OpenTofu root owns its exact OpenTofu and Google provider pins. Do not initialize or apply the generated file as a disconnected state root. Copy it into the existing reviewed root before `tofu init` and keep apply/state authority in CI.

## Parity modes

Shadow parity compares operational configuration while the legacy deployment command remains the only writer: image, command/arguments, sorted non-secret environment settings, task bounds, Direct VPC, Cloud SQL, secret reference/version, and service account. It intentionally ignores AppSurface provenance labels.

Cloud Run runtime names (`CLOUD_RUN_*`, `K_*`, and `PORT`) and `GOOGLE_APPLICATION_CREDENTIALS` cannot be declared as application settings. Use the runtime metadata as supplied and the explicit service identity for Google API access.

Owned parity additionally requires canonical environment and source-revision labels after state import and writer cutover. Effective inherited or custom IAM remains `not-independently-verified`; a public `allUsers` or `allAuthenticatedUsers` Job principal is always a failure.
