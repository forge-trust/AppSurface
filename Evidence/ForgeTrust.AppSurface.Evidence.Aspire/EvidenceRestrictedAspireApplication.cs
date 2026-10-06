using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Evidence.Aspire;

/// <summary>Owns the local state of a single root-broker application attempt.</summary>
/// <remarks>
/// This lease never builds an AppHost in the protected worker or accepts a factory, command or endpoint.
/// The host creates and retains it after shared admission, before startup I/O. The existing supervisor
/// owns physical application exit together with producer groups and pumps; disposal adds no stop timer.
/// Production catalogue and acceptance registration remain separate requirements.
/// </remarks>
internal sealed class EvidenceRestrictedAspireApplication : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly EvidenceLinuxWorkerSupervisor _worker;
    private readonly EvidenceAdmissionResult _admission;
    private readonly EvidencePlan _plan;
    private readonly EvidenceWorkerExecution _execution;
    private readonly EvidenceRestrictedApplicationCleanup _cleanup;
    private readonly EvidenceLinuxApplicationDescriptor _descriptor;
    private EvidenceLinuxApplicationStartReceipt? _started;
    private readonly HashSet<string> _resourceAttempts = new(StringComparer.Ordinal);
    private ResourceStage? _resourceStage;
    private bool _startClaimed;
    private bool _disposed;

    /// <summary>Captures an already selected immutable registration; does not start work or grant admission.</summary>
    /// <param name="worker">Actual connected, root-authenticated worker supervisor.</param>
    /// <param name="admission">Active capability issued for the exact protected plan.</param>
    /// <param name="plan">Plan compared with protected inputs during shared admission.</param>
    /// <param name="definition">Registration selected through the closed compiled catalogue.</param>
    /// <param name="execution">Existing tracked run and physical exit owner.</param>
    internal EvidenceRestrictedAspireApplication(EvidenceLinuxWorkerSupervisor worker,
        EvidenceAdmissionResult admission, EvidencePlan plan, EvidenceClosedApplicationDefinition definition,
        EvidenceWorkerExecution execution)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(execution);
        admission.ValidateActive(plan);
        var application = worker.Descriptor.Application;
        if (worker.Descriptor.Schema != "evidence-worker-linux-v2" || application is null || !worker.IsArmed
            || worker.RunId != admission.RunId || application.ApplicationId != definition.Id
            || application.EntryDigest != EvidenceClosedApplicationCatalogue.ComputeEntryDigest(definition))
        {
            throw Failure();
        }

        _worker = worker;
        _admission = admission;
        _plan = plan;
        _execution = execution;
        _cleanup = new(execution);
        _descriptor = application;
    }

    /// <summary>Claims one start and awaits the typed root-owned receipt under the existing host stage token.</summary>
    /// <param name="cancellationToken">Actual tracked Start-stage token.</param>
    internal async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposed || _startClaimed) throw Failure();
            _admission.ValidateActive(_plan);
            cancellationToken.ThrowIfCancellationRequested();
            _startClaimed = true;
        }

        var started = await _worker.StartApplicationAsync(_descriptor.ApplicationId, _descriptor.EntryDigest,
            cancellationToken).ConfigureAwait(false);
        _admission.ValidateActive(_plan);
        lock (_gate)
        {
            if (_disposed) throw Failure();
            _started = started;
        }
    }

    /// <summary>Creates a closed adapter for one resource selected by the compiled definition.</summary>
    /// <param name="resourceId">Exact selected resource identifier; never an endpoint or health callback.</param>
    internal IEvidenceResourceReadiness CreateReadiness(string resourceId)
    {
        lock (_gate)
        {
            if (_disposed || !_descriptor.Resources.Any(item => item.Id == resourceId)) throw Failure();
            return new RestrictedReadiness(resourceId, this);
        }
    }

    /// <summary>Binds one readiness attempt to the actual tracked host resource stage.</summary>
    /// <param name="resourceId">Exact selected resource identifier.</param>
    /// <param name="readiness">Private adapter captured before application startup.</param>
    /// <param name="stageToken">Actual Resource-stage cancellation token; no new timer is created.</param>
    /// <remarks>A public call to the adapter without this temporary binding grants no root operation.</remarks>
    internal async Task RunReadinessAsync(string resourceId, IEvidenceResourceReadiness readiness, CancellationToken stageToken)
    {
        ResourceStage stage;
        lock (_gate)
        {
            if (_disposed || _started is null || _resourceStage is not null || !stageToken.CanBeCanceled
                || readiness is not RestrictedReadiness adapter || !ReferenceEquals(adapter.Application, this)
                || adapter.Id != resourceId || !_resourceAttempts.Add(resourceId)) throw Failure();
            _admission.ValidateActive(_plan);
            stageToken.ThrowIfCancellationRequested();
            stage = new(resourceId, stageToken);
            _resourceStage = stage;
        }

        try { await readiness.WaitUntilReadyAsync(stageToken).ConfigureAwait(false); }
        finally
        {
            lock (_gate) if (ReferenceEquals(_resourceStage, stage)) _resourceStage = null;
        }
    }

    /// <summary>Closes local state only after the existing lifecycle establishes physical owned exit.</summary>
    /// <returns>Completed cleanup, or a latched failure which repeated disposal cannot erase.</returns>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (!_cleanup.Close()) return ValueTask.CompletedTask;

            _disposed = true;
            _started = null;
            _resourceStage = null;
            return ValueTask.CompletedTask;
        }
    }

    private Task WaitForResourceAsync(string resourceId, CancellationToken cancellationToken)
    {
        EvidenceLinuxApplicationStartReceipt started;
        CancellationToken stageToken;
        lock (_gate)
        {
            if (_disposed || _started is null || _resourceStage is not { } stage
                || stage.ResourceId != resourceId || stage.Consumed) throw Failure();
            _admission.ValidateActive(_plan);
            stage.Consumed = true;
            stageToken = stage.Token;
            started = _started;
        }

        return EvidenceRestrictedProducerLease.TrackProcedureAsync(_execution, stageToken, cancellationToken, async token =>
        {
            _ = await _worker.WaitForApplicationResourceAsync(started.LeaseId, resourceId, token).ConfigureAwait(false);
            _admission.ValidateActive(_plan);
            return true;
        });
    }

    private static EvidenceAdmissionException Failure() => new("ASEVD410", "Restricted application ownership is unavailable.");

    private sealed class RestrictedReadiness(string id, EvidenceRestrictedAspireApplication application) : IEvidenceResourceReadiness
    {
        internal EvidenceRestrictedAspireApplication Application { get; } = application;
        public string Id { get; } = id;
        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => Application.WaitForResourceAsync(Id, cancellationToken);
    }

    private sealed class ResourceStage(string resourceId, CancellationToken token)
    {
        internal string ResourceId { get; } = resourceId;
        internal CancellationToken Token { get; } = token;
        internal bool Consumed { get; set; }
    }
}

/// <summary>Local cleanup state; owns no supervisor, application start, admission or transport capability.</summary>
/// <remarks>Extracted to verify registered cleanup and permanently failed premature disposal through internal APIs.</remarks>
internal sealed class EvidenceRestrictedApplicationCleanup(EvidenceWorkerExecution execution)
{
    private readonly object _gate = new();
    private bool _closed;
    private bool _failed;

    /// <summary>Closes once in the joined pre-disposal phase; an early failure remains latched.</summary>
    /// <returns>True for the first close, false for a repeated successful close.</returns>
    internal bool Close()
    {
        lock (_gate)
        {
            if (_failed) throw EvidenceRestrictedProducerLease.Failure();
            if (_closed) return false;
            try { execution.RequireJoinedCleanupPhase(); }
            catch
            {
                _failed = true;
                throw;
            }
            _closed = true;
            return true;
        }
    }
}
