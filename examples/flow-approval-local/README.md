# AppSurface Flow Approval Example

This example shows the first local-development path for AppSurface Flow: define a typed graph, run until a wait, then resume the same node with an external event.

Run it from the repository root:

```bash
dotnet run --project examples/flow-approval-local/FlowApprovalLocalExample.csproj
```

Expected output:

```text
Waiting: approval-submitted
Completed: approved
```

Use the in-memory runner for unit tests, examples, and hello-world exploration. Move to `ForgeTrust.AppSurface.Flow.DurableTask` when the same graph needs durable persistence, timers, replay-safe orchestration decisions, and authorized external resume events.
