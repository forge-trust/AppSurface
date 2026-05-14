using System.Diagnostics.CodeAnalysis;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>
/// CLI entry point for generating or verifying the package chooser.
/// </summary>
internal static class Program
{
    private const string GenerateCommand = "generate";
    private const string VerifyPackagesCommand = "verify-packages";
    private const string VerifyCommand = "verify";
    private const string GateCommand = "gate";
    private const string PublishPrereleaseCommand = "publish-prerelease";
    private const string SmokeInstallCommand = "smoke-install";

    private static readonly string Usage = """
        ForgeTrust.AppSurface.PackageIndex

        Generates and verifies the public AppSurface package chooser, release gate, and prerelease package artifacts.

        Usage:
          dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- <command> [options]

        Commands:
          generate    Rewrites packages/README.md from packages/package-index.yml and project metadata.
          verify      Check that packages/README.md is already up to date.
          verify-packages
                      Pack and validate prerelease .nupkg artifacts without publishing them.
          publish-prerelease
                      Publish validated prerelease package artifacts to NuGet from a protected workflow job.
          smoke-install
                      Restore published prerelease packages from a clean NuGet configuration.
          gate        Validate release metadata, package class rules, and stale brand strings.

        Options:
          --repo-root <path>    Repository root. Defaults to the current directory.
          --manifest <path>     Package manifest path. Defaults to packages/package-index.yml.
          --output <path>       Generated chooser path. Defaults to packages/README.md.
          --artifacts-output <path>
                                Package artifact output directory. Defaults to artifacts/packages.
          --artifacts-input <path>
                                Validated package artifact input directory. Defaults to artifacts/packages.
          --artifact-manifest <path>
                                Machine-readable validated artifact manifest path. Defaults to artifacts/package-artifact-manifest.json.
          --package-version <version>
                                Required prerelease package version for verify-packages.
          --report <path>       Package artifact report path. Defaults to artifacts/package-validation-report.md.
          --publish-log <path>  Publish ledger path. Defaults to artifacts/package-publish-log.md.
          --source <url>        NuGet source URL. Defaults to https://api.nuget.org/v3/index.json.
          --api-key-env <name>  Environment variable containing the NuGet API key. Defaults to NUGET_API_KEY.
          --smoke-work-dir <path>
                                Isolated smoke install work directory. Defaults to artifacts/package-smoke.
          --smoke-report <path> Smoke install report path. Defaults to artifacts/package-smoke-report.md.
          -h, --help            Show this help.
        """;

    /// <summary>
    /// Launches the package chooser CLI with the current process IO streams and working directory.
    /// </summary>
    /// <param name="args">Command-line arguments supplied to the process.</param>
    /// <returns>Process exit code where <c>0</c> indicates success.</returns>
    internal static async Task<int> Main(string[] args)
    {
        return await RunAsync(args, Console.Out, Console.Error, Directory.GetCurrentDirectory());
    }

    /// <summary>
    /// Runs the package chooser CLI against the supplied IO streams and working directory.
    /// </summary>
    /// <param name="args">
    /// Command-line arguments, including the command and optional path overrides. If any help argument is present,
    /// this method returns usage output before command or option parsing so help remains available from any working
    /// directory.
    /// </param>
    /// <param name="standardOut">Writer that receives success messages and help/usage output.</param>
    /// <param name="standardError">Writer that receives invalid invocation usage and failure messages.</param>
    /// <param name="currentDirectory">Working directory used to resolve default repository-relative paths after help handling.</param>
    /// <param name="cancellationToken">Cancellation token propagated to generator operations.</param>
    /// <param name="verifyPackagesAsync">Optional package artifact workflow override used by tests.</param>
    /// <param name="publishPrereleaseAsync">Optional prerelease publish workflow override used by tests.</param>
    /// <param name="smokeInstallAsync">Optional smoke install workflow override used by tests.</param>
    /// <returns><c>0</c> when the command succeeds; otherwise a non-zero exit code.</returns>
    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter standardOut,
        TextWriter standardError,
        string currentDirectory,
        CancellationToken cancellationToken = default,
        Func<PackageArtifactRequest, CancellationToken, Task<PackageArtifactValidationReport>>? verifyPackagesAsync = null,
        Func<PackagePrereleasePublishRequest, CancellationToken, Task<PackagePublishLedger>>? publishPrereleaseAsync = null,
        Func<PackageSmokeInstallRequest, CancellationToken, Task<PackageSmokeInstallReport>>? smokeInstallAsync = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardOut);
        ArgumentNullException.ThrowIfNull(standardError);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        if (args.Length == 0)
        {
            await standardError.WriteLineAsync(Usage);
            return 1;
        }

        if (IsHelp(args[0]))
        {
            await standardOut.WriteLineAsync(Usage);
            return 0;
        }

        try
        {
            var command = args[0].Trim();
            if (args.Skip(1).Any(IsHelp))
            {
                await standardOut.WriteLineAsync(Usage);
                return 0;
            }

            var normalizedCommand = command.ToLowerInvariant();
            if (normalizedCommand is not GenerateCommand
                and not VerifyCommand
                and not VerifyPackagesCommand
                and not GateCommand
                and not PublishPrereleaseCommand
                and not SmokeInstallCommand)
            {
                await standardError.WriteLineAsync($"Unknown command '{command}'.");
                await standardError.WriteLineAsync(Usage);
                return 1;
            }

            var options = CommandLineOptions.Parse(args.Skip(1).ToArray(), currentDirectory);
            var generator = new PackageIndexGenerator(
                new PackageProjectScanner(),
                new DotNetProjectMetadataProvider(),
                new PackageManifestLoader());

            if (normalizedCommand == GenerateCommand)
            {
                await generator.GenerateToFileAsync(options.Request, cancellationToken);
                var outputPath = Path.GetRelativePath(options.Request.RepositoryRoot, options.Request.OutputPath)
                    .Replace('\\', '/');
                await standardOut.WriteLineAsync($"Generated {outputPath}.");
                return 0;
            }

            if (normalizedCommand == VerifyPackagesCommand)
            {
                var packageRequest = options.CreatePackageArtifactRequest();
                verifyPackagesAsync ??= RunPackageArtifactWorkflowAsync;
                var artifactReport = await verifyPackagesAsync(packageRequest, cancellationToken);
                var reportPath = FormatDisplayPath(packageRequest.RepositoryRoot, packageRequest.ReportPath);
                await standardOut.WriteLineAsync(
                    $"Validated {artifactReport.Entries.Count} package artifacts for {packageRequest.PackageVersion}. Report: {reportPath}.");
                return 0;
            }

            if (normalizedCommand == PublishPrereleaseCommand)
            {
                var publishRequest = options.CreatePackagePrereleasePublishRequest();
                publishPrereleaseAsync ??= RunPackagePrereleasePublishWorkflowAsync;
                var ledger = await publishPrereleaseAsync(publishRequest, cancellationToken);
                var reportPath = FormatDisplayPath(publishRequest.RepositoryRoot, publishRequest.PublishLogPath);
                await standardOut.WriteLineAsync(
                    $"Published {ledger.Entries.Count(entry => entry.Status is PackagePublishStatus.Pushed or PackagePublishStatus.DuplicateReported)} package artifacts for {ledger.PackageVersion}. Log: {reportPath}.");
                return ledger.Entries.Any(entry => entry.Status == PackagePublishStatus.Failed) ? 1 : 0;
            }

            if (normalizedCommand == SmokeInstallCommand)
            {
                var smokeRequest = options.CreatePackageSmokeInstallRequest();
                smokeInstallAsync ??= RunPackageSmokeInstallWorkflowAsync;
                var smokeReport = await smokeInstallAsync(smokeRequest, cancellationToken);
                var reportPath = FormatDisplayPath(smokeRequest.RepositoryRoot, smokeRequest.ReportPath);
                await standardOut.WriteLineAsync(
                    $"Smoke installed {smokeReport.Entries.Count(entry => entry.Status == PackageSmokeInstallStatus.Restored)} published prerelease packages for {smokeReport.PackageVersion}. Report: {reportPath}.");
                return smokeReport.Entries.Any(entry => entry.Status == PackageSmokeInstallStatus.Failed) ? 1 : 0;
            }

            if (normalizedCommand == VerifyCommand)
            {
                await generator.VerifyAsync(options.Request, cancellationToken);
                await standardOut.WriteLineAsync("Package chooser is up to date.");
                return 0;
            }

            var report = await generator.RunPackageGateAsync(options.Request, cancellationToken);
            await standardOut.WriteLineAsync(
                $"Package gate passed for {report.PackageCount} manifest entries and {report.ScannedFileCount} source files.");
            return 0;
        }
        catch (PackageIndexException ex)
        {
            await standardError.WriteLineAsync(ex.Message);
            return 1;
        }
    }

    private static bool IsHelp(string argument)
    {
        return string.Equals(argument, "--help", StringComparison.Ordinal)
            || string.Equals(argument, "-h", StringComparison.Ordinal);
    }

    [ExcludeFromCodeCoverage(Justification = "Default CLI dependency wiring is covered by package artifact workflow tests.")]
    private static async Task<PackageArtifactValidationReport> RunPackageArtifactWorkflowAsync(
        PackageArtifactRequest packageRequest,
        CancellationToken cancellationToken)
    {
        var workflow = new PackageArtifactWorkflow(
            new PackagePublishPlanResolver(
                new PackageProjectScanner(),
                new DotNetProjectMetadataProvider(),
                new PackageManifestLoader()),
            new ProcessCommandRunner(),
            new PackageArtifactValidator());
        return await workflow.RunAsync(packageRequest, cancellationToken);
    }

    [ExcludeFromCodeCoverage(Justification = "Default CLI dependency wiring is covered by package prerelease workflow tests.")]
    private static async Task<PackagePublishLedger> RunPackagePrereleasePublishWorkflowAsync(
        PackagePrereleasePublishRequest request,
        CancellationToken cancellationToken)
    {
        var workflow = new PackagePrereleasePublishWorkflow(
            new PackagePublishPlanResolver(
                new PackageProjectScanner(),
                new DotNetProjectMetadataProvider(),
                new PackageManifestLoader()),
            new PackageArtifactManifestReader(),
            new CliWrapCommandRunner(),
            new PackagePublishLedgerRenderer());
        return await workflow.RunAsync(request, cancellationToken);
    }

    [ExcludeFromCodeCoverage(Justification = "Default CLI dependency wiring is covered by package smoke workflow tests.")]
    private static async Task<PackageSmokeInstallReport> RunPackageSmokeInstallWorkflowAsync(
        PackageSmokeInstallRequest request,
        CancellationToken cancellationToken)
    {
        var workflow = new PackageSmokeInstallWorkflow(
            new PackageArtifactManifestReader(),
            new PackagePublishPlanResolver(
                new PackageProjectScanner(),
                new DotNetProjectMetadataProvider(),
                new PackageManifestLoader()),
            new CliWrapCommandRunner(),
            new PackageSmokeInstallReportRenderer(),
            Task.Delay);
        return await workflow.RunAsync(request, cancellationToken);
    }

    private static string FormatDisplayPath(string repositoryRoot, string path)
    {
        var normalizedRoot = Path.GetFullPath(repositoryRoot);
        var normalizedPath = Path.GetFullPath(path);
        var rootPrefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        var pathComparison = PackageIndexGenerator.RepositoryPathComparison;
        if (string.Equals(normalizedRoot, normalizedPath, pathComparison)
            || normalizedPath.StartsWith(rootPrefix, pathComparison))
        {
            return Path.GetRelativePath(normalizedRoot, normalizedPath).Replace('\\', '/');
        }

        return normalizedPath;
    }
}

/// <summary>
/// Parsed CLI options for one package chooser command invocation.
/// </summary>
/// <param name="Request">Resolved package chooser request derived from command-line options.</param>
/// <param name="ArtifactsOutputPath">Resolved package artifact output directory.</param>
/// <param name="ReportPath">Resolved package artifact validation report path.</param>
/// <param name="PackageVersion">Optional package version supplied for package artifact verification.</param>
/// <param name="ArtifactsInputPath">Resolved package artifact input directory for protected publish jobs.</param>
/// <param name="ArtifactManifestPath">Resolved machine-readable package artifact manifest path.</param>
/// <param name="PublishLogPath">Resolved protected publish ledger path.</param>
/// <param name="Source">NuGet source URL used for publish and smoke install.</param>
/// <param name="ApiKeyEnvironmentVariable">Environment variable name that supplies the NuGet API key.</param>
/// <param name="SmokeWorkDirectory">Resolved isolated smoke install work directory.</param>
/// <param name="SmokeReportPath">Resolved smoke install report path.</param>
internal sealed record CommandLineOptions(
    PackageIndexRequest Request,
    string ArtifactsOutputPath,
    string ReportPath,
    string? PackageVersion,
    string ArtifactsInputPath,
    string ArtifactManifestPath,
    string PublishLogPath,
    string Source,
    string ApiKeyEnvironmentVariable,
    string SmokeWorkDirectory,
    string SmokeReportPath)
{
    /// <summary>
    /// Parses path-related CLI options into a resolved chooser request.
    /// </summary>
    /// <param name="args">Arguments after the command verb.</param>
    /// <param name="currentDirectory">Working directory used to resolve relative overrides.</param>
    /// <returns>The parsed command-line options.</returns>
    /// <exception cref="PackageIndexException">Thrown when an option is unknown or missing its required value.</exception>
    internal static CommandLineOptions Parse(string[] args, string currentDirectory)
    {
        string? repositoryRoot = null;
        string? manifestPath = null;
        string? outputPath = null;
        string? artifactsOutputPath = null;
        string? artifactsInputPath = null;
        string? artifactManifestPath = null;
        string? packageVersion = null;
        string? reportPath = null;
        string? publishLogPath = null;
        string? source = null;
        string? apiKeyEnvironmentVariable = null;
        string? smokeWorkDirectory = null;
        string? smokeReportPath = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--repo-root", StringComparison.Ordinal))
            {
                repositoryRoot = ReadRequiredValue(args, ref index, argument);
                continue;
            }

            if (string.Equals(argument, "--manifest", StringComparison.Ordinal))
            {
                manifestPath = ReadRequiredValue(args, ref index, argument);
                continue;
            }

            if (string.Equals(argument, "--output", StringComparison.Ordinal))
            {
                outputPath = ReadRequiredValue(args, ref index, argument);
                continue;
            }

            if (string.Equals(argument, "--artifacts-output", StringComparison.Ordinal))
            {
                artifactsOutputPath = ReadRequiredValue(args, ref index, argument);
                continue;
            }

            if (string.Equals(argument, "--artifacts-input", StringComparison.Ordinal))
            {
                artifactsInputPath = ReadRequiredValue(args, ref index, argument);
                continue;
            }

            if (string.Equals(argument, "--artifact-manifest", StringComparison.Ordinal))
            {
                artifactManifestPath = ReadRequiredValue(args, ref index, argument);
                continue;
            }

            if (string.Equals(argument, "--package-version", StringComparison.Ordinal))
            {
                packageVersion = ReadRequiredValue(args, ref index, argument);
                continue;
            }

            if (string.Equals(argument, "--report", StringComparison.Ordinal))
            {
                reportPath = ReadRequiredValue(args, ref index, argument);
                continue;
            }

            if (string.Equals(argument, "--publish-log", StringComparison.Ordinal))
            {
                publishLogPath = ReadRequiredValue(args, ref index, argument);
                continue;
            }

            if (string.Equals(argument, "--source", StringComparison.Ordinal))
            {
                source = ReadRequiredValue(args, ref index, argument);
                continue;
            }

            if (string.Equals(argument, "--api-key-env", StringComparison.Ordinal))
            {
                apiKeyEnvironmentVariable = ReadRequiredValue(args, ref index, argument);
                continue;
            }

            if (string.Equals(argument, "--smoke-work-dir", StringComparison.Ordinal))
            {
                smokeWorkDirectory = ReadRequiredValue(args, ref index, argument);
                continue;
            }

            if (string.Equals(argument, "--smoke-report", StringComparison.Ordinal))
            {
                smokeReportPath = ReadRequiredValue(args, ref index, argument);
                continue;
            }

            throw new PackageIndexException($"Unknown option '{argument}'.");
        }

        var repoRoot = ResolvePath(repositoryRoot, currentDirectory, currentDirectory);
        var resolvedManifestPath = ResolvePath(manifestPath, repoRoot, Path.Combine(repoRoot, "packages", "package-index.yml"));
        var resolvedOutputPath = ResolvePath(outputPath, repoRoot, Path.Combine(repoRoot, "packages", "README.md"));
        var resolvedArtifactsOutputPath = ResolvePath(artifactsOutputPath, repoRoot, Path.Combine(repoRoot, "artifacts", "packages"));
        var resolvedArtifactsInputPath = ResolvePath(artifactsInputPath, repoRoot, resolvedArtifactsOutputPath);
        var resolvedArtifactManifestPath = ResolvePath(artifactManifestPath, repoRoot, Path.Combine(repoRoot, "artifacts", "package-artifact-manifest.json"));
        var resolvedReportPath = ResolvePath(reportPath, repoRoot, Path.Combine(repoRoot, "artifacts", "package-validation-report.md"));
        var resolvedPublishLogPath = ResolvePath(publishLogPath, repoRoot, Path.Combine(repoRoot, "artifacts", "package-publish-log.md"));
        var resolvedSmokeWorkDirectory = ResolvePath(smokeWorkDirectory, repoRoot, Path.Combine(repoRoot, "artifacts", "package-smoke"));
        var resolvedSmokeReportPath = ResolvePath(smokeReportPath, repoRoot, Path.Combine(repoRoot, "artifacts", "package-smoke-report.md"));

        return new CommandLineOptions(
            new PackageIndexRequest(repoRoot, resolvedManifestPath, resolvedOutputPath),
            resolvedArtifactsOutputPath,
            resolvedReportPath,
            packageVersion,
            resolvedArtifactsInputPath,
            resolvedArtifactManifestPath,
            resolvedPublishLogPath,
            string.IsNullOrWhiteSpace(source) ? "https://api.nuget.org/v3/index.json" : source,
            string.IsNullOrWhiteSpace(apiKeyEnvironmentVariable) ? "NUGET_API_KEY" : apiKeyEnvironmentVariable,
            resolvedSmokeWorkDirectory,
            resolvedSmokeReportPath);
    }

    /// <summary>
    /// Converts parsed CLI options into a package artifact request.
    /// </summary>
    /// <returns>The package artifact request.</returns>
    /// <exception cref="PackageIndexException">Thrown when the required package version is missing.</exception>
    internal PackageArtifactRequest CreatePackageArtifactRequest()
    {
        if (string.IsNullOrWhiteSpace(PackageVersion))
        {
            throw new PackageIndexException("Command 'verify-packages' requires '--package-version <version>'.");
        }

        return new PackageArtifactRequest(
            Request.RepositoryRoot,
            Request.ManifestPath,
            ArtifactsOutputPath,
            ReportPath,
            PackageVersion,
            ArtifactManifestPath);
    }

    /// <summary>
    /// Converts parsed CLI options into a protected prerelease publish request.
    /// </summary>
    /// <returns>The prerelease publish request.</returns>
    internal PackagePrereleasePublishRequest CreatePackagePrereleasePublishRequest()
    {
        return new PackagePrereleasePublishRequest(
            Request.RepositoryRoot,
            Request.ManifestPath,
            ArtifactsInputPath,
            ArtifactManifestPath,
            PublishLogPath,
            Source,
            ApiKeyEnvironmentVariable);
    }

    /// <summary>
    /// Converts parsed CLI options into a package smoke install request.
    /// </summary>
    /// <returns>The package smoke install request.</returns>
    internal PackageSmokeInstallRequest CreatePackageSmokeInstallRequest()
    {
        return new PackageSmokeInstallRequest(
            Request.RepositoryRoot,
            Request.ManifestPath,
            ArtifactManifestPath,
            SmokeWorkDirectory,
            SmokeReportPath,
            Source);
    }

    private static string ReadRequiredValue(string[] args, ref int index, string argument)
    {
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new PackageIndexException($"Option '{argument}' requires a value.");
        }

        index++;
        return args[index];
    }

    private static string ResolvePath(string? value, string baseDirectory, string defaultPath)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Path.GetFullPath(defaultPath);
        }

        return Path.IsPathRooted(value)
            ? Path.GetFullPath(value)
            : Path.GetFullPath(Path.Combine(baseDirectory, value));
    }
}
