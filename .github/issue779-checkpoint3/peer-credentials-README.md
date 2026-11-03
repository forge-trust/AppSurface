# Peer credential measurement

The C3 workflow runs `LinuxPeerCredentialsNativeTests` as a separate four-case
partition. Its genuine Linux socket tests exercise the `SO_PEERCRED` helper with
an actual `setpriv` child and compare observed PID, UID, and GID values with the
expected values. The partition runs under the workflow's existing bounded
command and collector settings; it does not create a protected connection,
owner, admission decision, or accepted proof.

The focused file-custody workflow retains this partition under its own artifact
name and TRX. The C3 workflow retains its TRX separately from the existing root
filesystem TRX, merges only the pinned collector output, and binds the raw TRX
digest and exact four-row result in `measurement-binding.json`. The strict
Python parser and the C# native-artifact validator require the fixed methods,
the exact theory parameters, four passing executions, zero other counters, a
matching SHA-256, and `native_acceptance: false`. TRX input is capped at 1 MiB.

[`PeerCredentialsMeasurementTests`](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/PeerCredentialsMeasurementTests.cs)
uses synthetic XML only to test the detached validator's rejection rules. Those
tests do not exercise a kernel socket or establish native execution. The
[C# validator](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/PeerCredentialsMeasurement.cs)
accepts evidence only when the actual workflow artifact supplies its bound TRX;
passing measurement validation is not an owner, admission, or Trusted claim.
