using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Running;

namespace AppSurfaceBenchmarks;

public class Program
{
    private static int LaunchCount =>
        int.TryParse(Environment.GetEnvironmentVariable("BENCH_LAUNCH_COUNT"), out var n) && n > 0 ? n : 100;

    public static void Main(string[] args)
    {
        // var cfg = DefaultConfig.Instance
        //     .WithOptions(ConfigOptions.JoinSummary)
        //     .AddLogicalGroupRules(BenchmarkLogicalGroupRule.ByMethod, BenchmarkLogicalGroupRule.ByCategory);

        var config = ManualConfig.Create(DefaultConfig.Instance)
            .AddExporter(new CustomJsonExporter())
            // .AddJob(CreateJob("AppSurface.Console", "APPSURFACE_CONSOLE"))
            // .AddJob(CreateJob("Spectre.Console", "SPECTRE_CLI"))
            .AddJob(CreateJob("AppSurface.Web", "APPSURFACE_WEB"))
            .AddJob(CreateJob("Native", "NATIVE_WEB"))
            .AddJob(CreateJob("Carter", "CARTER_WEB"))
            .AddJob(CreateJob("ABP", "ABP_WEB"))
            .AddFilter(new JobCategoryMatrixFilter(new Dictionary<string, IEnumerable<string>>
            {
                ["Carter"] = ["Minimal API"],
                // ["ABP"] = ["Minimal API", "Controllers", "Dependency Injection"],
                // ["Native"] = ["Controllers", "Minimal API", "Dependency Injection"],
                // ["AppSurface.Web"] = ["Controllers", "Minimal API", "Dependency Injection"]
            }));

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
    }

    public sealed class JobCategoryMatrixFilter : IFilter
    {
        private readonly Dictionary<string, HashSet<string>> allow;
        public JobCategoryMatrixFilter(IDictionary<string, IEnumerable<string>> map)
        {
            allow = map.ToDictionary(kv => kv.Key,
                kv => kv.Value.ToHashSet(StringComparer.OrdinalIgnoreCase));
        }

        public bool Predicate(BenchmarkCase b)
        {
            var jobId = b.Job.Id ?? "";
            if (!allow.TryGetValue(jobId, out var cats)) return true;      // no restriction for this job
            var bcats = b.Descriptor.Categories ?? Array.Empty<string>();
            return bcats.Any(cats.Contains);                               // keep only allowed categories
        }
    }

    private static Job CreateJob(string id, string define)
    {
        return Job.Default
            .WithStrategy(RunStrategy.ColdStart)
            .WithLaunchCount(LaunchCount)
            .WithWarmupCount(0)
            .WithIterationCount(1)
            .WithId(id)
            .WithBaseline(id == "Native")
            .WithArguments([new MsBuildArgument($"/p:DefineConstants={define}")]);
    }
}
