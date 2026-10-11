using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed timestamps copied from the original worker stop procedure, in elapsed milliseconds.</summary>
/// <param name="JobRemainingMilliseconds">Original job remainder sampled once at stop entry; negative values mean expired.</param>
/// <param name="StoppingMilliseconds">Unchanged configured stop allowance.</param>
/// <param name="RuntimeMicroseconds">Runtime limit in the actual selected worker recipe, when available.</param>
/// <param name="ContainmentStarted">Entry into the original containment procedure.</param>
/// <param name="FirstStopStarted">First actual systemd stop request entry.</param>
/// <param name="FirstStopReplied">First actual systemd stop reply; this does not establish signal delivery.</param>
/// <param name="SecondStopStarted">Second actual request after pending-start join, when required.</param>
/// <param name="SecondStopReplied">Second actual stop reply, when one completed.</param>
/// <param name="UnitAndGroupStopped">First matching stopped unit and empty selected group observation.</param>
/// <param name="MonitorJoined">Original native monitor task completion, including fault or cancellation.</param>
/// <param name="PumpsJoined">Original paired pump join returned, including a failed output receipt.</param>
/// <param name="FinalUnitAndGroupSampled">Existing final unit and group reads after pumps returned.</param>
/// <param name="SettlementPublished">Existing physical-settlement publication after all final checks.</param>
/// <param name="StopJoined">Original lifetime stop task completed, including a failed task.</param>
internal sealed record LinuxWorkerStopTimingSnapshot(long JobRemainingMilliseconds, long StoppingMilliseconds,
    ulong? RuntimeMicroseconds, long? ContainmentStarted, long? FirstStopStarted, long? FirstStopReplied,
    long? SecondStopStarted, long? SecondStopReplied, long? UnitAndGroupStopped, long? MonitorJoined,
    long? PumpsJoined, long? FinalUnitAndGroupSampled, long? SettlementPublished, long? StopJoined)
{
    /// <summary>Serializes fixed numeric private data; this establishes no deadline, exit or native authority.</summary>
    internal byte[] Encode()
    {
        var values = new[] { ContainmentStarted, FirstStopStarted, FirstStopReplied, SecondStopStarted,
            SecondStopReplied, UnitAndGroupStopped, MonitorJoined, PumpsJoined, FinalUnitAndGroupSampled,
            SettlementPublished, StopJoined };
        if (JobRemainingMilliseconds is < -3_600_000 or > 3_600_000
            || StoppingMilliseconds is <= 0 or > 3_600_000
            || RuntimeMicroseconds is 0 or > 3_600_000_000
            || values.Any(value => value is < 0 or > 3_600_000))
            throw new InvalidOperationException("ASEVD410: Worker stop timing data rejected.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "issue779-original-worker-stop-timing-v1",
            job_remaining_ms = JobRemainingMilliseconds,
            stopping_ms = StoppingMilliseconds,
            worker_runtime_us = RuntimeMicroseconds,
            containment_started_ms = ContainmentStarted,
            stop_1_started_ms = FirstStopStarted,
            stop_1_replied_ms = FirstStopReplied,
            stop_2_started_ms = SecondStopStarted,
            stop_2_replied_ms = SecondStopReplied,
            unit_and_group_stopped_ms = UnitAndGroupStopped,
            monitor_joined_ms = MonitorJoined,
            pumps_joined_ms = PumpsJoined,
            final_unit_and_group_sampled_ms = FinalUnitAndGroupSampled,
            settlement_published_ms = SettlementPublished,
            stop_joined_ms = StopJoined,
        });
        if (bytes.Length > 1024) throw new InvalidOperationException("ASEVD410: Worker stop timing data rejected.");
        return bytes;
    }
}

/// <summary>Best-effort monotonic telemetry at existing stop callsites; it never changes execution state.</summary>
/// <remarks>
/// Construction and all methods perform no OS access, waits or timer creation. The native holder creates
/// this only for its compiled N11 fixture. First observations are immutable; missing milestones stay null.
/// A clock failure makes the snapshot unavailable and never replaces the original cleanup failure.
/// Tests may supply a clock to verify bookkeeping only; a detached snapshot cannot prove actual stopping.
/// </remarks>
internal sealed class LinuxWorkerStopTiming(TimeProvider clock)
{
    private readonly object _gate = new();
    private long? _started;
    private long _remaining;
    private long _stopping;
    private ulong? _runtime;
    private bool _unavailable;
    private readonly long?[] _milestones = new long?[11];
    private int _stopAttempts;

    /// <summary>Records only the first caller's original allowance samples; no clock or budget is renewed.</summary>
    internal void Begin(TimeSpan remaining, TimeSpan stopping, ulong? runtime)
    {
        lock (_gate)
        {
            if (_started is not null || _unavailable) return;
            try
            {
                _started = clock.GetTimestamp();
                _remaining = remaining.Ticks / TimeSpan.TicksPerMillisecond;
                _stopping = stopping.Ticks / TimeSpan.TicksPerMillisecond;
                _runtime = runtime;
            }
            catch (Exception) { _unavailable = true; }
        }
    }

    /// <summary>Copies one fixed milestone at the actual original procedure event; first observation wins.</summary>
    internal void Observe(LinuxWorkerStopMilestone milestone)
    {
        lock (_gate)
        {
            if (_started is not long started || _unavailable) return;
            try
            {
                if (!Enum.IsDefined(milestone)) { _unavailable = true; return; }
                _milestones[(int)milestone] ??= clock.GetElapsedTime(started).Ticks / TimeSpan.TicksPerMillisecond;
            }
            catch (Exception) { _unavailable = true; }
        }
    }

    /// <summary>Records an actual stop request entry and returns its ordinal, without changing dispatch.</summary>
    internal int StopRequestStarted()
    {
        lock (_gate)
        {
            var attempt = ++_stopAttempts;
            if (attempt > 2) { _unavailable = true; return attempt; }
            Observe(attempt == 1 ? LinuxWorkerStopMilestone.FirstStopStarted : LinuxWorkerStopMilestone.SecondStopStarted);
            return attempt;
        }
    }

    /// <summary>Records only a returned request; it does not label the reply as delivery or physical exit.</summary>
    internal void StopRequestReplied(int attempt)
    {
        if (attempt is 1 or 2)
            Observe(attempt == 1 ? LinuxWorkerStopMilestone.FirstStopReplied : LinuxWorkerStopMilestone.SecondStopReplied);
    }

    /// <summary>Copies a detached immutable numeric record, or null when no reliable observation exists.</summary>
    internal LinuxWorkerStopTimingSnapshot? Snapshot
    {
        get
        {
            lock (_gate)
                return _started is null || _unavailable ? null : new(_remaining, _stopping, _runtime,
                    _milestones[0], _milestones[1], _milestones[2], _milestones[3], _milestones[4],
                    _milestones[5], _milestones[6], _milestones[7], _milestones[8], _milestones[9], _milestones[10]);
        }
    }
}

/// <summary>Fixed original worker-stop callsites; these labels grant no ownership or completion claim.</summary>
internal enum LinuxWorkerStopMilestone
{
    /// <summary>Original containment entry.</summary>
    ContainmentStarted,
    /// <summary>First original systemd stop request entry.</summary>
    FirstStopStarted,
    /// <summary>First original systemd stop reply.</summary>
    FirstStopReplied,
    /// <summary>Second original systemd stop request entry.</summary>
    SecondStopStarted,
    /// <summary>Second original systemd stop reply.</summary>
    SecondStopReplied,
    /// <summary>Matching stopped unit and empty group.</summary>
    UnitAndGroupStopped,
    /// <summary>Original monitor task joined, including fault.</summary>
    MonitorJoined,
    /// <summary>Original pump join returned, including failed receipt.</summary>
    PumpsJoined,
    /// <summary>Original final post-pump unit and group reads returned.</summary>
    FinalUnitAndGroupSampled,
    /// <summary>Original physical settlement flag published.</summary>
    SettlementPublished,
    /// <summary>Original lifetime stop task joined, including fault.</summary>
    StopJoined,
}
