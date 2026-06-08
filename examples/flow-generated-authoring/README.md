# AppSurface Flow Generated Authoring Example

This example shows the generated-authoring path for AppSurface Flow. The app defines a partial flow specification with `[FlowAuthoring]`, typed input and output ports, generated outcome cases, a generated envelope context, a generated graph mapping helper, and generated adapters that lower into the existing in-memory runner.

Run it from the repository root:

```bash
dotnet run --project examples/flow-generated-authoring/FlowGeneratedAuthoringExample.csproj
```

Expected output:

```text
Waiting: approval-submitted, timeout: 5m
Completed after re-entry: approved
Faulted: approval.denied
Timed out: timed-out
```

The first run uses the compact `BuildDefinition(nodeInstances...)` overload. The generator infers the graph because each `Next` outcome emits a typed port that exactly one node accepts:

```text
ApprovalOpened -> IntakeNode
ReviewRequested -> ReviewNode
ApprovalCompleted -> terminal
```

The same program also builds an explicit definition with `BuildDefinition(graph => ..., nodeInstances...)`. That overload lists every generated `Map...To...` and `Mark...Terminal()` method at the call site, which is useful when you want the graph to be visibly reviewed.

Context types in generated authoring are ports, not automatically one state class per node. `IntakeNode` consumes `ApprovalOpened` and emits `ReviewRequested`; `ReviewNode` consumes `ReviewRequested`, may emit `ReviewRequested` again for a re-entrant branch, and completes with `ApprovalCompleted`. The type is the plug shape that guides compatible connections.

Use generated authoring when missing transition mappings should fail early. Use the compact overload when typed ports make the graph unambiguous. Use the explicit graph overload when call-site graph visibility matters. If a `Next` output port matches multiple node input ports, create distinct port types so the graph is unambiguous. Use the low-level `IFlowNode<TContext>` contract when you need hand-written runtime nodes, custom graph construction, or very small tests that do not need generated cases.
