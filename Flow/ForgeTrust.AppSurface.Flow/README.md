# ForgeTrust.AppSurface.Flow

`ForgeTrust.AppSurface.Flow` provides stable contracts for typed long-running app processes.

## Release Guidance

AppSurface has cut the first coordinated `v0.1.0` release candidate. Before installing this package from a prerelease feed, read the [v0.1.0 RC 3 release note](../../releases/v0.1.0-rc.3.md) for current release risk, migration guidance, and package readiness.

## What It Includes

- `FlowNodeOutcome<TContext>` and the sealed outcome records `FlowNext<TContext>`, `FlowWait<TContext>`, `FlowTimedOut<TContext>`, `FlowComplete<TContext>`, and `FlowFaultOutcome<TContext>`.
- `IFlowNode<TContext>`, `FlowExecutionContext<TContext>`, and `FlowResumeEvent`.
- `FlowGraphBuilder<TContext>` and immutable `FlowDefinition<TContext>` graph validation.
- `IFlowDefinitionRegistry` for host and adapter lookup by context type, flow id, and version.
- `IFlowRunner<TContext>` and `InMemoryFlowRunner<TContext>` for local tests and examples.
- Generated-case authoring attributes: `FlowAuthoringAttribute`, `FlowNodeAttribute`, `FlowStartAttribute`, `FlowOutcomeAttribute`, `FlowGraphMappingAttribute`, and `FlowOutcomeKind`.
- `IFlowTransformerNode<TInput, TOutcome>` and `FlowTransformerContext<TInput>` for generated authoring nodes.
- Passive `AppSurfaceFlowModule` service registration.

## What It Does Not Include

- Durable persistence, external event queues, timers, replay, or storage.
- ASP.NET Core endpoints, middleware, authorization handlers, or UI.
- Semantic Kernel. Keep agentic or LLM-assisted authoring in samples or future packages until the core process contract has settled.
- Required preview C# union syntax.

## Basic Shape

### Generated authoring

Generated authoring is the package-first path when transition coverage should fail at build time. Think of context types as typed ports: a node declares the input port it accepts, and each outcome declares the output port it emits. The generator connects a `Next` outcome to the node whose input port has the same nominal type.

```csharp
[FlowAuthoring("approval")]
public partial class ApprovalFlow
{
    [FlowStart]
    [FlowNode("intake", typeof(ApprovalOpened))]
    [FlowOutcome("ready-for-review", FlowOutcomeKind.Next, typeof(ReviewRequested))]
    [FlowOutcome("approval-submitted", FlowOutcomeKind.Wait, typeof(ApprovalOpened))]
    public partial class IntakeNode : IFlowTransformerNode<ApprovalOpened, IntakeNodeOutcomes>
    {
        public ValueTask<IntakeNodeOutcomes> ExecuteAsync(
            FlowTransformerContext<ApprovalOpened> context,
            CancellationToken cancellationToken = default)
        {
            if (context.ResumeEvent is null)
            {
                return ValueTask.FromResult<IntakeNodeOutcomes>(
                    IntakeNodeOutcomes.ApprovalSubmitted(context.State));
            }

            return ValueTask.FromResult<IntakeNodeOutcomes>(
                IntakeNodeOutcomes.ReadyForReview(new ReviewRequested(context.State.RequestId)));
        }
    }

    [FlowNode("review", typeof(ReviewRequested))]
    [FlowOutcome("approved", FlowOutcomeKind.Complete, typeof(ApprovalCompleted))]
    public partial class ReviewNode : IFlowTransformerNode<ReviewRequested, ReviewNodeOutcomes>
    {
        public ValueTask<ReviewNodeOutcomes> ExecuteAsync(
            FlowTransformerContext<ReviewRequested> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ReviewNodeOutcomes>(
                ReviewNodeOutcomes.Approved(new ApprovalCompleted(context.State.RequestId)));
    }
}

public sealed record ApprovalOpened(string RequestId);
public sealed record ReviewRequested(string RequestId);
public sealed record ApprovalCompleted(string RequestId);
```

Then build a definition from node instances:

```csharp
var definition = ApprovalFlow.BuildDefinition(
    new ApprovalFlow.IntakeNode(),
    new ApprovalFlow.ReviewNode());
var result = await runner.RunAsync(definition, ApprovalFlow.CreateStartContext(new ApprovalOpened("APR-1001")));
```

The compact `BuildDefinition(nodeInstances...)` overload applies the generated default graph configuration. This works when each `Next` outcome output port has exactly one compatible node input port. In the example, `ready-for-review` emits `ReviewRequested`, and only `ReviewNode` accepts `ReviewRequested`, so the generator can infer that transition.

Use the explicit overload when you want the graph mapping visible at the call site:

```csharp
var definition = ApprovalFlow.BuildDefinition(
    graph => graph
        .MapIntakeNodeReadyForReviewToReviewNode()
        .MarkIntakeNodeApprovalSubmittedTerminal()
        .MarkReviewNodeApprovedTerminal(),
    new ApprovalFlow.IntakeNode(),
    new ApprovalFlow.ReviewNode());
```

The explicit overload must receive an inline lambda. Delegate variables and method groups are rejected so analyzer coverage works even when the flow spec is compiled in a reusable library and the host project consumes the generated public builder.

The generator emits outcome records such as `IntakeNodeOutcomes.ReadyForReviewOutcome`, one serializable envelope context such as `ApprovalFlowContext`, adapter nodes that implement `IFlowNode<ApprovalFlowContext>`, a `GraphBuilder` helper, and `BuildDefinition(...)` helpers that lower the authored graph into the existing runtime contract.

Use generated authoring for application workflows, package samples, and public APIs where missing transitions should break the build. Use the low-level runtime shape below for tiny tests, custom graph construction, or hand-authored nodes that intentionally own all runtime behavior.

### Low-level runtime contract

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
- Generated authoring uses nominal context types as typed ports. Two record types with the same properties are different ports; one record type reused in multiple places is the same port.
- Generated authoring resolves `Next` targets by matching an outcome output context type to exactly one declared node input context, then exposes that transition through a generated `GraphBuilder.Map...To...` method. If no node or multiple nodes match, the generator reports an error.
- If two nodes can accept the same kind of work, give the branches distinct nominal port types unless the graph really should be ambiguous. The generator does not guess between two nodes with the same input context type.
- `Wait` and `TimedOut` outcomes resume the same node, so their output port must be the node input port. `Fault` outcomes must carry `FlowFault`.
- The compact `BuildDefinition(nodeInstances...)` overload calls the generated default graph mapping. Prefer it when the typed ports make every `Next` transition unambiguous.
- The explicit `BuildDefinition(graph => ..., nodeInstances...)` overload requires every declared outcome to be mapped or marked terminal. Use it when graph visibility matters. `Complete`, `Fault`, `Wait`, and `TimedOut` outcomes use generated `Mark...Terminal()` methods because they do not declare `FlowNext<TContext>` targets in the low-level graph.
- Generated envelopes include concrete nullable context slots and a public serializer constructor so Durable Task JSON round-trip validation can inspect them.
- Declare every `Next` target in `AddNode`. The builder validates missing targets, and runners reject undeclared runtime targets.
- The in-memory runner stops at waits. It does not persist state, deliver events, or create timers.
- Use `FlowWait<TContext>.Timeout` as metadata for durable hosts. The core runner reports the timeout request but does not race timers.
- Keep context types serializer-friendly if you plan to use Durable Task. The Durable Task adapter validates JSON round-trips before evaluating nodes.
- Native C# discriminated unions can become authoring sugar later, but this package intentionally ships stable sealed records today.

## Generated Authoring Diagnostics

| Diagnostic | Default | Meaning | Fix |
| --- | --- | --- | --- |
| `ASFLOWA001` | Error | A declared outcome is missing generated graph mapping. | Add the matching `Map...To...` call for `Next` outcomes, add the matching `Mark...Terminal()` call for terminal outcomes, or use the compact generated `BuildDefinition(nodeInstances...)` overload. |
| `ASFLOWA002` | Error | A declared `Next` outcome targets a missing context. | Add the missing generated node or use the correct output context type. |
| `ASFLOWA003` | Error | A declared `Next` outcome matches more than one node input context. | Use distinct context types so the generated graph is unambiguous. |
| `ASFLOWA004` | Error | The authored flow has zero or multiple `[FlowStart]` nodes. | Mark exactly one generated node with `[FlowStart]`. |
| `ASFLOWA005` | Error | The generator cannot produce the envelope or graph because declarations conflict. | Make the flow and generated nodes partial, unique, and fully declared. |
| `ASFLOWA006` | Warning | Generated authoring is being mixed with low-level registration in a confusing way. | Keep generated definitions and hand-built definitions as separate entry points. |
