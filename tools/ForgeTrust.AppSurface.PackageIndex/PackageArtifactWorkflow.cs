namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>
/// Coordinates prerelease package artifact packing and validation without publishing to NuGet.
/// </summary>
internal sealed class PackageArtifactWorkflow
{
    internal const int RestoreTimeoutMilliseconds = 180_000;
    internal const int BuildTimeoutMilliseconds = 300_000;
    internal const int PackTimeoutMilliseconds = 180_000;

    private readonly PackagePublishPlanResolver _planResolver;
    private readonly ICommandRunner _commandRunner;
    private readonly PackageArtifactValidator _validator;

    /// <summary>
    /// Creates a package artifact workflow.
    /// </summary>
    /// <param name="planResolver">Resolver for the manifest-backed publish plan.</param>
    /// <param name="commandRunner">External command runner for restore, build, and pack operations.</param>
    /// <param name="validator">Package artifact validator.</param>
    internal PackageArtifactWorkflow(
        PackagePublishPlanResolver planResolver,
        ICommandRunner commandRunner,
        PackageArtifactValidator validator)
    {
        _planResolver = planResolver;
        _commandRunner = commandRunner;
        _validator = validator;
    }

    /// <summary>
    /// Packs and validates prerelease package artifacts.
    /// </summary>
    /// <param name="request">Artifact workflow request.</param>
    /// <param name="cancellationToken">Cancellation token propagated to external commands and file writes.</param>
    /// <returns>Successful validation report.</returns>
    internal async Task<PackageArtifactValidationReport> RunAsync(
        PackageArtifactRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        PackageVersionValidator.RequirePrerelease(request.PackageVersion);

        var plan = await _planResolver.ResolveAsync(
            request.RepositoryRoot,
            request.ManifestPath,
            cancellationToken);

        Directory.CreateDirectory(request.ArtifactsOutputPath);
        CleanPackageArtifacts(request.ArtifactsOutputPath);

        await RunRepositoryCommandAsync(
            request,
            [
                "restore",
                "ForgeTrust.AppSurface.slnx",
                "/p:ContinuousIntegrationBuild=true"
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
                "/p:ContinuousIntegrationBuild=true"
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
                    "/p:ContinuousIntegrationBuild=true"
                ],
                "dotnet pack",
                entry.ProjectPath,
                "pack",
                "packing",
                PackTimeoutMilliseconds,
                cancellationToken);
        }

        var report = _validator.Validate(plan, request.ArtifactsOutputPath, request.PackageVersion);
        Directory.CreateDirectory(Path.GetDirectoryName(request.ReportPath)!);
        await File.WriteAllTextAsync(
            request.ReportPath,
            PackageArtifactReportRenderer.RenderMarkdown(report),
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
}

/// <summary>
/// Request for package artifact packing and validation.
/// </summary>
/// <param name="RepositoryRoot">Absolute repository root.</param>
/// <param name="ManifestPath">Absolute package manifest path.</param>
/// <param name="ArtifactsOutputPath">Directory that receives produced <c>.nupkg</c> artifacts.</param>
/// <param name="ReportPath">Markdown validation report path.</param>
/// <param name="PackageVersion">Exact prerelease package version to pack and validate.</param>
internal sealed record PackageArtifactRequest(
    string RepositoryRoot,
    string ManifestPath,
    string ArtifactsOutputPath,
    string ReportPath,
    string PackageVersion);
