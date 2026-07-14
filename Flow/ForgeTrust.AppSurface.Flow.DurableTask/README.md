# ForgeTrust.AppSurface.Flow.DurableTask

`ForgeTrust.AppSurface.Flow.DurableTask` maps AppSurface Flow definitions into durable orchestration decisions.

## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package from a prerelease feed, check the [package chooser](../../packages/README.md) and [release hub](../../releases/README.md) for current release risk, migration guidance, and readiness.

## What It Includes

- Passive `AppSurfaceFlowDurableTaskModule` that depends on `AppSurfaceFlowModule`.
- `IDurableTaskFlowRunner<TContext>` for evaluating one flow node through the core `IFlowTransitionEvaluator<TContext>` and returning a durable decision.
- `IDurableTaskFlowClient<TContext>` and `IFlowResumeAuthorizer` for authorizing external resume events before hosts raise Durable Task events.
- `DurableTaskFlowDecision<TContext>` and `DurableTaskFlowStep<TContext>` for node scheduling, typed activity scheduling/result resumption, wait, completion, fault, timeout, and late-event behavior.
- `FlowContextSerializationValidator` with a `System.Text.Json` serializer implementation.
- `FlowRetryPolicy` carried on schedule decisions so hosts can translate retry intent into Durable Task retry options.

## What It Does Not Include

- Durable Task worker or client hosting setup.
- Storage provider registration.
- Activity codec registration, executor registration, provider retry safety, or effect reconciliation.
- ASP.NET Core resume endpoints or authentication handlers.
- Semantic Kernel.

## Durable Boundary

Durable Task owns persistence, replay, timers, and external event delivery. This package owns the AppSurface mapping contract around those host responsibilities:

1. Resolve the typed flow definition by flow id and version.
2. Validate that the typed context can round-trip through the configured serializer.
3. Ask the shared core evaluator to execute the current node exactly once.
4. Validate the returned Flow context and map the host-neutral transition to a durable decision.

`FlowWait<TContext>` becomes `WaitForExternalEvent` with optional timeout metadata. Typed waits also preserve their
`IFlowEventCallsite` on the decision so a host can select an allowlisted payload codec from the durable contract name
and version before resuming the node. The adapter does not authorize or decode the event. A host should race that
external event against its durable timer. When an event arrives after the timer branch already won, `ResumeAsync`
returns `IgnoreLateEvent` by default.

`FlowActivity<TContext, TWork, TResult>` becomes `ScheduleActivity`. The decision exposes callsite id, declared work/result CLR types, contract versions, work, and persisted Flow context through `IFlowActivityRequest<TContext>` without reflection. A host integrates it as follows:

1. Persist the Flow decision and activity command before dispatch.
2. Resolve registered codecs and an executor from the declared CLR types and contract versions.
3. Execute using the host's provider-safety and idempotency rules.
4. Decode the terminal result, create `FlowActivityWorkResult<TResult>` through the registered callsite, and set `DurableTaskFlowStep<TContext>.ActivityResult` when evaluating the same node again.

```csharp
if (decision.Kind == DurableTaskFlowDecisionKind.ScheduleActivity)
{
    var request = decision.Activity!;
    // Persist and dispatch request.Work with the registered request.WorkType codec.
}

var resumedStep = new DurableTaskFlowStep<ApprovalState>(
    "approval",
    "1",
    instanceId,
    waitingNodeId,
    persistedContext)
{
    ActivityResult = SendEmailCallsite.CreateResult(decodedResult),
};
var nextDecision = await runner.RunNodeAsync(resumedStep, cancellationToken);
```

The adapter's context serialization validator covers the Flow context before and after node evaluation. It does not prove that activity work/results have registered durable codecs; the Durable Task host must fail registration or scheduling when those declarations are absent. `DurableTaskFlowStep<TContext>` rejects an evaluation that supplies both `ResumeEvent` and `ActivityResult` through the shared evaluator.

`AppSurfaceFlowDurableTaskOptions.NodeRetryPolicy` can attach one retry policy to scheduled node work. The adapter does not execute retries itself; the Durable Task host translates the policy into its worker/client retry options.

`NodeRetryPolicy` does not apply to `ScheduleActivity`. Activity retries must follow the executor's declared provider-safety contract; a generic node retry cannot establish that an ambiguous external call was not applied.

## Authorization

The default `DenyAllFlowResumeAuthorizer` rejects every resume event. Hosts must register their own `IFlowResumeAuthorizer` before exposing HTTP endpoints, queues, webhooks, or browser actions that resume durable flows.

```csharp
services.AddSingleton<IFlowResumeAuthorizer, MyResumeAuthorizer>();
```

Authorization should consider the flow id, version, durable instance id, waiting node id, event name, caller identity, and any app-specific metadata. Instance ids and event names are not authorization.

## Pitfalls

- Do not mutate Durable Task host state from `AppSurfaceFlowDurableTaskModule`; it is intentionally passive.
- Do not execute providers inside Flow nodes. Node evaluation may repeat before a decision commits; only dispatch work after the `ScheduleActivity` decision is durable.
- Do not derive activity codec selection from runtime object inspection. Use the declared `WorkType`, `ResultType`, and contract versions, and treat the callsite id as persisted schema.
- Do not replay an activity result into a different node or callsite. Validate it against the persisted wait before setting `ActivityResult`; the typed callsite also fails closed on type, identity, or version mismatch.
- Do not treat late events as success. The default behavior ignores them because delayed external events are expected in timer races.
- Do not discard `EventCallsite` from typed wait decisions. Validate its exact event name and durable payload contract against the persisted wait before decoding and resuming; CLR `PayloadType` is runtime codec metadata, not a wire identifier.
- Decision factories validate and snapshot extensible event-callsite and activity-request metadata. They do not deep-clone application work or context values, so persist those values atomically before dispatch and avoid mutable custom request implementations.
- Do not register Semantic Kernel in this package. Agentic flow authoring belongs in samples or a future package, not the Durable Task adapter.
- Validate context serialization before starting durable instances so replay failures happen during local tests or startup verification, not halfway through a production process.
