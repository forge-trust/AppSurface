using System.Diagnostics;
using System.Collections.Concurrent;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

await CleanupTimeoutShouldInvalidateClaimWithoutLeakingDiagnosticAsync();
await DirectDisposalShouldFailWithStableCodeAsync();
await ExplicitLifetimeShouldStopJoinAndThenDisposeAsync();
await ErrorUnwindShouldExposeCleanupDiagnosticAsync();
Console.WriteLine("EVIDENCEHOST CLEANUP PACKAGE CONSUMER PASS");

static async Task CleanupTimeoutShouldInvalidateClaimWithoutLeakingDiagnosticAsync()
{
    var producer = new GatedDisposalProducer("cleanup-hang");
    var host = EvidenceHostBootstrap.Create(CreatePlan(producer.Id), registration => registration.AddProducer(producer), ShortBudgets());
    var run = host.RunAsync();
    var timer = Stopwatch.StartNew();

    try
    {
        await producer.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var manifest = await run.WaitAsync(TimeSpan.FromSeconds(3));
        timer.Stop();

        Require(manifest.ProducerResults.Single().Outcome == EvidenceProducerOutcome.Passed, "The producer did not pass before cleanup.");
        Require(manifest.ClaimKind == EvidenceClaimKind.None, "An incomplete cleanup retained an evidence claim.");
        Require(!manifest.Metrics.CleanupCompleted, "The manifest reported incomplete cleanup as completed.");
        Require(manifest.Metrics.CleanupDiagnostic == "Evidence cleanup timed out stopping/joining/disposing producer 'cleanup-hang'; owned work may remain active.", "The cleanup diagnostic was not the expected safe, bounded message.");
        Require(!manifest.Metrics.CleanupDiagnostic.Contains("private-payload", StringComparison.Ordinal), "The cleanup diagnostic leaked producer data.");
        Require(timer.Elapsed <= TimeSpan.FromSeconds(3), "The run did not reach terminal output within three seconds.");
        Console.WriteLine("PASS manifest: producer passed, claim none, cleanup incomplete, safe diagnostic, terminal <= 3s");
    }
    finally
    {
        producer.ReleaseDisposal();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        await producer.DisposalCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    }
}

static async Task DirectDisposalShouldFailWithStableCodeAsync()
{
    var producer = new GatedDisposalProducer("direct-dispose-hang");
    var host = EvidenceHostBootstrap.Create(CreatePlan(producer.Id), registration => registration.AddProducer(producer), ShortBudgets());
    Task? disposal = null;
    var timer = Stopwatch.StartNew();

    try
    {
        disposal = host.DisposeAsync().AsTask();
        await producer.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var failure = await CaptureAsync<EvidenceHostException>(disposal.WaitAsync(TimeSpan.FromSeconds(3)));
        timer.Stop();

        Require(failure.Code == "ASEVD306", "Direct disposal did not report ASEVD306.");
        var repeated = await CaptureAsync<EvidenceHostException>(host.DisposeAsync().AsTask());
        Require(repeated.Code == "ASEVD306", "Repeated direct disposal hid incomplete cleanup.");
        Require(timer.Elapsed <= TimeSpan.FromSeconds(3), "Direct disposal did not reach terminal output within three seconds.");
        Console.WriteLine("PASS direct dispose: ASEVD306, terminal <= 3s");
    }
    finally
    {
        producer.ReleaseDisposal();
        if (disposal is not null)
        {
            try
            {
                await disposal.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (EvidenceHostException)
            {
                // The expected bounded-disposal failure is asserted above.
            }
        }

        await producer.DisposalCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }
}

static async Task ExplicitLifetimeShouldStopJoinAndThenDisposeAsync()
{
    var producer = new StopJoinProducer("stop-join");
    var host = EvidenceHostBootstrap.Create(
        CreatePlan(producer.Id),
        registration => registration.AddProducer(producer),
        ShortBudgets() with { ExecutionTimeout = TimeSpan.FromSeconds(5) });
    using var cancellation = new CancellationTokenSource();
    Task<EvidenceManifest>? run = null;

    try
    {
        run = host.RunAsync(cancellationToken: cancellation.Token);
        await producer.ExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        var manifest = await run.WaitAsync(TimeSpan.FromSeconds(3));

        Require(manifest.ClaimKind == EvidenceClaimKind.None, "Caller cancellation unexpectedly retained an evidence claim.");
        Require(manifest.Metrics.CleanupCompleted, "Cooperative stop/join did not complete cleanup.");
        Require(producer.Events.SequenceEqual(["stop-started", "callback-joined", "stop-completed", "dispose"]), "Ordinary disposal did not follow the explicit stop and callback join.");
        Require(producer.StopCallCount == 1, "The explicit lifetime registration was not called exactly once.");
        Console.WriteLine("PASS explicit lifetime: StopAsync called, callback joined, then ordinary disposal");
    }
    finally
    {
        producer.ReleaseExecution();
        if (run is not null)
        {
            await run.WaitAsync(TimeSpan.FromSeconds(3));
        }

        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    }
}

static async Task ErrorUnwindShouldExposeCleanupDiagnosticAsync()
{
    var producer = new GatedDisposalProducer("unwind-error");
    var host = EvidenceHostBootstrap.Create(CreatePlan("missing"), registration => registration.AddProducer(producer), ShortBudgets());
    try
    {
        var failure = await CaptureAsync<EvidenceHostException>(host.RunAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        Require(failure.Code == "ASEVD303", "Cleanup replaced the original registration failure.");
        Require(host.CleanupDiagnostic is not null && host.CleanupDiagnostic.Contains("unwind-error", StringComparison.Ordinal),
            "Error unwinding hid the owned cleanup failure.");
        Require(!host.CleanupDiagnostic!.Contains("private-payload", StringComparison.Ordinal), "Error unwinding exposed a callback payload.");
        Console.WriteLine("PASS error unwind: original ASEVD303 preserved, cleanup failure available on host");
    }
    finally
    {
        producer.ReleaseDisposal();
        await producer.DisposalCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await host.DisposeAsync();
    }
}

static EvidenceHostOptions ShortBudgets() => new()
{
    ExecutionTimeout = TimeSpan.FromHours(1),
    CleanupTimeout = TimeSpan.FromSeconds(1),
};

static EvidencePlan CreatePlan(string producerId)
{
    var assertionId = $"{producerId}/assertion@1";
    var producer = new EvidenceProducerDeclaration(producerId, "consumer fixture", "1.0.0", [], [assertionId], [], 30);
    var obligation = new EvidenceObligation("cleanup", "lifecycle", "Evidence cleanup completes safely.", [producerId], assertionId);
    var profile = new EvidenceProfile("persistence", EvidenceProfileScope.Targeted, [], [producer], [obligation]);

    return new EvidencePlan(
        "1.0",
        "consumer-cleanup",
        "policy-digest",
        "diff-digest",
        profile,
        [new NormalizedDiffPath("src/Consumer.cs")],
        [obligation.Id],
        "plan-digest");
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static async Task<TException> CaptureAsync<TException>(Task task)
    where TException : Exception
{
    try
    {
        await task;
    }
    catch (TException exception)
    {
        return exception;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

sealed class GatedDisposalProducer(string id) : PassingProducer(id), IAsyncDisposable
{
    private readonly TaskCompletionSource _disposalGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource DisposalCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        DisposalStarted.TrySetResult();
        try
        {
            await _disposalGate.Task.ConfigureAwait(false);
        }
        finally
        {
            DisposalCompleted.TrySetResult();
        }
    }

    public void ReleaseDisposal() => _disposalGate.TrySetResult();
}

sealed class StopJoinProducer(string id) : PassingProducer(id), IEvidenceExecutionLifetime, IAsyncDisposable
{
    private readonly TaskCompletionSource _executionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _callbackJoined = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<string> _events = new();
    private int _stopCallCount;

    public TaskCompletionSource ExecutionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<string> Events => _events.ToArray();

    public int StopCallCount => _stopCallCount;

    public override async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
    {
        ExecutionStarted.TrySetResult();
        await _executionGate.Task.ConfigureAwait(false);
        _events.Enqueue("callback-joined");
        _callbackJoined.TrySetResult();
        return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, [$"{Id}/assertion@1"]);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _stopCallCount);
        _events.Enqueue("stop-started");
        _executionGate.TrySetResult();
        await _callbackJoined.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        _events.Enqueue("stop-completed");
    }

    public ValueTask DisposeAsync()
    {
        _events.Enqueue("dispose");
        return ValueTask.CompletedTask;
    }

    public void ReleaseExecution() => _executionGate.TrySetResult();
}

class PassingProducer(string id) : IEvidenceProducer
{
    public string Id { get; } = id;

    public virtual ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, [$"{Id}/assertion@1"]));
}
