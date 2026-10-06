using System.Reflection;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Actually retained protected request and deployment, before unit or admission construction.</summary>
/// <remarks>
/// The private native factory checks root Linux ownership and binds the currently executing runtime/entry.
/// It does not prove pre-CLR environment sanitation, owner activation, accounts or consumer acceptance.
/// The caller must finish those checks and arm external termination before starting any worker.
/// Readers/start operations must be joined before disposal; this object cannot be reconstructed from JSON.
/// </remarks>
internal sealed class EvidenceProtectedLaunchInput : IDisposable
{
    private readonly List<LinuxProtectedNode> _requestNodes;
    private readonly LinuxProtectedNode _requestFile;
    private readonly string _requestHash;
    private readonly LinuxProtectedDeployment _tool;
    private readonly LinuxProtectedDeployment _runtime;
    private readonly long _startedAt;
    private readonly TimeSpan _allowance;
    private int _closed;
    private int _expired;

    private EvidenceProtectedLaunchInput(string requestPath, EvidenceSupervisorRequest request, List<LinuxProtectedNode> requestNodes,
        LinuxProtectedNode requestFile, LinuxProtectedDeployment tool, LinuxProtectedDeployment runtime,
        long startedAt, TimeSpan allowance, string requestHash, string entryHash, string policyHash)
    {
        RequestPath = requestPath; Request = request; _requestNodes = requestNodes; _requestFile = requestFile; _requestHash = requestHash;
        _tool = tool; _runtime = runtime; _startedAt = startedAt; _allowance = allowance;
        EntrySha256 = entryHash; PolicySha256 = policyHash;
    }

    /// <summary>Gets copied launch metadata; it remains data, not worker admission.</summary>
    internal EvidenceSupervisorRequest Request { get; }
    /// <summary>Gets the actual retained request pathname, for binding the owner's fixed command.</summary>
    internal string RequestPath { get; }
    /// <summary>Gets SHA-256 measured from the retained managed entry in the complete tool inventory.</summary>
    internal string EntrySha256 { get; }
    /// <summary>Gets SHA-256 measured from the retained protected policy file.</summary>
    internal string PolicySha256 { get; }

    /// <summary>Gets the original monotonic remainder; expiry remains latched and cannot be reset by retry.</summary>
    internal TimeSpan Remaining
    {
        get
        {
            if (Volatile.Read(ref _closed) != 0 || Volatile.Read(ref _expired) != 0)
                throw LinuxProtectedDeployment.InvalidDeployment();
            var remaining = _allowance - TimeProvider.System.GetElapsedTime(_startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                Interlocked.Exchange(ref _expired, 1);
                throw LinuxProtectedDeployment.InvalidDeployment();
            }
            return remaining;
        }
    }

    /// <summary>Opens the real protected request and full deployment trees, binding the executing image.</summary>
    /// <param name="requestPath">Canonical absolute protected request, at most 64 KiB with root-only write ownership.</param>
    /// <param name="token">Existing external owner/start deadline; cancellation never returns an ownership receipt.</param>
    /// <returns>Retained native inputs only; supervisor unit/bootstrap checks remain mandatory.</returns>
    internal static EvidenceProtectedLaunchInput Open(string requestPath, CancellationToken token)
    {
        var clock = TimeProvider.System;
        var startedAt = clock.GetTimestamp();
        var startedUtc = clock.GetUtcNow();
        var nodes = new List<LinuxProtectedNode>();
        LinuxProtectedDeployment? tool = null;
        LinuxProtectedDeployment? runtime = null;
        try
        {
            var file = LinuxProtectedNode.OpenAbsolute(requestPath, false, false,
                EvidenceSupervisorRequest.MaximumRequestBytes, nodes, token);
            var bytes = file.ReadRequest(token);
            var requestHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            var request = EvidenceSupervisorRequest.Parse(bytes);
            var allowance = request.JobDeadlineUtc - startedUtc;
            if (allowance <= TimeSpan.Zero || allowance > TimeSpan.FromHours(1)
                || clock.GetElapsedTime(startedAt) >= allowance) throw LinuxProtectedDeployment.InvalidDeployment();
            if (Environment.ProcessPath != request.RuntimeHost || Assembly.GetEntryAssembly()?.Location != request.EntryPath)
                throw LinuxProtectedDeployment.InvalidDeployment();
            var deadline = new LinuxInputDeadline(startedAt, allowance);
            deadline.Check(token);
            tool = LinuxProtectedDeployment.Open(request.ToolRoot, token, deadline);
            runtime = request.RuntimeRoot == request.ToolRoot ? tool : LinuxProtectedDeployment.Open(request.RuntimeRoot, token, deadline);
            _ = runtime.RequireFile(Relative(request.RuntimeRoot, request.RuntimeHost), executable: true);
            var entryHash = tool.RequireFile(Relative(request.ToolRoot, request.EntryPath));
            var policyHash = tool.RequireFile(Relative(request.ToolRoot, request.PolicyFile));
            if (request.DiffFile is not null && tool.RequireFile(Relative(request.ToolRoot, request.DiffFile)) != request.DiffSha256)
                throw LinuxProtectedDeployment.InvalidDeployment();
            var result = new EvidenceProtectedLaunchInput(requestPath, request, nodes, file, tool, runtime,
                startedAt, allowance, requestHash, entryHash, policyHash);
            result.Recheck(token);
            tool = runtime = null; // Ownership transfers only after every final recheck.
            return result;
        }
        catch (OperationCanceledException) { runtime?.Dispose(); tool?.Dispose(); LinuxProtectedDeployment.Close(nodes); throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException
            or ArgumentException or EvidenceAdmissionException)
        {
            runtime?.Dispose(); tool?.Dispose(); LinuxProtectedDeployment.Close(nodes);
            throw LinuxProtectedDeployment.InvalidDeployment();
        }
    }

    /// <summary>Rechecks request/deployment names, metadata and bytes plus the original final deadline.</summary>
    internal void Recheck(CancellationToken token)
    {
        _ = Remaining;
        foreach (var node in _requestNodes) { token.ThrowIfCancellationRequested(); _ = Remaining; node.Recheck(); }
        if (_requestFile.Hash(token, new LinuxInputDeadline(_startedAt, _allowance)) != _requestHash) throw LinuxProtectedDeployment.InvalidDeployment();
        _tool.Recheck(token);
        if (!ReferenceEquals(_tool, _runtime)) _runtime.Recheck(token);
        token.ThrowIfCancellationRequested();
        _ = Remaining;
    }

    /// <summary>Reads copied, counted bytes of only the root-selected retained policy, bound to its measured SHA.</summary>
    /// <remarks>No subject policy is imported and no planner/admission capability is constructed.</remarks>
    internal byte[] ReadPolicyBytes(CancellationToken token) =>
        ReadSelectedBytes(Request.PolicyFile, PolicySha256, token);

    /// <summary>Reads only the retained protected diff when declared; null remains absence, not an empty fabricated diff.</summary>
    internal byte[]? ReadDiffBytes(CancellationToken token) => Request.DiffFile is null
        ? null : ReadSelectedBytes(Request.DiffFile, Request.DiffSha256!, token);

    private byte[] ReadSelectedBytes(string path, string digest, CancellationToken token)
    {
        Recheck(token);
        var bytes = _tool.ReadFile(Relative(Request.ToolRoot, path), token, new LinuxInputDeadline(_startedAt, _allowance));
        if (EvidenceDigest.Sha256(bytes) != digest) throw LinuxProtectedDeployment.InvalidDeployment();
        Recheck(token);
        return bytes;
    }

    /// <summary>Closes inputs after all readers and accepted/pending unit-start operations have joined.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _runtime.Dispose();
        if (!ReferenceEquals(_tool, _runtime)) _tool.Dispose();
        LinuxProtectedDeployment.Close(_requestNodes);
    }

    private static string Relative(string root, string path)
    {
        if (!path.StartsWith(root + "/", StringComparison.Ordinal)) throw LinuxProtectedDeployment.InvalidDeployment();
        return path[(root.Length + 1)..];
    }
}
