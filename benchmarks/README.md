# Benchmarks

Benchmarks compare application startup and execution times for the example
projects. They measure:

- **AppSurface.Console** vs **Spectre.Console.Cli**
- **AppSurface.Web** vs **Minimal APIs** vs **Carter** vs **ABP**
- **AppSurface.Flow** in-memory runner overhead vs an equivalent direct state machine

Benchmarks are compiled separately per library using conditional
compilation, ensuring that only the code under test is loaded for each job.

From the repository root, run them in release mode to get optimized measurements:

```bash
dotnet run -c Release --project benchmarks/AppSurfaceBenchmarks/AppSurfaceBenchmarks.csproj
```

Run only the Flow benchmarks when investigating state-machine overhead:

```bash
dotnet run -c Release --project benchmarks/AppSurfaceBenchmarks/AppSurfaceBenchmarks.csproj -- --filter "AppSurfaceBenchmarks.Flow.*"
```

The Flow benchmark is synthetic runner-overhead evidence. It compares the
in-memory runner with a direct lower-bound loop so changes to routing,
execution-context snapshots, outcome allocation, and result handling remain visible. Do not treat
it as product-level throughput guidance for real workflows; add a workload
benchmark that resembles production node work before making adoption claims.
Use the deeper Flow benchmark families to attribute overhead before changing APIs:
`FlowRunnerShapeBenchmarks` separates synchronous nodes, async-completed nodes,
and two-node routing; `FlowGeneratedAuthoringBenchmarks` measures generated
adapter/envelope overhead; and `FlowOutcomeAllocationBenchmarks` measures the
public outcome factory allocation floor outside the runner.
`FlowGeneratedFactoryAllocationBenchmarks` separates generated outcome-case
allocation from generated envelope wrapping.

---
[🏠 Back to Root](../README.md)
