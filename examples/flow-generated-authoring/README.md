# AppSurface Flow Generated Authoring Example

This example shows the generated-authoring path for AppSurface Flow. The app defines a partial flow specification with `[FlowAuthoring]`, generated outcome cases, a generated envelope context, a generated graph mapping helper, and generated adapters that lower into the existing in-memory runner.

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

The first run waits with a typed `StartState`, resumes into a typed `ReviewState`, takes the generated `again` transition back through `ReviewNode`, and completes with `DoneState`. The second and third resumes show the generated `Fault` and `TimedOut` cases without falling back to stringly `FlowNodeOutcome<TContext>` authoring.

Use generated authoring when missing transition mappings should fail early. The compact `BuildDefinition(nodeInstances...)` overload applies the generated default mapping; the explicit `BuildDefinition(graph => ..., nodeInstances...)` overload lets samples and applications list every `Map...To...` or `Mark...Terminal()` outcome mapping at the call site. Use the low-level `IFlowNode<TContext>` contract when you need hand-written runtime nodes, custom graph construction, or very small tests that do not need generated cases.
