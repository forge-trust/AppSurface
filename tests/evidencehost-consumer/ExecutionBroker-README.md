# Protected consumer execution broker fixture

This fixture exercises the production protected CLI and Aspire consumer entries over the same Unix socket protocol they use in production. It runs on disposable Linux x86-64 as root, starts the requested .NET test command under a non-root UID/GID with `setpriv`, and copies the restored, pinned ReportGenerator 5.5.10 `net10.0` payload into a root-owned tool root. Each scenario has its own root-owned socket and metadata file. The broker captures `SO_PEERCRED` from the first `ready` connection and pins that actual testhost PID, UID, and GID for every later operation on the scenario socket.

The worker output parents are fresh worker-owned directories with their device, inode, UID, and GID taken from `stat`. Policy bytes are written once under the root-owned tool root and exposed read-only to the worker. The fixture's `/system.slice/...` cgroup value is explicitly synthetic; this script starts no systemd unit and does not test cgroup supervision, subject isolation, a production launcher, or protected CI acceptance. The `run` response is deterministic fixture data. The positive coverage test still runs the production coverage adapter, collects hostile Cobertura bytes through the broker protocol, invokes the copied ReportGenerator package, writes artifacts through retained production handles, and verifies their digests. That proves consumer-path behavior only.

The positive Cobertura report contains one synthetic covered line and two covered branch outcomes in a class. ReportGenerator recalculates valid-item counts from these elements, so top-level percentages on an empty package tree cannot establish a positive numeric gate: the [coverage gate](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-gate) rejects zero valid items with `ASCOV006`. The [portable report regression](test_execution_broker_fixture.py) checks that covered line/branch elements exist and match the declared totals. The protected consumer test then requires the actual merged report to pass the existing numeric gate before claiming Observation evidence.

## Linux invocation

Use a disposable Linux x86-64 runner with Python 3, `setpriv`, .NET 10, and the already restored ReportGenerator 5.5.10 package. Build the two test assemblies before entering the root fixture. Then invoke the fixture from the repository root with the numeric worker and subject identities chosen for that runner. Both UID/GID pairs must be non-root and distinct. For example, if `worker` is 65534:65534 and `subject` is 65533:65533:

```sh
sudo -n python3 tests/evidencehost-consumer/test_execution_broker.py \
  --worker-uid 65534 --worker-gid 65534 \
  --subject-uid 65533 --subject-gid 65533 \
  --reportgenerator-package /home/runner/.nuget/packages/reportgenerator/5.5.10 \
  -- dotnet vstest \
    Cli/ForgeTrust.AppSurface.Cli.Tests/bin/Debug/net10.0/ForgeTrust.AppSurface.Cli.Tests.dll \
    Aspire/ForgeTrust.AppSurface.Aspire.Tests/bin/Debug/net10.0/ForgeTrust.AppSurface.Aspire.Tests.dll \
    '--TestCaseFilter:FullyQualifiedName~EvidenceProtectedCliExecutionTests|FullyQualifiedName~EvidenceProtectedAspireExecutionTests'
```

The worker starts with supplementary groups cleared. To grant a specific runner group, pass `--worker-supplementary-groups 998,1002` with the explicit positive GIDs required on that host. GID 0, duplicates, and the subject GID are rejected; the fixture never adds groups by default. The actual wrapper or validation command can be supplied unchanged after `--`. For a full wrapper, an owner can explicitly set its required environment in that command, for example `/usr/bin/env PATH=<runner-path> NUGET_PACKAGES=<runner-owned-cache> ./scripts/coverage-solution.sh`; the fixture does not pass through the caller's general environment. The fixture defaults `NUGET_PACKAGES` to a fresh worker-owned temporary cache. If the command overrides it, that selected cache must be writable by the non-root worker.

The test assemblies must be readable by the selected worker identity. The test command receives worker-owned temporary HOME, .NET CLI home, NuGet cache, temp, and result directories. The fixture prints its temporary root and test-command exit status to stderr; it preserves that root for inspection. Scenario metadata files next to the sockets carry the protected policy path, selected paths, output parent/slot, and operation log path to both test assemblies through `EVIDENCEHOST_TEST_BROKER_SOCKET` (the socket directory). `EVIDENCEHOST_TEST_BROKER_POLICY` and `EVIDENCEHOST_TEST_BROKER_RESULTS` identify the shared policy and worker-owned results directory. Only the four sandbox marker variables `CODEX_SANDBOX`, `SANDBOX_MODE`, `IN_SANDBOX`, and `IS_SANDBOX` are forwarded unchanged when present; the owner must preserve those keys through `sudo`. Other caller environment variables, including credentials, are not forwarded.

The fixture root and worker-root ancestors are root-owned with the worker group and mode `0750`. The [retained coverage output lease](../../Cli/ForgeTrust.AppSurface.Cli/CoverageRunOutputLease.cs) opens each temporary-path ancestor with `O_RDONLY | O_DIRECTORY`, which requires both read and search access. Execute-only `0710` ancestors make the coverage procedure fail before it can produce evidence. The worker group has no write access to those ancestors, and the distinct non-root subject UID/GID has no access through the other permission bits. Worker-owned HOME, cache, temp, and result directories use `0700`. The [portable fixture regression](test_execution_broker_fixture.py) checks the applied ancestor modes and root/worker ownership assignments without requiring root.

The C# suites always run a small guard proving that unsupported platforms and absent Linux channels reject. On Linux, the selected production-path cases require this fixture and fail closed when it is absent; a missing-channel rejection is not counted as a production-path pass. On macOS and Windows, each Linux-only case performs the explicit unsupported-connect assertion and returns; those returns do not count as protected execution coverage.

The scenario operation logs and `*.peer.json` files are useful for diagnosing test failures. The socket peer record must match the selected non-root worker UID/GID, and the C# tests compare its PID with the active testhost process. Each descriptor uses a scenario-specific run ID, and its policy digest is computed from the fixture policy bytes; the all-zero proof digest intentionally cannot admit Trusted execution. A successful report remains informational in Observation; Trusted remains rejected with ASEVD407 because the consumer proof allowlist is empty. A separate systemd-backed CI run is still required for actual launcher, cgroup, process-isolation, and published-gate acceptance.

## Validation record

The coordinating parent reports that native CLI verification passed 327 tests with no warnings. Formatting verification passed for both owned C# test files. These results do not establish Linux fixture or systemd-backed published-gate acceptance.
