using System.Text;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>
/// Coordinates package artifact packing and validation without publishing to NuGet.
/// </summary>
internal sealed class PackageArtifactWorkflow
{
    private const string TailwindMainPackageId = "ForgeTrust.AppSurface.Web.Tailwind";

    internal const int RestoreTimeoutMilliseconds = 180_000;
    internal const int BuildTimeoutMilliseconds = 300_000;
    internal const int PackTimeoutMilliseconds = 180_000;
    internal const int TailwindConsumerProofTimeoutMilliseconds = 240_000;

    private readonly PackagePublishPlanResolver _planResolver;
    private readonly ICommandRunner _commandRunner;
    private readonly PackageArtifactValidator _validator;
    private readonly ICoverageCliConsumerProofWorkflow _coverageProofWorkflow;
    private readonly IDocsPackageConsumerProofWorkflow _docsProofWorkflow;
    private readonly PackagePayloadInventoryLoader _payloadInventoryLoader;
    private readonly PackageArtifactManifestWriter _artifactManifestWriter;

    /// <summary>
    /// Creates a package artifact workflow.
    /// </summary>
    /// <param name="planResolver">Resolver for the manifest-backed publish plan.</param>
    /// <param name="commandRunner">External command runner for restore, build, and pack operations.</param>
    /// <param name="validator">Package artifact validator.</param>
    /// <param name="coverageProofWorkflow">
    /// Packaged coverage CLI proof that installs the validated CLI artifact in a clean consumer fixture before
    /// protected publish jobs can consume the artifact manifest.
    /// </param>
    /// <param name="docsProofWorkflow">
    /// Packed Docs consumer proof that restores the validated Docs artifact in an independent locked consumer before
    /// protected publish jobs can consume the artifact manifest.
    /// </param>
    internal PackageArtifactWorkflow(
        PackagePublishPlanResolver planResolver,
        ICommandRunner commandRunner,
        PackageArtifactValidator validator,
        ICoverageCliConsumerProofWorkflow coverageProofWorkflow,
        IDocsPackageConsumerProofWorkflow docsProofWorkflow)
    {
        _planResolver = planResolver;
        _commandRunner = commandRunner;
        _validator = validator;
        _coverageProofWorkflow = coverageProofWorkflow;
        _docsProofWorkflow = docsProofWorkflow;
        _payloadInventoryLoader = new PackagePayloadInventoryLoader();
        _artifactManifestWriter = new PackageArtifactManifestWriter();
    }

    /// <summary>
    /// Packs and validates package artifacts.
    /// </summary>
    /// <param name="request">Artifact workflow request.</param>
    /// <param name="cancellationToken">Cancellation token propagated to external commands and file writes.</param>
    /// <returns>Successful validation report.</returns>
    internal async Task<PackageArtifactValidationReport> RunAsync(
        PackageArtifactRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        PackageVersionValidator.Require(request.PackageVersion, PackageVersionPolicy.StableOrPrereleaseNoBuildMetadata);
        PackageProofWorkDirectory.RequireDisjoint(
            request.CoverageProofWorkDirectory,
            request.DocsProofWorkDirectory);
        var tailwindProofWorkDirectory = Path.Join(Path.GetTempPath(), "appsurface-tailwind-package-consumer-proof", Guid.NewGuid().ToString("N"));
        var tailwindProofReportPath = Path.Join(request.ArtifactsOutputPath, "tailwind-package-consumer-proof.md");
        PackageProofWorkDirectory.RequireDisjoint(
            tailwindProofWorkDirectory,
            request.CoverageProofWorkDirectory);
        PackageProofWorkDirectory.RequireDisjoint(
            tailwindProofWorkDirectory,
            request.DocsProofWorkDirectory);
        PackageProofWorkDirectory.Prepare(
            request.CoverageProofWorkDirectory,
            request.RepositoryRoot,
            request.ArtifactsOutputPath);
        PackageProofWorkDirectory.Prepare(
            request.DocsProofWorkDirectory,
            request.RepositoryRoot,
            request.ArtifactsOutputPath);

        var plan = await _planResolver.ResolveAsync(
            request.RepositoryRoot,
            request.ManifestPath,
            cancellationToken);
        var payloadInventory = await _payloadInventoryLoader.LoadAsync(
            request.RepositoryRoot,
            cancellationToken);

        Directory.CreateDirectory(request.ArtifactsOutputPath);
        CleanPackageArtifacts(request.ArtifactsOutputPath);
        DeleteArtifactManifest(request.ArtifactManifestPath);

        await RunRepositoryCommandAsync(
            request,
            [
                "restore",
                "ForgeTrust.AppSurface.slnx",
                "--configfile",
                "NuGet.package-gate.config",
                "/p:ContinuousIntegrationBuild=true",
            ],
            "dotnet restore",
            "repository",
            "restore",
            "restoring",
            RestoreTimeoutMilliseconds,
            cancellationToken);

        await RunRepositoryCommandAsync(
            request,
            [
                "build",
                "ForgeTrust.AppSurface.slnx",
                "--configuration",
                "Release",
                "--no-restore",
                $"/p:Version={request.PackageVersion}",
                $"/p:PackageVersion={request.PackageVersion}",
                "/p:ContinuousIntegrationBuild=true",
            ],
            "dotnet build",
            "repository",
            "build",
            "building",
            BuildTimeoutMilliseconds,
            cancellationToken);

        foreach (var entry in plan.Entries)
        {
            await RunRepositoryCommandAsync(
                request,
                [
                    "pack",
                    entry.ProjectPath,
                    "--configuration",
                    "Release",
                    "--no-restore",
                    "--no-build",
                    "--output",
                    request.ArtifactsOutputPath,
                    $"/p:Version={request.PackageVersion}",
                    $"/p:PackageVersion={request.PackageVersion}",
                    "/p:ContinuousIntegrationBuild=true",
                ],
                "dotnet pack",
                entry.ProjectPath,
                "pack",
                "packing",
                PackTimeoutMilliseconds,
                cancellationToken);
        }

        var report = _validator.Validate(
            plan,
            request.ArtifactsOutputPath,
            request.PackageVersion,
            request.RepositoryRoot,
            payloadInventory);
        if (plan.Entries.Any(static entry => string.Equals(
                entry.PackageId,
                TailwindMainPackageId,
                StringComparison.OrdinalIgnoreCase)))
        {
            DeleteFileIfPresent(tailwindProofReportPath);
            try
            {
                await RunTailwindPackedConsumerProofAsync(
                    request,
                    tailwindProofWorkDirectory,
                    tailwindProofReportPath,
                    cancellationToken);
            }
            finally
            {
                TryDeleteProofWorkspace(tailwindProofWorkDirectory);
            }
        }
        var coverageProofReport = await _coverageProofWorkflow.RunAsync(
            new CoverageCliConsumerProofRequest(
                request.RepositoryRoot,
                request.ArtifactsOutputPath,
                request.PackageVersion,
                request.CoverageProofWorkDirectory,
                request.Source),
            report,
            cancellationToken);
        CreateParentDirectoryIfPresent(request.CoverageProofReportPath);
        await File.WriteAllTextAsync(
            request.CoverageProofReportPath,
            CoverageCliConsumerProofReportRenderer.RenderMarkdown(coverageProofReport),
            cancellationToken);
        var coverageProofEvidencePath = Path.Join(
            Path.GetDirectoryName(request.CoverageProofReportPath)!,
            "coverage-cli-consumer-proof.evidence.json");
        await WriteCoverageProofEvidenceAsync(
            coverageProofEvidencePath,
            CoverageCliConsumerProofEvidenceRenderer.RenderJson(coverageProofReport),
            request.ArtifactsOutputPath,
            cancellationToken);

        var docsProofReport = await _docsProofWorkflow.RunAsync(
            new DocsPackageConsumerProofRequest(
                request.RepositoryRoot,
                request.ArtifactsOutputPath,
                request.PackageVersion,
                request.DocsProofWorkDirectory,
                request.Source),
            report,
            cancellationToken);
        CreateParentDirectoryIfPresent(request.DocsProofReportPath);
        await File.WriteAllTextAsync(
            request.DocsProofReportPath,
            DocsPackageConsumerProofReportRenderer.RenderMarkdown(docsProofReport),
            cancellationToken);

        CreateParentDirectoryIfPresent(request.ReportPath);
        await File.WriteAllTextAsync(
            request.ReportPath,
            PackageArtifactReportRenderer.RenderMarkdown(report, coverageProofReport, docsProofReport),
            cancellationToken);

        if (!coverageProofReport.Succeeded || !docsProofReport.Succeeded)
        {
            DeleteArtifactManifest(request.ArtifactManifestPath);
            var failedProofs = new List<string>();
            if (!coverageProofReport.Succeeded)
            {
                failedProofs.Add($"Coverage CLI consumer proof failed. Report: {request.CoverageProofReportPath}");
            }

            if (!docsProofReport.Succeeded)
            {
                failedProofs.Add($"Docs package consumer proof failed. Report: {request.DocsProofReportPath}");
            }

            throw new PackageIndexException(string.Join(" ", failedProofs));
        }

        await _artifactManifestWriter.WriteAsync(
            report,
            request.ArtifactsOutputPath,
            request.ArtifactManifestPath,
            cancellationToken);

        return report;
    }

    private static void ValidateRequest(PackageArtifactRequest request)
    {
        if (!Directory.Exists(request.RepositoryRoot))
        {
            throw new PackageIndexException($"Repository root '{request.RepositoryRoot}' does not exist.");
        }

        if (!File.Exists(request.ManifestPath))
        {
            throw new PackageIndexException(
                $"Manifest '{Path.GetRelativePath(request.RepositoryRoot, request.ManifestPath)}' does not exist.");
        }

        if (string.IsNullOrWhiteSpace(request.ArtifactsOutputPath))
        {
            throw new PackageIndexException("Package artifact output path must be provided.");
        }

        if (string.IsNullOrWhiteSpace(request.ReportPath))
        {
            throw new PackageIndexException("Package artifact report path must be provided.");
        }

        if (string.IsNullOrWhiteSpace(request.ArtifactManifestPath))
        {
            throw new PackageIndexException("Package artifact manifest path must be provided.");
        }

        if (string.IsNullOrWhiteSpace(request.CoverageProofWorkDirectory))
        {
            throw new PackageIndexException("Coverage CLI consumer proof work directory must be provided.");
        }

        if (string.IsNullOrWhiteSpace(request.CoverageProofReportPath))
        {
            throw new PackageIndexException("Coverage CLI consumer proof report path must be provided.");
        }

        if (string.IsNullOrWhiteSpace(request.DocsProofWorkDirectory))
        {
            throw new PackageIndexException("Docs package consumer proof work directory must be provided.");
        }

        if (string.IsNullOrWhiteSpace(request.DocsProofReportPath))
        {
            throw new PackageIndexException("Docs package consumer proof report path must be provided.");
        }

        if (string.IsNullOrWhiteSpace(request.Source))
        {
            throw new PackageIndexException("Package source must be provided.");
        }
    }

    private async Task RunRepositoryCommandAsync(
        PackageArtifactRequest request,
        IReadOnlyList<string> arguments,
        string operationName,
        string subject,
        string failureVerb,
        string timeoutDescription,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        await _commandRunner.RunAsync(
            new CommandRunRequest(
                "dotnet",
                arguments,
                request.RepositoryRoot,
                operationName,
                subject,
                failureVerb,
                timeoutDescription,
                timeoutMilliseconds,
                new Dictionary<string, string?>
                {
                    ["CI"] = "true",
                    ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                    ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1",
                    ["DOTNET_NOLOGO"] = "1",
                    ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
                }),
            cancellationToken);
    }

    private async Task RunTailwindPackedConsumerProofAsync(
        PackageArtifactRequest request,
        string proofWorkDirectory,
        string proofReportPath,
        CancellationToken cancellationToken)
    {
        PackageProofWorkDirectory.Prepare(
            proofWorkDirectory,
            request.RepositoryRoot,
            request.ArtifactsOutputPath);
        await _commandRunner.RunAsync(
            new CommandRunRequest(
                "bash",
                [
                    "scripts/verify-tailwind-package-consumer.sh",
                    "--artifacts",
                    request.ArtifactsOutputPath,
                    "--package-version",
                    request.PackageVersion,
                    "--work-directory",
                    proofWorkDirectory,
                    "--report-path",
                    proofReportPath
                ],
                request.RepositoryRoot,
                "Tailwind packed consumer proof",
                "ForgeTrust.AppSurface.Web.Tailwind",
                "verify",
                "verifying the packed Tailwind consumer",
                TailwindConsumerProofTimeoutMilliseconds,
                new Dictionary<string, string?>
                {
                    ["CI"] = "true",
                    ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                    ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1",
                    ["DOTNET_NOLOGO"] = "1",
                    ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
                }),
            cancellationToken);
    }

    private static void CleanPackageArtifacts(string artifactsOutputPath)
    {
        foreach (var packagePath in Directory.EnumerateFiles(artifactsOutputPath, "*.nupkg", SearchOption.TopDirectoryOnly))
        {
            File.Delete(packagePath);
        }

        foreach (var symbolPackagePath in Directory.EnumerateFiles(artifactsOutputPath, "*.snupkg", SearchOption.TopDirectoryOnly))
        {
            File.Delete(symbolPackagePath);
        }
    }

    private static void TryDeleteProofWorkspace(string proofWorkDirectory)
    {
        TryDeleteProofWorkspace(
            proofWorkDirectory,
            Directory.Exists,
            static directory => Directory.Delete(directory, recursive: true));
    }

    /// <summary>
    /// Deletes a temporary proof workspace and tolerates cleanup failures after proof evidence has been published.
    /// </summary>
    /// <param name="proofWorkDirectory">Temporary workspace to delete.</param>
    /// <param name="directoryExists">Directory existence probe.</param>
    /// <param name="deleteDirectory">Recursive directory deletion operation.</param>
    internal static void TryDeleteProofWorkspace(
        string proofWorkDirectory,
        Func<string, bool> directoryExists,
        Action<string> deleteDirectory)
    {
        try
        {
            if (directoryExists(proofWorkDirectory))
            {
                deleteDirectory(proofWorkDirectory);
            }
        }
        catch (IOException)
        {
            // A temporary proof workspace never participates in the uploaded package artifact.
        }
        catch (UnauthorizedAccessException)
        {
            // A temporary proof workspace never participates in the uploaded package artifact.
        }
    }

    private static void DeleteArtifactManifest(string artifactManifestPath)
    {
        DeleteFileIfPresent(artifactManifestPath);
    }

    private static void DeleteFileIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void CreateParentDirectoryIfPresent(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>
    /// Atomically publishes scrubbed coverage proof evidence without following an existing symbolic-link destination.
    /// </summary>
    /// <param name="evidencePath">Final evidence path below the trusted artifact output directory.</param>
    /// <param name="contents">Already-scrubbed JSON payload.</param>
    /// <param name="trustedRootDirectory">Regular artifact output directory that bounds every evidence directory component.</param>
    /// <param name="cancellationToken">Cancellation token for temporary-file creation.</param>
    /// <param name="temporaryFileCreated">
    /// Optional narrow test seam invoked after the temporary file is closed and before the destination is replaced.
    /// </param>
    /// <returns>A task that completes after the temporary file has replaced a regular destination.</returns>
    internal static async Task WriteCoverageProofEvidenceAsync(
        string evidencePath,
        string contents,
        string trustedRootDirectory,
        CancellationToken cancellationToken,
        Action<string>? temporaryFileCreated = null)
    {
        var parentDirectory = Path.GetDirectoryName(evidencePath);
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            throw new PackageIndexException("Coverage proof evidence path does not have a parent directory.");
        }
        EnsureRegularDirectoryChain(trustedRootDirectory, parentDirectory);
        EnsureRegularOrAbsentFile(evidencePath);
        var temporaryPath = Path.Join(parentDirectory, $".{Path.GetFileName(evidencePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new System.IO.FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.Asynchronous))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(contents), cancellationToken);
            }

            temporaryFileCreated?.Invoke(temporaryPath);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRegularDirectoryChain(trustedRootDirectory, parentDirectory);
            EnsureRegularOrAbsentFile(evidencePath);
            File.Move(temporaryPath, evidencePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void EnsureRegularDirectoryChain(string trustedRootDirectory, string directoryPath)
    {
        var root = new DirectoryInfo(Path.GetFullPath(trustedRootDirectory));
        if (!root.Exists || root.LinkTarget is not null)
        {
            throw new PackageIndexException($"Coverage proof evidence root '{root.FullName}' must be a regular existing directory.");
        }

        var relative = Path.GetRelativePath(root.FullName, Path.GetFullPath(directoryPath));
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new PackageIndexException($"Coverage proof evidence directory '{directoryPath}' must be contained by artifact output '{root.FullName}'.");
        }

        var current = root;
        foreach (var component in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = new DirectoryInfo(Path.Join(current.FullName, component));
            if (!current.Exists || current.LinkTarget is not null)
            {
                throw new PackageIndexException($"Coverage proof evidence directory '{current.FullName}' must be a regular existing directory.");
            }
        }
    }

    private static void EnsureRegularOrAbsentFile(string path)
    {
        if (Directory.Exists(path))
        {
            throw new PackageIndexException(
                $"Coverage proof evidence destination '{path}' must be a regular file or absent; a directory already exists at that path.");
        }

        var file = new FileInfo(path);
        if (file.LinkTarget is not null)
        {
            throw new PackageIndexException($"Coverage proof evidence destination '{path}' must not be a symbolic link.");
        }
    }
}

/// <summary>
/// Request for package artifact packing and validation.
/// </summary>
/// <param name="RepositoryRoot">Absolute repository root.</param>
/// <param name="ManifestPath">Absolute package manifest path.</param>
/// <param name="ArtifactsOutputPath">Directory that receives produced <c>.nupkg</c> artifacts.</param>
/// <param name="ReportPath">Markdown validation report path.</param>
/// <param name="PackageVersion">Exact stable or prerelease package version to pack and validate. SemVer build metadata such as <c>1.2.3+sha</c> is rejected because NuGet strips it from package identity.</param>
/// <param name="ArtifactManifestPath">Machine-readable validation manifest path for the publish workflow.</param>
/// <param name="CoverageProofWorkDirectory">Isolated work directory for the packaged coverage CLI consumer proof.</param>
/// <param name="CoverageProofReportPath">Standalone markdown report path for the packaged coverage CLI consumer proof.</param>
/// <param name="DocsProofWorkDirectory">Isolated work directory for the packed Docs consumer proof.</param>
/// <param name="DocsProofReportPath">Standalone markdown report path for the packed Docs consumer proof.</param>
/// <param name="Source">NuGet source used for third-party dependencies while first-party packages map to local artifacts.</param>
internal sealed record PackageArtifactRequest(
    string RepositoryRoot,
    string ManifestPath,
    string ArtifactsOutputPath,
    string ReportPath,
    string PackageVersion,
    string ArtifactManifestPath,
    string CoverageProofWorkDirectory,
    string CoverageProofReportPath,
    string DocsProofWorkDirectory,
    string DocsProofReportPath,
    string Source);
