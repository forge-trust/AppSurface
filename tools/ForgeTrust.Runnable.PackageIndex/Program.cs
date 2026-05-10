using System.Diagnostics.CodeAnalysis;

namespace ForgeTrust.Runnable.PackageIndex;

/// <summary>
/// CLI entry point for generating or verifying the package chooser.
/// </summary>
internal static class Program
{
    private const string GenerateCommand = "generate";
    private const string VerifyPackagesCommand = "verify-packages";
    private const string VerifyCommand = "verify";

    private static readonly string Usage = """
        ForgeTrust.Runnable.PackageIndex

        Generates and verifies the public Runnable package chooser and prerelease package artifacts.

        Usage:
          dotnet run --project tools/ForgeTrust.Runnable.PackageIndex/ForgeTrust.Runnable.PackageIndex.csproj -- <command> [options]

        Commands:
          generate    Rewrites packages/README.md from packages/package-index.yml and project metadata.
          verify      Check that packages/README.md is already up to date.
          verify-packages
                      Pack and validate prerelease .nupkg artifacts without publishing them.

        Options:
          --repo-root <path>    Repository root. Defaults to the current directory.
          --manifest <path>     Package manifest path. Defaults to packages/package-index.yml.
          --output <path>       Generated chooser path. Defaults to packages/README.md.
          --artifacts-output <path>
                                Package artifact output directory. Defaults to artifacts/packages.
          --package-version <version>
                                Required prerelease package version for verify-packages.
          --report <path>       Package artifact report path. Defaults to artifacts/package-validation-report.md.
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
    /// <returns><c>0</c> when the command succeeds; otherwise a non-zero exit code.</returns>
    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter standardOut,
        TextWriter standardError,
        string currentDirectory,
        CancellationToken cancellationToken = default,
        Func<PackageArtifactRequest, CancellationToken, Task<PackageArtifactValidationReport>>? verifyPackagesAsync = null)
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
            if (normalizedCommand is not GenerateCommand and not VerifyCommand and not VerifyPackagesCommand)
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
                var report = await verifyPackagesAsync(packageRequest, cancellationToken);
                var reportPath = FormatDisplayPath(packageRequest.RepositoryRoot, packageRequest.ReportPath);
                await standardOut.WriteLineAsync(
                    $"Validated {report.Entries.Count} package artifacts for {packageRequest.PackageVersion}. Report: {reportPath}.");
                return 0;
            }

            await generator.VerifyAsync(options.Request, cancellationToken);
            await standardOut.WriteLineAsync("Package chooser is up to date.");
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

    private static string FormatDisplayPath(string repositoryRoot, string path)
    {
        var normalizedRoot = Path.GetFullPath(repositoryRoot);
        var normalizedPath = Path.GetFullPath(path);
        var rootPrefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        if (string.Equals(normalizedRoot, normalizedPath, StringComparison.Ordinal)
            || normalizedPath.StartsWith(rootPrefix, StringComparison.Ordinal))
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
internal sealed record CommandLineOptions(
    PackageIndexRequest Request,
    string ArtifactsOutputPath,
    string ReportPath,
    string? PackageVersion)
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
        string? packageVersion = null;
        string? reportPath = null;

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

            throw new PackageIndexException($"Unknown option '{argument}'.");
        }

        var repoRoot = ResolvePath(repositoryRoot, currentDirectory, currentDirectory);
        var resolvedManifestPath = ResolvePath(manifestPath, repoRoot, Path.Combine(repoRoot, "packages", "package-index.yml"));
        var resolvedOutputPath = ResolvePath(outputPath, repoRoot, Path.Combine(repoRoot, "packages", "README.md"));
        var resolvedArtifactsOutputPath = ResolvePath(artifactsOutputPath, repoRoot, Path.Combine(repoRoot, "artifacts", "packages"));
        var resolvedReportPath = ResolvePath(reportPath, repoRoot, Path.Combine(repoRoot, "artifacts", "package-validation-report.md"));

        return new CommandLineOptions(
            new PackageIndexRequest(repoRoot, resolvedManifestPath, resolvedOutputPath),
            resolvedArtifactsOutputPath,
            resolvedReportPath,
            packageVersion);
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
            PackageVersion);
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
