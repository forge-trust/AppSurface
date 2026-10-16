using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FakeItEasy;
using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Tests;

/// <summary>
/// Reproducible local resource evidence, with no timing threshold. The stress fixtures verify complete result bounds
/// while retaining actual cached generations; measurements are diagnostic and include test-runner process activity.
/// </summary>
[Collection(nameof(RepositoryRecommendationCorpusCollection))]
public sealed class RepositoryRecommendationResourceMeasurements(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [Fact]
    public async Task CaptureCandidateAndTenfoldCommonComponentEvidence()
    {
        var root = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var options = new AppSurfaceDocsOptions();
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Join(root,
            "Web/ForgeTrust.AppSurface.Docs.Standalone/appsettings.json")).Build();
        configuration.GetSection(AppSurfaceDocsOptions.SectionName).Bind(options);
        options.Source.RepositoryRoot = root;
        var rows = new List<object>();
        using var first = Create(root, options);
        rows.Add(await Measure("candidate-1", first, false));
        using (var second = Create(root, options))
        {
            // Keep the first cache, Memo and aggregator alive: this retains its WHOLE cached generation, not only
            // public consumers, throughout the second independent build and its peak sampler.
            rows.Add(await Measure("candidate-2-whole-generation-overlap", second, true));
            GC.KeepAlive(first);
        }
        using (var third = Create(root, options)) rows.Add(await Measure("candidate-3", third, true));
        var baseDocs = await first.Aggregator.GetDocsAsync();
        using (var tenfold = Create(root, options, Replicate(baseDocs, false)))
            rows.Add(await Measure("tenfold-corpus", tenfold, true));
        using (var common = Create(root, options, Replicate(baseDocs, true)))
            rows.Add(await Measure("tenfold-common-authored-component", common, true));
        GC.KeepAlive(first);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            measurement = "issue255 candidate, whole-generation overlap, and synthetic stress",
            machine = new
            {
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.OSArchitecture.ToString(),
                processors = Environment.ProcessorCount,
                runtime = RuntimeInformation.FrameworkDescription
            },
            scope = new
            {
                configuration = "Web/ForgeTrust.AppSurface.Docs.Standalone/appsettings.json",
                candidate = "Three independent real Markdown/CSharp/JavaScript/Python snapshots; baseline artifact captured before implementation.",
                stress = "Ten copies of current harvested nodes with disjoint source/canonical identities. Bodies/typed data are shared immutable inputs; authored references cleared to isolate automatic policy cost. Common case assigns every clone the same explicit nonderived component.",
                peaks = "Managed heap and process working/private bytes sampled every 20ms during build; sampled peaks may miss shorter bursts. OS process lifetime peak working set also reported. Nonpositive private/lifetime counters are unavailable and represented as null. Process includes test runner; no per-snapshot attribution is claimed.",
                overlap = "First whole cached generation stays strongly reachable (Memo/cache/aggregator) through all later phases, not merely its consumer rows.",
                serialization = "Complete web JSON bytes measured outside build allocation/timing/peak interval; no payload trimming or limit change.",
                thresholds = "None; maintainers dispose material regressions and existing payload-limit overflow using recorded evidence."
            },
            phases = rows
        }, JsonOptions));
    }

    private static IReadOnlyList<DocNode> Replicate(IReadOnlyList<DocNode> docs, bool common)
    {
        var result = new List<DocNode>(docs.Count * 10);
        for (var copy = 0; copy < 10; copy++)
        {
            var prefix = $"stress/{copy}/";
            foreach (var doc in docs)
            {
                var metadata = doc.Metadata ?? new DocMetadata();
                var canonicalOwner = doc.CanonicalPath?.Split('#', 2)[0];
                result.Add(doc with
                {
                    Path = prefix + doc.Path,
                    ParentPath = doc.ParentPath is null ? null : prefix + doc.ParentPath,
                    CanonicalPath = null,
                    Metadata = metadata with
                    {
                        CanonicalSlug = canonicalOwner is null ? null : prefix + canonicalOwner,
                        RelatedPages = [],
                        Component = common ? "Common stress component" : metadata.Component,
                        ComponentIsDerived = common ? false : metadata.ComponentIsDerived
                    }
                });
            }
        }
        return result;
    }

    private static Lease Create(string root, AppSurfaceDocsOptions options, IReadOnlyList<DocNode>? docs = null)
    {
        IDocHarvester[] harvesters;
        if (docs is null)
        {
            harvesters = [new MarkdownHarvester(NullLogger<MarkdownHarvester>.Instance, File.ReadAllTextAsync, options),
                new CSharpDocHarvester(options, NullLogger<CSharpDocHarvester>.Instance),
                new JavaScriptDocHarvester(options, NullLogger<JavaScriptDocHarvester>.Instance),
                new PythonDocHarvester(options, NullLogger<PythonDocHarvester>.Instance)];
        }
        else
        {
            var harvester = A.Fake<IDocHarvester>();
            A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);
            harvesters = [harvester];
        }
        var environment = A.Fake<IWebHostEnvironment>();
        A.CallTo(() => environment.ContentRootPath).Returns(root);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var memo = new Memo(cache);
        return new Lease(cache, memo, new DocAggregator(harvesters, options, environment, memo,
            new AppSurfaceDocsHtmlSanitizer(), NullLogger<DocAggregator>.Instance));
    }

    private static async Task<object> Measure(string phase, Lease lease, bool overlapping)
    {
        var retainedBefore = GC.GetTotalMemory(true);
        long sampledManaged = retainedBefore, sampledWorking = 0, sampledPrivate = 0;
        using var process = Process.GetCurrentProcess();
        using var sampling = new CancellationTokenSource();
        async Task Sample()
        {
            while (!sampling.IsCancellationRequested)
            {
                process.Refresh();
                sampledManaged = Math.Max(sampledManaged, GC.GetTotalMemory(false));
                sampledWorking = Math.Max(sampledWorking, process.WorkingSet64);
                sampledPrivate = Math.Max(sampledPrivate, process.PrivateMemorySize64);
                try { await Task.Delay(20, sampling.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
        var sampler = Sample();
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var stopwatch = Stopwatch.StartNew();
        var docs = await lease.Aggregator.GetDocsAsync();
        var payload = await lease.Aggregator.GetSearchIndexPayloadAsync();
        Assert.All((await lease.Aggregator.GetHarvestHealthAsync()).Harvesters, harvester => Assert.True(
            harvester.Status is DocHarvesterHealthStatus.Succeeded or DocHarvesterHealthStatus.ReturnedEmpty,
            $"{harvester.HarvesterType}: {harvester.Status}"));
        stopwatch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        sampling.Cancel();
        await sampler;
        var measurements = await lease.Aggregator.GetRecommendationMeasurementsAsync();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length;
        var retainedAfter = GC.GetTotalMemory(true);
        process.Refresh();
        var lifetimePeakWorking = process.PeakWorkingSet64;
        Assert.All(payload.Documents, document => Assert.InRange(document.RecommendedPages.Count(row => row.Source == "automatic"), 0, 3));
        Assert.Equal(docs.Count, (await lease.Aggregator.GetHarvestHealthAsync()).TotalDocs);
        GC.KeepAlive(lease);
        return new
        {
            phase,
            elapsedMs = stopwatch.Elapsed.TotalMilliseconds,
            allocatedProcessBytes = allocated,
            retainedBefore,
            retainedAfter,
            sampledManagedPeak = sampledManaged,
            sampledWorkingPeak = sampledWorking,
            sampledPrivatePeak = sampledPrivate > 0 ? (long?)sampledPrivate : null,
            osLifetimePeakWorkingBytes = lifetimePeakWorking > 0 ? (long?)lifetimePeakWorking : null,
            wholePriorGenerationRetained = overlapping,
            documents = docs.Count,
            searchRecords = payload.Documents.Count,
            measurements.EligibleTargets,
            measurements.CandidatesEvaluated,
            measurements.AutomaticResults,
            sourceCandidateCounts = measurements.CandidateCounts,
            payloadBytes = bytes,
            rewriteLimitBytes = AppSurfaceDocsVersioningOptions.DefaultMaxRewrittenFileSizeBytes
        };
    }

    private sealed class Lease(MemoryCache cache, Memo memo, DocAggregator aggregator) : IDisposable
    {
        public DocAggregator Aggregator { get; } = aggregator;
        public void Dispose() { memo.Dispose(); cache.Dispose(); }
    }
}
