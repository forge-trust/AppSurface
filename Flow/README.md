# AppSurface Flow

AppSurface Flow is the typed long-running process surface for AppSurface. It lets package authors describe a process as a stable graph of nodes, run that graph locally for tests and examples, then map the same node outcomes into durable orchestration decisions.

## Packages

| Package | Use when | Includes | Does not include |
| --- | --- | --- | --- |
| `ForgeTrust.AppSurface.Flow` | You need typed input/output port flow contracts, generated-case authoring, and an in-memory runner for local testing. | `FlowNodeOutcome<TContext>`, `IFlowNode<TContext>`, generated authoring attributes, `IFlowTransformerNode<TInput, TOutcome>`, graph builder/definition, definition registry, passive module, and in-memory runner. | Durable persistence, timers, external-event buffering, ASP.NET endpoints, Semantic Kernel, or background workers. |
| `ForgeTrust.AppSurface.Flow.DurableTask` | You are adapting Flow definitions to a Durable Task host. | Passive module, durable decision runner, resume-event client/authorization contract, timeout and late-event mapping, and context serialization validation. | Durable Task worker/client hosting, storage providers, auth handlers, endpoints, or Semantic Kernel. |
| `ForgeTrust.AppSurface.Workers` | You need durable worker claim, completion, projection-repair, and privacy-safe diagnostic contracts before choosing runtime infrastructure. | Passive module, typed worker envelopes, outcome/retryability enums, projection contract, executor contract, safe metadata validation, and bounded repair requests. | Durable Task hosting, EF/Postgres runtime ownership, queue runners, scheduler services, endpoints, auth handlers, UI, or Semantic Kernel. |
| `ForgeTrust.AppSurface.Workers.DurableTask` | You are mapping worker contracts to Durable Task-facing orchestration decisions. | Passive module, worker chain runner, schedule/wait/repair/complete/fault/retry/timeout decisions, and retry policy metadata for host translation. | Durable Task worker/client hosting, storage providers, EF/Postgres runtime ownership, endpoints, auth handlers, UI, or Semantic Kernel. |

Start with `examples/flow-generated-authoring` when you want generated outcome cases, typed input/output ports, inferred graph mapping for unambiguous ports, and build-time graph safety. Use `examples/flow-approval-local` when you want to see the low-level node/runtime contract directly, then register the Durable Task adapter when real instances need replay, timers, persistence, and externally authorized resume events.

Use `ForgeTrust.AppSurface.Workers` when the process is better described as a durable worker chain with a side-effecting executor and a visible projection to repair. Add `ForgeTrust.AppSurface.Workers.DurableTask` when that chain should run through Durable Task orchestration decisions. EF/Postgres can remain app-owned product state or durable fact storage, but AppSurface does not provide an EF/Postgres worker runtime in this path.
