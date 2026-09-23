using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>One package file prepared for the final push boundary.</summary>
internal sealed record TailwindPreparedPackage(string PackageId, string PackageVersion, string ArtifactFileName, string PackageSha512, string Path);

/// <summary>Validated identity and exact package file inventory for a release publish.</summary>
internal sealed record PreparedTailwindPublication(string PackageVersion, string AggregateSha256, string NativeInvocationId, IReadOnlyList<TailwindPreparedPackage> Packages);
internal sealed record TailwindEvidenceCommandResult(bool Succeeded, string Status, string ReportPath);
internal sealed record TailwindHostArtifact(string Rid, string ArtifactId, string Directory);
internal sealed record TailwindProducerBinding(TailwindPublicationRequest Request, TailwindProofSubject Subject);
internal sealed record TailwindDiagnosticError(string Code, string Message, string? Expected, string? Observed, string NextAction, string DocsUrl, string? RelativeEvidencePath = null);

/// <summary>Typed verifier for native Tailwind release evidence and publication inputs.</summary>
internal static class TailwindEvidenceWorkflow
{
    private static readonly string[] RequiredRids = ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64"];
    private const string AggregateFile = "tailwind-native-aggregate.json";
    private const string StartReceiptFile = "publication-start-receipt.json";
    private const string DiagnosticSchema = "appsurface-tailwind-diagnostic-v1";
    private const string DiagnosticDocsUrl = "https://github.com/forge-trust/AppSurface/issues/798";

    internal static async Task<TailwindProducerBinding> ValidateProducerBindingAsync(
        string repositoryRoot, string artifactsInputPath, string artifactManifestPath,
        TailwindCommandOptions options, CancellationToken cancellationToken)
    {
        var producerArtifactId = TailwindCommandOptions.Require(options.ProducerArtifactId, "--producer-artifact-id");
        var expectedSubjectSha256 = TailwindCommandOptions.Require(options.ExpectedSubjectSha256, "--expected-subject-sha256");
        var repositoryId = TailwindCommandOptions.Require(options.RepositoryId, "--repository-id");
        var producerRunId = TailwindCommandOptions.Require(options.ProducerRunId, "--producer-run-id");
        var sourceCommit = TailwindCommandOptions.Require(options.SourceCommit, "--source-commit");
        await TailwindSourceIdentity.RequireAsync(repositoryRoot, sourceCommit, cancellationToken);
        var bundleRoot = Path.GetFullPath(artifactsInputPath);
        var subjectPath = TailwindProofSubjectService.ResolvePath(options.ProducerSubject, bundleRoot, TailwindProofSubjectService.FileName);
        var manifestPath = Path.GetFullPath(artifactManifestPath);
        var subject = await TailwindProofSubjectService.ValidateAsync(subjectPath, bundleRoot, manifestPath,
            expectedSubjectSha256, repositoryId, producerRunId, sourceCommit, producerArtifactId, cancellationToken);
        var aggregatePath = Path.GetFullPath(options.AggregateInput is null ? Path.Combine(bundleRoot, "aggregate-placeholder") : options.AggregateInput);
        var reportDirectory = Path.GetFullPath(options.ReportDirectory is null ? Path.Combine(bundleRoot, "report-placeholder") : options.ReportDirectory);
        var request = new TailwindPublicationRequest(repositoryRoot, bundleRoot, manifestPath, subjectPath,
            producerArtifactId, expectedSubjectSha256, repositoryId, producerRunId, sourceCommit,
            aggregatePath, options.AggregateArtifactId ?? "1", options.ExpectedAggregateSha256 ?? new string('0', 64),
            options.PublicationDirectory is null ? Path.Combine(reportDirectory, "publication") : options.PublicationDirectory,
            options.PublicationStartReceipt ?? Path.Combine(reportDirectory, StartReceiptFile),
            options.PublicationStartArtifactId ?? string.Empty, reportDirectory);
        return new TailwindProducerBinding(request, subject);
    }

    internal static async Task<TailwindProducerBinding> ValidateAndWriteProducerBindingAsync(
        string repositoryRoot, string artifactsInputPath, string artifactManifestPath,
        TailwindCommandOptions options, string outputPath, CancellationToken token)
    {
        var report = Path.GetFullPath(TailwindCommandOptions.Require(options.ReportDirectory, "--report-directory"));
        try
        {
            var binding = await ValidateProducerBindingAsync(repositoryRoot, artifactsInputPath, artifactManifestPath, options, token);
            await WriteResolvedProducerBindingAsync(binding, outputPath, token);
            return binding;
        }
        catch (Exception exception) when (exception is PackageIndexException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            await WriteFailureReportBestEffortAsync(report, "producer-binding", exception, token, releaseEligible: false);
            throw;
        }
    }

    internal static async Task<IReadOnlyList<TailwindHostArtifact>> ReadHostArtifactMapAsync(string path, CancellationToken token)
    {
        var bytes = await ReadBoundedAsync(path, token);
        using var document = ParseStrict(bytes, "host artifact map");
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != RequiredRids.Length)
            throw new PackageIndexException("Host artifact map must contain exactly five entries.");
        var result = new List<TailwindHostArtifact>(RequiredRids.Length);
        foreach (var item in root.EnumerateArray())
        {
            RequireExactFields(item, ["rid", "artifactId", "directory"], "host artifact map entry");
            var rid = RequireNonEmptyString(item, "rid");
            var idElement = item.GetProperty("artifactId");
            if (idElement.ValueKind != JsonValueKind.String)
                throw new PackageIndexException("Host artifact ID must be a canonical decimal string.");
            var id = idElement.GetString()!;
            var directory = RequireNonEmptyString(item, "directory");
            RequireDecimal(id, "host artifact ID");
            if (!RequiredRids.Contains(rid, StringComparer.Ordinal) || directory != rid)
                throw new PackageIndexException($"Host artifact map has unsupported RID or directory '{rid}/{directory}'.");
            result.Add(new TailwindHostArtifact(rid, id, directory));
        }
        if (!result.Select(item => item.Rid).SequenceEqual(RequiredRids, StringComparer.Ordinal))
            throw new PackageIndexException("Host artifact map must have exactly the five ordered supported RIDs.");
        if (result.Select(item => item.ArtifactId).Distinct(StringComparer.Ordinal).Count() != RequiredRids.Length)
            throw new PackageIndexException("Host artifact map contains duplicate artifact IDs.");
        return result;
    }

    internal static async Task WriteResolvedProducerBindingAsync(TailwindProducerBinding binding, string outputPath, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var manifest = await new PackageArtifactManifestReader().ReadAsync(binding.Request.ArtifactManifestPath, token);
        var tailwind = manifest.Entries.Single(item => item.PackageId.Equals("ForgeTrust.AppSurface.Web.Tailwind", StringComparison.OrdinalIgnoreCase));
        var packages = binding.Subject.FirstPartyPackages.Select(package => new
        {
            packageId = package.PackageId,
            packageVersion = package.PackageVersion,
            artifactFileName = package.ArtifactFileName,
            packageSha512 = package.PackageSha512
        }).ToArray();
        var payload = new
        {
            schema = "appsurface-tailwind-resolved-producer-binding-v1",
            repositoryId = binding.Subject.RepositoryId,
            producerRunId = binding.Subject.ProducerRunId,
            producerAttempt = binding.Subject.ProducerAttempt,
            sourceCommit = binding.Subject.SourceCommit,
            producerArtifactId = binding.Request.ProducerArtifactId,
            subjectSha256 = binding.Request.ExpectedSubjectSha256,
            artifactManifestSha256 = binding.Subject.ArtifactManifestSha256,
            packageVersion = binding.Subject.PackageVersion,
            packageId = tailwind.PackageId,
            artifactFileName = tailwind.ArtifactFileName,
            packageSha512 = tailwind.Sha512,
            tailwindManifestSha256 = binding.Subject.TailwindManifestSha256,
            payloadProjectionVersion = binding.Subject.PayloadProjectionVersion,
            firstPartyPackages = packages
        };
        var reportDirectory = binding.Request.ReportDirectory;
        RequireFreshDirectory(reportDirectory, "producer-binding report");
        Directory.CreateDirectory(reportDirectory);
        var fullOutput = Path.GetFullPath(outputPath);
        await WriteCreateNewJsonAsync(fullOutput, payload, token);
        await WriteDiagnosticReportAsync(reportDirectory, "succeeded", "producer-binding", false, [],
            $"Validated producer artifact `{binding.Request.ProducerArtifactId}` and {packages.Length} first-party packages.", token);
    }

    internal static object[] EnumerateInventory(string directory, params string[] excludedFiles)
    {
        var excluded = excludedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                RequireRegularFileWithoutLinks(path);
                var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
                return new { relative, fullPath = path };
            })
            .Where(item => !excluded.Contains(item.relative))
            .OrderBy(item => item.relative, StringComparer.Ordinal)
            .Select(item => (object)new { path = item.relative, sha256 = HashFileBounded(item.fullPath, 1024L * 1024 * 1024) })
            .ToArray();
    }

    internal static async Task WriteFailureReportBestEffortAsync(string directory, string stage, Exception exception,
        CancellationToken cancellationToken, object? details = null, bool releaseEligible = true)
    {
        try
        {
            _ = details;
            var status = exception is OperationCanceledException ? "cancelled" : "failed";
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WriteDiagnosticReportAsync(directory, status, stage, releaseEligible, [CreateDiagnosticError(stage, exception)],
                $"Tailwind proof {status} during `{stage}`.", cleanup.Token, overwrite: true);
        }
        catch { /* Diagnostics are best effort and never replace the original stage error. */ }
    }

    internal static async Task WriteDiagnosticReportAsync(string directory, string status, string stage, bool releaseEligible,
        IReadOnlyList<TailwindDiagnosticError> errors, string summary, CancellationToken token, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (status is not "succeeded" and not "failed" and not "cancelled") throw new PackageIndexException("Diagnostic status must be succeeded, failed, or cancelled.");
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        Directory.CreateDirectory(directory);
        var retained = errors.Take(100).ToArray();
        var diagnostics = new
        {
            schema = DiagnosticSchema,
            status,
            stage,
            releaseEligible,
            errors = retained,
            errorsTruncated = errors.Count > retained.Length
        };
        var path = Path.Combine(directory, "diagnostics.json");
        await WriteAtomicJsonAsync(path, diagnostics, token, overwrite);
        await WriteAtomicTextAsync(Path.Combine(directory, "summary.md"),
            $"# Tailwind proof report\n\nStatus: **{status}**\n\nStage: `{stage}`\n\nRelease eligible: `{releaseEligible.ToString().ToLowerInvariant()}`\n\n{summary}\n", token, overwrite);
    }

    internal static async Task WriteAtomicCreateNewJsonAsync<T>(string path, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, PackageArtifactJson.Options)
            .Concat(Encoding.UTF8.GetBytes(Environment.NewLine)).ToArray();
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await stream.WriteAsync(bytes, token);
            File.Move(temporary, full, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static TailwindDiagnosticError CreateDiagnosticError(string stage, Exception exception)
    {
        var code = stage switch
        {
            "producer-binding" => "producer-binding-invalid",
            "initial-restore" => "restore-failed",
            "locked-restore" => "locked-restore-failed",
            "restored-graph-and-payload" => "restored-payload-invalid",
            "native-build" => "native-build-failed",
            "post-build-revalidation" => "post-build-payload-changed",
            "aggregate" => "aggregate-invalid",
            "publish-preflight" => "publication-preflight-invalid",
            "local-consumer" => "local-proof-failed",
            _ => "proof-prerequisite-failed"
        };
        var message = Regex.Replace(exception.Message, "(?i)(token|password|api[_-]?key)(\\s*[:=]\\s*)[^\\s]+", "$1$2[redacted]");
        message = Regex.Replace(message, "(?i)(https?://)[^/@\\s]+:[^/@\\s]+@", "$1[redacted]@");
        if (message.Length > 2000) message = message[..2000] + "…";
        return new TailwindDiagnosticError(code, message, null, null,
            "Review the failed stage and rerun the documented Tailwind evidence command with the same trusted producer bundle.", DiagnosticDocsUrl);
    }

    private static async Task WriteAtomicJsonAsync<T>(string path, T value, CancellationToken token, bool overwrite)
        => await WriteAtomicBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(value, PackageArtifactJson.Options).Concat(Encoding.UTF8.GetBytes(Environment.NewLine)).ToArray(), token, overwrite);

    private static Task WriteAtomicTextAsync(string path, string value, CancellationToken token, bool overwrite)
        => WriteAtomicBytesAsync(path, Encoding.UTF8.GetBytes(value), token, overwrite);

    private static async Task WriteAtomicBytesAsync(string path, byte[] bytes, CancellationToken token, bool overwrite)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await stream.WriteAsync(bytes, token);
            File.Move(temporary, full, overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static async Task<TailwindEvidenceCommandResult> AggregateAsync(
        string repositoryRoot,
        string artifactsInputPath,
        string artifactManifestPath,
        TailwindCommandOptions options,
        CancellationToken cancellationToken)
    {
        var reportDirectory = Path.GetFullPath(TailwindCommandOptions.Require(options.ReportDirectory, "--report-directory"));
        var reportPath = Path.Combine(reportDirectory, AggregateFile);
        try
        {
            RequireFreshDirectory(reportDirectory, "aggregate report");
            var binding = await ValidateProducerBindingAsync(repositoryRoot, artifactsInputPath, artifactManifestPath, options, cancellationToken);
            var invocation = TailwindCommandOptions.Require(options.NativeInvocationId, "--native-invocation-id");
            var evidenceRoot = Path.GetFullPath(TailwindCommandOptions.Require(options.EvidenceInput, "--evidence-input"));
            var mapPath = Path.GetFullPath(TailwindCommandOptions.Require(options.HostArtifactsMap, "--host-artifacts-map"));
            var hostMap = await ReadHostArtifactMapAsync(mapPath, cancellationToken);
            var hostRecords = new List<object>(RequiredRids.Length);
            foreach (var rid in RequiredRids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var host = hostMap.Single(item => item.Rid == rid);
                var sourceDirectory = ResolveChildDirectory(evidenceRoot, host.Directory);
                var receiptPath = Path.Combine(sourceDirectory, "tailwind-native-host-proof.json");
                RequireRegularFileWithoutLinks(receiptPath);
                var receiptBytes = await ReadBoundedAsync(receiptPath, cancellationToken);
                using var receipt = ParseStrict(receiptBytes, $"{rid} native receipt");
                await ValidateNativeReceiptAsync(receipt.RootElement, rid, invocation, binding.Request, binding.Subject, sourceDirectory, cancellationToken);
                var destination = Path.Combine(reportDirectory, "hosts", rid);
                Directory.CreateDirectory(destination);
                CopyDirectory(sourceDirectory, destination);
                hostRecords.Add(new
                {
                    rid,
                    hostArtifactId = host.ArtifactId,
                    receiptPath = $"hosts/{rid}/tailwind-native-host-proof.json",
                    receiptSha256 = Hash(receiptBytes)
                });
            }

            await WriteDiagnosticReportAsync(reportDirectory, "succeeded", "aggregate", true, [],
                $"Validated and aggregated all {RequiredRids.Length} native host receipts for `{invocation}`.", cancellationToken);
            var inventory = EnumerateInventory(reportDirectory, AggregateFile);
            var aggregate = new
            {
                schema = "appsurface-tailwind-native-host-evidence-v2",
                status = "succeeded",
                repositoryId = binding.Subject.RepositoryId,
                sourceCommit = binding.Subject.SourceCommit,
                producerRunId = binding.Subject.ProducerRunId,
                producerAttempt = binding.Subject.ProducerAttempt,
                producerArtifactId = binding.Request.ProducerArtifactId,
                subjectSha256 = binding.Request.ExpectedSubjectSha256,
                artifactManifestSha256 = binding.Subject.ArtifactManifestSha256,
                packageVersion = binding.Subject.PackageVersion,
                payloadProjectionVersion = 1,
                nativeInvocationId = invocation,
                requiredRids = RequiredRids,
                hosts = hostRecords,
                files = inventory
            };
            await WriteAtomicCreateNewJsonAsync(reportPath, aggregate, cancellationToken);
            return new TailwindEvidenceCommandResult(true, "succeeded", reportPath);
        }
        catch (Exception ex) when (ex is PackageIndexException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            var status = ex is OperationCanceledException ? "cancelled" : "failed";
            await WriteFailureReportBestEffortAsync(reportDirectory, "aggregate", ex, cancellationToken);
            return new TailwindEvidenceCommandResult(false, status, reportPath);
        }
    }

    /// <summary>Validates the aggregate and producer bundle, copies immutable push inputs, and writes an upload-ready start receipt.</summary>
    internal static async Task<PreparedTailwindPublication> PreparePublicationAsync(
        TailwindPublicationRequest request,
        PackageArtifactManifest manifest,
        IReadOnlyList<PlannedPackageArtifact> originalEntries,
        CancellationToken cancellationToken)
    {
        try
        {
            return await PreparePublicationCoreAsync(request, manifest, originalEntries, cancellationToken);
        }
        catch (Exception exception) when (exception is PackageIndexException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            await WriteFailureReportBestEffortAsync(request.ReportDirectory, "publish-preflight", exception, cancellationToken);
            throw;
        }
    }

    private static async Task<PreparedTailwindPublication> PreparePublicationCoreAsync(
        TailwindPublicationRequest request,
        PackageArtifactManifest manifest,
        IReadOnlyList<PlannedPackageArtifact> originalEntries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(originalEntries);
        await TailwindSourceIdentity.RequireAsync(request.RepositoryRoot, request.SourceCommit, cancellationToken);
        var subject = await TailwindProofSubjectService.ValidateAsync(
            request.ProducerSubjectPath, request.ArtifactsInputPath, request.ArtifactManifestPath,
            request.ExpectedSubjectSha256, request.RepositoryId, request.ProducerRunId, request.SourceCommit,
            request.ProducerArtifactId, cancellationToken);
        if (manifest.PackageVersion != subject.PackageVersion) throw new PackageIndexException("Preflight manifest version differs from the bound producer subject.");
        var aggregateRoot = Path.GetFullPath(request.AggregateInputPath);
        var aggregatePath = Path.Combine(aggregateRoot, AggregateFile);
        RequireRegularFileWithoutLinks(aggregatePath);
        var aggregateBytes = await ReadBoundedAsync(aggregatePath, cancellationToken);
        var aggregateHash = Hash(aggregateBytes);
        RequireDigest(request.ExpectedAggregateSha256, 64, "expected aggregate SHA-256");
        if (!FixedEquals(aggregateHash, request.ExpectedAggregateSha256)) throw new PackageIndexException("Native aggregate JSON SHA-256 differs from its trusted workflow output.");
        RequireDecimal(request.AggregateArtifactId, "aggregate artifact ID");
        using var aggregate = ParseStrict(aggregateBytes, "native aggregate");
        var invocation = await ValidateAggregateAsync(aggregate.RootElement, request, subject, aggregateRoot, cancellationToken);

        var publicationRoot = Path.GetFullPath(request.PublicationDirectory);
        RequireFreshDirectory(publicationRoot, "publication");
        PackageProofWorkDirectory.RequireDisjoint(publicationRoot, request.ReportDirectory);
        Directory.CreateDirectory(publicationRoot);
        var prepared = new List<TailwindPreparedPackage>(originalEntries.Count);
        foreach (var entry in originalEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TailwindProofSubjectService.ValidateBasename(entry.ManifestEntry.ArtifactFileName, "publish artifact filename");
            var source = Path.Combine(request.ArtifactsInputPath, entry.ManifestEntry.ArtifactFileName);
            RequireRegularFileWithoutLinks(source);
            if (!string.Equals(await PackageHash.ComputeSha512Async(source, cancellationToken), entry.ManifestEntry.Sha512, StringComparison.Ordinal))
                throw new PackageIndexException($"Frozen producer package '{entry.ManifestEntry.ArtifactFileName}' changed before preflight.");
            var target = Path.Combine(publicationRoot, entry.ManifestEntry.ArtifactFileName);
            File.Copy(source, target, overwrite: false);
            if (!string.Equals(await PackageHash.ComputeSha512Async(target, cancellationToken), entry.ManifestEntry.Sha512, StringComparison.Ordinal))
                throw new PackageIndexException($"Prepared publication package '{entry.ManifestEntry.ArtifactFileName}' differs from producer bytes.");
            prepared.Add(new TailwindPreparedPackage(entry.ManifestEntry.PackageId, manifest.PackageVersion,
                entry.ManifestEntry.ArtifactFileName, entry.ManifestEntry.Sha512, target));
        }

        RequireFreshDirectory(request.ReportDirectory, "preflight report");
        PackageProofWorkDirectory.RequireDisjoint(request.ReportDirectory, publicationRoot);
        Directory.CreateDirectory(request.ReportDirectory);
        var startReceiptPath = Path.Combine(request.ReportDirectory, StartReceiptFile);
        var draft = new
        {
            schema = "appsurface-tailwind-publication-start-v1",
            repositoryId = subject.RepositoryId,
            sourceCommit = subject.SourceCommit,
            producerRunId = subject.ProducerRunId,
            producerAttempt = subject.ProducerAttempt,
            producerArtifactId = request.ProducerArtifactId,
            subjectSha256 = request.ExpectedSubjectSha256,
            artifactManifestSha256 = subject.ArtifactManifestSha256,
            packageVersion = subject.PackageVersion,
            payloadProjectionVersion = 1,
            aggregateArtifactId = request.AggregateArtifactId,
            aggregateSha256 = aggregateHash,
            nativeInvocationId = invocation,
            packages = prepared.Select(package => new { packageId = package.PackageId, packageVersion = package.PackageVersion, artifactFileName = package.ArtifactFileName, packageSha512 = package.PackageSha512 }).ToArray()
        };
        await WriteCreateNewJsonAsync(startReceiptPath, draft, cancellationToken);
        await WriteDiagnosticReportAsync(request.ReportDirectory, "succeeded", "publish-preflight", true, [],
            $"Validated aggregate `{request.AggregateArtifactId}` and prepared {prepared.Count} packages. Upload `{StartReceiptFile}` and pass its returned artifact ID to the publisher.", cancellationToken);
        return new PreparedTailwindPublication(subject.PackageVersion, aggregateHash, invocation, prepared);
    }

    /// <summary>Revalidates producer, aggregate and uploaded start receipt, returning only prepared push paths.</summary>
    internal static async Task<IReadOnlyList<PlannedPackageArtifact>> ValidatePublicationAsync(
        TailwindPublicationRequest request,
        PackageArtifactManifest manifest,
        IReadOnlyList<PlannedPackageArtifact> originalEntries,
        CancellationToken cancellationToken)
    {
        var prepared = await ValidatePublicationCoreAsync(request, manifest, originalEntries, cancellationToken);
        return originalEntries.Select(entry =>
        {
            var item = prepared.Packages.Single(package => string.Equals(package.PackageId, entry.ManifestEntry.PackageId, StringComparison.OrdinalIgnoreCase));
            return new PlannedPackageArtifact(entry.ManifestEntry, item.Path);
        }).ToArray();
    }

    internal static async Task<PreparedTailwindPublication> ValidatePublicationCoreAsync(
        TailwindPublicationRequest request,
        PackageArtifactManifest manifest,
        IReadOnlyList<PlannedPackageArtifact> originalEntries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(originalEntries);
        await TailwindSourceIdentity.RequireAsync(request.RepositoryRoot, request.SourceCommit, cancellationToken);
        var bundleRoot = Path.GetFullPath(request.ArtifactsInputPath);
        var manifestPath = Path.GetFullPath(request.ArtifactManifestPath);
        var subjectPath = Path.GetFullPath(request.ProducerSubjectPath);
        var subject = await TailwindProofSubjectService.ValidateAsync(
            subjectPath, bundleRoot, manifestPath, request.ExpectedSubjectSha256, request.RepositoryId,
            request.ProducerRunId, request.SourceCommit, request.ProducerArtifactId, cancellationToken);
        if (manifest.PackageVersion != subject.PackageVersion)
            throw new PackageIndexException("Publisher manifest version differs from the bound producer subject.");

        var aggregateRoot = Path.GetFullPath(request.AggregateInputPath);
        var aggregatePath = Path.Combine(aggregateRoot, AggregateFile);
        RequireRegularFileWithoutLinks(aggregatePath);
        var aggregateBytes = await ReadBoundedAsync(aggregatePath, cancellationToken);
        var aggregateHash = Hash(aggregateBytes);
        RequireDigest(request.ExpectedAggregateSha256, 64, "expected aggregate SHA-256");
        if (!FixedEquals(aggregateHash, request.ExpectedAggregateSha256))
            throw new PackageIndexException("Native aggregate JSON SHA-256 differs from its trusted workflow output.");
        RequireDecimal(request.AggregateArtifactId, "aggregate artifact ID");
        using var aggregate = ParseStrict(aggregateBytes, "native aggregate");
        var aggregateRootJson = aggregate.RootElement;
        var aggregateInvocation = await ValidateAggregateAsync(aggregateRootJson, request, subject, aggregateRoot, cancellationToken);

        var startPath = Path.GetFullPath(request.PublicationStartReceiptPath);
        RequireDirectChild(Path.GetDirectoryName(startPath)!, startPath);
        RequireRegularFileWithoutLinks(startPath);
        RequireDecimal(request.PublicationStartArtifactId, "publication-start artifact ID");
        var startBytes = await ReadBoundedAsync(startPath, cancellationToken);
        using var startDocument = ParseStrict(startBytes, "publication-start receipt");
        var invocation = ValidateStartReceipt(startDocument.RootElement, request, subject, aggregateHash, originalEntries);
        if (!string.Equals(invocation, aggregateInvocation, StringComparison.Ordinal))
            throw new PackageIndexException("Publication-start receipt nativeInvocationId differs from the validated aggregate.");

        var publicationRoot = Path.GetFullPath(request.PublicationDirectory);
        RequireDirectoryWithoutLinks(publicationRoot);
        var output = new List<TailwindPreparedPackage>(originalEntries.Count);
        foreach (var entry in originalEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TailwindProofSubjectService.ValidateBasename(entry.ManifestEntry.ArtifactFileName, "publish artifact filename");
            var path = Path.Combine(publicationRoot, entry.ManifestEntry.ArtifactFileName);
            RequireDirectChild(publicationRoot, path);
            RequireRegularFileWithoutLinks(path);
            var hash = await PackageHash.ComputeSha512Async(path, cancellationToken);
            if (!string.Equals(hash, entry.ManifestEntry.Sha512, StringComparison.Ordinal))
                throw new PackageIndexException($"Prepared publication file '{entry.ManifestEntry.ArtifactFileName}' differs from the frozen producer package bytes.");
            output.Add(new TailwindPreparedPackage(entry.ManifestEntry.PackageId, manifest.PackageVersion,
                entry.ManifestEntry.ArtifactFileName, hash, path));
        }

        return new PreparedTailwindPublication(manifest.PackageVersion, aggregateHash, invocation, output);
    }

    internal static async Task<PreparedTailwindPublication> ValidatePublicationStartAsync(
        TailwindPublicationRequest request,
        PackageArtifactManifest manifest,
        IReadOnlyList<PlannedPackageArtifact> originalEntries,
        CancellationToken cancellationToken)
    {
        try
        {
            var validated = await ValidatePublicationCoreAsync(request, manifest, originalEntries, cancellationToken);
            await WriteDiagnosticReportAsync(request.ReportDirectory, "succeeded", "validate-publication-start", true, [],
                $"Validated uploaded start receipt `{request.PublicationStartArtifactId}` against the aggregate and {validated.Packages.Count} prepared packages. No files were restaged.", cancellationToken, overwrite: true);
            return validated;
        }
        catch (Exception exception) when (exception is PackageIndexException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            await WriteFailureReportBestEffortAsync(request.ReportDirectory, "validate-publication-start", exception, cancellationToken);
            throw;
        }
    }

    private static async Task<string> ValidateAggregateAsync(JsonElement root, TailwindPublicationRequest request, TailwindProofSubject subject, string aggregateDirectory, CancellationToken token)
    {
        RequireExactFields(root, ["schema", "status", "repositoryId", "sourceCommit", "producerRunId", "producerAttempt", "producerArtifactId", "subjectSha256", "artifactManifestSha256", "packageVersion", "payloadProjectionVersion", "nativeInvocationId", "requiredRids", "hosts", "files"], "aggregate");
        RequireString(root, "schema", "appsurface-tailwind-native-host-evidence-v2");
        RequireString(root, "status", "succeeded");
        ValidateCommonBinding(root, request, subject);
        var invocation = RequireNonEmptyString(root, "nativeInvocationId");
        var rids = RequireStringArray(root, "requiredRids");
        if (!rids.SequenceEqual(RequiredRids, StringComparer.Ordinal)) throw new PackageIndexException("Aggregate requiredRids must be the exact ordered five-host set.");
        var hosts = RequireArray(root, "hosts");
        if (hosts.GetArrayLength() != RequiredRids.Length) throw new PackageIndexException("Aggregate must contain exactly five host records.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var host in hosts.EnumerateArray())
        {
            RequireExactFields(host, ["rid", "hostArtifactId", "receiptPath", "receiptSha256"], "aggregate host");
            var rid = RequireNonEmptyString(host, "rid");
            if (!RequiredRids.Contains(rid, StringComparer.Ordinal) || !seen.Add(rid)) throw new PackageIndexException($"Aggregate contains unknown or duplicate RID '{rid}'.");
            RequireDecimal(RequireNonEmptyString(host, "hostArtifactId"), "host artifact ID");
            var relative = RequireNonEmptyString(host, "receiptPath");
            var confined = ResolveEvidencePath(aggregateDirectory, relative);
            RequireDigest(RequireNonEmptyString(host, "receiptSha256"), 64, "receipt SHA-256");
            RequireRegularFileWithoutLinks(confined);
            var receiptBytes = ReadBounded(confined, TailwindProofSubjectService.MaximumDocumentBytes);
            var actual = Hash(receiptBytes);
            if (!FixedEquals(actual, host.GetProperty("receiptSha256").GetString()!)) throw new PackageIndexException($"Receipt hash mismatch for RID '{rid}'.");
            using var receipt = ParseStrict(receiptBytes, $"{rid} native receipt");
            await ValidateNativeReceiptAsync(receipt.RootElement, rid, invocation, request, subject, Path.GetDirectoryName(confined)!, token);
        }
        if (seen.Count != RequiredRids.Length) throw new PackageIndexException("Aggregate is missing one or more required RIDs.");
        await ValidateFileInventoryAsync(root, "files", aggregateDirectory, AggregateFile, token);
        return invocation;
    }

    private static async Task ValidateNativeReceiptAsync(JsonElement root, string rid, string invocation, TailwindPublicationRequest request, TailwindProofSubject subject, string evidenceRoot, CancellationToken token)
    {
        RequireExactFields(root,
            ["schema", "status", "repositoryId", "sourceCommit", "producerRunId", "producerAttempt", "producerArtifactId", "subjectSha256", "artifactManifestSha256", "packageVersion", "payloadProjectionVersion", "nativeInvocationId", "nativeRunId", "nativeAttempt", "expectedRid", "observedRid", "hostOs", "osArchitecture", "processArchitecture", "runnerLabel", "sdkVersion", "firstPartyPackages", "tailwindManifestSha256", "restoredTailwindManifestSha256", "binaryName", "binarySha256", "checks", "files", "diagnosticPath"],
            "native host receipt");
        RequireString(root, "schema", "appsurface-tailwind-native-host-proof-v2");
        RequireString(root, "status", "succeeded");
        ValidateCommonBinding(root, request, subject);
        RequireString(root, "nativeInvocationId", invocation);
        RequireString(root, "expectedRid", rid);
        RequireString(root, "observedRid", rid);
        RequireDecimal(RequireNonEmptyString(root, "nativeRunId"), "native run ID");
        RequireDecimal(RequireNonEmptyString(root, "nativeAttempt"), "native attempt");
        _ = RequireNonEmptyString(root, "runnerLabel");
        _ = RequireNonEmptyString(root, "sdkVersion");
        ValidateHostMapping(root, rid);
        if (!root.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Object)
            throw new PackageIndexException($"Native receipt '{rid}' is missing checks.");
        foreach (var check in new[] { "generatedCss", "hostCacheBinary", "noRuntimeCompanionDependency", "noNativeConsumerOutput", "postBuildPayloadUnchanged" })
            if (!checks.TryGetProperty(check, out var value) || value.ValueKind != JsonValueKind.True)
                throw new PackageIndexException($"Native receipt '{rid}' did not prove '{check}'.");
        if (!root.TryGetProperty("firstPartyPackages", out var packages) || packages.ValueKind != JsonValueKind.Array)
            throw new PackageIndexException($"Native receipt '{rid}' is missing first-party package evidence.");
        var resolved = packages.EnumerateArray().ToArray();
        if (resolved.Length != subject.FirstPartyPackages.Count) throw new PackageIndexException($"Native receipt '{rid}' first-party closure count differs from subject.");
        foreach (var expected in subject.FirstPartyPackages)
        {
            var matches = resolved.Where(item => item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("packageId", out var id) && id.ValueKind == JsonValueKind.String
                && string.Equals(id.GetString(), expected.PackageId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new PackageIndexException($"Native receipt '{rid}' omits or duplicates package '{expected.PackageId}'.");
            var match = matches[0];
            RequireExactFields(match, ["packageId", "packageVersion", "producerSha512", "restoredSha512", "archivePath", "payloadVerified", "payloadFiles"], "native first-party package record");
            RequireString(match, "packageVersion", subject.PackageVersion);
            RequireString(match, "producerSha512", expected.PackageSha512);
            RequireString(match, "restoredSha512", expected.PackageSha512);
            if (!match.TryGetProperty("payloadVerified", out var payload) || payload.ValueKind != JsonValueKind.True)
                throw new PackageIndexException($"Native receipt '{rid}' has no successful payload verification for '{expected.PackageId}'.");
            var archivePath = ResolveEvidencePath(evidenceRoot, RequireNonEmptyString(match, "archivePath"));
            RequireRegularFileWithoutLinks(archivePath);
            if (!string.Equals(PackageHash.ComputeSha512(archivePath), expected.PackageSha512, StringComparison.Ordinal))
                throw new PackageIndexException($"Native receipt '{rid}' consumed archive for '{expected.PackageId}' differs from producer bytes.");
            ValidatePayloadFileSet(match, expected, archivePath, evidenceRoot);
        }
        var binaryName = RequireNonEmptyString(root, "binaryName");
        var expectedBinaryName = ReadExpectedBinaryName(Path.Combine(request.ArtifactsInputPath, subject.ArtifactFileName), rid);
        if (!string.Equals(binaryName, expectedBinaryName, StringComparison.Ordinal)) throw new PackageIndexException($"Native receipt '{rid}' selected binary '{binaryName}', expected '{expectedBinaryName}' from the producer Tailwind manifest.");
        RequireDigest(RequireNonEmptyString(root, "binarySha256"), 64, "Tailwind CLI binary SHA-256");
        RequireDigest(RequireNonEmptyString(root, "tailwindManifestSha256"), 64, "internal Tailwind manifest SHA-256");
        RequireDigest(RequireNonEmptyString(root, "restoredTailwindManifestSha256"), 64, "restored Tailwind manifest SHA-256");
        RequireString(root, "tailwindManifestSha256", subject.TailwindManifestSha256);
        if (!string.Equals(root.GetProperty("tailwindManifestSha256").GetString(), root.GetProperty("restoredTailwindManifestSha256").GetString(), StringComparison.Ordinal))
            throw new PackageIndexException($"Native receipt '{rid}' Tailwind release manifest digest changed after restore.");
        await ValidateFileInventoryAsync(root, "files", evidenceRoot, "tailwind-native-host-proof.json", token);
        var diagnosticPath = RequireNonEmptyString(root, "diagnosticPath");
        if (!string.Equals(diagnosticPath, "native-consumer-report.md", StringComparison.Ordinal))
            throw new PackageIndexException($"Native receipt '{rid}' has an unexpected diagnostic path.");
        _ = ResolveEvidencePath(evidenceRoot, diagnosticPath);
    }

    private static void ValidateHostMapping(JsonElement receipt, string rid)
    {
        var expected = rid switch
        {
            "linux-x64" => ("Linux", "X64", "X64"),
            "linux-arm64" => ("Linux", "Arm64", "Arm64"),
            "osx-x64" => ("macOS", "X64", "X64"),
            "osx-arm64" => ("macOS", "Arm64", "Arm64"),
            "win-x64" => ("Windows", (string?)null, "X64"),
            _ => throw new PackageIndexException($"Unsupported host RID '{rid}'.")
        };
        RequireString(receipt, "hostOs", expected.Item1);
        if (expected.Item2 is not null) RequireString(receipt, "osArchitecture", expected.Item2);
        else if (RequireNonEmptyString(receipt, "osArchitecture") is not ("X64" or "Arm64"))
            throw new PackageIndexException("Windows x64 proof must observe x64 or the documented Arm64 emulation host.");
        RequireString(receipt, "processArchitecture", expected.Item3);
    }

    private static void ValidatePayloadFileSet(JsonElement package, TailwindSubjectPackage expected, string archivePath, string evidenceRoot)
    {
        if (!package.TryGetProperty("payloadFiles", out var payloads) || payloads.ValueKind != JsonValueKind.Array)
            throw new PackageIndexException($"Native payload evidence for '{expected.PackageId}' must be an array.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var payload in payloads.EnumerateArray())
        {
            RequireExactFields(payload, ["packageRelativePath", "evidencePath", "sha256"], "native payload file");
            var packagePath = TailwindProofSubjectService.NormalizeArchivePath(RequireNonEmptyString(payload, "packageRelativePath"));
            if (!seen.Add(packagePath)) throw new PackageIndexException($"Native payload inventory for '{expected.PackageId}' contains duplicate/case-colliding path '{packagePath}'.");
            var evidencePath = ResolveEvidencePath(evidenceRoot, RequireNonEmptyString(payload, "evidencePath"));
            RequireRegularFileWithoutLinks(evidencePath);
            var expectedHash = RequireNonEmptyString(payload, "sha256");
            RequireDigest(expectedHash, 64, "payload SHA-256");
            if (!FixedEquals(HashFile(evidencePath), expectedHash)) throw new PackageIndexException($"Native payload evidence bytes changed at '{packagePath}'.");
        }
        var archiveEntries = ReadProtectedArchiveHashes(archivePath);
        if (archiveEntries.Count != seen.Count || !archiveEntries.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(seen))
            throw new PackageIndexException($"Native payload inventory for '{expected.PackageId}' is incomplete or contains extra files.");
        foreach (var (path, hash) in archiveEntries)
        {
            var payload = payloads.EnumerateArray().Single(item => string.Equals(item.GetProperty("packageRelativePath").GetString(), path, StringComparison.OrdinalIgnoreCase));
            if (!string.Equals(payload.GetProperty("sha256").GetString(), hash, StringComparison.Ordinal))
                throw new PackageIndexException($"Native payload '{path}' is not byte-identical to the producer archive.");
        }
    }

    private static Dictionary<string, string> ReadProtectedArchiveHashes(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        if (zip.Entries.Count > 100_000) throw new PackageIndexException("Evidence package archive exceeds the ZIP entry limit.");
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                _ = TailwindProofSubjectService.NormalizeArchivePath(entry.FullName[..^1]);
                continue;
            }
            var name = TailwindProofSubjectService.NormalizeArchivePath(entry.FullName);
            var protectedPath = new[] { "build/", "buildTransitive/", "buildMultiTargeting/", "lib/", "ref/", "analyzers/", "tools/", "tasks/", "runtimes/", "contentFiles/" }.Any(root => name.StartsWith(root, StringComparison.OrdinalIgnoreCase));
            if (!protectedPath) continue;
            if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000) throw new PackageIndexException($"Evidence package contains symlink '{name}'.");
            expanded = checked(expanded + entry.Length);
            if (expanded > 4L * 1024 * 1024 * 1024) throw new PackageIndexException("Protected archive payload exceeds the 4 GiB limit.");
            using var stream = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long actual = 0;
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                actual = checked(actual + read);
                if (actual > entry.Length) throw new PackageIndexException($"Archive entry '{name}' exceeded its declared length.");
                hash.AppendData(buffer, 0, read);
            }
            if (actual != entry.Length) throw new PackageIndexException($"Archive entry '{name}' expanded to an unexpected size.");
            result.Add(name, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        return result;
    }

    private static string ReadExpectedBinaryName(string archivePath, string rid)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(archivePath);
        var entry = zip.Entries.SingleOrDefault(item => string.Equals(item.FullName, "build/tailwind.release.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new PackageIndexException("Producer Tailwind archive is missing its internal release manifest.");
        using var document = ParseStrict(ReadZipEntryBounded(entry), "producer Tailwind release manifest");
        if (!document.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new PackageIndexException("Producer Tailwind release manifest has no assets array.");
        var matches = assets.EnumerateArray().Where(item => item.TryGetProperty("rid", out var value) && value.GetString() == rid).ToArray();
        if (matches.Length != 1 || !matches[0].TryGetProperty("binaryName", out var binary) || binary.ValueKind != JsonValueKind.String)
            throw new PackageIndexException($"Producer Tailwind release manifest does not define exactly one binary for '{rid}'.");
        return binary.GetString()!;
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ValidateStartReceipt(JsonElement root, TailwindPublicationRequest request, TailwindProofSubject subject, string aggregateHash, IReadOnlyList<PlannedPackageArtifact> entries)
    {
        RequireExactFields(root, ["schema", "repositoryId", "sourceCommit", "producerRunId", "producerAttempt", "producerArtifactId", "subjectSha256", "artifactManifestSha256", "packageVersion", "payloadProjectionVersion", "aggregateArtifactId", "aggregateSha256", "nativeInvocationId", "packages"], "publication-start receipt");
        RequireString(root, "schema", "appsurface-tailwind-publication-start-v1");
        ValidateCommonBinding(root, request, subject);
        RequireString(root, "aggregateArtifactId", request.AggregateArtifactId);
        RequireString(root, "aggregateSha256", aggregateHash);
        var invocation = RequireNonEmptyString(root, "nativeInvocationId");
        var packages = RequireArray(root, "packages");
        if (packages.GetArrayLength() != entries.Count) throw new PackageIndexException("Publication-start package inventory does not match the publish plan.");
        foreach (var entry in entries)
        {
            var matches = packages.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("packageId", out var id) && id.GetString() == entry.ManifestEntry.PackageId).ToArray();
            if (matches.Length != 1) throw new PackageIndexException($"Publication-start receipt does not bind exactly one package '{entry.ManifestEntry.PackageId}'.");
            var item = matches[0];
            RequireExactFields(item, ["packageId", "packageVersion", "artifactFileName", "packageSha512"], "publication-start package");
            RequireString(item, "packageVersion", subject.PackageVersion);
            RequireString(item, "artifactFileName", entry.ManifestEntry.ArtifactFileName);
            RequireString(item, "packageSha512", entry.ManifestEntry.Sha512);
        }
        return invocation;
    }

    private static void ValidateCommonBinding(JsonElement root, TailwindPublicationRequest request, TailwindProofSubject subject)
    {
        RequireString(root, "repositoryId", request.RepositoryId);
        RequireString(root, "sourceCommit", request.SourceCommit);
        RequireString(root, "producerRunId", request.ProducerRunId);
        RequireString(root, "producerAttempt", subject.ProducerAttempt);
        RequireString(root, "producerArtifactId", request.ProducerArtifactId);
        RequireString(root, "subjectSha256", request.ExpectedSubjectSha256);
        RequireString(root, "artifactManifestSha256", subject.ArtifactManifestSha256);
        RequireString(root, "packageVersion", subject.PackageVersion);
        if (!root.TryGetProperty("payloadProjectionVersion", out var projection) || !projection.TryGetInt32(out var version) || version != 1)
            throw new PackageIndexException("Evidence payloadProjectionVersion must be the integer 1.");
    }

    private static async Task ValidateFileInventoryAsync(JsonElement root, string property, string directory, string containingFile, CancellationToken token)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { containingFile };
        var files = RequireArray(root, property);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in files.EnumerateArray())
        {
            RequireExactFields(item, ["path", "sha256"], "evidence file inventory item");
            var relative = RequireNonEmptyString(item, "path");
            var full = ResolveEvidencePath(directory, relative);
            if (!seen.Add(relative)) throw new PackageIndexException($"Evidence inventory contains duplicate path '{relative}'.");
            if (excluded.Contains(relative)) throw new PackageIndexException($"Evidence files inventory must exclude its containing file '{relative}'.");
            RequireDigest(RequireNonEmptyString(item, "sha256"), 64, "evidence file SHA-256");
            RequireRegularFileWithoutLinks(full);
            var actual = await HashFileBoundedAsync(full, 1024L * 1024 * 1024, token);
            if (!FixedEquals(actual, item.GetProperty("sha256").GetString()!)) throw new PackageIndexException($"Evidence file '{relative}' does not match its bound SHA-256.");
        }
        var actualFiles = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var excludedFile in excluded)
            if (!actualFiles.Remove(excludedFile)) throw new PackageIndexException($"Evidence directory is missing required document '{excludedFile}'.");
        if (!actualFiles.SetEquals(seen))
        {
            var missing = actualFiles.Except(seen, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            var extra = seen.Except(actualFiles, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            throw new PackageIndexException($"Aggregate file inventory is incomplete or has surplus entries (unbound file: '{missing ?? "none"}', missing file: '{extra ?? "none"}').");
        }
    }

    internal static JsonDocument ParseStrict(byte[] bytes, string description)
    {
        if (bytes.Length > TailwindProofSubjectService.MaximumDocumentBytes) throw new PackageIndexException($"{description} exceeds the 16 MiB JSON limit.");
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        ValidateNoDuplicateProperties(document.RootElement, description);
        return document;
    }

    internal static byte[] ReadBounded(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new PackageIndexException($"Evidence document '{path}' exceeds the {maximum}-byte limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    internal static string HashFileBounded(string path, long maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new PackageIndexException($"Evidence file '{path}' exceeds its {maximum}-byte bound.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var count = stream.Read(buffer, 0, buffer.Length);
            if (count == 0) break;
            total = checked(total + count);
            if (total > maximum) throw new PackageIndexException($"Evidence file '{path}' grew beyond its {maximum}-byte bound.");
            hash.AppendData(buffer, 0, count);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static async Task<string> HashFileBoundedAsync(string path, long maximum, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new PackageIndexException($"Evidence file '{path}' exceeds its {maximum}-byte bound.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token);
            if (count == 0) break;
            total = checked(total + count);
            if (total > maximum) throw new PackageIndexException($"Evidence file '{path}' grew beyond its {maximum}-byte bound.");
            hash.AppendData(buffer, 0, count);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static byte[] ReadZipEntryBounded(System.IO.Compression.ZipArchiveEntry entry)
    {
        if (entry.Length > TailwindProofSubjectService.MaximumDocumentBytes) throw new PackageIndexException("Tailwind release manifest exceeds the 16 MiB JSON limit.");
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = stream.Read(buffer, 0, buffer.Length);
            if (count == 0) break;
            if (memory.Length + count > TailwindProofSubjectService.MaximumDocumentBytes) throw new PackageIndexException("Tailwind release manifest exceeds the 16 MiB JSON limit.");
            memory.Write(buffer, 0, count);
        }
        if (memory.Length != entry.Length) throw new PackageIndexException("Tailwind release manifest expanded to a different length than its ZIP declaration.");
        return memory.ToArray();
    }

    private static void ValidateNoDuplicateProperties(JsonElement value, string description)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new PackageIndexException($"{description} contains duplicate field '{property.Name}'.");
                ValidateNoDuplicateProperties(property.Value, description);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateNoDuplicateProperties(item, description);
    }

    private static void RequireExactFields(JsonElement element, IReadOnlyCollection<string> fields, string description)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new PackageIndexException($"{description} must be a JSON object.");
        var actual = element.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        if (actual.Count != fields.Count || fields.Any(field => !actual.Contains(field))) throw new PackageIndexException($"{description} does not match the frozen v2 wire schema.");
    }

    private static void RequireString(JsonElement root, string name, string expected)
    {
        var actual = RequireNonEmptyString(root, name);
        if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw new PackageIndexException($"Evidence field '{name}' does not match trusted value.");
    }

    private static string RequireNonEmptyString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new PackageIndexException($"Evidence field '{name}' must be a non-empty string.");
        return value.GetString()!;
    }

    private static JsonElement RequireArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) throw new PackageIndexException($"Evidence field '{name}' must be an array.");
        return value;
    }

    private static string[] RequireStringArray(JsonElement root, string name)
    {
        return RequireArray(root, name).EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
            ? item.GetString()! : throw new PackageIndexException($"Evidence field '{name}' must contain only strings.")).ToArray();
    }

    private static string ResolveEvidencePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':') || relative.Contains('\0'))
            throw new PackageIndexException($"Unsafe relative evidence path '{relative}'.");
        var parts = relative.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or "..")) throw new PackageIndexException($"Unsafe relative evidence path '{relative}'.");
        var path = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new PackageIndexException($"Evidence path '{relative}' escapes its artifact directory.");
        return path;
    }

    private static void RequireDirectChild(string directory, string file)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(file)), Path.GetFullPath(directory), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new PackageIndexException("Bound document must be a direct child of its downloaded artifact directory.");
    }

    private static void RequireDirectoryWithoutLinks(string directory)
    {
        if (!Directory.Exists(directory)) throw new PackageIndexException($"Publication directory '{directory}' does not exist.");
        RequireNoReparseAncestors(directory);
    }

    private static void RequireRegularFileWithoutLinks(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new PackageIndexException($"Evidence or publication file '{path}' is missing or is a link/reparse point.");
        RequireNoReparseAncestors(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    private static void RequireNoReparseAncestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new PackageIndexException($"Evidence path contains a link/reparse-point ancestor '{current}'.");
            var parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent ?? string.Empty;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > TailwindProofSubjectService.MaximumDocumentBytes) throw new PackageIndexException($"Evidence document '{path}' exceeds the 16 MiB limit.");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, token);
        return bytes;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool FixedEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));

    private static void RequireDecimal(string value, string field)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 20 || value.Length > 1 && value[0] == '0' || value.Any(ch => ch is < '0' or > '9'))
            throw new PackageIndexException($"{field} must be a canonical decimal string.");
    }

    private static void RequireDigest(string value, int length, string field)
    {
        if (value.Length != length || value.Any(ch => !(ch is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new PackageIndexException($"{field} must be {length} lowercase hexadecimal characters.");
    }

    internal static string ResolveOptionPath(string? value, string root, string defaultName)
    {
        var required = TailwindCommandOptions.Require(value, defaultName);
        return Path.GetFullPath(Path.IsPathRooted(required) ? required : Path.Combine(root, required));
    }

    internal static async Task WriteCreateNewJsonAsync<T>(string path, T value, CancellationToken token)
    {
        await WriteAtomicCreateNewJsonAsync(path, value, token);
    }

    private static void RequireFreshDirectory(string path, string description)
    {
        if (Directory.Exists(path) || File.Exists(path)) throw new PackageIndexException($"{description} directory '{path}' must be fresh and absent.");
        var parent = Path.GetDirectoryName(path) ?? throw new PackageIndexException($"{description} directory has no parent.");
        Directory.CreateDirectory(parent);
        var current = new DirectoryInfo(parent);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new PackageIndexException($"{description} path contains link/reparse-point ancestor '{current.FullName}'.");
            current = current.Parent;
        }
    }

    private static string ResolveChildDirectory(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Split('/').Any(part => part is "" or "." or ".."))
            throw new PackageIndexException($"Unsafe host artifact directory '{relative}'.");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || !Directory.Exists(full))
            throw new PackageIndexException($"Host artifact directory '{relative}' is missing or escapes its input root.");
        RequireNoReparseAncestors(full);
        return full;
    }

    private static void CopyDirectory(string source, string destination)
    {
        RequireNoReparseAncestors(source);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            RequireRegularFileWithoutLinks(file);
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

}
