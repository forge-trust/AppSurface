# Source-pinned cancellation native preparation

This private fixture exercises the same [C# supervisor and worker executable](../../docs/designs/issue-779-csharp-supervision-core.md).
It does not add a production application, producer, proof or admission entry.

## Ordering and ownership

The private compiled N08 image pauses the original CliFx caller before allocation;
N09 pauses it after the original allocation handle is retained. The original C#
root owns the tracked checkpoint and sends SIGINT only through its retained
original worker pidfd after authenticated READY and a charged phase observation.
The fixture never selects a supplied process ID for delivery.

The original C# owner joins worker start/monitor, server and output pumps before
emitting its fixed failed result and bounded original stream bytes. The OS reader
then checks the selected generated layout, absence of both original processes,
fresh empty/absent worker group, and original NSS accounts retained in quarantine.
Worker FD closure is kernel closure on observed process exit; a successful managed
Dispose call or successful custody transfer is not inferred.

## Fixed data APIs and bounds

`capture_cancellation.py` runs isolated Python 3.12 from a fresh root700 directory.
Its four source-pinned modules read only fixed generated paths and perform no
launch, signal, ID release, deletion, admission, manifest publication or timer
reset. `cancellation_adapter.capture` receives the original root-selected BOOTTIME
end; retained FDs and named identities are rechecked before and after bounded reads.

`check_cancellation_archive.check_archive` is a detached data function. It requires
all original records, exact hashes/bytes/EOF/quota, distinct original identities,
negative fixture terminal, selected case and source/image/policy hashes. It never
authenticates actors. Real source-pinned execution and original terminal receipts
remain separate prerequisites. Malformed data cannot upgrade the failed run.

Root retention permits only fixed file names, per-file bounds, root600 single-link
sources, canonical sorted USTAR headers and the existing 32MiB aggregate ceiling.
Raw streams remain in the private archive and are never echoed to public logs.

## Current state and pitfalls

This is a nonexecuting preparation. All new integration review gates are false.
Local detached parser tests prove parser behavior only. A correct SIGINT syscall,
JSON join flag, unit-test pass or archive digest alone does not establish Linux
acceptance. N08/N09 remain pending until the exact deployed images execute and all
original signal, terminal, EOF, path, group and quarantine observations are verified.

One source plus an exact installation commit will be built through the ordinary
locked dependency graph. The builder rejects any unlisted installation delta;
source membership includes exactly 2843 hashes and modes. Accounts and paths are
preserved on failure, and cleanup consumes the original fixture allowance.

### Cancellation cleanup source revision

This candidate binds source `134aeaba59d2b0cd4d429c7820f7ce775b6d2afb` (direct parent `b4e4f632c080e9c168ac129070ccc5760c3dc2af`) and 2,843 source files. The root cleanup record is emitted after joined work, root custody, account removal and original owner closure. The fixed archive retains `root-cleanup.json` at a 1 KiB bound; the post-join observer checks root-owned terminal paths and forward/reverse NSS absence. These diagnostics preserve the original failed Observation and issue no Evidence success, admission, or native acceptance. The source audit has a fresh numeric zero result; the original capturer's missing final session remains a historical limitation. Review promotion and current Linux controls remain pending.
