# AppSurface deployment

AppSurface deployment turns explicit application-topology intent into reviewable provider artifacts while leaving cloud authority with the consuming application's release workflow.

The deployment family has three layers:

- [`ForgeTrust.AppSurface.Deployment`](./ForgeTrust.AppSurface.Deployment/README.md) defines portable, schema-versioned intent, validation, diagnostics, deterministic serialization, and provider contracts. Install it directly when authoring a provider.
- [`ForgeTrust.AppSurface.Aspire`](../Aspire/ForgeTrust.AppSurface.Aspire/README.md) annotates existing Aspire resources and registers native Aspire publish and named verification steps. It does not create a second topology graph.
- [`ForgeTrust.AppSurface.Deployment.GcpCloudRun`](./ForgeTrust.AppSurface.Deployment.GcpCloudRun/README.md) compiles a bounded migration job to Cloud Run v2 Job Terraform JSON and performs read-only parity verification.

## Ownership boundary

`aspire publish` evaluates the AppHost and writes deterministic intent, Terraform JSON, and evidence. It makes no cloud calls and does not build or push images, resolve secret values, apply infrastructure, execute migrations, or change traffic.

`aspire do appsurface-gcp-verify` regenerates and validates those artifacts before using read-only `gcloud run jobs describe` and IAM-policy inspection. It never starts a job or changes infrastructure. CI remains responsible for credentials, OpenTofu state and apply, execution, canaries, approval, promotion, and rollback.

## Adoption order

1. Capture the existing deployed job contract, including server defaults that an older deployment command omitted.
2. Annotate the existing Aspire `ProjectResource` and assign it to one explicit AppSurface compute environment.
3. Publish artifacts from an immutable image digest and full source revision.
4. Prove shadow operational parity while the legacy writer remains authoritative.
5. Treat state import and writer cutover as a separately approved operation; never run two configuration writers.

Do not infer legacy task, parallelism, retry, or timeout defaults. They must come from deployed and imported-provider evidence.

Read next:

- [Native deployment example](../examples/aspire-deployment-apphost/README.md)
- [Public contracts and schemas](reference.md)
- [Shadow adoption and single-writer cutover](adoption.md)
- [Diagnostics and troubleshooting](diagnostics.md)

---
[Back to root](../README.md)
