#if EVIDENCE_PRIVATE_N16
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Pure N16 progress-record data controls; these tests acquire no native owner or execution lease.</summary>
public sealed class LinuxN16ProgressDiagnosticTests
{
    private static LinuxN16ProgressDiagnostic.Snapshot Snapshot(LinuxN16ProgressDiagnostic.Milestone milestone,
        long? elapsedMilliseconds = null, bool progress = false) => new(milestone, elapsedMilliseconds,
        progress, progress, progress, progress, progress, progress, progress, progress, progress, progress, null);

    /// <summary>An unobserved milestone has a null duration and carries no authority.</summary>
    [Fact]
    public void UnknownMilestoneUsesNullDurationAndFixedMetadataOnly()
    {
        var bytes = LinuxN16ProgressDiagnostic.EncodeDetached(Snapshot(LinuxN16ProgressDiagnostic.Milestone.Unknown));
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;

        Assert.Equal("issue779-n16-progress-diagnostic-v2", root.GetProperty("schema").GetString());
        Assert.Equal("Unknown", root.GetProperty("last_milestone").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("last_milestone_elapsed_ms").ValueKind);
        Assert.False(root.GetProperty("native_authority").GetBoolean());
        Assert.False(root.GetProperty("native_acceptance").GetBoolean());
        Assert.Equal(21, root.EnumerateObject().Count());
        Assert.InRange(bytes.Length, 1, LinuxN16ProgressDiagnostic.MaximumJsonBytes);
        Assert.DoesNotContain("canary", Encoding.UTF8.GetString(bytes));
    }

    /// <summary>Observed flags and a fixed closed milestone survive encoding without adding caller fields.</summary>
    [Fact]
    public void ObservedSnapshotContainsOnlyClosedMilestoneFlags()
    {
        var bytes = LinuxN16ProgressDiagnostic.EncodeDetached(
            Snapshot(LinuxN16ProgressDiagnostic.Milestone.ExitCommitted, 1_234, progress: true));
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;

        Assert.Equal("ExitCommitted", root.GetProperty("last_milestone").GetString());
        Assert.Equal(1_234, root.GetProperty("last_milestone_elapsed_ms").GetInt64());
        Assert.True(root.GetProperty("accepted_write_committed").GetBoolean());
        Assert.True(root.GetProperty("wait_write_committed").GetBoolean());
        Assert.True(root.GetProperty("exit_committed").GetBoolean());
        Assert.Equal(JsonValueKind.False, root.GetProperty("native_authority").ValueKind);
        Assert.Equal(JsonValueKind.False, root.GetProperty("native_acceptance").ValueKind);
        Assert.DoesNotContain("\"path\"", Encoding.UTF8.GetString(bytes));
        Assert.InRange(bytes.Length, 1, LinuxN16ProgressDiagnostic.MaximumJsonBytes);
    }

    /// <summary>Elapsed time uses the supplied monotonic frequency and rounds down to whole milliseconds.</summary>
    [Fact]
    public void MonotonicElapsedMillisecondsUsesBoundedTickArithmetic()
    {
        Assert.Equal(2_500, LinuxN16ProgressDiagnostic.ElapsedMilliseconds(100, 125, 10));
        Assert.Equal(0, LinuxN16ProgressDiagnostic.ElapsedMilliseconds(100, 100, 10));
    }

    /// <summary>Reversed or invalid monotonic samples cannot be encoded as a plausible duration.</summary>
    [Theory]
    [InlineData(100, 99, 10)]
    [InlineData(100, 100, 0)]
    [InlineData(0, 100, 10)]
    public void InvalidMonotonicSamplesReject(long start, long end, long frequency) =>
        Assert.Throws<InvalidOperationException>(() =>
            LinuxN16ProgressDiagnostic.ElapsedMilliseconds(start, end, frequency));

    /// <summary>Invalid closed stages and inconsistent unknown/duration pairs fail closed.</summary>
    [Fact]
    public void InvalidStageAndUnknownDurationMismatchReject()
    {
        Assert.Throws<ArgumentNullException>(() =>
            LinuxN16ProgressDiagnostic.EncodeDetached(null!));
        Assert.Throws<InvalidOperationException>(() => LinuxN16ProgressDiagnostic.EncodeDetached(
            Snapshot((LinuxN16ProgressDiagnostic.Milestone)int.MaxValue)));
        Assert.Throws<InvalidOperationException>(() => LinuxN16ProgressDiagnostic.EncodeDetached(
            Snapshot(LinuxN16ProgressDiagnostic.Milestone.Unknown, elapsedMilliseconds: 0)));
        Assert.Throws<InvalidOperationException>(() => LinuxN16ProgressDiagnostic.EncodeDetached(
            Snapshot(LinuxN16ProgressDiagnostic.Milestone.BodyEntered)));
    }

    /// <summary>Elapsed values have a fixed upper bound independent of any caller-provided budget.</summary>
    [Fact]
    public void ElapsedValueAboveFixedBoundRejects()
    {
        Assert.Throws<InvalidOperationException>(() => LinuxN16ProgressDiagnostic.EncodeDetached(
            Snapshot(LinuxN16ProgressDiagnostic.Milestone.WorkClaimed,
                LinuxN16ProgressDiagnostic.MaximumElapsedMilliseconds + 1)));
        Assert.Throws<InvalidOperationException>(() => LinuxN16ProgressDiagnostic.ElapsedMilliseconds(
            1, 86_400_001_001, 1_000));
    }

    /// <summary>Only exact ordered client tokens in a fully joined output receipt become copied milestones.</summary>
    [Fact]
    public void ClientProgressRequiresOrderedFixedMarkersAndJoinedEof()
    {
        const string privateCanary = "private-client-stderr-canary";
        var output = JoinedOutput(privateCanary + "\nASEVDN16C:01\nASEVDN16C:02\nASEVDN16C:03\n", eof: true);
        var parsed = LinuxN16ProgressDiagnostic.ParseJoinedClientProgress(output);
        Assert.Equal(LinuxN16ProgressDiagnostic.ClientMilestone.ControlsJoined, parsed.LastMilestone);
        Assert.True(parsed.JoinedStreamComplete);
        Assert.True(parsed.MarkerSequenceValid);
        var fullTrace = LinuxN16ProgressDiagnostic.ParseJoinedClientProgress(JoinedOutput(
            string.Join("\n", Enumerable.Range(1, 7).Select(number => $"ASEVDN16C:{number:D2}")) + "\n", eof: true));
        Assert.Equal(LinuxN16ProgressDiagnostic.ClientMilestone.ExitResponseReceived, fullTrace.LastMilestone);
        Assert.True(fullTrace.MarkerSequenceValid);
        AssertUnknown(JoinedOutput(string.Join("\n", Enumerable.Range(1, 8)
            .Select(number => $"ASEVDN16C:{number:D2}")) + "\n", eof: true));
        var encoded = LinuxN16ProgressDiagnostic.EncodeDetached(
            Snapshot(LinuxN16ProgressDiagnostic.Milestone.Unknown) with { Client = parsed });
        Assert.DoesNotContain(privateCanary, Encoding.UTF8.GetString(encoded));

        AssertUnknown(JoinedOutput("ASEVDN16C:01\nASEVDN16C:03\n", eof: true));
        AssertUnknown(JoinedOutput("ASEVDN16C:01\nASEVDN16C:01\n", eof: true));
        AssertUnknown(JoinedOutput("ASEVDN16C:99\n", eof: true));
        AssertUnknown(JoinedOutput("ASEVDN16C:01canary\n", eof: true));
        AssertUnknown(JoinedOutput("ASEVDN16C:" + new string('9', 97) + "\n", eof: true));
        AssertUnknown(JoinedOutput(string.Concat(Enumerable.Range(1, 11).Select(_ => "ASEVDN16C:01\n")), eof: true));
        AssertUnknown(JoinedOutput(new string('x', 96 * 1024), eof: true));
        var missingEof = LinuxN16ProgressDiagnostic.ParseJoinedClientProgress(JoinedOutput("ASEVDN16C:01\n", eof: false));
        AssertUnknown(JoinedOutput("ASEVDN16C:01\n", eof: false));
        Assert.False(missingEof.JoinedStreamComplete);
        var failedPump = LinuxN16ProgressDiagnostic.ParseJoinedClientProgress(
            JoinedOutput("ASEVDN16C:01\n", eof: true, failure: SupervisionOutputFailure.ReadFailed));
        AssertUnknown(JoinedOutput("ASEVDN16C:01\n", eof: true, failure: SupervisionOutputFailure.ReadFailed));
        Assert.False(failedPump.JoinedStreamComplete);
        var completeWithoutMarkers = LinuxN16ProgressDiagnostic.ParseJoinedClientProgress(JoinedOutput("ordinary stderr\n", eof: true));
        Assert.Equal(LinuxN16ProgressDiagnostic.ClientMilestone.Unknown, completeWithoutMarkers.LastMilestone);
        Assert.True(completeWithoutMarkers.JoinedStreamComplete);
        Assert.False(completeWithoutMarkers.MarkerSequenceValid);
    }

    /// <summary>EXIT markers identify completed client I/O barriers; a terminal failure remains data-only.</summary>
    [Fact]
    public void ClientExitFailureReportsLastCompletedBarrierWithoutChangingAuthority()
    {
        const string canary = "private-exit-diagnostic-canary";
        var trace = string.Join("\n", Enumerable.Range(1, 6).Select(number => $"ASEVDN16C:{number:D2}"))
            + "\nASEVDN16X:01\nASEVDN16X:02\nASEVDN16X:03\nASEVDN16X:04\nASEVDN16E:04:01:00\n";
        var parsed = LinuxN16ProgressDiagnostic.ParseJoinedClientProgress(JoinedOutput(canary + "\n" + trace, eof: true));

        Assert.Equal(LinuxN16ProgressDiagnostic.ClientMilestone.ExitStarted, parsed.LastMilestone);
        Assert.True(parsed.JoinedStreamComplete);
        Assert.True(parsed.MarkerSequenceValid);
        Assert.Equal(LinuxN16ProgressDiagnostic.ClientExitStage.ResponseReadStarted, parsed.ExitStage);
        Assert.Equal(LinuxN16ProgressDiagnostic.ClientExitFailureClass.Cancelled, parsed.ExitFailureClass);
        Assert.Equal(LinuxN16ProgressDiagnostic.ClientExitDiagnosticCode.None, parsed.ExitDiagnosticCode);

        var encoded = LinuxN16ProgressDiagnostic.EncodeDetached(
            Snapshot(LinuxN16ProgressDiagnostic.Milestone.Unknown) with { Client = parsed });
        using var json = JsonDocument.Parse(encoded);
        Assert.Equal("ResponseReadStarted", json.RootElement.GetProperty("client_exit_stage").GetString());
        Assert.Equal("Cancelled", json.RootElement.GetProperty("client_exit_failure_class").GetString());
        Assert.False(json.RootElement.GetProperty("native_authority").GetBoolean());
        Assert.False(json.RootElement.GetProperty("native_acceptance").GetBoolean());
        Assert.DoesNotContain(canary, Encoding.UTF8.GetString(encoded));
        Assert.InRange(encoded.Length, 1, LinuxN16ProgressDiagnostic.MaximumJsonBytes);
    }

    /// <summary>Only complete, ordered EXIT exchanges produce the response-completed marker.</summary>
    [Fact]
    public void ClientExitSuccessRequiresAllFiveBarriersBeforeResponseReceived()
    {
        var trace = string.Join("\n", Enumerable.Range(1, 6).Select(number => $"ASEVDN16C:{number:D2}"))
            + "\n" + string.Join("\n", Enumerable.Range(1, 5).Select(number => $"ASEVDN16X:{number:D2}"))
            + "\nASEVDN16C:07\n";

        var parsed = LinuxN16ProgressDiagnostic.ParseJoinedClientProgress(JoinedOutput(trace, eof: true));

        Assert.Equal(LinuxN16ProgressDiagnostic.ClientMilestone.ExitResponseReceived, parsed.LastMilestone);
        Assert.Equal(LinuxN16ProgressDiagnostic.ClientExitStage.ResponseReadCompleted, parsed.ExitStage);
        Assert.Equal(LinuxN16ProgressDiagnostic.ClientExitFailureClass.None, parsed.ExitFailureClass);
        Assert.Equal(LinuxN16ProgressDiagnostic.ClientExitDiagnosticCode.None, parsed.ExitDiagnosticCode);
        Assert.True(parsed.MarkerSequenceValid);
    }

    /// <summary>EXIT failures must agree with the last completed barrier and the closed code/class mapping.</summary>
    [Theory]
    [InlineData("ASEVDN16C:01\\nASEVDN16C:02\\nASEVDN16C:03\\nASEVDN16C:04\\nASEVDN16C:05\\nASEVDN16C:06\\nASEVDN16X:01\\nASEVDN16E:01:01:00\\n", "ConnectStarted", "Cancelled", "None")]
    [InlineData("ASEVDN16C:01\\nASEVDN16C:02\\nASEVDN16C:03\\nASEVDN16C:04\\nASEVDN16C:05\\nASEVDN16C:06\\nASEVDN16X:01\\nASEVDN16E:02:04:01\\n", "RootPeerCheck", "Protocol", "ASEVD402")]
    [InlineData("ASEVDN16C:01\\nASEVDN16C:02\\nASEVDN16C:03\\nASEVDN16C:04\\nASEVDN16C:05\\nASEVDN16C:06\\nASEVDN16X:01\\nASEVDN16X:02\\nASEVDN16E:03:02:00\\n", "RequestWrite", "Socket", "None")]
    [InlineData("ASEVDN16C:01\\nASEVDN16C:02\\nASEVDN16C:03\\nASEVDN16C:04\\nASEVDN16C:05\\nASEVDN16C:06\\nASEVDN16X:01\\nASEVDN16X:02\\nASEVDN16X:03\\nASEVDN16E:04:01:00\\n", "ResponseReadStarted", "Cancelled", "None")]
    public void ClientExitFailureStagesAreClosedAndOrdered(string trace, string expectedStage,
        string expectedFailure, string expectedCode)
    {
        var parsed = LinuxN16ProgressDiagnostic.ParseJoinedClientProgress(
            JoinedOutput(trace.Replace("\\n", "\n", StringComparison.Ordinal), eof: true));

        Assert.True(parsed.MarkerSequenceValid);
        Assert.Equal(expectedStage, parsed.ExitStage.ToString());
        Assert.Equal(expectedFailure, parsed.ExitFailureClass.ToString());
        Assert.Equal(expectedCode, parsed.ExitDiagnosticCode.ToString());
    }

    /// <summary>Malformed, misplaced, duplicated or post-failure EXIT markers collapse to unknown.</summary>
    [Theory]
    [InlineData("ASEVDN16X:01\\n")]
    [InlineData("ASEVDN16C:01\\nASEVDN16C:02\\nASEVDN16C:03\\nASEVDN16C:04\\nASEVDN16C:05\\nASEVDN16C:06\\nASEVDN16X:02\\n")]
    [InlineData("ASEVDN16C:01\\nASEVDN16C:02\\nASEVDN16C:03\\nASEVDN16C:04\\nASEVDN16C:05\\nASEVDN16C:06\\nASEVDN16X:01\\nASEVDN16E:03:02:00\\n")]
    [InlineData("ASEVDN16C:01\\nASEVDN16C:02\\nASEVDN16C:03\\nASEVDN16C:04\\nASEVDN16C:05\\nASEVDN16C:06\\nASEVDN16X:01\\nASEVDN16E:01:01:00\\nASEVDN16X:02\\n")]
    [InlineData("ASEVDN16C:01\\nASEVDN16C:02\\nASEVDN16C:03\\nASEVDN16C:04\\nASEVDN16C:05\\nASEVDN16C:06\\nASEVDN16X:01\\nASEVDN16E:01:00:00\\n")]
    [InlineData("ASEVDN16C:01\\nASEVDN16C:02\\nASEVDN16C:03\\nASEVDN16C:04\\nASEVDN16C:05\\nASEVDN16C:06\\nASEVDN16X:01\\nASEVDN16E:01:02:04\\n")]
    [InlineData("ASEVDN16C:01\\nASEVDN16C:02\\nASEVDN16C:03\\nASEVDN16C:04\\nASEVDN16C:05\\nASEVDN16C:06\\nASEVDN16Z:01\\n")]
    public void InvalidClientExitMarkerSequenceIsUnknown(string trace) =>
        AssertUnknown(JoinedOutput(trace.Replace("\\n", "\n", StringComparison.Ordinal), eof: true));

    /// <summary>Unavailable, truncated, failed, or over-budget original receipts cannot supply client milestones.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void IncompleteOriginalReceiptDoesNotExposeClientProgress(int fault)
    {
        // Fixed public documentation boundary; this test does not expose the Contracts internal limit type.
        const int maximumRetainedPrefixBytes = 1024 * 1024;
        var joined = JoinedOutput("ASEVDN16C:01\n", eof: true);
        SupervisionOutputReceipt? output = fault switch
        {
            0 => null,
            1 => joined with { Stderr = joined.Stderr with { Prefix = default } },
            2 => joined with
            {
                Stderr = joined.Stderr with { ReceivedBytes = joined.Stderr.ReceivedBytes + 1 },
                ReceivedBytes = joined.ReceivedBytes + 1
            },
            3 => joined with { ReceivedBytes = joined.ReceivedBytes + 1 },
            4 => joined with { QuotaExceeded = true },
            5 => joined with { StopSignalFailed = true },
            6 => joined with { Stdout = joined.Stdout with { EndOfStream = false } },
            7 => joined with { ReceivedByteLimit = joined.ReceivedBytes - 1 },
            8 => JoinedOutput("ASEVDN16C:01\n" + new string('x',
                maximumRetainedPrefixBytes), eof: true) with
            {
                ReceivedByteLimit = maximumRetainedPrefixBytes + 13
            },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };

        var parsed = LinuxN16ProgressDiagnostic.ParseJoinedClientProgress(output);

        Assert.Equal(LinuxN16ProgressDiagnostic.ClientMilestone.Unknown, parsed.LastMilestone);
        Assert.False(parsed.JoinedStreamComplete);
        Assert.False(parsed.MarkerSequenceValid);
    }

    /// <summary>Detached client data cannot claim a milestone with an invalid enum or incomplete marker receipt.</summary>
    [Theory]
    [InlineData(int.MaxValue, false, false)]
    [InlineData(0, false, true)]
    [InlineData(0, true, true)]
    [InlineData(3, false, true)]
    [InlineData(3, true, false)]
    public void InconsistentDetachedClientSnapshotRejects(int milestone, bool joined, bool valid)
    {
        var snapshot = Snapshot(LinuxN16ProgressDiagnostic.Milestone.Unknown) with
        {
            Client = new((LinuxN16ProgressDiagnostic.ClientMilestone)milestone, joined, valid)
        };

        Assert.Throws<InvalidOperationException>(() => LinuxN16ProgressDiagnostic.EncodeDetached(snapshot));
    }

    /// <summary>Detached EXIT fields accept only closed enums and states consistent with the joined marker sequence.</summary>
    [Fact]
    public void InconsistentDetachedClientExitStateRejects()
    {
        var validBase = Snapshot(LinuxN16ProgressDiagnostic.Milestone.Unknown);
        var client = new LinuxN16ProgressDiagnostic.ClientSnapshot(
            LinuxN16ProgressDiagnostic.ClientMilestone.ExitStarted, true, true,
            LinuxN16ProgressDiagnostic.ClientExitStage.RootPeerCheck,
            LinuxN16ProgressDiagnostic.ClientExitFailureClass.Protocol,
            LinuxN16ProgressDiagnostic.ClientExitDiagnosticCode.ASEVD402);

        Assert.Throws<InvalidOperationException>(() => LinuxN16ProgressDiagnostic.EncodeDetached(validBase with
        {
            Client = client with { ExitStage = (LinuxN16ProgressDiagnostic.ClientExitStage)int.MaxValue }
        }));
        Assert.Throws<InvalidOperationException>(() => LinuxN16ProgressDiagnostic.EncodeDetached(validBase with
        {
            Client = client with { ExitFailureClass = LinuxN16ProgressDiagnostic.ClientExitFailureClass.None }
        }));
        Assert.Throws<InvalidOperationException>(() => LinuxN16ProgressDiagnostic.EncodeDetached(validBase with
        {
            Client = client with { ExitDiagnosticCode = (LinuxN16ProgressDiagnostic.ClientExitDiagnosticCode)int.MaxValue }
        }));
        Assert.Throws<InvalidOperationException>(() => LinuxN16ProgressDiagnostic.EncodeDetached(validBase with
        {
            Client = client with { LastMilestone = LinuxN16ProgressDiagnostic.ClientMilestone.Unknown }
        }));
        Assert.Throws<InvalidOperationException>(() => LinuxN16ProgressDiagnostic.EncodeDetached(validBase with
        {
            Client = client with { LastMilestone = LinuxN16ProgressDiagnostic.ClientMilestone.ExitResponseReceived }
        }));
    }

    private static void AssertUnknown(SupervisionOutputReceipt? output)
    {
        var parsed = LinuxN16ProgressDiagnostic.ParseJoinedClientProgress(output);
        Assert.Equal(LinuxN16ProgressDiagnostic.ClientMilestone.Unknown, parsed.LastMilestone);
        Assert.False(parsed.MarkerSequenceValid);
    }

    private static SupervisionOutputReceipt JoinedOutput(string stderr, bool eof,
        SupervisionOutputFailure failure = SupervisionOutputFailure.None)
    {
        var bytes = Encoding.UTF8.GetBytes(stderr).ToImmutableArray();
        var empty = new SupervisionOutputStreamReceipt(0, ImmutableArray<byte>.Empty, true, SupervisionOutputFailure.None);
        var output = new SupervisionOutputStreamReceipt(bytes.Length, bytes, eof, failure);
        return new(empty, output, bytes.Length, 1024 * 1024, failure, false, false);
    }

    /// <summary>A valid progress snapshot decorates the same latched failure without changing its negative result.</summary>
    [Fact]
    public void RejectedWithValidProgressRetainsOriginalFailureAndMessage()
    {
        var latch = NewFailureLatch(out var original);
        var first = latch.First;
        var snapshot = Snapshot(LinuxN16ProgressDiagnostic.Milestone.BodyEntered, 125);

        var rejected = latch.RejectedWithN16Progress(snapshot);

        Assert.Same(first, rejected.Failure);
        Assert.Equal("ASEVD410", rejected.Failure.DiagnosticCode);
        Assert.Equal(original.Message, rejected.Message);
        Assert.Null(rejected.InnerException);
        Assert.Null(rejected.RootCleanupJson);
        Assert.Equal(snapshot, rejected.N16ProgressSnapshot);
    }

    /// <summary>Invalid or unknown stage data falls back to the identical original negative failure.</summary>
    [Theory]
    [InlineData(int.MaxValue, 12)]
    [InlineData(0, 12)]
    [InlineData(1, LinuxN16ProgressDiagnostic.MaximumElapsedMilliseconds + 1)]
    public void RejectedWithInvalidProgressFallsBackToOriginalFailure(int milestone, long elapsed)
    {
        var latch = NewFailureLatch(out var original);
        var first = latch.First;
        var invalid = Snapshot((LinuxN16ProgressDiagnostic.Milestone)milestone, elapsed);

        var rejected = latch.RejectedWithN16Progress(invalid);

        Assert.Same(first, rejected.Failure);
        Assert.Equal("ASEVD410", rejected.Failure.DiagnosticCode);
        Assert.Equal(original.Message, rejected.Message);
        Assert.Null(rejected.InnerException);
        Assert.Null(rejected.RootCleanupJson);
        Assert.Null(rejected.N16ProgressSnapshot);
    }

    /// <summary>Missing optional progress data leaves the existing negative exception unchanged.</summary>
    [Fact]
    public void RejectedWithoutProgressHasNoAdditionalSnapshot()
    {
        var latch = NewFailureLatch(out var original);
        var first = latch.First;

        var rejected = latch.RejectedWithN16Progress(null);

        Assert.Same(first, rejected.Failure);
        Assert.Equal("ASEVD410", rejected.Failure.DiagnosticCode);
        Assert.Equal(original.Message, rejected.Message);
        Assert.Null(rejected.InnerException);
        Assert.Null(rejected.RootCleanupJson);
        Assert.Null(rejected.N16ProgressSnapshot);
    }

    private static EvidenceNativeObservationFailureLatch NewFailureLatch(out EvidenceNativeObservationException original)
    {
        var latch = new EvidenceNativeObservationFailureLatch();
        var failure = Assert.Throws<EvidenceAdmissionException>(() =>
            LinuxEmptyObservationExecution.RequireFinalClose(TimeProvider.System, 0, TimeSpan.Zero));
        latch.Capture(EvidenceNativeObservationPhase.WorkerStop, failure);
        original = latch.Rejected();
        return latch;
    }
}
#endif
