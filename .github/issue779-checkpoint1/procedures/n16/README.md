# Current-source N16 accepted-work control

`adapter.execute(context, *, deadline)` follows the shared [C1 adapter contract](../../README.md).
It requires Linux root and the dispatcher's fresh existing five-input output. It
verifies the raw current source/build receipt and every fixed module before using
`owner-death.adapter.prepare_verified_inputs(..., source_sealed=False)`. The
retained source keeps original file modes and 0700 directories because the original
N16 shell owns its later seal. Tool/runtime copies are always sealed 0444/0555.
No N13/N14 request or materialization policy is reused.

The actual metadata-only `Audit` library inspects the complete sealed runtime/tool
and fixed OS, ELF, alias and Python stdlib graph under its original maximum 60s
bound. Its historical library predicates are unchanged. It is called directly by
the root dispatcher to inspect root-private copies; the old unprivileged CLI and
unrelated embedded historical build-contract adapter are not used.

The fixed historical N16 shell runs through the retained utility runner with its
independent root guard/timer, process identity, stop/join and cgroup checks. Its
600s fixture, 30s cleanup, 240s root unit, request budgets and actual accepted-work,
worker identity, stream, NSS, unit retirement and custody predicates remain. The
source/build prelude validates real current receipts and truthful retained maps;
no old schema, review flags, command facts or TRX is manufactured. The six fixed
worker/root environment entries include the current `XDG_RUNTIME_DIR=/tmp` contract.
The fixed custody reader was decoded once to readable Python with the same bytes
and is pinned before every use.

After physical completion, the adapter reads only eight fixed native/private
leaves plus the actual bounded utility command record and two logs. It verifies
root ownership, 0600, single links, retained-FD stability and byte bounds; raw bytes
are copied unchanged where necessary. `N16-evidence.json` embeds those exact bytes,
the original build receipt, current hashes and truthful retained inventories.
`validate_evidence` invokes the original N16 record parser and retained request,
identity, terminal, twelve exact tree-output and cleanup predicates.
`verify_retained_evidence` independently re-reads every retained leaf and reapplies
that parser. No arbitrary path, executable, callback, runtime selector or native
acceptance flag can be supplied to this bridge.

`port-manifest.json` and the adjacent source delta record the historical source
and current bridge. Source preparation has not executed N16. Detached data tests
are metadata controls, not a native owner/worker claim; fresh Linux execution is
required before acceptance can be reported.
