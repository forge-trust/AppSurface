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
