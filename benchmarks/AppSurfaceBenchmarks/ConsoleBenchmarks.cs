// using BenchmarkDotNet.Attributes;
// using ConsoleAppExample;
// using ForgeTrust.AppSurface.Console;
// #elif SPECTRE_CLI
// using Scli = Spectre.Console.Cli;
// #endif
//
// namespace AppSurfaceBenchmarks;
//
// [MemoryDiagnoser]
// [GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory, BenchmarkLogicalGroupRule.ByJob)]
// public class ConsoleBenchmarks
// {
//     private readonly string[] _args = ["greet", "world"];
//
//     [Benchmark]
//     public async Task Run()
//     {
// #if APPSURFACE_CONSOLE
//         using var _ = ConsoleSilenceScope.Start();
//         await ConsoleApp<ExampleModule>.RunAsync(_args);
// #elif SPECTRE_CLI
//         using var _ = ConsoleSilenceScope.Start();
//         var app = new Scli.CommandApp<SpectreGreetCommand>();
//         await app.RunAsync(_args);
// #endif
//     }
//
// #if SPECTRE_CLI
//     private sealed class SpectreGreetSettings : Scli.CommandSettings
//     {
//         [Scli.CommandArgument(0, "[name]")]
//         public string Name { get; set; } = "world";
//     }
//
//     private sealed class SpectreGreetCommand : Scli.AsyncCommand<SpectreGreetSettings>
//     {
//         public override Task<int> ExecuteAsync(Scli.CommandContext context, SpectreGreetSettings settings)
//         {
//             System.Console.WriteLine($"Hello, {settings.Name}!");
//             return Task.FromResult(0);
//         }
//     }
// #endif
//
//     private sealed class ConsoleSilenceScope : IDisposable
//     {
//         private readonly TextWriter _original;
//         private ConsoleSilenceScope(TextWriter original) { _original = original; }
//         public static ConsoleSilenceScope Start()
//         {
//             var original = System.Console.Out;
//             System.Console.SetOut(TextWriter.Null);
//             return new ConsoleSilenceScope(original);
//         }
//         public void Dispose()
//         {
//             System.Console.SetOut(_original);
//         }
//     }
// }
// #endif
