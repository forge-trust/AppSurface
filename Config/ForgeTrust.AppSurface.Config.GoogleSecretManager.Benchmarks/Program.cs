using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Config.GoogleSecretManager;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Core.Defaults;
using Microsoft.Extensions.DependencyInjection;

internal static class Program
{
    private const string HarnessId = "issue-819-google-mapped-fake-v3";
    private const string SyntheticPayload = "synthetic-proof-value";
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h")
            {
                PrintUsage();
                return 0;
            }

            return args[0] switch
            {
                "proof" => RunProof(),
                "benchmark" => RunBenchmark(Arguments.Parse(args[1..])),
                _ => PrintUsageAndFail()
            };
        }
        catch (Exception)
        {
            // Harness diagnostics are deliberately value-free, even if a provider throws unexpectedly.
            Console.Error.WriteLine("{\"type\":\"harness-error\",\"detail\":\"See the command and workload controls; raw exceptions are suppressed.\"}");
            return 2;
        }
    }

    private static int RunProof()
    {
        var ownerFixture = Fixture.Create(resourceCount: 1, cacheTtl: TimeSpan.FromMinutes(5),
            new FakeSecretManagerClient(gated: true));
        using (ownerFixture)
        {
            var ownerClient = ownerFixture.Client;
            var ownerValue = (string?)null;
            var ownerThreadId = 0;
            var owner = new Thread(() =>
            {
                ownerThreadId = Thread.CurrentThread.ManagedThreadId;
                ownerValue = ownerFixture.Manager.GetValue<string>("Proof", Key(0));
            })
            { IsBackground = true, Name = "mapped-proof-owner" };

            owner.Start();
            var entered = ownerClient.WaitForEntry(OperationTimeout);
            var clientThreadId = ownerClient.CallingThreadIds.FirstOrDefault();
            ownerClient.Release();
            var ownerFinished = owner.Join(OperationTimeout);
            var ownerSameThread = entered && ownerFinished && ownerThreadId == clientThreadId;
            var ownerReturnedExpected = ownerFinished && StringComparer.Ordinal.Equals(SyntheticPayload, ownerValue);
            var coldCalls = ownerClient.CallCount;

            var warmValue = ownerFixture.Manager.GetValue<string>("Proof", Key(0));
            var warmHit = StringComparer.Ordinal.Equals(SyntheticPayload, warmValue)
                && ownerClient.CallCount == coldCalls
                && coldCalls == 1;

            var sharedFlight = ProveSharedFlight();
            var passed = ownerSameThread && ownerReturnedExpected && warmHit && sharedFlight;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                type = "proof",
                harnessId = HarnessId,
                coldOwnerSameManagedThread = ownerSameThread,
                coldOwnerReturnedExpected = ownerReturnedExpected,
                coldRemoteCalls = coldCalls,
                warmCacheAddedCalls = ownerClient.CallCount - coldCalls,
                sameResourceJoinersSharedOneCall = sharedFlight,
                result = passed ? "pass" : "fail",
                valuesEmitted = false,
                resourceNamesEmitted = false,
                credentialsUsed = false
            }));
            return passed ? 0 : 1;
        }
    }

    private static bool ProveSharedFlight()
    {
        const int callerCount = 8;
        using var fixture = Fixture.Create(resourceCount: 1, cacheTtl: null,
            new FakeSecretManagerClient(gated: true));
        var client = fixture.Client;
        var results = new string?[callerCount];
        var owner = new Thread(() => results[0] = fixture.Manager.GetValue<string>("Proof", Key(0)))
        { IsBackground = true, Name = "mapped-proof-shared-owner" };
        owner.Start();

        if (!client.WaitForEntry(OperationTimeout))
        {
            client.Release();
            owner.Join(OperationTimeout);
            return false;
        }

        var joiners = new Thread[callerCount - 1];
        using var ready = new CountdownEvent(joiners.Length);
        using var start = new ManualResetEventSlim(false);
        for (var index = 0; index < joiners.Length; index++)
        {
            var resultIndex = index + 1;
            joiners[index] = new Thread(() =>
            {
                start.Wait();
                // Readiness is post-gate: the blocked-state check below cannot mistake this gate for the flight wait.
                ready.Signal();
                results[resultIndex] = fixture.Manager.GetValue<string>("Proof", Key(0));
            })
            { IsBackground = true, Name = $"mapped-proof-joiner-{resultIndex}" };
            joiners[index].Start();
        }

        start.Set();
        var allPassedStartGate = ready.Wait(OperationTimeout);
        var allJoinedWait = allPassedStartGate && SpinWait.SpinUntil(
            () => joiners.All(thread => (thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0),
            TimeSpan.FromSeconds(5));

        client.Release();
        var ownerFinished = owner.Join(OperationTimeout);
        var joinersFinished = joiners.All(thread => thread.Join(OperationTimeout));
        return allJoinedWait
            && ownerFinished
            && joinersFinished
            && client.CallCount == 1
            && results.All(value => StringComparer.Ordinal.Equals(SyntheticPayload, value));
    }

    private static int RunBenchmark(Arguments args)
    {
        ThreadPool.GetMinThreads(out _, out var ioMinimum);
        if (!ThreadPool.SetMinThreads(args.ThreadPoolMinimum, ioMinimum))
        {
            throw new InvalidOperationException();
        }
        ThreadPool.GetMinThreads(out var actualWorkerMinimum, out var actualIoMinimum);

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            type = "metadata",
            harnessId = HarnessId,
            revision = args.Revision,
            runtime = RuntimeInformation.FrameworkDescription,
            processorCount = Environment.ProcessorCount,
            threadPoolWorkerMinimum = actualWorkerMinimum,
            threadPoolIoMinimum = actualIoMinimum,
            resourceCount = args.ResourceCount,
            callerCount = args.CallerCount,
            fakeLookupLatencyMs = args.LookupLatencyMs,
            sampleIntervalMs = args.SampleIntervalMs,
            repetitions = args.Repetitions,
            cacheModes = new { distinctCold = "off", sameResourceCold = "off", warmControl = "on" },
            expectedRemoteCallsByScenario = new
            {
                distinctCold = args.ResourceCount,
                sameResourceCold = 1,
                warmCache = 0
            },
            perScenarioThreadSamplesIsolated = false,
            adopterCalibrationStatus = "unavailable-not-supplied",
            valuesEmitted = false,
            resourceNamesEmitted = false,
            credentialsUsed = false
        }));

        var allResults = new List<RunResult>();
        for (var repetition = 1; repetition <= args.Repetitions; repetition++)
        {
            allResults.Add(Measure("distinct-cold", args.ResourceCount, args.CallerCount,
                cacheTtl: null, args, repetition));
            allResults.Add(Measure("same-resource-cold", resourceCount: 1, args.CallerCount,
                cacheTtl: null, args, repetition));
            allResults.Add(Measure("warm-cache", resourceCount: 1, args.CallerCount,
                cacheTtl: TimeSpan.FromMinutes(5), args, repetition, prewarm: true));
        }

        foreach (var group in allResults.GroupBy(result => result.Scenario, StringComparer.Ordinal))
        {
            var elapsed = group.Select(result => result.ElapsedMilliseconds).Order().ToArray();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                type = "summary",
                revision = args.Revision,
                scenario = group.Key,
                repetitions = elapsed.Length,
                medianElapsedMs = Median(elapsed),
                minimumElapsedMs = elapsed[0],
                maximumElapsedMs = elapsed[^1],
                elapsedSpreadRangeMs = elapsed[^1] - elapsed[0],
                maximumPeakActiveFakeCalls = group.Max(result => result.PeakActiveFakeCalls),
                valuesEmitted = false,
                resourceNamesEmitted = false
            }));
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            type = "suite-summary",
            revision = args.Revision,
            threadMetricScope = "suite-wide maximum across sequential scenarios and repetitions; not isolated by scenario",
            maximumSampledThreadPoolThreadCount = allResults.Max(result => result.ThreadPoolSamples.Max()),
            maximumSampledProcessThreadCount = allResults.Max(result => result.ProcessThreadSamples.Max()),
            valuesEmitted = false,
            resourceNamesEmitted = false
        }));

        return 0;
    }

    private static RunResult Measure(
        string scenario,
        int resourceCount,
        int callerCount,
        TimeSpan? cacheTtl,
        Arguments args,
        int repetition,
        bool prewarm = false)
    {
        using var fixture = Fixture.Create(resourceCount, cacheTtl,
            new FakeSecretManagerClient(TimeSpan.FromMilliseconds(args.LookupLatencyMs)));
        if (prewarm)
        {
            _ = fixture.Manager.GetValue<string>("Benchmark", Key(0));
            fixture.Client.ResetMetrics();
        }

        using var sampler = new MetricsSampler(TimeSpan.FromMilliseconds(args.SampleIntervalMs));
        using var ready = new CountdownEvent(callerCount);
        using var start = new ManualResetEventSlim(false);
        var failures = 0;
        var resolverThreadIds = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
        var threads = new Thread[callerCount];
        for (var caller = 0; caller < callerCount; caller++)
        {
            var firstResource = scenario == "distinct-cold" ? caller : 0;
            var resourceLimit = scenario == "distinct-cold" ? resourceCount : 1;
            threads[caller] = new Thread(() =>
            {
                ready.Signal();
                start.Wait();
                try
                {
                    for (var resource = firstResource; resource < resourceLimit; resource += callerCount)
                    {
                        resolverThreadIds.TryAdd(Thread.CurrentThread.ManagedThreadId, 0);
                        var value = fixture.Manager.GetValue<string>("Benchmark", Key(resource));
                        if (!StringComparer.Ordinal.Equals(SyntheticPayload, value))
                        {
                            Interlocked.Increment(ref failures);
                        }
                    }
                }
                catch
                {
                    Interlocked.Increment(ref failures);
                }
            })
            { IsBackground = true, Name = $"mapped-benchmark-{caller}" };
            threads[caller].Start();
        }

        if (!ready.Wait(OperationTimeout))
        {
            throw new InvalidOperationException();
        }

        sampler.Start();
        var stopwatch = Stopwatch.StartNew();
        start.Set();
        var allFinished = threads.All(thread => thread.Join(OperationTimeout));
        stopwatch.Stop();
        var samples = sampler.Stop();
        var measuredRemoteCalls = fixture.Client.CallCount;
        var observedResolverThreads = resolverThreadIds.Count;
        var expectedResolverThreads = scenario == "distinct-cold"
            ? Math.Min(resourceCount, callerCount)
            : callerCount;
        var expectedRemoteCalls = scenario switch
        {
            "distinct-cold" => resourceCount,
            "same-resource-cold" => 1,
            "warm-cache" => 0,
            _ => throw new InvalidOperationException()
        };
        if (!allFinished || failures != 0 || observedResolverThreads != expectedResolverThreads
            || measuredRemoteCalls != expectedRemoteCalls)
        {
            throw new InvalidOperationException();
        }

        var result = new RunResult(scenario, repetition, stopwatch.Elapsed.TotalMilliseconds,
            measuredRemoteCalls, fixture.Client.PeakActiveCalls, samples.ThreadPoolThreadCounts,
            samples.ProcessThreadCounts);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            type = "sample",
            revision = args.Revision,
            harnessId = HarnessId,
            scenario,
            repetition,
            logicalResourceCount = resourceCount,
            callerCount,
            resolverThreadCount = observedResolverThreads,
            fakeLookupLatencyMs = args.LookupLatencyMs,
            cacheEnabled = cacheTtl is not null,
            expectedRemoteCalls,
            elapsedMs = Math.Round(result.ElapsedMilliseconds, 3),
            measuredRemoteCalls = result.MeasuredRemoteCalls,
            peakActiveFakeCalls = result.PeakActiveFakeCalls,
            threadPoolThreadCountSamplesNonIsolated = result.ThreadPoolSamples,
            processThreadCountSamplesNonIsolated = result.ProcessThreadSamples,
            valuesEmitted = false,
            resourceNamesEmitted = false
        }));
        return result;
    }

    private static double Median(double[] values) => values.Length % 2 == 1
        ? values[values.Length / 2]
        : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2d;

    private static AppSurfaceConfigKey Key(int index) => AppSurfaceConfigKey.Parse($"Harness:Key:{index}");

    private static int PrintUsageAndFail()
    {
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run --project <benchmark-project> -c Release -- proof");
        Console.WriteLine("   or: dotnet run --project <benchmark-project> -c Release -- benchmark [options]");
        Console.WriteLine("Options: --revision ID --resource-count N --caller-count N --lookup-ms N --sample-ms N --repetitions N --threadpool-min N");
    }

    private sealed record RunResult(
        string Scenario,
        int Repetition,
        double ElapsedMilliseconds,
        int MeasuredRemoteCalls,
        int PeakActiveFakeCalls,
        int[] ThreadPoolSamples,
        int[] ProcessThreadSamples);

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _services;

        private Fixture(ServiceProvider services, FakeSecretManagerClient client)
        {
            _services = services;
            Manager = services.GetRequiredService<IConfigManager>();
            Client = client;
        }

        internal IConfigManager Manager { get; }
        internal FakeSecretManagerClient Client { get; }

        internal static Fixture Create(int resourceCount, TimeSpan? cacheTtl, FakeSecretManagerClient client)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IEnvironmentProvider>(new EmptyEnvironmentProvider());
            services.UseAppSurfaceGoogleSecretManagerClient(client);
            services.ConfigureAppSurfaceGoogleSecretManager(options =>
            {
                options.CacheTtl = cacheTtl;
                for (var index = 0; index < resourceCount; index++)
                {
                    options.MapSecret(Key(index), $"projects/proof-project/secrets/bench-key-{index}/versions/1");
                }
            });

            var context = new StartupContext([], new NoHostModule());
            new AppSurfaceConfigModule().ConfigureServices(context, services);
            new AppSurfaceGoogleSecretManagerModule().ConfigureServices(context, services);
            foreach (var registration in context.CustomRegistrations)
            {
                registration(services);
            }

            return new Fixture(services.BuildServiceProvider(), client);
        }

        public void Dispose() => _services.Dispose();
    }

    private sealed class EmptyEnvironmentProvider : IEnvironmentProvider
    {
        public string Environment => "Benchmark";
        public bool IsDevelopment => false;
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() =>
            new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private sealed class FakeSecretManagerClient : IAppSurfaceGoogleSecretManagerClient, IDisposable
    {
        private readonly byte[] _payload = Encoding.UTF8.GetBytes(SyntheticPayload);
        private readonly TimeSpan _latency;
        private readonly ManualResetEventSlim? _entered;
        private readonly ManualResetEventSlim? _release;
        private readonly System.Collections.Concurrent.ConcurrentQueue<int> _callingThreadIds = new();
        private int _callCount;
        private int _activeCalls;
        private int _peakActiveCalls;

        internal FakeSecretManagerClient(TimeSpan latency)
        {
            _latency = latency;
        }

        internal FakeSecretManagerClient(bool gated)
        {
            if (gated)
            {
                _entered = new ManualResetEventSlim(false);
                _release = new ManualResetEventSlim(false);
            }
        }

        internal int CallCount => Volatile.Read(ref _callCount);
        internal int PeakActiveCalls => Volatile.Read(ref _peakActiveCalls);
        internal IEnumerable<int> CallingThreadIds => _callingThreadIds.ToArray();
        internal bool WaitForEntry(TimeSpan timeout) => _entered?.Wait(timeout) ?? false;
        internal void Release() => _release?.Set();

        public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout)
        {
            Interlocked.Increment(ref _callCount);
            _callingThreadIds.Enqueue(Thread.CurrentThread.ManagedThreadId);
            var active = Interlocked.Increment(ref _activeCalls);
            UpdatePeak(active);
            try
            {
                if (_release is not null)
                {
                    _entered!.Set();
                    if (!_release.Wait(OperationTimeout))
                    {
                        throw new TimeoutException();
                    }
                }
                else if (_latency > TimeSpan.Zero)
                {
                    Thread.Sleep(_latency);
                }

                return new AppSurfaceGoogleSecretPayload(_payload, resourceName);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        internal void ResetMetrics()
        {
            Interlocked.Exchange(ref _callCount, 0);
            Interlocked.Exchange(ref _peakActiveCalls, 0);
            while (_callingThreadIds.TryDequeue(out _)) { }
        }

        private void UpdatePeak(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref _peakActiveCalls);
                if (current >= value || Interlocked.CompareExchange(ref _peakActiveCalls, value, current) == current)
                {
                    return;
                }
            }
        }

        public void Dispose()
        {
            _entered?.Dispose();
            _release?.Dispose();
        }
    }

    private sealed class MetricsSampler : IDisposable
    {
        private readonly TimeSpan _interval;
        private readonly ManualResetEventSlim _stop = new(false);
        private readonly List<int> _threadPoolCounts = [];
        private readonly List<int> _processThreadCounts = [];
        private Thread? _thread;

        internal MetricsSampler(TimeSpan interval)
        {
            _interval = interval;
        }

        internal void Start()
        {
            Sample();
            _thread = new Thread(SampleLoop) { IsBackground = true, Name = "mapped-benchmark-sampler" };
            _thread.Start();
        }

        internal SampleSet Stop()
        {
            _stop.Set();
            _thread?.Join(OperationTimeout);
            return new SampleSet(_threadPoolCounts.ToArray(), _processThreadCounts.ToArray());
        }

        private void SampleLoop()
        {
            while (!_stop.Wait(_interval))
            {
                Sample();
            }
            Sample();
        }

        private void Sample()
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            _threadPoolCounts.Add(ThreadPool.ThreadCount);
            _processThreadCounts.Add(process.Threads.Count);
        }

        public void Dispose() => _stop.Dispose();
    }

    private sealed record SampleSet(int[] ThreadPoolThreadCounts, int[] ProcessThreadCounts);

    private sealed record Arguments(
        string Revision,
        int ResourceCount,
        int CallerCount,
        int LookupLatencyMs,
        int SampleIntervalMs,
        int Repetitions,
        int ThreadPoolMinimum)
    {
        internal static Arguments Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException();
                }
                values.Add(args[index], args[index + 1]);
            }

            var result = new Arguments(
                Get(values, "--revision", "unrecorded"),
                GetInt(values, "--resource-count", 16),
                GetInt(values, "--caller-count", 16),
                GetInt(values, "--lookup-ms", 50),
                GetInt(values, "--sample-ms", 10),
                GetInt(values, "--repetitions", 9),
                GetInt(values, "--threadpool-min", 16));
            if (result.ResourceCount <= 0 || result.CallerCount <= 0
                || result.LookupLatencyMs < 0 || result.SampleIntervalMs <= 0
                || result.Repetitions <= 0 || result.ThreadPoolMinimum <= 0)
            {
                throw new ArgumentOutOfRangeException();
            }
            return result;
        }

        private static string Get(Dictionary<string, string> values, string key, string fallback) =>
            values.TryGetValue(key, out var value) ? value : fallback;

        private static int GetInt(Dictionary<string, string> values, string key, int fallback) =>
            values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed)
                ? parsed
                : values.ContainsKey(key) ? throw new ArgumentException() : fallback;
    }
}
