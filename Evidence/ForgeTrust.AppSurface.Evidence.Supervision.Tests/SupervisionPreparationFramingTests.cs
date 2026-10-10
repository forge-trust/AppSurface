using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Original framed-stream ownership only; no kernel peer, worker or admission is fabricated.</summary>
public sealed class SupervisionPreparationFramingTests
{
    private static readonly byte[] Request = Encoding.UTF8.GetBytes("{\"op\":\"ready\"}\n");

    private static MemoryStream Stream()
    {
        var stream = new MemoryStream(); stream.Write(Request); stream.Position = 0; return stream;
    }

    [Fact]
    public async Task OrderedPreparationAndStartPrecedeTheSoleClosingResponse()
    {
        var stream = Stream(); await using var framing = new SupervisionControlLineFraming(stream);
        await framing.ReadRequestAsync(default);
        await framing.WritePreparationAsync(120000, default);
        Assert.True(stream.CanWrite);
        await framing.WriteAdmissionStartAsync(default);
        Assert.True(stream.CanWrite);
        await framing.WriteResponseAsync(Encoding.UTF8.GetBytes("{\"ok\":true,\"descriptor\":{}}"), default);
        Assert.False(stream.CanWrite);
        var lines = Encoding.UTF8.GetString(stream.ToArray()[Request.Length..]).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        using var prepared = JsonDocument.Parse(lines[0]);
        Assert.Equal("pre-admission", prepared.RootElement.GetProperty("phase").GetString());
        Assert.Equal(120000, prepared.RootElement.GetProperty("remaining_job_ms").GetInt64());
        using var started = JsonDocument.Parse(lines[1]);
        Assert.Equal("admission-start", started.RootElement.GetProperty("phase").GetString());
        using var response = JsonDocument.Parse(lines[2]);
        Assert.True(response.RootElement.TryGetProperty("descriptor", out _));
    }

    [Theory]
    [InlineData("start-before-preparation")]
    [InlineData("preparation-replay")]
    [InlineData("ready-before-start")]
    [InlineData("start-replay")]
    [InlineData("invalid-zero-allowance")]
    [InlineData("invalid-oversized-allowance")]
    public async Task InvalidPhaseOrderClosesWithoutAReadyResponse(string control)
    {
        var stream = Stream(); await using var framing = new SupervisionControlLineFraming(stream);
        await framing.ReadRequestAsync(default);
        if (control is "preparation-replay" or "ready-before-start" or "start-replay")
            await framing.WritePreparationAsync(120000, default);
        if (control == "start-replay") await framing.WriteAdmissionStartAsync(default);
        var error = await Assert.ThrowsAsync<ControlLineException>(() => control switch
        {
            "start-before-preparation" or "start-replay" => framing.WriteAdmissionStartAsync(default),
            "ready-before-start" => framing.WriteResponseAsync(Encoding.UTF8.GetBytes("{\"ok\":true}"), default),
            "invalid-zero-allowance" => framing.WritePreparationAsync(0, default),
            "invalid-oversized-allowance" => framing.WritePreparationAsync(3600001, default),
            _ => framing.WritePreparationAsync(120000, default)
        });
        Assert.Equal(ControlLineFailure.InvalidSequence, error.Failure);
        Assert.False(stream.CanWrite);
        Assert.DoesNotContain("descriptor", Encoding.UTF8.GetString(stream.ToArray()[Request.Length..]));
    }

    [Fact]
    public async Task CleanupRequestCannotIssuePreparationFrames()
    {
        var stream = new MemoryStream(); stream.Write(Encoding.UTF8.GetBytes("{\"op\":\"stop\"}\n")); stream.Position = 0;
        await using var framing = new SupervisionControlLineFraming(stream);
        await framing.ReadRequestAsync(default);
        var error = await Assert.ThrowsAsync<ControlLineException>(() => framing.WritePreparationAsync(120000, default));
        Assert.Equal(ControlLineFailure.InvalidSequence, error.Failure);
        Assert.False(stream.CanWrite);
    }

    [Fact]
    public async Task PreparationCancellationStillJoinsTheOriginalIgnoringWrite()
    {
        var stream = new HeldWriteStream(); await using var framing = new SupervisionControlLineFraming(stream);
        await framing.ReadRequestAsync(default);
        using var cancellation = new CancellationTokenSource();
        var write = framing.WritePreparationAsync(120000, cancellation.Token);
        try
        {
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            Assert.False(write.IsCompleted);
            Assert.False(stream.Finished.Task.IsCompleted);
        }
        finally { stream.Release.TrySetResult(); }
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(stream.Finished.Task.IsCompletedSuccessfully);
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
            try { await Release.Task; await base.WriteAsync(buffer, CancellationToken.None); }
            finally { Finished.TrySetResult(); }
        }
    }
}
