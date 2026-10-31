# Cloud Run execution observation consumers

This executable example exercises the public `ForgeTrust.AppSurface.Deployment.GcpCloudRun` observer from two neutral migration-job consumers: `schema-preparation` and `catalog-refresh`. Each mode renders its own three-artifact bundle, selects its own canonical execution resource, and supplies an independently authored fixture through the public bounded-reader seam.

The fixture reader is intentionally offline. The sample does not invoke `gcloud`, submit or cancel an execution, resolve a secret, or mutate a cloud resource. The `--swapped` mode supplies the other execution's evidence and must return `Unknown` with a nonzero exit code.

The v1 fixture uses the command's lowercase `all-traffic` egress annotation representation required by the observer; the rendered bundle retains the public profile's `ALL_TRAFFIC` value. These example fixtures explicitly include zero counters to exercise the parser's positive terminal-counter path. For v1 only, omitted zero counters may be admitted when the exact bounded SDK/CLI replay mapping documents that omission; the separate evidence worksheets preserve other unavailable-proof fields and must not be read as provider-attested payloads.

```bash
dotnet run --project examples/cloud-run-execution-consumers/CloudRunExecutionConsumers.csproj -- schema-preparation
dotnet run --project examples/cloud-run-execution-consumers/CloudRunExecutionConsumers.csproj -- catalog-refresh
dotnet run --project examples/cloud-run-execution-consumers/CloudRunExecutionConsumers.csproj -- schema-preparation --swapped
dotnet run --project examples/cloud-run-execution-consumers/CloudRunExecutionConsumers.csproj -- schema-preparation --bundle-output ./artifacts/schema-preparation
```

The logical job is optional and defaults to `schema-preparation`. The two successful modes produce `Succeeded` with `ConfigurationMatched=true`; the swapped mode demonstrates exact execution and captured-image rejection. `--bundle-output` is optional and requires a directory operand; the sample writes the rendered bundle there and removes its temporary profile and fixture workspace. Unknown or repeated arguments, including a missing or option-like bundle directory, return a usage error before producing output. These are contract proofs using synthetic data, not provider-attested execution results.
