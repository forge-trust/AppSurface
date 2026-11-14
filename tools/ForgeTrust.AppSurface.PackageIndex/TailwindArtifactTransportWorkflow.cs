using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>Inputs for one exact-ID GitHub Actions artifact transport attempt.</summary>
/// <param name="RepositoryRoot">Source-stamped checkout used for validation and relative path resolution.</param>
/// <param name="Expected">Independent protected producer identity.</param>
/// <param name="ReceiptPath">Protected producer receipt file.</param>
/// <param name="ExpectedSubjectSha256">Trusted digest of the Tailwind producer subject.</param>
/// <param name="WorkDirectory">Fresh private scratch directory on the bundle destination volume.</param>
/// <param name="ArtifactsOutput">Fresh bundle destination.</param>
/// <param name="ReportDirectory">Fresh directory for safe diagnostics and the final marker.</param>
/// <param name="Token">Explicit GH_TOKEN value; never logged or serialized.</param>
internal sealed record TailwindArtifactTransportRequest(
    string RepositoryRoot,
    ArtifactExpectedContext Expected,
    string ReceiptPath,
    string ExpectedSubjectSha256,
    string WorkDirectory,
    string ArtifactsOutput,
    string ReportDirectory,
    string Token)
{
    /// <summary>Returns a redacted representation suitable for accidental diagnostic use.</summary>
    public override string ToString()
        => $"{nameof(TailwindArtifactTransportRequest)} {{ RepositoryRoot = [redacted], Expected = [identity], ReceiptPath = [redacted], ExpectedSubjectSha256 = [digest], WorkDirectory = [redacted], ArtifactsOutput = [redacted], ReportDirectory = [redacted], Token = [redacted] }}";
}

/// <summary>Safe outcome of the adapter; only a successful result plus its final record is eligible for consumption.</summary>
internal sealed record ArtifactTransportResult(bool Succeeded, string Status, string? Code, string? NextAction, bool ReportWritten);

/// <summary>Narrow filesystem boundary used only to exercise owned-output failures deterministically.</summary>
internal sealed record TailwindArtifactTransportFileSystem(
    Action<string> CreateDirectory,
    Action<string, string> MoveDirectory,
    Action<string, bool> DeleteDirectory,
    Action<string> DeleteFile,
    Func<string, object, CancellationToken, Task> WriteJson,
    Func<string, string, CancellationToken, Task> WriteText)
{
    internal static TailwindArtifactTransportFileSystem Real { get; } = new(
        path => Directory.CreateDirectory(path),
        Directory.Move,
        (path, recursive) => Directory.Delete(path, recursive),
        File.Delete,
        async (path, value, token) => await TailwindEvidenceWorkflow.WriteAtomicCreateNewJsonAsync(path, value, token),
        async (path, value, token) =>
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(value), token);
        });
}

/// <summary>Immutable resource limits; the injectable constructor seam is internal and production defaults are fixed.</summary>
/// <param name="MaximumEntries">Maximum actual ZIP entries before extraction.</param>
/// <param name="MaximumExpandedBytes">Maximum actual expanded bytes written to staging.</param>
/// <param name="OverallBudget">Single adapter deadline covering transport through marker installation.</param>
internal sealed record TailwindArtifactTransportLimits(int MaximumEntries, long MaximumExpandedBytes, TimeSpan OverallBudget)
{
    internal static TailwindArtifactTransportLimits Production { get; } = new(100_000, 4L * 1024 * 1024 * 1024, TimeSpan.FromMinutes(5));
}

/// <summary>
/// Verifies the exact producer archive, extracts it under a portable path policy, reuses the real Tailwind binding
/// validator, atomically installs the bundle, removes owned scratch, and publishes the success marker last.
///
/// State flow: owned scratch ZIP -> bounded extraction stage -> real content binding -> same-volume move -> reports
/// -> scratch removal -> final marker. The marker is the only success authority exposed to the caller.
/// </summary>
internal sealed class TailwindArtifactTransportWorkflow : IDisposable
{
    private const string MarkerSchema = "appsurface-github-actions-verified-artifact-v1";
    private static readonly IReadOnlyDictionary<string, string> NextActions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["artifact-input-invalid"] = "Correct the named argument or GH_TOKEN binding without printing secrets; ask the maintainer before a new full rehearsal.",
        ["artifact-receipt-invalid"] = "Repair protected producer receipt emission/schema; never edit artifact-contained data to manufacture authority; ask the maintainer before a new full rehearsal.",
        ["artifact-receipt-binding-mismatch"] = "Investigate the named protected receipt/context field; do not substitute an ID; ask the maintainer before a new full rehearsal.",
        ["artifact-attempt-mismatch"] = "Ask the maintainer to authorize a full rerun; re-run all jobs so the producer emits a receipt for the current attempt. Failed-jobs-only reruns retain the older receipt and will refuse again.",
        ["artifact-repository-mismatch"] = "Stop and verify protected repository authority; do not use a cross-repository fallback.",
        ["artifact-run-mismatch"] = "Stop and verify protected run authority; do not select a latest run.",
        ["artifact-workflow-mismatch"] = "Stop and verify protected workflow path/event; do not relax the manual-main policy.",
        ["artifact-source-mismatch"] = "Restore/build the exact authorized source and investigate conflicting metadata; never relabel a package.",
        ["artifact-not-found-or-inaccessible"] = "Ask the maintainer to establish access and retention for the exact ID; do not choose another candidate.",
        ["artifact-expired"] = "Stop and ask the maintainer for a new authorized full rehearsal; do not use a different artifact under the old receipt.",
        ["artifact-permission-denied"] = "Ask the maintainer to verify GH_TOKEN wiring and read permissions; never print credentials or broaden them automatically.",
        ["artifact-rate-limited"] = "Ask the maintainer to establish that GitHub's limit has cleared and authorize a full rerun; re-run all jobs so the producer emits a receipt for the current attempt.",
        ["artifact-api-unavailable"] = "Ask the maintainer to confirm API/network recovery and reconcile local output, then authorize a full rerun; re-run all jobs so the producer emits a receipt for the current attempt.",
        ["artifact-api-response-invalid"] = "Preserve safe metadata context and ask the maintainer to investigate the pinned API contract.",
        ["artifact-metadata-digest-mismatch"] = "Stop and investigate the original upload/API digest authority; do not rewrite the digest or repack under this identity.",
        ["artifact-bytes-digest-mismatch"] = "Stop and investigate transfer integrity using safe identity/digest evidence; do not extract or select a replacement.",
        ["artifact-download-failed"] = "Establish transfer recovery and reconcile owned scratch, then authorize a full rerun; re-run all jobs so the producer emits a receipt for the current attempt.",
        ["artifact-redirect-invalid"] = "Stop and investigate the exact artifact download path using safe stage/status only; never log its signed URL.",
        ["artifact-budget-exceeded"] = "Inspect the named resource limit; do not raise it ad hoc. If a new rehearsal is authorized, re-run all jobs so the producer emits a receipt for the current attempt.",
        ["artifact-archive-unsafe"] = "Stop and ask the maintainer to investigate the protected archive structure; do not rewrite paths or repack under this identity.",
        ["artifact-producer-binding-invalid"] = "Preserve safe binding evidence and ask the maintainer to investigate subject/manifest/package identity.",
        ["artifact-output-failed"] = "Reconcile invocation-owned partial output and repair disk, access, or same-volume configuration before any authorized fresh run.",
        ["artifact-cancelled"] = "Respect caller cancellation and reconcile output eligibility before asking the maintainer about another full rehearsal."
    };

    private readonly GitHubActionsArtifactVerifier _verifier;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, string, CancellationToken, Task> _sourceIdentity;
    private readonly TailwindArtifactTransportFileSystem _fileSystem;
    private readonly bool _ownsVerifier;
    private readonly TailwindArtifactTransportLimits _limits;
    private bool _disposed;

    /// <summary>Creates the production adapter or injects narrow deterministic test boundaries.</summary>
    /// <param name="verifier">Optional direct GitHub Actions artifact verifier.</param>
    /// <param name="timeProvider">Clock for the shared transport and extraction deadline.</param>
    /// <param name="sourceIdentity">Source check; production always uses the stamped checkout validator.</param>
    /// <param name="fileSystem">Owned-path operations; production uses real filesystem calls.</param>
    /// <param name="limits">Internal deterministic resource limits; omit this argument for fixed production limits.</param>
    internal TailwindArtifactTransportWorkflow(GitHubActionsArtifactVerifier? verifier = null,
        TimeProvider? timeProvider = null,
        Func<string, string, CancellationToken, Task>? sourceIdentity = null,
        TailwindArtifactTransportFileSystem? fileSystem = null,
        TailwindArtifactTransportLimits? limits = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ownsVerifier = verifier is null;
        _verifier = verifier ?? GitHubActionsArtifactVerifier.CreateProduction(_timeProvider);
        _sourceIdentity = sourceIdentity ?? TailwindSourceIdentity.RequireAsync;
        _fileSystem = fileSystem ?? TailwindArtifactTransportFileSystem.Real;
        _limits = limits ?? TailwindArtifactTransportLimits.Production;
        if (_limits.MaximumEntries <= 0 || _limits.MaximumExpandedBytes <= 0 || _limits.OverallBudget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    /// <summary>Runs one bounded exact-ID transport attempt and returns a safe result without writing console output.</summary>
    /// <param name="request">Protected expected identity, receipt, explicit token, and fresh output locations.</param>
    /// <param name="cancellationToken">Caller cancellation, preserved separately from the adapter deadline.</param>
    /// <returns>Success only after the final verified marker is installed.</returns>
    internal async Task<ArtifactTransportResult> RunAsync(TailwindArtifactTransportRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string? ownedWork = null;
        string? ownedOutput = null;
        string? ownedReport = null;
        string? archivePath = null;
        var reportReady = false;
        var ownedDirectories = new List<string>();
        var ownedReportFiles = new List<string>();
        var stage = "arguments";
        ArtifactTransportException? failure = null;
        CancellationTokenSource? overallDeadline = null;
        try
        {
            ValidateRequest(request);
            GitHubActionsArtifactVerifier.ValidateExpected(request.Expected);
            var work = Path.GetFullPath(request.WorkDirectory);
            var output = Path.GetFullPath(request.ArtifactsOutput);
            var report = Path.GetFullPath(request.ReportDirectory);
            ValidatePaths(request, work, output, report);
            overallDeadline = new CancellationTokenSource(_limits.OverallBudget, _timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, overallDeadline.Token);
            var token = linked.Token;

            CreateFreshOwnedDirectory(report, ownedDirectories, path => ownedReport = path);

            stage = "source-validation";
            try
            {
                await _sourceIdentity(request.RepositoryRoot, request.Expected.SourceCommit, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (PackageIndexException exception)
            {
                throw Failure("artifact-source-mismatch", stage, causeKind: SafeCause(exception));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                throw Failure("artifact-output-failed", stage, causeKind: "unexpected-local");
            }
            EnsureNotExpired(overallDeadline, cancellationToken);

            CreateFreshOwnedDirectory(work, ownedDirectories, path => ownedWork = path);
            stage = "artifact-verification";
            var verified = await _verifier.VerifyAsync(request.Expected, request.ReceiptPath, request.Token, work, token);
            archivePath = verified.ArchivePath;
            EnsureNotExpired(overallDeadline, cancellationToken);

            var staging = Path.Combine(work, "bundle-stage");
            CreateFreshOwnedDirectory(staging, ownedDirectories, _ => { });
            stage = "archive-preflight";
            PreflightZip(archivePath, _limits.MaximumEntries, token);
            stage = "extraction";
            ExtractArchive(archivePath, staging, token, overallDeadline, cancellationToken, _limits.MaximumEntries, _limits.MaximumExpandedBytes);
            EnsureNotExpired(overallDeadline, cancellationToken);

            stage = "producer-binding";
            var options = CreateBindingOptions(request, work, report);
            TailwindProducerBinding binding;
            try
            {
                binding = await TailwindEvidenceWorkflow.ValidateProducerBindingAsync(request.RepositoryRoot, staging,
                    Path.Combine(staging, "package-artifact-manifest.json"), options, token, BindingSourceIdentityAsync);
            }
            catch (OperationCanceledException) when (overallDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw Failure("artifact-budget-exceeded", stage, field: "overall-time");
            }
            catch (OperationCanceledException) { throw; }
            catch (ArtifactTransportException) { throw; }
            catch (Exception exception) when (exception is PackageIndexException or JsonException or InvalidDataException)
            {
                throw Failure("artifact-producer-binding-invalid", stage, causeKind: SafeCause(exception));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                throw Failure("artifact-output-failed", stage, causeKind: "unexpected-local");
            }
            EnsureNotExpired(overallDeadline, cancellationToken);
            if (!string.Equals(binding.Subject.ProducerAttempt, request.Expected.RunAttempt, StringComparison.Ordinal))
            {
                stage = "subject-validation";
                throw Failure("artifact-attempt-mismatch", "subject-validation", field: "runAttempt",
                    expected: request.Expected.RunAttempt, observed: binding.Subject.ProducerAttempt);
            }

            stage = "bundle-installation";
            CreateMissingDirectories(Path.GetDirectoryName(output)!, ownedDirectories);
            _fileSystem.MoveDirectory(staging, output);
            ownedOutput = output;
            EnsureNotExpired(overallDeadline, cancellationToken);

            stage = "report-writing";
            var markerPath = Path.Combine(report, "verified-artifact.json");
            var identity = new
            {
                schema = MarkerSchema,
                repository = verified.Receipt.Repository,
                repositoryId = verified.Receipt.RepositoryId,
                workflowPath = verified.Receipt.WorkflowPath,
                @event = verified.Receipt.Event,
                producerJob = verified.Receipt.ProducerJob,
                runId = verified.Receipt.RunId,
                runAttempt = verified.Receipt.RunAttempt,
                sourceCommit = verified.Receipt.SourceCommit,
                artifactId = verified.Receipt.ArtifactId,
                archiveSha256 = verified.Receipt.ArchiveSha256,
                archiveBytes = verified.ArchiveBytes,
                status = "succeeded",
                releaseEligible = false
            };
            var successSummary = "Verified producer artifact installed. Release eligible: false.";
            await WriteReportAsync(report, "succeeded", "complete", [], SafeSuccessContext() + "; " + successSummary, token, ownedReportFiles);
            EnsureNotExpired(overallDeadline, cancellationToken);

            stage = "scratch-cleanup";
            DeleteOwnedDirectory(ownedWork);
            ownedWork = null;
            CleanupEmptyOwnedDirectories(ownedDirectories, preserve: [report, output]);
            ownedOutput = output;
            EnsureNotExpired(overallDeadline, cancellationToken);

            stage = "success-marker";
            await _fileSystem.WriteJson(markerPath, identity, token);
            EnsureNotExpired(overallDeadline, cancellationToken);
            reportReady = true;
            overallDeadline.Dispose();
            return new ArtifactTransportResult(true, "succeeded", null, null, true);
        }
        catch (ArtifactTransportException exception)
        {
            failure = exception.Code == "artifact-cancelled" && !cancellationToken.IsCancellationRequested
                && overallDeadline?.IsCancellationRequested == true
                    ? Failure("artifact-budget-exceeded", exception.Operation, field: "overall-time")
                    : exception;
        }
        catch (OperationCanceledException)
        {
            failure = cancellationToken.IsCancellationRequested
                ? Failure("artifact-cancelled", stage)
                : Failure("artifact-budget-exceeded", stage, field: "overall-time");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            failure = Failure("artifact-output-failed", stage, causeKind: SafeCause(exception));
        }

        BestEffortDeleteFile(archivePath);
        BestEffortDeleteDirectory(ownedWork);
        BestEffortDeleteDirectory(ownedOutput);
        if (ownedReport is not null) BestEffortDeleteFile(Path.Combine(ownedReport, "verified-artifact.json"));
        foreach (var path in ownedReportFiles) BestEffortDeleteFile(path);
        foreach (var path in ownedDirectories.AsEnumerable().Reverse()) BestEffortDeleteDirectory(path, recursive: false);
        if (ownedReport is not null)
        {
            reportReady = await TryWriteFailureReportAsync(ownedReport, failure!, stage, ownedReportFiles);
        }
        overallDeadline?.Dispose();
        return new ArtifactTransportResult(false, failure!.Code == "artifact-cancelled" ? "cancelled" : "failed",
            failure!.Code, NextActions.GetValueOrDefault(failure.Code), reportReady);
    }

    private static void ValidateRequest(TailwindArtifactTransportRequest request)
    {
        if (request is null) throw Failure("artifact-input-invalid", "arguments");
        if (string.IsNullOrWhiteSpace(request.RepositoryRoot) || string.IsNullOrWhiteSpace(request.ReceiptPath)
            || string.IsNullOrWhiteSpace(request.ExpectedSubjectSha256) || string.IsNullOrWhiteSpace(request.WorkDirectory)
            || string.IsNullOrWhiteSpace(request.ArtifactsOutput) || string.IsNullOrWhiteSpace(request.ReportDirectory)
            || string.IsNullOrWhiteSpace(request.Token) || request.Token.Any(char.IsWhiteSpace)
            || request.Token.Any(char.IsControl) || request.Expected is null)
            throw Failure("artifact-input-invalid", "arguments");
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.ExpectedSubjectSha256, "^[0-9a-f]{64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw Failure("artifact-input-invalid", "arguments", field: "--expected-subject-sha256");
    }

    private static TailwindCommandOptions CreateBindingOptions(TailwindArtifactTransportRequest request, string work, string report)
        => TailwindCommandOptions.Extract([
            "--producer-subject", TailwindProofSubjectService.FileName,
            "--producer-artifact-id", request.Expected.ArtifactId,
            "--expected-subject-sha256", request.ExpectedSubjectSha256,
            "--repository-id", request.Expected.RepositoryId,
            "--producer-run-id", request.Expected.RunId,
            "--producer-attempt", request.Expected.RunAttempt,
            "--source-commit", request.Expected.SourceCommit,
            "--work-directory", work,
            "--report-directory", report
        ]);

    private static void ValidatePaths(TailwindArtifactTransportRequest request, string work, string output, string report)
    {
        var receipt = Path.GetFullPath(request.ReceiptPath);
        var receiptDirectory = Path.GetDirectoryName(receipt)!;
        var targets = new[] { work, output, report };
        if (targets.Distinct(PathComparer).Count() != targets.Length
            || targets.SelectMany((left, i) => targets.Skip(i + 1).Select(right => (left, right))).Any(pair => Overlap(pair.left, pair.right)))
            throw Failure("artifact-input-invalid", "path-validation", field: "output-overlap");
        if (targets.Any(path => Overlap(path, receipt)))
            throw Failure("artifact-input-invalid", "path-validation", field: "receipt-overlap");
        if (targets.Any(path => Overlap(path, receiptDirectory)))
            throw Failure("artifact-input-invalid", "path-validation", field: "receipt-directory-overlap");
        foreach (var target in targets)
        {
            if (Directory.Exists(target) || File.Exists(target))
                throw Failure("artifact-input-invalid", "path-validation", field: "fresh-output-required");
            EnsureNoLinkedAncestors(target);
            var relative = Path.GetRelativePath(Path.GetFullPath(request.RepositoryRoot), target);
            if (!relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && relative != ".." && !Path.IsPathRooted(relative) && IsWatched(relative))
                throw Failure("artifact-input-invalid", "path-validation", field: "watched-source-output");
        }
        EnsureNoLinkedAncestors(receipt);
    }

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static bool Overlap(string a, string b) => IsWithin(a, b) || IsWithin(b, a);
    private static bool IsWithin(string parent, string child)
    {
        var relative = Path.GetRelativePath(parent, child);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static bool IsWatched(string relative)
    {
        var normalized = relative.Replace(Path.DirectorySeparatorChar, '/');
        string[] watched = ["tools/ForgeTrust.AppSurface.PackageIndex", "tools/ForgeTrust.AppSurface.ReleaseContracts",
            "Web/ForgeTrust.AppSurface.Web.Tailwind", "packages/package-index.yml", "scripts/verify-tailwind-package-consumer.sh",
            "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "NuGet.package-gate.config", "NuGet.config", "global.json"];
        return watched.Any(path => normalized.Equals(path, StringComparison.Ordinal) || normalized.StartsWith(path + "/", StringComparison.Ordinal));
    }

    private static void EnsureNoLinkedAncestors(string path)
    {
        var cursor = Path.GetFullPath(path);
        while (true)
        {
            if (File.Exists(cursor) || Directory.Exists(cursor))
            {
                var info = Directory.Exists(cursor) ? (FileSystemInfo)new DirectoryInfo(cursor) : new FileInfo(cursor);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw Failure("artifact-input-invalid", "path-validation", field: "linked-ancestor");
            }
            var parent = Path.GetDirectoryName(cursor);
            if (parent is null || parent == cursor) break;
            cursor = parent;
        }
    }

    private void CreateFreshOwnedDirectory(string path, List<string> ownedDirectories, Action<string> track)
    {
        if (Directory.Exists(path) || File.Exists(path)) throw Failure("artifact-input-invalid", "path-validation", field: "fresh-output-required");
        CreateMissingDirectories(path, ownedDirectories);
        track(Path.GetFullPath(path));
    }

    private void CreateMissingDirectories(string path, List<string> ownedDirectories)
    {
        if (File.Exists(path)) throw Failure("artifact-input-invalid", "path-validation", field: "output-parent-is-file");
        var missing = new Stack<string>();
        var cursor = Path.GetFullPath(path);
        while (!Directory.Exists(cursor))
        {
            missing.Push(cursor);
            cursor = Path.GetDirectoryName(cursor) ?? throw Failure("artifact-output-failed", "directory-create");
        }
        while (missing.Count > 0)
        {
            var next = missing.Pop();
            _fileSystem.CreateDirectory(next);
            ownedDirectories.Add(next);
        }
    }

    private void DeleteOwnedDirectory(string? path)
    {
        if (path is not null && Directory.Exists(path)) _fileSystem.DeleteDirectory(path, true);
    }

    private void BestEffortDeleteFile(string? path) { try { if (path is not null && File.Exists(path)) _fileSystem.DeleteFile(path); } catch { } }
    private void BestEffortDeleteDirectory(string? path, bool recursive = true) { try { if (path is not null && Directory.Exists(path)) _fileSystem.DeleteDirectory(path, recursive); } catch { } }

    private void CleanupEmptyOwnedDirectories(IEnumerable<string> paths, IReadOnlyList<string> preserve)
    {
        foreach (var path in paths.Reverse())
        {
            if (preserve.Any(item => IsWithin(path, item) || IsWithin(item, path))) continue;
            BestEffortDeleteDirectory(path, recursive: false);
        }
    }

    private static void EnsureNotExpired(CancellationTokenSource deadline, CancellationToken caller)
    {
        if (deadline.IsCancellationRequested && !caller.IsCancellationRequested)
            throw Failure("artifact-budget-exceeded", "overall-deadline", field: "overall-time");
        caller.ThrowIfCancellationRequested();
    }

    private static void PreflightZip(string archivePath, int maximumEntries, CancellationToken token)
    {
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 22) throw Failure("artifact-archive-unsafe", "zip-envelope");
        var tailLength = (int)Math.Min(stream.Length, 22 + ushort.MaxValue + 20L);
        var tail = new byte[tailLength];
        stream.Position = stream.Length - tailLength;
        stream.ReadExactly(tail);
        var eocd = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i, 4)) == 0x06054b50 && i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20, 2)) == tail.Length) { eocd = i; break; }
        if (eocd < 0) throw Failure("artifact-archive-unsafe", "zip-envelope");
        var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 20, 2));
        if (eocd + 22 + commentLength != tail.Length) throw Failure("artifact-archive-unsafe", "zip-envelope");
        var disk = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 4, 2));
        var directoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 6, 2));
        var onDisk = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 8, 2));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10, 2));
        var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12, 4));
        var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16, 4));
        if (disk != 0 || directoryDisk != 0 || onDisk != count) throw Failure("artifact-archive-unsafe", "zip-envelope");
        ulong entries = count;
        ulong size = directorySize;
        ulong offset = directoryOffset;
        ulong directoryEndLimit = (ulong)(stream.Length - tailLength + eocd);
        if (count == ushort.MaxValue || directorySize == uint.MaxValue || directoryOffset == uint.MaxValue)
        {
            var locator = eocd - 20;
            if (locator < 0 || BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(locator, 4)) != 0x07064b50) throw Failure("artifact-archive-unsafe", "zip64-envelope");
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(locator + 4, 4)) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(locator + 16, 4)) != 1) throw Failure("artifact-archive-unsafe", "zip64-envelope");
            var recordOffset = BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(locator + 8, 8));
            var absoluteLocator = (ulong)(stream.Length - tailLength + locator);
            if (recordOffset > (ulong)stream.Length - Math.Min(56UL, (ulong)stream.Length)
                || recordOffset > absoluteLocator || absoluteLocator - recordOffset < 56)
                throw Failure("artifact-archive-unsafe", "zip64-envelope");
            stream.Position = (long)recordOffset;
            Span<byte> record = stackalloc byte[56];
            stream.ReadExactly(record);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x06064b50
                || BinaryPrimitives.ReadUInt64LittleEndian(record[4..]) < 44
                || BinaryPrimitives.ReadUInt32LittleEndian(record[16..]) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(record[20..]) != 0)
                throw Failure("artifact-archive-unsafe", "zip64-envelope");
            var recordBytes = BinaryPrimitives.ReadUInt64LittleEndian(record[4..]);
            if (recordBytes > absoluteLocator - recordOffset - 12)
                throw Failure("artifact-archive-unsafe", "zip64-envelope");
            var onDisk64 = BinaryPrimitives.ReadUInt64LittleEndian(record[24..]);
            entries = BinaryPrimitives.ReadUInt64LittleEndian(record[32..]);
            size = BinaryPrimitives.ReadUInt64LittleEndian(record[40..]);
            offset = BinaryPrimitives.ReadUInt64LittleEndian(record[48..]);
            if (onDisk64 != entries) throw Failure("artifact-archive-unsafe", "zip64-envelope");
            directoryEndLimit = recordOffset;
        }
        if (entries > (ulong)maximumEntries) throw Failure("artifact-budget-exceeded", "zip-envelope", field: "entry-count");
        if (offset > ulong.MaxValue - size || offset > (ulong)stream.Length || size > (ulong)stream.Length
            || offset + size > directoryEndLimit)
            throw Failure("artifact-archive-unsafe", "zip-envelope");

        // Count fixed central-directory envelopes before ZipArchive materializes entries. A false
        // EOCD count must not bypass the allocation bound; entry payload parsing remains the BCL's job.
        stream.Position = (long)offset;
        var directoryEnd = offset + size;
        ulong actualEntries = 0;
        Span<byte> header = stackalloc byte[46];
        while ((ulong)stream.Position < directoryEnd)
        {
            token.ThrowIfCancellationRequested();
            if (directoryEnd - (ulong)stream.Position < (ulong)header.Length)
                throw Failure("artifact-archive-unsafe", "zip-envelope");
            stream.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50)
                throw Failure("artifact-archive-unsafe", "zip-envelope");
            if (++actualEntries > (ulong)maximumEntries)
                throw Failure("artifact-budget-exceeded", "zip-envelope", field: "entry-count");
            var variableBytes = (ulong)BinaryPrimitives.ReadUInt16LittleEndian(header[28..])
                + BinaryPrimitives.ReadUInt16LittleEndian(header[30..])
                + BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            if (variableBytes > directoryEnd - (ulong)stream.Position)
                throw Failure("artifact-archive-unsafe", "zip-envelope");
            stream.Position += (long)variableBytes;
        }
        if (actualEntries != entries)
            throw Failure("artifact-archive-unsafe", "zip-envelope");
    }

    private static void ExtractArchive(string archivePath, string staging, CancellationToken token,
        CancellationTokenSource overall, CancellationToken caller, int maximumEntries, long maximumExpandedBytes)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count > maximumEntries) throw Failure("artifact-budget-exceeded", "extraction", field: "entry-count");
            var paths = new Dictionary<string, (bool Directory, string Spelling)>(StringComparer.OrdinalIgnoreCase);
            var explicitPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            var actualEntries = 0;
            foreach (var entry in archive.Entries)
            {
                EnsureNotExpired(overall, caller);
                token.ThrowIfCancellationRequested();
                if (++actualEntries > maximumEntries) throw Failure("artifact-budget-exceeded", "extraction", field: "entry-count");
                var (relative, isDirectory) = ValidateEntryName(entry.FullName);
                var key = relative.Replace('/', Path.DirectorySeparatorChar);
                RegisterPath(paths, explicitPaths, relative, isDirectory, token);
                var destination = Path.GetFullPath(Path.Combine(staging, key));
                if (!IsWithin(staging, destination) || Path.GetRelativePath(staging, destination).Replace(Path.DirectorySeparatorChar, '/') != relative)
                    throw Failure("artifact-archive-unsafe", "extraction", field: "entry-name");
                if (IsUnixLink(entry)) throw Failure("artifact-archive-unsafe", "extraction", field: "entry-type");
                if (isDirectory)
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var source = entry.Open();
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920];
                while (true)
                {
                    EnsureNotExpired(overall, caller);
                    var read = source.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    total = checked(total + read);
                    if (total > maximumExpandedBytes) throw Failure("artifact-budget-exceeded", "extraction", field: "expanded-bytes");
                    output.Write(buffer, 0, read);
                }
            }
        }
        catch (ArtifactTransportException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (InvalidDataException exception)
        {
            throw Failure("artifact-archive-unsafe", "extraction", causeKind: SafeCause(exception));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Failure("artifact-output-failed", "extraction", causeKind: SafeCause(exception));
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw Failure("artifact-archive-unsafe", "extraction", causeKind: SafeCause(exception));
        }
    }

    private static (string Relative, bool IsDirectory) ValidateEntryName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('\\') || name.Contains('\0') || name.Contains(':') || name.StartsWith("/", StringComparison.Ordinal))
            throw Failure("artifact-archive-unsafe", "extraction", field: "entry-name");
        var directory = name.EndsWith("/", StringComparison.Ordinal);
        var relative = directory ? name[..^1] : name;
        if (relative.Length is 0 or > 4096 || !relative.IsNormalized(NormalizationForm.FormC)) throw Failure("artifact-archive-unsafe", "extraction", field: "entry-name");
        var segments = relative.Split('/');
        if (segments.Length > 128) throw Failure("artifact-archive-unsafe", "extraction", field: "entry-name");
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.'))
                throw Failure("artifact-archive-unsafe", "extraction", field: "entry-name");
            if (segment.Any(ch => ch < 32 || "<>\"|?*".Contains(ch)) || IsReservedWindowsName(segment))
                throw Failure("artifact-archive-unsafe", "extraction", field: "entry-name");
        }
        return (relative, directory);
    }

    private static bool IsReservedWindowsName(string segment)
    {
        var baseName = segment.Split('.')[0];
        return baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) || baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) || baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (baseName.Length == 4 && (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && baseName[3] is >= '1' and <= '9');
    }

    private static bool IsUnixLink(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        var dosAttributes = entry.ExternalAttributes & 0xFFFF;
        if ((dosAttributes & (int)FileAttributes.ReparsePoint) != 0) return true;
        return unixMode != 0 && unixMode is not (0x8000 or 0x4000);
    }

    private static void RegisterPath(Dictionary<string, (bool Directory, string Spelling)> paths, HashSet<string> explicitPaths, string relative, bool directory, CancellationToken token)
    {
        var parts = relative.Split('/');
        for (var i = 1; i < parts.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var ancestor = string.Join('/', parts.Take(i));
            if (paths.TryGetValue(ancestor, out var prior))
            {
                if (!prior.Directory) throw Failure("artifact-archive-unsafe", "extraction", field: "file-directory-collision");
                if (prior.Spelling != ancestor) throw Failure("artifact-archive-unsafe", "extraction", field: "duplicate-entry");
            }
            paths.TryAdd(ancestor, (true, ancestor));
        }
        if (paths.TryGetValue(relative, out var existing))
        {
            if (existing.Directory != directory || existing.Spelling != relative || !directory || explicitPaths.Contains(relative))
                throw Failure("artifact-archive-unsafe", "extraction", field: "duplicate-entry");
            explicitPaths.Add(relative);
            return;
        }
        // Every descendant registers its ancestors above, so the existing-key check already
        // rejects replacing an inferred directory with a file without scanning all paths.
        paths.Add(relative, (directory, relative));
        explicitPaths.Add(relative);
    }

    private async Task WriteReportAsync(string directory, string status, string stage,
        IReadOnlyList<TailwindDiagnosticError> errors, string summary, CancellationToken token, List<string>? ownedFiles = null)
    {
        var diagnostics = new { schema = "appsurface-tailwind-diagnostic-v1", status, stage = "artifact-transport", releaseEligible = false, errors, errorsTruncated = false };
        var jsonPath = Path.Combine(directory, "diagnostics.json");
        var summaryPath = Path.Combine(directory, "summary.md");
        await _fileSystem.WriteJson(jsonPath, diagnostics, token);
        ownedFiles?.Add(jsonPath);
        await _fileSystem.WriteText(summaryPath, $"# Tailwind artifact transport\n\nStatus: **{status}**\n\nStage: `artifact-transport`\n\nPhase: `{SafeAtom(stage)}`\n\nRelease eligible: `false`\n\n{summary}\n", token);
        ownedFiles?.Add(summaryPath);
    }

    private async Task<bool> TryWriteFailureReportAsync(string directory, ArtifactTransportException failure, string stage, List<string> ownedFiles)
    {
        try
        {
            foreach (var path in ownedFiles.ToArray()) BestEffortDeleteFile(path);
            ownedFiles.Clear();
            if (!Directory.Exists(directory))
            {
                var cleanup = new List<string>();
                CreateFreshOwnedDirectory(directory, cleanup, _ => { });
            }
            var url = $"https://github.com/forge-trust/AppSurface/blob/main/docs/tailwind-artifact-provenance.md#{failure.Code}";
            var message = SafeDiagnosticContext(failure, stage);
            var error = new TailwindDiagnosticError(failure.Code, message, failure.Expected, failure.Observed,
                NextActions.GetValueOrDefault(failure.Code) ?? "Ask the maintainer to inspect the safe report before another rehearsal.", url);
            await WriteReportAsync(directory, failure.Code == "artifact-cancelled" ? "cancelled" : "failed",
                stage, [error], message, CancellationToken.None, ownedFiles);
            return true;
        }
        catch { return false; }
    }

    private async Task BindingSourceIdentityAsync(string repositoryRoot, string sourceCommit, CancellationToken token)
    {
        try { await _sourceIdentity(repositoryRoot, sourceCommit, token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            throw Failure("artifact-source-mismatch", "source-validation", causeKind: SafeCause(exception));
        }
    }

    private static ArtifactTransportException Failure(string code, string operation, string? field = null,
        string? expected = null, string? observed = null, string? causeKind = null)
        => new(code, operation, field, expected, observed, causeKind: causeKind);

    private static string SafeCause(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "access-denied",
        DirectoryNotFoundException => "directory-missing",
        FileNotFoundException => "file-missing",
        IOException => "io-failure",
        InvalidDataException => "invalid-data",
        OperationCanceledException => "cancelled",
        ArgumentException => "invalid-argument",
        _ => "unexpected-local"
    };

    private static string SafeMessage(string code) => code switch
    {
        "artifact-input-invalid" => "Transport arguments or owned paths violate the required contract.",
        "artifact-attempt-mismatch" => "The producer receipt, API, or subject is from a different workflow attempt.",
        "artifact-archive-unsafe" => "The archive is corrupt or violates the supported extraction policy.",
        "artifact-producer-binding-invalid" => "The extracted subject, manifest, or package binding failed validation.",
        "artifact-output-failed" => "A required owned output could not be installed or recorded.",
        "artifact-budget-exceeded" => "The transport exceeded a documented time or size bound.",
        _ => "The exact producer artifact could not be verified safely."
    };

    private static string SafeDiagnosticContext(ArtifactTransportException failure, string stage)
    {
        var parts = new List<string>
        {
            SafeMessage(failure.Code),
            $"operation={SafeAtom(failure.Operation)}",
            $"stage={SafeAtom(stage)}",
            "apiVersion=2026-03-10",
            $"verifierSource={VerifierSourceStamp()}"
        };
        if (failure.StatusCode is { } statusCode) parts.Add($"httpStatus={statusCode}");
        if (failure.Field is { } field) parts.Add($"field={SafeAtom(field)}");
        if (failure.CauseKind is { } cause) parts.Add($"cause={SafeAtom(cause)}");
        if (failure.Expected is { } expected) parts.Add($"expected={SafeIdentity(failure.Field, expected)}");
        if (failure.Observed is { } observed) parts.Add($"observed={SafeIdentity(failure.Field, observed)}");
        if (failure.CauseKind == "unexpected-local")
            parts.Add("frames=TailwindArtifactTransportWorkflow.RunAsync -> TailwindArtifactTransportWorkflow");
        return string.Join("; ", parts);
    }

    private static string SafeSuccessContext()
        => $"operation=complete; phase=complete; apiVersion=2026-03-10; verifierSource={VerifierSourceStamp()}";

    private static string SafeAtom(string value)
        => System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9._:/-]{1,128}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            ? value : "[redacted]";

    private static string SafeIdentity(string? field, string value)
    {
        var pattern = field switch
        {
            "runAttempt" or "runId" or "artifactId" or "repositoryId" => "^[1-9][0-9]{0,19}$",
            "sourceCommit" or "head_sha" => "^[0-9a-f]{40}$",
            "archiveSha256" or "digest" => "^(sha256:)?[0-9a-f]{64}$",
            "repository" => "^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$",
            _ => null
        };
        return pattern is not null && System.Text.RegularExpressions.Regex.IsMatch(value, pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            ? value : "[redacted]";
    }

    private static string VerifierSourceStamp()
    {
        var informational = typeof(TailwindArtifactTransportWorkflow).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        var stamp = informational?.Split('+', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return stamp is not null && System.Text.RegularExpressions.Regex.IsMatch(stamp, "^[0-9a-fA-F]{40}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            ? stamp.ToLowerInvariant() : "unavailable";
    }

    /// <summary>Disposes the production verifier only when this adapter created it.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsVerifier) _verifier.Dispose();
    }

}
