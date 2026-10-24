# Supervision exception policy

[`SupervisionExceptionPolicy`](SupervisionExceptionPolicy.cs) is an internal classifier
used by existing supervision
catch filters. It classifies an exception object only; it does not create an owner,
process, handle, lease, admission result, retry, replacement exception, or native
authority. Existing callers retain their catch bodies, failure latches, quarantine,
cleanup, cancellation, and fatal propagation.

`IsRecoverable` preserves the broad original filter exactly: every exception except
`OutOfMemoryException`, `StackOverflowException`, and `AccessViolationException` is
accepted. The null behavior is intentionally unchanged because the original type
pattern also returns `true` for null.

The admission exception type is the internal
[`EvidenceAdmissionException`](../ForgeTrust.AppSurface.Evidence.Contracts/EvidenceAdmission.cs)
used by the Contracts admission boundary; recognizing it here does not create or grant
an admission result.

The two workspace policies are separate by design. Both preserve their original
closed family of `IOException`, `UnauthorizedAccessException`,
`PlatformNotSupportedException`, `ArgumentException`, `EvidenceAdmissionException`,
`ObjectDisposedException`, `InvalidOperationException`, and `OverflowException`.
`IsApplicationWorkspaceRecoverable` additionally accepts `Win32Exception`; the
producer workspace policy does not. The policies do not use a union switch or a
configuration flag, so a producer catch filter cannot silently inherit the
application native-error behavior.

The [policy tests](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/SupervisionExceptionPolicyTests.cs)
exercise ordinary accepted types, the three constructed fatal exception objects,
ordinary `ArgumentException`, the existing `EvidenceAdmissionException`
fixed rejection factory, `Win32Exception` divergence, cancellation rejection by both narrow
families, and the original null behavior. These are classification tests only; they
do not invoke a native caller or treat classification as authority.
