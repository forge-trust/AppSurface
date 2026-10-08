using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Xunit;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Portable actual-task/pump bookkeeping and closed-data controls; no native owner or lease is created.</summary>
public sealed class SupervisionN07OwnedCheckpointTests
{
    internal const string Frame = "{\"schema\":\"issue779-n07-precleanup-allocation-fault-v1\",\"phase\":\"Allocation\",\"operation\":\"CheckParentIdentity\",\"error_family\":\"Admission\",\"capture_point\":\"AllocateCatchBeforeCallbackRethrow\",\"terminal_observed\":false,\"observation_only\":true,\"native_authority\":false,\"native_acceptance\":false}";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Source() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<byte[]> Bytes() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Prepared(LinuxN07CheckpointOrder order) { order.Claim(true); order.Complete(true); }
    private static void Committed(LinuxN07CheckpointOrder order) { order.Claim(false); order.Complete(false); }

    [Fact]
    public async Task ChunkedOriginalFrameDoesNotPublishBeforeLf()
    {
        var observation = new SupervisionN07PrecleanupObservation();
        var bytes = Encoding.UTF8.GetBytes(Frame);
        observation.Feed(bytes.AsSpan(0, 7)); observation.Feed(bytes.AsSpan(7));
        Assert.False(observation.Task.IsCompleted);
        observation.Feed([(byte)'\n']);
        Assert.Equal(bytes, await observation.Task.WaitAsync(Bound));
        observation.Complete(true); Assert.False(observation.Failed);
    }

    [Fact]
    public async Task MissingEofFrameFailsInsteadOfRelease()
    {
        var observation = new SupervisionN07PrecleanupObservation(); observation.Complete(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => observation.Task.WaitAsync(Bound));
        Assert.True(observation.Failed);
    }

    [Fact]
    public async Task WrongOperationOrDuplicateKeyCannotRelease()
    {
        foreach (var frame in new[] { Frame.Replace("CheckParentIdentity", "CreateDirectory", StringComparison.Ordinal),
            Frame.Replace("\"phase\":\"Allocation\",", "\"phase\":\"Allocation\",\"phase\":\"Allocation\",", StringComparison.Ordinal) })
        {
            var observation = new SupervisionN07PrecleanupObservation(); observation.Feed(Encoding.UTF8.GetBytes(frame + "\n"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => observation.Task.WaitAsync(Bound));
        }
    }

    [Fact]
    public async Task LaterPumpFailureCannotBecomeSuccessfulFinalObservation()
    {
        var observation = new SupervisionN07PrecleanupObservation(); observation.Feed(Encoding.UTF8.GetBytes(Frame + "\n"));
        await observation.Task.WaitAsync(Bound); observation.Complete(false); Assert.True(observation.Failed);
    }

    [Fact]
    public async Task ActualPairedCollectorFeedsOnlyOriginalStderrAndStillJoinsBothEofs()
    {
        var observation = new SupervisionN07PrecleanupObservation();
        using var stdout = new MemoryStream(); using var stderr = new MemoryStream(Encoding.UTF8.GetBytes(Frame + "\n"));
        var collector = new SupervisionOutputCollector(); collector.ObserveN07(observation);
        var receipt = await collector.CollectAsync(stdout, stderr, default).WaitAsync(Bound);
        Assert.True(receipt.Successful); Assert.True(receipt.Stdout.EndOfStream); Assert.True(receipt.Stderr.EndOfStream);
        Assert.Equal(Encoding.UTF8.GetBytes(Frame), await observation.Task.WaitAsync(Bound));
    }

    [Fact]
    public async Task CallerReadyAndPreparedDoNotReleaseNextAccept()
    {
        var order = new LinuxN07CheckpointOrder(); var exit = Source(); var stderr = Bytes(); order.Reserve();
        var wait = order.BeforeNextAcceptAsync(exit.Task, stderr.Task, default);
        order.Retain(wait); Prepared(order); Assert.False(wait.IsCompleted);
        Committed(order); Assert.False(wait.IsCompleted);
        stderr.SetResult(Encoding.UTF8.GetBytes(Frame)); await wait.WaitAsync(Bound);
        order.Close(); await order.JoinAsync().WaitAsync(Bound);
    }

    [Fact]
    public async Task EarlyFrameStillRequiresActualCommittedEvent()
    {
        var order = new LinuxN07CheckpointOrder(); order.Reserve(); var exit = Source(); var stderr = Bytes();
        stderr.SetResult(Encoding.UTF8.GetBytes(Frame));
        var wait = order.BeforeNextAcceptAsync(exit.Task, stderr.Task, default); Prepared(order);
        Assert.False(wait.IsCompleted); Committed(order); await wait.WaitAsync(Bound);
    }

    [Fact]
    public async Task OriginalWorkerTerminationCannotStandInForFaultFrame()
    {
        var order = new LinuxN07CheckpointOrder(); order.Reserve(); Prepared(order); Committed(order);
        var exit = Source(); var stderr = Bytes(); var wait = order.BeforeNextAcceptAsync(exit.Task, stderr.Task, default);
        exit.SetResult(); await Assert.ThrowsAsync<EvidenceAdmissionException>(() => wait.WaitAsync(Bound));
    }

    [Fact]
    public async Task CloseAndOriginalCancellationJoinRetainedWaitWithStickyFailure()
    {
        var order = new LinuxN07CheckpointOrder(); var exit = Source(); var stderr = Bytes();
        using var stop = new CancellationTokenSource(); order.Reserve(); Prepared(order); Committed(order);
        var wait = order.BeforeNextAcceptAsync(exit.Task, stderr.Task, stop.Token); order.Retain(wait);
        order.Close(); stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(Bound));
        await Assert.ThrowsAsync<EvidenceAdmissionException>(() => order.JoinAsync().WaitAsync(Bound)); Assert.True(order.Failed);
    }

    [Fact]
    public void DuplicateFinalFrameAndCanarySchemaCannotBeProjected()
    {
        Assert.Throws<InvalidOperationException>(() => LinuxN07FailureSettlement.RequirePrecleanupPrefix(
            Encoding.UTF8.GetBytes(Frame + "\n" + Frame + "\n")));
        Assert.Throws<InvalidOperationException>(() => SupervisionN07PrecleanupObservation.Validate(
            Encoding.UTF8.GetBytes(Frame.Replace("\"native_authority\":false", "\"native_authority\":true", StringComparison.Ordinal))));
    }
}
