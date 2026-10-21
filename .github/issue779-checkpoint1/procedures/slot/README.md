# Current slot and peer fixture bridge

The [C1 dispatcher](../../README.md#fixed-adapter-cli) selects this bridge for
ordinary `None` (N01/N02/N03) and N04–N08. It stages current source and genuine
CLI, runtime and helper images, performs the same-host OS/ELF audit, and runs the
fixed historical native procedures with their original clocks and predicates.
The C# executable remains the production supervisor and worker.

## JSON names and Linux inventory paths

`pairs(items)` and `decode(raw)` in the [bridge](bridge.py) reject duplicate
JSON member names by exact, case-sensitive comparison at every object depth.
They preserve distinct Linux inventory paths such as `Assets` and `assets`;
neither path is renamed, combined or discarded. JSON escape spellings that
decode to the same name still count as duplicates. Receipt, image and packet
schemas continue to require their exact closed field sets, so `Root` is not an
alias for `root`. All existing type, byte, hash, mode and native validations
still apply after decoding.

Source44's verified None publication contained both directory names and was
rejected before native staging by the old case-folding decoder. The
[focused controls](../../test_json_keys.py) retain a small projection of those
actual build-map rows, exercise both bridges, and reject exact duplicates and
outer schema aliases. These are parser/data controls, not a native result.

## Retained evidence API

`execute_case(context, *, deadline)` requires the dispatcher's exact existing
root 0700 output and five root 0600 inputs. `verify_packet(context, *, deadline)`
starts no process and rereads the original private archive, selected member
bytes, both actual command records and both streams for each command. The
internal `validate_packet` requires the actual `archive_raw` bytes and checks
their hash and member projection before applying the original data predicates.
Reconstructing a different archive from JSON is insufficient.

`save_archive(path, raw, *, deadline)` accepts only the fixed
`native-private.tar` leaf under a caller-owned private directory. Its exclusive
no-follow 0600 write retains the opened FD through fsync and file identity/size
checks. The archive's 33,619,968-byte bound is separate from the 8 MiB bound for
an individual member or command log; an existing destination is never replaced.

If the fixture fails and the existing bounded retainer returns its actual archive,
`execute_case` saves those unchanged bytes as `native-private.tar` before
rethrowing the original fixture exception. This failure-only save uses the same
`retain_end` as the archive read; it creates no additional retention allowance.
Expiry, an occupied destination or an I/O error cannot replace the original
fixture failure. No successful evidence packet or case result is issued on this
path. The [failure-retention controls](../../test_failed_fixture_retention.py)
exercise the actual orchestration block and ordinary owned files, not native
execution or protected-owner authority.

`validate_commands(commands)` requires exactly two terminal command rows, each
with one ordinal-specific stdout row and one stderr row. Both rows carry typed,
bounded byte counts and SHA256 values. `verify_command_logs(output, commands,
*, deadline)` validates this complete shape before reopening metadata and both
files, including zero-byte streams. Missing, duplicated or partial log rows
cannot skip a file read. These APIs check data and file consistency; they do not
establish a native outcome independently of actual fixed procedure execution.

## N03 prerequisite schemas

The enclosing None and N07 fixture results contain `n03_result_sha256` and keep
that binding. N04, N05, N06 and N08 have different closed result schemas which
omit the field. Their N03 validators instead bind the actual retained
`logs/n03/result.json` bytes to their decoded projection and digest, while keeping
the original closed result, live image, credentials and continuity checks. The
original archive hash and member projection bind those bytes to the packet.

The [portable regressions](../../test_slot.py) cover the archive size boundary,
exclusive writes, exact stream completeness and all four negative fixture
shapes. Passing them supplies no Linux kernel, admission, Trusted or coverage
claim. Fresh current-source execution of every selected native procedure is
still required before whole-lane aggregation.

## Negative fixture source binding

N04–N08 snapshot `source_revision` into the readonly
`NEGATIVE_SOURCE_REVISION` variable immediately after the fixed current-source
root verification succeeds and before source-input diagnostics or the N03 guard.
The guard compares its live source argument against that snapshot, so later
source-variable changes or attempts to reassign the snapshot fail closed. The
[portable controls](../../test_negative_source_binding.py) extract that exact
comparison from each live script and exercise it with ordinary values under a
nonroot shell; they do not run the native procedure or claim an EUID/native pass.

## N03 prerequisite record framing

The N04, N05, N06 and N08 publishers serialize the original closed N03 result
with `jq -cS`, which includes its terminal LF, before the existing deadline check
and atomic rename. `jq -j` suppressed that LF and made the retained bytes fail
these fixtures' strict data parsers after the native work. The parsers and their
identity, image, EOF and digest checks remain unchanged. The
[publisher regressions](../../test_n08_record_framing.py) exercise all four live
serialization blocks, their partial-write failure path and the strict parser's
missing-LF rejection. They test record framing only and issue no native credit.

## N04 joined-outcome diagnostic

Before the unchanged `N04-owned-outcomes` rejection, the fixed shell now emits
`N04_JOINED_OUTCOMES` with the helper and root launch join statuses, whether each
registration was retired, and whether root stdout was empty. These are existing
numeric results and three Boolean values; no path or raw output is included.
The [diagnostic controls](../../test_n04_join_diagnostic.py) exercise the actual
guard's accepted tuple and each rejected neighbor. The diagnostic neither proves
the inner failure cause nor changes any native acceptance or cleanup condition.


`join_owned` also handles the exit transition between its liveness sample and
its retained start-identity read. An identity-read failure permits the original
exact-child `wait` only after the same deadline is rechecked and both the PID
name and its original process group are absent. A live identity mismatch,
missing registration, surviving group or expired deadline still rejects. The
actual child status is retained, including expected nonzero exits; registration
retirement follows another group/deadline check. No signal, identity substitute
or fresh allowance is added. The [join-order controls](../../test_n04_join_race.py)
force this ordering with detached process metadata and make no native-cause or
physical-settlement claim.

## Setup metadata queries

`pin_file(path, sha256, limit)` in None, N05 and N06 obtains size and link count from
the existing complete before sample, validates its seven typed fields, and keeps
the same file hash and complete after-sample comparison. `ancestor(path)` samples
UID and octal permissions together at every original component through `/`.
`alias_walk` in None, N05 and N06 likewise samples each ordinary component's UID and
permissions together. It retains both complete walks, every deadline check,
symlink metadata-before/readlink/metadata-after checks, and the resolved file's
hash and identity checks. The [N12 procedure](../n12/README.md#metadata-sampling-without-repeated-subprocesses)
uses the same consolidation.

The [metadata controls](../../test_metadata_sample_batching.py) exercise the live
functions with ordinary temporary paths and detached metadata. They check exact
command counts, all traversed components, malformed or unsafe fields, changed
link identity and expiry. The consolidation removes redundant subprocesses; it
does not extend the original 600/30-second fixture budget or change its prelaunch
reserve. Fresh Linux execution must establish whether setup finishes in time.

N05's existing six stage observations use only closed names and the original
elapsed/remaining clock values. They do not renew a deadline or change a guard.
The [stage controls](../../test_n05_stage_observation.py) cover the live helper's
schema, expiry/write failures and its placement around the unchanged commands.

### Bounded alias declaration lookup

None's `initialize_alias_declarations()` runs once after the complete typed OS
audit schema and unique-literal check. It projects at most 64 sorted alias rows
within 1 MiB, checks lexical paths, strict order and exact count, then seals the
indexed path/row arrays as readonly. Reinitialization rejects. The data remains
the validated declaration, including the original link metadata and target; no
filesystem result is cached. The [N12 shell](../n12/README.md#bounded-alias-declaration-lookup)
uses the identical lookup contract.

`lookup_alias_declaration(path)` requires initialization and the original
remaining allowance, clears its scratch index, then compares at most 64 exact
strings without `eval` or a subprocess. `verify_os_path` uses that row in place
of repeatedly parsing the whole audit for alias count and selection. An ordinary
path still takes its fresh ancestor walk and hash pin. An alias still requires
the declared digest, two fresh alias walks, its physical hash and complete
before/after identity samples. Both complete initial/final OS passes and the
audit's own hash pins remain mandatory. The
[declaration controls](../../test_alias_declaration_lookup.py) exercise real jq,
cache bounds/immutability, exact lookup and detached physical call ordering;
they grant no native authority or measured timing improvement.
