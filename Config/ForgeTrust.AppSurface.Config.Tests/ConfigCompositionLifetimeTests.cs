using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit.Abstractions;

namespace ForgeTrust.AppSurface.Config.Tests;

[Collection("Composition lifetime and timing")]
public sealed class ConfigCompositionLifetimeTests(ITestOutputHelper output)
{
    private const string EnvironmentName = "Production";
    private const string RootKey = "Service";

    [Fact]
    public void Execute_CompletedRootWrapperAndPayloadAreReleasedWhileEngineAndTraceRemainAlive()
    {
        var provider = new EmittingSecretProvider();
        var engine = CreateEngine(provider);
        engine.ValidatePlan(EnvironmentName, RootKey, typeof(LifetimeOptions));

        var observation = ExecuteAndRelease(engine);

        AssertCollected(observation.References.Concat(provider.Payloads));
        GC.KeepAlive(observation.Metadata);
        GC.KeepAlive(provider);
        GC.KeepAlive(engine);
    }

    [Fact]
    public void Execute_BindingFailureReleasesPartialRootAndSlotsWithCachedPlanAndFailureResultAlive()
    {
        var provider = new EmittingSecretProvider();
        var engine = CreateEngine(provider);
        engine.ValidatePlan(EnvironmentName, RootKey, typeof(ThrowingBindingOptions));

        var failed = ExecuteBindingFailureAndRelease(engine);
        var recovery = ExecuteAndRelease(engine);

        AssertCollected(failed.References.Concat(recovery.References).Concat(provider.Payloads));
        GC.KeepAlive(failed.Metadata);
        GC.KeepAlive(recovery.Metadata);
        GC.KeepAlive(provider);
        GC.KeepAlive(engine);
    }

    [Fact]
    public void Execute_ConversionFailureReleasesProviderPayloadWithFailureResultAlive()
    {
        var provider = new EmittingSecretProvider();
        var engine = CreateEngine(provider);
        engine.ValidatePlan(EnvironmentName, RootKey, typeof(IntegerOptions));

        var failed = ExecuteConversionFailure(engine);

        AssertCollected(provider.Payloads);
        GC.KeepAlive(failed);
        GC.KeepAlive(provider);
        GC.KeepAlive(engine);
    }

    [Fact]
    public void Execute_RawBaseTextAndParsedPayloadAreReleasedWithLongLivedEngine()
    {
        var provider = new EmittingRawProvider();
        var engine = new ConfigCompositionEngine(new EmptyEnvironment(), [provider], [], [], new(), TimeProvider.System);
        engine.ValidatePlan(EnvironmentName, RootKey, typeof(LifetimeOptions));

        var observation = ExecuteAndRelease(engine);

        AssertCollected(observation.References.Concat(provider.Payloads));
        GC.KeepAlive(observation.Metadata);
        GC.KeepAlive(provider);
        GC.KeepAlive(engine);
    }

    [Fact]
    public void Execute_ConcurrentInvocationsReleaseAllRootsAndPayloadsAfterWorkersComplete()
    {
        const int invocations = 64;
        var provider = new EmittingSecretProvider();
        var engine = CreateEngine(provider);
        engine.ValidatePlan(EnvironmentName, RootKey, typeof(LifetimeOptions));
        var observations = new LifetimeObservation[invocations];

        Parallel.For(0, invocations, new ParallelOptions { MaxDegreeOfParallelism = 4 },
            i => observations[i] = ExecuteAndRelease(engine));

        Assert.Equal(invocations, provider.ResolveCalls);
        AssertCollected(observations.SelectMany(o => o.References).Concat(provider.Payloads));
        GC.KeepAlive(observations);
        GC.KeepAlive(provider);
        GC.KeepAlive(engine);
    }

    [Fact]
    public void Benchmark_ReportsColdWarmConcurrentAndSyntheticProviderDelayWithoutTimingThresholds()
    {
        const int warmup = 32;
        const int sequentialCount = 256;
        const int concurrentCount = 512;
        const int delayedCount = 32;
        var workers = Math.Clamp(Environment.ProcessorCount, 2, 8);
        var provider = new EmittingSecretProvider(trackPayloads: false);
        var coldStart = Stopwatch.GetTimestamp();
        var engine = CreateEngine(provider);
        engine.ValidatePlan(EnvironmentName, RootKey, typeof(LifetimeOptions));
        var coldCompileMs = Stopwatch.GetElapsedTime(coldStart).TotalMilliseconds;
        Assert.Equal(0, provider.ResolveCalls);

        var firstInvocation = MeasureSequential(engine, 1);
        for (var i = 0; i < warmup; i++) ExecuteAndDiscard(engine);
        var warm = MeasureSequential(engine, sequentialCount);
        var concurrent = MeasureConcurrent(engine, concurrentCount, workers);

        var delayedProvider = new EmittingSecretProvider(trackPayloads: false, delay: TimeSpan.FromMilliseconds(1));
        var delayedEngine = CreateEngine(delayedProvider);
        delayedEngine.ValidatePlan(EnvironmentName, RootKey, typeof(LifetimeOptions));
        ExecuteAndDiscard(delayedEngine);
        var synthetic = MeasureSequential(delayedEngine, delayedCount);

        Assert.Equal(1 + warmup + sequentialCount + concurrentCount, provider.ResolveCalls);
        Assert.Equal(1 + delayedCount, delayedProvider.ResolveCalls);
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        output.WriteLine("# Issue #807 bounded composition performance observation");
        output.WriteLine(FormattableString.Invariant($"Recorded: {DateTimeOffset.UtcNow:O}"));
        output.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.ProcessArchitecture}; " +
            $"configuration: {configuration}; logical processors: {Environment.ProcessorCount}.");
        output.WriteLine("Fixture: one Secret<string> slot, a fresh 256-character provider value per call, " +
            "an empty environment, code-declared reference, no whole-root base, and no network.");
        output.WriteLine("Providers retain no payloads or raw fixture dictionaries; weak tracking is disabled only for timing batches.");
        output.WriteLine(FormattableString.Invariant($"Cold host creation + first ValidatePlan: {coldCompileMs:F3} ms; no Resolve calls."));
        output.WriteLine($"Warmup: {warmup} executions; concurrent batch: {workers} workers.");
        output.WriteLine("| Measurement | Operations | Batch wall ms | Median operation us | p95 operation us | Current-thread bytes/op |");
        output.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: |");
        WriteSample("First execution after compile", firstInvocation);
        WriteSample("Warm sequential, no provider delay", warm);
        WriteSample("Concurrent, no provider delay", concurrent);
        WriteSample("Synthetic provider Thread.Sleep(1 ms)", synthetic);
        output.WriteLine(FormattableString.Invariant(
            $"Warm local median / synthetic-delay median: {warm.MedianMicroseconds / synthetic.MedianMicroseconds:F3}; warm local median / nominal 1 ms: {warm.MedianMicroseconds / 1000:F3}."));
        output.WriteLine("Synthetic delay is a comparison fixture, not measured Google/LocalSecrets/network latency. " +
            "Thread.Sleep can overshoot 1 ms. Cold includes per-host metadata and any JIT remaining after other tests; " +
            "it is not a process-cold measurement. Concurrent per-operation latency includes scheduling/contention. " +
            "Allocation counts are measured on the sequential caller only and include test instrumentation. " +
            "These bounded observations have no timing pass/fail threshold and are not a throughput SLA.");
        GC.KeepAlive(engine);
        GC.KeepAlive(delayedEngine);
    }

    private void WriteSample(string name, Sample sample)
    {
        var allocation = sample.BytesPerOperation?.ToString("F0", CultureInfo.InvariantCulture) ?? "not measured";
        output.WriteLine(FormattableString.Invariant(
            $"| {name} | {sample.Operations} | {sample.ElapsedMilliseconds:F3} | {sample.MedianMicroseconds:F3} | {sample.P95Microseconds:F3} | {allocation} |"));
    }

    private static ConfigCompositionEngine CreateEngine(EmittingSecretProvider provider) =>
        new(new EmptyEnvironment(), [], [provider], [provider], new(), TimeProvider.System);

    // All strong execution locals die in this frame, independent of Debug/Release JIT local lifetime choices.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LifetimeObservation ExecuteAndRelease(ConfigCompositionEngine engine)
    {
        var result = engine.Execute(EnvironmentName, RootKey, typeof(LifetimeOptions));
        Assert.Equal(ConfigCompositionRootState.Resolved, result.State);
        var root = Assert.IsType<LifetimeOptions>(result.Value);
        Assert.True(root.ApiKey.HasValue);
        Assert.Equal(256, root.ApiKey.Value.Length);
        return new([Track(result), Track(root), Track(root.ApiKey), Track(root.ApiKey.Value)], result.Slots);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LifetimeObservation ExecuteBindingFailureAndRelease(ConfigCompositionEngine engine)
    {
        ThrowingBindingOptions.LastAttempt = [];
        var result = engine.Execute(EnvironmentName, RootKey, typeof(ThrowingBindingOptions));
        Assert.Equal(ConfigCompositionRootState.Failed, result.State);
        Assert.Null(result.Value);
        Assert.Contains(result.Failures, f => f.Code == "config-composition-bind-failed");
        Assert.NotEmpty(ThrowingBindingOptions.LastAttempt);
        // Keeping the entire failed result is intentional: diagnostics must not retain the partial bound object.
        return new(ThrowingBindingOptions.LastAttempt, result);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ConfigCompositionExecutionResult ExecuteConversionFailure(ConfigCompositionEngine engine)
    {
        var result = engine.Execute(EnvironmentName, RootKey, typeof(IntegerOptions));
        Assert.Equal(ConfigCompositionRootState.Failed, result.State);
        Assert.Null(result.Value);
        Assert.Contains(result.Failures, f => f.Code == "secret-value-conversion-failed");
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ExecuteAndDiscard(ConfigCompositionEngine engine)
    {
        var result = engine.Execute(EnvironmentName, RootKey, typeof(LifetimeOptions));
        Assert.Equal(ConfigCompositionRootState.Resolved, result.State);
        var root = Assert.IsType<LifetimeOptions>(result.Value);
        Assert.True(root.ApiKey.HasValue);
        Assert.Equal(256, root.ApiKey.Value.Length);
    }

    private static void AssertCollected(IEnumerable<WeakReference<object>> references)
    {
        var snapshot = references.ToArray();
        Assert.NotEmpty(snapshot);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            if (!snapshot.Any(IsAlive)) return;
        }
        Assert.All(snapshot, reference => Assert.False(IsAlive(reference), "An invocation object remained rooted after forced collections."));
    }

    // TryGetTarget never exposes a temporary strong target in the frame performing the next GC.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<object> reference) => reference.TryGetTarget(out _);

    private static WeakReference<object> Track(object value) => new(value);

    private static Sample MeasureSequential(ConfigCompositionEngine engine, int operations)
    {
        var latencies = new double[operations];
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var batchStart = Stopwatch.GetTimestamp();
        for (var i = 0; i < operations; i++)
        {
            var start = Stopwatch.GetTimestamp();
            ExecuteAndDiscard(engine);
            latencies[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        }
        var elapsed = Stopwatch.GetElapsedTime(batchStart).TotalMilliseconds;
        var bytesPerOperation = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / (double)operations;
        return Summarize(latencies, elapsed, bytesPerOperation);
    }

    private static Sample MeasureConcurrent(ConfigCompositionEngine engine, int operations, int workers)
    {
        var latencies = new double[operations];
        var batchStart = Stopwatch.GetTimestamp();
        Parallel.For(0, operations, new ParallelOptions { MaxDegreeOfParallelism = workers }, i =>
        {
            var start = Stopwatch.GetTimestamp();
            ExecuteAndDiscard(engine);
            latencies[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        });
        return Summarize(latencies, Stopwatch.GetElapsedTime(batchStart).TotalMilliseconds, null);
    }

    private static Sample Summarize(double[] latencies, double elapsedMilliseconds, double? bytesPerOperation)
    {
        Array.Sort(latencies);
        var middle = latencies.Length / 2;
        var median = latencies.Length % 2 == 0 ? (latencies[middle - 1] + latencies[middle]) / 2 : latencies[middle];
        return new(latencies.Length, elapsedMilliseconds, median,
            latencies[(int)Math.Ceiling(latencies.Length * 0.95) - 1], bytesPerOperation);
    }

    private sealed record Sample(int Operations, double ElapsedMilliseconds, double MedianMicroseconds,
        double P95Microseconds, double? BytesPerOperation);

    private sealed record LifetimeObservation(IReadOnlyList<WeakReference<object>> References, object Metadata);

    private sealed class LifetimeOptions
    {
        public Secret<string> ApiKey { get; init; } = new();
    }

    private sealed class IntegerOptions
    {
        public Secret<int> ApiKey { get; init; } = new();
    }

    private sealed class ThrowingBindingOptions
    {
        // Test-only weak observations of a partially constructed object; never a strong payload/root cache.
        public static IReadOnlyList<WeakReference<object>> LastAttempt { get; set; } = [];
        public Secret<string> ApiKey { get; }

        public ThrowingBindingOptions(Secret<string> apiKey)
        {
            ApiKey = apiKey;
            LastAttempt = [Track(this), Track(apiKey), Track(apiKey.Value)];
            throw new JsonException("Synthetic failure after the opaque slot has been bound.");
        }
    }

    private sealed class EmptyEnvironment : IEnvironmentConfigProvider
    {
        public string Environment => EnvironmentName;
        public bool IsDevelopment => false;
        public int Priority => int.MaxValue;
        public string Name => "empty-environment";
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
        public T? GetValue<T>(string environment, string key) => default;
    }

    private sealed class EmittingSecretProvider(bool trackPayloads = true, TimeSpan delay = default)
        : IConfigSecretProvider, IConfigSecretDeclarationSource
    {
        private int _sequence;
        public string Id => "ephemeral-provider";
        public int ResolveCalls => Volatile.Read(ref _sequence);
        public ConcurrentQueue<WeakReference<object>> Payloads { get; } = new();
        public ConfigSecretReferenceValidation ValidateReference(ConfigSecretReference reference) => ConfigSecretReferenceValidation.Supported();

        public IReadOnlyList<ConfigSecretConfiguredClaim> InspectClaims(string rootLogicalPath, IReadOnlyList<string> secretDestinationPaths) =>
            [new(ConfigSecretConfiguredClaimKind.ExactMapping, rootLogicalPath + ":ApiKey", Id, "credential", null)];

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ConfigSecretProviderResolution Resolve(ConfigSecretReference reference, ConfigSecretResolutionContext context)
        {
            if (delay > TimeSpan.Zero) Thread.Sleep(delay);
            var payload = CreatePayload(Interlocked.Increment(ref _sequence));
            if (trackPayloads) Payloads.Enqueue(Track(payload));
            return ConfigSecretProviderResolution.Resolved(payload, ConfigSecretSourceMetadata.Create(Id));
        }
    }

    private sealed class EmittingRawProvider : IConfigProvider, IConfigCompositionValueProvider
    {
        private int _sequence;
        public int Priority => 1;
        public string Name => "ephemeral-raw-provider";
        public ConcurrentQueue<WeakReference<object>> Payloads { get; } = new();
        public T? GetValue<T>(string environment, string key) => throw new InvalidOperationException("Only raw resolution is expected.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ConfigCompositionValueResolution ResolveRaw(string environment, string logicalKey)
        {
            var payload = CreatePayload(Interlocked.Increment(ref _sequence));
            var raw = JsonSerializer.Serialize(new { ApiKey = payload });
            Payloads.Enqueue(Track(payload));
            Payloads.Enqueue(Track(raw));
            return ConfigCompositionValueResolution.Resolved(raw, Name, Priority, isSensitive: true);
        }
    }

    private static string CreatePayload(int sequence) => string.Create(256, sequence, static (characters, value) =>
    {
        characters.Fill('p');
        value.TryFormat(characters, out _, "D12", CultureInfo.InvariantCulture);
    });
}

// Forced collections and measurements must not overlap unrelated xUnit test collections.
[CollectionDefinition("Composition lifetime and timing", DisableParallelization = true)]
public sealed class ConfigCompositionLifetimeCollection;
