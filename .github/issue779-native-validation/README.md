# Current #779 native Linux validation bundle

This bundle captures the local shared implementation from `codex/make-it-so-evidencehost-779`.
It does not update draft PR #850 and does not grant Trusted support. Its private snapshot
commit exists only to let the unchanged patch gate compare the complete current source
against the preserved `origin/main` revision. The original checkout and feature branch
are not changed by creating this bundle.

## Environment

Use a disposable **native Linux x86-64** host with a non-root runner account and
passwordless sudo. Install .NET 10, Node 24, pnpm 11.1.3, Docker with access for the
runner, Python 3, Git, `setpriv`, GNU `timeout`, and PowerShell. The script invokes the
repository's pinned Playwright installer. Give this host no protected CI credentials;
the deterministic root broker is a library test fixture, not a subject isolation proof.
Do not run it through the failing QEMU container on the current ARM Mac.

## Run

Copy the archive and its SHA-256 sidecar into a new directory, then run:

```sh
sha256sum -c issue779-native-validation.tar.gz.sha256
tar -xzf issue779-native-validation.tar.gz
cd issue779-native-validation
bash run-native-validation.sh
```

The script checks source hashes and a clean private snapshot, preserves the exact
`origin/main` comparison, restores locked packages, prepares web/browser dependencies,
runs portable launcher and actual Linux protocol/lifecycle cases, then executes the
unchanged `./scripts/coverage-solution.sh` under a root-owned test broker with the
non-root runner's exact identity. Explicit sandbox marker variables survive the fixture;
no thresholds, exclusions, skips or environment gates are disabled. Only explicitly
selected supplementary groups are retained so existing Docker tests can run.

The wrapper must exit zero with the existing aggregate and patch thresholds. Return
`receipts/`, including coverage output, terminal log and before/after source verification.
The exit trap retains partial coverage output and source verification after failure.
The complete runner console output is retained as `receipts/run.log`.
If a check fails, retain its log and stop. Do not clear the comparison base or claim a
focused pass is a solution gate pass. This script has passed local syntax checks; its
Linux execution is pending and any fixture or source failures must be repaired honestly.

A green native validation run still leaves actual current systemd/GitHub consumer
Observation, protected/fork/downstream acceptance, the restricted Aspire resource lane,
packed SDK acceptance and the remaining approved test-plan groups outstanding. Neither
this bundle nor its synthetic cgroup metadata enables Trusted or closes #779.
