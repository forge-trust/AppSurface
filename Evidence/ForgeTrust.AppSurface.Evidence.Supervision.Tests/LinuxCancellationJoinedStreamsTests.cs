using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Detached codec controls only; no process, kernel owner, signal, custody or native credit is created.</summary>
public sealed class LinuxCancellationJoinedStreamsTests
{
    private static readonly Guid Generation = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static SupervisionOutputStreamReceipt Stream(byte[] bytes) =>
        new(bytes.Length, ImmutableArray.CreateRange(bytes), true, SupervisionOutputFailure.None);

    private static SupervisionOutputReceipt Pair(byte[]? stdout = null, byte[]? stderr = null)
    {
        var first = Stream(stdout ?? []);
        var second = Stream(stderr ?? Encoding.UTF8.GetBytes("private-output-canary\n"));
        return new(first, second, first.ReceivedBytes + second.ReceivedBytes,
            16 * 1024 * 1024, SupervisionOutputFailure.None, false, false);
    }

    private static byte[] Encode(SupervisionOutputReceipt value, CancellationToken token = default) =>
        LinuxNegativeKernelObservation.EncodeJoinedStreamsDetached(Generation, value, token);

    private static void Reject(SupervisionOutputReceipt value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Encode(value));
        Assert.Equal("ASEVD410: Negative kernel observation data rejected.", error.Message);
        Assert.Null(error.InnerException);
    }

    /// <summary>Exact original binary bytes round-trip, with closed fields and independently recomputed hashes.</summary>
    [Fact]
    public void CompletePairEncodesOnlyClosedFieldsAndExactBase64Bytes()
    {
        var bytes = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        var pair = Pair(bytes);
        var encoded = Encode(pair);
        using var json = JsonDocument.Parse(encoded);
        var root = json.RootElement;
        Assert.Equal(9, root.EnumerateObject().Count());
        Assert.Equal("issue779-cancellation-joined-streams-v1", root.GetProperty("schema").GetString());
        Assert.Equal(Generation.ToString("N"), root.GetProperty("generation").GetString());
        Assert.True(root.GetProperty("observation_only").GetBoolean());
        Assert.False(root.GetProperty("native_authority").GetBoolean());
        Assert.False(root.GetProperty("native_acceptance").GetBoolean());
        Assert.Equal(pair.ReceivedBytes, root.GetProperty("received_bytes").GetInt64());
        Assert.Equal(pair.ReceivedByteLimit, root.GetProperty("received_byte_limit").GetInt64());
        foreach (var item in new[] { ("stdout", pair.Stdout), ("stderr", pair.Stderr) })
        {
            var stream = root.GetProperty(item.Item1);
            Assert.Equal(7, stream.EnumerateObject().Count());
            Assert.Equal(item.Item2.Prefix.ToArray(), Convert.FromBase64String(stream.GetProperty("base64").GetString()!));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(item.Item2.Prefix.AsSpan())),
                stream.GetProperty("sha256").GetString());
            Assert.Equal(item.Item2.ReceivedBytes, stream.GetProperty("received_bytes").GetInt64());
            Assert.Equal(item.Item2.Prefix.Length, stream.GetProperty("retained_bytes").GetInt32());
            Assert.Equal(0, stream.GetProperty("discarded_bytes").GetInt64());
            Assert.True(stream.GetProperty("eof").GetBoolean());
            Assert.Equal("None", stream.GetProperty("failure").GetString());
        }
        Assert.DoesNotContain("private-output-canary", Encoding.UTF8.GetString(encoded));
        Assert.InRange(encoded.Length + 1, 1, LinuxNegativeKernelObservation.MaximumJoinedStreamLineBytes);
    }

    /// <summary>Worst HTML-sensitive base64 alphabets fit the complete 64 KiB pair and 96 KiB line bound.</summary>
    [Fact]
    public void ExactMaximumWithPlusSlashAndPaddingFitsWithoutTruncation()
    {
        var first = Enumerable.Repeat((byte)0xfb, 32767).ToArray();
        var second = Enumerable.Repeat((byte)0xff, 32769).ToArray();
        var encoded = Encode(Pair(first, second));
        Assert.InRange(encoded.Length + 1, 1, LinuxNegativeKernelObservation.MaximumJoinedStreamLineBytes);
        using var json = JsonDocument.Parse(encoded);
        Assert.Equal(first, Convert.FromBase64String(json.RootElement.GetProperty("stdout").GetProperty("base64").GetString()!));
        Assert.Equal(second, Convert.FromBase64String(json.RootElement.GetProperty("stderr").GetProperty("base64").GetString()!));
    }

    /// <summary>A complete zero-byte pair is encodable data, never a native cancellation result.</summary>
    [Fact]
    public void EmptyCompletePairDoesNotInventOutput()
    {
        using var json = JsonDocument.Parse(Encode(Pair([], [])));
        Assert.Equal(0, json.RootElement.GetProperty("received_bytes").GetInt64());
        Assert.Equal(string.Empty, json.RootElement.GetProperty("stdout").GetProperty("base64").GetString());
    }

    /// <summary>Either missing EOF rejects; a claimed task completion is not substituted.</summary>
    /// <param name="stdout">Which detached EOF is absent.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingEitherEofRejects(bool stdout)
    {
        var pair = Pair();
        Reject(stdout ? pair with { Stdout = pair.Stdout with { EndOfStream = false } }
            : pair with { Stderr = pair.Stderr with { EndOfStream = false } });
    }

    /// <summary>Neither individual pump failure can produce a qualifying raw export.</summary>
    /// <param name="stdout">Which detached pump reports a failure.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PumpFailureRejects(bool stdout)
    {
        var pair = Pair();
        Reject(stdout ? pair with { Stdout = pair.Stdout with { Failure = SupervisionOutputFailure.ReadFailed } }
            : pair with { Stderr = pair.Stderr with { Failure = SupervisionOutputFailure.Cancelled } });
    }

    /// <summary>Original shared failure, quota and failed stop signalling remain failures.</summary>
    /// <param name="field">Fixed detached branch, not a runtime selector.</param>
    [Theory]
    [InlineData("failure")]
    [InlineData("quota")]
    [InlineData("stop")]
    public void SharedFailureRejects(string field)
    {
        var pair = Pair();
        Reject(field switch
        {
            "failure" => pair with { Failure = SupervisionOutputFailure.ReadFailed },
            "quota" => pair with { QuotaExceeded = true },
            _ => pair with { StopSignalFailed = true },
        });
    }

    /// <summary>Discarded original bytes cannot be presented as a complete captured prefix.</summary>
    [Fact]
    public void DiscardedPrefixRejects()
    {
        var pair = Pair();
        Reject(pair with { Stderr = pair.Stderr with { ReceivedBytes = pair.Stderr.ReceivedBytes + 1 },
            ReceivedBytes = pair.ReceivedBytes + 1 });
    }

    /// <summary>Exact pair sum and original protected quota are checked independently.</summary>
    /// <param name="field">Fixed detached accounting branch.</param>
    [Theory]
    [InlineData("count")]
    [InlineData("limit")]
    [InlineData("zero-limit")]
    [InlineData("raised-limit")]
    public void WrongSharedAccountingRejects(string field)
    {
        var pair = Pair();
        Reject(field switch
        {
            "count" => pair with { ReceivedBytes = pair.ReceivedBytes - 1 },
            "limit" => pair with { ReceivedByteLimit = pair.ReceivedBytes - 1 },
            "zero-limit" => pair with { ReceivedByteLimit = 0 },
            _ => pair with { ReceivedByteLimit = 16 * 1024 * 1024 + 1 },
        });
    }

    /// <summary>One byte above the export allowance rejects without truncating otherwise complete streams.</summary>
    [Fact]
    public void OversizedPairRejects()
    {
        Reject(Pair(new byte[LinuxNegativeKernelObservation.MaximumJoinedStreamBytes], [1]));
    }

    /// <summary>Missing output, either stream or initialized prefix cannot be replaced with empty data.</summary>
    [Fact]
    public void MissingReceiptStreamOrPrefixRejects()
    {
        Reject(null!);
        var pair = Pair();
        Reject(pair with { Stdout = null! });
        Reject(pair with { Stderr = null! });
        Reject(pair with { Stdout = pair.Stdout with { Prefix = default } });
    }

    /// <summary>Original cancellation propagates before encoding and is never normalized to a copied receipt.</summary>
    [Fact]
    public void OriginalCancelledTokenPreventsEncoding()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() => Encode(Pair(), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    /// <summary>A generation is required, but choosing one in detached tests grants no native owner.</summary>
    [Fact]
    public void EmptyGenerationRejects()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            LinuxNegativeKernelObservation.EncodeJoinedStreamsDetached(Guid.Empty, Pair()));
        Assert.Null(error.InnerException);
    }

    /// <summary>Changing a returned byte array cannot mutate original immutable receipt data.</summary>
    [Fact]
    public void ExportCannotMutateOriginalReceipt()
    {
        var pair = Pair();
        var original = pair.Stderr.Prefix.ToArray();
        var first = Encode(pair);
        var expected = first.ToArray();
        Array.Fill(first, (byte)0);
        Assert.Equal(expected, Encode(pair));
        Assert.Equal(original, pair.Stderr.Prefix.ToArray());
    }
}
