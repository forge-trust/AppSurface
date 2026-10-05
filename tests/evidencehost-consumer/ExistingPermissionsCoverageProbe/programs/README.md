# Native existing-permission tiny probe — source preparation only

These files have not been formatted, compiled or executed. The parent reported a passing ordinary
local prototype; this new worker fixture still requires coordinated compilation and Linux validation.
There is no Evidence admission, catalogue, lease, Trusted or repository coverage acceptance claim.
No product source or worker unit policy is changed by these program files.

## Public task host and selected package

TaskHost.cs is byte-identical to the local prototype. One live process uses the public
[InstrumentationTask](https://github.com/coverlet-coverage/coverlet/blob/d21b5b6a08d48f51405ba2c5c5660f91a565776d/src/coverlet.msbuild.tasks/InstrumentationTask.cs)
and [CoverageResultTask](https://github.com/coverlet-coverage/coverlet/blob/d21b5b6a08d48f51405ba2c5c5660f91a565776d/src/coverlet.msbuild.tasks/CoverageResultTask.cs).
It retains the official shared task service provider and official state/hit/backup handling.
No Coverlet core call, reflection, private interface, synthetic state or fabricated hits are introduced.
The exact include filter remains [CounterFixture]*, and managed instrumentation restore remains enabled.
The prototype Threshold=0 generates reports for inspection; it does not alter the repository gate.

Both projects target net10.0, with fixed assembly names CounterFixture and OfficialTaskHost and no
PackageReference. Supply -p:CoverletPackageRoot=/absolute/verified/package to the host build. The root
controller authenticates the complete official 10.0.1 package before use; project existence checks do
not authenticate a package. Task DLL/dependency HintPaths use that root's tasks/net10.0 directory.
Microsoft.Build.Framework/Utilities.Core use the selected SDK's MSBuildBinPath. Preserve the generated
runtimeconfig/deps and all copied reference DLLs. Directory.Build.props separates bin/obj by project.
Copy these source bytes into the fresh private build directory before evaluation; do not build through
an unrelated repository's inherited targets. No package fetch or build command was run in preparation.

## Exact argv and ACK contract for the root controller

Task host: dotnet OfficialTaskHost.dll FIXTURE_DLL REPORT_PREFIX (exactly two application arguments).
The controller selects canonical paths, private stdout/stderr, and TMPDIR before starting the host;
it disables diagnostics only for this host. Host writes a flushed prepared JSON with its official
state path. After the worker is physically joined, host stdin receives exactly collect followed by
newline. The host runs the public result task and emits collected JSON; EOF/wrong ACK rejects.
Keep the same host alive throughout the prepare/consume pair. On failure join the worker before
terminating that host, because the official helper has process-exit restoration behavior.

Worker: dotnet CounterFixture.dll SESSION STATE_BASENAME DLL_BACKUP_BASENAME PDB_BACKUP_BASENAME.
Exactly four application arguments are permitted. Session is an absolute canonical POSIX path bounded
to4096 UTF-8 bytes; each distinct basename is1..255 ASCII letters/digits/dot/underscore/hyphen, with
no separators or dot/dotdot aliases. The root owner selects all names and verifies actual regular
single-link root-owned state/backups and their retained FD identities; arguments confer no authority.

Worker prints and flushes {"stage":"role-ready"}, then waits for exactly continue plus newline.
The external root owner supplies the existing cumulative deadline and physical process-group cleanup;
this blocking ReadLine creates no new timer or allowance. Before ACK, controller pins the three
selected files, fixture DLL, mounted session identity, and absence of the derived sibling
SESSION-worker-rename-probe. Worker then requires UnauthorizedAccessException separately for actual
ReadAllBytes, Open(Open, Write), and Delete of each of the three selected root files. Missing files,
successful operations and all other errors fail closed. No successful operation writes data.

## Corrected existing-permission session layout

The existing granted run anchor is worker-owned0700. The session child is an actual bounded32MiB
tmpfs mountpoint, root-owned01770 with group65011, inside that anchor. No worker ReadWritePaths,
allowance, cap or timeout is added. The sticky session protects root-owned0600 state/backup names
while genuine official hit files can be freshly created by the actual worker. Mount identity and
account/namespace/policy checks belong to the root controller, not this managed data fixture.

The worker attempts Directory.Move(session, derived sibling). Only UnauthorizedAccessException or
IOException with raw Unix HResult16 (EBUSY) is accepted. No generic IOException or
missing path is accepted as denial proof. After the exception the original session must still exist
at its same pathname and the sibling must remain absent. This managed same-path check does not prove
inode identity: root retained-FD and mountpoint identity must independently remain equal afterward.
The mounted-child EBUSY behavior and actual .NET errno/HResult mapping require Linux measurement.
Unexpected successful movement fails; controller cleanup must account for both fixed original and
sibling names under its fresh anchor rather than overlooking a moved object.

Worker also requires UnauthorizedAccessException or IOException with raw Unix HResult30 (EROFS)
opening the fixed running CounterFixture.dll for write, selected from AppContext.BaseDirectory without reflection. Root independently pins that
selected executable inode. Finally Calculate(1)+Calculate(-1) exercises both real branches and must
produce3. Success is flushed {"stage":"completed","denials_passed":true,"value":3}, exit0.
Every rejected condition returns exit1 with a fixed failed/category JSON; no exception text or raw
selected paths are echoed by CounterFixture. Official task-host diagnostic text remains private.

## Required validation and limits

Parent coordinates exact-file formatting/build before native dispatch. The Linux root owner must
measure actual task-host/worker identities, readonly fixture bytes, root state/backup hashes and
modes, session mount/inode and name stability, bounded genuine hit files, worker exit0, host result0,
report contents and official restoration after owned completion. Fresh hit creation is performed
by official instrumentation; no empty hit inode or header is precreated. Every child/server/pump is
joined within the unchanged controller deadline before cleanup. An unsuccessful denial probe may
have altered a selected object; treat that as failure and do not consume it as trusted evidence.
The probe is a mechanical file-permission/mount/hit procedure, not systemd health or Evidence authority.
No coverage number, denied operation, restoration, native pass or guard-preserving proof is claimed
from source preparation. The external controller's guards and cleanup remain independently reviewable.


## Subsequent local source validation

The initial handoff status above is historical. The private copies were restored,
formatted in the two owned C# files, and built locally: both builds completed with
zero compiler warnings and errors. An initial TaskHost formatter workspace warning
was corrected by supplying the verified package root as an MSBuild environment
property; the scoped formatter then completed with empty stderr. The source bytes
were unchanged. The generated dependency-free lock is committed and native restores
use locked mode. Linux execution, account separation, mountpoint denial, and native
coverage remain pending until an actual mechanism receipt is retained.
