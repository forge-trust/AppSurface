# ForgeTrust.AppSurface.Deployment.GcpCloudRun

This provider compiles AppSurface migration-job intent into reviewable Google Cloud Run v2 Job Terraform/OpenTofu JSON and verifies deployed configuration through read-only `gcloud` commands.

## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package from a prerelease feed, check the [package chooser](../../packages/README.md) and [release hub](../../releases/README.md) for current release risk, migration guidance, and readiness.

Publishing is pure artifact generation. It does not authenticate to Google Cloud, resolve secret values, apply infrastructure, execute a job, or change traffic. Foundations such as the VPC, subnet, service account, Cloud SQL instance, and Secret Manager secrets remain externally provisioned.

Use `GcpCloudRunDeploymentTarget.Create()` from an AppSurface Aspire deployment target. The binding profile is a checked-in, non-secret, schema-versioned JSON file beneath the AppHost. Unknown properties, symlinks anywhere inside that trusted path, environment mismatches, malformed provider identifiers, Terraform template expressions, public principals, and fields shaped like secret values are rejected.

Verification validates hashes, resource addresses, capabilities, and every duplicated operational field across the portable intent, provider plan, and Terraform artifact before invoking `gcloud`. A coordinated plan/Terraform rewrite that contradicts intent fails closed. Shadow mode compares operational configuration, including declared non-secret environment settings, while a legacy writer remains authoritative. Owned mode additionally checks canonical AppSurface provenance labels. Undefined parity modes fail rather than silently behaving like shadow mode.

The target applies Cloud Run's provider boundary before rendering: names containing `=`, names beginning with `X_GOOGLE_`, and runtime-owned names are rejected, and the secret-backed connection variable counts toward Cloud Run's 1,000-variable container limit.
