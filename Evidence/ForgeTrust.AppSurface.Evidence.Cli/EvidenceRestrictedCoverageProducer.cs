using System.Diagnostics;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Evidence.Cli;

/// <summary>
/// Runs a declared coverage command through a protected restricted-process transport and returns bounded reports
/// only after that transport has confirmed the child process group and output pumps have exited.
/// </summary>
/// <remarks>
/// The transport binds <c>solutionPath</c> and <c>resultsToken</c> to the authenticated
/// worker descriptor's dotnet executable and test-output root. It must not accept subject-selected executable,
/// working-directory, or artifact-root authority. Returned report bytes remain hostile input and are checked again
/// by this adapter before any file-based coverage core receives them.
/// </remarks>
internal interface IEvidenceRestrictedCoverageRun
{
    /// <summary>
    /// Runs the fixed restricted coverage command for one solution and tokenized results directory.
    /// </summary>
    /// <param name="solutionPath">The validated solution selected by the protected worker descriptor.</param>
    /// <param name="resultsToken">A fresh single path segment beneath the descriptor's test-output root.</param>
    /// <param name="cancellationToken">The producer-stage cancellation token.</param>
    /// <returns>Bounded child output and exit status after child and output-pump exit are confirmed.</returns>
    Task<EvidenceRestrictedProcessResult> RunAsync(
        string solutionPath,
        string resultsToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// Collects only bounded Cobertura reports beneath the tokenized results directory after <see cref="RunAsync"/>
    /// has completed. The adapter validates every returned path and byte count again.
    /// </summary>
    /// <param name="resultsToken">The exact fresh token supplied to <see cref="RunAsync"/>.</param>
    /// <param name="cancellationToken">The producer-stage cancellation token.</param>
    /// <returns>Broker-retained report paths and bytes copied after the restricted child has exited.</returns>
    Task<IReadOnlyList<RestrictedCoverageReport>> CollectReportsAsync(
        string resultsToken,
        CancellationToken cancellationToken);
}

/// <summary>
/// One hostile Cobertura report copied from a broker-retained restricted artifact.
/// </summary>
/// <param name="RelativePath">Normalized path beneath the tokenized test-results directory.</param>
/// <param name="Contents">Report bytes, subject to the adapter's per-report and aggregate limits.</param>
internal sealed record RestrictedCoverageReport(string RelativePath, ReadOnlyMemory<byte> Contents);

/// <summary>
/// Produces coverage evidence from bounded Cobertura reports returned by a restricted subject process.
/// </summary>
/// <remarks>
/// This adapter never invokes <see cref="CoverageRunWorkflow"/> and never runs subject MSBuild in the trusted
/// worker. The subject process only produces reports; the trusted worker stages those reports under generated safe
/// names, calls the shared <see cref="CoverageMergeWorkflow"/>, and evaluates the merged report with
/// <see cref="CoverageGateEvaluator"/>. A passing subject exit code or text such as “tests passed” is not coverage
/// evidence. The emitted coverage assertion attests that this protected procedure ran and its declared numeric gate
/// passed; it does not authenticate the test source or independently establish application correctness.
/// </remarks>
internal sealed class EvidenceRestrictedCoverageProducer
{
    private const string SupportedProducerKind = "coverage";
    private const string SupportedAssertionId = "appsurface/coverage/behavioral-patch@1";
    private const string CoberturaFileName = "coverage.cobertura.xml";
    private const string StagingDirectoryPrefix = "appsurface-evidence-coverage-";
    private const int MaximumReportCount = 64;
    private const int MaximumReportBytes = 20 * 1024 * 1024;
    private const long MaximumAggregateReportBytes = 256L * 1024 * 1024;

    private static readonly XmlReaderSettings CoberturaReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaximumReportBytes,
    };

    private static readonly IReadOnlyDictionary<string, CoverageArtifactSource> ArtifactSources =
        new Dictionary<string, CoverageArtifactSource>(StringComparer.Ordinal)
        {
            ["coverage-report"] = new("merged", CoberturaFileName, RequiresPatchAnalysis: false),
            ["coverage-summary"] = new("gate", CoverageGateArtifactNames.Markdown, RequiresPatchAnalysis: false),
            ["coverage-gate"] = new("gate", CoverageGateArtifactNames.Json, RequiresPatchAnalysis: false),
            ["coverage-patch-targets"] = new("gate", CoverageGateArtifactNames.PatchTargetsJson, RequiresPatchAnalysis: true),
            ["coverage-patch-targets-report"] = new("gate", CoverageGateArtifactNames.PatchTargetsMarkdown, RequiresPatchAnalysis: true),
        };

    private readonly IEvidenceRestrictedCoverageRun _restrictedRun;
    private readonly CoverageMergeWorkflow _mergeWorkflow;

    /// <summary>
    /// Initializes the producer with the restricted transport and the shared Cobertura merge workflow.
    /// </summary>
    /// <param name="restrictedRun">Protected broker adapter for restricted execution and bounded artifact reads.</param>
    /// <param name="mergeWorkflow">Shared coverage merge workflow; it does not execute subject test projects.</param>
    public EvidenceRestrictedCoverageProducer(
        IEvidenceRestrictedCoverageRun restrictedRun,
        CoverageMergeWorkflow mergeWorkflow)
    {
        _restrictedRun = restrictedRun ?? throw new ArgumentNullException(nameof(restrictedRun));
        _mergeWorkflow = mergeWorkflow ?? throw new ArgumentNullException(nameof(mergeWorkflow));
    }

    /// <summary>
    /// Runs restricted coverage for one declared producer, merges the broker-returned reports, evaluates the exact
    /// declared gate, and writes only artifacts whose logical names are declared in the producer's slots.
    /// </summary>
    /// <param name="producer">The selected coverage declaration.</param>
    /// <param name="solutionPath">The protected descriptor's solution path; no solution is inferred.</param>
    /// <param name="diffSnapshot">The immutable planning snapshot, reused for any patch analysis.</param>
    /// <param name="writer">The Evidence writer bound to this declaration and its closed artifact slots.</param>
    /// <param name="cancellationToken">Cancellation for the producer stage.</param>
    /// <returns>A bounded result. Assertions are returned only when restricted execution and the numeric gate pass.</returns>
    /// <exception cref="OperationCanceledException">The producer-stage token is cancelled.</exception>
    public async Task<EvidenceProducerResult> RunAsync(
        EvidenceProducerDeclaration producer,
        string? solutionPath,
        EvidenceDiffSnapshot? diffSnapshot,
        EvidenceArtifactWriter writer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentNullException.ThrowIfNull(writer);

        if (!string.Equals(producer.Kind, SupportedProducerKind, StringComparison.Ordinal))
        {
            return CreateResult(producer, EvidenceProducerOutcome.Unavailable,
                "This protected adapter handles only the registered coverage producer kind.");
        }

        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            return CreateResult(producer, EvidenceProducerOutcome.Invalid,
                "The protected coverage producer requires an explicit solution path.");
        }

        if (producer.CoverageGate is not { } gate)
        {
            return CreateResult(producer, EvidenceProducerOutcome.Invalid,
                "Coverage producer declarations must include explicit coverageGate requirements.");
        }

        if ((gate.MinPatchLinePercent.HasValue || gate.MinPatchBranchPercent.HasValue) && diffSnapshot is null)
        {
            return CreateResult(producer, EvidenceProducerOutcome.Invalid,
                "The declared patch coverage gate requires the immutable planning diff snapshot.");
        }

        if (!IsValidDeclaration(producer, gate, out var declarationProblem))
        {
            return CreateResult(producer, EvidenceProducerOutcome.Invalid, declarationProblem!);
        }

        var started = Stopwatch.GetTimestamp();
        var resultsToken = "coverage-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        string? stagingRoot = null;
        var cleanupSucceeded = true;
        EvidenceProducerResult result;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var process = await _restrictedRun.RunAsync(solutionPath, resultsToken, cancellationToken).ConfigureAwait(false);
            if (process.OutputTruncated)
            {
                result = CreateResult(producer, EvidenceProducerOutcome.Failed,
                    "The restricted coverage process output exceeded its protected output quota.");
            }
            else if (process.ExitCode != 0)
            {
                result = CreateResult(producer, EvidenceProducerOutcome.Failed,
                    $"The restricted coverage process exited with code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}.");
            }
            else
            {
                var reports = await _restrictedRun.CollectReportsAsync(resultsToken, cancellationToken).ConfigureAwait(false);
                if (!TryValidateReports(reports, out var validatedReports, out var reportProblem))
                {
                    result = CreateResult(producer, EvidenceProducerOutcome.Invalid, reportProblem!);
                }
                else
                {
                    stagingRoot = CreateFreshStagingRoot();
                    result = await MergeAndGateAsync(
                        producer,
                        solutionPath,
                        diffSnapshot,
                        gate,
                        writer,
                        validatedReports!,
                        stagingRoot,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            result = CreateResult(producer, EvidenceProducerOutcome.Failed,
                $"The protected coverage procedure failed with {exception.GetType().Name}.", writer.WrittenArtifacts);
        }
        finally
        {
            if (stagingRoot is not null)
            {
                cleanupSucceeded = TryDeleteStagingRoot(stagingRoot);
            }
        }

        if (!cleanupSucceeded)
        {
            return CreateResult(producer, EvidenceProducerOutcome.Failed,
                "The protected coverage procedure could not remove its private staging directory.", writer.WrittenArtifacts)
                with { ElapsedMilliseconds = GetElapsedMilliseconds(started) };
        }

        return result with { ElapsedMilliseconds = GetElapsedMilliseconds(started) };
    }

    private async Task<EvidenceProducerResult> MergeAndGateAsync(
        EvidenceProducerDeclaration producer,
        string solutionPath,
        EvidenceDiffSnapshot? diffSnapshot,
        EvidenceCoverageGateRequirements requirements,
        EvidenceArtifactWriter writer,
        IReadOnlyList<RestrictedCoverageReport> reports,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        var inputDirectory = CreateStagingSubdirectory(stagingRoot, "reports");
        for (var index = 0; index < reports.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reportDirectory = CreateStagingSubdirectory(inputDirectory, (index + 1).ToString("D3", CultureInfo.InvariantCulture));
            var reportPath = Path.Join(reportDirectory, CoberturaFileName);
            await using var reportStream = new FileStream(
                reportPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await reportStream.WriteAsync(reports[index].Contents, cancellationToken).ConfigureAwait(false);
        }

        var mergeDirectory = Path.Join(stagingRoot, "merged");
        var merge = await _mergeWorkflow.MergeAsync(
            new CoverageMergeRequest(inputDirectory, mergeDirectory, Clean: true),
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null),
            cancellationToken).ConfigureAwait(false);

        var gateDirectory = Path.Join(stagingRoot, "gate");
        Directory.CreateDirectory(gateDirectory);
        EnsurePrivateDirectory(gateDirectory);
        var patchCoverage = CreatePatchRequest(solutionPath, diffSnapshot, requirements);
        var gateRequest = new CoverageGateRequest(
            merge.CoveragePath,
            gateDirectory,
            requirements.MinLinePercent,
            requirements.MinBranchPercent,
            patchCoverage,
            requirements.TolerancePercent);
        var gateResult = await CoverageGateEvaluator.EvaluateAsync(gateRequest, cancellationToken).ConfigureAwait(false);
        await CoverageGateReportWriter.WriteAsync(gateResult, gateRequest, cancellationToken).ConfigureAwait(false);

        var artifactError = await WriteDeclaredArtifactsAsync(
            producer,
            writer,
            stagingRoot,
            gateResult.PatchAnalysis is not null,
            cancellationToken).ConfigureAwait(false);
        if (artifactError is not null)
        {
            return CreateResult(producer, EvidenceProducerOutcome.Invalid, artifactError, writer.WrittenArtifacts);
        }

        return gateResult.Passed
            ? CreateResult(producer, EvidenceProducerOutcome.Passed,
                $"Coverage gate passed (line {gateResult.LineCoverage.Percent:0.00}%, branch {gateResult.BranchCoverage.Percent:0.00}%).",
                writer.WrittenArtifacts,
                [SupportedAssertionId])
            : CreateResult(producer, EvidenceProducerOutcome.Failed,
                $"Coverage gate failed (line {gateResult.LineCoverage.Percent:0.00}%, branch {gateResult.BranchCoverage.Percent:0.00}%).",
                writer.WrittenArtifacts);
    }

    private async Task<string?> WriteDeclaredArtifactsAsync(
        EvidenceProducerDeclaration producer,
        EvidenceArtifactWriter writer,
        string stagingRoot,
        bool patchAnalysisAvailable,
        CancellationToken cancellationToken)
    {
        var pending = new List<PendingCoverageArtifact>();
        long totalBytes = 0;
        foreach (var slot in producer.ArtifactSlots)
        {
            if (!ArtifactSources.TryGetValue(slot.LogicalName, out var source))
            {
                continue; // Unknown optional slots are not produced by this closed adapter.
            }

            if (source.RequiresPatchAnalysis && !patchAnalysisAvailable)
            {
                if (slot.Required)
                {
                    return $"Required artifact slot '{slot.LogicalName}' needs patch analysis, but no patch report was produced.";
                }

                continue;
            }

            var sourceDirectory = Path.Join(stagingRoot, source.DirectoryName);
            var sourcePath = Path.Join(sourceDirectory, source.FileName);
            if (!File.Exists(sourcePath))
            {
                if (slot.Required)
                {
                    return $"Required artifact slot '{slot.LogicalName}' has no corresponding protected coverage output.";
                }

                continue;
            }

            var relativePath = EvidenceArtifactValidation.NormalizeRelativePath(
                slot.RelativeRoot + "/" + source.FileName);
            EvidenceArtifactValidation.ValidatePathForSlot(slot, relativePath);
            var remainingTotal = EvidenceArtifactWriter.MaximumTotalArtifactBytes - totalBytes;
            if (slot.MaximumBytes <= 0 || remainingTotal <= 0)
            {
                return $"Declared artifact slot '{slot.LogicalName}' has no remaining bounded capacity.";
            }

            var maximumBytes = Math.Min(slot.MaximumBytes, remainingTotal);
            var contents = await ReadPrivateArtifactAsync(sourcePath, maximumBytes, cancellationToken).ConfigureAwait(false);
            if (contents is null)
            {
                if (slot.Required)
                {
                    return $"Required artifact slot '{slot.LogicalName}' exceeds its declared byte limit.";
                }

                continue;
            }

            totalBytes = checked(totalBytes + contents.Length);
            pending.Add(new PendingCoverageArtifact(slot.LogicalName, relativePath, contents));
        }

        foreach (var artifact in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteAsync(artifact.LogicalName, artifact.RelativePath, artifact.Contents, cancellationToken)
                .ConfigureAwait(false);
        }

        return null;
    }

    private static CoveragePatchRequest? CreatePatchRequest(
        string solutionPath,
        EvidenceDiffSnapshot? diffSnapshot,
        EvidenceCoverageGateRequirements requirements)
    {
        if (diffSnapshot is null)
        {
            return null;
        }

        var solutionDirectory = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var repositoryRoot = GitRepositoryRootResolver.FindRepositoryRoot(solutionDirectory);
        return new CoveragePatchRequest(
            repositoryRoot,
            PatchDiffSource.ForSnapshot(
                diffSnapshot.Bytes.ToArray(),
                diffSnapshot.Label,
                MaximumReportBytes,
                diffSnapshot.Sha256),
            requirements.MinPatchLinePercent,
            requirements.MinPatchBranchPercent,
            string.Equals(requirements.PatchLineMode, "codecov", StringComparison.OrdinalIgnoreCase)
                ? PatchLineMode.Codecov
                : PatchLineMode.Measurable);
    }

    private static bool IsValidDeclaration(
        EvidenceProducerDeclaration producer,
        EvidenceCoverageGateRequirements gate,
        out string? problem)
    {
        if (producer.AssertionIds is null
            || producer.AssertionIds.Count != 1
            || !string.Equals(producer.AssertionIds[0], SupportedAssertionId, StringComparison.Ordinal))
        {
            problem = "The protected coverage producer supports only the behavioral coverage assertion.";
            return false;
        }

        if (!string.Equals(producer.Version, "1.0.0", StringComparison.Ordinal))
        {
            problem = "The protected coverage producer supports only declaration version 1.0.0.";
            return false;
        }

        if (!CoverageGateEvaluator.IsPercentInRange(gate.MinLinePercent)
            || !CoverageGateEvaluator.IsPercentInRange(gate.MinBranchPercent)
            || !CoverageGateEvaluator.IsPercentInRange(gate.TolerancePercent)
            || (gate.MinPatchLinePercent is { } patchLine && !CoverageGateEvaluator.IsPercentInRange(patchLine))
            || (gate.MinPatchBranchPercent is { } patchBranch && !CoverageGateEvaluator.IsPercentInRange(patchBranch))
            || !(string.Equals(gate.PatchLineMode, "measurable", StringComparison.OrdinalIgnoreCase)
                || string.Equals(gate.PatchLineMode, "codecov", StringComparison.OrdinalIgnoreCase)))
        {
            problem = "The coverage gate declaration contains an unsupported threshold or patch-line mode.";
            return false;
        }

        if (producer.ArtifactSlots is null)
        {
            problem = "Coverage producer artifact slots are missing.";
            return false;
        }

        var logicalNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in producer.ArtifactSlots)
        {
            if (slot is null
                || string.IsNullOrWhiteSpace(slot.LogicalName)
                || string.IsNullOrWhiteSpace(slot.RelativeRoot)
                || string.IsNullOrWhiteSpace(slot.MediaType)
                || slot.MaximumBytes < 0
                || slot.MaximumBytes > EvidenceArtifactWriter.MaximumTotalArtifactBytes
                || !logicalNames.Add(slot.LogicalName))
            {
                problem = "Coverage producer artifact slots must have unique names and valid bounded roots.";
                return false;
            }

            if (slot.Required && !ArtifactSources.ContainsKey(slot.LogicalName))
            {
                problem = $"Required artifact slot '{slot.LogicalName}' is not produced by the protected coverage adapter.";
                return false;
            }

            if (slot.Required
                && ArtifactSources.TryGetValue(slot.LogicalName, out var source)
                && source.RequiresPatchAnalysis
                && !gate.MinPatchLinePercent.HasValue
                && !gate.MinPatchBranchPercent.HasValue)
            {
                problem = $"Required artifact slot '{slot.LogicalName}' needs a declared patch gate.";
                return false;
            }

            try
            {
                _ = EvidenceArtifactValidation.NormalizeRelativePath(slot.RelativeRoot);
            }
            catch (ArgumentException)
            {
                problem = $"Artifact slot '{slot.LogicalName}' has an unsafe relative root.";
                return false;
            }
        }

        problem = null;
        return true;
    }

    private static bool TryValidateReports(
        IReadOnlyList<RestrictedCoverageReport> reports,
        out IReadOnlyList<RestrictedCoverageReport>? validated,
        out string? problem)
    {
        validated = null;
        if (reports is null || reports.Count == 0)
        {
            problem = "The restricted coverage process produced no Cobertura reports.";
            return false;
        }

        if (reports.Count > MaximumReportCount)
        {
            problem = "The restricted coverage report count exceeds the adapter limit.";
            return false;
        }

        long totalBytes = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<RestrictedCoverageReport>(reports.Count);
        foreach (var report in reports)
        {
            if (report is null || string.IsNullOrWhiteSpace(report.RelativePath) || report.RelativePath.Length > 4096)
            {
                problem = "A restricted coverage report has invalid path metadata.";
                return false;
            }

            string normalizedPath;
            try
            {
                normalizedPath = EvidenceArtifactValidation.NormalizeRelativePath(report.RelativePath);
            }
            catch (ArgumentException)
            {
                problem = "A restricted coverage report path is unsafe.";
                return false;
            }

            if (!string.Equals(normalizedPath, report.RelativePath, StringComparison.Ordinal)
                || !string.Equals(normalizedPath.Split('/')[^1], CoberturaFileName, StringComparison.Ordinal)
                || !paths.Add(normalizedPath))
            {
                problem = "Restricted reports must have unique normalized Cobertura paths.";
                return false;
            }

            var length = report.Contents.Length;
            if (length is <= 0 or > MaximumReportBytes
                || length > MaximumAggregateReportBytes - totalBytes)
            {
                problem = "Restricted coverage reports exceed the per-report or aggregate byte limit.";
                return false;
            }

            totalBytes += length;
            ordered.Add(report);
        }

        foreach (var report in ordered)
        {
            if (!IsCoberturaXml(report.Contents))
            {
                problem = "A restricted coverage report is malformed or is not Cobertura XML.";
                return false;
            }
        }

        validated = ordered.OrderBy(static report => report.RelativePath, StringComparer.Ordinal).ToArray();
        problem = null;
        return true;
    }

    private static bool IsCoberturaXml(ReadOnlyMemory<byte> contents)
    {
        try
        {
            using var stream = new MemoryStream(contents.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, CoberturaReaderSettings);
            var root = XDocument.Load(reader).Root;
            return root is not null && string.Equals(root.Name.LocalName, "coverage", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is XmlException or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string CreateFreshStagingRoot()
    {
        var directory = Directory.CreateTempSubdirectory(StagingDirectoryPrefix);
        var path = Path.GetFullPath(directory.FullName);
        try
        {
            var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(Path.GetDirectoryName(path), temporaryRoot, comparison)
                || !Path.GetFileName(path).StartsWith(StagingDirectoryPrefix, StringComparison.Ordinal))
            {
                throw new IOException("The fresh coverage staging directory is outside the trusted temporary root.");
            }

            EnsurePrivateDirectory(path);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var mode = File.GetUnixFileMode(path);
                if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                {
                    throw new IOException("The coverage staging directory is accessible outside its owner.");
                }
            }

            return path;
        }
        catch
        {
            _ = TryDeleteStagingRoot(path);
            throw;
        }
    }

    private static string CreateStagingSubdirectory(string parent, string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Contains(Path.DirectorySeparatorChar)
            || name.Contains(Path.AltDirectorySeparatorChar)
            || name is "." or "..")
        {
            throw new InvalidOperationException("A protected staging directory name is invalid.");
        }

        var path = Path.Join(parent, name);
        Directory.CreateDirectory(path);
        EnsurePrivateDirectory(path);
        return path;
    }

    private static void EnsurePrivateDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("A protected coverage staging path is not an ordinary directory.");
        }
    }

    private static async Task<byte[]?> ReadPrivateArtifactAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0 || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("A protected coverage output is not an ordinary file.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > maximumBytes || stream.Length > int.MaxValue)
        {
            return null;
        }

        var contents = new byte[(int)stream.Length];
        var offset = 0;
        while (offset < contents.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(contents.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("A protected coverage output changed while it was being read.");
            }

            offset += read;
        }

        if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new IOException("A protected coverage output changed while it was being read.");
        }

        return contents;
    }

    private static bool TryDeleteStagingRoot(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(Path.GetDirectoryName(fullPath), expectedParent, comparison)
                || !Path.GetFileName(fullPath).StartsWith(StagingDirectoryPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            if (!Directory.Exists(fullPath))
            {
                return true;
            }

            EnsurePrivateDirectory(fullPath);
            Directory.Delete(fullPath, recursive: true);
            return !Directory.Exists(fullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static EvidenceProducerResult CreateResult(
        EvidenceProducerDeclaration producer,
        EvidenceProducerOutcome outcome,
        string diagnostic,
        IReadOnlyList<EvidenceArtifactResult>? artifacts = null,
        IReadOnlyList<string>? assertions = null)
        => new(producer.Id, outcome, assertions ?? [], diagnostic, artifacts);

    private static long GetElapsedMilliseconds(long started)
        => (long)Math.Max(0, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    private static bool IsNonFatal(Exception exception)
        => exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException
            and not AppDomainUnloadedException;

    private sealed record CoverageArtifactSource(string DirectoryName, string FileName, bool RequiresPatchAnalysis);

    private sealed record PendingCoverageArtifact(string LogicalName, string RelativePath, byte[] Contents);
}
