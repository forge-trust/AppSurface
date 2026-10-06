using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Runs subject coverage exclusively through the credential-checked restricted broker.</summary>
/// <remarks>Both subject and protected reporter output charge the same host quota; the broker independently enforces its bound.</remarks>
internal sealed class EvidenceRestrictedCoverageTransport(EvidenceLinuxWorkerSupervisor worker,
    EvidenceRunByteQuota outputQuota) : IEvidenceRestrictedCoverageRun
{
    /// <inheritdoc />
    public async Task<EvidenceRestrictedProcessResult> RunAsync(string solutionPath, string resultsToken,
        CancellationToken cancellationToken)
    {
        var descriptor = worker.Descriptor;
        if (solutionPath != descriptor.Solution || !Path.IsPathFullyQualified(solutionPath)
            || !solutionPath.StartsWith(descriptor.SubjectRoot.TrimEnd('/') + "/", StringComparison.Ordinal)
            || resultsToken.Length is < 1 or > 96 || resultsToken.Any(static c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new EvidenceAdmissionException("ASEVD403", "Restricted coverage inputs do not match the protected descriptor.");
        if (outputQuota.IsFailed)
            throw new EvidenceAdmissionException("ASEVD420", "Run-wide process output admission is closed.");
        var result = await worker.RunSubjectAsync(descriptor.DotnetPath,
            ["test", solutionPath, "--configuration", "Debug", "--collect:XPlat Code Coverage",
                "--results-directory", Path.Join(descriptor.TestOutputRoot, resultsToken),
                "--verbosity", "quiet", "-m:1", "-p:UseSharedCompilation=false", "-nodeReuse:false"],
            descriptor.SubjectRoot, cancellationToken).ConfigureAwait(false);
        if (result.ReceivedBytes < 0 || !outputQuota.TryChargeReceived(result.ReceivedBytes))
            throw new EvidenceAdmissionException("ASEVD420", "Run-wide process output quota exceeded.");
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RestrictedCoverageReport>> CollectReportsAsync(string resultsToken,
        CancellationToken cancellationToken)
    {
        var artifacts = await worker.CollectArtifactsAsync(resultsToken, cancellationToken).ConfigureAwait(false);
        return artifacts.Select(static artifact => new RestrictedCoverageReport(artifact.RelativePath, artifact.Contents)).ToArray();
    }
}

/// <summary>Runs only the ReportGenerator entry shipped beneath the protected tool root, with no package-cache fallback.</summary>
/// <remarks>The independent worker service owns this trusted subprocess and its pumps. Its output shares the Evidence quota.</remarks>
internal sealed class EvidenceProtectedReportGenerator(EvidenceLinuxWorkerSupervisor worker,
    EvidenceRunByteQuota outputQuota) : ICoverageRunReportGenerator
{
    /// <inheritdoc />
    public async Task<CoverageRunMergeResult> MergeAsync(IReadOnlyList<string> coverageFiles, string outputDirectory,
        CancellationToken cancellationToken)
    {
        var reporter = Path.Join(worker.Descriptor.ToolRoot, "reportgenerator", "net10.0", "ReportGenerator.dll");
        if (!File.Exists(reporter))
            throw new EvidenceAdmissionException("ASEVD404", "The protected packaged coverage reporter is unavailable.");
        if (outputQuota.IsFailed || !worker.IsArmed)
            throw new EvidenceAdmissionException("ASEVD420", "Protected reporter output admission is closed.");
        var runner = new CliWrapCoverageRunProcessRunner();
        var result = await runner.RunAsync(new CoverageRunProcessRequest(worker.Descriptor.DotnetPath,
            [reporter, "-reports:" + string.Join(';', coverageFiles), "-targetdir:" + outputDirectory,
                "-reporttypes:Cobertura;TextSummary", "-verbosity:Error"],
            worker.Descriptor.ToolRoot, null, null, CoverageRunProcessLease.Detached(), outputQuota),
            cancellationToken).ConfigureAwait(false);
        if (result.OutputTruncated || outputQuota.IsFailed)
            throw new EvidenceAdmissionException("ASEVD420", "Protected reporter output exceeded its budget.");
        return new CoverageRunMergeResult(result.ExitCode, Path.Join(outputDirectory, "Cobertura.xml"),
            Path.Join(outputDirectory, "Summary.txt"));
    }
}
