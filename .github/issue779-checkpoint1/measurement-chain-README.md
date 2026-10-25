# Segmented fourteen-selector measurement chain

[`measurement_chain.py`](measurement_chain.py) validates private artifact data and
custody. It never executes a collector, native procedure, merge, or product
command, and never edits report counters. The version 2 fixed order is:

```text
None → N04 → N05 → N06 → N07 → N08 → N09 → N10 → N11 → N12 → N13 → N14 → N15 → N16
```

`None` measures N01/N02/N03; each subsequent selector measures its single named
case. A complete chain contains sixteen measured case rows. The independent
[original C1 all16 workflow](../workflows/issue779-checkpoint1-worker.yml) remains
required. Retained-data validation is not native acceptance or a coverage verdict.

## Why segments are retained separately

Each selector publishes its own authoritative artifact containing complete raw
JSON and Cobertura reports, native evidence, and wrapper receipts. Its `chain/`
segment carries the unchanged C2 seed and only that selector's full node. Earlier
selectors' full nodes remain in their own artifacts instead of being copied into
every later artifact. No report or native leaf is dropped, truncated, recompressed,
or replaced with a digest-only projection to meet a retention limit.

Gate validation receives a fixed set of fourteen separately retained artifacts and
rereads every segment. The 192 MiB limit applies to each artifact, not to the
combined fourteen-artifact download set. There is no monolithic fourteen-stage
export.

The [standard wrapper](measurement-README.md) preserves the instrumented
Supervision DLL/PDB pair, two root-only filters, original procedure bodies, fixed
native environment, clocks, joins and restoration checks. Standard Coverlet alone
performs each sequential `--merge-with`. The first stage consumes C2; every later
stage consumes only its immediate predecessor's completed JSON. The gate selects
the final N16 JSON once. This helper never merges reports or changes counters.

## Segment and record contracts

The record schema is `issue779-c1-measurement-chain-v2`. Old cumulative v1 records
remain historical and cannot satisfy the segmented contract. Each outgoing
segment has these fixed roles:

```text
chain/
  source.json
  coverage.json
  chain-binding.json
  lineage/c2/<authoritative raw seed leaves>
  lineage/selectors/CURRENT/<complete current node>
  lineage/selectors/CURRENT/prior-binding.json
  lineage/selectors/CURRENT/prior-report.json
```

The current node retains both JSON and Cobertura XML, source/build/plan/result/
execution records, original and measured image maps, command logs, raw archives
and retained native leaves. Its prior leaves are the exact raw immediate
predecessor record and report. None instead binds the initial C2 predecessor.
Top-level `coverage.json` is the unchanged current standard JSON.

The record carries the full ordered prefix, cases and links. Its `files` map
binds only lineage actually present: C2 and the current full node. The retained
prior record's map describes the prior segment, not native bytes present in this
segment. Earlier actual artifacts are still required at the gate.

Wrapper target/final bindings retain the version 2 contract in the
[per-selector reference](measurement-README.md#existing-private-image-data-controls).
N04, N05, N07, N08, N10, N12, N13, N14 and N16 retain their fixed `data-tests/` leaves and validate the
source-bound receipt and actual TRX. These tests run after the original native
procedure in the same collector session and add no native case IDs. Other
selectors require a null data-receipt hash and no supplemental leaves.

## Preparation and publication

Run `python3 -B .github/issue779-checkpoint1/measurement_chain.py prepare` with:

- `--input ABS_DOWNLOAD_ROOT --input-artifact EXACT_NAME`
- `--source-capture ABS_CURRENT_CAPTURE --source CURRENT_HEAD`
- `--selector SELECTOR`, from the fixed order above
- `--workflow-identity github-actions-RUN-ATTEMPT --output FRESH_ABS_DIR`

None requires `issue779-csharp-checkpoint2-RUN-ATTEMPT`. Later selectors require
`issue779-c1-measured-PREVIOUS_SELECTOR-RUN-ATTEMPT`, with the actual previous
segment at `INPUT/chain`. Source, run, attempt, artifact name, order and links
must agree.

Prepared input contains `source.json`, unchanged `prior-report.json`, raw
`prior-binding.json`, and `lineage/c2/` seed bytes. After None it additionally
contains `previous/`, the complete actual prior segment. This permits finalize
to repeat validation of the actual immediate predecessor's native data. A digest
alone is insufficient. `previous/` is temporary input and is never exported.

The external [measurement wrapper](measurement.py) runs its unchanged bind,
collector-target and finalize sequence against these prior inputs. After success,
use `measurement_chain.py finalize` with the same source/selector/workflow
arguments, `--input ABS_PREPARED_PRIOR --evidence ABS_MEASUREMENT_ROOT`, and
`--output ABS_MEASUREMENT_ROOT/chain` (fresh). `--input-artifact` belongs only to
prepare. Publication checks the current full node and actual immediate previous
segment before emitting only C2 and current-node lineage. Reports and evidence
bytes remain unchanged.

## Validation APIs and proof boundaries

`validate_record_fields(value, source_raw, head, identity, prefix)` checks exact
record fields, schema/source/workflow/prefix identity, authority flags, ordered
link hashes and finite manifest metadata. It reads no previous native files and
does not establish raw custody. `validate_record(value, source_raw, head,
identity, prefix, files)` additionally compares the manifest with the exact
supplied byte map. That comparison still does not replace native-data validation.

`verify_segment(source_raw, raws, identity, prefix, deadline)` returns the current
record after checking the C2 seed, complete current native node, exact segment
inventory and fixed prefix. It structurally checks the retained prior record and
binds the raw prior report. This does not claim native revalidation of every
earlier segment.

Gate staging calls:

```python
coverage_path = validate_final(
    source_raw, segments_root, 'github-actions-RUN-ATTEMPT',
    deadline=original_deadline,
)
```

`segments_root` contains exactly fourteen fixed selector directories, each the
corresponding downloaded outer artifact with its actual `chain/` beneath it.
Missing or extra selectors, substituted directories and incomplete segments
reject. Validation processes the fixed order and:

1. Revalidates every actual segment's full current native/report/image/command
   data using current source-bound helpers.
2. Compares each current raw prior binding and prior report byte-for-byte with
   the preceding actual segment's record and JSON report.
3. Requires identical C2 raw seed bytes throughout, the same source and workflow
   identity, full ordered prefix links, and all sixteen measured case rows.
4. Returns the actual N16 `chain/coverage.json` unchanged for the single gate
   merge input.

Rehashing a fabricated predecessor record cannot replace missing actual earlier
native bytes. A reset to C2, extra baseline link, reordered stages or links,
another source/run/attempt, or substituted previous report rejects.
The initial predecessor's `report_sha256` always identifies C2, not the immediate
previous selector. Independent C3 → Accounts → Rollback → C2 gate verification
still owns its complete predecessor semantics.

## Custody and bounds

Fixed producer-root native/report/witness validators load only after checking
their source and imported helper bytes against the full capture. C1 retains
publication and measured maps, native plan/result/execution/case evidence,
original callbacks and raw custody, wrapper argv/logs, selected data-test
receipts, and restoration observations. Reports keep the seven-module geometry
and original counters.

Reads require stable single-link regular files through no-follow parent FDs.
Each artifact retains the existing 192 MiB / 1,024-file / 2,048-node measurement
retention contract through [`check-retention.py --measurement`](check-retention.py).
Internal tree and directory scans retain their own finite bounds. The ordinary
all-sixteen lane keeps its original 96 MiB / 512-file contract. The fixed
fourteen-artifact set has no claimed global 192 MiB bound.

Report leaves remain bounded to 32 MiB and ordinary chain leaves to 8 MiB.
Exact `native-private.tar` and `native.tar` basenames retain their existing
33,619,968-byte maximum. No repacking or truncation occurs. Outputs are exclusive,
directories 0700 and files 0600. Failure preserves partial output without adopting
it or publishing success. No required leaves are deleted, input permissions
changed, or deadlines renewed to satisfy limits.

These APIs validate retained data, not live kernel ownership or native authorship.
Restoration is a bound observation from the genuine wrapper, not a new binary
rehash on another VM. Authority remains `native_acceptance:false`,
`coverage_credit:false` and `all_sixteen_claimed:false`, including a complete
sixteen-row measured chain. Original all16 controls and the unchanged gate remain
required.

## Portable controls and validation status

[`test_measurement_chain.py`](test_measurement_chain.py) uses detached records,
explicit transition seams and ordinary owned files. Transition fixtures establish
ordering and orchestration only; they supply no positive kernel result. Existing
raw archive and retained-leaf custody, stable-node, exclusive publication,
no-adoption, expiry, bounds and source-before-import controls remain necessary.
Segmented controls also reject missing/extra stages, wrong order, digest-only
forged predecessors, incomplete previous native data, repeated baseline admission,
changed source and inconsistent raw predecessor bytes.

The focused version 2 source-preparation run executed
`python3 -B -m unittest test_measurement_chain.MeasurementChainDataTests -v` once:
**22/22 tests passed, exit 0** (unittest 0.641 seconds; command 0.766 seconds,
within a 40-second command timeout and 45-second outer bound). Implementation,
custody-helper and test hashes were unchanged throughout that command. The child
was joined and its owned process group was absent. A preceding source-hash
preflight found the not-yet-created custody helper and launched no test process;
that preflight failure remains preserved separately.

The v1 suite's historical 12/12 result and earlier failed temporary-path run apply
only to that earlier source. The new result establishes detached record/file
behavior at the captured source hashes. Genuine selected Linux procedures,
independent retained-data verification and the unchanged gate remain required.
No coverage gain, live kernel fact or fourteen-artifact capacity proof is inferred
from design or detached fixtures. Later source changes require their own bounded
validation before integration credit.
