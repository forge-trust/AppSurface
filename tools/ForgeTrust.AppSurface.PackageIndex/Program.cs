using System.Diagnostics.CodeAnalysis;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>
/// CLI entry point for generating or verifying the package chooser and maintainer readiness dashboard.
/// </summary>
internal static class Program
{
    private const string GenerateCommand = "generate";
    private const string VerifyPackagesCommand = "verify-packages";
    private const string VerifyCommand = "verify";
    private const string GateCommand = "gate";
    private const string PublishPrereleaseCommand = "publish-prerelease";
    private const string PublishStableCommand = "publish-stable";
    private const string SmokeInstallCommand = "smoke-install";
    private const string ReleasePreparationWitnessCommand = "release-prep-witness";

    private static readonly string Usage = """
        ForgeTrust.AppSurface.PackageIndex

        Generates and verifies the public AppSurface package chooser, maintainer readiness dashboard, release gate, and package artifacts.

        Usage:
          dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- <command> [options]

        Commands:
          generate    Rewrites packages/README.md and packages/readiness.md, then reconciles managed package README release guidance.
          verify      Check that generated package-index documents and managed package README release guidance are already up to date; does not write files.
          verify-packages
                      Pack and validate stable or prerelease .nupkg artifacts without SemVer build metadata, without publishing them.
          publish-prerelease
                      Publish validated prerelease package artifacts to NuGet from a protected workflow job.
          publish-stable
                      Publish validated stable package artifacts to NuGet from a protected workflow job.
          smoke-install
                      Restore published packages from a clean NuGet configuration.
          release-prep-witness
                      Emit a read-only JSON witness for generated package documentation in a release-preparation diff.
          gate        Validate release metadata, package class rules, stale brand strings, and managed release-guidance policy; does not write files.

        Options:
          --repo-root <path>    Repository root. Defaults to the current directory.
          --manifest <path>     Package manifest path. Defaults to packages/package-index.yml.
          --output <path>       Generated chooser path. Defaults to packages/README.md.
          --readiness-output <path>
                                Generated package readiness dashboard path. Defaults to packages/readiness.md.
          --artifacts-output <path>
                                Package artifact output directory. Defaults to artifacts/packages.
          --artifacts-input <path>
                                Validated package artifact input directory. Defaults to artifacts/packages.
          --artifact-manifest <path>
                                Machine-readable validated artifact manifest path. Defaults to artifacts/package-artifact-manifest.json.
          --package-version <version>
                                Required stable or prerelease package version without SemVer build metadata for verify-packages.
          --report <path>       Package artifact report path. Defaults to artifacts/package-validation-report.md.
          --coverage-proof-work-dir <path>
                                Isolated packaged coverage CLI proof work directory. Defaults to <artifacts-output>/coverage-cli-consumer-proof.
          --coverage-proof-report <path>
                                Packaged coverage CLI proof report path. Defaults to <artifacts-output>/coverage-cli-consumer-proof.md.
          --docs-proof-work-dir <path>
                                Isolated packed Docs consumer proof work directory. Defaults to <artifacts-output>/docs-package-consumer-proof.
          --docs-proof-report <path>
                                Packed Docs consumer proof report path. Defaults to <artifacts-output>/docs-package-consumer-proof.md.
          --publish-log <path>  Publish ledger path. Defaults to artifacts/package-publish-log.md.
          --source <url>        NuGet source URL. Defaults to https://api.nuget.org/v3/index.json.
          --api-key-env <name>  Environment variable containing the NuGet API key. Defaults to NUGET_API_KEY.
          --smoke-work-dir <path>
                                Isolated smoke install work directory. Defaults to artifacts/package-smoke.
          --smoke-report <path> Smoke install report path. Defaults to artifacts/package-smoke-report.md.
          --base-ref <ref>      Required fetched base ref or commit for release-prep-witness.
          --witness <path>      Required JSON witness output path for release-prep-witness; normally a temporary path.
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
    /// <param name="publishStableAsync">Optional stable publish workflow override used by tests.</param>
    /// <param name="smokeInstallAsync">Optional smoke install workflow override used by tests.</param>
    /// <returns><c>0</c> when the command succeeds; otherwise a non-zero exit code.</returns>
    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter standardOut,
        TextWriter standardError,
        string currentDirectory,
        CancellationToken cancellationToken = default,
        Func<PackageArtifactRequest, CancellationToken, Task<PackageArtifactValidationReport>>? verifyPackagesAsync = null,
        Func<PackagePublishRequest, CancellationToken, Task<PackagePublishLedger>>? publishPrereleaseAsync = null,
        Func<PackagePublishRequest, CancellationToken, Task<PackagePublishLedger>>? publishStableAsync = null,
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
                and not PublishStableCommand
                and not SmokeInstallCommand
                and not ReleasePreparationWitnessCommand)
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
                var generationReport = await generator.GenerateToFileAsync(options.Request, cancellationToken);
                await standardOut.WriteLineAsync(
                    $"Generated {FormatDisplayPath(options.Request.RepositoryRoot, options.Request.ChooserOutputPath)} and {FormatDisplayPath(options.Request.RepositoryRoot, options.Request.ReadinessOutputPath)}. Reconciled {generationReport.ChangedReleaseGuidanceCount} of {generationReport.ManagedReleaseGuidanceCount} managed package README release-guidance region(s).");
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
                var publishRequest = options.CreatePackagePublishRequest();
                publishPrereleaseAsync ??= RunPackagePublishWorkflowAsync;
                var ledger = await publishPrereleaseAsync(publishRequest, cancellationToken);
                var reportPath = FormatDisplayPath(publishRequest.RepositoryRoot, publishRequest.PublishLogPath);
                await standardOut.WriteLineAsync(
                    $"Published {ledger.Entries.Count(entry => entry.Status is PackagePublishStatus.Pushed or PackagePublishStatus.DuplicateReported)} prerelease package artifacts for {ledger.PackageVersion}. Log: {reportPath}.");
                return ledger.Entries.Any(entry => entry.Status == PackagePublishStatus.Failed) ? 1 : 0;
            }

            if (normalizedCommand == PublishStableCommand)
            {
                var publishRequest = options.CreatePackagePublishRequest();
                publishStableAsync ??= RunPackageStablePublishWorkflowAsync;
                var ledger = await publishStableAsync(publishRequest, cancellationToken);
                var reportPath = FormatDisplayPath(publishRequest.RepositoryRoot, publishRequest.PublishLogPath);
                await standardOut.WriteLineAsync(
                    $"Published {ledger.Entries.Count(entry => entry.Status is PackagePublishStatus.Pushed or PackagePublishStatus.DuplicateReported)} stable package artifacts for {ledger.PackageVersion}. Log: {reportPath}.");
                return ledger.Entries.Any(entry => entry.Status == PackagePublishStatus.Failed) ? 1 : 0;
            }

            if (normalizedCommand == SmokeInstallCommand)
            {
                var smokeRequest = options.CreatePackageSmokeInstallRequest();
                smokeInstallAsync ??= RunPackageSmokeInstallWorkflowAsync;
                var smokeReport = await smokeInstallAsync(smokeRequest, cancellationToken);
                var reportPath = FormatDisplayPath(smokeRequest.RepositoryRoot, smokeRequest.ReportPath);
                await standardOut.WriteLineAsync(
                    $"Smoke installed {smokeReport.Entries.Count(entry => entry.Status == PackageSmokeInstallStatus.Restored)} published packages for {smokeReport.PackageVersion}. Report: {reportPath}.");
                return smokeReport.Entries.Any(entry => entry.Status == PackageSmokeInstallStatus.Failed) ? 1 : 0;
            }

            if (normalizedCommand == VerifyCommand)
            {
                await generator.VerifyAsync(options.Request, cancellationToken);
                await standardOut.WriteLineAsync(
                    $"Generated package-index documents and managed package README release guidance are up to date: {FormatDisplayPath(options.Request.RepositoryRoot, options.Request.ChooserOutputPath)}, {FormatDisplayPath(options.Request.RepositoryRoot, options.Request.ReadinessOutputPath)}.");
                return 0;
            }

            if (normalizedCommand == ReleasePreparationWitnessCommand)
            {
                var request = options.CreateReleasePreparationWitnessRequest();
                var witnessBuilder = new ReleasePreparationWitnessBuilder(generator);
                var witness = await witnessBuilder.CreateAsync(request.Request, request.BaseRef, cancellationToken);
                await ReleasePreparationWitnessBuilder.WriteAsync(witness, request.WitnessPath, cancellationToken);
                await standardOut.WriteLineAsync(
                    $"Wrote release-preparation package witness with {witness.ChangedInputs.Count} changed input(s) and {witness.Surfaces.Count} generated surface(s): {request.WitnessPath}.");
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
            new PackageArtifactValidator(),
            new CoverageCliConsumerProofWorkflow(new CliWrapCommandRunner()),
            new DocsPackageConsumerProofWorkflow(new CliWrapCommandRunner()));
        return await workflow.RunAsync(packageRequest, cancellationToken);
    }

    [ExcludeFromCodeCoverage(Justification = "Default CLI dependency wiring is covered by package prerelease workflow tests.")]
    private static async Task<PackagePublishLedger> RunPackagePublishWorkflowAsync(
        PackagePublishRequest request,
        CancellationToken cancellationToken)
    {
        var workflow = new PackagePublishWorkflow(
            new PackagePublishPlanResolver(
                new PackageProjectScanner(),
                new DotNetProjectMetadataProvider(),
                new PackageManifestLoader()),
            new PackageArtifactManifestReader(PackageVersionPolicy.PrereleaseOnly),
            new CliWrapCommandRunner(),
            new PackagePublishLedgerRenderer());
        return await workflow.RunAsync(request, cancellationToken);
    }

    [ExcludeFromCodeCoverage(Justification = "Default CLI dependency wiring is covered by package stable workflow tests.")]
    private static async Task<PackagePublishLedger> RunPackageStablePublishWorkflowAsync(
        PackagePublishRequest request,
        CancellationToken cancellationToken)
    {
        var workflow = new PackagePublishWorkflow(
            new PackagePublishPlanResolver(
                new PackageProjectScanner(),
                new DotNetProjectMetadataProvider(),
                new PackageManifestLoader()),
            new PackageArtifactManifestReader(PackageVersionPolicy.StableOnly),
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
/// Parsed CLI options for a package-index command that may read and write package chooser and readiness dashboard outputs.
/// </summary>
/// <param name="Request">Resolved package chooser manifest, chooser output, and readiness dashboard output request derived from command-line options.</param>
/// <param name="ArtifactsOutputPath">Resolved package artifact output directory.</param>
/// <param name="ReportPath">Resolved package artifact validation report path.</param>
/// <param name="PackageVersion">Optional package version supplied for package artifact verification.</param>
/// <param name="ArtifactsInputPath">Resolved package artifact input directory for protected publish jobs.</param>
/// <param name="ArtifactManifestPath">Resolved machine-readable package artifact manifest path.</param>
/// <param name="CoverageProofWorkDirectory">Resolved packaged coverage CLI consumer proof work directory.</param>
/// <param name="CoverageProofReportPath">Resolved packaged coverage CLI consumer proof report path.</param>
/// <param name="DocsProofWorkDirectory">Resolved packed Docs consumer proof work directory.</param>
/// <param name="DocsProofReportPath">Resolved packed Docs consumer proof report path.</param>
/// <param name="PublishLogPath">Resolved protected publish ledger path.</param>
/// <param name="Source">NuGet source URL used for publish and smoke install.</param>
/// <param name="ApiKeyEnvironmentVariable">Environment variable name that supplies the NuGet API key.</param>
/// <param name="SmokeWorkDirectory">Resolved isolated smoke install work directory.</param>
/// <param name="SmokeReportPath">Resolved smoke install report path.</param>
/// <param name="BaseRef">Optional fetched base ref or commit used only by the release-preparation witness command.</param>
/// <param name="WitnessPath">Optional explicit JSON witness destination used only by the release-preparation witness command.</param>
internal sealed record CommandLineOptions(
    PackageIndexRequest Request,
    string ArtifactsOutputPath,
    string ReportPath,
    string? PackageVersion,
    string ArtifactsInputPath,
    string ArtifactManifestPath,
    string CoverageProofWorkDirectory,
    string CoverageProofReportPath,
    string DocsProofWorkDirectory,
    string DocsProofReportPath,
    string PublishLogPath,
    string Source,
    string ApiKeyEnvironmentVariable,
    string SmokeWorkDirectory,
    string SmokeReportPath,
    string? BaseRef,
    string? WitnessPath)
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
        string? readinessOutputPath = null;
        string? artifactsOutputPath = null;
        string? artifactsInputPath = null;
        string? artifactManifestPath = null;
        string? coverageProofWorkDirectory = null;
        string? coverageProofReportPath = null;
        string? docsProofWorkDirectory = null;
        string? docsProofReportPath = null;
        string? packageVersion = null;
        string? reportPath = null;
        string? publishLogPath = null;
        string? source = null;
        string? apiKeyEnvironmentVariable = null;
        string? smokeWorkDirectory = null;
        string? smokeReportPath = null;
        string? baseRef = null;
        string? witnessPath = null;

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

            if (string.Equals(argument, "--readiness-output", StringComparison.Ordinal))
            {
                readinessOutputPath = ReadRequiredValue(args, ref index, argument);
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

            if (string.Equals(argument, "--coverage-proof-work-dir", StringComparison.Ordinal))
            {
                coverageProofWorkDirectory = ReadRequiredValue(args, ref index, argument);
                continue;
            }

            if (string.Equals(argument, "--coverage-proof-report", StringComparison.Ordinal))
            {
                coverageProofReportPath = ReadRequiredValue(args, ref index, argument);
                continue;
            }

            if (string.Equals(argument, "--docs-proof-work-dir", StringComparison.Ordinal))
            {
                docsProofWorkDirectory = ReadRequiredValue(args, ref index, argument);
                continue;
            }

            if (string.Equals(argument, "--docs-proof-report", StringComparison.Ordinal))
            {
                docsProofReportPath = ReadRequiredValue(args, ref index, argument);
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

            if (string.Equals(argument, "--base-ref", StringComparison.Ordinal))
            {
                baseRef = ReadRequiredValue(args, ref index, argument);
                continue;
            }

            if (string.Equals(argument, "--witness", StringComparison.Ordinal))
            {
                witnessPath = ReadRequiredValue(args, ref index, argument);
                continue;
            }

            throw new PackageIndexException($"Unknown option '{argument}'.");
        }

        var repoRoot = ResolvePath(repositoryRoot, currentDirectory, currentDirectory);
        var resolvedManifestPath = ResolvePath(manifestPath, repoRoot, Path.Join(repoRoot, "packages", "package-index.yml"));
        var resolvedOutputPath = ResolvePath(outputPath, repoRoot, Path.Join(repoRoot, "packages", "README.md"));
        var resolvedReadinessOutputPath = ResolvePath(readinessOutputPath, repoRoot, Path.Join(repoRoot, "packages", "readiness.md"));
        var resolvedArtifactsOutputPath = ResolvePath(artifactsOutputPath, repoRoot, Path.Join(repoRoot, "artifacts", "packages"));
        var resolvedArtifactsInputPath = ResolvePath(artifactsInputPath, repoRoot, resolvedArtifactsOutputPath);
        var resolvedArtifactManifestPath = ResolvePath(artifactManifestPath, repoRoot, Path.Join(repoRoot, "artifacts", "package-artifact-manifest.json"));
        var resolvedReportPath = ResolvePath(reportPath, repoRoot, Path.Join(repoRoot, "artifacts", "package-validation-report.md"));
        var resolvedCoverageProofWorkDirectory = ResolvePath(coverageProofWorkDirectory, repoRoot, Path.Join(resolvedArtifactsOutputPath, "coverage-cli-consumer-proof"));
        var resolvedCoverageProofReportPath = ResolvePath(coverageProofReportPath, repoRoot, Path.Join(resolvedArtifactsOutputPath, "coverage-cli-consumer-proof.md"));
        var resolvedDocsProofWorkDirectory = ResolvePath(docsProofWorkDirectory, repoRoot, Path.Join(resolvedArtifactsOutputPath, "docs-package-consumer-proof"));
        var resolvedDocsProofReportPath = ResolvePath(docsProofReportPath, repoRoot, Path.Join(resolvedArtifactsOutputPath, "docs-package-consumer-proof.md"));
        var resolvedPublishLogPath = ResolvePath(publishLogPath, repoRoot, Path.Join(repoRoot, "artifacts", "package-publish-log.md"));
        var resolvedSmokeWorkDirectory = ResolvePath(smokeWorkDirectory, repoRoot, Path.Join(repoRoot, "artifacts", "package-smoke"));
        var resolvedSmokeReportPath = ResolvePath(smokeReportPath, repoRoot, Path.Join(repoRoot, "artifacts", "package-smoke-report.md"));

        return new CommandLineOptions(
            new PackageIndexRequest(repoRoot, resolvedManifestPath, resolvedOutputPath, resolvedReadinessOutputPath),
            resolvedArtifactsOutputPath,
            resolvedReportPath,
            packageVersion,
            resolvedArtifactsInputPath,
            resolvedArtifactManifestPath,
            resolvedCoverageProofWorkDirectory,
            resolvedCoverageProofReportPath,
            resolvedDocsProofWorkDirectory,
            resolvedDocsProofReportPath,
            resolvedPublishLogPath,
            string.IsNullOrWhiteSpace(source) ? "https://api.nuget.org/v3/index.json" : source,
            string.IsNullOrWhiteSpace(apiKeyEnvironmentVariable) ? "NUGET_API_KEY" : apiKeyEnvironmentVariable,
            resolvedSmokeWorkDirectory,
            resolvedSmokeReportPath,
            baseRef,
            string.IsNullOrWhiteSpace(witnessPath) ? null : ResolvePath(witnessPath, repoRoot, witnessPath));
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
            ArtifactManifestPath,
            CoverageProofWorkDirectory,
            CoverageProofReportPath,
            DocsProofWorkDirectory,
            DocsProofReportPath,
            Source);
    }

    /// <summary>
    /// Converts parsed CLI options into a protected package publish request.
    /// </summary>
    /// <returns>The package publish request.</returns>
    internal PackagePublishRequest CreatePackagePublishRequest()
    {
        return new PackagePublishRequest(
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

    /// <summary>
    /// Validates the dedicated read-only release-preparation witness options.
    /// </summary>
    /// <returns>Resolved witness inputs.</returns>
    /// <exception cref="PackageIndexException">Thrown when either required witness option is absent.</exception>
    internal ReleasePreparationWitnessRequest CreateReleasePreparationWitnessRequest()
    {
        if (string.IsNullOrWhiteSpace(BaseRef))
        {
            throw new PackageIndexException("Command 'release-prep-witness' requires '--base-ref <ref>'.");
        }

        if (string.IsNullOrWhiteSpace(WitnessPath))
        {
            throw new PackageIndexException("Command 'release-prep-witness' requires '--witness <path>'.");
        }

        return new ReleasePreparationWitnessRequest(Request, BaseRef, WitnessPath);
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

/// <summary>
/// Resolved inputs for the read-only package release-preparation witness command.
/// </summary>
/// <param name="Request">Package-index generation request rooted at the checked-out repository.</param>
/// <param name="BaseRef">Fetched base ref or immutable base commit used to calculate the merge base.</param>
/// <param name="WitnessPath">Explicit JSON output path, normally outside the repository.</param>
internal sealed record ReleasePreparationWitnessRequest(PackageIndexRequest Request, string BaseRef, string WitnessPath);
