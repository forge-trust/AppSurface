using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>
/// Runs the pre-publish consumer proof for the packaged AppSurface coverage CLI.
/// </summary>
internal interface ICoverageCliConsumerProofWorkflow
{
    /// <summary>
    /// Installs the validated CLI artifact into an isolated fixture and exercises the public coverage commands.
    /// </summary>
    /// <param name="request">Proof request with repository, artifact, work-directory, and package-source settings.</param>
    /// <param name="validationReport">Validated package artifact report that selects the CLI tool artifact.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Structured proof report. Command and artifact failures are represented in the report.</returns>
    Task<CoverageCliConsumerProofReport> RunAsync(
        CoverageCliConsumerProofRequest request,
        PackageArtifactValidationReport validationReport,
        CancellationToken cancellationToken);
}

/// <summary>
/// Verifies that the packed <c>ForgeTrust.AppSurface.Cli</c> works from a clean package-consumer project.
/// </summary>
/// <remarks>
/// <para>
/// The proof runs before package publication. It selects the already validated CLI <c>.nupkg</c>, installs it with a
/// local-first NuGet configuration, creates a clean xUnit fixture plus an excluded failing sentinel, and executes <c>coverage run</c>,
/// <c>coverage merge</c>, a passing <c>coverage gate</c>, and an intentionally failing <c>coverage gate</c>.
/// </para>
/// <para>
/// Pitfall: the failing gate is considered successful only when the command exits non-zero and still writes
/// <c>coverage-gate.json</c> and <c>coverage-gate.md</c>. This preserves the CLI contract that failures are visible in
/// local diagnostics rather than only in the process exit code.
/// </para>
/// </remarks>
internal sealed class CoverageCliConsumerProofWorkflow : ICoverageCliConsumerProofWorkflow
{
    internal const string CliPackageId = "ForgeTrust.AppSurface.Cli";
    internal const string CliCommandName = "appsurface";
    internal const int DotNetCommandTimeoutMilliseconds = 180_000;
    internal const int CoverageRunTimeoutMilliseconds = 300_000;

    private readonly IExternalCommandRunner _commandRunner;

    internal CoverageCliConsumerProofWorkflow(IExternalCommandRunner commandRunner)
    {
        _commandRunner = commandRunner;
    }

    /// <inheritdoc />
    public async Task<CoverageCliConsumerProofReport> RunAsync(
        CoverageCliConsumerProofRequest request,
        PackageArtifactValidationReport validationReport,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(validationReport);

        CoverageCliConsumerProofSelectedArtifact selectedArtifact;
        try
        {
            selectedArtifact = await SelectCliToolPackageAsync(validationReport, request.PackageVersion, cancellationToken);
        }
        catch (PackageIndexException ex)
        {
            return CoverageCliConsumerProofReport.Failed(
                request.PackageVersion,
                request.WorkDirectory,
                request.Source,
                ex.Message);
        }

        try
        {
            PrepareWorkDirectory(request.WorkDirectory, request.RepositoryRoot, request.ArtifactsDirectory);
        }
        catch (PackageIndexException ex)
        {
            return CoverageCliConsumerProofReport.Failed(
                request.PackageVersion,
                request.WorkDirectory,
                request.Source,
                ex.Message,
                selectedArtifact);
        }

        var fixtureDirectory = Path.Join(request.WorkDirectory, "consumer");
        var logsDirectory = Path.Join(request.WorkDirectory, "logs");
        var toolNuGetConfigPath = Path.Join(request.WorkDirectory, "NuGet.tool.config");
        var fixtureNuGetConfigPath = Path.Join(fixtureDirectory, "NuGet.config");
        var sharedPackagesPath = Path.Join(request.WorkDirectory, "packages");
        var dotnetHomePath = Path.Join(request.WorkDirectory, "dotnet-home");
        Directory.CreateDirectory(fixtureDirectory);
        Directory.CreateDirectory(logsDirectory);
        Directory.CreateDirectory(sharedPackagesPath);
        Directory.CreateDirectory(dotnetHomePath);

        await File.WriteAllTextAsync(
            toolNuGetConfigPath,
            RenderMappedNuGetConfig(request.ArtifactsDirectory, request.Source),
            cancellationToken);
        await File.WriteAllTextAsync(
            fixtureNuGetConfigPath,
            RenderNuGetOrgOnlyConfig(request.Source),
            cancellationToken);

        var context = new CoverageCliConsumerProofContext(
            request,
            selectedArtifact,
            fixtureDirectory,
            logsDirectory,
            toolNuGetConfigPath,
            fixtureNuGetConfigPath,
            sharedPackagesPath,
            dotnetHomePath);
        var commands = new List<CoverageCliConsumerProofCommandResult>();
        var artifacts = new List<CoverageCliConsumerProofArtifactCheck>();

        async Task<bool> RunRequiredAsync(ExternalCommandRequest commandRequest)
        {
            var result = await RunCommandAsync(commandRequest, logsDirectory, commands.Count + 1, ExpectedCommandExitCode.Zero, cancellationToken);
            commands.Add(result);
            return result.Succeeded;
        }

        if (!await RunRequiredAsync(DotNetCommand(context, ["new", "sln", "-n", "Smoke"], "dotnet new sln", "creating smoke solution")))
        {
            return BuildReport(context, commands, artifacts);
        }

        if (!await RunRequiredAsync(DotNetCommand(context, ["new", "classlib", "-n", "Smoke"], "dotnet new classlib", "creating smoke library")))
        {
            return BuildReport(context, commands, artifacts);
        }

        if (!await RunRequiredAsync(DotNetCommand(context, ["new", "xunit", "-n", "Smoke.Tests"], "dotnet new xunit", "creating smoke tests")))
        {
            return BuildReport(context, commands, artifacts);
        }

        if (!await RunRequiredAsync(DotNetCommand(context, ["new", "xunit", "-n", "Smoke.Browser.Tests"], "dotnet new xunit sentinel", "creating excluded failing sentinel tests")))
        {
            return BuildReport(context, commands, artifacts);
        }

        var solutionPath = ResolveSmokeSolutionPath(fixtureDirectory);
        if (!await RunRequiredAsync(DotNetCommand(context, ["sln", solutionPath, "add", "Smoke/Smoke.csproj", "Smoke.Tests/Smoke.Tests.csproj", "Smoke.Browser.Tests/Smoke.Browser.Tests.csproj"], "dotnet sln add", "adding smoke projects")))
        {
            return BuildReport(context, commands, artifacts);
        }

        if (!await RunRequiredAsync(DotNetCommand(context, ["add", "Smoke.Tests/Smoke.Tests.csproj", "reference", "Smoke/Smoke.csproj"], "dotnet add reference", "adding smoke project reference")))
        {
            return BuildReport(context, commands, artifacts);
        }

        if (!await RunRequiredAsync(DotNetCommand(context, ["add", "Smoke.Tests/Smoke.Tests.csproj", "package", "coverlet.msbuild", "--version", "10.0.1"], "dotnet add package", "adding Coverlet to smoke tests")))
        {
            return BuildReport(context, commands, artifacts);
        }

        await WriteSmokeFixtureAsync(fixtureDirectory, cancellationToken);

        if (!await RunRequiredAsync(DotNetCommand(
            context,
            ["new", "tool-manifest"],
            "dotnet new tool-manifest",
            "creating local tool manifest")))
        {
            return BuildReport(context, commands, artifacts);
        }

        if (!await RunRequiredAsync(DotNetCommand(
            context,
            ["tool", "install", CliPackageId, "--version", request.PackageVersion, "--configfile", toolNuGetConfigPath],
            "dotnet tool install",
            $"installing '{CliPackageId}'")))
        {
            return BuildReport(context, commands, artifacts);
        }

        if (!await RunRequiredAsync(ToolCommand(context, ["--version"], "appsurface --version", "checking installed CLI version")))
        {
            return BuildReport(context, commands, artifacts);
        }

        var versionCommand = commands[^1];
        if (!string.Equals(versionCommand.StandardOutput.Trim(), request.PackageVersion, StringComparison.Ordinal))
        {
            commands[^1] = versionCommand with
            {
                Succeeded = false,
                FailureReason = $"Expected '{CliCommandName} --version' to print '{request.PackageVersion}', but it printed '{versionCommand.StandardOutput.Trim()}'."
            };
            return BuildReport(context, commands, artifacts);
        }

        if (!await RunRequiredAsync(ToolCommand(
            context,
            ["coverage", "run", "--help"],
            "appsurface coverage run --help",
            "checking packaged watchdog option discovery")))
        {
            return BuildReport(context, commands, artifacts);
        }

        var coverageRunHelp = commands[^1];
        var missingWatchdogOptions = new[] { "--watchdog", "--heartbeat-interval", "--no-progress-timeout" }
            .Where(option => !coverageRunHelp.StandardOutput.Contains(option, StringComparison.Ordinal))
            .ToArray();
        if (missingWatchdogOptions.Length > 0)
        {
            commands[^1] = coverageRunHelp with
            {
                Succeeded = false,
                FailureReason = $"Packaged coverage run help omitted watchdog option(s): {string.Join(", ", missingWatchdogOptions)}."
            };
            return BuildReport(context, commands, artifacts);
        }

        var coverageMergedDirectory = Path.Join(fixtureDirectory, "TestResults", "coverage-merged");
        if (!await RunRequiredAsync(ToolCommand(
            context,
            ["coverage", "run", "--solution", solutionPath, "--exclude-test-project", "**/Smoke.Browser.Tests.csproj", "--include", "[Smoke]*", "--output", coverageMergedDirectory],
            "appsurface coverage run",
            "running packaged coverage CLI")))
        {
            return BuildReport(context, commands, artifacts);
        }

        var coverageRunCommand = commands[^1];
        var sentinelProjectPath = "Smoke.Browser.Tests/Smoke.Browser.Tests.csproj";
        var sentinelExclusionPattern = "**/Smoke.Browser.Tests.csproj";
        var reportedSentinelExclusion = coverageRunCommand.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.TrimStart().StartsWith("skip ", StringComparison.Ordinal)
                && line.Contains(sentinelProjectPath, StringComparison.Ordinal)
                && line.Contains($"'{sentinelExclusionPattern}'", StringComparison.Ordinal));
        if (!reportedSentinelExclusion)
        {
            commands[^1] = coverageRunCommand with
            {
                Succeeded = false,
                FailureReason = $"Coverage run did not report excluded sentinel '{sentinelProjectPath}' with pattern '{sentinelExclusionPattern}'."
            };
            return BuildReport(context, commands, artifacts);
        }

        artifacts.AddRange(CheckCoverageRunArtifacts(coverageMergedDirectory));
        artifacts.Add(CheckExcludedProjectArtifacts(coverageMergedDirectory, "Smoke.Browser.Tests"));
        if (artifacts.Any(artifact => !artifact.Exists))
        {
            return BuildReport(context, commands, artifacts);
        }

        var watchdogFailDirectory = Path.Join(fixtureDirectory, "TestResults", "coverage-watchdog-fail");
        var watchdogFailResult = await RunCommandAsync(
            ToolCommand(
                context,
                [
                    "coverage", "run",
                    "--test-project", Path.Join(fixtureDirectory, "Smoke.Tests", "Smoke.Tests.csproj"),
                    "--output", watchdogFailDirectory,
                    "--no-build", "--no-restore",
                    "--watchdog", "fail",
                    "--heartbeat-interval", "100ms",
                    "--no-progress-timeout", "1s",
                ],
                "appsurface coverage run watchdog fail",
                "verifying packaged no-progress exit 124"),
            logsDirectory,
            commands.Count + 1,
            ExpectedCommandExitCode.Watchdog,
            cancellationToken);
        commands.Add(watchdogFailResult);
        var watchdogArtifact = CheckArtifact(
            Path.Join(watchdogFailDirectory, "coverage-watchdog.json"),
            "coverage run watchdog terminal incident");
        artifacts.Add(watchdogArtifact);
        if (!watchdogFailResult.Succeeded || !watchdogArtifact.Exists)
        {
            return BuildReport(context, commands, artifacts);
        }

        var coverageShardsDirectory = Path.Join(fixtureDirectory, "TestResults", "coverage-shards");
        CopyCoverageShard(coverageMergedDirectory, coverageShardsDirectory);
        var coverageFanInDirectory = Path.Join(fixtureDirectory, "TestResults", "coverage-fan-in");
        if (!await RunRequiredAsync(ToolCommand(
            context,
            ["coverage", "merge", "--source", coverageShardsDirectory, "--output", coverageFanInDirectory],
            "appsurface coverage merge",
            "merging packaged coverage shards")))
        {
            return BuildReport(context, commands, artifacts);
        }

        artifacts.AddRange(CheckCoverageMergeArtifacts(coverageFanInDirectory));
        if (artifacts.Any(artifact => !artifact.Exists))
        {
            return BuildReport(context, commands, artifacts);
        }

        var mergedCoveragePath = Path.Join(coverageFanInDirectory, "coverage.cobertura.xml");
        var passingGateDirectory = Path.Join(fixtureDirectory, "TestResults", "coverage-gate-pass");
        if (!await RunRequiredAsync(ToolCommand(
            context,
            ["coverage", "gate", "--coverage", mergedCoveragePath, "--output", passingGateDirectory, "--min-line", "1", "--min-branch", "0", "--no-github-summary"],
            "appsurface coverage gate",
            "running passing packaged coverage gate")))
        {
            return BuildReport(context, commands, artifacts);
        }

        artifacts.AddRange(CheckCoverageGateArtifacts(passingGateDirectory, "passing gate"));
        if (artifacts.Any(artifact => !artifact.Exists))
        {
            return BuildReport(context, commands, artifacts);
        }

        var failingGateDirectory = Path.Join(fixtureDirectory, "TestResults", "coverage-gate-fail");
        var failingGateResult = await RunCommandAsync(
            ToolCommand(
                context,
                ["coverage", "gate", "--coverage", mergedCoveragePath, "--output", failingGateDirectory, "--min-line", "100", "--min-branch", "100", "--no-github-summary"],
                "appsurface coverage gate",
                "running intentionally failing packaged coverage gate"),
            logsDirectory,
            commands.Count + 1,
            ExpectedCommandExitCode.NonZero,
            cancellationToken);
        commands.Add(failingGateResult);
        artifacts.AddRange(CheckCoverageGateArtifacts(failingGateDirectory, "failing gate"));

        var failingGateArtifactsMissing = artifacts
            .Where(artifact => artifact.Description.StartsWith("failing gate ", StringComparison.Ordinal)
                && !artifact.Exists)
            .ToArray();
        if (failingGateArtifactsMissing.Length > 0)
        {
            commands[^1] = failingGateResult with
            {
                Succeeded = false,
                FailureReason = "The intentionally failing coverage gate exited as expected but did not write both gate reports."
            };
        }

        return BuildReport(context, commands, artifacts);
    }

    /// <summary>
    /// Selects the validated <c>ForgeTrust.AppSurface.Cli</c> tool package that the consumer proof installs.
    /// </summary>
    /// <param name="report">Package artifact validation report produced from the just-packed local artifacts.</param>
    /// <param name="packageVersion">Exact package version that must be represented by the selected <c>.nupkg</c>.</param>
    /// <returns>Selected CLI package metadata plus a SHA-512 hash for diagnostics.</returns>
    /// <exception cref="PackageIndexException">
    /// Thrown when the validated report is missing the CLI package, contains more than one CLI row, marks it as a
    /// non-tool package, uses the wrong command name, points at a missing artifact, or points at a different version.
    /// </exception>
    /// <remarks>
    /// The proof intentionally selects from the validation report instead of globbing the artifact directory so package
    /// publication cannot silently test an unvalidated file.
    /// </remarks>
    internal static CoverageCliConsumerProofSelectedArtifact SelectCliToolPackage(
        PackageArtifactValidationReport report,
        string packageVersion)
    {
        ArgumentNullException.ThrowIfNull(report);
        var matches = report.Entries
            .Where(entry => string.Equals(entry.PackageId, CliPackageId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
        {
            throw new PackageIndexException($"Coverage CLI consumer proof requires validated package '{CliPackageId}'.");
        }

        if (matches.Length > 1)
        {
            throw new PackageIndexException($"Coverage CLI consumer proof found multiple validated package rows for '{CliPackageId}'.");
        }

        var entry = matches[0];
        if (!entry.IsTool)
        {
            throw new PackageIndexException($"Coverage CLI consumer proof requires '{CliPackageId}' to be marked as a .NET tool package.");
        }

        if (!string.Equals(entry.ToolCommandName, CliCommandName, StringComparison.Ordinal))
        {
            throw new PackageIndexException($"Coverage CLI consumer proof requires '{CliPackageId}' tool command '{CliCommandName}', found '{entry.ToolCommandName}'.");
        }

        if (string.IsNullOrWhiteSpace(entry.ArtifactPath))
        {
            throw new PackageIndexException($"Coverage CLI consumer proof requires '{CliPackageId}' to include a validated artifact path.");
        }

        if (!File.Exists(entry.ArtifactPath))
        {
            throw new PackageIndexException($"Coverage CLI consumer proof selected artifact '{entry.ArtifactPath}' does not exist.");
        }

        if (!string.Equals(Path.GetFileName(entry.ArtifactPath), $"{CliPackageId}.{packageVersion}.nupkg", StringComparison.OrdinalIgnoreCase))
        {
            throw new PackageIndexException($"Coverage CLI consumer proof selected artifact '{entry.ArtifactPath}' does not match package version '{packageVersion}'.");
        }

        return new CoverageCliConsumerProofSelectedArtifact(
            entry.PackageId,
            entry.ProjectPath,
            entry.ArtifactPath,
            entry.ToolCommandName,
            ComputeSha512(entry.ArtifactPath));
    }

    /// <summary>
    /// Renders the NuGet config used for local tool installation.
    /// </summary>
    /// <param name="localSource">Directory containing locally packed AppSurface and RazorWire package artifacts.</param>
    /// <param name="nugetOrgSource">NuGet source used for third-party dependency resolution.</param>
    /// <returns>NuGet configuration XML with package-source mapping.</returns>
    /// <remarks>
    /// AppSurface and RazorWire package ids are mapped to the local artifact source with more-specific package patterns;
    /// the <c>*</c> mapping on the public source remains available for third-party dependencies such as xUnit and
    /// Coverlet. Keep this config separate from the fixture config so the consumer fixture itself does not restore
    /// first-party packages from local artifacts accidentally.
    /// </remarks>
    internal static string RenderMappedNuGetConfig(string localSource, string nugetOrgSource)
    {
        var escapedLocalSource = SecurityElement.Escape(Path.GetFullPath(localSource)) ?? localSource;
        var escapedNuGetOrgSource = SecurityElement.Escape(nugetOrgSource) ?? nugetOrgSource;

        return $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="local-appsurface" value="{{escapedLocalSource}}" />
                <add key="nuget-org" value="{{escapedNuGetOrgSource}}" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="local-appsurface">
                  <package pattern="ForgeTrust.AppSurface" />
                  <package pattern="ForgeTrust.AppSurface.*" />
                  <package pattern="ForgeTrust.RazorWire" />
                  <package pattern="ForgeTrust.RazorWire.*" />
                </packageSource>
                <packageSource key="nuget-org">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """;
    }

    /// <summary>
    /// Renders the NuGet config used by the generated consumer fixture for test-only dependencies.
    /// </summary>
    /// <param name="nugetOrgSource">NuGet source used by <c>dotnet new xunit</c> and <c>dotnet add package coverlet.msbuild</c>.</param>
    /// <returns>NuGet configuration XML containing only the supplied third-party source.</returns>
    /// <remarks>
    /// This config deliberately excludes the local package artifact directory so the fixture exercises the packed CLI
    /// only through the local tool manifest installation path.
    /// </remarks>
    internal static string RenderNuGetOrgOnlyConfig(string nugetOrgSource)
    {
        var escapedNuGetOrgSource = SecurityElement.Escape(nugetOrgSource) ?? nugetOrgSource;

        return $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget-org" value="{{escapedNuGetOrgSource}}" />
              </packageSources>
            </configuration>
            """;
    }

    /// <summary>
    /// Deletes and recreates the isolated coverage proof workspace after rejecting unsafe deletion targets.
    /// </summary>
    /// <param name="workDirectory">Workspace that may be recursively deleted and recreated.</param>
    /// <param name="repositoryRoot">Repository root that must not be deleted or contained by the work directory.</param>
    /// <param name="artifactsDirectory">Package artifact directory that must not be deleted or contained by the work directory.</param>
    /// <exception cref="PackageIndexException">
    /// Thrown when <paramref name="workDirectory" /> is a filesystem root, the repository root, the artifact directory,
    /// the user's home directory, or a parent of the repository or artifact directory.
    /// </exception>
    /// <remarks>
    /// All compared paths are normalized and trailing directory separators are trimmed before comparison. This prevents
    /// bypasses such as passing the repository root with a trailing slash before the recursive delete runs.
    /// </remarks>
    internal static void PrepareWorkDirectory(string workDirectory, string repositoryRoot, string artifactsDirectory)
    {
        var normalizedWorkDirectory = NormalizeDirectoryForSafetyComparison(workDirectory);
        var normalizedRepositoryRoot = NormalizeDirectoryForSafetyComparison(repositoryRoot);
        var normalizedArtifactsDirectory = NormalizeDirectoryForSafetyComparison(artifactsDirectory);
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var invalidTargets = new List<string>
        {
            Path.GetPathRoot(normalizedWorkDirectory) ?? normalizedWorkDirectory,
            normalizedRepositoryRoot,
            normalizedArtifactsDirectory
        };
        if (!string.IsNullOrWhiteSpace(homeDirectory))
        {
            invalidTargets.Add(NormalizeDirectoryForSafetyComparison(homeDirectory));
        }

        if (invalidTargets.Any(target => string.Equals(normalizedWorkDirectory, target, PackageIndexGenerator.RepositoryPathComparison)))
        {
            throw new PackageIndexException($"Coverage CLI consumer proof work directory '{normalizedWorkDirectory}' is not a safe deletion target.");
        }

        if (IsParentOrSame(normalizedWorkDirectory, normalizedRepositoryRoot)
            || IsParentOrSame(normalizedWorkDirectory, normalizedArtifactsDirectory))
        {
            throw new PackageIndexException($"Coverage CLI consumer proof work directory '{normalizedWorkDirectory}' must not contain the repository root or package artifact directory.");
        }

        if (Directory.Exists(normalizedWorkDirectory))
        {
            Directory.Delete(normalizedWorkDirectory, recursive: true);
        }

        Directory.CreateDirectory(normalizedWorkDirectory);
    }

    private static string NormalizeDirectoryForSafetyComparison(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        while (fullPath.Length > root.Length
            && (fullPath.EndsWith(Path.DirectorySeparatorChar)
                || fullPath.EndsWith(Path.AltDirectorySeparatorChar)))
        {
            fullPath = fullPath[..^1];
        }

        return fullPath;
    }

    private static async Task<CoverageCliConsumerProofSelectedArtifact> SelectCliToolPackageAsync(
        PackageArtifactValidationReport report,
        string packageVersion,
        CancellationToken cancellationToken)
    {
        var selectedArtifact = SelectCliToolPackage(report, packageVersion);
        await Task.CompletedTask.WaitAsync(cancellationToken);
        return selectedArtifact;
    }

    private static void ValidateRequest(CoverageCliConsumerProofRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Directory.Exists(request.RepositoryRoot))
        {
            throw new PackageIndexException($"Repository root '{request.RepositoryRoot}' does not exist.");
        }

        if (!Directory.Exists(request.ArtifactsDirectory))
        {
            throw new PackageIndexException($"Package artifact directory '{request.ArtifactsDirectory}' does not exist.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.PackageVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Source);
    }

    private async Task<CoverageCliConsumerProofCommandResult> RunCommandAsync(
        ExternalCommandRequest request,
        string logsDirectory,
        int index,
        ExpectedCommandExitCode expectedExitCode,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await _commandRunner.RunAsync(request, cancellationToken);
        stopwatch.Stop();

        var logPrefix = $"{index:000}-{SanitizeFileName(request.OperationName)}";
        var stdoutPath = Path.Join(logsDirectory, $"{logPrefix}.stdout.log");
        var stderrPath = Path.Join(logsDirectory, $"{logPrefix}.stderr.log");
        await File.WriteAllTextAsync(stdoutPath, result.StandardOutput, cancellationToken);
        await File.WriteAllTextAsync(stderrPath, result.StandardError, cancellationToken);

        var succeeded = expectedExitCode switch
        {
            ExpectedCommandExitCode.Zero => result.ExitCode == 0,
            ExpectedCommandExitCode.NonZero => result.ExitCode != 0,
            ExpectedCommandExitCode.Watchdog => result.ExitCode == 124,
            _ => false,
        };
        var failureReason = succeeded
            ? string.Empty
            : expectedExitCode switch
            {
                ExpectedCommandExitCode.Zero => $"Expected exit code 0, got {result.ExitCode}.",
                ExpectedCommandExitCode.NonZero => $"Expected a non-zero exit code, got {result.ExitCode}.",
                ExpectedCommandExitCode.Watchdog => $"Expected watchdog exit code 124, got {result.ExitCode}.",
                _ => $"Unexpected expected-exit classification '{expectedExitCode}'.",
            };

        return new CoverageCliConsumerProofCommandResult(
            request.OperationName,
            request.FileName,
            request.Arguments,
            request.WorkingDirectory,
            result.ExitCode,
            expectedExitCode != ExpectedCommandExitCode.Zero,
            succeeded,
            failureReason,
            stopwatch.Elapsed,
            stdoutPath,
            stderrPath,
            result.StandardOutput,
            result.StandardError);
    }

    private static ExternalCommandRequest DotNetCommand(
        CoverageCliConsumerProofContext context,
        IReadOnlyList<string> arguments,
        string operationName,
        string timeoutDescription)
    {
        return new ExternalCommandRequest(
            "dotnet",
            arguments,
            context.FixtureDirectory,
            operationName,
            timeoutDescription,
            DotNetCommandTimeoutMilliseconds,
            CreateProofEnvironment(context.DotNetHomePath, context.SharedPackagesPath));
    }

    private static ExternalCommandRequest ToolCommand(
        CoverageCliConsumerProofContext context,
        IReadOnlyList<string> arguments,
        string operationName,
        string timeoutDescription)
    {
        return new ExternalCommandRequest(
            "dotnet",
            ["tool", "run", CliCommandName, "--", .. arguments],
            context.FixtureDirectory,
            operationName,
            timeoutDescription,
            string.Equals(operationName, "appsurface coverage run", StringComparison.Ordinal)
                ? CoverageRunTimeoutMilliseconds
                : DotNetCommandTimeoutMilliseconds,
            CreateProofEnvironment(context.DotNetHomePath, context.SharedPackagesPath));
    }

    private static async Task WriteSmokeFixtureAsync(string fixtureDirectory, CancellationToken cancellationToken)
    {
        var libraryDirectory = Path.Join(fixtureDirectory, "Smoke");
        var testDirectory = Path.Join(fixtureDirectory, "Smoke.Tests");
        var excludedTestDirectory = Path.Join(fixtureDirectory, "Smoke.Browser.Tests");
        Directory.CreateDirectory(libraryDirectory);
        Directory.CreateDirectory(testDirectory);
        Directory.CreateDirectory(excludedTestDirectory);

        await File.WriteAllTextAsync(
            Path.Join(libraryDirectory, "Calculator.cs"),
            """
            namespace Smoke;

            public static class Calculator
            {
                public static int Add(int left, int right) => left + right;

                public static string Sign(int value) => value >= 0 ? "non-negative" : "negative";

                public static int Untested(int value) => value * 2;
            }
            """,
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Join(excludedTestDirectory, "UnitTest1.cs"),
            """
            using Xunit;

            namespace Smoke.Browser.Tests;

            public sealed class UnitTest1
            {
                [Fact]
                public void ExcludedSentinel_MustNeverRun()
                {
                    Assert.Fail("The excluded sentinel test project was executed.");
                }
            }
            """,
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Join(testDirectory, "UnitTest1.cs"),
            """
            using Xunit;

            using Smoke;

            namespace Smoke.Tests;

            public sealed class UnitTest1
            {
                [Fact]
                public void Add_ReturnsSum()
                {
                    Assert.Equal(3, Calculator.Add(1, 2));
                }

                [Theory]
                [InlineData(1, "non-negative")]
                [InlineData(-1, "negative")]
                public void Sign_ClassifiesValue(int value, string expected)
                {
                    Assert.Equal(expected, Calculator.Sign(value));
                }

                [Fact]
                public async Task QuietOperation_AllowsWatchdogProof()
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));
                }
            }
            """,
            cancellationToken);
    }

    private static string ResolveSmokeSolutionPath(string fixtureDirectory)
    {
        var slnxPath = Path.Join(fixtureDirectory, "Smoke.slnx");
        if (File.Exists(slnxPath))
        {
            return slnxPath;
        }

        var slnPath = Path.Join(fixtureDirectory, "Smoke.sln");
        return File.Exists(slnPath) ? slnPath : slnxPath;
    }

    private static IReadOnlyList<CoverageCliConsumerProofArtifactCheck> CheckCoverageRunArtifacts(string coverageMergedDirectory)
    {
        return
        [
            CheckArtifact(Path.Join(coverageMergedDirectory, "coverage.cobertura.xml"), "coverage run merged Cobertura"),
            CheckArtifact(Path.Join(coverageMergedDirectory, "summary.txt"), "coverage run summary"),
            CheckArtifact(Path.Join(coverageMergedDirectory, "timings.json"), "coverage run timings"),
            CheckArtifact(Path.Join(coverageMergedDirectory, ".appsurface-coverage-output"), "coverage run ownership marker"),
            CheckGlob(Path.Join(coverageMergedDirectory, "projects"), "*", "dotnet-test.log", "coverage run project log"),
            CheckGlob(Path.Join(coverageMergedDirectory, "projects"), "*", "coverage.cobertura.xml", "coverage run project Cobertura")
        ];
    }

    private static IReadOnlyList<CoverageCliConsumerProofArtifactCheck> CheckCoverageMergeArtifacts(string coverageFanInDirectory)
    {
        return
        [
            CheckArtifact(Path.Join(coverageFanInDirectory, "coverage.cobertura.xml"), "coverage merge Cobertura"),
            CheckArtifact(Path.Join(coverageFanInDirectory, "summary.txt"), "coverage merge summary"),
            CheckArtifact(Path.Join(coverageFanInDirectory, "timings.json"), "coverage merge timings"),
            CheckGlob(Path.Join(coverageFanInDirectory, "reportgenerator-input"), "*", "coverage.cobertura.xml", "coverage merge staged input")
        ];
    }

    private static CoverageCliConsumerProofArtifactCheck CheckExcludedProjectArtifacts(
        string coverageMergedDirectory,
        string projectName)
    {
        var projectsDirectory = Path.Join(coverageMergedDirectory, "projects");
        var excludedArtifact = Directory.Exists(projectsDirectory)
            ? Directory.EnumerateDirectories(projectsDirectory, $"{projectName}-*", SearchOption.TopDirectoryOnly).FirstOrDefault()
            : null;
        return new CoverageCliConsumerProofArtifactCheck(
            $"excluded project '{projectName}' produced no coverage artifacts",
            excludedArtifact ?? Path.Join(projectsDirectory, $"{projectName}-*"),
            excludedArtifact is null);
    }

    private static IReadOnlyList<CoverageCliConsumerProofArtifactCheck> CheckCoverageGateArtifacts(
        string gateDirectory,
        string label)
    {
        return
        [
            CheckArtifact(Path.Join(gateDirectory, "coverage-gate.json"), $"{label} JSON report"),
            CheckArtifact(Path.Join(gateDirectory, "coverage-gate.md"), $"{label} Markdown report")
        ];
    }

    private static CoverageCliConsumerProofArtifactCheck CheckArtifact(string path, string description)
    {
        return new CoverageCliConsumerProofArtifactCheck(description, path, File.Exists(path));
    }

    private static CoverageCliConsumerProofArtifactCheck CheckGlob(
        string directory,
        string childPattern,
        string fileName,
        string description)
    {
        var matchedPath = Directory.Exists(directory)
            ? Directory.EnumerateDirectories(directory, childPattern, SearchOption.TopDirectoryOnly)
                .Select(path => Path.Join(path, fileName))
                .FirstOrDefault(File.Exists)
            : null;
        return new CoverageCliConsumerProofArtifactCheck(description, matchedPath ?? Path.Join(directory, childPattern, fileName), matchedPath is not null);
    }

    private static void CopyCoverageShard(string coverageMergedDirectory, string coverageShardsDirectory)
    {
        var projectCoveragePath = Directory
            .EnumerateFiles(Path.Join(coverageMergedDirectory, "projects"), "coverage.cobertura.xml", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .First();
        var shardDirectory = Path.Join(coverageShardsDirectory, "Smoke.Tests");
        Directory.CreateDirectory(shardDirectory);
        File.Copy(projectCoveragePath, Path.Join(shardDirectory, "coverage.cobertura.xml"), overwrite: true);
    }

    private static CoverageCliConsumerProofReport BuildReport(
        CoverageCliConsumerProofContext context,
        IReadOnlyList<CoverageCliConsumerProofCommandResult> commands,
        IReadOnlyList<CoverageCliConsumerProofArtifactCheck> artifacts)
    {
        var failedCommand = commands.FirstOrDefault(command => !command.Succeeded);
        var firstFailure = failedCommand?.FailureReason;
        if (string.IsNullOrWhiteSpace(firstFailure))
        {
            firstFailure = artifacts.FirstOrDefault(artifact => !artifact.Exists)?.Description;
        }

        return new CoverageCliConsumerProofReport(
            context.Request.PackageVersion,
            context.Request.WorkDirectory,
            context.Request.Source,
            context.SelectedArtifact,
            context.ToolNuGetConfigPath,
            context.FixtureNuGetConfigPath,
            context.LogsDirectory,
            commands,
            artifacts,
            firstFailure ?? string.Empty,
            CreateReproduceCommand(context.Request));
    }

    private static string CreateReproduceCommand(CoverageCliConsumerProofRequest request)
    {
        return string.Join(
            " ",
            [
                "dotnet",
                "run",
                "--project",
                "tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj",
                "--",
                "verify-packages",
                "--package-version",
                QuoteShellArgument(request.PackageVersion),
                "--artifacts-output",
                QuoteShellArgument(request.ArtifactsDirectory),
                "--coverage-proof-work-dir",
                QuoteShellArgument(request.WorkDirectory),
                "--source",
                QuoteShellArgument(request.Source)
            ]);
    }

    private static IReadOnlyDictionary<string, string?> CreateProofEnvironment(
        string dotnetHomePath,
        string sharedPackagesPath)
    {
        return new Dictionary<string, string?>
        {
            ["DOTNET_CLI_HOME"] = dotnetHomePath,
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["NUGET_PACKAGES"] = sharedPackagesPath
        };
    }

    private static string ComputeSha512(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA512.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsParentOrSame(string possibleParent, string child)
    {
        var parent = possibleParent.EndsWith(Path.DirectorySeparatorChar)
            ? possibleParent
            : possibleParent + Path.DirectorySeparatorChar;
        return string.Equals(possibleParent, child, PackageIndexGenerator.RepositoryPathComparison)
            || child.StartsWith(parent, PackageIndexGenerator.RepositoryPathComparison);
    }

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(character => invalidCharacters.Contains(character) || char.IsWhiteSpace(character) ? '-' : character).ToArray());
    }

    private static string QuoteShellArgument(string value)
    {
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    private enum ExpectedCommandExitCode
    {
        Zero,
        NonZero,
        Watchdog,
    }
}

/// <summary>
/// Request for the packaged coverage CLI consumer proof.
/// </summary>
/// <param name="RepositoryRoot">Repository root used for safety checks and reproduction instructions.</param>
/// <param name="ArtifactsDirectory">Directory containing locally packed package artifacts.</param>
/// <param name="PackageVersion">Exact stable or prerelease package version under proof.</param>
/// <param name="WorkDirectory">Isolated proof workspace that can be deleted and recreated.</param>
/// <param name="Source">NuGet source for third-party dependencies.</param>
internal sealed record CoverageCliConsumerProofRequest(
    string RepositoryRoot,
    string ArtifactsDirectory,
    string PackageVersion,
    string WorkDirectory,
    string Source);

/// <summary>
/// Selected CLI artifact metadata captured before running the consumer proof.
/// </summary>
/// <param name="PackageId">Selected package id.</param>
/// <param name="ProjectPath">Project that produced the artifact.</param>
/// <param name="ArtifactPath">Validated local <c>.nupkg</c> path.</param>
/// <param name="ToolCommandName">Command shim expected after tool installation.</param>
/// <param name="Sha512">SHA-512 hash of the selected artifact.</param>
internal sealed record CoverageCliConsumerProofSelectedArtifact(
    string PackageId,
    string ProjectPath,
    string ArtifactPath,
    string ToolCommandName,
    string Sha512);

/// <summary>
/// Structured packaged coverage CLI proof report.
/// </summary>
/// <param name="PackageVersion">Exact package version under proof.</param>
/// <param name="WorkDirectory">Proof workspace path.</param>
/// <param name="Source">NuGet source used for third-party dependencies.</param>
/// <param name="SelectedArtifact">Selected CLI artifact, if package selection succeeded.</param>
/// <param name="ToolNuGetConfigPath">NuGet config used for tool installation.</param>
/// <param name="FixtureNuGetConfigPath">NuGet config used for fixture dependencies.</param>
/// <param name="LogsDirectory">Directory containing per-command stdout/stderr logs.</param>
/// <param name="Commands">Executed command ledger.</param>
/// <param name="Artifacts">Produced and missing artifact checks.</param>
/// <param name="FirstFailure">First failure summary, or empty when the proof passed.</param>
/// <param name="ReproduceCommand">Command that reruns the package verifier with the same proof workspace.</param>
internal sealed record CoverageCliConsumerProofReport(
    string PackageVersion,
    string WorkDirectory,
    string Source,
    CoverageCliConsumerProofSelectedArtifact? SelectedArtifact,
    string ToolNuGetConfigPath,
    string FixtureNuGetConfigPath,
    string LogsDirectory,
    IReadOnlyList<CoverageCliConsumerProofCommandResult> Commands,
    IReadOnlyList<CoverageCliConsumerProofArtifactCheck> Artifacts,
    string FirstFailure,
    string ReproduceCommand)
{
    /// <summary>
    /// Gets whether every command and artifact check matched the expected consumer contract.
    /// </summary>
    internal bool Succeeded => string.IsNullOrWhiteSpace(FirstFailure)
        && Commands.All(command => command.Succeeded)
        && Artifacts.All(artifact => artifact.Exists);

    internal static CoverageCliConsumerProofReport Failed(
        string packageVersion,
        string workDirectory,
        string source,
        string firstFailure,
        CoverageCliConsumerProofSelectedArtifact? selectedArtifact = null)
    {
        return new CoverageCliConsumerProofReport(
            packageVersion,
            workDirectory,
            source,
            selectedArtifact,
            string.Empty,
            string.Empty,
            string.Empty,
            [],
            [],
            firstFailure,
            string.Empty);
    }
}

/// <summary>
/// One external command in the packaged coverage CLI proof ledger.
/// </summary>
/// <param name="OperationName">Human-readable operation name.</param>
/// <param name="FileName">Executable path or command name.</param>
/// <param name="Arguments">Command arguments.</param>
/// <param name="WorkingDirectory">Command working directory.</param>
/// <param name="ExitCode">Process exit code.</param>
/// <param name="ExpectedNonZeroExitCode">Whether a non-zero exit code was the expected outcome.</param>
/// <param name="Succeeded">Whether the command matched the expected exit-code contract.</param>
/// <param name="FailureReason">Failure details when the command did not match the expected contract.</param>
/// <param name="Duration">Measured command duration.</param>
/// <param name="StandardOutputPath">Path where stdout was written.</param>
/// <param name="StandardErrorPath">Path where stderr was written.</param>
/// <param name="StandardOutput">Captured stdout excerpt source.</param>
/// <param name="StandardError">Captured stderr excerpt source.</param>
internal sealed record CoverageCliConsumerProofCommandResult(
    string OperationName,
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    int ExitCode,
    bool ExpectedNonZeroExitCode,
    bool Succeeded,
    string FailureReason,
    TimeSpan Duration,
    string StandardOutputPath,
    string StandardErrorPath,
    string StandardOutput,
    string StandardError);

/// <summary>
/// One artifact expected from the packaged coverage CLI consumer proof.
/// </summary>
/// <param name="Description">Artifact role.</param>
/// <param name="Path">Expected or produced artifact path.</param>
/// <param name="Exists">Whether the artifact exists.</param>
internal sealed record CoverageCliConsumerProofArtifactCheck(string Description, string Path, bool Exists);

/// <summary>
/// Runtime paths shared across the packaged coverage CLI consumer proof.
/// </summary>
/// <param name="Request">Original proof request and caller-supplied paths.</param>
/// <param name="SelectedArtifact">Validated CLI package artifact selected for local tool installation.</param>
/// <param name="FixtureDirectory">Clean consumer repository where solution, test projects, and local tool manifest are created.</param>
/// <param name="LogsDirectory">Directory for per-command stdout and stderr logs.</param>
/// <param name="ToolNuGetConfigPath">NuGet configuration used only for installing the AppSurface local tool package.</param>
/// <param name="FixtureNuGetConfigPath">NuGet configuration used by the consumer fixture for third-party test dependencies.</param>
/// <param name="SharedPackagesPath">Isolated global packages cache for all proof commands.</param>
/// <param name="DotNetHomePath">Isolated .NET CLI home used to avoid host-machine state.</param>
internal sealed record CoverageCliConsumerProofContext(
    CoverageCliConsumerProofRequest Request,
    CoverageCliConsumerProofSelectedArtifact SelectedArtifact,
    string FixtureDirectory,
    string LogsDirectory,
    string ToolNuGetConfigPath,
    string FixtureNuGetConfigPath,
    string SharedPackagesPath,
    string DotNetHomePath);

/// <summary>
/// Renders packaged coverage CLI consumer proof reports for package validation artifacts.
/// </summary>
internal static class CoverageCliConsumerProofReportRenderer
{
    /// <summary>
    /// Renders a standalone markdown report.
    /// </summary>
    /// <param name="report">Proof report.</param>
    /// <returns>Markdown report.</returns>
    internal static string RenderMarkdown(CoverageCliConsumerProofReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Coverage CLI consumer proof");
        builder.AppendLine();
        RenderSection(builder, report);
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>
    /// Appends the proof section to a larger package validation report.
    /// </summary>
    /// <param name="builder">Destination markdown builder.</param>
    /// <param name="report">Proof report.</param>
    internal static void RenderSection(StringBuilder builder, CoverageCliConsumerProofReport report)
    {
        builder.AppendLine($"Status: `{(report.Succeeded ? "passed" : "failed")}`");
        builder.AppendLine($"Version: `{report.PackageVersion}`");
        builder.AppendLine($"Work directory: `{report.WorkDirectory}`");
        builder.AppendLine($"NuGet source: `{report.Source}`");
        if (!string.IsNullOrWhiteSpace(report.FirstFailure))
        {
            builder.AppendLine($"First failure: `{EscapeCode(report.FirstFailure)}`");
        }

        if (report.SelectedArtifact is not null)
        {
            builder.AppendLine($"Selected artifact: `{report.SelectedArtifact.ArtifactPath}`");
            builder.AppendLine($"Selected artifact SHA-512: `{report.SelectedArtifact.Sha512}`");
        }

        if (!string.IsNullOrWhiteSpace(report.ToolNuGetConfigPath))
        {
            builder.AppendLine($"Tool NuGet config: `{report.ToolNuGetConfigPath}`");
        }

        if (!string.IsNullOrWhiteSpace(report.FixtureNuGetConfigPath))
        {
            builder.AppendLine($"Fixture NuGet config: `{report.FixtureNuGetConfigPath}`");
        }

        if (!string.IsNullOrWhiteSpace(report.LogsDirectory))
        {
            builder.AppendLine($"Command logs: `{report.LogsDirectory}`");
        }

        if (!string.IsNullOrWhiteSpace(report.ReproduceCommand))
        {
            builder.AppendLine();
            builder.AppendLine("Reproduce:");
            builder.AppendLine();
            builder.AppendLine("```bash");
            builder.AppendLine(report.ReproduceCommand);
            builder.AppendLine("```");
        }

        builder.AppendLine();
        builder.AppendLine("## Command ledger");
        if (report.Commands.Count == 0)
        {
            builder.AppendLine();
            builder.AppendLine("No commands ran.");
        }
        else
        {
            builder.AppendLine();
            builder.AppendLine("| Step | Status | Exit code | Expected | Duration | Command | Cwd | Stdout | Stderr |");
            builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");
            for (var index = 0; index < report.Commands.Count; index++)
            {
                var command = report.Commands[index];
                var expected = command.ExpectedNonZeroExitCode ? "non-zero" : "0";
                builder.AppendLine($"| `{index + 1}` | `{(command.Succeeded ? "passed" : "failed")}` | `{command.ExitCode}` | `{expected}` | `{command.Duration.TotalMilliseconds:0} ms` | `{EscapeCode(FormatCommand(command))}` | `{command.WorkingDirectory}` | `{command.StandardOutputPath}` | `{command.StandardErrorPath}` |");
            }
        }

        var missingArtifacts = report.Artifacts.Where(artifact => !artifact.Exists).ToArray();
        builder.AppendLine();
        builder.AppendLine("## Artifacts");
        if (report.Artifacts.Count == 0)
        {
            builder.AppendLine();
            builder.AppendLine("No artifact checks ran.");
        }
        else
        {
            builder.AppendLine();
            builder.AppendLine("| Status | Artifact | Path |");
            builder.AppendLine("| --- | --- | --- |");
            foreach (var artifact in report.Artifacts)
            {
                builder.AppendLine($"| `{(artifact.Exists ? "produced" : "missing")}` | `{EscapeCode(artifact.Description)}` | `{artifact.Path}` |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Output excerpts");
        foreach (var command in report.Commands.Where(command => !string.IsNullOrWhiteSpace(command.StandardOutput) || !string.IsNullOrWhiteSpace(command.StandardError)))
        {
            builder.AppendLine();
            builder.AppendLine($"### {command.OperationName}");
            AppendExcerpt(builder, "stdout", command.StandardOutput);
            AppendExcerpt(builder, "stderr", command.StandardError);
        }

        if (missingArtifacts.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Missing artifacts");
            foreach (var artifact in missingArtifacts)
            {
                builder.AppendLine($"- `{artifact.Description}` at `{artifact.Path}`");
            }
        }
    }

    private static void AppendExcerpt(StringBuilder builder, string streamName, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine($"{streamName}:");
        builder.AppendLine();
        builder.AppendLine("```text");
        builder.AppendLine(TrimExcerpt(value));
        builder.AppendLine("```");
    }

    private static string FormatCommand(CoverageCliConsumerProofCommandResult command)
    {
        return string.Join(" ", new[] { command.FileName }.Concat(command.Arguments.Select(QuoteArgument)));
    }

    private static string QuoteArgument(string argument)
    {
        return argument.Any(char.IsWhiteSpace) ? $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : argument;
    }

    private static string TrimExcerpt(string value)
    {
        var normalized = value.Trim();
        return normalized.Length <= 2_000 ? normalized : normalized[..2_000] + Environment.NewLine + "[truncated]";
    }

    private static string EscapeCode(string value)
    {
        return value.Replace("`", "\\`", StringComparison.Ordinal);
    }
}
