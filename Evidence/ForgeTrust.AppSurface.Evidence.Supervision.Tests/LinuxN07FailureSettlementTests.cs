using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Detached terminal/output data controls only; no original holder, custody or kernel exit exists.</summary>
public sealed class LinuxN07FailureSettlementTests
{
    private sealed record Data(Guid Generation, LinuxProcessSample Identity, LinuxUnitProperties Terminal,
        LinuxCgroupSample Group, SupervisionOutputReceipt Output);
    private static Data Valid(int code = 2, int status = 6)
    {
        var generation = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var unit = LinuxUnitName.Create(LinuxUnitRole.Worker, generation).Value;
        var group = "/system.slice/" + unit;
        var stdout = new SupervisionOutputStreamReceipt(0, ImmutableArray<byte>.Empty, true, SupervisionOutputFailure.None);
        var bytes = ImmutableArray.CreateRange(Encoding.UTF8.GetBytes(SupervisionN07OwnedCheckpointTests.Frame + "\nprivate-fatal-text-canary"));
        var stderr = new SupervisionOutputStreamReceipt(bytes.Length, bytes, true, SupervisionOutputFailure.None);
        return new(generation, new(new(123, 42, 'S'), new(999, 999, 999, 999), new(987, 987, 987, 987), group),
            new(unit, "loaded", "failed", "failed", group, 0, 123, code, status,
                "999", "987", "exec", "control-group", true, 60_000_000, 10_000_000),
            new(true, false, false, 0, 14, 32),
            new(stdout, stderr, bytes.Length, 8192, SupervisionOutputFailure.None, false, false));
    }
    private static LinuxN07FailureSettlement Encode(Data data, TaskStatus monitor = TaskStatus.Faulted,
        bool pipesClosed = true) => LinuxN07FailureSettlement.CreateDetached(data.Generation, data.Identity,
            data.Terminal, data.Group, data.Output, monitor, true, true, true, true, pipesClosed);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 6)]
    [InlineData(3, 6)]
    public void ActualNormalOrSignalCodeIsNeverRewrittenAsNormalOne(int code, int status)
    {
        using var json = JsonDocument.Parse(Encode(Valid(code, status)).Bytes);
        Assert.Equal(code, json.RootElement.GetProperty("terminal").GetProperty("exec_main_code").GetInt32());
        Assert.Equal(status, json.RootElement.GetProperty("terminal").GetProperty("exec_main_status").GetInt32());
        Assert.False(json.RootElement.GetProperty("native_acceptance").GetBoolean());
    }

    [Theory]
    [InlineData((int)TaskStatus.RanToCompletion, true)]
    [InlineData((int)TaskStatus.Faulted, false)]
    [InlineData((int)TaskStatus.Canceled, false)]
    public void MonitorCompletionDoesNotMasqueradeAsSuccessfulJoin(int status, bool successful)
    {
        using var json = JsonDocument.Parse(Encode(Valid(), (TaskStatus)status).Bytes);
        Assert.True(json.RootElement.GetProperty("monitor").GetProperty("completed").GetBoolean());
        Assert.Equal(successful, json.RootElement.GetProperty("monitor").GetProperty("completed_successfully").GetBoolean());
    }

    [Fact]
    public void IncompleteMonitorOrPipeJoinCannotPublish()
    {
        Assert.Throws<InvalidOperationException>(() => Encode(Valid(), TaskStatus.WaitingForActivation));
        Assert.Throws<InvalidOperationException>(() => Encode(Valid(), pipesClosed: false));
    }

    [Fact]
    public void MissingEofOrDiscardedBytesCannotBeCalledFullOutput()
    {
        var data = Valid();
        Assert.Throws<InvalidOperationException>(() => Encode(data with
            { Output = data.Output with { Stderr = data.Output.Stderr with { EndOfStream = false } } }));
        Assert.Throws<InvalidOperationException>(() => Encode(data with
            { Output = data.Output with { Stderr = data.Output.Stderr with { Prefix = ImmutableArray<byte>.Empty } } }));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 65)]
    public void AmbiguousOrInvalidTerminalRejects(int code, int status) =>
        Assert.Throws<InvalidOperationException>(() => Encode(Valid(code, status)));

    [Fact]
    public void HashesFullBytesWithoutEchoAndReturnsCopies()
    {
        var value = Encode(Valid());
        var first = value.Bytes;
        Assert.DoesNotContain("private-fatal-text-canary", Encoding.UTF8.GetString(first));
        Assert.InRange(first.Length, 1, LinuxN07FailureSettlement.MaximumJsonBytes);
        Array.Fill(first, (byte)0);
        Assert.NotEqual(first, value.Bytes);
    }

    [Fact]
    public void PrunedSelectedGroupHasNoInventedInode()
    {
        using var json = JsonDocument.Parse(Encode(Valid() with { Group = new(false, null, null, null, null, null) }).Bytes);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("cgroup").GetProperty("inode").ValueKind);
    }

    private static Data WithFullStderr(int length, byte fill = (byte)'x')
    {
        var data = Valid();
        var bytes = Enumerable.Repeat(fill, length).ToArray();
        var prefix = Encoding.UTF8.GetBytes(SupervisionN07OwnedCheckpointTests.Frame + "\n");
        prefix.CopyTo(bytes, 0);
        var stderr = new SupervisionOutputStreamReceipt(length, ImmutableArray.CreateRange(bytes), true, SupervisionOutputFailure.None);
        return data with { Output = new(data.Output.Stdout, stderr, length, 128 * 1024,
            SupervisionOutputFailure.None, false, false) };
    }

    [Fact]
    public void PrivateRawPairRoundTripsActualFullBytesAndOriginalHashes()
    {
        var data = Valid(); var settlement = Encode(data);
        using var raw = JsonDocument.Parse(settlement.SerializePrivateStreams());
        Assert.Equal("issue779-n07-joined-worker-raw-v1", raw.RootElement.GetProperty("schema").GetString());
        Assert.Equal(6, raw.RootElement.EnumerateObject().Count());
        var stdout = raw.RootElement.GetProperty("stdout_base64").GetBytesFromBase64();
        var stderr = raw.RootElement.GetProperty("stderr_base64").GetBytesFromBase64();
        Assert.Empty(stdout); Assert.Equal(data.Output.Stderr.Prefix.ToArray(), stderr);
        Assert.Equal(data.Output.Stderr.ReceivedBytes, raw.RootElement.GetProperty("stderr_bytes").GetInt64());
        LinuxN07FailureSettlement.RequirePrecleanupPrefix(stderr);
        using var facts = JsonDocument.Parse(settlement.Bytes);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stderr)),
            facts.RootElement.GetProperty("pumps").GetProperty("stderr").GetProperty("sha256").GetString());
        Assert.False(raw.RootElement.GetProperty("native_authority").GetBoolean());
    }

    [Fact]
    public void PrivateRawCanaryIsPreservedOnlyInDecodedPrivateBytes()
    {
        var settlement = Encode(Valid());
        var encoded = settlement.SerializePrivateStreams();
        Assert.DoesNotContain("private-fatal-text-canary", Encoding.UTF8.GetString(encoded));
        using var raw = JsonDocument.Parse(encoded);
        Assert.Contains("private-fatal-text-canary", Encoding.UTF8.GetString(raw.RootElement.GetProperty("stderr_base64").GetBytesFromBase64()));
        Assert.DoesNotContain("private-fatal-text-canary", Encoding.UTF8.GetString(settlement.Bytes));
    }

    [Fact]
    public void ReturnedRawBytesCannotMutateOriginalSnapshot()
    {
        var settlement = Encode(Valid());
        var first = settlement.SerializePrivateStreams(); var expected = first.ToArray();
        Array.Fill(first, (byte)0);
        Assert.Equal(expected, settlement.SerializePrivateStreams());
    }

    [Fact]
    public void Complete64KiBIncludingNonTextBytesStaysWithin96KiBFrame()
    {
        var data = WithFullStderr(LinuxN07FailureSettlement.MaximumPrivateStderrBytes, 0xfb);
        var encoded = Encode(data).SerializePrivateStreams();
        Assert.InRange(encoded.Length + 1, 1, LinuxN07FailureSettlement.MaximumPrivateRawFrameBytes);
        using var raw = JsonDocument.Parse(encoded);
        Assert.Equal(data.Output.Stderr.Prefix.ToArray(), raw.RootElement.GetProperty("stderr_base64").GetBytesFromBase64());
        Assert.Equal(65536L, raw.RootElement.GetProperty("stderr_bytes").GetInt64());
    }

    [Fact]
    public void Above64KiBIsRejectedWithoutTruncationOrProjection()
    {
        Assert.Throws<InvalidOperationException>(() => Encode(WithFullStderr(LinuxN07FailureSettlement.MaximumPrivateStderrBytes + 1)));
    }

    [Fact]
    public void MissingOrDuplicateActualFrameCannotCreateRawSnapshot()
    {
        var data = Valid();
        foreach (string value in new[] { "{}\n", SupervisionN07OwnedCheckpointTests.Frame + "\n" + SupervisionN07OwnedCheckpointTests.Frame })
        {
            var bytes = ImmutableArray.CreateRange(Encoding.UTF8.GetBytes(value));
            Assert.Throws<InvalidOperationException>(() => Encode(data with { Output = new(data.Output.Stdout,
                new(bytes.Length, bytes, true, SupervisionOutputFailure.None), bytes.Length, 8192,
                SupervisionOutputFailure.None, false, false) }));
        }
    }

    [Fact]
    public void CanceledOriginalTokenRejectsRawEncodingWithoutNewClock()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => Encode(Valid()).SerializePrivateStreams(cancelled.Token));
    }

    [Fact]
    public void ClosedRootLineRemainsBoundedAndNeverCreditsFailedServer()
    {
        var bytes = LinuxN07FailureEmitter.Encode(Encode(Valid()), new string('a', 64), TaskStatus.Faulted);
        Assert.InRange(bytes.Length, 1, LinuxN07FailureEmitter.MaximumJsonBytes);
        using var root = JsonDocument.Parse(bytes);
        Assert.False(root.RootElement.GetProperty("server").GetProperty("completed_successfully").GetBoolean());
        Assert.False(root.RootElement.GetProperty("native_authority").GetBoolean());
        Assert.False(root.RootElement.GetProperty("native_acceptance").GetBoolean());
        Assert.DoesNotContain("private-fatal-text-canary", Encoding.UTF8.GetString(bytes));
    }
}
