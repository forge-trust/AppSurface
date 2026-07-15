# Native Aspire deployment example

This AppHost calls `DistributedApplication.CreateBuilder(args)` so Aspire owns operation mode, environment, parameters, output path, step discovery, and named-step execution. It annotates the existing `ProjectResource` once and assigns it to one explicit AppSurface compute environment.

`WebAppExample` stands in for a migration executable so this repository can compile the complete authoring shape. A real application uses its existing migration-service project and command.

## First artifact

Supply a full immutable image identity and full lowercase source commit. The connection parameter is secret, but AppSurface records only its logical name and never resolves its value.

```bash
export Parameters__appsurface_gcp_bindings=gcp-staging.bindings.json
export Parameters__example_migrations_image=us-central1-docker.pkg.dev/example-project/apps/migrations@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
export Parameters__appsurface_source_revision=0123456789abcdef0123456789abcdef01234567

aspire publish --apphost examples/aspire-deployment-apphost/AspireDeploymentAppHostExample.csproj --environment Staging --list-steps
aspire publish --apphost examples/aspire-deployment-apphost/AspireDeploymentAppHostExample.csproj --environment Staging --output-path ./artifacts/appsurface
```

The output contains `deployment-intent.v1.json`, `gcp-cloud-run-migration.tf.json`, and `gcp-cloud-run-migration.plan.json`. Publishing requires neither `gcloud` nor OpenTofu. It makes no cloud calls and changes no infrastructure.

## Read-only parity

After reviewing the artifacts and authenticating a read-only GCP inspection identity:

```bash
aspire do appsurface-gcp-verify --apphost examples/aspire-deployment-apphost/AspireDeploymentAppHostExample.csproj --environment Staging --output-path ./artifacts/appsurface
```

Verification regenerates the bundle, validates its hashes, describes the bound Job and IAM policy, and compares normalized operational fields. It does not execute the job. Inherited or custom IAM effectiveness remains `not-independently-verified`.

This native named step uses [`Shadow` parity](../../Deployment/reference.md#parity-modes) only. After state import and writer cutover, the application-owned release workflow must invoke the provider target with `Owned` parity when it needs provenance-label enforcement.

Do not use the sample profile as production infrastructure configuration. Capture the existing deployed and imported-provider contract first, especially task count, parallelism, retries, and timeout. State import and switching the authoritative writer require a separate reviewed cutover.
