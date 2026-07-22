# PostgreSQL Work protocol v1

This is the normative operation and lock manifest for Durable slice 3. It specifies observable behavior, transaction
boundaries, and ordering. Internal C# and SQL may be decomposed differently, but must preserve these invariants. The
package starts no worker; tests and later hosting code drive one operation at a time.

## Authoritative records

PostgreSQL is the only durable truth. The Work/shared schema owns store metadata and migration history, scope tombstones
and generation, payload-free dispatch, Work and its immutable acceptance snapshot, append-only history/stale
observations, effect permits, and runtime epoch history.

- A **terminal fact** is the immutable history row committed with a terminal Work state.
- A **permit** records the exact attempt, lease generation, scope generation, runtime epoch, and activity identity that
  authorized one provider operation. A claim alone never authorizes an external effect.
- A **quarantined observation** is append-only evidence from a stale completion. It never changes current Work.
- `ActivityId` is immutable and is also the Provider contract's `ProviderKey`; there is no independent key hash.
- Notifications are optional payload-free latency hints. Discovery remains authoritative.

## Global lock order

Every operation locking more than one protocol record uses: **scope -> Work -> dispatch -> permit -> history**.

Schema application instead takes one package session advisory lock before reading migration history and holds it through
history validation, migration transactions, and final status validation. Runtime mutations take the shared,
transaction-scoped form of that lock before validating the active epoch, so migration or epoch rotation waits for
in-flight mutations and a stale epoch cannot commit afterward.
Provider I/O never occurs while a database transaction or connection is held.

## Operation manifest

| Operation | Transaction and locks | Required validation | Result and durable effects |
| --- | --- | --- | --- |
| Get schema status | Read-only deployment connection | Migration hashes and reader/writer ranges | Missing, compatible, inconsistent, too old/new; includes StoreId and nullable active epoch. |
| Generate script | No database connection | Caller supplies the exact reviewed installed version | Deterministic forward-only SQL for the selected pending migrations; never rerun blindly. |
| Apply migrations | Migration owner; session lock for full operation | History before/after each migration | Applies pending known migrations only; mismatch fails closed. |
| Initialize epoch | Deployment transaction under schema lock | StoreId, null active epoch, non-empty candidate/audit | One-time activation plus append-only history; never done by a writer. |
| Rotate epoch | Deployment transaction under schema lock | StoreId and expected active epoch CAS | Records previous/new epoch and audit; stale expectation changes nothing. |
| Accept Work | Caller/client transaction; scope then Work/dispatch | Target, StoreId, epoch, registry, fingerprint, active scope | Atomically creates generation-1 scope, Work, dispatch/history, or exact duplicate/conflict. |
| Discover | Short dispatcher transaction; payload-free access | Due time/state only | Bounded candidate identities; lost notification cannot hide Work. |
| Claim | Short scoped transaction; canonical lock order | Scope, revision, due state, epoch; classify expired permit | One winner advances attempt/lease; unsafe expired permits suspend. |
| Renew lease | Short scoped transaction; canonical lock order | Exact revision, attempt, lease/scope generation, epoch, owner, lifetime | Extends exact active lease; stale renewal changes nothing. |
| Prepare | No store transaction | Registry decodes immutable contract/payload/safety | Produces landed prepared invocation; unavailable history suspends `ASDUR109` before permit. |
| Acquire permit | Short scoped transaction; canonical lock order | Exact current claim, active scope/epoch, no pre-effect cancellation | Commits one immutable exact-fence permit before provider I/O. |
| Complete | Short scoped transaction; canonical lock order | Exact permit/claim; result matches registration | Commits terminal fact, retry, or suspension; stale result is quarantined only. |
| Cancel | Short scoped transaction; canonical lock order | Fingerprint and current state | Before permit terminalizes canceled-before-effect; after permit records intent and follows safety. |
| Disable scope | One scoped transaction; canonical lock order | Active generation and actor/reason | Permanent tombstone and atomic suspension of nonterminal Work. |
| Recover expired permits | Payload-free discovery then scoped candidate transaction | Every expired permit and exact Work fence | Safe classes may reclaim; unsafe classes suspend without repeating effects. |
| Reconcile/resolve/release | Short scoped transaction; canonical lock order | Host authorization, command fingerprint, state/evidence/audit | Applies only the safety transition supported by evidence and appends history; recovery release moves exact current ambiguous evidence with the authorized epoch, safely releases when the current attempt has none, and rolls back if an expected exact move fails. |

## Acceptance identity matrix

`CommandId` and `IdempotencyKey` are both mandatory and independently unique within a scope.

| Resolution | Fingerprint | Outcome |
| --- | --- | --- |
| Neither key resolves | N/A | Accept new Work even if an unrelated row has the same fingerprint. |
| One key resolves | Same known schema/hash | Return `Duplicate` with original WorkId, CommandId, revision, and accepted time. |
| Both keys resolve to the same row | Same known schema/hash | Return that same `Duplicate`. |
| One/same-row keys resolve | Different or unknown fingerprint | Fail `ASDUR102`; do not mutate. |
| Keys resolve to two rows | Any | Fail `ASDUR102`; do not choose a winner or mutate. |

Concurrent first use creates one active generation-1 scope. A disabled scope is a permanent tombstone and returns
`ASDUR107`; it is never silently reactivated.

## Provider-effect safety

| Safety | Unknown outcome after permit | Applied evidence | Not-applied evidence |
| --- | --- | --- | --- |
| `Idempotent` | Exact-fence recovery may retry. | Encoded result may terminalize success. | Retry under a new claim/permit. |
| `ProviderKeyed` | Recovery may retry with the same ActivityId/ProviderKey. | Encoded result may terminalize success. | Retry with the same stable key. |
| `ReconcileBeforeRetry` | Suspend for reconciliation. | Reconciliation may terminalize with encoded result. | Only explicit proof may authorize retry. |
| `ManualResolution` | Suspend for manual resolution. | `Applied` requires encoded result and terminalizes. | `ProvenNotApplied` may authorize retry only via operator command. |

No unknown post-permit outcome becomes `FailedTerminal`. Cancellation after permit does not change this matrix.

## Retry v1

Only `exponential-v1` is supported. After failed attempt `n >= 1`:

```text
delay = min(MaximumRetryDelay, InitialRetryDelay * 2^(n - 1))
eligible_at = PostgreSQL clock_timestamp() + delay
```

Calculation is cap-first and overflow safe, with no jitter or provider Retry-After. Unknown algorithms and invalid
bounds fail before mutation.

## Caller-owned transaction contract

Local preflight failures before SQL preserve transaction usability: inactive/wrong target, empty epoch/StoreId,
unregistered Work, invalid payload/policy, or unsupported retry policy. Expected database domain outcomes also preserve
usability: disabled scope, exact duplicate, command conflict, or stale fence.

PostgreSQL errors, timeout, network loss, server cancellation, or any aborting SQLSTATE require rollback. The writer never
commits, rolls back, disposes, replaces, or opens a second connection for the caller's transaction. Savepoints are
unsupported. Endpoint/database comparison is a configuration guard; durable identity is the StoreId read through the
supplied transaction and compared with the deployment-time value.

## Security and diagnostics

The migration owner alone owns DDL. The dispatcher reads payload-free discovery. The scoped runtime has minimum DML
under forced RLS; `PUBLIC` has no privileges and runtime roles have neither ownership nor `BYPASSRLS`. Transaction-local
scope context is defense in depth, not application authorization: a credential holder can deliberately select a scope.

Diagnostics preserve `ASDURxxx`, concrete Npgsql/PostgreSQL exception types, inner exceptions, stack traces, and SQLSTATE,
but exclude connection strings, credentials, parameter values, payloads, and provider responses. See the
[`ASDURxxx` catalog](../troubleshooting/durable-diagnostics.md).

## Scale and contention gates

The strict PostgreSQL verification gate uses PostgreSQL 17.5 and must prove all of the following:

- discovery uses `ix_dispatch_due`, without a sequential scan, across 100,000 Work/dispatch rows in 100 scopes;
- 32 simultaneous claimers sharing an eight-connection pool complete within 30 seconds and produce one lease owner;
- disabling a scope projects 10,000 pre-effect Work items and dispatch rows within 30 seconds; and
- concurrent migration owners serialize, canceled lock waiters do not leak the session lock, and a terminated migration
  connection can retry safely on a new physical session.

These are regression gates, not throughput promises. Operators must benchmark their own payload sizes, retention,
indexes, PostgreSQL settings, and contention patterns before setting production capacity.
