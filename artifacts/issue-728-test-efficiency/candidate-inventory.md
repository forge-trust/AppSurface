# Issue #728 coverage-efficiency candidate inventory

Every actual scheduler barrier receives a row before it is changed. Do not add a lifecycle timer or modify a fixture until the exact coverage-step baseline leaves two candidates indistinguishable.

| Candidate ID | Suite / project | Resource and current owner | Setup / cleanup operations | Mutable-state leak vector | Measured critical-path evidence | Proposed boundary | Isolation, reset, and replacement contract | Testcontainers / external applicability | Failure-injection identity and assertion | Admit / reject | Owner |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| pending | pending | pending | pending | pending | pending | pending | pending | pending | pending | pending | pending |

## Admission checklist

- The candidate has an actual-serial-set row and measured expected coverage-step effect.
- The owner, reset/replacement contract, and every mutable state vector are explicit.
- PostgreSQL proves Testcontainers and externally configured paths separately. A post-`CREATE DATABASE`, pre-ownership failure compensates cleanup; data-source, container, and external-drop cleanup are best-effort and aggregate failures only after every cleanup attempt.
- RazorWire and DevAuth capture owned PID/profile/context paths and prove absence after launch, readiness, warmup, browser-start, timeout, and disposal failures.
- The targeted project passes ten times, including a recorded randomized order where supported, and the full gate passes twice.
- A candidate that fails or flakes is rejected until root cause and a fresh complete safety protocol are recorded.

## Non-goals

- No global container, mutable database/schema, browser context, process, or cache reuse.
- No CI fan-out, public telemetry protocol, GitHub Check, or persistent benchmark product.
- `CoverageRun` terminal coverage-path semantics and Tailwind’s static-override serialization gap stay separately scoped unless this inventory proves direct relevance.
