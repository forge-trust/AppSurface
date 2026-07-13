# ForgeTrust.AppSurface.Flow

`ForgeTrust.AppSurface.Flow` provides stable contracts for typed long-running app processes.

## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package from a prerelease feed, check the [package chooser](../../packages/README.md) and [release hub](../../releases/README.md) for current release risk, migration guidance, and readiness.

## What It Includes

- `FlowNodeOutcome<TContext>` and its append-only `Next`, `Wait`, `TimedOut`, `Complete`, `Fault`, and typed `Activity` outcomes.
- `FlowActivityCallsite<TWork, TResult>`, `IFlowActivityRequest<TContext>`, and `FlowActivityWorkResult<TResult>` for reflection-free work dispatch and result resumption.
- `IFlowNode<TContext>`, `FlowExecutionContext<TContext>`, `FlowResumeEvent`, and `IFlowTransitionEvaluator<TContext>`.
- `FlowGraphBuilder<TContext>` and immutable `FlowDefinition<TContext>` graph validation.
- `IFlowDefinitionRegistry` for host and adapter lookup by context type, flow id, and version.
- `IFlowRunner<TContext>` and `InMemoryFlowRunner<TContext>` for local tests, including explicit activity pause/resume.
- Generated-case authoring attributes: `FlowAuthoringAttribute`, `FlowNodeAttribute`, `FlowStartAttribute`, `FlowOutcomeAttribute`, `FlowGraphMappingAttribute`, and `FlowOutcomeKind`.
- `IFlowTransformerNode<TInput, TOutcome>` and `FlowTransformerContext<TInput>` for generated authoring nodes.
- Passive `AppSurfaceFlowModule` service registration.

## What It Does Not Include

- Durable persistence, external event queues, timers, replay, storage, or activity execution. Durable hosts consume the contracts in this package.
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

### Typed activities

An activity is the only Flow outcome intended for external I/O. Generated authoring declares the persisted context plus typed work/result contracts. Contract versions and an explicit `CallsiteId` are recommended when instances will be durable:

```csharp
[FlowNode("notify", typeof(NotificationState))]
[FlowOutcome(
    "send-email",
    FlowOutcomeKind.Activity,
    typeof(NotificationState),
    typeof(SendEmailWork),
    typeof(SendEmailResult),
    CallsiteId = "approval.send-email",
    WorkContractVersion = 1,
    ResultContractVersion = 1)]
[FlowOutcome("done", FlowOutcomeKind.Complete, typeof(ApprovalCompleted))]
public partial class NotifyNode : IFlowTransformerNode<NotificationState, NotifyNodeOutcomes>
{
    public ValueTask<NotifyNodeOutcomes> ExecuteAsync(
        FlowTransformerContext<NotificationState> context,
        CancellationToken cancellationToken = default)
    {
        if (NotifyNodeOutcomes.SendEmailCallsite.TryGetResult(context.ActivityResult, out var result))
        {
            return ValueTask.FromResult<NotifyNodeOutcomes>(
                NotifyNodeOutcomes.Done(new ApprovalCompleted(result.MessageId)));
        }

        return ValueTask.FromResult<NotifyNodeOutcomes>(
            NotifyNodeOutcomes.SendEmail(
                new SendEmailWork(context.State.RequestId),
                context.State with { Status = "sending" }));
    }
}
```

The generator emits `SendEmailCallsite` and lowers the case to the same low-level `FlowNodeOutcome<TContext>.Activity(...)` contract. If `CallsiteId` is omitted, its stable default is `<node-id>.<outcome-name>`. The generated graph treats Activity like Wait: it pauses and later resumes the same node, so its output context type must match the node input context.

`InMemoryFlowRunner<TContext>` returns `FlowRunStatus.ActivityPending`; it does not execute work. A test executes or fakes the work, creates the typed result through the callsite, and resumes explicitly:

```csharp
var pending = await runner.RunAsync(definition, initialContext);
var work = (SendEmailWork)pending.Activity!.Work;
var activityResult = NotifyNodeOutcomes.SendEmailCallsite.CreateResult(
    new SendEmailResult("provider-message-123"));
var completed = await runner.ResumeActivityAsync(
    definition,
    pending.NodeId!,
    pending.Context!,
    activityResult);
```

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

### One-node host boundary

`IFlowTransitionEvaluator<TContext>` is the host-neutral durable boundary. Unlike `IFlowRunner<TContext>`, it evaluates exactly one node and never follows a `Next` transition in the same call:

```csharp
var transition = await evaluator.EvaluateAsync(
    definition,
    new FlowTransitionInput<ApprovalState>("review", persistedContext));
```

The evaluator maps every outcome to `FlowTransition<TContext>`, validates declared `Next` targets, and exposes activity metadata without reflection. A durable host must commit that transition before evaluating another node. For Activity, it must atomically persist the Flow context, activity command, and wait registration before dispatching external work.

## Decisions And Pitfalls

- Flow ids, versions, node ids, generated outcome names, activity callsite ids, and activity contract versions are durable identifiers. Treat them like persisted schema once real instances exist.
- Flow nodes are transition-only. A host may evaluate a node again if the process dies before its decision commits, so nodes must not call providers, write application state, read wall-clock time, or generate hidden random identifiers. Pass nondeterministic values through explicit context/resume contracts and return Activity for external effects.
- `IFlowTransitionEvaluator<TContext>` evaluates one node; `IFlowRunner<TContext>` may follow several in-memory `Next` transitions. Durable runtimes must use the evaluator and commit each decision, not call the multi-step runner.
- Generated authoring uses nominal context types as typed ports. Two record types with the same properties are different ports; one record type reused in multiple places is the same port.
- Generated authoring resolves `Next` targets by matching an outcome output context type to exactly one declared node input context, then exposes that transition through a generated `GraphBuilder.Map...To...` method. If no node or multiple nodes match, the generator reports an error.
- If two nodes can accept the same kind of work, give the branches distinct nominal port types unless the graph really should be ambiguous. The generator does not guess between two nodes with the same input context type.
- `Wait`, `TimedOut`, and `Activity` outcomes resume the same node, so their output port must be the node input port. `Fault` outcomes must carry `FlowFault`. Activity declarations must also provide concrete work and result types.
- The compact `BuildDefinition(nodeInstances...)` overload calls the generated default graph mapping. Prefer it when the typed ports make every `Next` transition unambiguous.
- The explicit `BuildDefinition(graph => ..., nodeInstances...)` overload requires every declared outcome to be mapped or marked terminal. Use it when graph visibility matters. `Complete`, `Fault`, `Wait`, `TimedOut`, and `Activity` outcomes use generated `Mark...Terminal()` methods because they do not declare `FlowNext<TContext>` targets in the low-level graph.
- Generated envelopes include concrete nullable context slots and a public serializer constructor so Durable Task JSON round-trip validation can inspect them.
- `FlowExecutionContext<TContext>` is an immutable value-type snapshot. Runners populate it for each node call so tight synchronous flows avoid allocating a reference context per step. Do not treat `default(FlowExecutionContext<TContext>)` as a valid execution context; structs can be default-created without flow ids, node ids, or state.
- Declare every `Next` target in `AddNode`. The builder validates missing targets, and runners reject undeclared runtime targets.
- The in-memory runner stops at waits and activities. It does not persist state, deliver events, create timers, or execute activity work.
- The in-memory runner uses prevalidated internal routing metadata from `FlowDefinition<TContext>` so local execution does not repeat graph-existence checks on every `Next` transition. This preserves the public `Nodes` graph view and the undeclared target diagnostic from `FlowDefinition<TContext>` construction.
- Flow benchmarks isolate runner orchestration overhead. Use Flow for graph safety, long-running process contracts, and durable-host alignment; use a direct loop when all you need is a pure in-process tight state machine.
- Use `FlowWait<TContext>.Timeout` as metadata for durable hosts. The core runner reports the timeout request but does not race timers.
- Keep context types serializer-friendly if you plan to use Durable Task. The Durable Task adapter validates JSON round-trips before evaluating nodes.
- Expected business failure belongs in the typed activity result contract. Technical exhaustion, provider ambiguity, and retry safety belong to the durable activity host; a Flow node must not infer that a timed-out provider call had no effect.
- `FlowActivityCallsite<TWork, TResult>.TryGetResult` returns false for absent or mismatched results. Use `GetResult` when a mismatch is a definition/runtime invariant violation and should throw a focused `FlowDefinitionException`.
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
| `ASFLOWA007` | Warning | A generated Flow node directly reads wall-clock time or creates hidden random input. | Pass time, identifiers, and random values through persisted context or resume contracts so replay evaluates the same durable decision. |
