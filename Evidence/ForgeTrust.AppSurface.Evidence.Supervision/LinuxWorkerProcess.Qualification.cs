using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

internal sealed partial class LinuxWorkerProcess
{
    /// <summary>
    /// Holds compile-selected observations without adding selected async methods to the worker.
    /// </summary>
    /// <remarks>
    /// The worker owns this holder and constructs it only after its existing pending coordinator.
    /// Hooks are diagnostic or selector plumbing only: they cannot admit a caller, create an owner,
    /// alter a deadline, replace a native call, or publish settlement. Keeping their async methods in
    /// this nested type leaves the eight worker lifecycle state-machine declarations in their common
    /// source order. The holder does not reinitialize any existing worker field.
    /// </remarks>
    private sealed class Qualification
    {
        private readonly LinuxWorkerProcess _owner;

        internal Qualification(LinuxWorkerProcess owner) => _owner = owner;

        internal SupervisionCancellationPhaseObservation? CancellationPhase
        {
#if EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09
            get => EvidenceNativeQualification.CancellationEnabled ? _owner._cancellationPhase : null;
#else
            get => null;
#endif
        }

        internal SupervisionDescendantObservation? Descendant
        {
#if EVIDENCE_PRIVATE_N12
            get => EvidenceNativeQualification.DescendantEnabled ? _owner._descendant : null;
#else
            get => null;
#endif
        }

        internal Task<string> StartWorkerAsync(LinuxSystemdBackend backend, LinuxWorkerUnit recipe, CancellationToken token)
        {
#if EVIDENCE_PRIVATE_N10
            return EvidenceNativeQualification.PendingStartRaceEnabled
                ? backend.StartWorkerForN10Async(recipe, _owner._n10PendingStart, token)
                : backend.StartWorkerAsync(recipe, token);
#else
#if EVIDENCE_PRIVATE_C3_SDK
            LinuxWorkerMeasurement.ObserveStartup(WorkerMeasurementStartup.NativeDispatch);
#endif
            return backend.StartWorkerAsync(recipe, token);
#endif
        }

        internal void ObserveNativeStartJoined()
        {
#if EVIDENCE_PRIVATE_C3_SDK
            LinuxWorkerMeasurement.ObserveStartup(WorkerMeasurementStartup.NativeStartJoined);
#endif
        }

        internal void ObserveRunningIdentity()
        {
#if EVIDENCE_PRIVATE_C3_SDK
            LinuxWorkerMeasurement.ObserveStartup(WorkerMeasurementStartup.RunningIdentity);
#endif
        }

        internal void ObserveDescriptorStart()
        {
#if EVIDENCE_PRIVATE_C3_SDK
            LinuxWorkerMeasurement.ObserveStartup(WorkerMeasurementStartup.DescriptorStart);
#endif
        }

        internal void ObserveDescriptorJoined()
        {
#if EVIDENCE_PRIVATE_C3_SDK
            LinuxWorkerMeasurement.ObserveStartup(WorkerMeasurementStartup.DescriptorJoined);
#endif
        }

        internal void ObserveMonitorRegistered()
        {
#if EVIDENCE_PRIVATE_C3_SDK
            LinuxWorkerMeasurement.ObserveStartup(WorkerMeasurementStartup.MonitorRegistered);
#endif
        }

        internal void CaptureMeasurement(LinuxProcessIdentity worker, CancellationToken token)
        {
#if EVIDENCE_PRIVATE_C3
#if EVIDENCE_PRIVATE_C3_SDK
            LinuxWorkerMeasurement.ObserveStartup(WorkerMeasurementStartup.TrackerCaptureStart);
#endif
            _owner._measurement = LinuxWorkerMeasurement.TryCapture(worker, token);
#if EVIDENCE_PRIVATE_C3_SDK
            LinuxWorkerMeasurement.ObserveStartup(WorkerMeasurementStartup.TrackerCaptureJoined);
            LinuxWorkerMeasurement.ObserveStartup(WorkerMeasurementStartup.ExitPlanStart);
            _owner._measurement?.PrepareFailedExitBarrier(_owner._input.Request.JobDeadlineUtc, token);
            LinuxWorkerMeasurement.ObserveStartup(WorkerMeasurementStartup.ExitPlanJoined);
#endif
#endif
        }

        internal void ObserveFailedExitBarrier(CancellationToken token)
        {
#if EVIDENCE_PRIVATE_C3_SDK
            _owner._measurement?.ObserveFailedExitBarrier(token);
#endif
        }

        internal void CaptureExit(LinuxUnitProperties facts, CancellationToken token)
        {
#if EVIDENCE_PRIVATE_C3
            if (facts.ExecMainCode == 1 && facts.ExecMainStatus == 0)
                _owner._measurement?.CaptureNormalExit(facts, token);
#if EVIDENCE_PRIVATE_C3_SDK
            else if (facts.ExecMainCode == 1 && facts.ExecMainStatus == 1)
                _owner._measurement?.CaptureFailedNormalExit(facts, token);
#endif
#endif
        }

        internal void ObserveContainmentStarted()
        {
#if EVIDENCE_PRIVATE_N11
            _owner._stopTiming?.Observe(LinuxWorkerStopMilestone.ContainmentStarted);
#endif
        }

        internal void ObserveUnitAndGroupStopped()
        {
#if EVIDENCE_PRIVATE_N11
            _owner._stopTiming?.Observe(LinuxWorkerStopMilestone.UnitAndGroupStopped);
#endif
        }

        internal void ObserveMonitorJoined()
        {
#if EVIDENCE_PRIVATE_N11
            _owner._stopTiming?.Observe(LinuxWorkerStopMilestone.MonitorJoined);
#endif
        }

        internal void ObservePumpsJoined()
        {
#if EVIDENCE_PRIVATE_N11
            _owner._stopTiming?.Observe(LinuxWorkerStopMilestone.PumpsJoined);
#endif
        }

        internal void ObserveFinalUnitAndGroupSampled()
        {
#if EVIDENCE_PRIVATE_N11
            _owner._stopTiming?.Observe(LinuxWorkerStopMilestone.FinalUnitAndGroupSampled);
#endif
        }

        internal void ObserveSettlementPublished()
        {
#if EVIDENCE_PRIVATE_N11
            _owner._stopTiming?.Observe(LinuxWorkerStopMilestone.SettlementPublished);
#endif
        }

        internal void RecordStopProcedureCall()
        {
#if EVIDENCE_PRIVATE_N10
            if (EvidenceNativeQualification.PendingStartRaceEnabled)
                Interlocked.Increment(ref _owner._n10StopProcedureCalls);
#endif
        }

        internal int? StopRequestStarted()
        {
#if EVIDENCE_PRIVATE_N11
            return _owner._stopTiming?.StopRequestStarted();
#else
            return null;
#endif
        }

        internal void StopRequestReplied(int? attempt)
        {
#if EVIDENCE_PRIVATE_N11
            if (attempt is int value) _owner._stopTiming?.StopRequestReplied(value);
#endif
        }

        internal void RecordSettlementUnit(LinuxUnitProperties stopped)
        {
#if EVIDENCE_PRIVATE_N07 || EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09 || EVIDENCE_PRIVATE_N10 || EVIDENCE_PRIVATE_N11
            _owner._failedSettlementUnit = stopped;
#endif
        }

        internal void RecordSettlementGroup(LinuxCgroupSample group)
        {
#if EVIDENCE_PRIVATE_N07 || EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09 || EVIDENCE_PRIVATE_N10 || EVIDENCE_PRIVATE_N11
            _owner._failedSettlementGroup = group;
            _owner._failedSettlementGroupAfterPumps = true;
#endif
        }

        internal void CaptureFailureSettlement(bool monitorJoined, bool unitJoined, bool groupJoined, bool pipesClosed)
        {
#if EVIDENCE_PRIVATE_N07
            try
            {
                if (EvidenceNativeQualification.ParentReplacementEnabled
                    && monitorJoined && _owner._exit is { IsCompleted: true }
                    && _owner._worker is not null && _owner._failedSettlementUnit is not null
                    && _owner._failedSettlementGroup is not null && _owner._output is not null)
                {
                    var pending = _owner._pending.Snapshot;
                    var data = LinuxN07FailureSettlement.CreateDetached(_owner._owner.RunId, _owner._worker.SampledFacts,
                        _owner._failedSettlementUnit, _owner._failedSettlementGroup, _owner._output, _owner._exit.Status,
                        _owner._lifetime.StartJoined, pending.StopJoined, unitJoined, groupJoined, pipesClosed);
                    Interlocked.CompareExchange(ref _owner._failureSettlement, data, null);
                }
            }
            catch (Exception) { }
#endif
        }

        internal void DisposeMeasurement()
        {
#if EVIDENCE_PRIVATE_C3
            _owner._measurement?.Dispose();
#endif
        }

        internal void BeginStopTiming()
        {
#if EVIDENCE_PRIVATE_N11
            if (_owner._stopTiming is not null)
            {
                try
                {
                    _owner._stopTiming.Begin(_owner._input.DiagnosticRemaining,
                        TimeSpan.FromSeconds(_owner._input.Request.StoppingSeconds), _owner._recipe?.RuntimeMicroseconds);
                }
                catch (Exception) { }
            }
#endif
        }

        internal Task ObserveStopJoinAsync(Task original)
        {
#if EVIDENCE_PRIVATE_N11
            if (_owner._stopTiming is null) return original;
            return ObserveStopJoinActualAsync(original);
#else
            return original;
#endif
        }

#if EVIDENCE_PRIVATE_N11
        private async Task ObserveStopJoinActualAsync(Task original)
        {
            try { await original.ConfigureAwait(false); }
            finally { _owner._stopTiming!.Observe(LinuxWorkerStopMilestone.StopJoined); }
        }
#endif
    }
}
