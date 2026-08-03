# Slice 5 reconstruction ledger

This ledger records the evidence used to build Schedule persistence. It prevents
historical or preview-only fragments from outranking the landed Durable protocols.

| Evidence | Classification | Use in Slice 5 |
| --- | --- | --- |
| `Scheduling/DurableSchedule.cs` | retained | Authoritative `At`, `After`, `Every`, and CronosV1 public shapes. |
| `Scheduling/IDurableScheduleClient.cs` | retained | Authoritative client, snapshot, lifecycle, list, and explanation contract. |
| `Scheduling/DurableScheduleTarget.cs` | retained | Typed Work/Flow target construction and encoded snapshot boundary. |
| Work migration and store (`0001`, `PostgreSqlDurableWorkStore`) | adapted | Reuse scoped RLS, idempotent command, dispatch, history, and Work transaction-writer conventions. |
| Flow migration and store (`0003`, `PostgreSqlDurableFlowStore`) | adapted | Reuse fenced Flow semantics only; its self-owned start transaction is not a Schedule bridge seam. |
| `PostgreSqlDurableWorkTransactionWriter` | retained | Gate A caller-owned transaction bridge for Work only. |
| Existing Work/Flow TestHost barriers | adapted | Establish subprocess evidence conventions; they do not cover Schedule commits. |
| Existing V2 mixed-version harness | replaced-by-current-contract | It is insufficient for a v3 Work-and-Flow compatibility claim. |
| Hosted processing loops or provider I/O | deferred-to-slice-6 | Slice 5 exposes only a bounded manual processor. |
| Generic scheduler/repository/event-sourcing framework | removed-with-rationale | The protocol uses direct PostgreSQL facts and existing Durable identifiers. |

The checked reconstruction procedure is: create a Schedule target using the retained
public contract; capture its provider facts with the adapted Work protocol; and keep
each Flow or hosted-runtime claim behind its explicit feasibility or later-slice gate.
