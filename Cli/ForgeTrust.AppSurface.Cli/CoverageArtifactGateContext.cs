namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Coordinates a gate's read roots and report-write destination before evaluation starts.</summary>
internal sealed class CoverageArtifactGateContext : IDisposable
{
    private readonly List<CoverageArtifactOperationLease> _leases = [];
    private readonly TextWriter _errors;
    private CoverageArtifactOperationLease? _outputLease;

    private CoverageArtifactGateContext(TextWriter errors) => _errors = errors;

    /// <summary>Acquires strongest modes in deterministic physical-object order, reusing an explicit nested session.</summary>
    /// <param name="request">Numeric gate request with owned input/report roots.</param>
    /// <param name="errors">Local diagnostics writer.</param>
    /// <param name="token">Caller cancellation before acquiring authority.</param>
    /// <returns>A disposable context retained through report publication and caller hashing.</returns>
    internal static Task<CoverageArtifactGateContext> AcquireAsync(CoverageGateRequest request, TextWriter errors, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var context = new CoverageArtifactGateContext(errors);
        if (request.ArtifactSession is not null)
        {
            request.ArtifactSession.VerifyBinding();
            return Task.FromResult(context);
        }
        try
        {
            var outputPath = Path.GetFullPath(request.OutputDirectory);
            using var output = CoverageRunOutputLease.AcquireArtifactDirectory(outputPath, true, false)
                ?? throw new IOException("Report output is missing.");
            var inputPath = CoverageArtifactSession.FindOwnedInputRoot(request.CoveragePath);
            var desired = new Dictionary<string, (string Path, bool Exclusive)>(StringComparer.Ordinal)
            { [output.ArtifactDirectoryIdentity] = (outputPath, true) };
            if (inputPath is not null)
            {
                using var input = CoverageRunOutputLease.AcquireArtifactDirectory(inputPath, false, false)
                    ?? throw new IOException("Gate input root disappeared.");
                if (!desired.ContainsKey(input.ArtifactDirectoryIdentity)) desired[input.ArtifactDirectoryIdentity] = (inputPath, false);
            }
            foreach (var item in desired.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var lease = CoverageArtifactOperationLease.Acquire(item.Value.Path, item.Value.Exclusive);
                context._leases.Add(lease);
                if (lease.PhysicalIdentity != item.Key)
                    throw new IOException("Gate root changed while acquiring its operation lease.");
                if (item.Key == output.ArtifactDirectoryIdentity) context._outputLease = lease;
            }
            return Task.FromResult(context);
        }
        catch { context.Dispose(); throw; }
    }

    /// <summary>Extends compatible current inventory with gate report bytes after publication.</summary>
    /// <param name="request">Original report destination.</param>
    /// <param name="result">Report paths actually published by this gate.</param>
    /// <param name="token">Caller cancellation.</param>
    internal async Task RecordReportsAsync(CoverageGateRequest request, CoverageGateResult result, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var paths = new List<string> { result.JsonReportPath, result.MarkdownReportPath };
        if (result.PatchTargetsJsonPath is not null) paths.Add(result.PatchTargetsJsonPath);
        if (result.PatchTargetsMarkdownPath is not null) paths.Add(result.PatchTargetsMarkdownPath);
        if (request.ArtifactSession is { } session)
        {
            await session.RecordPublishedArtifactsAsync(paths).ConfigureAwait(false);
            return;
        }
        await CoverageArtifactSession.UpdateRegisteredInventoryAsync(request.OutputDirectory, paths, _errors,
            _outputLease ?? throw new InvalidOperationException("Gate output lease is unavailable.")).ConfigureAwait(false);
    }

    /// <summary>Releases all acquired leases in reverse deterministic order.</summary>
    public void Dispose()
    {
        foreach (var lease in _leases.AsEnumerable().Reverse()) lease.Dispose();
        _leases.Clear();
    }

}
