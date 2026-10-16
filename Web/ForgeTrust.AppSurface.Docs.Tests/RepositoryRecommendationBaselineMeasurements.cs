using System.Diagnostics;
using System.Runtime;
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

[Collection(nameof(RepositoryRecommendationCorpusCollection))]
public sealed class RepositoryRecommendationBaselineMeasurements
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions ReportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ITestOutputHelper _output;

    public RepositoryRecommendationBaselineMeasurements(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task CaptureCurrentRepositoryCorpusBaseline()
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var settingsPath = TestPathUtils.PathUnder(
            repositoryRoot,
            "Web",
            "ForgeTrust.AppSurface.Docs.Standalone",
            "appsettings.json");

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(settingsPath)
            .Build();

        var options = new AppSurfaceDocsOptions();
        configuration.GetSection(AppSurfaceDocsOptions.SectionName).Bind(options);
        Assert.Equal(AppSurfaceDocsMode.Source, options.Mode);
        options.Source.RepositoryRoot = repositoryRoot;

        var appSurfaceDocsConfiguration = configuration
            .GetSection(AppSurfaceDocsOptions.SectionName)
            .AsEnumerable()
            .Where(entry => entry.Value is not null)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(
                entry => entry.Key,
                entry => entry.Value,
                StringComparer.Ordinal);

        var (firstGeneration, secondGeneration) = await MeasureOverlappingGenerationsAsync(
            repositoryRoot,
            options);
        var thirdGeneration = await MeasureGenerationAsync(
            generation: 3,
            repositoryRoot,
            options,
            priorConsumers: null);
        Assert.Equal(firstGeneration.DocumentCount, secondGeneration.DocumentCount);
        Assert.Equal(firstGeneration.DocumentCount, thirdGeneration.Measurement.DocumentCount);
        Assert.Equal(firstGeneration.SearchRecordCount, secondGeneration.SearchRecordCount);
        Assert.Equal(firstGeneration.SearchRecordCount, thirdGeneration.Measurement.SearchRecordCount);

        var report = new
        {
            measurement = "current-corpus snapshot measurements",
            machine = new
            {
                name = Environment.MachineName,
                osDescription = RuntimeInformation.OSDescription,
                osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                processorCount = Environment.ProcessorCount
            },
            runtime = new
            {
                frameworkDescription = RuntimeInformation.FrameworkDescription,
                version = Environment.Version.ToString(),
                processId = Environment.ProcessId,
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                is64BitProcess = Environment.Is64BitProcess,
                serverGc = GCSettings.IsServerGC
            },
            configuration = new
            {
                settingsPath,
                effectiveRepositoryRoot = repositoryRoot,
                effectiveMode = options.Mode.ToString(),
                appSurfaceDocs = appSurfaceDocsConfiguration
            },
            measurementScope = new
            {
                elapsed = "Wall-clock time from cache/aggregator construction through the first docs and search-payload reads.",
                allocated = "Process-wide GC.GetTotalAllocatedBytes(true) delta over the elapsed interval; includes concurrent managed allocations elsewhere in this test process.",
                retained = "Process-wide live managed heap from GC.GetTotalMemory(true), sampled before and after each build while its cache and captured values are live; this is not per-snapshot attribution.",
                overlap = "During generation 2, generation 1's captured docs list and search payload remain rooted while generation 2 builds with a fresh cache. Generation 1's Memo and cache have been disposed, and its aggregator is not intentionally retained. These are process-wide consumer-overlap point samples, not whole-snapshot sizes or peak-memory measurements.",
                payloadBytes = "Length of UTF-8 bytes from JsonSerializer.SerializeToUtf8Bytes(payload, JsonSerializerDefaults.Web); serialization is outside elapsed and allocation intervals.",
                performanceThresholds = "None. Measurements are reported without numerical performance assertions or SLOs."
            },
            overlap = new
            {
                firstGeneration = firstGeneration.Generation,
                secondGeneration = secondGeneration.Generation,
                processManagedHeapBytesBeforeSecondBuild = secondGeneration.ProcessManagedHeapBytesBeforeBuild,
                processManagedHeapBytesAfterSecondBuild = secondGeneration.ProcessManagedHeapBytesAfterBuild,
                processManagedHeapDeltaBytes = secondGeneration.RetainedManagedHeapDeltaBytes
            },
            generations = new[]
            {
                firstGeneration,
                secondGeneration,
                thirdGeneration.Measurement
            }
        };

        _output.WriteLine(JsonSerializer.Serialize(report, ReportJsonOptions));
    }

    private static async Task<(GenerationMeasurement First, GenerationMeasurement Second)>
        MeasureOverlappingGenerationsAsync(
            string repositoryRoot,
            AppSurfaceDocsOptions options)
    {
        var first = await MeasureGenerationAsync(
            generation: 1,
            repositoryRoot,
            options,
            priorConsumers: null);
        var second = await MeasureGenerationAsync(
            generation: 2,
            repositoryRoot,
            options,
            first.Consumers);

        GC.KeepAlive(first.Consumers);
        GC.KeepAlive(second.Consumers);
        return (first.Measurement, second.Measurement);
    }

    private static async Task<GenerationCapture> MeasureGenerationAsync(
        int generation,
        string repositoryRoot,
        AppSurfaceDocsOptions options,
        CapturedConsumers? priorConsumers)
    {
        var managedHeapBytesBeforeBuild = GC.GetTotalMemory(forceFullCollection: true);
        var allocatedBytesBeforeBuild = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var memo = new Memo(cache);

        var environment = A.Fake<IWebHostEnvironment>();
        A.CallTo(() => environment.ContentRootPath).Returns(repositoryRoot);

        var harvesters = new IDocHarvester[]
        {
            new MarkdownHarvester(
                NullLogger<MarkdownHarvester>.Instance,
                File.ReadAllTextAsync,
                options),
            new CSharpDocHarvester(options, NullLogger<CSharpDocHarvester>.Instance),
            new JavaScriptDocHarvester(options, NullLogger<JavaScriptDocHarvester>.Instance),
            new PythonDocHarvester(options, NullLogger<PythonDocHarvester>.Instance)
        };

        var aggregator = new DocAggregator(
            harvesters,
            options,
            environment,
            memo,
            new AppSurfaceDocsHtmlSanitizer(),
            NullLogger<DocAggregator>.Instance);

        var docs = await aggregator.GetDocsAsync();
        var payload = await aggregator.GetSearchIndexPayloadAsync();
        stopwatch.Stop();

        var allocatedManagedBytes =
            GC.GetTotalAllocatedBytes(precise: true) - allocatedBytesBeforeBuild;

        var harvestHealth = await aggregator.GetHarvestHealthAsync();
        Assert.All(harvestHealth.Harvesters, harvester => Assert.True(
            harvester.Status is DocHarvesterHealthStatus.Succeeded or DocHarvesterHealthStatus.ReturnedEmpty,
            $"{harvester.HarvesterType}: {harvester.Status}"));
        var repeatedPayload = await aggregator.GetSearchIndexPayloadAsync();
        Assert.Equal(docs.Count, harvestHealth.TotalDocs);
        Assert.Equal(payload.Documents.Count, repeatedPayload.Documents.Count);

        var serializedPayload = JsonSerializer.SerializeToUtf8Bytes(payload, PayloadJsonOptions);
        var payloadBytes = serializedPayload.Length;
        serializedPayload = [];

        var consumers = new CapturedConsumers(docs, payload);
        var managedHeapBytesAfterBuild = GC.GetTotalMemory(forceFullCollection: true);

        GC.KeepAlive(priorConsumers);
        GC.KeepAlive(consumers);
        GC.KeepAlive(aggregator);
        GC.KeepAlive(environment);
        GC.KeepAlive(harvesters);
        GC.KeepAlive(memo);
        GC.KeepAlive(cache);

        var measurement = new GenerationMeasurement(
            generation,
            stopwatch.Elapsed.TotalMilliseconds,
            allocatedManagedBytes,
            managedHeapBytesBeforeBuild,
            managedHeapBytesAfterBuild,
            managedHeapBytesAfterBuild - managedHeapBytesBeforeBuild,
            docs.Count,
            payload.Documents.Count,
            payloadBytes,
            priorConsumers is not null);

        return new GenerationCapture(measurement, consumers);
    }

    private sealed record CapturedConsumers(
        IReadOnlyList<DocNode> Docs,
        DocsSearchIndexPayload Payload);

    private sealed record GenerationCapture(
        GenerationMeasurement Measurement,
        CapturedConsumers Consumers);

    private sealed record GenerationMeasurement(
        int Generation,
        double ElapsedMilliseconds,
        long ProcessAllocatedManagedBytes,
        long ProcessManagedHeapBytesBeforeBuild,
        long ProcessManagedHeapBytesAfterBuild,
        long RetainedManagedHeapDeltaBytes,
        int DocumentCount,
        int SearchRecordCount,
        int SearchPayloadUtf8Bytes,
        bool PriorCapturedConsumersRetainedDuringBuild);
}
