namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Admits collector XML before normalization and supervises generated file growth until process completion.</summary>
/// <remarks>Only fan-in uses this wrapper; ordinary run/merge compatibility retains its existing policy.
/// The caller's deadline owns cancellation. Budget failure cancels and drains the exact inner process before returning.</remarks>
internal sealed class CoverageFanInNormalization(ICoverageRunReportGenerator inner, CoverageFanInBounds bounds, CoverageFanInBudget? budget = null)
    : ICoverageRunReportGenerator
{
    public Task<CoverageRunMergeResult> MergeAsync(IReadOnlyList<string> coverageFiles, string outputDirectory,
        CancellationToken cancellationToken) => MergeSupervisedAsync(coverageFiles, outputDirectory,
            CoverageRunProcessLease.Detached(), _ => { }, cancellationToken);

    public async Task<CoverageRunMergeResult> MergeSupervisedAsync(IReadOnlyList<string> coverageFiles,
        string outputDirectory, CoverageRunProcessLease lease, Action<int> outputObserver, CancellationToken cancellationToken)
    {
        using var admission = new CoverageFanInIO(bounds, cancellationToken, budget);
        admission.Acquire(coverageFiles.Select(path => (Path.GetDirectoryName(Path.GetFullPath(path))!, false))
            .Distinct(), requireCoordination: false);
        long inputBytes = 0;
        var observedOutputBytes = new Dictionary<(string Name, string Identity), long>();
        foreach (var path in coverageFiles)
        {
            var bytes = admission.Read(path, xml: true);
            _ = admission.Xml(bytes, allowInertGeneratorDoctype: true);
            inputBytes = checked(inputBytes + bytes.LongLength);
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var normalization = inner.MergeSupervisedAsync(coverageFiles, outputDirectory, lease, outputObserver, stop.Token);
        try
        {
            while (!normalization.IsCompleted)
            {
                CheckOutput(admission, outputDirectory, inputBytes, observedOutputBytes, cancellationToken);
                await Task.WhenAny(normalization, Task.Delay(25, cancellationToken)).ConfigureAwait(false);
            }
            var result = await normalization.ConfigureAwait(false);
            CheckOutput(admission, outputDirectory, inputBytes, observedOutputBytes, cancellationToken);
            admission.VerifyBindings();
            return result;
        }
        catch
        {
            stop.Cancel();
            try { await normalization.ConfigureAwait(false); }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { }
            throw;
        }
    }

    private void CheckOutput(CoverageFanInIO admission, string directory, long inputBytes,
        Dictionary<(string Name, string Identity), long> observedOutputBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(directory, false, false);
        if (root is null) return;
        var total = inputBytes;
        foreach (var entry in root.EnumerateArtifactEntries(32))
        {
            token.ThrowIfCancellationRequested();
            if (entry.IsDirectory) throw new InvalidDataException("Unexpected normalization output directory.");
            // This is a growth observation while ReportGenerator owns a writable handle.
            // Windows retained readers intentionally deny write sharing, so use metadata here;
            // authoritative capture later retains and hashes the completed regular file.
            var file = new FileInfo(Path.Join(directory, entry.Name));
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked normalization output is forbidden.");
            var maximum = entry.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? bounds.XmlBytes : bounds.JsonBytes;
            total = checked(total + file.Length);
            if (file.Length > maximum || total > bounds.TotalBytes)
                throw new InvalidDataException("Normalization output exceeds the declared file or staging budget.");
            var key = (entry.Name, entry.Identity);
            var observed = observedOutputBytes.GetValueOrDefault(key);
            if (file.Length > observed)
            {
                admission.ChargeGeneratedOutput(file.FullName, entry.Identity, file.Length - observed);
                observedOutputBytes[key] = file.Length;
            }
        }
        root.VerifyArtifactBinding();
    }
}
