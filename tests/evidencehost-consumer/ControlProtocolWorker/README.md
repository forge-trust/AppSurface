# Linux control protocol mechanism check

Build the fixture after the parent build gate is open, from the repository root:

```sh
dotnet build tests/evidencehost-consumer/ControlProtocolWorker/ControlProtocolWorker.csproj -c Release
```

Run the independent protocol matrix as root inside a disposable Linux container or VM with .NET 10 and
`setpriv` available:

```sh
python3 -B tests/evidencehost-consumer/test_control_protocol.py \
  --worker-dll tests/evidencehost-consumer/ControlProtocolWorker/bin/Release/net10.0/EvidenceHost.ControlProtocolWorker.dll
```

The driver launches a root-owned fake Unix-socket broker and a .NET worker with UID/GID 65534. It covers the
real `EvidenceLinuxWorkerSupervisor.ConnectAsync` peer-credential handshake, descriptor identity and deadline
rejection, broker PID replacement, bounded/malformed responses, artifact-count limits, and multi-chunk reads.
The fake descriptor uses a `/system.slice/...` value because the client validates that shape; the fixture does
not create a systemd unit or cgroup and does not establish EvidenceHost admission or consumer acceptance.

`EvidenceLinuxControlProtocolTests` separately exercises platform rejection on non-Linux hosts and invalid-path
preflight on Linux. It does not replace this root-only mechanism run. Run both only when the repository's parent
build owner authorizes the build and test steps.
