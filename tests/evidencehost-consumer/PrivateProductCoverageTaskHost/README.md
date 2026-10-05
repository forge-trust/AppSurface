# Private product coverage task host

This private executable prepares and collects real instrumentation through the
official public [Coverlet 10.0.1 MSBuild tasks](https://github.com/coverlet-coverage/coverlet/tree/v10.0.1/src/coverlet.msbuild.tasks).
It generalizes the surviving [public-task native mechanism host](https://github.com/forge-trust/AppSurface/blob/eeaed064cd1d40172fa60840df8d2a82313f1cac/tests/evidencehost-consumer/ExistingPermissionsCoverageProbe/programs/README.md).
It provides process coordination and measured report data. It creates no
admission, lease, worker write grant, accepted consumer proof or Trusted authority.

## Recovery and current status

These three files were reconstructed after the temporary checkouts disappeared.
They are new source, with new hashes; they are not claimed to reproduce the former
frozen Program, project or README bytes. The surviving tiny-host source supplied
the public task sequence and reference list. The original mechanism evidence
remains historical and does not establish that this reconstructed product host
has compiled or executed.

This packet has only source/static verification. No .NET formatter, restore,
build, tests, instrumentation, root API or native consumer has run for it. The
parent owns the next compiler slot, lock generation, source review and actual
product integration validation. No packages.lock.json was fabricated or copied
from a different project.

## Fixed build inputs

`PrivateProductCoverageTaskHost.csproj` builds the executable assembly
`OfficialTaskHost` for `net10.0`. It compiles only `Program.cs`, has no
`PackageReference`, and retains the surviving host's fourteen public assembly
references. Microsoft.Build framework and utilities are selected through the
SDK's `MSBuildBinPath`; Coverlet and its accompanying assemblies are selected
from `$(PackagePath)/tasks/net10.0`.

The build owner must supply `PackagePath` as the absolute root of the
authenticated Coverlet 10.0.1 package, not the tasks subdirectory. The project
rejects an empty or relative value and a missing task assembly. That guard checks
selection shape and presence only. The root owner must authenticate the package
digest, extract bounded bytes, reject links and seal the actual package before
using it. Supplying a build property does not authenticate a package.

The parent must retain the SDK/reference identities and required MSBuild runtime
resources in the runnable deployment. Merely finding the task DLL does not prove
that its managed dependencies or satellite resources are deployable on Linux.
Restore, formatting, compilation and lock-state checks remain parent-owned.

## Invocation and path constraints

The host accepts exactly two root-selected arguments:

```text
OfficialTaskHost TOP_CLI_DLL REPORT_PREFIX
```

In the framework-dependent deployment the root owner invokes its selected
`dotnet` host with `OfficialTaskHost.dll` followed by these two arguments. The
first argument must name `ForgeTrust.AppSurface.Cli.dll`. Its directory must also
contain all three selected DLL/PDB pairs:

- `ForgeTrust.AppSurface.Evidence.Cli.dll` and `.pdb`;
- `ForgeTrust.AppSurface.Evidence.Aspire.dll` and `.pdb`;
- `ForgeTrust.AppSurface.Evidence.Coverage.dll` and `.pdb`.

`REPORT_PREFIX` must have the fixed basename `coverage` under an existing root
report directory. The prefix, `coverage.json` and `coverage.cobertura.xml` must
not already be files or directories. The host creates no input or output
directories. Every argument and derived input/output path must be an absolute
canonical Unix path, at most 4096 characters and 4096 UTF-8 bytes, without control
characters, backslashes, empty components, `.` or `..`. Trailing separators are
rejected except the one returned by `Path.GetTempPath()` for the temporary root.

Managed presence/canonicalization checks are not retained-FD, inode, owner,
single-link or no-follow authentication. The root controller must separately
verify those facts and guard directory ancestors. It must select the real
immutable product bytes, authenticate the emitted DLL/PDB pair identities and
source documents, and establish mixed-binary ABI compatibility before admitting
any consumer. Private Contracts, Planner and top-level CLI/Host entry remain
private integration inputs; this host does not grant coverage credit to their
generated binding logic.

## Task sequence and protocol

One process creates one private `IBuildEngine`, invokes public
`InstrumentationTask`, waits for a fixed acknowledgment, then invokes public
`CoverageResultTask`. It does not replace `BaseTask.ServiceProvider`. The actual
`InstrumenterState` item returned by preparation is passed directly to
collection in the same living host; it is never reconstructed from caller JSON.
There are no reflection calls, private Coverlet API calls, manufactured state or
precreated hit headers. The engine's `BuildProjectFile` callback rejects nested
build requests; it exposes no dependency callback capability to a caller.

Preparation uses exactly:

```text
Include=[ForgeTrust.AppSurface.Evidence.Cli]*,[ForgeTrust.AppSurface.Evidence.Aspire]*,[ForgeTrust.AppSurface.Evidence.Coverage]*
IncludeTestAssembly=false
DisableManagedInstrumentationRestore=false
```

The task's `Path` is the selected top-level CLI DLL. Contracts, Planner, top-level
CLI and the private Host entry are outside this include filter. Inclusion shape
alone does not prove which emitted modules were instrumented; the owner must
verify the actual official preparation records and sealed module hashes.

After successful preparation, a flushed whole-line JSON packet has this shape:

```json
{"stage":"prepared","state":"<official absolute state path>","temp":"<selected temporary directory>"}
```

The temporary directory comes from the root-selected `TMPDIR` through the
runtime's `Path.GetTempPath()`. Preparation requires an existing canonical state
file and temporary directory. This packet is private data for the root owner;
it does not authenticate the named state/backups or establish ownership.

The only accepted stdin command is the exact eight-character sequence
`collect\n`. Characters are read individually, without an unbounded `ReadLine`.
EOF, a mismatched character or `collect\r\n` rejects collection. Keep the process
alive while consumers execute; do not interpret stdin as a source, path, state,
callback or policy channel. After the command the host accepts no further
protocol operation. Waiting is bounded externally by the root owner's deadline.

Collection uses the actual state item, `Output=REPORT_PREFIX`, formats
`json,cobertura`, `Threshold=0`, `ThresholdType=line,branch,method` and
`ThresholdStat=total`. Threshold zero permits collection of measured data; it
does not relax the product coverage gate or make a collected report acceptable.
The resulting flushed whole-line packet is:

```json
{"stage":"collected","passed":true,"errors":0,"warnings":0}
```

**Stdout is not a JSON-only stream.** Official `CoverageResultTask` may print its
coverage tables directly to stdout outside `IBuildEngine`. The root owner must
drain and bound the complete stream, select exact packet lines, tolerate the
official intervening table, and treat oversize, unexpected protocol or incomplete
output as failure. It must not grant acceptance from the packet or a table row.
Each packet emitted by this host is capped at 16 KiB including its LF newline.

## Private task logging and terminal failures

The private engine sends task events to stderr under a lock. Each event is at
most 4096 UTF-8 bytes including its newline; stderr emitted by this engine is at
most 128 KiB. Error and warning counters saturate at one million each. A message
or count over its limit marks the host unsuccessful and suppresses the excess
message. These are bounded private logs and may contain official task paths;
the owner must retain them privately rather than echoing them publicly. The
engine cap does not bound direct stdout/stderr writes by external task internals,
so root process ownership and total pipe caps remain mandatory.

| Exit | Meaning |
|---:|---|
| 0 | Collection returned true, error/warning counts are zero, and engine limits were not exceeded. |
| 64 | Argument, path, required input or fresh report selection rejected. |
| 65 | Preparation failed, task logging failed its limits, or state/temp output shape rejected. |
| 66 | Collection acknowledgment rejected. |
| 67 | Collection, task diagnostics or engine bounds failed. |
| 68 | Unexpected host exception. |

Closed failure packets contain only `stage=failed` and a fixed category; the host
does not echo exception messages in them. Exit zero and packets are mechanism
facts, not lease, health, cleanup, coverage-gate or qualification authority.

## Root-owned lifetime and restoration

Official managed restoration is enabled. The root owner must retain and protect
the real state, backup names, module pairs and temporary namespace while this
host lives. Chmod alone cannot restrain a root host, and unexpected host exit can
invoke the official automatic restoration handler. The controller must enforce
its reviewed namespace/mount/lifetime protection, real consumer unit ownership,
watchdog, pipe limits, cancellation and rollback ordering; none is created here.

Send `collect\n` only after every consumer unit, group and output pump has
actually settled and the applicable accounts have closed. Keep the same official
host alive through collection/restore. On failure preserve original failure,
stop/join owned work, and verify actual sealed module/restoration facts before
publishing any report. Do not turn a killed host, missing state/hit file, partial
restore or cleanup timeout into a successful report. Only actual official hit
files and reports may be consumed; no synthetic coverage data is generated here.
