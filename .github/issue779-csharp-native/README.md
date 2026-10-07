# Ubuntu execution runtime for the private C# checkpoint

## Decision and measured failure

The frozen source remains `2993dcfaac1b9b6dfb8adf057191f837876f01fe` (2819 files).
[Attempt 7](https://github.com/forge-trust/AppSurface/actions/runs/37670278485) built the
ordinary CLI with SDK 10.0.401. The complete ELF audit then rejected the generic .NET
10.0.12 `libcoreclrtraceptprovider.so`: its `liblttng-ust.so.0` dependency had zero RUNPATH
and loader-cache candidates. The authenticated private failure sidecar records that fact.

Microsoft's [exact runtime source](https://github.com/dotnet/runtime/blob/v10.0.12/src/coreclr/pal/src/misc/tracepointprovider.cpp#L109-L113)
tolerates failure to load this tracing component. Its [tracing guidance](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/trace-perfcollect-lttng)
describes the LTTng 2.12/2.13 incompatibility. This prerequisite stages the supported Ubuntu
runtime while retaining the complete ELF audit and full deployment inventories.
[Ubuntu's runtime package](https://packages.ubuntu.com/noble/dotnet-runtime-10.0)
depends on `liblttng-ust1t64`. Package metadata supports trying that runtime; it does not
establish the actual ELF closure or any native checkpoint success.

## Private preparation API and ordering

`prepare-ubuntu-runtime.py --execute --reviewed-script-sha256 SHA --runner-sha256 SHA`
requires both externally reviewed script hashes before importing the existing private
runner. The workflow verifies these source bytes before interpreter execution.

The fixed four Ubuntu packages are `dotnet-host-10.0`, `dotnet-hostfxr-10.0`,
`dotnet-runtime-10.0`, and `aspnetcore-runtime-10.0`, each version
`10.0.12-0ubuntu1~24.04.1` on amd64. A signed APT index refresh precedes candidate metadata
and the exact-version install with unauthenticated and insecure repositories disabled.
The existing root-tool runner owns the generated unit, prearmed absolute deadline,
start guard, complete cgroup stop/join and original phase bounds. One 300-second
prerequisite interval, including final reserve, must fit the original 2100-second job
deadline. The update/install limits are 90/165 seconds, including their cleanup reserves.

After installation, exact installed package/version/architecture/status data and the
single canonical root-owned nonwritable physical host are verified. The fixed private
`issue779-csharp-ubuntu-runtime/receipt.json` retains every process, log, guard and group
result. A failed, late or unsettled prerequisite prevents the build.

The builder invokes SDK 10.0.401 explicitly through setup-dotnet's `DOTNET_ROOT`.
Execution host, hostfxr and both frameworks come coherently from `/usr/lib/dotnet`.
The framework-dependent CLI runtime configuration must select exactly 10.0.12 for each
framework and hostfxr; another installed version is rejected before copying. Full selected framework contents are copied and
hashed; SDK directories are not deployment content. Source, input and aggregate
deployment bounds, all root/worker policies and the strict ELF audit remain unchanged.

## Pitfalls and validation status

Do not assemble execution files from both the generic and Ubuntu installations. Do not
fall back to another package version or host, delete the tracepoint provider, synthesize
resolved dependency rows, or treat a package receipt as runtime admission. Actual complete
ELF audit and N01/N02 remain required; the remaining fourteen checkpoint controls and
producer/application/coverage/cutover gates are still pending.

`test_ubuntu_runtime.py` defines eight pure metadata/provenance controls. They exercise
package, host, failed/unjoined/forced/timed-out/root-guard and mixed-source rejection.
They install no package and run no runtime, root command, native worker or systemd unit.
Their actual terminal result is recorded separately after execution.

The terminal publisher rechecks the original deadline after stdout flush. A late or
failed terminal write preserves failure and records a bounded invalidation marker when
the original reserve remains. The builder rejects that marker before reading provenance.
Eight additional pure boundary controls exercise version rejection before copy and
late/failing terminal output. Historical command-count mismatch and duplicate unittest
discovery are retained honestly; the final boundary-only run passed eight cases.
