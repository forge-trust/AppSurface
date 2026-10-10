#if EVIDENCE_PRIVATE_N16
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Encodes the fixed, bounded N16 progress snapshot without reading owners or accepting caller data.</summary>
/// <remarks>Construction is data-only; only the original joined N16 server supplies runtime snapshots.</remarks>
internal static class LinuxN16ProgressDiagnostic
{
    /// <summary>Closed original-server milestones retained for this private failure diagnostic.</summary>
    internal enum Milestone
    {
        /// <summary>No N16 milestone was observed.</summary>
        Unknown,
        /// <summary>The original accepted-work request claimed its workload slot.</summary>
        WorkClaimed,
        /// <summary>The original registered blocked body entered.</summary>
        BodyEntered,
        /// <summary>The intermediate acceptance write committed on the original connection.</summary>
        AcceptedWriteCommitted,
        /// <summary>The accepted-work response committed on the original connection.</summary>
        ResponseCommitted,
        /// <summary>The original STOP request began its owned stop operation.</summary>
        StopStarted,
        /// <summary>The original positive WAIT request began its owned join.</summary>
        WaitStarted,
        /// <summary>The original request handlers observed their control overlap.</summary>
        ControlsOverlapped,
        /// <summary>The original STOP response write committed.</summary>
        StopWriteCommitted,
        /// <summary>The original positive WAIT response write committed.</summary>
        WaitWriteCommitted,
        /// <summary>The original EXIT response write committed.</summary>
        ExitCommitted,
    }

    /// <summary>Copied original N16 server flags and the monotonic age of its last observed milestone.</summary>
    /// <remarks>This detached data has no native, admission, custody, or success authority.</remarks>
    /// <param name="LastMilestone">The last observed closed original-server milestone, or Unknown.</param>
    /// <param name="LastMilestoneElapsedMilliseconds">Elapsed monotonic milliseconds, or null when unobserved.</param>
    /// <param name="WorkClaimed">Whether the original workload claim flag is set.</param>
    /// <param name="BodyEntered">Whether the original blocked body entry task completed successfully.</param>
    /// <param name="AcceptedWriteCommitted">Whether the original acceptance write committed.</param>
    /// <param name="ResponseCommitted">Whether the original request response committed.</param>
    /// <param name="StopStarted">Whether the original stop-start task completed successfully.</param>
    /// <param name="WaitStarted">Whether the original wait-start task completed successfully.</param>
    /// <param name="ControlsOverlapped">Whether the existing original control-overlap flag is set.</param>
    /// <param name="StopWriteCommitted">Whether the original STOP response committed.</param>
    /// <param name="WaitWriteCommitted">Whether the original positive WAIT response committed.</param>
    /// <param name="ExitCommitted">Whether the original EXIT response commit task completed successfully.</param>
    internal sealed record Snapshot(
        Milestone LastMilestone,
        long? LastMilestoneElapsedMilliseconds,
        bool WorkClaimed,
        bool BodyEntered,
        bool AcceptedWriteCommitted,
        bool ResponseCommitted,
        bool StopStarted,
        bool WaitStarted,
        bool ControlsOverlapped,
        bool StopWriteCommitted,
        bool WaitWriteCommitted,
        bool ExitCommitted);

    /// <summary>Maximum UTF-8 JSON bytes, excluding the caller's line feed.</summary>
    internal const int MaximumJsonBytes = 1024;

    /// <summary>Maximum representable elapsed milliseconds for a bounded supervisor run.</summary>
    internal const long MaximumElapsedMilliseconds = 86_400_000;

    /// <summary>Serializes fixed closed fields and rejects unknown stages or unbounded elapsed values.</summary>
    /// <param name="snapshot">Copied milestone data from the original N16 control server.</param>
    /// <returns>A new bounded JSON byte array with no path, argv, message, exception, or caller string.</returns>
    /// <exception cref="InvalidOperationException">A stage or elapsed value is outside the closed schema.</exception>
    internal static byte[] EncodeDetached(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Enum.IsDefined(snapshot.LastMilestone)
            || (snapshot.LastMilestone == Milestone.Unknown)
                != (snapshot.LastMilestoneElapsedMilliseconds is null)
            || snapshot.LastMilestoneElapsedMilliseconds is < 0 or > MaximumElapsedMilliseconds)
            throw Rejected();

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "issue779-n16-progress-diagnostic-v1",
            last_milestone = snapshot.LastMilestone.ToString(),
            last_milestone_elapsed_ms = snapshot.LastMilestoneElapsedMilliseconds,
            work_claimed = snapshot.WorkClaimed,
            body_entered = snapshot.BodyEntered,
            accepted_write_committed = snapshot.AcceptedWriteCommitted,
            response_committed = snapshot.ResponseCommitted,
            stop_started = snapshot.StopStarted,
            wait_started = snapshot.WaitStarted,
            controls_overlapped = snapshot.ControlsOverlapped,
            stop_write_committed = snapshot.StopWriteCommitted,
            wait_write_committed = snapshot.WaitWriteCommitted,
            exit_committed = snapshot.ExitCommitted,
            native_authority = false,
            native_acceptance = false,
        });
        if (bytes.Length is 0 or > MaximumJsonBytes) throw Rejected();
        return bytes;
    }

    /// <summary>Measures a monotonic timestamp interval as bounded whole milliseconds.</summary>
    /// <param name="startedAt">The original milestone timestamp.</param>
    /// <param name="observedAt">A later timestamp from the same monotonic clock.</param>
    /// <param name="frequency">Ticks per second for that clock.</param>
    /// <returns>Elapsed whole milliseconds, rounded down.</returns>
    /// <exception cref="InvalidOperationException">The interval is reversed, invalid, or outside the fixed bound.</exception>
    internal static long ElapsedMilliseconds(long startedAt, long observedAt, long frequency)
    {
        if (startedAt <= 0 || observedAt < startedAt || frequency <= 0) throw Rejected();
        var elapsed = Math.Floor(((double)(observedAt - startedAt) * 1000d) / frequency);
        if (!double.IsFinite(elapsed) || elapsed < 0 || elapsed > MaximumElapsedMilliseconds) throw Rejected();
        return (long)elapsed;
    }

    private static InvalidOperationException Rejected() =>
        new("ASEVD410: N16 progress diagnostic data rejected.");
}
#endif
