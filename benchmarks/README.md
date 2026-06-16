# Benchmarks

Benchmarks compare application startup and execution times for the example
projects. They measure:

- **AppSurface.Console** vs **Spectre.Console.Cli**
- **AppSurface.Web** vs **Minimal APIs** vs **Carter** vs **ABP**
- **AppSurface.Flow** in-memory runner overhead vs an equivalent direct state machine

Benchmarks are compiled separately per library using conditional
compilation, ensuring that only the code under test is loaded for each job.

Run them in release mode to get optimized measurements:

```bash
dotnet run -c Release --project AppSurfaceBenchmarks
```

Run only the Flow benchmarks when investigating state-machine overhead:

```bash
dotnet run -c Release --project AppSurfaceBenchmarks -- --filter "*FlowRunnerBenchmarks*"
```

---
[🏠 Back to Root](../README.md)
