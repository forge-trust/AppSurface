# Benchmarks

Benchmarks compare application startup and execution times for the example
projects. They measure:

- **AppSurface.Console** vs **Spectre.Console.Cli**
- **AppSurface.Web** vs **Minimal APIs** vs **Carter** vs **ABP**

Benchmarks are compiled separately per library using conditional
compilation, ensuring that only the code under test is loaded for each job.

Run them in release mode to get optimized measurements:

```bash
dotnet run -c Release --project AppSurfaceBenchmarks
```

---
[🏠 Back to Root](../README.md)
