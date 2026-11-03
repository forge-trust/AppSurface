namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>One callback-scoped restricted producer binding, issued only through a true protected writer.</summary>
/// <remarks>
/// It retains the actual connected supervisor, active admission, complete plan/declaration snapshots,
/// copied protected diff, shared received-output quota and existing lifecycle. It creates no admission.
/// Dispose irreversibly invalidates use; already tracked work remains owned by the lifecycle until joined.
/// </remarks>
internal sealed class EvidenceRestrictedProducerLease : IDisposable
{
    private readonly EvidenceAdmissionResult _admission;
    private readonly EvidencePlan _plan;
    private readonly byte[] _planBytes;
    private readonly byte[] _declarationBytes;
    private readonly byte[]? _diffBytes;
    private readonly EvidenceWorkerExecution _execution;
    private readonly CancellationToken _stageToken;
    private readonly EvidenceRestrictedProducerAttempt _attempt = new();

    private EvidenceRestrictedProducerLease(EvidenceLinuxWorkerSupervisor worker, EvidenceAdmissionResult admission,
        EvidencePlan plan, EvidenceProducerDeclaration declaration, byte[]? diffBytes, EvidenceRunByteQuota outputQuota,
        EvidenceWorkerExecution execution, CancellationToken stageToken)
    {
        Worker = worker;
        OutputQuota = outputQuota;
        _admission = admission;
        _planBytes = EvidenceCanonicalJson.Serialize(plan);
        _plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(_planBytes);
        _declarationBytes = EvidenceCanonicalJson.Serialize(declaration);
        _diffBytes = diffBytes?.ToArray();
        _execution = execution;
        _stageToken = stageToken;
    }

    /// <summary>Gets the actual authenticated supervisor; never a caller-supplied transport.</summary>
    internal EvidenceLinuxWorkerSupervisor Worker { get; }
    /// <summary>Gets the single run-wide received-output quota shared by subject and protected reporter.</summary>
    internal EvidenceRunByteQuota OutputQuota { get; }
    /// <summary>Returns a defensive copy of the counted protected planning diff, or null.</summary>
    internal byte[]? CopyDiffBytes() => _diffBytes?.ToArray();

    /// <summary>Builds metadata only after the protected writer has checked its ownership references.</summary>
    /// <remarks>The writer is the sole issuing entry. This internal factory performs no process or artifact I/O.</remarks>
    internal static EvidenceRestrictedProducerLease Create(EvidenceArtifactWriter writer, EvidenceLinuxWorkerSupervisor worker,
        EvidenceAdmissionResult admission, EvidencePlan plan, EvidenceProducerDeclaration declaration,
        byte[]? diffBytes, EvidenceRunByteQuota outputQuota, EvidenceWorkerExecution execution, CancellationToken stageToken)
    {
        if (writer is null) throw Failure();
        writer.RequireRestrictedProducerOwnership(worker, admission, execution);
        if (worker is null || admission is null || outputQuota is null || execution is null
            || !stageToken.CanBeCanceled || execution.IsAdmissionClosed || !worker.IsArmed || outputQuota.IsFailed
            || outputQuota.Limit > EvidenceRunBudgetLimits.MaximumProcessOutputBytes)
            throw Failure();
        stageToken.ThrowIfCancellationRequested();
        ValidateMetadata(admission, plan, declaration, worker.RunId);
        if (diffBytes?.Length > EvidenceCanonicalJson.MaximumInputBytes) throw Failure();
        var copiedDiff = diffBytes?.ToArray();
        if (copiedDiff is null ? worker.Descriptor.DiffFile is not null || worker.Descriptor.DiffSha256 is not null
            : copiedDiff.Length > EvidenceCanonicalJson.MaximumInputBytes || worker.Descriptor.DiffFile is null
                || EvidenceDigest.Sha256(copiedDiff) != worker.Descriptor.DiffSha256)
            throw Failure();
        return new(worker, admission, plan, declaration, copiedDiff, outputQuota, execution, stageToken);
    }

    /// <summary>Checks complete plan, unique producer declaration and run identity without issuing a runtime lease.</summary>
    /// <remarks>Intentionally internal pure metadata control; passing it authenticates no supervisor or writer.</remarks>
    internal static void ValidateMetadata(EvidenceAdmissionResult admission, EvidencePlan plan,
        EvidenceProducerDeclaration declaration, string supervisorRunId)
    {
        try
        {
            if (admission is null || plan?.Profile?.Producers is null || declaration is null
                || string.IsNullOrWhiteSpace(supervisorRunId) || admission.RunId != supervisorRunId)
                throw Failure();
            admission.ValidateActive(plan);
            var selected = plan.Profile.Producers.Where(item => item is not null && item.Id == declaration.Id).ToArray();
            if (selected.Length != 1 || !EvidenceCanonicalJson.Serialize(selected[0]).AsSpan()
                .SequenceEqual(EvidenceCanonicalJson.Serialize(declaration)))
                throw Failure();
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or ArgumentException or NullReferenceException)
        {
            throw Failure();
        }
    }

    /// <summary>Claims once and tracks the entire fixed procedure, including transport, merge, gate and cleanup.</summary>
    /// <remarks>The internal procedure delegate is supplied by the Coverage assembly, never by the public factory.</remarks>
    internal Task<EvidenceProducerResult> RunAsync(EvidenceProducerContext context, EvidenceProducerDeclaration expected,
        Func<EvidenceRestrictedProducerLease, CancellationToken, Task<EvidenceProducerResult>> procedure,
        CancellationToken callerToken)
    {
        _attempt.Claim();
        try
        {
            if (context is null || expected is null || procedure is null
                || !EvidenceCanonicalJson.Serialize(context.Plan).AsSpan().SequenceEqual(_planBytes)
                || !EvidenceCanonicalJson.Serialize(context.Producer).AsSpan().SequenceEqual(_declarationBytes)
                || !EvidenceCanonicalJson.Serialize(expected).AsSpan().SequenceEqual(_declarationBytes))
                throw Failure();
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or ArgumentException or NullReferenceException)
        { throw Failure(); }
        ValidateCurrent();
        return TrackProcedureAsync(_execution, _stageToken, callerToken, async token =>
        {
            token.ThrowIfCancellationRequested();
            ValidateCurrent();
            var result = await procedure(this, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ValidateCurrent();
            return result;
        });
    }

    /// <summary>Registers the full asynchronous procedure before returning its task to the caller.</summary>
    /// <remarks>
    /// Even an ignored returned task remains joined by the existing lifecycle. Linked cancellation always
    /// includes the actual producer-stage token; this helper creates no timer or supervisor authority.
    /// </remarks>
    internal static Task<T> TrackProcedureAsync<T>(EvidenceWorkerExecution execution, CancellationToken stageToken,
        CancellationToken callerToken, Func<CancellationToken, Task<T>> procedure)
    {
        if (execution is null || procedure is null || !stageToken.CanBeCanceled) throw Failure();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(stageToken, callerToken);
        T result = default!;
        Task? tracked;
        try
        {
            tracked = execution.TrackOwnedWork(async token =>
            {
                token.ThrowIfCancellationRequested();
                result = await procedure(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }, linked.Token);
        }
        catch { linked.Dispose(); throw; }
        if (tracked is null) { linked.Dispose(); throw Failure(); }
        return AwaitTrackedAsync(tracked);

        async Task<T> AwaitTrackedAsync(Task owned)
        {
            try { await owned.ConfigureAwait(false); return result; }
            finally { linked.Dispose(); }
        }
    }

    private void ValidateCurrent()
    {
        _attempt.RequireOpen();
        _admission.ValidateActive(_plan);
        if (!Worker.IsArmed || Worker.RunId != _admission.RunId || _execution.IsAdmissionClosed || OutputQuota.IsFailed)
            throw Failure();
    }

    /// <summary>Closes this callback binding permanently; it cannot be rebound or retried.</summary>
    public void Dispose() => _attempt.Dispose();
    internal static EvidenceAdmissionException Failure() => new("ASEVD410", "Restricted producer binding is unavailable or closed.");
}

/// <summary>Pure atomic single-attempt state; it carries no worker, admission or execution capability.</summary>
internal sealed class EvidenceRestrictedProducerAttempt : IDisposable
{
    private int _state;
    /// <summary>Claims this attempt once, including attempts whose later metadata or execution fails.</summary>
    internal void Claim()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) throw EvidenceRestrictedProducerLease.Failure();
    }
    /// <summary>Rejects permanent callback closure without issuing or renewing any capability.</summary>
    internal void RequireOpen()
    {
        if (Volatile.Read(ref _state) == 2) throw EvidenceRestrictedProducerLease.Failure();
    }
    /// <summary>Closes this attempt permanently and idempotently.</summary>
    public void Dispose() => Interlocked.Exchange(ref _state, 2);
}
