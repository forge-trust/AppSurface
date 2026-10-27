# Issue 779 checkpoint 3 native Aspire application

This checkpoint prepares a genuine .NET Aspire AppHost 13.4.4 bundle and exercises it through the [compiled C# root application supervisor](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/application-execution.md). The [Python test runner](run-native.py) is orchestration and observation only; the root process owns application selection, startup, readiness, cancellation, cleanup, and the accepted result.

The separate [compile-owned SDK lifecycle stall controls](lifecycle-stalls-source-preparation.md) exercise synchronous registration configuration and a registered application disposer through the same authenticated C# worker. Their two private build selections are absent from ordinary images, run under the original product clocks, and retain failure observations in a separate bounded artifact. They neither replace this checkpoint's seven original cases nor enable Trusted support. Fresh native execution is required before either new control is credited.

The [workflow](../workflows/issue779-checkpoint3-application.yml) runs on `ubuntu-24.04` with .NET SDK `10.0.401`. It first captures the complete committed source tree, runs the ordinary `None` supervision tests and focused CLI tests with warnings treated as errors, and restores the native resource and AppHost in locked mode. AppHost's SDK does not support `publish`; the workflow therefore builds each native resource variant into a fresh output directory, then builds the AppHost against that exact resource output. The four resource bundles are `normal`, `readiness`, `factory-stall`, and `stuck-descendant`.

After the bundles are built, [payload preparation](prepare_payload.py) verifies the finished bundle files and modes, copies each variant and its policy into a fresh preparation root, and generates the C3 Planner registration source. The same generated source is compiled into the private C3 CLI and C3Sdk CLI. The [compiled digest helper](registration-digest/Program.cs) first passes every generated producer declaration through the actual shared metadata factory, then records the registration catalogue and entry digests. This preflight starts no producer or application and grants no admission. It rejects declarations that can be hashed by the catalogue but cannot be registered by the adapter. Both tool images receive the pinned ReportGenerator payload and the subject's fourteen packages, acquired by the checkpoint2 NuGet content-hash helper against the subject lock file before the root runner starts.

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

The seven-case matrix has a 30-minute orchestration bound, including protected SDK/bundle preparation for each fresh execution. Each case still has its original 480-second bound, each protected request retains its 1200-second job deadline, and each root OS owner retains its 1100-second runtime bound. Application, producer, stopping and cleanup limits are unchanged. [Run 39071711362](https://github.com/forge-trust/AppSurface/actions/runs/39071711362) passed the first six controls but exhausted the earlier shared 20-minute matrix clock during the final disposal control. That control recorded the required `EBUSY` unmount rejection, but its terminal root/account custody was unverified; it remains a failed control until a complete retry passes.

The workflow retains `source.json`, the generated registration source and its SHA-256, `payload-preparation.json`, `registration-digests.json`, the native receipts, and bounded logs. The final source capture rechecks the original committed source bytes and modes. Artifacts are uploaded only after the retention bound checks pass and are kept for one day.

The workflow is triggered by pushes to `codex/issue779-checkpoint3-application-20261017`. Source preparation alone does not establish native acceptance; only a successful run of the complete seven-control workflow against its captured source revision can do that.

## Verified checkpoint result

[Run 39076625148](https://github.com/forge-trust/AppSurface/actions/runs/39076625148) passed on source
`4ce55f5407c9bcf48e1b8dd6f64195450272fbfc` on 2026-10-17. All seven controls passed with zero skipped
native cases. Linux source builds also passed 1,740 supervision, 576 focused CLI and 155 Aspire tests;
the portable runner suite passed eight tests. Compiler/analyzer diagnostic counts were zero.

| Control | Verified result |
| --- | --- |
| CLI application and coverage | Actual AppHost/DCP/resource/producer; Passed informational Observation; 9/9 lines and 7/7 branches; accounts removed. |
| SDK application and coverage | Same plan and root implementation; same numerical coverage and claim restrictions; accounts removed. |
| Readiness failure | Independently authenticated resource returned HTTP 503; failed root result and joined cleanup. |
| Factory stall | Actual factory-entry marker before DCP; bounded forced containment and joined cleanup. |
| Cancellation | Actual running producer cancelled; original nonzero result retained and cleanup joined. |
| Stuck descendant | Pinned child received TERM and remained live with its exact handler marker before cancellation; whole group forcibly joined. |
| Disposal failure | Actual retained scratch holder caused strict unmount `EBUSY`; root failed and all eight accounts remained quarantined. |

Every case retained the original process/pending-start joins, double empty-group observations and reconciled,
error-free EOF accounting. Normal CLI and SDK application mains exited zero; their monitor-failure bits and
ledger failures were clear. The disposal failure did not publish successful root output or release identities.

Artifact `12117576849` contains 1,129,194 compressed bytes with SHA-256
`b78632201be684a42df08f837a17927dcf25a358c2359d667b5e0446defd1346`. The downloaded digest and the
complete 2,969-file source capture, Git blobs, hashes and modes matched the tested commit, with zero mismatches.
The fixture's 100% coverage is not a full-repository coverage result. The production application catalogue
and accepted Trusted proof registry remain closed; [checkpoint four](../../docs/plans/issue-779-csharp-supervision-migration.md#checkpoint-4-cutover-and-full-verification)
still requires runtime cutover, complete consumer acceptance, packaging and the unchanged solution/patch gate.

## Establishing the stuck-descendant negative control

The application stop recipe sends cooperative TERM only to AppHost. After its original grace expires, unit stop and immediate group KILL provide forced containment; a descendant's TERM callback is not guaranteed to run in that final interval. [Run 39073759716](https://github.com/forge-trust/AppSurface/actions/runs/39073759716) authenticated and joined the actual stuck child but did not observe its TERM marker, so that control correctly failed.

The fixture first pins the child PID with a Linux [pidfd](https://man7.org/linux/man-pages/man2/pidfd_open.2.html), then samples and rechecks its UID/GID, start time and cgroup while requiring that descriptor to remain live around the samples. It sends TERM through that retained descriptor. It requires the complete exact handler marker and the same still-live process before publishing the private health-coordination marker or cancelling the running producer. The marker read is bounded to 65 bytes; a missing, partial or invalid marker grants nothing and remains pending within the original deadline. The descriptor closes on every outcome. PID reuse and exit reject. This establishes the intended TERM-resistant child before testing the unchanged C# stop/KILL/join procedure; it changes no production shutdown policy or accepted claim. The [portable controls](test_run_native.py) verify ordering and rejection with pipe descriptors and mocked signaling, not real pidfds or native authority. The seven-case Linux matrix supplies that execution evidence.

## Managed-load denial translation

The native resource independently attempts both `File.OpenRead` and `Assembly.LoadFile` on the root-selected
real `protected-tool.dll`. On Linux, the denied tool mount can make the managed loader report
`FileNotFoundException` rather than access denial. The private resource accepts that translation only after
the independent open attempt was denied. Before start, readiness and final join, the C# workspace separately
requires the actual host file to be a root-owned, nonwritable, bounded managed PE/CLR image. Missing or invalid
probe input therefore rejects at the root and cannot pass this negative control. No tool path is made readable.

## Descriptor publication observation

The [native runner](run-native.py) delegates to the shared [C2 sealed-publication observer](../issue779-checkpoint2/run-native.py), selecting the v2 schema. It waits for the C# owner's existing `0440` descriptor seal before
reading JSON. File creation alone is not publication: [`WriteDescriptor`](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/LinuxRunWorkspace.cs)
creates a writable file, writes and fsyncs it, then seals and retains it. The fixture treats its original
`0600`/`0640` phase as pending within the existing case deadline. It checks a bounded, single-link regular
file and unchanged retained/named metadata around the read; malformed sealed JSON still fails. This
observer neither authenticates the worker nor replaces the C# peer, descriptor or runtime binding checks.
The [portable tests](test_run_native.py) exercise real temporary file publication and rejection; they do
not establish Linux consumer acceptance.

## Private worker coverage capture

The checkpoint-four lane additionally uses the [private C3 measurement fixture](../issue779-checkpoint4/measurement/README.md) to retain the authentic worker's private `/tmp` handle before descriptor publication. Only its first normal exit per instrumented image can contribute unchanged, validated tracker bytes; later root files are left unchanged and absent worker files contribute nothing. Coverlet's original tracker and standard report reader perform all counter accumulation. This is compiled out of ordinary images, grants no runtime authority, and changes no unit environment or protected path. The seven selected modules and all original/instrumented DLL/PDB hashes in both images are bound before native execution. Nested pinned collectors instrument C3 and C3Sdk before the same seven controls run once; the SDK report then reaches the outer collector through its supported report merge. The [measurement reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/README.md) specifies the six fixed gate inputs, separate first-worker witnesses, and actual root TRX requirements. This SDK measurement requires a fresh Linux run; prior uninstrumented SDK successes supply no presumed counters.

The seven-module measurement artifact uses a dedicated [retention checker](check-retention.py): JSON up to 32 MiB, XML up to 40 MiB, other files up to 8 MiB, at most 134 files/96 MiB total, with the same regular-file/single-link and private-mode checks. Three supplementary file slots retain the separate 44-case root-marker/subtree/file-copy supplement's TRX, collector log and portable retention-control log; three further slots retain the combined 17-case application-audit/report-discovery TRX and logs, as documented in the [measurement reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/README.md#supplementary-root-marker-and-subtree-controls). These are validation artifact bounds; product output quotas, runtime limits and C2 retention are unchanged. A source build or helper proof is not a successful AppSurface measurement run.

### Failed application-audit diagnostics

If the audit collector or its exact TRX comparison fails, the
[failure-diagnostic helper](audit-failure-retention-README.md) copies only the original
`application-bundle-driver.log`, optional raw `application-bundle-tests/application-bundle.trx`,
and original `source.json` into a fresh private diagnostic directory. Their independent
limits are 8 MiB, 1 MiB and 1 MiB. The fixed
`issue779-c3-audit-failure-<run>-<attempt>` artifact is uploaded only after that bounded
copy succeeds, even if ordinary measurement retention subsequently rejects an unfinished
report. Source bytes remain unchanged; no report is synthesized or truncated. A missing
TRX is recorded as absent, and the original failed step still fails the job. Diagnostic
retention grants no native, coverage, gate or acceptance credit.

The [focused Linux filesystem workflow](../issue779-audit-focused/README.md) runs the
same seventeen audit/discovery cases against the ordinary image in a separate short
Linux job. It retains raw failure logs and TRX after bounded retention and requires
the same exact zero-skip row set. It is an early filesystem regression check; it
does not replace this checkpoint's authenticated owners, consumers or standard
collector measurement.

## Native workspace rejection controls

The [finite workspace fixture](workspace_guards.py) runs six additional controls only after all seven original application consumers pass. It uses the same fixed [C# owner request and launch recipe](run-native.py), compiled registration and unit policy, under one separate fixed 900-second orchestration partition. Python only prepares immutable inputs and observes the C# runtime. Every new case must fail with its expected original closed diagnostic, retain account quarantine, observe empty generated groups before fixture containment, and leave an outside root-owned sentinel unchanged. A failed setup, incomplete case list or failed final group check fails the lane. Each new control reserves 60 seconds for actual containment inside the shared guard-partition clock and refuses to launch without that reserve. A failed or timed-out manager stop cannot skip the separate whole-unit KILL attempt; killing only the systemd-run client is insufficient.


[`run_partitions(args, end)`](run-native.py) retains the supplied original matrix end
unchanged for staging and all seven original controls. `main` still starts that clock
at 1800 seconds before staging. Only after all seven return passing receipts does the
orchestrator sample its clock once and call the six-guard fixture with `now + 900`.
That second bound covers guard setup, observation and the existing 60-second containment
reserve; it is never restarted per guard. Any original failure prevents guard dispatch.
A guard exception or incomplete/failed six-case result leaves the existing aggregate
schema failed, preserving the original seven results without manufacturing a guard pass.

[Source32 run 39200766342](https://github.com/forge-trust/AppSurface/actions/runs/39200766342)
exposed the shared-clock defect: the verified artifact's original seven receipts ended
at 04:55:30, 04:58:42, 05:01:28, 05:05:44, 05:08:42, 05:11:44 and 05:14:08 UTC.
Three bundle guards then passed by 05:15:52, followed by FIFO at 05:18:18 and hardlink
at 05:20:42. The final oversize case had only about 17 seconds before the old matrix's
observation cutoff around 05:20:59, and recorded `root-terminal-before-deadline` at
05:21:06 after containment. Its empty final groups establish containment, not the
expected oversize rejection. Step15 publicly recorded only exit1; the individual
artifact receipts and their timestamps identify the shortened fixture window.

The original 480-second case ceiling, protected-request 1200-second deadline, 120-second
startup bound, 1100-second root-owner runtime, unit limits and actual containment
procedure are unchanged. The separate 900-second guard clock remains finite and shared:
the existing minimum with its remaining time can still shorten a later observation, so
it does not promise every guard a full 480 seconds. Incomplete work still fails. The
workflow's enclosing harness allowance must cover both partitions; this does not extend
any product deadline. The [portable orchestration controls](test_run_native.py) use a
fake clock and detached procedure data to check ordering, failure propagation, one fresh
guard clock and the unchanged seven-plus-six schema. They create no owner or native
acceptance. This correction has no fresh Linux pass yet; the full guard result still
requires genuine execution.

Three fresh normal-deployment copies are staged under generated root-owned `/var/lib/issue779-c3-guard-input-*` directories, matching the existing C3 tool-input staging. Large duplicated package inputs do not consume `/run`; the small request/subject/sentinel and C#-owned runtime workspaces retain their original layout. Each copy contains exactly one input change: an extra empty 0555 application directory, same-length changed declared-input bytes, or a 0555 declared-input mode in place of 0444. Generic protected-input auditing can accept these safe objects; the compiled application inventory must reject them at `WorkspaceCreate` with `ASEVD410`, before publishing a worker descriptor. The compiled entry and requested digest remain unchanged. The fixture records actual root ownership and node metadata rather than inventing an application lease.

The other three cases use the [actual producer subject](subject/SubjectFailureScenarios.cs) to create, close and leave a 0600 FIFO, a pair of hard-linked regular files, or a sparse 256 MiB plus one byte file in its genuine scratch directory. The original numerical coverage procedure must pass before root sealing rejects the actual object. FIFO type must reject before any blocking read; hard-link and size cases must match the sampled inode, UID/GID, link count and length in the existing closed guard frame. The first failure remains `ProducerSeal`/`ASEVD402`, with the `collect` checkpoint retained.

Account quarantine is the expected result of these custody failures. The fixture verifies all eight generated user/group names remain reserved; it never deletes them or presents fixture containment as successful product cleanup. Only the case JSON and bounded owner stdout/stderr are copied for these new controls. The original matrix used 128 artifact slots; the separate root supplement adds three slots within the same 96 MiB total and unchanged product quotas. The [receipt consistency checks](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/WorkspaceGuardMeasurement.cs) require all six observations in the source-bound native receipt; their portable tests are data checks, not native acceptance. Fresh Linux execution is required before claiming these controls pass.

## Separate SDK failed-producer consumer

The [failed-SDK controller](../issue779-checkpoint4/sdk_failure.py) runs the existing normal SDK application with the copied subject's fixed `test-failure` scenario, before the original matrix. `stage(..., sdk_only=True)` restricts fixture staging to that one SDK deployment; default staging and the original seven-case list remain unchanged. C# still owns every application, worker, producer, pipe, account and cleanup operation. Python prepares data and observes the original work. Failure containment targets only the generated units and cannot turn an original failure into a passing control.

This separate negative control requires the actual six-test failure summary, original producer natural status1, the C# application readiness/liveness guard after that result, and an `Incomplete` manifest with the expected failed producer, no artifacts/assertions, no claim/eligibility and successful cleanup. The application must stop normally; producer, worker and application groups must be empty, and all eight named/numeric identities must be absent. Worker measurement requires natural status1 with original pidfd exit and unchanged handoffs for the four executed SDK modules: CLI, Contracts, Coverage and Aspire. The [SDK worker branch](../../Cli/ForgeTrust.AppSurface.Cli/EvidenceWorkerCommand.cs) calls the host directly, so the seven-module image/report inventory does not imply a protected Evidence.Cli execution or require an unused tracker file. Neither a killed worker nor an arbitrary nonzero root exit is a substitute.

Its [standard collector partition and binding](../issue779-checkpoint4/measurement/README.md#separate-sdk-failed-test-partition) has its own finite allowance. It leaves the original matrix's clocks, runtime limits and retention ceilings unchanged. The negative report is completed and all fourteen SDK originals are restored before the original matrix can consume that report through standard merging. This new case remains unverified until a fresh genuine Linux run; portable reader/model tests grant no native or Trusted authority.

The separate [post-stop SDK control](post-stop-guards-source-preparation.md) checks real closed work/write admission after joined cleanup. It uses a private compile-only selector and remains uncredited until fresh Linux execution; ordinary producer limits and the original seven-case matrix are unchanged.
