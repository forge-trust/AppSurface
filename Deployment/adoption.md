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

## Observe an existing execution

After publishing the three artifacts, a caller may observe one already-selected execution. Observation does not submit, cancel, retry, or mutate a Job. The caller must correlate the execution resource to its own submission and remains responsible for acceptance and recovery.

The exact identity is `projects/{project-or-number}/locations/{region}/jobs/{physical-job}/executions/{execution}`. Supply the logical Job id and full resource; `latest`, prefixes, listing, project alias lookup, and current-Job substitution are rejected. The project may be the configured project ID or explicitly configured project number, matched literally without inference.

The CLI reads the trusted bundle directory and validates the three canonical artifacts before provider access:

```sh
appsurface deployment gcp-cloud-run observe-execution \
  --bundle-dir ./artifacts/schema-preparation \
  --job schema-preparation \
  --execution projects/my-project/locations/us-central1/jobs/schema-preparation/executions/exec-20261024 \
  --timeout 60s --poll-interval 2s --json
```

The CLI defaults are 60 seconds and 2 seconds. The API has no defaults. Its one JSON result has `schemaVersion: "1.0"`, logical id, selected resource, state, configuration match, UTC `observedAt`, and diagnostic code. It has no raw provider fields. Exit codes are 0 for matched success, 1 for matched workload failure or remote cancellation, 2 for invalid/unknown/mismatch/provider/output/cleanup failure, 3 for clean last-observed running evidence at the deadline, and 130 for caller cancellation. Text-mode failures write only fixed safe problem/cause/fix/recovery guidance and the canonical reference link to stderr.

For offline adoption, use the neutral `schema-preparation` and `catalog-refresh` fixtures to prove bundle identity, configuration matching, state classification, and swapped-execution rejection. A separately authorized provider read, if performed, uses the caller's existing `gcloud` installation, credentials, and `PATH`; this guide makes no live-read or compatibility claim. Document only variable names in CI and keep values out of bundles and logs. The observer's v1 Cloud SQL proof is the gen2 `run.googleapis.com/cloudsql-instances` annotation (implicit socket configuration), while v2 proves a typed Cloud SQL volume and `/cloudsql` mount. Neither shape proves filesystem/IAM/database reachability or application acceptance.

The complete two-consumer workflow is in the [Cloud Run execution consumers example](../examples/cloud-run-execution-consumers/README.md). It is an offline contract walkthrough, not a production migration or provider-compatibility claim.

For POSIX CI, capture and branch on every result without retrying or mutating the workload:

```sh
set +e
appsurface deployment gcp-cloud-run observe-execution --bundle-dir "$BUNDLE_DIR" --job "$JOB_ID" --execution "$EXECUTION_RESOURCE" --timeout 60s --poll-interval 2s --json
status=$?
set -e
case "$status" in
  0) echo "execution matched and succeeded" ;;
  1) echo "execution matched but failed or was remotely cancelled"; exit 1 ;;
  2) echo "observation was unknown or invalid"; exit 2 ;;
  3) echo "execution is still running at the observation deadline"; exit 3 ;;
  130) echo "observation was cancelled by the caller"; exit 130 ;;
  *) echo "unexpected observation exit"; exit 2 ;;
esac
```

The command uses a single safe writer. On cancellation it attempts one safe `Unknown`/false envelope only before output failure; after the first write/flush failure it suppresses further stdout/stderr writes and still returns 130. Ordinary output delivery failure is `ASDEPLOY188` and returns 2; unexpected internal failures are `ASDEPLOY189` and return safe `Unknown`. It exposes no general executable or extra-argument option.
