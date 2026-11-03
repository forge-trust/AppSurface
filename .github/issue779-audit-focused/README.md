# Focused Linux file-custody controls

This candidate-only workflow runs two existing root Linux filesystem test classes on Ubuntu 24.04:
`LinuxApplicationBundleAuditNativeTests` and `LinuxProducerReportDiscoveryNativeTests`. It uses .NET SDK
10.0.401, restores the locked test project, builds the ordinary `EvidencePrivateQualification=None`
Release test assembly with warnings as errors, and runs the exact class filter under `sudo` with a
180-second process timeout.

The test collector result is checked with the existing
[`application_bundle_measurement.read_trx`](../issue779-checkpoint3/application_bundle_measurement.py)
contract. That parser requires the fixed seventeen test names, seventeen passed/executed rows, and zero
skip/failure/error counters; it does not rewrite a failed result into success. The unmodified TRX and
bounded command logs are retained when available, including on test failure, if the evidence directory
passes its separate retention checks. Upload runs only after retention succeeds.

The job captures and verifies the complete source tree around the commands. Its private runner evidence
directory is mode 0700 and leaves are normalized to 0600. Retention rejects symlinks, nonregular or
multilink leaves, unexpected paths, logs over 8 MiB, a source capture or TRX over 1 MiB, more than eight
files, or more than 20 MiB total. Shell redirection keeps command output in those private logs; no raw
test output is echoed into workflow logs.

These are Linux filesystem-handle controls only. They do not create a protected workspace owner,
admission, accepted proof, native application result, or coverage claim. A skipped Linux test is not a
pass. The workflow has not run merely because this source is prepared, and candidate-branch output is
not protected-main qualification.

## Fixed commands

The workflow has no dispatch inputs or command parameters supplied by a caller. It checks out the
triggering candidate commit with persisted credentials disabled, installs the pinned SDK into a fresh
runner-temporary directory, captures the full source, and runs only the fixed restore, build, and VSTest
arguments in the workflow. Each .NET process has a 180-second `timeout --signal=KILL` boundary. The
overall job timeout is 15 minutes.
