# Issue 779 checkpoint 3 native Aspire application

This checkpoint prepares a genuine .NET Aspire AppHost 13.4.4 bundle and exercises it through the [compiled C# root application supervisor](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/application-execution.md). The [Python test runner](run-native.py) is orchestration and observation only; the root process owns application selection, startup, readiness, cancellation, cleanup, and the accepted result.

The [workflow](../workflows/issue779-checkpoint3-application.yml) runs on `ubuntu-24.04` with .NET SDK `10.0.401`. It first captures the complete committed source tree, runs the ordinary `None` supervision tests and focused CLI tests with warnings treated as errors, and restores the native resource and AppHost in locked mode. AppHost's SDK does not support `publish`; the workflow therefore builds each native resource variant into a fresh output directory, then builds the AppHost against that exact resource output. The four resource bundles are `normal`, `readiness`, `factory-stall`, and `stuck-descendant`.

After the bundles are built, [payload preparation](prepare_payload.py) verifies the finished bundle files and modes, copies each variant and its policy into a fresh preparation root, and generates the C3 Planner registration source. The same generated source is compiled into the private C3 CLI and C3Sdk CLI. A compiled C# digest helper records the registration catalogue and entry digests. Both tool images receive the pinned ReportGenerator payload and the subject's fourteen packages, acquired by the checkpoint2 NuGet content-hash helper against the subject lock file before the root runner starts.

The root native runner receives the CLI image, SDK image, full SDK root, subject root, preparation receipt, compiled registration digests, output directory, source revision, and base revision. It runs these seven controls sequentially:

1. Normal application through the C3 CLI image.
2. Normal application through the C3Sdk CLI image.
3. Actual resource readiness failure with HTTP 503 and independently observed process identity.
4. Factory-entry stall before DCP startup.
5. Cancellation while the application is running.
6. Stuck descendant with a test coordination marker after PID, UID/GID, and cgroup identity checks, followed by the real SIGTERM handler marker. Its private health handler stays pending until that independent check completes, using request cancellation; it does not answer a premature transient 503 that could end the real one-attempt readiness procedure before the control is established.
7. Disposal failure while a real scratch holder prevents ordinary unmount, with no accepted receipt and accounts retained through root termination.

The CLI and SDK tool images are each built once and reused across the case matrix. The root runner selects the matching generated application registration and copies that case's prepared bundle and policy into its fresh protected tool root. Immutable SDK and tool staging uses a fresh root-owned `/var/lib/issue779-c3-input-<UUID>` directory: duplicating those large inputs into `/run` exhausted the runner's temporary filesystem before the first control in the initial Linux attempt. The C# owner's bounded application and producer workspaces remain under `/run`.

After a control has passed its original process settlement and named/numeric account-absence checks, the fixture may reclaim only that generation's two large sealed `/run/appsurface-app-<UUID>` and `/run/appsurface-evidence-producer-data-<UUID>` trees. Reclamation uses bounded descriptor-relative traversal, enumerates entries incrementally, rejects links, foreign owners, writable group/other modes and device or kernel mount-ID crossings, and does not run for the final quarantine control. Failure to reclaim fails the harness; fixture housekeeping cannot upgrade a failed C# invocation. Each GitHub-hosted runner is disposable; the immutable `/var/lib` staging tree is retained for diagnosis until that job VM is destroyed, rather than reused by another run. The small worker output tree remains available for inspection, and successful manifests are copied into private evidence before reclamation.

The workflow retains `source.json`, the generated registration source and its SHA-256, `payload-preparation.json`, `registration-digests.json`, the native receipts, and bounded logs. The final source capture rechecks the original committed source bytes and modes. Artifacts are uploaded only after the retention bound checks pass and are kept for one day.

The workflow is triggered by pushes to `codex/issue779-checkpoint3-application-20261017`. Source preparation alone does not establish native acceptance; only a successful run of the complete seven-control workflow against its captured source revision can do that.

## Managed-load denial translation

The native resource independently attempts both `File.OpenRead` and `Assembly.LoadFile` on the root-selected
real `protected-tool.dll`. On Linux, the denied tool mount can make the managed loader report
`FileNotFoundException` rather than access denial. The private resource accepts that translation only after
the independent open attempt was denied. Before start, readiness and final join, the C# workspace separately
requires the actual host file to be a root-owned, nonwritable, bounded managed PE/CLR image. Missing or invalid
probe input therefore rejects at the root and cannot pass this negative control. No tool path is made readable.
