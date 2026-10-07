# C# native supervision checkpoint

This private workflow builds and runs the ordinary CLI with the reviewed
[C# supervision core](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md).
The [migration plan](../../docs/plans/issue-779-csharp-supervision-migration.md)
requires real N01/N02 first and all sixteen native controls before producer,
application or production cutover work.

## Source and prerequisites

The source is 5d1036b7035a7d9e980cdab5afc88879fca5048c, direct parent
2993dcfaac1b9b6dfb8adf057191f837876f01fe. The actual source merge is
98be4042d47fc6f7e55f0fcdac3e26cc8131ebca. All 2,819 source hashes and modes
are bound by the capture receipt and checked around the build. The
[source review](source-review.json) distinguishes the historical 997 local
baseline from the 28 focused STOP/WAIT tests; these counts are not additive.
Its pending-at-preparation field records the original handoff status.

The SDK is 10.0.401. Execution uses the exact Ubuntu 10.0.12 packages and
frameworks, recorded by [the prerequisite](prepare-ubuntu-runtime.py).
All runtime ELFs and their dependencies remain subject to the same-host
[OS audit](prepare-os-audit-v2.py). Source hashes are verified before interpreter
use. No runtime file is skipped, feature disabled or foreign library supplied.

## Staging and capture

[The dispatcher](run-native.py) applies the existing 256 MiB payload-file
limit to staging. Each stdout/stderr capture writer has its own 8 MiB limit,
exclusive private log creation and core output disabled. Fixed private FIFOs
join the writers to the fixture. Each child clears the parent EXIT trap before
setup. The parent retains both PIDs, attempts both joins, and removes only
FIFOs it successfully created. Fixture, pump, cleanup and containment failures
remain failures. All waits use the prearmed root utility's original deadline.
The fixed audit adds only mkfifo and rm at its existing trusted OS locations.

## Diagnostic and acceptance limits

[The fixture](checkpoint-n01-n02-v5.sh) adds fixed internal stage labels and a
best-effort closed stage/tool/exit marker before its existing bounded-operation
rejection. It adds no paths, argv or exception text. Marker failure cannot
replace the original rejection. The latch is per shell process, not a global
claim. The earlier failing child remains unknown.

Root containment of validation tools does not prove supervisor or worker
settlement. Existing native process credentials, cgroups, source custody,
canonical files, NSS cleanup, deadlines and receipt predicates remain required.
Preparation and local pipe tests establish no native case pass, qualification
or Trusted admission. Production catalogue and proof registry remain closed.
