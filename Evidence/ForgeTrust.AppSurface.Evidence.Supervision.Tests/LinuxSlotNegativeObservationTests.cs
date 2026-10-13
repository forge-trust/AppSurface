using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Detached codecs only; fixture metadata and stream bytes create no Linux owner or admission.</summary>
public sealed class LinuxSlotNegativeObservationTests
{
    private const string Allocation = "{\"schema\":\"evidence-allocation-failure-v1\",\"phase\":\"Allocation\",\"operation\":\"CreateSlot\",\"stageOutcome\":\"Failed\",\"terminalCode\":\"StageFailed\",\"errorClass\":\"Io\",\"nativeErrno\":17}";
    private static readonly LinuxProtectedMetadata Parent = new(8, 1, 100, 65010, 65011, 0x41c0, 2, 64, 0, 0, 0, 0);
    private static readonly LinuxProtectedMetadata Slot = Parent with { Inode = 101 };
    private static readonly LinuxProtectedMetadata Sentinel = Parent with { Inode = 102, Mode = 0x8180, LinkCount = 1, Length = 28 };
    private static readonly LinuxProtectedMetadata Target = Parent with { Inode = 103, Uid = 65012, Gid = 65014, Mode = 0x41c8 };
    private static readonly LinuxProtectedMetadata Link = Parent with { Inode = 104, Uid = 0, Gid = 0, Mode = 0xa1ff, LinkCount = 1, Length = 14 };

    [Theory]
    [InlineData("N05", "setup")]
    [InlineData("N05", "post_join")]
    [InlineData("N06", "setup")]
    [InlineData("N06", "post_join")]
    public void FixedInspectionIsBoundedAndCarriesObservedObjectsOnly(string caseId, string phase)
    {
        var bytes = Inspection(caseId, phase);
        Assert.InRange(bytes.Length, 1, 1024);
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(phase, json.RootElement.GetProperty("phase").GetString());
        Assert.Equal(100UL, json.RootElement.GetProperty("parent").GetProperty("inode").GetUInt64());
        Assert.False(json.RootElement.TryGetProperty("native_authority", out _));
        Assert.False(json.RootElement.TryGetProperty("native_acceptance", out _));
    }

    [Theory]
    [InlineData("N01", "setup")]
    [InlineData("N05", "changed")]
    [InlineData("n05", "setup")]
    public void InspectionRejectsOpenCaseOrPhase(string caseId, string phase) =>
        Assert.Throws<InvalidOperationException>(() => Inspection(caseId, phase));

    [Fact]
    public void InspectionRejectsMissingIdentityPolicyAndDigest()
    {
        foreach (var parent in new[] { Parent with { Inode = 0 }, Parent with { Uid = 0 }, Parent with { Gid = 0 }, Parent with { Mode = 0x41ff } })
            Assert.Throws<InvalidOperationException>(() => LinuxSlotNegativeObservation.EncodeInspection("N05", "setup", parent, Slot, Sentinel, new('a', 64)));
        foreach (var sentinel in new[] { Sentinel with { LinkCount = 2 }, Sentinel with { Length = 0 }, Sentinel with { Mode = 0x8184 }, Sentinel with { Uid = 0 } })
            Assert.Throws<InvalidOperationException>(() => LinuxSlotNegativeObservation.EncodeInspection("N05", "setup", Parent, Slot, sentinel, new('a', 64)));
        Assert.Throws<InvalidOperationException>(() => LinuxSlotNegativeObservation.EncodeInspection("N06", "setup", Parent, Link, Sentinel with { Uid = 0, Gid = 0 }, "canary"));
        Assert.Throws<InvalidOperationException>(() => LinuxSlotNegativeObservation.EncodeInspection("N05", "setup", Parent, Slot, Sentinel, new('a', 64), Target));
        Assert.Throws<InvalidOperationException>(() => LinuxSlotNegativeObservation.EncodeInspection("N06", "setup", Parent, Link with { Length = 13 }, Sentinel with { Uid = 0, Gid = 0 }, new('a', 64), Target));
    }

    [Theory]
    [InlineData("N05")]
    [InlineData("N06")]
    public void JoinedOutputPreservesExactRawFailureWithoutAuthority(string caseId)
    {
        var result = LinuxSlotNegativeObservation.EncodeJoinedOutput(caseId, Output());
        using var json = JsonDocument.Parse(result.Projection);
        Assert.Equal("CreateSlot", json.RootElement.GetProperty("allocation").GetProperty("operation").GetString());
        Assert.False(json.RootElement.GetProperty("native_authority").GetBoolean());
        using var raw = JsonDocument.Parse(result.Raw);
        Assert.Equal(Encoding.UTF8.GetBytes(Allocation + "\n" + LinuxSlotNegativeObservation.WorkerTerminal),
            Convert.FromBase64String(raw.RootElement.GetProperty("stderr_base64").GetString()!));
    }

    [Theory]
    [InlineData("missing-eof")]
    [InlineData("pump-error")]
    [InlineData("count-mismatch")]
    [InlineData("quota")]
    [InlineData("signal-error")]
    [InlineData("truncated")]
    [InlineData("nonempty-stdout")]
    public void IncompleteOrFailedStreamIsNeverCredited(string defect)
    {
        var output = Output();
        output = defect switch
        {
            "missing-eof" => output with { Stderr = output.Stderr with { EndOfStream = false } },
            "pump-error" => output with { Failure = SupervisionOutputFailure.ReadFailed },
            "count-mismatch" => output with { ReceivedBytes = output.ReceivedBytes + 1 },
            "quota" => output with { QuotaExceeded = true },
            "signal-error" => output with { StopSignalFailed = true },
            "truncated" => output with { Stderr = output.Stderr with { Prefix = output.Stderr.Prefix.RemoveAt(0) } },
            _ => output with { Stdout = new(1, [1], true, SupervisionOutputFailure.None), ReceivedBytes = output.ReceivedBytes + 1 },
        };
        Assert.Throws<InvalidOperationException>(() => LinuxSlotNegativeObservation.EncodeJoinedOutput("N05", output));
    }

    [Theory]
    [InlineData("wrong-operation")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("wrong-terminal")]
    [InlineData("malformed")]
    [InlineData("wrong-errno")]
    public void MalformedOrDifferentFailureCannotSubstituteForAllocatorRejection(string defect)
    {
        var allocation = defect switch
        {
            "wrong-operation" => Allocation.Replace("CreateSlot", "OpenSlot", StringComparison.Ordinal),
            "duplicate" => Allocation.Replace("{", "{\"schema\":\"canary\",", StringComparison.Ordinal),
            "extra" => Allocation.Replace("{", "{\"canary\":true,", StringComparison.Ordinal),
            "malformed" => "canary-private-path",
            "wrong-errno" => Allocation.Replace("17", "0", StringComparison.Ordinal),
            _ => Allocation,
        };
        var terminal = defect == "wrong-terminal" ? "ASEVD402: canary\n" : LinuxSlotNegativeObservation.WorkerTerminal;
        var error = Assert.Throws<InvalidOperationException>(() => LinuxSlotNegativeObservation.EncodeJoinedOutput("N06", Output(allocation + "\n" + terminal)));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.Message, StringComparison.Ordinal);
    }

    private static byte[] Inspection(string caseId, string phase) =>
        LinuxSlotNegativeObservation.EncodeInspection(caseId, phase, Parent, caseId == "N06" ? Link : Slot,
            caseId == "N06" ? Sentinel with { Uid = 0, Gid = 0 } : Sentinel, new('a', 64), caseId == "N06" ? Target : null);

    private static SupervisionOutputReceipt Output(string? content = null)
    {
        var bytes = Encoding.UTF8.GetBytes(content ?? Allocation + "\n" + LinuxSlotNegativeObservation.WorkerTerminal);
        return new(new(0, [], true, SupervisionOutputFailure.None),
            new(bytes.Length, bytes.ToImmutableArray(), true, SupervisionOutputFailure.None), bytes.Length,
            65536, SupervisionOutputFailure.None, false, false);
    }
}
