# Four-selector measurement chain

`measurement_chain.py` is private artifact data validation and custody. It never
executes a collector, native procedure, merge, or product command, and never edits
report counters. The measured order is **None → N04 → N07 → N16**, accounting for
six case rows (N01/N02/N03/N04/N07/N16). The independent
[original C1 all16 workflow](../workflows/issue779-checkpoint1-worker.yml)
remains required; this chain cannot replace it or claim sixteen measured cases.

## API and ordering

Run `python3 -B .github/issue779-checkpoint1/measurement_chain.py prepare` with:

- `--input ABS_DOWNLOAD_ROOT --input-artifact EXACT_NAME`
- `--source-capture ABS_CURRENT_CAPTURE --source CURRENT_HEAD`
- `--selector None|N04|N07|N16`
- `--workflow-identity github-actions-RUN-ATTEMPT --output FRESH_ABS_DIR`

None requires `issue779-csharp-checkpoint2-RUN-ATTEMPT`. Later selectors require
`issue779-c1-measured-PREVIOUS_SELECTOR-RUN-ATTEMPT`, with the previous chain at
`INPUT/chain`. Preparation emits exactly `source.json`, unchanged
`prior-report.json`, canonical `prior-binding.json`, and `lineage/`. It rejects
wrong source bytes, run/attempt, artifact name, selector order or incomplete links.

The external [measurement wrapper](measurement.py) runs its unchanged
bind/collector-target/finalize sequence using these prior leaves. It binds actual
instrumentation, original native validation and restored-image verification; the pinned
collector alone owns instrumentation, counter collection, merge and restoration.
Each selector has its own VM and original procedure clocks. Standard Coverlet
10.0.1 alone merges the completed predecessor JSON. There is no ReportGenerator
pre-merge, XML substitution or worker-hit import here.

After that wrapper succeeds, use the same common arguments with `finalize`,
`--input ABS_PREPARED_PRIOR --evidence ABS_MEASUREMENT_ROOT`, and
`--output ABS_MEASUREMENT_ROOT/chain` (fresh). `--input-artifact` belongs only to
prepare. The final chain contains `source.json`, unchanged current `coverage.json`,
canonical `chain-binding.json`, and retained `lineage/` raw leaves.

Gate staging calls:

```python
coverage_path = validate_final(source_raw, downloaded_artifact / 'chain',
                               'github-actions-RUN-ATTEMPT', deadline=original_deadline)
```

This requires all four links, all six rows once, raw earlier prefix records,
report/native/plan/evidence/command-log hashes and source-bound data helpers. The
returned path is the final N16 standard JSON. Compare
`chain-binding.json['predecessor']['report_sha256']` with the independently staged
current C2 `native-producer-roots.json`: **this field always identifies initial
C2**, never immediate N07. No original C2 report is merged twice by this helper.
The independent existing C3 → Accounts → Rollback → C2 gate verification still
owns predecessor semantics beyond the carried C2 endpoints.

## Validation and bounds

The fixed captured `producer_roots.py` native/report/witness validators are loaded
only after checking their source bytes and fixed imported helper against the
current full capture. C2 requires v2 eligibility, matching external/context
predecessors, current run artifact naming, all three numeric zero exits, exact
12-file maps for both original/instrumented/restored images, actual seven native
rows including the intentional quarantined cleanup failure, raw case/owner logs,
staged inventories and both current witness arms. C1 links require original v1
publication plus measured v2, the fixed selector, six-module standard reports,
wrapper command argv/log hashes, native plan/result/execution/evidence and
restoration observations. Pinned Coverlet keys collected modules by DLL basename;
no alternate 12/24-module geometry is silently accepted.

All artifact reads are stable single-link regular files through no-follow parent
FDs. Every existing input tree and every outgoing tree is bounded to 1,024 files,
512 directories, 192 MiB total; report leaves have a 32 MiB maximum and other leaves
8 MiB. The exact original archive basenames `native-private.tar` and `native.tar`
retain their existing 33,619,968-byte maximum (also inside lineage); no repacking
or truncation occurs. Outputs are exclusive, directories 0700 and files 0600. Failure retains any
partial output without publishing a success. There is no truncation, deletion,
permission change to inputs, or reuse to satisfy limits. Cumulative raw lineage
uses a dedicated fixed 192 MiB/1,024-file contract, selected by
`check-retention.py --measurement ROOT`. The ordinary all-sixteen lane retains
96 MiB/512 files. Source56 size projection with four cumulative reports, raw
lineage and duplicated current-stage reports exceeds the ordinary total; no
required bytes are omitted. Exceeding the measurement total still fails closed.

This validates retained data, not live kernel ownership or native authorship.
Restoration is a hash-bound observation from the genuine wrapper, not a second
rehash of binaries on a different VM. Original raw archives/evidence are retained;
this helper does not rerun every family's native predicate. All16 native results
and existing C2/C4 semantic checks remain separately mandatory. Every record has
`native_acceptance:false`, `coverage_credit:false`, and `all_sixteen_claimed:false`.

## Portable controls and remaining proof

`test_measurement_chain.py` uses detached records and ordinary owned files. It
covers fixed prefixes/links/authority, raw map mutations, path/run grammar,
unchanged-byte private publication, no-adoption, symlink/hardlink/FIFO rejection,
expiry, bounds and source-before-import binding. Passing these controls grants no
native or coverage credit. Fresh source-bound Linux execution of every selected
collector and the unchanged full gate remain required before integration credit.

### Source-preparation validation status

The initial bounded preparation run returned **exit 1: ten methods, seven passed,
three errors** (unittest 0.027 s; wrapper 0.141 s). All three errors selected
noncanonical macOS temporary-path aliases (`/var` versus `/private/var`). The
fixture roots were corrected with `Path(...).resolve()`; the canonical-path guard
remains unchanged. That failed execution remains historical evidence.

The subsequent custody correction requires the slot selectors' actual original
`native/native-private.tar`, its SHA and member projection, and every declared
retained leaf. N16's embedded retained data must match the corresponding actual
files. Both utility commands must include both fixed stdout/stderr streams,
including empty streams. Deletion or tampering after wrapper finalization is a
closed failure before chain publication.

The corrected `MeasurementChainDataTests` suite completed **12/12 passed, exit 0**
(unittest 0.054 s; wrapper 0.186192 s, under its 30-second bound). These are pure
records and ordinary owned-file controls, not native execution or a completed
four-runner chain. Fresh Linux execution, independent retained-data verification
and the unchanged full coverage gate are still required. No capacity-clear claim
is made for cumulative lineage beyond the explicit retention bounds above.
