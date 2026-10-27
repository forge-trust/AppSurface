# Completed C1 measurement-chain data

[`completed_measurement_chain.py`](completed_measurement_chain.py) revalidates the
fixed fourteen retained measured segments after [`completed_run.py`](completed-run-README.md)
stages the closed completed-run artifact inventory and
[`completed_validation.py`](completed-validation-README.md) parses its structural
ZIP projections. It is a retained-data projection. It does not run a collector,
native producer, merge, or protected admission flow.

The public internal entrypoint is:

```python
projection = run_completed_measurement_chain(
    stage, inventory, stage_directory, source_root, staging, deadline=deadline)
```

`stage` and `inventory` must be the matching completed-run records. `stage_directory`
contains the exact staged ZIP set, `source_root` is a clean checkout matching the
captured source, and `staging` is a fresh private directory disjoint from both.
The caller supplies one finite monotonic deadline; the helper never renews it.
It verifies the full source capture before loading the captured original
[`measurement_chain.py`](../issue779-checkpoint1/measurement-chain-README.md).

The original order is `None`, then `N04` through `N16`. Every one of the fourteen
ZIPs is checked against its full structural projection and staged provider digest.
The helper reads and CRC-checks the top-level `source.json` and the complete
`chain/` payload. It does not read or CRC-check non-chain ZIP payloads; this
projection makes no claim about those unrelated members. Per segment it invokes
the source-bound `verify_segment` routine, which checks the full current native,
report, command, image, retained-data, and C2 witness content under the original
192 MiB, 1,024-file, and 2,048-node segment limits.

Segments are extracted one at a time to stay within the fixed 512 MiB working
budget. Before extraction, the bound includes the complete staged archive bytes,
the currently opened ZIP, twice the declared `chain/` expansion (one on-disk tree
plus the bounded raw-byte scan), a maximum member buffer, the carried raw C2
seeds, immediate prior record and report, and source capture. After validation,
the temporary segment is removed by inode-owned cleanup before the next ZIP is
opened. The next segment must contain byte-identical raw C2 seed files and
byte-identical immediate predecessor binding and report. Hash-only predecessors
do not pass. No stage is merged, rewritten, or substituted.

Before the original segment validator runs, the helper installs the captured
`binding`, `measurement_arguments`, `publish`, `measurement`, and
`measurement_custody` modules in a temporary import scope. It restores the
caller’s prior module cache and `sys.path` after success or failure. The source
capture is checked against the checkout’s current `HEAD` and tree before these
source modules load.

The returned `CompletedMeasurementChainProjection` contains the unchanged raw
N16 `chain/coverage.json` bytes and their SHA-256, run/attempt/source identity,
and fixed selector/case order. It sets `native_acceptance`, `coverage_credit`,
and `all_sixteen_claimed` to `False`. Those flags do not indicate anything about
protected admission, trusted manifests, or gate eligibility. A later caller may
use the bytes only under its separately defined workflow.

[`test_completed_measurement_chain.py`](test_completed_measurement_chain.py)
tests structural projection matching, selected-member extraction, cleanup, raw
predecessor equality, and the fixed nonclaims. Its fixtures are ordinary data;
they do not establish a successful fourteen-segment native chain.
