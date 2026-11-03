using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Private diagnostic data and original collector controls; no Linux or custody authority is fabricated.</summary>
public sealed class LinuxJoinedWorkerOutputDiagnosticTests
{
    private static readonly Guid Generation = Guid.Parse("a74a9f49-581d-49a4-9565-33689a360b15");

    [Fact]
    public void MissingReceiptRemainsUnavailableWithoutInventedEofOrCounts()
    {
        using var parsed = JsonDocument.Parse(LinuxJoinedWorkerOutputDiagnostic.EncodeDetached(Generation, null));
        var root = parsed.RootElement;
        Assert.Equal(14, root.EnumerateObject().Count());
        Assert.Equal("unavailable", root.GetProperty("status").GetString());
        foreach (var name in new[] { "received_bytes", "received_byte_limit", "stdout", "stderr", "quota_exceeded", "stop_signal_failed" })
            Assert.Equal(JsonValueKind.Null, root.GetProperty(name).ValueKind);
        Assert.True(root.GetProperty("physical_settlement_unknown").GetBoolean());
        Assert.False(root.GetProperty("native_authority").GetBoolean());
        Assert.False(root.GetProperty("native_acceptance").GetBoolean());
    }

    [Fact]
    public async Task ActualOriginalCollectorBytesAreCopiedOnlyAfterBothPumpsFinish()
    {
        var raw = Encoding.UTF8.GetBytes("worker-private-canary\n");
        using var stdout = new MemoryStream(new byte[] { 1, 2, 3 });
        using var stderr = new MemoryStream(raw);
        var receipt = await new SupervisionOutputCollector().CollectAsync(stdout, stderr, default);
        var bytes = LinuxJoinedWorkerOutputDiagnostic.EncodeDetached(Generation, receipt);
        using var parsed = JsonDocument.Parse(bytes);
        var root = parsed.RootElement;
        Assert.Equal("joined", root.GetProperty("status").GetString());
        Assert.Equal(raw.Length + 3, root.GetProperty("received_bytes").GetInt64());
        var error = root.GetProperty("stderr");
        Assert.Equal(raw, Convert.FromBase64String(error.GetProperty("prefix_base64").GetString()!));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(raw)), error.GetProperty("prefix_sha256").GetString());
        Assert.True(error.GetProperty("eof").GetBoolean());
        Assert.False(error.GetProperty("prefix_truncated").GetBoolean());
        Assert.True(root.GetProperty("stdout").GetProperty("eof").GetBoolean());
        Assert.DoesNotContain("worker-private-canary", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.True(root.GetProperty("physical_settlement_unknown").GetBoolean());
        Assert.False(root.GetProperty("native_acceptance").GetBoolean());
        Assert.False(root.GetProperty("native_authority").GetBoolean());
    }

    [Fact]
    public async Task ActualReadFailureIsRetainedWithoutExceptionTextOrInventedEof()
    {
        using var stdout = new MemoryStream();
        using var stderr = new ThrowingReadStream();
        var receipt = await new SupervisionOutputCollector().CollectAsync(stdout, stderr, default);
        var bytes = LinuxJoinedWorkerOutputDiagnostic.EncodeDetached(Generation, receipt);
        using var parsed = JsonDocument.Parse(bytes);
        Assert.Equal("read-failed", parsed.RootElement.GetProperty("failure").GetString());
        Assert.False(parsed.RootElement.GetProperty("stderr").GetProperty("eof").GetBoolean());
        Assert.DoesNotContain("read-error-private-canary", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.False(parsed.RootElement.GetProperty("native_authority").GetBoolean());
    }

    [Theory]
    [InlineData(0, "none")]
    [InlineData(1, "quota-exceeded")]
    [InlineData(2, "read-failed")]
    [InlineData(3, "cancelled")]
    public void DetachedClosedFailuresStayDiagnosticOnly(int failureValue, string name)
    {
        var failure = (SupervisionOutputFailure)failureValue;
        var receipt = Receipt(new byte[] { 3 }, failure);
        using var parsed = JsonDocument.Parse(LinuxJoinedWorkerOutputDiagnostic.EncodeDetached(Generation, receipt));
        Assert.Equal(name, parsed.RootElement.GetProperty("failure").GetString());
        Assert.Equal(name, parsed.RootElement.GetProperty("stderr").GetProperty("failure").GetString());
        Assert.False(parsed.RootElement.GetProperty("native_acceptance").GetBoolean());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65_536)]
    [InlineData(65_537)]
    public void PrivatePrefixHasExactBoundAndActualReceivedCounts(int size)
    {
        var raw = Enumerable.Repeat((byte)255, size).ToArray();
        var bytes = LinuxJoinedWorkerOutputDiagnostic.EncodeDetached(Generation, Receipt(raw));
        Assert.InRange(bytes.Length, 1, LinuxJoinedWorkerOutputDiagnostic.MaximumJsonBytes);
        using var parsed = JsonDocument.Parse(bytes);
        var error = parsed.RootElement.GetProperty("stderr");
        var retained = Math.Min(size, LinuxJoinedWorkerOutputDiagnostic.MaximumPrefixBytes);
        Assert.Equal(size, error.GetProperty("received_bytes").GetInt64());
        Assert.Equal(retained, error.GetProperty("retained_bytes").GetInt32());
        Assert.Equal(raw.AsSpan(0, retained).ToArray(), Convert.FromBase64String(error.GetProperty("prefix_base64").GetString()!));
        Assert.Equal(size > retained, error.GetProperty("prefix_truncated").GetBoolean());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void InconsistentDetachedDataCannotBecomeDiagnosticReceipt(int mutation)
    {
        var value = Receipt(new byte[] { 1 });
        value = mutation switch
        {
            0 => value with { ReceivedBytes = 0 },
            1 => value with { ReceivedByteLimit = 0 },
            2 => value with { Failure = (SupervisionOutputFailure)999 },
            3 => value with { Stderr = value.Stderr with { ReceivedBytes = -1 } },
            4 => value with { Stderr = value.Stderr with { Prefix = default } },
            _ => value with { Stderr = value.Stderr with { Failure = (SupervisionOutputFailure)999 } }
        };
        var error = Assert.Throws<EvidenceAdmissionException>(() => LinuxJoinedWorkerOutputDiagnostic.EncodeDetached(Generation, value));
        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void EmptyGenerationIsRejectedBeforeEncoding()
    {
        Assert.Equal("ASEVD410", Assert.Throws<EvidenceAdmissionException>(
            () => LinuxJoinedWorkerOutputDiagnostic.EncodeDetached(Guid.Empty, null)).Code);
    }

    private static SupervisionOutputReceipt Receipt(byte[] raw, SupervisionOutputFailure failure = SupervisionOutputFailure.None) =>
        new(new(0, ImmutableArray<byte>.Empty, true, SupervisionOutputFailure.None),
            new(raw.Length, ImmutableArray.CreateRange(raw), failure == SupervisionOutputFailure.None, failure),
            raw.Length, 16 * 1024 * 1024, failure,
            failure == SupervisionOutputFailure.QuotaExceeded, false);

    private sealed class ThrowingReadStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("read-error-private-canary"));
    }
}
