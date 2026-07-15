# ForgeTrust.AppSurface.Deployment.GcpCloudRun

This provider compiles AppSurface migration-job intent into reviewable Google Cloud Run v2 Job Terraform/OpenTofu JSON and verifies deployed configuration through read-only `gcloud` commands.

## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package from a prerelease feed, check the [package chooser](../../packages/README.md) and [release hub](../../releases/README.md) for current release risk, migration guidance, and readiness.

Publishing is pure artifact generation. It does not authenticate to Google Cloud, resolve secret values, apply infrastructure, execute a job, or change traffic. Foundations such as the VPC, subnet, service account, Cloud SQL instance, and Secret Manager secrets remain externally provisioned.

Use `GcpCloudRunDeploymentTarget.Create()` from an AppSurface Aspire deployment target. The binding profile is a checked-in, non-secret, schema-versioned JSON file beneath the AppHost. The trusted-root loader rejects symlinks from that AppHost boundary through the file; the rootless loader scans to the filesystem root while allowing only the standard macOS `/var`, `/tmp`, and `/etc` aliases to their matching `/private` directories. Unknown properties, other symlinks, environment mismatches, malformed provider identifiers, Terraform template expressions, and fields shaped like secret values are rejected.

Verification validates hashes, resource addresses, capabilities, and every duplicated operational field across the portable intent, provider plan, and Terraform artifact before invoking `gcloud`. It also inspects the live IAM policy and rejects public principals. A coordinated plan/Terraform rewrite that contradicts intent fails closed. Shadow mode compares operational configuration, including declared non-secret environment settings, while a legacy writer remains authoritative. Owned mode additionally checks canonical AppSurface provenance labels. Undefined parity modes fail rather than silently behaving like shadow mode.

The target applies Cloud Run's provider boundary before rendering: names containing `=`, names beginning with `X_GOOGLE_`, and runtime-owned names are rejected, and the secret-backed connection variable counts toward Cloud Run's 1,000-variable container limit.

The default verifier launches `gcloud` directly on Unix-like hosts. Windows uses the Cloud SDK's `gcloud.cmd` launcher through `cmd.exe /d /c`, but only after rejecting empty arguments, whitespace, control characters, and command-shell metacharacters. Keep provider identifiers canonical; do not pass free-form shell fragments to `IGcloudCommandRunner`.
