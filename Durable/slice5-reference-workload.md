# Slice 5 Work-first Schedule reference workload

This is the checked PostgreSQL Gate A proof for the public preview. It creates a one-time Work Schedule, runs one
manual pass, and verifies one immutable occurrence and one accepted Work. It proves durable identity and target
acceptance; external provider effects remain governed by the Work registration's provider-safety policy.

## Prerequisites

- PostgreSQL 16+ through Docker/Testcontainers, or `APPSURFACE_POSTGRES_TEST_CONNECTION` pointing to PostgreSQL 16+.
- A migration-owner applies `0001` through `0009`, then runs
  [`configure-postgresql-roles.sql`](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql) with distinct migration-owner, dispatcher, and
  runtime roles.
- The Schedule processor receives a dispatcher data source, a separate runtime data source, immutable Work registry,
  validated `PostgreSqlDurableWorkOptions`, and
  `PostgreSqlDurableScheduleOptions` containing the exact runtime role name.
- The schedule processor must use the same dispatcher/runtime role transition as production: apply migrations (through 0009) and run the role recipe before enabling the hosted host; keep worker host disabled during this reference workload and use only a manually bounded pass.

Historically, this workload also executes against
`postgres:17.5@sha256:aadf2c0696f5ef357aa7a68da995137f0cf17bad0bf6e1f17de06ae5c769b302` for preserved proof evidence.
Current default strict verification uses
`postgres:16.5@sha256:53f3e608f9475ce120ced2d0f430b89458d7faa28530e0b0977a6af64d294877`.

Run the proof from the repository root:

```console
./Durable/verify-postgresql.sh --quick --schedule
```

The named test is `PostgreSqlDurableScheduleTests.AtWorkSchedule_CapturesOneAnchor_DeduplicatesCreate_AndMaterializesOneWork`.
It creates an `At` Schedule with the default `QueueOne` + `RunOnce` policies, retries the same create request, and runs
one `ProcessDueAsync` call. Expected durable facts are:

| Fact | Expected result |
|---|---|
| Schedule create | `Created`, then `Duplicate` with the same persisted commit timestamp |
| Dispatch pass | one claimed queue row, one recorded occurrence, one materialized Work target |
| Ledger | one `schedule_occurrence` row |
| Work store | one accepted Work aggregate |
| Public snapshot | no next occurrence for the terminal one-time Schedule |

The provider also checks `After` and unanchored `Every` explanation semantics without opening a database. The stored
`transaction_timestamp()` anchor—not caller wall clock and not later Work `accepted_at`—defines their first due time.

`PostgreSqlDurableScheduleTests.EveryQueueOne_CoalescesWhileWorkIsNonTerminal_AndRequeuesWhenWorkBecomesTerminal`
proves the default [`QueueOne` policy](schedule-protocol-v1.md#occurrence-materialization): one active Work target
holds the slot, a later nominal instant becomes one coalesced occurrence, and the Work terminal transaction requeues
that pending occurrence for the next bounded Schedule pass.

For the dispatcher-facing selection and gating rules, this slice also follows
[`0009_work_contract_discovery.sql`](https://github.com/forge-trust/AppSurface/blob/main/Durable/ForgeTrust.AppSurface.Durable.PostgreSql/Migrations/0009_work_contract_discovery.sql) and
[`appsurface_durable.discover_work_dispatch(text[], text[], integer)`](https://github.com/forge-trust/AppSurface/blob/main/Durable/ForgeTrust.AppSurface.Durable.PostgreSql/Migrations/0009_work_contract_discovery.sql) (payload-free).

## Operating limits

`PostgreSqlDurableScheduleProcessRequest.MaximumSchedules` defaults to `1` and is limited to `128`. A zero-result pass
is success. Cancellation is checked before the next dispatch lease, so it never undoes a committed Schedule fact.
Do not place an unbounded loop around this API in an HTTP request and do not register a hosted service; that activation
boundary remains Slice 6.

## Deferred proof

This workload does not claim CronosV1, Flow targets, hosted scheduling, or the five deterministic child-process crash
barriers. Flow is blocked until its start operation accepts a caller-owned transaction; Cron requires a pinned evaluator
and time-zone compatibility proof. Those gates must pass before the increment is called full Slice 5 completion.

## Failure interpretation

- `ASDUR119` means PostgreSQL worker activation could not snapshot its custom Work registry. Correct the complete,
  stable `RegisteredContracts` list and restart the host; migration and role failures have separate diagnostics.
