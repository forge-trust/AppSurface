# Engineering test plan: Durable Testing (#802)

This test plan was approved before implementation. Use the public Testing APIs and exact production contracts; never use reflection to reach private state.

## Provider-free contract suite

| Area | Success cases | Failure and boundary cases |
| --- | --- | --- |
| Health builder | Healthy, NotStarted, Stale, Draining, Incompatible, Unavailable defaults; all four computed predicates and valid overrides | Undefined state; invalid ID, epoch, surfaces, versions, age, count; contradictory fields rejected by Build and accepted only by explicit escape |
| Pump request/result/attempt builders | Defaults and named bounds; four exact attempt kinds; completed empty pass differs from refusal | Zero/overflow item bound, zero/>5 min budget, invalid surface, negative result count/elapsed, invalid result/problem-code pairings |
| Typed Work request | Match direct definition.CreateRequest for identity, codec metadata, encoded payload and fingerprint | Null payload, invalid scope/command/key, rejecting codec, invalid metadata; original production exception preserved |
| Native envelope | Exact CreateNative outcome, correlation and fence identity | Null or invalid execution identity, unsafe reason/metadata |
| Health fake | Snapshot sequence, exact cancellation tokens, delegate return | Caller cancellation before/after invocation, delegate exception, immutable history |
| Pump fake | Default completed empty pass; four outcomes; exact request, surfaces, limits | Delegate exception, cancellation before/after invocation, overlapping calls complete in reverse order |
| Drain fake | Begin, resume, repeated transitions, ordered history | Caller cancellation and delegate failure retain original error |
| Scenario assessment | Successful provider result accepted at the method's terminal decision point publishes under lock; newer accepted assessment replaces it; repeated/concurrent pump capture | No assessment raises explicit InvalidOperationException; failed/canceled/timed-out assessment does not replace success; out-of-order and late-after-timeout completion |
| Scenario admission | Exactly one call using exact request/assessment for every assessed health state; completed empty pass and refusal remain distinct | Negative or stale advisory assessment still reaches admission; provider returns Refused, Unavailable, or Incompatible; no synthetic result or check-then-act gate |
| Scenario deadlines | TimeProvider.GetTimestamp/GetElapsedTime controls per-observation and shared overall monotonic budget; terminal check orders completed task, caller cancellation, overall expiry, observation expiry | Wall-clock jumps do not alter budget; completion/cancellation/deadline ties; timeout before and after invocation; provider ignores cancellation and returns later; budget does not reset on reuse |
| Timed-out invocation handle | Post-invocation timeout exposes sequence, exact request/assessment and repeat-awaitable Completion; scenario snapshot retrieves handle after reuse | Late Completed/Refused/Unavailable/Incompatible, canceled versus faulted Completion, synchronous and asynchronous throw, timeout never cancels provider token, pre-invocation timeout has no handle |
| Scenario handle retention | ClearCompletedPumpInvocations removes terminal handles atomically but preserves in-flight handles | Clear/complete race, shallow payload reference lifetime, provider resources stay available until handles finish |
| Histories | Atomic immutable copy ordered by invocation start; frozen scalar metadata and completion on same entry | Snapshot/clear race, in-flight call from prior generation stays absent, payload mutation changes shallow reference but never frozen metadata, payload values never formatted |
| Contract observations | Definition identity, codec facts, safety/retry default, registry exact presence, binding identity | Missing/duplicate registration, codec rejection, wrong name/version; original exceptions |

Use gated TaskCompletionSource schedules and fake time; no wall-clock sleeps for race assertions. For fake-clock tests add Microsoft.Extensions.TimeProvider.Testing only to the test project.

Await each timed-out invocation's Completion in the test teardown path so a late provider failure is observed. Check that scenario reuse leaves earlier in-flight handles reachable, completed handles may be cleared, and a post-permit exception remains ambiguous until real-provider recovery. Test fake delegates that honor and ignore caller cancellation, as well as synchronous throws.

## Real provider conformance

Run targeted PostgreSQL tests for one real claim, completion, stale-worker recovery, and post-permit ambiguous result. Assert shared observation fields against the real provider rather than treating the fake as evidence for storage or effect-permit behavior. A post-permit failure must leave status unknown until the provider's recovery path establishes truth.

## Packed external consumer

Extend Durable/verify-packed-consumers.sh to pack Testing with all dependencies into a fresh local feed, restore an xUnit consumer from that feed, run its health/refusal/empty-pass assertions, verify the restored nupkg bytes, and inspect project.assets.json for the expected graph and forbidden PostgreSQL, ASP.NET Core, Testcontainers, assertion-library, and test-framework dependencies in the Testing package closure. Keep xUnit solely in the consumer/test project. Record the install-to-first-assertion time; target five minutes, report measured time rather than claiming it in advance.

## Commands and exit evidence

Run the affected Testing and PostgreSQL test projects with dotnet test; run Durable/verify-packed-consumers.sh; run the repository API snapshot/package checks, formatting check, and practical ./scripts/coverage-solution.sh. Exact project and script commands should be finalized when the additive project paths exist. If local PostgreSQL or toolchain is unavailable, record the missing gate rather than treating it as passed.
