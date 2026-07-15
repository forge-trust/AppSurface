# Adopt AppSurface deployment safely

## Before authoring

Capture one real migration-job change across the current AppHost, deployment workflow, environment file, and OpenTofu root. Record the deployed Job and imported-provider representation, including task count, parallelism, retries, timeout, provenance labels, IAM policy, network, Cloud SQL attachment, secret reference, and service account. Values omitted by a legacy command are not AppSurface defaults.

## Shadow adoption

1. Install coordinated versions of `ForgeTrust.AppSurface.Aspire` and `ForgeTrust.AppSurface.Deployment.GcpCloudRun` in the AppHost.
2. Keep the native entry point on `DistributedApplication.CreateBuilder(args)` and reuse the existing component classes.
3. Add non-secret parameters for the relative binding-profile path, full image digest, and full source commit. Keep the connection parameter secret.
4. Annotate the existing migration `ProjectResource` once and assign it to the explicit target with Aspire's `WithComputeEnvironment`.
5. Run `aspire publish --list-steps`, then publish to a dedicated artifact directory. Review the three artifacts and retain them as CI evidence.
6. Run `aspire do appsurface-gcp-verify` with read-only credentials. The existing deployment command remains authoritative throughout shadow parity.

Publish success means only that deterministic artifacts were written. It does not mean an image was built or pushed, infrastructure was applied, a migration ran, or traffic changed. Verify success means configuration parity was observed; it does not prove migration execution or inherited IAM effectiveness.

## Integrate the existing IaC root

Pin OpenTofu and the Google provider in the application's existing root. Place the generated JSON at a stable generated path before `tofu init`; never hand-edit it and never apply it as a separate state root. CI validates hashes and schema before using it.

## Separately approved cutover

Acquire the existing remote-state lock, import the physical Job at `google_cloud_run_v2_job.appsurface_migration["<logical-id>"]`, and require a no-op imported-resource plan. Put the legacy writer and generated-resource apply behind one mutually exclusive writer decision. Only after that gate may OpenTofu become authoritative and owned parity require provenance labels.

Rollback stays inside the same OpenTofu state and selects a prior immutable image. Do not re-enable a competing `gcloud run jobs deploy` writer as rollback.
