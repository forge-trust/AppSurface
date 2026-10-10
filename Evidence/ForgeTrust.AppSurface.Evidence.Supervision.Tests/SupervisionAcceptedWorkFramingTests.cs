#if EVIDENCE_PRIVATE_ACCEPTED_WORK
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Portable data-framing controls for private accepted-work variants; no Linux peer is fabricated.</summary>
public sealed class SupervisionAcceptedWorkFramingTests
{
    private static readonly byte[] Request = Encoding.UTF8.GetBytes("{\"op\":\"n16-accepted-work\"}\n");

    private static MemoryStream AcceptedRequest()
    {
        var stream = new MemoryStream(); stream.Write(Request); stream.Position = 0; return stream;
    }

    /// <summary>Confirms the intermediate acceptance remains on the original stream before its sole final reply.</summary>
    [Fact]
    public async Task AcceptedPhasePrecedesFinalReplyAndPreservesTheStream()
    {
        var stream = AcceptedRequest(); await using var framing = new SupervisionControlLineFraming(stream);
        Assert.IsType<EvidenceAcceptedBlockedWorkControlRequest>(await framing.ReadRequestAsync(default));
        await framing.WriteAcceptedBlockedWorkAsync(default);
        Assert.True(stream.CanWrite);
        await framing.WriteResponseAsync(Encoding.UTF8.GetBytes("{\"ok\":true,\"terminal\":true}"), default);
        Assert.False(stream.CanWrite);
        var lines = Encoding.UTF8.GetString(stream.ToArray()[Request.Length..]).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        using var accepted = JsonDocument.Parse(lines[0]);
        Assert.True(accepted.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("work-accepted", accepted.RootElement.GetProperty("phase").GetString());
        Assert.True(accepted.RootElement.GetProperty("body_blocked").GetBoolean());
        using var final = JsonDocument.Parse(lines[1]);
        Assert.True(final.RootElement.GetProperty("terminal").GetBoolean());
    }

    /// <summary>Rejects another operation and duplicate acceptance without leaving the stream reusable.</summary>
    [Fact]
    public async Task WrongRequestAndDuplicateAcceptanceCloseTheSingleAttempt()
    {
        var wrong = new MemoryStream(Encoding.UTF8.GetBytes("{\"op\":\"stop\"}\n"));
        await using (var framing = new SupervisionControlLineFraming(wrong))
        {
            await framing.ReadRequestAsync(default);
            var error = await Assert.ThrowsAsync<ControlLineException>(() => framing.WriteAcceptedBlockedWorkAsync(default));
            Assert.Equal(ControlLineFailure.InvalidSequence, error.Failure);
            Assert.False(wrong.CanWrite);
        }

        var stream = AcceptedRequest(); await using var acceptedFraming = new SupervisionControlLineFraming(stream);
        await acceptedFraming.ReadRequestAsync(default);
        await acceptedFraming.WriteAcceptedBlockedWorkAsync(default);
        var duplicate = await Assert.ThrowsAsync<ControlLineException>(() => acceptedFraming.WriteAcceptedBlockedWorkAsync(default));
        Assert.Equal(ControlLineFailure.InvalidSequence, duplicate.Failure);
        Assert.False(stream.CanWrite);
    }

    /// <summary>Rejects a final response before acceptance and a second request attempt.</summary>
    [Fact]
    public async Task EarlyFinalAndRepeatedReadAreRejected()
    {
        var stream = AcceptedRequest(); await using var framing = new SupervisionControlLineFraming(stream);
        await framing.ReadRequestAsync(default);
        var early = await Assert.ThrowsAsync<ControlLineException>(() =>
            framing.WriteResponseAsync(Encoding.UTF8.GetBytes("{\"ok\":true}"), default));
        Assert.Equal(ControlLineFailure.InvalidSequence, early.Failure);
        Assert.False(stream.CanWrite);

        var second = AcceptedRequest(); await using var secondFraming = new SupervisionControlLineFraming(second);
        await secondFraming.ReadRequestAsync(default);
        var reread = await Assert.ThrowsAsync<ControlLineException>(() => secondFraming.ReadRequestAsync(default));
        Assert.Equal(ControlLineFailure.InvalidSequence, reread.Failure);
        Assert.False(second.CanWrite);
    }

    /// <summary>Closes and joins an acceptance write already in progress before rejecting a competing final reply.</summary>
    [Fact]
    public async Task FinalReplyDuringAcceptanceJoinsTheOriginalWrite()
    {
        var stream = new HeldWriteStream(); await using var framing = new SupervisionControlLineFraming(stream);
        await framing.ReadRequestAsync(default);
        var acceptance = framing.WriteAcceptedBlockedWorkAsync(default);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var final = framing.WriteResponseAsync(Encoding.UTF8.GetBytes("{\"ok\":true}"), default);
        try
        {
            Assert.False(final.IsCompleted);
            Assert.False(stream.Finished.Task.IsCompleted);
        }
        finally { stream.Release.TrySetResult(); }
        await Assert.ThrowsAsync<ControlLineException>(() => acceptance.WaitAsync(TimeSpan.FromSeconds(2)));
        var finalError = await Assert.ThrowsAsync<ControlLineException>(() => final.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(ControlLineFailure.InvalidSequence, finalError.Failure);
        Assert.True(stream.Finished.Task.IsCompletedSuccessfully);
        Assert.False(stream.CanWrite);
    }

    /// <summary>Cancellation closes the stream but retains and awaits an underlying write that ignores cancellation.</summary>
    [Fact]
    public async Task CancelledAcceptanceStillJoinsItsOriginalWriteAndClose()
    {
        var stream = new HeldWriteStream(); await using var framing = new SupervisionControlLineFraming(stream);
        await framing.ReadRequestAsync(default);
        using var cancellation = new CancellationTokenSource();
        var acceptance = framing.WriteAcceptedBlockedWorkAsync(cancellation.Token);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        try
        {
            Assert.False(acceptance.IsCompleted);
            Assert.False(stream.Finished.Task.IsCompleted);
        }
        finally { stream.Release.TrySetResult(); }
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acceptance.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(stream.Finished.Task.IsCompletedSuccessfully);
        Assert.False(stream.CanWrite);
    }

    /// <summary>Maps an actual framing write failure to the closed I/O category and closes the stream.</summary>
    [Fact]
    public async Task AcceptanceWriteFailureIsSanitizedAndClosed()
    {
        var stream = new FailingWriteStream(); await using var framing = new SupervisionControlLineFraming(stream);
        await framing.ReadRequestAsync(default);
        var error = await Assert.ThrowsAsync<ControlLineException>(() => framing.WriteAcceptedBlockedWorkAsync(default));
        Assert.Equal(ControlLineFailure.IoFailed, error.Failure);
        Assert.False(stream.CanWrite);
    }

    private sealed class HeldWriteStream : MemoryStream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal HeldWriteStream() { Write(Request); Position = 0; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try { await Release.Task.ConfigureAwait(false); await base.WriteAsync(buffer, CancellationToken.None).ConfigureAwait(false); }
            finally { Finished.TrySetResult(); }
        }
    }

    private sealed class FailingWriteStream : MemoryStream
    {
        internal FailingWriteStream() { Write(Request); Position = 0; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("private framing test failure"));
    }
}
#endif
