# Private product coverage ownership wiring

This recovered source handoff wires the internal root launcher to the independently
owned `product-coverage.py`. At the initial source handoff the four failure-only controls were defined and unrun. The later parent-owned local run passed all four; this is portable failure-path verification. Real three-library integration has not run from these
recovered bytes. The parent reported the earlier actual native mechanism PASS;
this source handoff does not independently reverify that artifact. That mechanism
result is separate evidence and is not a product coverage, qualification, admission
or Trusted claim.

The [private qualification procedure](README.md) retains its compiled None/None
result handling and empty production catalogue/proof registry. The worker's existing
writable anchor and unit properties are unchanged. No descriptor field, environment
switch, public callback or wire request can choose a coverage owner or grant a path.

## Fixed root API and layout

The [launcher](../../../scripts/evidencehost-linux-launcher.py) imports exactly the
physical sibling qualification module `product-coverage.py`, with no search fallback.
Main owns that module and the controller/build preparation. The launcher calls
`ProductCoverageOwner(tool, worker_anchor, uid, gid, deadline, dotnet, taskhost_dir,
report_dir, worker_unit)` only after creating the actual accounts, worker anchor and
original monotonic job deadline. The independent root watchdog directly owns the
real public Coverlet task host; no launcher-held pre-host interval can substitute
for that ownership.

The task host input is the fixed protected `tool.parent / "product-coverage-taskhost"`
directory. Root report output is `outer_output_parent / ("product-coverage-" + tag)`.
The controller must prepare that exact input layout and collect that exact root output.
The launcher creates the report directory fresh, pins it no-follow, verifies root
ownership and fixes mode 0700 before owner preparation. The owner must validate and
pin both paths. The writable tmpfs session belongs strictly
under the actual existing worker anchor; report placement adds no worker grant.
These are internal layout requirements, not command-line options.

`prepare()` returns the real instrumented `execution_map` before tool pinning or
worker start. The launcher calls `bind_tool_pin()` after its existing worker-group mode change and before dispatch. The owner validates the actual per-file formulas separately from instrumented byte identities. Root baseline/restored hashes
and instrumented execution hashes must remain distinct. The launcher stores a bounded
JSON copy (at most 1 MiB, no non-finite values) when transferring completion, and
`completion.execution_map` returns a defensive copy. It does not authenticate reports.

## Dispatch and physical settlement

The root registers the generated worker unit, rejects `live_abort() is True`, marks
worker dispatch before the actual start, and verifies the live worker PID before
creating its descriptor or serving ready. The exact retained owner is passed to
`Broker` and `bind_broker(broker)` runs before request handlers start.

`Broker._before_product_dispatch(unit)` registers the actual generated producer or
application unit outside the broker lock and rejects an owner abort. Producer Popen
still uses the existing closed-gate check under the broker lock; application start
keeps its own existing locked checks. Registration and an abort observation are not
an atomic dispatch permit. The independent watchdog must prevent/settle a late spawn,
including by physically terminating a surviving root parent before concluding unit
settlement. Launcher source wiring alone does not prove that watchdog behavior.

Only the existing protocol/handler/pump and aggregate physical-exit checks, followed
by the checked completed-worker stop, precede `confirm_owned_exit()`. On failure,
stop, wait, checked systemd stop, exit inspection, owner abort, channel cleanup and
completion closure are independently attempted. Exit-query failure never confirms
exit. `abort_after_owned_exit()` runs even after a failed query and must quarantine
uncertain consumers; it cannot assume that its method name proves physical exit.
The first cleanup error is retained, and an original launch error has priority.

## Collection and account release

`_finish_launch_transfer` closes launcher channels and application workspace before
attaching the exact owner and transferring accounts. An exception before attachment
still attempts owner abort and closes both completion FDs, preserving the original
exception. An `AbortObserver` cannot pass the exact owner type guard.

The root controller must first collect declared artifacts and close caller-owned
duplicate directory FDs, then call `completion.collect_product_coverage()`. It delegates
to the same actual owner's `collect_and_restore()` under the original deadline. The
owner must consume genuine hits, run official restoration, verify restored bytes and
settle its task host/session. Before completion close, the controller copies reports
from the read-only `completion.product_coverage_report_directory` property, which
returns `.reports` only from the retained exact `ProductCoverageOwner`. The property
rejects a closed completion or an unaccepted owner; it does not replace the controller's
bounded no-follow reader or report validation. There is no fallback hit file, alternate provider or
generated positive coverage claim. Private Contracts/Planner and the top CLI/HostEntry
remain private compiled dependencies; only independently eligible product Cli/Aspire/
Coverage DLL/PDB pairs may be attributed to product sources.

`completion.close()` attempts both retained directory FD closes, then owner close.
Account deletion requires a real retained owner receipt with schema
`issue779-private-product-coverage-close-v1` and exact fields:

| Field | Required value |
| --- | --- |
| `account_cleanup_allowed` | `true` |
| `cleanup_complete` | `true` |
| `consumer_exit_confirmed` | `true` |
| `workspace_quarantined` | `false` |
| `mount_retained` | `false` |
| `failure_category` | `null` (present) |

Missing fields and integer lookalikes reject. Additional diagnostic fields confer
no authority. `_product_close_receipt_allows_accounts` tests data only: a matching
dictionary cannot attach an owner or release accounts. Any FD cleanup error, owner
exception or unsafe receipt retains the accounts and permanently fails completion.
Owner cleanup is still attempted after an FD error; repeated close propagates the
same first failure and never repeats account deletion.

## Failure-only controls and limits

[`test_product_wiring.py`](test_product_wiring.py) defines four controls using real
ordinary directory/socket FDs, an explicitly unaccepted abort observer, and a mocked
account-deletion counter. They check pre-attachment abort/error preservation, exact
owner rejection, actual EBADF retaining accounts while closing the other FD, and
single-field close-receipt rejection beside a valid data-only neighbor. They construct
no root owner, protected lease, admission context, provider or worker capability.

No native run, Git mutation or publication is established by the initial handoff. The later four-control local receipt is kept separately. Main must review the independent owner/controller APIs, validate these
controls, bind the frozen maps and obtain actual mixed qualified execution before
claiming product coverage. Earlier source hashes and deleted receipts are historical;
the recovery packet records newly measured source bytes only.
