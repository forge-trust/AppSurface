# ForgeTrust.AppSurface.Flow

`ForgeTrust.AppSurface.Flow` provides stable contracts for typed long-running app processes.

## What It Includes

- `FlowNodeOutcome<TContext>` and the sealed outcome records `FlowNext<TContext>`, `FlowWait<TContext>`, `FlowTimedOut<TContext>`, `FlowComplete<TContext>`, and `FlowFaultOutcome<TContext>`.
- `IFlowNode<TContext>`, `FlowExecutionContext<TContext>`, and `FlowResumeEvent`.
- `FlowGraphBuilder<TContext>` and immutable `FlowDefinition<TContext>` graph validation.
- `IFlowDefinitionRegistry` for host and adapter lookup by context type, flow id, and version.
- `IFlowRunner<TContext>` and `InMemoryFlowRunner<TContext>` for local tests and examples.
- Passive `AppSurfaceFlowModule` service registration.

## What It Does Not Include

- Durable persistence, external event queues, timers, replay, or storage.
- ASP.NET Core endpoints, middleware, authorization handlers, or UI.
- Semantic Kernel. Keep agentic or LLM-assisted authoring in samples or future packages until the core process contract has settled.
- Required preview C# union syntax.

## Basic Shape

```csharp
var definition = FlowGraphBuilder<ApprovalState>
    .Create("approval-request")
    .AddNode("review", new ApprovalReviewNode())
    .StartAt("review")
    .Build();

var result = await runner.RunAsync(definition, new ApprovalState("created"));
```

Nodes return explicit discriminated outcomes:

```csharp
public sealed class ApprovalReviewNode : IFlowNode<ApprovalState>
{
    public ValueTask<FlowNodeOutcome<ApprovalState>> ExecuteAsync(
        FlowExecutionContext<ApprovalState> context,
        CancellationToken cancellationToken = default)
    {
        if (context.ResumeEvent is null)
        {
            return ValueTask.FromResult<FlowNodeOutcome<ApprovalState>>(
                FlowNodeOutcome<ApprovalState>.Wait("approval-submitted", context.State));
        }

        return ValueTask.FromResult<FlowNodeOutcome<ApprovalState>>(
            FlowNodeOutcome<ApprovalState>.Complete(context.State with { Status = "approved" }));
    }
}
```

## Decisions And Pitfalls

- Flow ids, versions, and node ids are durable identifiers. Treat them like persisted schema once real instances exist.
- Declare every `Next` target in `AddNode`. The builder validates missing targets, and runners reject undeclared runtime targets.
- The in-memory runner stops at waits. It does not persist state, deliver events, or create timers.
- Use `FlowWait<TContext>.Timeout` as metadata for durable hosts. The core runner reports the timeout request but does not race timers.
- Keep context types serializer-friendly if you plan to use Durable Task. The Durable Task adapter validates JSON round-trips before evaluating nodes.
- Native C# discriminated unions can become authoring sugar later, but this package intentionally ships stable sealed records today.
