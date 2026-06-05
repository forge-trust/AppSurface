# AppSurface Flow Generated Authoring Example

This example shows the generated-authoring path for AppSurface Flow. The app defines a partial flow specification with `[FlowAuthoring]`, generated outcome cases, a generated envelope context, a generated graph mapping helper, and generated adapters that lower into the existing in-memory runner.

Run it from the repository root:

```bash
dotnet run --project examples/flow-generated-authoring/FlowGeneratedAuthoringExample.csproj
```

Expected output:

```text
Waiting: approval-submitted
Completed: approved
```

Use generated authoring when missing transition mappings should fail early. The compact `BuildDefinition(nodeInstances...)` overload applies the generated default mapping; the explicit `BuildDefinition(graph => ..., nodeInstances...)` overload lets samples and applications list every `Map...To...` or `Mark...Terminal()` outcome mapping at the call site. Use the low-level `IFlowNode<TContext>` contract when you need hand-written runtime nodes, custom graph construction, or very small tests that do not need generated cases.
