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
dotnet run -c Release --project benchmarks/AppSurfaceBenchmarks/AppSurfaceBenchmarks.csproj -- --filter "*FlowRunnerBenchmarks*"
```

The Flow benchmark is synthetic runner-overhead evidence. It compares the
in-memory runner with a direct lower-bound loop so changes to routing,
execution-context creation, and result handling remain visible. Do not treat
it as product-level throughput guidance for real workflows; add a workload
benchmark that resembles production node work before making adoption claims.

---
[🏠 Back to Root](../README.md)
