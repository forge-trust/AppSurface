# Source64 N04 diagnostic replay

This isolated validation workflow checks out immutable source `7d8873f2106aa217d453b9f3b02625ee7165cc61`. It repeats the existing N04 source, build, native procedure, supplemental tests, collector and finalizer with their original bounds. It retains bounded private Git status and raw-diff metadata immediately before and after measurement, without modifying the source tree or replacing the original command exits.

The predecessor is the actual successful None artifact from run `39885837229`, validated using that original run identity by the unchanged source-owned chain validator. The new native N04 replay uses its new workflow identity. No chain finalizer or coverage gate is invoked. The resulting diagnostic artifact cannot qualify a current sixteen-control or consumer chain, and its name is deliberately outside the current measured-artifact prefix. No threshold, coverage filter, production execution policy, runtime limit or acceptance predicate is changed.

The raw status/diff records are private diagnostics and are not echoed in public job logs. The workflow preserves nonzero collector/finalizer/source-check results. Missing diagnostics do not convert failure into acceptance.

## Controlled SDK-data comparison

The first replay measured a clean checkout before collection and one new `.local/share/NuGet/Migrations/1` afterward. The comparison replay supplies a fresh root-owned mode-0700 absolute `XDG_DATA_HOME` to the outer validation collector/SDK environment. The unchanged native dispatcher replaces its environment with its original fixed native environment, so no worker, subject, native clock, sandbox, or acceptance predicate changes. Both private status snapshots and original failure exits remain. This comparison is diagnostic only and is not a current qualification chain.
