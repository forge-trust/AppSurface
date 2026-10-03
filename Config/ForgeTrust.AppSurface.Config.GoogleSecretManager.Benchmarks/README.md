# Mapped Google Secret Manager fake-client proof and benchmark

This standalone .NET console project exercises the real mapped Google configuration provider through the public
`IConfigManager` path and replaces the Google client with an in-process fake. It never creates a Google client, reads
Application Default Credentials, opens a network connection, or prints synthetic values, logical keys, or resource
names.

From the repository root, run the mapped proof:

```bash
dotnet run --project Config/ForgeTrust.AppSurface.Config.GoogleSecretManager.Benchmarks/ForgeTrust.AppSurface.Config.GoogleSecretManager.Benchmarks.csproj -c Release -- proof
```

A passing JSON result reports `coldOwnerSameManagedThread: true`, one cold client call, zero additional warm-cache calls,
and `sameResourceJoinersSharedOneCall: true`. The proof holds the fake client call open while same-resource callers
pass a start gate, signal readiness, and enter the shared lookup. It waits for every post-gate readiness signal and then
confirms every joiner is blocked before releasing the owner, so the shared-flight assertion cannot mistake the start
gate for the flight wait. It verifies all callers receive the same synthetic outcome and checks that the winning public
configuration call and fake client use one managed thread. It exits nonzero if an assertion fails. `proof` accepts no
additional arguments; extra arguments exit with code `2` and print usage. It does not test internal
token-bearing cancellation or audit scheduling; those contracts are covered by the provider's focused tests and
documented in the [Google provider reference](../ForgeTrust.AppSurface.Config.GoogleSecretManager/README.md#mapped-lookup-scheduling).
This is a candidate proof: on the pre-change `origin/main` implementation, it is expected to exit nonzero only because
`coldOwnerSameManagedThread` is false; the mapped call, shared-flight, and warm-cache checks still run. The benchmark
command below is the cross-revision comparison workload.

The benchmark command runs nine repetitions each of distinct-resource cold reads, same-resource cold contention, and
a warm-cache control. Its default pinned workload is 16 resources and 16 dedicated resolver threads for the distinct
case, 16 callers for the one-resource contention case, a 50 ms blocking fake lookup, 10 ms sampling, nine repetitions,
a 16-worker ThreadPool minimum, and no cache except in the warm control. Every scenario validates its exact remote-call
count: distinct-cold makes one fake call per mapped resource, same-resource-cold makes one shared call, and warm-cache
makes no call after prewarming. Build first, then invoke the compiled harness
so build or restore diagnostics cannot mix with the JSONL sample stream:

```bash
# In a clean checkout/archive of the pinned baseline commit 4aa8329c76f4279ad319747f9f3c14b9b8de51ef, after copying this sidecar into Config:
dotnet build Config/ForgeTrust.AppSurface.Config.GoogleSecretManager.Benchmarks/ForgeTrust.AppSurface.Config.GoogleSecretManager.Benchmarks.csproj -c Release
dotnet Config/ForgeTrust.AppSurface.Config.GoogleSecretManager.Benchmarks/bin/Release/net10.0/ForgeTrust.AppSurface.Config.GoogleSecretManager.Benchmarks.dll benchmark --revision 4aa8329c76f4279ad319747f9f3c14b9b8de51ef --resource-count 16 --caller-count 16 --lookup-ms 50 --sample-ms 10 --repetitions 9 --threadpool-min 16 > /tmp/issue-819-origin-main.jsonl

# In the candidate checkout based on 4aa8329c76f4279ad319747f9f3c14b9b8de51ef; identify any uncommitted patch in the result record:
dotnet build Config/ForgeTrust.AppSurface.Config.GoogleSecretManager.Benchmarks/ForgeTrust.AppSurface.Config.GoogleSecretManager.Benchmarks.csproj -c Release
dotnet Config/ForgeTrust.AppSurface.Config.GoogleSecretManager.Benchmarks/bin/Release/net10.0/ForgeTrust.AppSurface.Config.GoogleSecretManager.Benchmarks.dll benchmark --revision 4aa8329c76f4279ad319747f9f3c14b9b8de51ef-plus-working-tree --resource-count 16 --caller-count 16 --lookup-ms 50 --sample-ms 10 --repetitions 9 --threadpool-min 16 > /tmp/issue-819-candidate.jsonl
```

The command emits one JSON metadata record, one raw record per scenario and repetition, three elapsed-time summaries,
and one suite-wide thread-count summary. Raw samples include elapsed time, expected and measured fake-client calls,
peak active fake calls, resolver thread count, and sampled `ThreadPool.ThreadCount` and process thread counts. The raw
per-scenario thread arrays are explicitly labeled non-isolated: all scenarios and repetitions run sequentially in one
process, so worker and runtime threads from earlier scenarios can remain. Per-scenario elapsed and active-call summaries
do not contain thread counts. Report only the `suite-summary` ThreadPool and process-thread maxima across the complete
run. The sampler uses one dedicated observer thread; thread counts do not identify which thread performed a lookup.

Each sample validates its expected fake-client calls: `resource-count` for distinct-cold, `1` for same-resource-cold,
and `0` for warm-cache after prewarming. It also reports the observed resolver count, checked against
`min(resource-count, caller-count)` for distinct-cold and `caller-count` for the other scenarios. Call-count or resolver
count mismatches fail the run.
Unknown, repeated, missing, or malformed benchmark options exit with code `2` and emit only the value-free harness error.

To verify that caller count is independent of resource count, run 16 mapped resources on four dedicated resolver threads:

```bash
dotnet Config/ForgeTrust.AppSurface.Config.GoogleSecretManager.Benchmarks/bin/Release/net10.0/ForgeTrust.AppSurface.Config.GoogleSecretManager.Benchmarks.dll benchmark --revision caller-count-verification --resource-count 16 --caller-count 4 --lookup-ms 50 --sample-ms 10 --repetitions 1 --threadpool-min 16
```

The `distinct-cold` sample must report `logicalResourceCount: 16`, `callerCount: 4`, `resolverThreadCount: 4`, and
`measuredRemoteCalls: 16`; the same run also checks one same-resource cold call and zero warm-cache calls. Distinct
resources are assigned round-robin, so all sixteen mapped reads are exercised by those four resolver threads. If caller
count exceeds resource count, the excess callers have no distinct-cold read and the effective resolver count is the
resource count.

Run that exact workload once against the pinned baseline and once against the candidate, using the same harness source
and workload settings in each revision. For a baseline checkout that predates this sidecar, copy this directory into
the baseline checkout's `Config` directory before running the command there. Record both full commit IDs and the
harness source SHA-256 in the [value-free result template](results-template.md), and preserve the complete, unedited
JSONL stdout from each run as the raw sample artifact. A candidate measured from an uncommitted worktree must be
identified by its base commit and provider patch fingerprint in the template; it is not a separate candidate commit.
Calculate the harness fingerprints with `shasum -a 256` over `Program.cs`, the `.csproj`, and `packages.lock.json` in
this directory.
Do not compare runs with different runtime, processor count, ThreadPool minimum, fake latency, sampling interval, cache
state, caller/resource count, or repetition count. The issue's adopter inputs (distinct startup resources, maximum
concurrent resolutions, and end-to-end startup duration) have not been supplied; label this workload provisional until
calibrated. See the [captured baseline/candidate results and raw samples](results/issue-819-comparison-2026-10-01.md),
including the [16-resource/4-caller verification samples](https://github.com/forge-trust/AppSurface/blob/1843abd85e97c21ea5830cd78b300c567ba32495/Config/ForgeTrust.AppSurface.Config.GoogleSecretManager.Benchmarks/results/issue-819-caller-count-4-verification-2026-10-01.jsonl).

This fake workload measures the harness and scheduling shape under controlled synchronous calls. It is not a production
startup measurement and does not establish a latency improvement or production speedup. Report the raw samples even
when elapsed time does not improve.
