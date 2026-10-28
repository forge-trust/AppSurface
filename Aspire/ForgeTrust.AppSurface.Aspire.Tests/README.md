# ForgeTrust.AppSurface.Aspire.Tests

Unit tests for the .NET Aspire integration components.

## EvidenceHost ownership controls

[`EvidenceAspireApplicationUnstartedOwnershipTests`](EvidenceAspireApplicationUnstartedOwnershipTests.cs)
builds an actual empty Aspire application through the existing internal ownership path, then cancels
before startup. It verifies that ownership survives the canceled start, invalid readiness requests
do not retain hidden registrations, and the final readiness registration releases the application.
It never starts DCP or waits for health, endpoint allocation, or HTTP readiness. These are ownership
and API validation checks, not native application or consumer-acceptance proof.

[`EvidenceHostProducerCancellationTests`](EvidenceHostProducerCancellationTests.cs) holds an active
producer after it observes caller cancellation. Even after the fake supervisor reports owned-work
exit, the host must join that managed callback before disposing registrations or completing collection;
later producers remain unstarted and the result has no claim or eligibility. The barrier is released
and execution joined even when an assertion fails.
This extends the existing cancellation-result checks with the cleanup-order boundary. The
[internal admission fixture](EvidenceHostAdmissionTestRun.cs) exercises library behavior only and
creates no production support registration. See the [EvidenceHost guide](../../start-here/evidencehost.md)
for the production execution and acceptance limits.

## Running Tests

```bash
dotnet test
```

---
[📂 Back to Aspire List](../README.md) | [🏠 Back to Root](../../README.md)
