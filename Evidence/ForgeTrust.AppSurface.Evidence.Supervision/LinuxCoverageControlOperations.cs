using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Owns the one root-selected coverage procedure and its retained report transfer.</summary>
/// <remarks>
/// Only native composition supplies the original input, accounts, workspace, producer and shared stream ledger.
/// Client command strings are comparisons against the fixed transport ABI, never commands to execute.
/// This owner retains a failed/ambiguous original run task until the producer's physical stop and pump joins.
/// </remarks>
internal sealed class LinuxCoverageControlOperations : IDisposable
{
    private readonly object _gate = new();
    private readonly EvidenceProtectedLaunchInput _input;
    private readonly LinuxOwnerActivation _owner;
    private readonly LinuxRunAccounts _accounts;
    private readonly LinuxRunWorkspace _workerWorkspace;
    private readonly LinuxProducerWorkspace _workspace;
    private readonly LinuxProducerProcess _producer;
    private Task<EvidenceRestrictedProcessResult>? _run;
    private LinuxProducerArtifactCustody? _reports;
    private string? _token;
    private bool _inventoryClaimed;
    private IReadOnlyList<LinuxRetainedReportSet.ReportInfo>? _inventory;
    private bool _failed;
    private bool _closed;
#if EVIDENCE_PRIVATE_C2_ARTIFACT_MUTATION
    private bool _c2MutationApplied;
    private bool _c2ReadRejectionWritten;
#endif

    private LinuxCoverageControlOperations(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workerWorkspace, LinuxProducerWorkspace workspace,
        LinuxProducerProcess producer)
    { _input = input; _owner = owner; _accounts = accounts; _workerWorkspace = workerWorkspace; _workspace = workspace; _producer = producer; }

    /// <summary>Creates the owner only from matching genuine native objects, before dispatch.</summary>
    internal static LinuxCoverageControlOperations Create(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxRunWorkspace workerWorkspace, LinuxProducerWorkspace workspace,
        SupervisionWorkRegistry ledger, SupervisionRunOutputBudget output, CancellationToken token)
    {
        _ = input.Request.ApplicationId is null ? EvidenceCoverageObservationPlan.FromInput(input, token) : EvidenceApplicationSelection.ResolvePlan(input, token);
        owner.RequireLaunchInput(input, token);
        workspace.RequireOwnedBy(owner, accounts, token);
        var producer = LinuxProducerProcess.Create(input, owner, accounts, workerWorkspace, workspace, ledger, output, token);
        return new(input, owner, accounts, workerWorkspace, workspace, producer);
    }

    /// <summary>Claims the entire actual producer task before invoking any start I/O; replay remains consumed.</summary>
    internal Task<EvidenceRestrictedProcessResult> RunAsync(EvidenceRunControlRequest request, CancellationToken token)
    {
        var dispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<EvidenceRestrictedProcessResult> run;
        lock (_gate)
        {
            RequireOpen();
            if (_run is not null) throw Rejected();
            // Consume before checking hostile metadata; malformed retries cannot reopen the attempt.
            _run = run = RunOwnedAsync(request, token, dispatch.Task);
        }
        dispatch.SetResult();
        return run;
    }

    private async Task<EvidenceRestrictedProcessResult> RunOwnedAsync(EvidenceRunControlRequest request,
        CancellationToken token, Task dispatch)
    {
        await dispatch.ConfigureAwait(false);
        try
        {
            _owner.RequireLaunchInput(_input, token);
            var selected = ValidateCommand(_input.Request, _workerWorkspace.RawResultsRoot, request);
            lock (_gate) { RequireOpen(); _token = selected; }
            return await _producer.RunAsync(selected, token).ConfigureAwait(false);
        }
        catch { lock (_gate) _failed = true; throw; }
    }

    /// <summary>Checks the fixed existing worker transport command without importing executable/argument authority.</summary>
    internal static string ValidateCommand(EvidenceSupervisorRequest selected, string virtualResultsRoot,
        EvidenceRunControlRequest request)
    {
        if (selected is null || request is null || selected.Solution is null
            || request.Executable != selected.RuntimeHost || request.WorkingDirectory != selected.SubjectRoot
            || request.Arguments.Count != 12) throw Rejected();
        var args = request.Arguments;
        var root = virtualResultsRoot + "/";
        if (!args[6].StartsWith(root, StringComparison.Ordinal)) throw Rejected();
        var token = args[6][root.Length..];
        RequireToken(token);
        string[] expected = ["test", selected.Solution, "--configuration", "Debug", "--collect:XPlat Code Coverage",
            "--results-directory", root + token, "--verbosity", "quiet", "-m:1", "-p:UseSharedCompilation=false", "-nodeReuse:false"];
        if (!args.SequenceEqual(expected, StringComparer.Ordinal)) throw Rejected();
        return token;
    }

    /// <summary>Validates the closed relative result token; separators, aliases and inherited paths cannot select a root.</summary>
    internal static void RequireToken(string token)
    {
        if (token is null || token.Length is < 1 or > 96
            || token.Any(static c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw Rejected();
    }

    /// <summary>Opens report custody once, only after the original producer's normal process/group/pump settlement.</summary>
    internal IReadOnlyList<LinuxRetainedReportSet.ReportInfo> Inventory(string token, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            RequireOpen();
            if (_inventoryClaimed || _run?.IsCompletedSuccessfully != true || token != _token) throw Rejected();
            _inventoryClaimed = true;
            try
            {
                _reports = LinuxProducerArtifactCustody.Capture(_input, _owner, _accounts, _workspace,
                    _producer, token, cancellationToken);
                _inventory = _reports.ListReports(cancellationToken);
#if EVIDENCE_PRIVATE_C2_ARTIFACT_MUTATION
                EmitC2(_reports.MutateRetainedReportForC2(cancellationToken));
                _c2MutationApplied = true;
#endif
                return _inventory;
            }
            catch { _failed = true; throw; }
        }
    }

    /// <summary>Serves only the previously inventoried token/path/next offset through the original retained handles.</summary>
    internal byte[] Read(EvidenceArtifactControlRequest request, CancellationToken token)
    {
        lock (_gate)
        {
            RequireOpen();
            if (!_inventoryClaimed || _reports is null || request.RelativeRoot != _token) throw Rejected();
            try { return _reports.ReadChunk(request.Path, request.Offset, 32 * 1024, token); }
            catch (Exception failure)
            {
                _failed = true;
#if EVIDENCE_PRIVATE_C2_ARTIFACT_MUTATION
                if (_c2MutationApplied && !_c2ReadRejectionWritten && failure is IOException && !token.IsCancellationRequested)
                {
                    _c2ReadRejectionWritten = true;
                    try { _owner.RequireActive(token); EmitC2(EvidenceCanonicalJson.Serialize(new
                        { schema = "evidence-c2-artifact-mutation-v1", phase = "read-rejected", returned_bytes = 0 })); }
                    catch (Exception error) when (SupervisionExceptionPolicy.IsRecoverable(error)) { }
                }
#else
                _ = failure;
#endif
                throw;
            }
        }
    }

    /// <summary>Compares the completed next offset to the original retained inventory, not supplied length data.</summary>
    internal bool IsEnd(string path, long nextOffset, CancellationToken token)
    {
        lock (_gate)
        {
            RequireOpen();
            _owner.RequireActive(token);
            var item = _inventory?.SingleOrDefault(item => item.RelativePath == path) ?? throw Rejected();
            if (nextOffset < 0 || nextOffset > item.Length) throw Rejected();
            return nextOffset == item.Length;
        }
    }

    /// <summary>Closes producer admission and joins the original native start/stop and paired pumps independently of handlers.</summary>
    internal async Task<SupervisionControlJoinFacts> StopAndJoinAsync(CancellationToken token)
    {
        Task<EvidenceRestrictedProcessResult>? run;
        lock (_gate) { _closed = true; run = _run; }
        await _producer.StopAndJoinAsync(token).ConfigureAwait(false);
        if (run is not null)
        {
            try { await run.ConfigureAwait(false); }
            catch { lock (_gate) _failed = true; }
        }
        _producer.RequirePhysicalSettlement(_owner, _accounts, _workspace, token);
        // An unsuccessful producer remains failed, but physical cleanup can still finish.
        return new(true, true, true);
    }

    /// <summary>Closes report readers after handlers and physical producer joins; never releases reusable identities.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
            _reports?.Dispose();
        }
    }

#if EVIDENCE_PRIVATE_C2_ARTIFACT_MUTATION
    private static void EmitC2(byte[] bytes)
    {
        if (bytes.Length > 4096) throw Rejected();
        using var error = Console.OpenStandardError();
        error.Write(bytes);
        error.Write("\n"u8);
        error.Flush();
    }
#endif

    private void RequireOpen()
    {
        if (_closed || _failed) throw Rejected();
    }
    private static EvidenceAdmissionException Rejected() => new("ASEVD410", "The protected coverage operation was rejected.");
}
