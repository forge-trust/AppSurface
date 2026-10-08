using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Portable actual-collector and detached-parser controls, with no native owner or acceptance capability.</summary>
public sealed class N05JoinedWorkerOutputTests
{
    private const string Allocation = "{\"schema\":\"evidence-allocation-failure-v1\",\"phase\":\"Allocation\",\"operation\":\"CreateSlot\",\"stageOutcome\":\"Failed\",\"terminalCode\":\"StageFailed\",\"errorClass\":\"Io\",\"nativeErrno\":17}";
    private static string Complete(string allocation = Allocation) => allocation + "\n" + N05JoinedWorkerOutput.TerminalLine + "\n";

    private static async Task<SupervisionOutputReceipt> Joined(string stderr, string stdout = "")
    {
        using var output = new MemoryStream(Encoding.UTF8.GetBytes(stdout));
        using var error = new MemoryStream(Encoding.UTF8.GetBytes(stderr));
        return await new SupervisionOutputCollector().CollectAsync(output, error, default);
    }

    /// <summary>Uses both real MemoryStream EOFs to verify a closed, explicitly non-authoritative projection.</summary>
    [Fact]
    public async Task ActualTwoEofsProjectOnlyClosedWorkerReportedData()
    {
        var value = N05JoinedWorkerOutput.Parse(await Joined(Complete()));
        Assert.Equal(17, value.WorkerReportedErrno);
        using var document = JsonDocument.Parse(N05JoinedWorkerOutput.Serialize(value));
        Assert.False(document.RootElement.GetProperty("native_authority").GetBoolean());
        Assert.Equal("ASEVD409", document.RootElement.GetProperty("terminal_diagnostic").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("stdout_bytes").GetInt32());
        Assert.True(Encoding.UTF8.GetByteCount(N05JoinedWorkerOutput.Serialize(value)) + 1 <= 1024);
    }

    /// <summary>Retains an absent worker errno as null without inventing a numeric cause.</summary>
    [Fact]
    public async Task MissingObservedErrnoRemainsNullNotManufacturedZero()
    {
        var value = N05JoinedWorkerOutput.Parse(await Joined(Complete(Allocation.Replace(":17", ":null"))));
        Assert.Null(value.WorkerReportedErrno);
    }

    /// <summary>Exercises incorrect actual collected bytes through the same parser used by the fixed native caller.</summary>
    /// <param name="control">Closed test data mutation, never a runtime case selector.</param>
    [Theory]
    [InlineData("duplicate")][InlineData("extra")][InlineData("phase")][InlineData("operation")]
    [InlineData("outcome")][InlineData("terminal-code")][InlineData("error-class")][InlineData("errno-string")]
    [InlineData("errno-range")][InlineData("malformed")][InlineData("terminal")][InlineData("extra-line")]
    [InlineData("missing-line")][InlineData("missing-lf")][InlineData("oversize")][InlineData("canary")]
    public async Task ActualCollectorBytesRejectIncorrectClosedShape(string control)
    {
        var allocation = Allocation;
        var text = Complete();
        switch (control)
        {
            case "duplicate": allocation = Allocation.Replace("{", "{\"phase\":\"Allocation\","); break;
            case "extra": allocation = Allocation.Replace("{", "{\"extra\":true,"); break;
            case "phase": allocation = Allocation.Replace("Allocation\"", "Activation\""); break;
            case "operation": allocation = Allocation.Replace("CreateSlot", "Completed"); break;
            case "outcome": allocation = Allocation.Replace(":\"Failed\"", ":\"Passed\""); break;
            case "terminal-code": allocation = Allocation.Replace("StageFailed", "None"); break;
            case "error-class": allocation = Allocation.Replace("Io", "Unknown"); break;
            case "errno-string": allocation = Allocation.Replace(":17", ":\"17\""); break;
            case "errno-range": allocation = Allocation.Replace(":17", ":4096"); break;
            case "malformed": allocation = "{"; break;
            case "terminal": text = Complete().Replace("ASEVD409:", "ASEVD402:"); break;
            case "extra-line": text = Complete() + "extra\n"; break;
            case "missing-line": text = Allocation + "\n"; break;
            case "missing-lf": text = Complete().TrimEnd('\n'); break;
            case "oversize": text = new string('x', N05JoinedWorkerOutput.MaximumBytes + 1); break;
            case "canary": allocation = Allocation.Replace("Io", "CANARY_SECRET_MUST_NOT_ESCAPE"); break;
        }
        if (allocation != Allocation) text = Complete(allocation);
        var joined = await Joined(text);
        var error = Assert.Throws<InvalidOperationException>(() => N05JoinedWorkerOutput.Parse(joined));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("CANARY", error.Message);
    }

    /// <summary>Rejects detached inconsistent receipt data and unexpected actual stdout without constructing an owner.</summary>
    [Fact]
    public async Task PartialRetentionMissingEofAndPumpFaultNeverPass()
    {
        var joined = await Joined(Complete());
        var errors = new[]
        {
            joined with { Stderr = joined.Stderr with { ReceivedBytes = joined.Stderr.ReceivedBytes + 1 }, ReceivedBytes = joined.ReceivedBytes + 1 },
            joined with { Stderr = joined.Stderr with { EndOfStream = false } },
            joined with { Failure = SupervisionOutputFailure.ReadFailed },
            joined with { QuotaExceeded = true },
            joined with { StopSignalFailed = true },
            joined with { Stderr = joined.Stderr with { Prefix = default } },
        };
        foreach (var receipt in errors) Assert.Throws<InvalidOperationException>(() => N05JoinedWorkerOutput.Parse(receipt));
        Assert.Throws<InvalidOperationException>(() => N05JoinedWorkerOutput.Parse(null));
        var noisy = await Joined(Complete(), "unexpected");
        Assert.Throws<InvalidOperationException>(() => N05JoinedWorkerOutput.Parse(noisy));
    }

    /// <summary>Rejects invalid bytes and preserves the original canceled token rather than normalizing it.</summary>
    [Fact]
    public async Task InvalidUtf8AndOriginalCancellationRemainFailures()
    {
        var joined = await Joined(Complete());
        var bytes = joined.Stderr.Prefix.ToArray(); bytes[0] = 0xff;
        Assert.Throws<InvalidOperationException>(() => N05JoinedWorkerOutput.Parse(joined with
        { Stderr = joined.Stderr with { Prefix = ImmutableArray.CreateRange(bytes) } }));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => N05JoinedWorkerOutput.Parse(joined, stop.Token));
        Assert.Throws<OperationCanceledException>(() => N05JoinedWorkerOutput.Serialize(new(17, 1), stop.Token));
    }
    /// <summary>Round-trips complete actual collector bytes without granting native provenance.</summary>
    [Fact]
    public async Task PrivateStreamsContainExactFullFailureBytesOnly()
    {
        var receipt = await Joined(Complete());
        using var document = JsonDocument.Parse(N05JoinedWorkerOutput.SerializePrivateStreams(receipt));
        var root = document.RootElement;
        Assert.Equal(6, root.EnumerateObject().Count());
        Assert.Equal("issue779-n05-joined-worker-raw-v1", root.GetProperty("schema").GetString());
        Assert.Empty(Convert.FromBase64String(root.GetProperty("stdout_base64").GetString()!));
        Assert.Equal(receipt.Stderr.Prefix.ToArray(), Convert.FromBase64String(root.GetProperty("stderr_base64").GetString()!));
        Assert.Equal(receipt.Stderr.ReceivedBytes, root.GetProperty("stderr_bytes").GetInt64());
        Assert.False(root.GetProperty("native_authority").GetBoolean());
        Assert.InRange(Encoding.UTF8.GetByteCount(N05JoinedWorkerOutput.SerializePrivateStreams(receipt)) + 1, 1, 4096);
    }

    /// <summary>Incomplete or unexpected real collected streams cannot become private evidence; original cancellation propagates.</summary>
    [Fact]
    public async Task PrivateStreamsRejectUnjoinedOutputAndOriginalCancellation()
    {
        var receipt = await Joined(Complete());
        Assert.Throws<InvalidOperationException>(() => N05JoinedWorkerOutput.SerializePrivateStreams(receipt with
        { Stderr = receipt.Stderr with { EndOfStream = false } }));
        var noisy = await Joined(Complete(), "private-canary");
        var error = Assert.Throws<InvalidOperationException>(() => N05JoinedWorkerOutput.SerializePrivateStreams(noisy));
        Assert.DoesNotContain("private-canary", error.Message);
        Assert.Null(error.InnerException);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = Assert.Throws<OperationCanceledException>(() => N05JoinedWorkerOutput.SerializePrivateStreams(receipt, cancellation.Token));
        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
    }

}
