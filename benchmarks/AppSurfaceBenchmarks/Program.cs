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
            .AddJob(CreateHealthComparisonJob())
            .AddJob(CreateFlowJob())
            .AddFilter(new JobCategoryMatrixFilter(new Dictionary<string, IEnumerable<string>>
            {
                ["AppSurface.Web"] = ["Minimal API", "Controllers", "Dependency Injection"],
                ["AppSurface.Web.HealthAB"] = ["AppSurface Health A/B"],
                ["Native"] = ["Minimal API", "Controllers", "Dependency Injection"],
                ["Carter"] = ["Minimal API"],
                ["ABP"] = ["Minimal API", "Controllers", "Dependency Injection"],
                ["Flow"] = ["Flow"],
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

    private static Job CreateFlowJob()
    {
        return Job.Default
            .WithStrategy(RunStrategy.Throughput)
            .WithLaunchCount(1)
            .WithWarmupCount(3)
            .WithIterationCount(8)
            .WithId("Flow")
            .WithArguments([new MsBuildArgument("/p:DefineConstants=FLOW")]);
    }

    private static Job CreateHealthComparisonJob()
    {
        return Job.Default
            .WithStrategy(RunStrategy.ColdStart)
            .WithLaunchCount(LaunchCount)
            .WithWarmupCount(0)
            .WithIterationCount(1)
            .WithInvocationCount(1)
            .WithUnrollFactor(1)
            .WithId("AppSurface.Web.HealthAB")
            .WithArguments([new MsBuildArgument("/p:DefineConstants=APPSURFACE_WEB")]);
    }
}
