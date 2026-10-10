#if EVIDENCE_PRIVATE_N10
using System.Collections.Immutable;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Data-only N10 frame and join-guard controls; these tests create no native start or ownership.</summary>
public sealed class LinuxN10PendingStartCheckpointTests
{
    private static readonly Guid Generation = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static LinuxUnitName Unit() => LinuxUnitName.Create(LinuxUnitRole.Worker, Generation);
    private static LinuxCgroupSample Group() => new(false, null, null, null, null, null);
    private static LinuxUnitProperties FinalUnit() => new(Unit().Value, "loaded", "inactive", "dead", string.Empty,
        0, 123, 1, 0, "4", "4", "exec", "control-group", true, 30_000_000, 5_000_000);
    private static SupervisionOutputReceipt Output()
    {
        var stream = new SupervisionOutputStreamReceipt(0, ImmutableArray<byte>.Empty, true, SupervisionOutputFailure.None);
        return new(stream, stream, 0, 114_688, SupervisionOutputFailure.None, false, false);
    }
    private static SupervisionPendingStartSnapshot Pending(SupervisionPendingStartFailure failure = SupervisionPendingStartFailure.StartCancelled) =>
        new(true, false, true, true, true, failure);

    [Fact]
    public void StartReplyFrameIsClosedAndBounded()
    {
        var checkpoint = new LinuxN10PendingStartCheckpoint();
        using var frame = JsonDocument.Parse(checkpoint.RecordStartReply(Unit(), "/org/freedesktop/systemd1/job/17"));
        Assert.Equal(7, frame.RootElement.EnumerateObject().Count());
        Assert.Equal("issue779-n10-pending-start-phase-v1", frame.RootElement.GetProperty("schema").GetString());
        Assert.Equal("N10", frame.RootElement.GetProperty("case").GetString());
        Assert.Equal("start-transient-unit-reply", frame.RootElement.GetProperty("phase").GetString());
        Assert.Equal(Unit().Value, frame.RootElement.GetProperty("unit").GetString());
        Assert.Equal("/org/freedesktop/systemd1/job/17", frame.RootElement.GetProperty("job_path").GetString());
        Assert.False(frame.RootElement.GetProperty("native_authority").GetBoolean());
        Assert.False(frame.RootElement.GetProperty("native_acceptance").GetBoolean());
    }

    [Theory]
    [InlineData("/org/freedesktop/systemd1/unit/not-a-job")]
    [InlineData("/org/freedesktop/systemd1/job/17\ncanary")]
    [InlineData("/org/freedesktop/systemd1/job/")]
    public void StartReplyRejectsUnvalidatedJobPath(string jobPath)
    {
        var checkpoint = new LinuxN10PendingStartCheckpoint();
        var error = Assert.Throws<EvidenceAdmissionException>(() => checkpoint.RecordStartReply(Unit(), jobPath));
        Assert.Equal("ASEVD410", error.Code);
        Assert.StartsWith("ASEVD410: The protected systemd operation or its response is invalid. Fix:", error.Message, StringComparison.Ordinal);
        Assert.EndsWith("See start-here/evidencehost.md.", error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.False(error.Message.Contains("canary", StringComparison.Ordinal));
    }

    [Fact]
    public void JoinedProjectionPreservesStickyCancellationAndFalsePhysicalSettlement()
    {
        var checkpoint = new LinuxN10PendingStartCheckpoint();
        var unit = Unit();
        _ = checkpoint.RecordStartReply(unit, "/org/freedesktop/systemd1/job/17");
        var bytes = checkpoint.EncodeAfterJoin(Generation, unit, Pending(), true, true, true,
            false, 2, FinalUnit(), true, Group(), true, Output());
        using var frame = JsonDocument.Parse(bytes);
        Assert.Equal("original-start-stop-and-pumps-joined-before-custody", frame.RootElement.GetProperty("phase").GetString());
        Assert.False(frame.RootElement.GetProperty("lifetime").GetProperty("physically_settled").GetBoolean());
        Assert.Equal("StartCancelled", frame.RootElement.GetProperty("pending_start").GetProperty("first_failure").GetString());
        Assert.Equal(2, frame.RootElement.GetProperty("original_stop_delegate_calls").GetInt32());
        Assert.True(frame.RootElement.GetProperty("final_unit").GetProperty("stopped").GetBoolean());
        Assert.True(frame.RootElement.GetProperty("final_group_after_pumps").GetProperty("empty").GetBoolean());
        Assert.False(frame.RootElement.GetProperty("root_custody_completed").GetBoolean());
        Assert.False(frame.RootElement.GetProperty("native_authority").GetBoolean());
        Assert.False(frame.RootElement.GetProperty("native_acceptance").GetBoolean());
    }

    [Theory]
    [InlineData(false, true, true, true, 2, (int)SupervisionPendingStartFailure.StartCancelled)]
    [InlineData(true, false, true, true, 2, (int)SupervisionPendingStartFailure.StartCancelled)]
    [InlineData(true, true, false, true, 2, (int)SupervisionPendingStartFailure.StartCancelled)]
    [InlineData(true, true, true, false, 2, (int)SupervisionPendingStartFailure.StartCancelled)]
    [InlineData(true, true, true, true, 1, (int)SupervisionPendingStartFailure.StartCancelled)]
    [InlineData(true, true, true, true, 2, (int)SupervisionPendingStartFailure.None)]
    public void JoinedProjectionRejectsMissingOriginalJoinOrTwoStopEvidence(bool reserved, bool startJoined,
        bool closed, bool stopJoined, int stopCalls, int failureValue)
    {
        var checkpoint = new LinuxN10PendingStartCheckpoint();
        var unit = Unit();
        _ = checkpoint.RecordStartReply(unit, "/org/freedesktop/systemd1/job/17");
        var failure = (SupervisionPendingStartFailure)failureValue;
        var pending = new SupervisionPendingStartSnapshot(reserved, false, startJoined, closed, stopJoined, failure);
        var error = Assert.Throws<EvidenceAdmissionException>(() => checkpoint.EncodeAfterJoin(Generation, unit,
            pending, true, true, true, false, stopCalls, FinalUnit(), true, Group(), true, Output()));
        Assert.Equal("ASEVD410", error.Code);
        Assert.StartsWith("ASEVD410: The protected systemd operation or its response is invalid. Fix:", error.Message, StringComparison.Ordinal);
        Assert.EndsWith("See start-here/evidencehost.md.", error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.False(error.Message.Contains("canary", StringComparison.Ordinal));
    }
}
#endif
