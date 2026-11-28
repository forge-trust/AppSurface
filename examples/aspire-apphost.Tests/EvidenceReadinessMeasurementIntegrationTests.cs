using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Readiness;
using Xunit.Abstractions;

namespace AspireAppHostExample.Tests;

public sealed class EvidenceReadinessMeasurementIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ResourceBackedHost_ReportsMonotonicStartupAndOwnedCleanupMeasurements()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var resource = MeasuredLoopbackResource.Start();
        var producer = new PassingProducer();
        await using var host = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(resource);
                registration.AddProducer(producer);
            });

        var manifest = await host.RunAsync(cancellationToken: timeout.Token);
        var resourceResult = Assert.Single(manifest.ResourceResults);
        var producerResult = Assert.Single(manifest.ProducerResults);

        Assert.True(resourceResult.Outcome == EvidenceResourceOutcome.Ready, resourceResult.Diagnostic);
        Assert.Equal(EvidenceProducerOutcome.Passed, producerResult.Outcome);
        Assert.Equal(EvidenceClaimKind.TargetedComplete, manifest.ClaimKind);
        Assert.True(manifest.Metrics.CleanupCompleted);
        Assert.True(resource.StartupElapsedTicks > 0);
        Assert.True(resource.ReadyTimestamp > resource.FactoryInvocationTimestamp);
        Assert.True(resource.CleanupElapsedTicks > 0);
        Assert.True(resource.CleanupSettledTimestamp > resource.CleanupStartedTimestamp);
        Assert.Equal(1, producer.Runs);
        Assert.Equal(1, resource.Disposals);
        Assert.Equal(1, resource.RequestsServed);

        var receipt = new
        {
            schema = "forge-trust.appsurface.issue-780.resource-proof.v1",
            test = $"{nameof(EvidenceReadinessMeasurementIntegrationTests)}.{nameof(ResourceBackedHost_ReportsMonotonicStartupAndOwnedCleanupMeasurements)}",
            resource_kind = "loopback_tcp_http",
            monotonic_clock = "System.Diagnostics.Stopwatch",
            stopwatch_frequency = Stopwatch.Frequency,
            resource_startup = new
            {
                factory_invocation_timestamp_ticks = resource.FactoryInvocationTimestamp,
                ready_timestamp_ticks = resource.ReadyTimestamp,
                elapsed_ticks = resource.StartupElapsedTicks,
                elapsed_milliseconds = ToMilliseconds(resource.StartupElapsedTicks),
                evidence_host_readiness_milliseconds = resourceResult.ElapsedMilliseconds,
                outcome = resourceResult.Outcome.ToString(),
            },
            owned_cleanup = new
            {
                started_timestamp_ticks = resource.CleanupStartedTimestamp,
                settled_timestamp_ticks = resource.CleanupSettledTimestamp,
                elapsed_ticks = resource.CleanupElapsedTicks,
                elapsed_milliseconds = ToMilliseconds(resource.CleanupElapsedTicks),
                evidence_host_cleanup_milliseconds = manifest.Metrics.CleanupMilliseconds,
                completed = manifest.Metrics.CleanupCompleted,
                resource_disposals = resource.Disposals,
            },
            test_evidence = new
            {
                producer_outcome = producerResult.Outcome.ToString(),
                claim_kind = manifest.ClaimKind.ToString(),
                producer_runs = producer.Runs,
                http_requests_served = resource.RequestsServed,
            },
        };

        output.WriteLine($"APPSURFACE_780_RESOURCE_PROOF={JsonSerializer.Serialize(receipt)}");
    }

    private static EvidencePlan CreatePlan() => new(
        "1.0",
        "issue-780-resource-measurement",
        "policy-digest",
        "diff-digest",
        new EvidenceProfile(
            "http-condition",
            EvidenceProfileScope.Targeted,
            [new EvidenceResourceDeclaration("web", "loopback_http", 20, [])],
            [new EvidenceProducerDeclaration("http-condition", "assertion", "1.0.0", ["web"], ["http-condition@1"], [], 10)],
            [new EvidenceObligation("http-condition", "example", "Verify the declared HTTP condition.", ["http-condition"], "http-condition@1")]),
        [new NormalizedDiffPath("examples/web-app/Program.cs")],
        ["http-condition"],
        "plan-digest");

    private static double ToMilliseconds(long elapsedTicks) => elapsedTicks * 1000d / Stopwatch.Frequency;

    private sealed class MeasuredLoopbackResource : IEvidenceResourceReadiness, IAsyncDisposable
    {
        private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(15);
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _serverCancellation = new();
        private readonly Task _serverTask;
        private readonly HttpClient _client = new();
        private readonly Uri _endpoint;
        private readonly object _disposeGate = new();
        private Task? _disposeTask;
        private int _requestsServed;

        private MeasuredLoopbackResource(long factoryInvocationTimestamp, TcpListener listener)
        {
            FactoryInvocationTimestamp = factoryInvocationTimestamp;
            _listener = listener;
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _endpoint = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
            _serverTask = ServeAsync(listener, _serverCancellation.Token);
        }

        public string Id => "web";

        internal long FactoryInvocationTimestamp { get; }
        internal long ReadyTimestamp { get; private set; }
        internal long CleanupStartedTimestamp { get; private set; }
        internal long CleanupSettledTimestamp { get; private set; }
        internal long StartupElapsedTicks => ReadyTimestamp - FactoryInvocationTimestamp;
        internal long CleanupElapsedTicks => CleanupSettledTimestamp - CleanupStartedTimestamp;
        internal int Disposals { get; private set; }
        internal int RequestsServed => Volatile.Read(ref _requestsServed);

        internal static MeasuredLoopbackResource Start()
        {
            var factoryInvocationTimestamp = Stopwatch.GetTimestamp();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                return new MeasuredLoopbackResource(factoryInvocationTimestamp, listener);
            }
            catch
            {
                listener.Stop();
                throw;
            }
        }

        public async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            await HttpReadinessProbe.WaitAsync(
                _client,
                _endpoint,
                "/ready",
                ReadinessTimeout,
                cancellationToken: cancellationToken);
            ReadyTimestamp = Stopwatch.GetTimestamp();
        }

        public ValueTask DisposeAsync()
        {
            lock (_disposeGate)
            {
                _disposeTask ??= DisposeCoreAsync();
                return new ValueTask(_disposeTask);
            }
        }

        private async Task DisposeCoreAsync()
        {
            Disposals++;
            CleanupStartedTimestamp = Stopwatch.GetTimestamp();
            _client.Dispose();
            _serverCancellation.Cancel();
            _listener.Stop();
            await _serverTask;
            _serverCancellation.Dispose();
            CleanupSettledTimestamp = Stopwatch.GetTimestamp();
        }

        private async Task ServeAsync(TcpListener listener, CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
                    await RespondReadyAsync(connection.GetStream(), cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private async Task RespondReadyAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken);
            if (requestLine is null)
            {
                return;
            }

            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
            {
            }

            var status = string.Equals(requestLine, "GET /ready HTTP/1.1", StringComparison.Ordinal)
                ? "200 OK"
                : "404 Not Found";
            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, cancellationToken);
            Interlocked.Increment(ref _requestsServed);
        }
    }

    private sealed class PassingProducer : IEvidenceProducer
    {
        public string Id => "http-condition";
        internal int Runs { get; private set; }

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Runs++;
            return ValueTask.FromResult(new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["http-condition@1"]));
        }
    }
}
