# Durable Work execution policies v1

Schema `12` raises the compatible reader/writer floor. Before applying migration `0012`, stop new submissions and drain every pre-schema-12 reader, writer, host, and activator; use the [migration and rollback checklist](migrations/execution-policies-v1.md). Do not stage the floor change as a mixed-version rolling upgrade.

| Package / schema state | Support boundary | Rollback boundary |
|---|---|---|
| Historical preview 8 package, schema `<= 11` | Supported only at its historical schema floor; it cannot read or write schema `12`. | Roll back while the persistent schema floor remains `<= 11`; stop submissions and drain before changing packages. |
| This execution-policy feature, schema `12` | Use the matching Core, Provider, and PostgreSQL feature packages at reader/writer floor `12`; older packages must be drained before migration. No prerelease package version is assumed here. | Stop accepting new policy Work; keep schema-12-compatible code available to drain, inspect, or reconcile accepted Work. |
| Future metadata schema floor `>= 12` | Packages must understand the persistent floor they encounter; a future floor may raise the minimum beyond `12`. | Roll back application behavior only with code compatible with the persistent floor; never restore a package below that floor. |

This guide chooses between the existing completion-relative retry behavior, an absolute execution deadline, and a fixed acceptance-relative attempt plan. The public contracts live in [`ForgeTrust.AppSurface.Durable`](ForgeTrust.AppSurface.Durable/README.md); PostgreSQL remains the authoritative admission and persistence boundary. The compiled adopter example is [`TypedWorkDefinitionProof.cs`](https://github.com/forge-trust/AppSurface/blob/codex/make-it-so-765-retry-deadlines/Durable/packed-consumers/Adopter/TypedWorkDefinitionProof.cs), and the fresh-feed package proof is [`verify-packed-consumers.sh`](https://github.com/forge-trust/AppSurface/blob/codex/make-it-so-765-retry-deadlines/Durable/verify-packed-consumers.sh).

## Choose the policy that matches the recovery promise

| Choice | Configure | Eligibility behavior | Use when |
|---|---|---|---|
| Legacy | Existing `DurableWorkRetryPolicy` and request/definition APIs | Existing backoff is calculated from the safe retry decision; an existing `dueAtUtc` remains the initial eligibility | Existing Work semantics and fingerprints must remain unchanged |
| Deadline-only | `DurableWorkExecutionPolicy.FromRetryPolicy(...)` plus `DurableExecutionDeadline` on the named execution-policy request API | Keeps legacy backoff; new admission closes at the earlier of the accepted elapsed horizon and absolute deadline | A fixed UTC window is required while retry spacing stays completion-relative |
| Planned | `DurableWorkExecutionPolicy.ForAttemptPlan(...)` with `DurableAttemptPlan` | Each slot is eligible at acceptance plus its fixed offset; no exponential delay or jitter is added | Recovery should follow a fixed schedule independent of prior attempt duration |

Keep existing calls on the legacy API when their timing contract is unchanged. Opt in through the named execution-policy APIs when a Work version needs a deadline or fixed-slot behavior. These are immutable request facts: configuration changes affect future Work only. Give changed semantics a new Work version and retain the registration needed to interpret historical Work.

## Define and submit planned Work

Plan offsets are zero-based; execution attempts remain one-based. This source-linked example uses the five-slot schedule `0, 5, 20, 60, 180` minutes, a 240-minute exclusive circuit, reconcile-before-retry safety, and an absolute deadline. The compiled source also checks legacy and deadline-only request construction.

<!-- appsurface:snippet id="durable-execution-policy-chooser" file="Durable/packed-consumers/Adopter/TypedWorkDefinitionProof.cs" marker="durable-execution-policy-chooser" lang="csharp" -->
```csharp
internal static void VerifyExecutionPolicyChooser()
{
    var scope = new DurableScopeId("execution-policy-proof-scope");
    var command = new DurableCommandId("execution-policy-proof-command");
    var input = new LedgerWork("entry-1");
    var legacy = ReconciledDefinition.CreateRequest(
        scope, command, "legacy-key", input, retryPolicy: ExplicitRetry);
    if (legacy.ExecutionPolicy.AttemptPlan is not null
        || legacy.ExecutionDeadline is not null
        || legacy.Fingerprint.SchemaId != "appsurface.durable.work.enqueue.v1")
    {
        throw new InvalidOperationException("Legacy request construction changed its policy or fingerprint schema.");
    }

    var deadline = new DurableExecutionDeadline(
        new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero));
    var deadlineOnly = DurableWorkRequest.CreateWithExecutionPolicy(
        scope,
        command,
        "deadline-only-key",
        OrdinaryDefinition.WorkName,
        "v2",
        OrdinaryDefinition.WorkCodec.Encode(new InvoiceWork("invoice-1002")),
        OrdinaryDefinition.ProviderSafety,
        DurableWorkExecutionPolicy.FromRetryPolicy(ExplicitRetry),
        deadline);
    if (deadlineOnly.ExecutionPolicy.AttemptPlan is not null
        || deadlineOnly.ExecutionDeadline != deadline
        || deadlineOnly.Fingerprint.SchemaId != "appsurface.durable.work.enqueue.v2")
    {
        throw new InvalidOperationException("Deadline-only construction must retain legacy backoff with the opt-in fingerprint.");
    }

    var retry = new DurableWorkRetryPolicy(
        maximumAttempts: 5,
        maximumElapsedTime: TimeSpan.FromHours(4),
        initialRetryDelay: TimeSpan.FromMinutes(5),
        maximumRetryDelay: TimeSpan.FromHours(1),
        leaseDuration: TimeSpan.FromMinutes(1),
        renewalCadence: TimeSpan.FromSeconds(15),
        maximumLeaseLifetime: TimeSpan.FromMinutes(10),
        backoffAlgorithm: "exponential-v1");
    var plan = new DurableAttemptPlan(
        "attempt-plan-v1",
        [TimeSpan.Zero, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20),
         TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(180)],
        TimeSpan.FromMinutes(240));
    var policy = DurableWorkExecutionPolicy.ForAttemptPlan(retry, plan);
    var definition = DurableWork.DefineWithExecutionPolicy<LedgerWork, LedgerResult>(
        "examples.ledger.reconcile", "v2",
        TypedWorkCodecs.LedgerWorkCodec, TypedWorkCodecs.LedgerResultCodec,
        DurableProviderSafety.ReconcileBeforeRetry, policy);
    var services = new ServiceCollection();
    services.AddDurableWork(definition.ExecutedBy<LedgerExecutor>()
        .ReconciledBy<LedgerReconciler>());
    var request = definition.CreateRequestWithExecutionPolicy(
        scope, command, "ledger-reconcile-v2", input,
        executionDeadline: deadline);
    using var provider = services.BuildServiceProvider();
    var registration = provider.GetRequiredService<IDurableWorkRegistry>()
        .GetRequired(definition.WorkName, definition.WorkVersion);
    if (!request.ExecutionPolicy.Equals(policy)
        || request.ExecutionDeadline != deadline
        || request.DueAtUtc is not null
        || request.Fingerprint.SchemaId != "appsurface.durable.work.enqueue.v2"
        || !registration.DefaultExecutionPolicy.Equals(policy))
    {
        throw new InvalidOperationException("Planned Work must preserve its named policy through request and registration.");
    }
}
```
<!-- /appsurface:snippet -->

The [API budget](api-budget.md#deterministic-execution-policy-v1-issue-765) and Core/Provider API snapshots list the additive constructors, factories, and nullable execution projections. `DurableWorkExecutionSnapshot` carries accepted facts through provider execution and inspection; it is descriptive and can become stale. It never grants a claim, permit, retry, or completion. PostgreSQL rechecks the current row, fence, effect permit, and cutoff under the authoritative lock.

## Validation and timing rules

`DurableAttemptPlan` accepts one through 256 offsets. The first is zero; later offsets are strictly increasing. Its positive `MaximumCircuitDuration` must be strictly greater than the final offset, so the final slot can be admitted before the exclusive circuit cutoff. A planned policy requires `RetryPolicy.MaximumAttempts` to equal the number of offsets and `RetryPolicy.MaximumElapsedTime` to be at least the circuit duration. Its legacy delay fields remain part of the retry-policy snapshot but do not add backoff or jitter to planned slots.

Opt-in durations and UTC instants use PostgreSQL microsecond precision. Finer precision is rejected, not rounded into different persisted or fingerprinted values. Arithmetic is checked against the supported timestamp range. Lease and renewal additions compare the remaining time before adding, so a large duration can be safely bounded by the absolute deadline or the supported UTC range. Deadline-only retry eligibility is bounded by the immutable admission cutoff; reaching that cutoff closes admission instead of overflowing or extending the window. The legacy retry-policy and request constructors retain their existing validation and bytes.

For acceptance instant `A`, plan offset `O[i]`, circuit duration `C`, maximum elapsed time `M`, and optional exclusive deadline `D`:

```text
planned slot i eligibility = A + O[i]
planned admission cutoff   = min(A + C, A + M, D when present)
deadline-only cutoff       = min(A + M, D)
admit a new attempt        = due_at <= authoritative_now < admission cutoff
```

Acceptance is the insertion-time database timestamp, not request creation or transaction commit. A long caller-owned transaction consumes part of the execution window before another worker can see the Work. Slot zero uses that same stored instant. A planned request cannot also provide `dueAtUtc`; a deadline-only request may retain its existing initial due time.

Only a safe sequential retry transition can claim the next slot. A refusal consumes no slot; a recovered claim counts as an attempt under the existing claim counter. Downtime does not execute missed slots by itself. Once a slot is overdue, each ordinary safe retry transition can make the next slot immediately eligible, so several fast failures can consume overdue slots in a burst. If spacing must begin after each failure, use legacy backoff instead.

The execution deadline closes new claim, permit, invocation-admission, retry-release, and ordinary-success decisions at or after `D`. Renewals for an already admitted attempt remain capped by its original maximum lease lifetime and the absolute deadline; a plan circuit cutoff alone does not revoke that attempt's normal successful completion. Reconciliation or authorized proof recording can occur after cutoff without reopening execution.

PostgreSQL preserves two separate internal observations. `execution_admission_closed_at` and
`execution_admission_closed_reason` record the first observed admission cutoff and never overwrite an earlier cause. If
circuit or planned-slot exhaustion closes admission before `D`, those first-closure facts remain unchanged. The separate
`execution_deadline_reached_at` records the first authoritative observation at or after `D`, even when another cutoff
closed admission earlier. This retains both the original closure cause and the later deadline observation without
adding a public API member.

The provider's post-lock decision is the linearization point. A transaction can commit after that sampled instant. The runtime passes a deadline-bounded cancellation token and checks locally before invoking the executor, but cancellation is cooperative: executor code can be paused, ignore the token, or make more calls. Generic Durable cannot guarantee that no external network byte is sent after `D`. Use provider-enforced deadlines or fencing when the external system must enforce that stronger promise.

## Identity and outcome truth

Legacy requests without an execution policy or deadline keep the `appsurface.durable.work.enqueue.v1` fingerprint schema and existing canonical bytes. Opt-in requests use the v2 semantic fingerprint, including retry and lease settings, plan version and ordered offsets, circuit duration, deadline, and permitted initial due time. The database acceptance instant is never hashed. An identical duplicate resolves to its original acceptance even after expiry; changed policy, plan, due time, or deadline under the same command/idempotency identity conflicts.

An absolute deadline is not proof that an external effect did not happen. A current late encoded result is quarantined as evidence and does not become Work success. An unresolved permit remains ambiguous and follows the declared provider safety and reconciliation rule. Only exact proven-no-effect or authorized Applied/NotApplied reconciliation can establish effect truth; those proofs do not reopen a closed admission window. See the [`ASDURxxx` diagnostic catalog](../troubleshooting/durable-diagnostics.md#execution-deadline-and-attempt-plan-diagnostics) for timing codes and safe next actions.

A safe replay can leave an earlier admitted attempt unresolved even when the current attempt never reaches invocation. Authorized proof selects the most recent unresolved admitted permit under the Work lock, independently of the current attempt. History records that permit and its original attempt; the current unadmitted permit remains proven-no-effect. A NotApplied proof resolves only the selected permit. If another admitted effect remains uncertain, Work stays suspended and a new authorized command can resolve the next permit. Applied may establish the logical Work result; neither proof extends the accepted deadline or reopens admission. See the [Work safety and operator protocol](work-protocol-v1.md#provider-effect-safety) for command, revision, actor, and result-codec requirements.

## Schema and release migration

The opt-in fields are additive in PostgreSQL migration `0012`; legacy rows remain null and keep their old timing semantics. The schema compatibility floor advances to 12 for readers and writers. The runtime does not apply DDL. Preserve all released migration bytes through `0011`, use the matching provider package's role recipe, and require schema status/preflight before enabling execution.

Follow the ordered [execution-policy migration checklist](migrations/execution-policies-v1.md): stop new submissions, drain hosts that do not understand schema 12, apply the forward migration as migration owner, deploy compatible producer/reader/worker packages to every host sharing the store, confirm schema and epoch health, and only then accept a new immutable Work version. The old package cannot process schema 12 after the persistent floor advances. Rollback means stop new acceptance and keep compatible code available to drain or reconcile accepted Work; it does not mean reintroducing a pre-floor binary or rewriting migration history.

For release proof, compile examples through the fresh-feed [packed-consumer gate](https://github.com/forge-trust/AppSurface/blob/codex/make-it-so-765-retry-deadlines/Durable/verify-packed-consumers.sh). The PostgreSQL consumer proof uses the pinned real server and fixture-only controlled clock, and requires a passing TRX with zero skips. The exact historical package artifact check and the precompiled Core/Provider ABI consumer answer different compatibility questions; neither replaces the other.
