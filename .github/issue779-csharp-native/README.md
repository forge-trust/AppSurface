# Private C# N04 Linux validation

This harness invokes the same [CLI role source](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/LinuxEmptyObservationExecution.cs)
as the primary command: a root `evidence supervise` instance owns a separate
unprivileged `evidence worker` instance. Python and Bash in this folder prepare and
verify test inputs; the C# image owns the live worker lifecycle.

## Fixed test and prerequisites

The sole new control is N04: after the genuine worker has authenticated and received
READY, a fixed root coordinator replaces the named socket. The worker's next real
request must reject the changed root PID with ASEVD402 before sending application
bytes. The original supervisor must reap the worker and join its work and both
pumps. Its failed run retains quarantined accounts and paths; it cannot establish
positive custody, enable Trusted or enter the production registry.

The coordinator uses its own transient unit and the fixed KILL, CHOWN,
DAC_READ_SEARCH, FOWNER and SYS_PTRACE capability set. Two live proc samples must
match its UID/GID, image, managed argv, cgroup, NoNewPrivs=1 and exact capability
mask. These coordinator capabilities never appear in the worker's policy.
All control actions share the original root deadline. An unobserved or missed live
process sample fails the test; an exited PID cannot replace live proof.

The workflow builds the entire captured source, both complete FDD coordinators and
the selected runtime. Acquisition and OS auditing validate all five trees, exact
membership, every file SHA and mode, and every declared OS dependency and alias.
Source flags represent reviewed source prerequisites; only actual command receipts,
kernel observations, guarded account checks and joined invocations can verify N04.

## Detached data interfaces

`n04-record-data.py` checks the fixed three supervisor stderr frames, helper result,
live-policy sample and descriptor. `n04-archive-data.py` provides
`validate_n04_archive(values, expected_source, expected_base)` after canonical USTAR
inspection. Required raw members, copies, numeric types, identities, stream hashes,
descriptor policy, account row and replayed consistency must agree. Rejection has
one fixed message and never echoes supplied data.

The original C# holder's complete worker count/hash is compared with the fixed 402
bytes. It is not a separate raw worker capture. Request bytes are not retained;
their digest is detached consistency backed by the actual fixture/helper pin checks.
`request-policy.sha256` is a policy hash and cannot stand in for request bytes.
JSON or a detached parser alone provides no authenticated actor or admission.

## Running and interpreting results

The checked workflow pins every interpreter input before use. Detached controls run
as an ordinary user; they test data guards and small owned files, not systemd or
privileged process behavior. The fixed actual Bash parser controls precede package
setup, source build and native dispatch. Each phase retains its original deadline
and joins its owned process group before the next phase.

`run-native.py` requires explicit `--execute`, absolute fresh outputs, the reviewed
script SHA, complete build roots and the original job deadline. It publishes zero
only after the fixture exits zero, canonical private retention completes and all N04
relationships verify. Retention cannot upgrade the original failure or clean units.
NSS/account quarantine is established by the fixture's actual forward and reverse
checks; the TSV is a retained consistency record. Missing, late, substituted or
incomplete records remain failures. No other control is credited by this fixture.

Local helper builds and detached data tests do not establish Linux runtime behavior.
This private candidate remains unpublished until independent final composition and
installation-driver review. All native outputs are unknown at source preparation.


## Qualified alias retry preparation (pending)

This fresh direct-child retry preserves source 5b5a/full2827 and the original helper. The fixed N04 alias declaration candidate is 130543 bytes; only immutable declarations are cached, every physical check remains fresh. The 23 removed comments are presentation only. Prior failure was prelaunch at the unchanged native-life guard; stage cost is not measured here. Candidate-specific actual Linux qualification, fresh composition review, and explicit promotion are pending. No native credit is supplied by this preparation.

## N04 parser selection retry

The previous attempt stopped in the unprivileged parser controls because a test selected a source span by a removed comment. The [actual parser controls](test_actual_batch_parser.py) now use unique, ordered function boundaries and require the exact seven-function declaration set. The qualified fixture, runtime code, original 18 Bash control bodies and separate 48 data controls are unchanged. Seven selector data controls passed locally; the full Bash controls and genuine N04 worker control still require Linux. This retry grants no native control credit.

### Exact parser-repair installation delta

The N04 build prerequisite includes the modified `test_actual_batch_parser.py`
in its exact installation inventory. The source commit, all product bytes,
runtime limits, and native assertions are unchanged. Missing, extra, or
incorrectly classified installation paths still reject before compilation.
The prior parser-repair run failed this prerequisite before native execution.

## Original fixture clock read

This retry preserves the original fixture, cleanup, owner and private startup bounds. Each guard reads the same `/proc/uptime` value using a Bash builtin in the holder shell instead of a command substitution. Clock values remain fresh and use the same whole-second floor. Local clock controls are procedure evidence only; the prior native precondition failure remains recorded and no native case is credited by this preparation.
