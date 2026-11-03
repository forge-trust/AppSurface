#if EVIDENCE_PRIVATE_N16
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

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

    /// <summary>Fixed client-side N16 milestones written to its existing stderr stream.</summary>
    internal enum ClientMilestone
    {
        /// <summary>No trusted marker was parsed.</summary>
        Unknown,
        /// <summary>The original accepted-work request was about to start.</summary>
        OriginalRequestStarted,
        /// <summary>The original acceptance was observed and STOP/WAIT dispatch was about to start.</summary>
        ControlsStarted,
        /// <summary>The original concurrent STOP and WAIT tasks both completed.</summary>
        ControlsJoined,
        /// <summary>The original accepted-work response task returned.</summary>
        OriginalResponseReceived,
        /// <summary>The original accepted-work response passed its existing closed checks.</summary>
        OriginalResponseValidated,
        /// <summary>The original EXIT request was about to start.</summary>
        ExitStarted,
        /// <summary>The original EXIT response task returned.</summary>
        ExitResponseReceived
    }

    /// <summary>Closed client-side stage of the original private N16 EXIT exchange.</summary>
    internal enum ClientExitStage
    {
        Unknown, ConnectStarted, RootPeerCheck, RootPeerVerified, RequestWrite, RequestLineWritten,
        ResponseReadStarted, ResponseReadCompleted
    }

    /// <summary>Closed failure classes from the original EXIT socket operation.</summary>
    internal enum ClientExitFailureClass { None, Cancelled, Socket, Disposed, Protocol, Other }

    /// <summary>Closed diagnostic-code family; arbitrary exception codes are never retained.</summary>
    internal enum ClientExitDiagnosticCode { None, ASEVD402, ASEVD410, ASEVD420 }

    /// <summary>Closed client progress copied from the original joined stderr receipt.</summary>
    /// <param name="LastMilestone">The last ordered marker, or Unknown when the stream cannot be trusted.</param>
    /// <param name="JoinedStreamComplete">Whether the original paired output receipt is complete; not process success.</param>
    /// <param name="MarkerSequenceValid">Whether at least one exact, ordered marker was parsed from that receipt.</param>
    /// <param name="ExitStage">The last fixed EXIT barrier or the operation stage that failed.</param>
    /// <param name="ExitFailureClass">A closed failure category from the original EXIT request, if one was recorded.</param>
    /// <param name="ExitDiagnosticCode">A closed diagnostic-code family; no exception text is retained.</param>
    internal sealed record ClientSnapshot(ClientMilestone LastMilestone, bool JoinedStreamComplete, bool MarkerSequenceValid,
        ClientExitStage ExitStage = ClientExitStage.Unknown,
        ClientExitFailureClass ExitFailureClass = ClientExitFailureClass.None,
        ClientExitDiagnosticCode ExitDiagnosticCode = ClientExitDiagnosticCode.None);

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
    /// <param name="Client">Optional client milestones parsed from the original joined output; data only.</param>
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
        bool ExitCommitted,
        ClientSnapshot? Client);

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
            || snapshot.LastMilestoneElapsedMilliseconds is < 0 or > MaximumElapsedMilliseconds
            || (snapshot.Client is { } client && (!Enum.IsDefined(client.LastMilestone)
                || !Enum.IsDefined(client.ExitStage) || !Enum.IsDefined(client.ExitFailureClass)
                || !Enum.IsDefined(client.ExitDiagnosticCode)
                || (client.ExitFailureClass != ClientExitFailureClass.None
                    && (client.ExitStage is ClientExitStage.Unknown or ClientExitStage.ResponseReadCompleted))
                || (client.ExitFailureClass == ClientExitFailureClass.None && client.ExitDiagnosticCode != ClientExitDiagnosticCode.None)
                || (client.ExitStage != ClientExitStage.Unknown
                    && (!client.MarkerSequenceValid
                        || client.LastMilestone is not (ClientMilestone.ExitStarted or ClientMilestone.ExitResponseReceived)))
                || (client.ExitFailureClass != ClientExitFailureClass.None
                    && client.LastMilestone != ClientMilestone.ExitStarted)
                || (client.ExitStage == ClientExitStage.ResponseReadCompleted
                    && client.LastMilestone != ClientMilestone.ExitResponseReceived)
                || client.MarkerSequenceValid && (!client.JoinedStreamComplete || client.LastMilestone == ClientMilestone.Unknown)
                || client.LastMilestone != ClientMilestone.Unknown && (!client.JoinedStreamComplete || !client.MarkerSequenceValid))))
            throw Rejected();

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "issue779-n16-progress-diagnostic-v2",
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
            client_last_milestone = snapshot.Client?.LastMilestone.ToString() ?? ClientMilestone.Unknown.ToString(),
            client_joined_stream_complete = snapshot.Client?.JoinedStreamComplete ?? false,
            client_marker_sequence_valid = snapshot.Client?.MarkerSequenceValid ?? false,
            client_exit_stage = snapshot.Client?.ExitStage.ToString() ?? ClientExitStage.Unknown.ToString(),
            client_exit_failure_class = snapshot.Client?.ExitFailureClass.ToString() ?? ClientExitFailureClass.None.ToString(),
            client_exit_diagnostic_code = snapshot.Client?.ExitDiagnosticCode.ToString() ?? ClientExitDiagnosticCode.None.ToString(),
            native_authority = false,
            native_acceptance = false,
        });
        if (bytes.Length is 0 or > MaximumJsonBytes) throw Rejected();
        return bytes;
    }

    /// <summary>Parses only exact fixed tokens from the already-joined original output receipt.</summary>
    /// <remarks>Any incomplete, failed, truncated, duplicated, reordered, or unknown marker set becomes Unknown.</remarks>
    /// <param name="output">The existing original paired output receipt, or null if unavailable.</param>
    /// <returns>Closed client progress data; it establishes no worker or native completion.</returns>
    internal static ClientSnapshot ParseJoinedClientProgress(SupervisionOutputReceipt? output)
    {
        if (output is null || !output.Successful || output.Stderr.Prefix.IsDefault
            || output.Stderr.ReceivedBytes != output.Stderr.Prefix.Length
            || output.Stderr.Prefix.Length > EvidenceRunBudgetLimits.RetainedOutputPrefixBytesPerStream)
            return new(ClientMilestone.Unknown, false, false);

        var last = ClientMilestone.Unknown;
        var expected = 1;
        var exitStage = ClientExitStage.Unknown;
        var exitFailure = ClientExitFailureClass.None;
        var exitCode = ClientExitDiagnosticCode.None;
        var expectedExit = 1;
        var sawMarker = false;
        var sawExitFailure = false;
        var prefix = output.Stderr.Prefix.AsSpan();
        var start = 0;
        for (var index = 0; index <= prefix.Length; index++)
        {
            if (index != prefix.Length && prefix[index] != (byte)'\n') continue;
            var line = prefix[start..index];
            start = index + 1;
            if (line.Length == 0) continue;
            if (line.StartsWith("ASEVDN16C:"u8))
            {
                if (sawExitFailure || index == prefix.Length || line.Length != 12 || !TwoDigits(line, 10, out var number)
                    || number != expected || number is < 1 or > 7
                    || number == 7 && expectedExit != 1 && expectedExit != 6)
                    return InvalidClientProgress();
                last = (ClientMilestone)number;
                expected++;
                sawMarker = true;
            }
            else if (line.StartsWith("ASEVDN16X:"u8))
            {
                if (last != ClientMilestone.ExitStarted || sawExitFailure || index == prefix.Length
                    || line.Length != 12 || !TwoDigits(line, 10, out var number)
                    || number != expectedExit || number is < 1 or > 5)
                    return InvalidClientProgress();
                exitStage = number switch
                {
                    1 => ClientExitStage.ConnectStarted,
                    2 => ClientExitStage.RootPeerVerified,
                    3 => ClientExitStage.RequestLineWritten,
                    4 => ClientExitStage.ResponseReadStarted,
                    _ => ClientExitStage.ResponseReadCompleted
                };
                expectedExit++;
                sawMarker = true;
            }
            else if (line.StartsWith("ASEVDN16E:"u8))
            {
                if (last != ClientMilestone.ExitStarted || sawExitFailure || index == prefix.Length
                    || line.Length != 18 || line[12] != (byte)':'
                    || line[15] != (byte)':' || !TwoDigits(line, 10, out var stage)
                    || !TwoDigits(line, 13, out var failure) || !TwoDigits(line, 16, out var code)
                    || stage is < 1 or > 4 || failure is < 1 or > 5 || code is < 0 or > 3
                    || code != 0 && failure != (int)ClientExitFailureClass.Protocol
                    || !FailureMatchesProgress(stage, expectedExit - 1))
                    return InvalidClientProgress();
                exitStage = stage switch
                {
                    1 => ClientExitStage.ConnectStarted,
                    2 => ClientExitStage.RootPeerCheck,
                    3 => ClientExitStage.RequestWrite,
                    _ => ClientExitStage.ResponseReadStarted
                };
                exitFailure = (ClientExitFailureClass)failure;
                exitCode = (ClientExitDiagnosticCode)code;
                sawExitFailure = true;
                sawMarker = true;
            }
            else if (line.StartsWith("ASEVDN16"u8))
            {
                return InvalidClientProgress();
            }
        }
        return new(last, true, sawMarker, exitStage, exitFailure, exitCode);
    }

    private static bool TwoDigits(ReadOnlySpan<byte> line, int offset, out int value)
    {
        value = 0;
        if (offset < 0 || offset + 1 >= line.Length || line[offset] is < (byte)'0' or > (byte)'9'
            || line[offset + 1] is < (byte)'0' or > (byte)'9') return false;
        value = (line[offset] - (byte)'0') * 10 + line[offset + 1] - (byte)'0';
        return true;
    }

    private static bool FailureMatchesProgress(int stage, int lastExitMarker) => stage switch
    {
        1 or 2 => lastExitMarker == 1,
        3 => lastExitMarker == 2,
        4 => lastExitMarker is 3 or 4,
        _ => false
    };

    private static ClientSnapshot InvalidClientProgress() => new(ClientMilestone.Unknown, true, false);

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
