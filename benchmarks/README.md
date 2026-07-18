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

Measure the incremental cold-start and allocation cost of the
[opt-in AppSurface Web health-check services and `/health` plus `/ready` endpoints](../Web/ForgeTrust.AppSurface.Web/README.md#health-and-readiness-probes)
with a same-run A/B comparison:

```bash
dotnet run -c Release --project benchmarks/AppSurfaceBenchmarks/AppSurfaceBenchmarks.csproj -- --filter "*WebHealthColdStartBenchmarks*"
```

`Health_Disabled` is the baseline. `Health_Enabled` changes only
`WebOptions.Health.Enabled`, so the comparison targets the optional health surface. The pair binds an
ephemeral loopback port, avoiding conflicts with local services while keeping network behavior identical
between cases. Configuration
file reload is disabled for this pair so operating-system file-watcher callbacks do not contaminate
the feature delta. Its dedicated job records one start/request/stop operation in each timed sample,
avoiding invocation calibration that would combine multiple host lifecycles into one sample;
[BenchmarkDotNet](https://benchmarkdotnet.org/) may run a separate diagnostic invocation to collect allocation
data. This comparison
measures the combined cost of health-check service registration and endpoint mapping; it does not
attribute that cost to either operation individually. Because every sample creates a complete host,
small timing and allocation deltas can fall within cold-start variance; treat them as attributable only
when their direction and magnitude remain stable across repeated runs.

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
