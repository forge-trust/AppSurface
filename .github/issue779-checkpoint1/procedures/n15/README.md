# N15: pending worker start, owner death and consumed-guard replay

This is the actual current-source port of the historical N15 procedure, under the
[shared C1 adapter contract](../../README.md). It is source-only until independent
review, portable validation and a fresh Linux run. Detached test records establish
no native owner, account, worker, acceptance or coverage credit.

## Fixed adapter API

`execute(context, *, deadline)` requires Linux root, the exact shared context, and
the existing root-owned 0700 output containing the five dispatcher input files. It
verifies full current source/build/map/log data and every fixed source module before
using the shared current input materializer. No helper image is required. The OS
audit uses the real runtime/tool plus the same actual empty audit root as the
historical N15 runner; `initialize_tools()` runs before `Audit.build()`.

The materializer in `materialize.py` reuses the shared original sealed-copy and
custody functions, fixed empty-observation policy/request and actual current Git
parent. It does not call the N13/N14 materializer that samples a new clock. Instead,
N15's original job clocks are sampled once at the beginning of `execute`, before
its verification and staging:

- job: 240 seconds, sampled in monotonic, boottime and UTC domains;
- work: original job expiry minus the original 60-second cleanup reserve;
- startup: original work expiry or one Start30 window, whichever is earlier;
- the legacy observer's pre-admission ABI shares that same Start30 expiry; it does
  not introduce an Admission10 renewal or change the request's Admission10 value.

All fixture operations stay within those original expiries and the dispatcher's
original outer deadline. Native staging, slow preparation or a late phase cannot
renew a clock or turn failure into a skip/pass. The shell derives owner and replay
unit runtime from the original remaining budget using the historical reserves.

## Actual native chain

`checkpoint-n15-pending-start-death.sh` runs the fixed original owner launch,
`observe_n15_pending_start.py`, consumed-guard replay and
`finalize_n15_observation.py`. The observer retains original worker and root owner
PID/starttime/UID/GID/cgroup samples, account snapshots, the original pidfd signal,
terminal service metadata, physical cgroup emptiness and unit reference lifetime.
The replay preserves the one-generation consumed guard and rejects a new start
phase or successful root exit. The finalizer independently checks actual retained
raw snapshots, original launch join, account identity/reuse, generated utility
results and numeric/name absence before assembling the original measured record.

The only native-chain port changes are current source/parser pins, explicit current
source/capture provenance passed by the already-verified root controller, and the
current sixth fixed environment entry `XDG_RUNTIME_DIR=/tmp`. Signal, kernel,
identity, group, EOF, stop/join, guard, request and cleanup predicates are retained.
The copied finalizer has all 18 non-main functions unchanged; all 29 observer
functions/classes are unchanged. The finalizer's main adapts current provenance
and the existing source-parameterized parser API, without adding admission data.

N15's original utility runner is retained locally in `process.py`: its 11 extracted
definitions are AST-equal to the original, including explicit no-follow/exclusive
0600 diagnostic stream creation and independent utility guards/timers/group join.
`retain-native.sh` is byte-identical. Original dispatch/FIFO joins and canonical tar
checks are retained in `records.py`. This local runner avoids substituting the
older shared runner's stream-open implementation.

## Retained evidence and failures

`verify_retained_evidence(context, *, deadline)` is the mandatory CLI callback.
It re-reads the actual finite `native.tar`, both utility command JSON records and
both stdout/stderr files for each command. It checks raw hashes/sizes, exact two
stream membership, exit/timeout/guard/group/join facts and the original canonical
archive grammar. It then re-runs the original partial/final phase, replay,
worker-snapshot, source binding, request/deadline and account cleanup predicates.
`N15-evidence.json` is only an envelope binding those bytes, not proof on its own.
The case row has exactly the shared five fields and credits only N15.

The diagnostic retainer is attempted after actual dispatch failure under its
original bounded retention interval. It cannot erase the first fixture exception
or publish success. Partial sealed roots and private generations are retained on
failure; no fabricated historical build receipt, command, TRX, completion flag or
native acceptance is introduced. Source/data changes require a new full capture
and explicit review of the fixed predicate/pin delta.

`test_data.py` defines portable metadata controls only; it performs no root/NSS,
worker, native process or subprocess operations. These controls were not executed
during this source preparation. `port-manifest.json` records historical/current
hashes, exact-source deltas and the static preservation checks.
