# AppSurface Flow

AppSurface Flow is the typed long-running process surface for AppSurface. It lets package authors describe a process as a stable graph of nodes, run that graph locally for tests and examples, then map the same node outcomes into durable orchestration decisions.

## Packages

| Package | Use when | Includes | Does not include |
| --- | --- | --- | --- |
| `ForgeTrust.AppSurface.Flow` | You need typed flow contracts, generated-case authoring, and an in-memory runner for local testing. | `FlowNodeOutcome<TContext>`, `IFlowNode<TContext>`, generated authoring attributes, `IFlowTransformerNode<TInput, TOutcome>`, graph builder/definition, definition registry, passive module, and in-memory runner. | Durable persistence, timers, external-event buffering, ASP.NET endpoints, Semantic Kernel, or background workers. |
| `ForgeTrust.AppSurface.Flow.DurableTask` | You are adapting Flow definitions to a Durable Task host. | Passive module, durable decision runner, resume-event client/authorization contract, timeout and late-event mapping, and context serialization validation. | Durable Task worker/client hosting, storage providers, auth handlers, endpoints, or Semantic Kernel. |

Start with `examples/flow-generated-authoring` when you want generated outcome cases and build-time graph safety. Use `examples/flow-approval-local` when you want to see the low-level node/runtime contract directly, then register the Durable Task adapter when real instances need replay, timers, persistence, and externally authorized resume events.
