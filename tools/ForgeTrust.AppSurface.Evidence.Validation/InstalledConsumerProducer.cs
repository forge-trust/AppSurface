using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Runs the supplied CLI package against an isolated, source-integrity-checked consumer.</summary>
internal sealed class InstalledConsumerProducer : IEvidenceProducer, IAsyncDisposable
{
    private const string ProducerId = "installed-consumer";
    private const string PackageId = "ForgeTrust.AppSurface.Cli";
    private const string CliAssembly = "ForgeTrust.AppSurface.Cli.dll";
    private static readonly string[] AssertionIds =
    [
        "consumer/package-identity@1",
        "consumer/executed-tests@1",
        "consumer/coverage-gate@1",
        "consumer/source-integrity@1",
    ];

    private readonly ValidationSettings _settings;
    private readonly EvidenceCommandRunner _runner;
    private EvidenceWorkspace? _workspace;
    private int _disposed;
    private string? _settingsHash;
    private List<CommandSummary> _commandSummaries = [];
    private string _stageDetail = "";
    private int? _lastExitCode;
    private string _lastOutput = "";
    private string _lastError = "";

    /// <summary>Creates an installed-consumer producer with immutable caller settings.</summary>
    public InstalledConsumerProducer(ValidationSettings settings, EvidenceCommandRunner? runner = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _runner = runner ?? new EvidenceCommandRunner();
    }

    /// <summary>Gets the stable producer identifier.</summary>
    public string Id => ProducerId;

    /// <summary>Runs package, test, report, gate, and source-integrity assertions.</summary>
    public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(context.Producer.Id, ProducerId, StringComparison.Ordinal))
        {
            return Failure("The installed-consumer declaration did not match the producer.");
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var stage = "settings";
        try
        {
            ValidateSettings();
            _settingsHash = HashBytes(JsonSerializer.SerializeToUtf8Bytes(_settings));
            _commandSummaries = [];
            _stageDetail = "";
            _lastExitCode = null;
            _workspace = EvidenceWorkspace.Create(
                _settings.ScratchParent,
                new EvidenceWorkspaceOptions(_settings.MaximumScratchBytes, _settings.MinimumFreeBytes));
            var budget = new Func<CancellationToken, ValueTask>(_ => CheckBudgetAsync());
            var consumer = _settings.Consumer ?? new ConsumerValidationOptions();
            stage = "source";
            var source = await PrepareSourceAsync(consumer, budget, cancellationToken).ConfigureAwait(false);
            stage = "package";
            var package = await InstallPackageAsync(source.Root, budget, cancellationToken).ConfigureAwait(false);
            stage = "coverage";
            var coverage = await RunCoverageAsync(source, package, consumer, budget, cancellationToken).ConfigureAwait(false);
            stage = "integrity";
            var integrity = await VerifySourceIntegrityAsync(source, cancellationToken).ConfigureAwait(false);
            if (!integrity)
            {
                throw new InvalidDataException();
            }

            var summary = new ValidationSummary(
                PackageVersion: _settings.PackageVersion,
                PackageSha256: package.PackageSha256,
                CliAssemblySha256: package.CliAssemblySha256,
                TestCaseCount: coverage.TestCaseCount,
                ProjectCount: coverage.ProjectCount,
                CoverageGatePassed: coverage.GatePassed,
                ManagedRetentionVerified: coverage.ManagedRetentionVerified,
                SourceIntegrityVerified: integrity,
                AssertionIds: AssertionIds,
                Commands: _commandSummaries);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(summary, new JsonSerializerOptions { WriteIndented = false });
            if (context.Artifacts is null)
            {
                return Failure("The installed-consumer producer has no artifact writer.", stopwatch.ElapsedMilliseconds);
            }

            await context.Artifacts.WriteAsync("result", "proof/result.json", bytes, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            return new EvidenceProducerResult(
                ProducerId,
                EvidenceProducerOutcome.Passed,
                AssertionIds,
                ElapsedMilliseconds: stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EvidenceCommandException exception)
        {
            return Failure($"The installed-consumer command journey did not complete/{exception.Code}.", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            var exitDetail = _lastExitCode is { } exitCode ? $"/exit-{exitCode}" : "";
            if (context.Artifacts is not null)
            {
                var root = _workspace?.RootPath ?? "";
                string Safe(string value) => root.Length > 0 ? value.Replace(root, "<workspace>", StringComparison.Ordinal) : value;
                await context.Artifacts.WriteAsync("result", "proof/result.json", JsonSerializer.SerializeToUtf8Bytes(new
                {
                    completed = false,
                    stage,
                    detail = _stageDetail,
                    exitCode = _lastExitCode,
                    commands = _commandSummaries,
                    stdout = _settings.Consumer?.SourceDirectory is null ? Safe(_lastOutput) : "withheld for external consumer",
                    stderr = _settings.Consumer?.SourceDirectory is null ? Safe(_lastError) : "withheld for external consumer",
                }), CancellationToken.None).ConfigureAwait(false);
            }
            return Failure($"The installed-consumer validation contract was not satisfied at {stage}{(_stageDetail.Length == 0 ? "" : "/" + _stageDetail)}{exitDetail}.", stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>Disposes the producer-owned workspace so host cleanup is part of the claim.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || _workspace is null)
        {
            return ValueTask.CompletedTask;
        }

        return _workspace.DisposeAsync();
    }

    private async Task<SourceSnapshot> PrepareSourceAsync(ConsumerValidationOptions options, Func<CancellationToken, ValueTask> budget, CancellationToken cancellationToken)
    {
        var sourceDirectory = options.SourceDirectory is null
            ? Path.Join(_settings.RepositoryRoot, "tools", "ForgeTrust.AppSurface.Evidence.Validation", "fixtures", "consumer")
            : Path.GetFullPath(Path.IsPathFullyQualified(options.SourceDirectory)
                ? options.SourceDirectory
                : Path.Join(_settings.RepositoryRoot, options.SourceDirectory));
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException();
        }

        var originalStatus = await GitStatusAsync(sourceDirectory, budget, cancellationToken).ConfigureAwait(false);
        if (options.SourceDirectory is not null && originalStatus.Length != 0)
        {
            throw new InvalidOperationException();
        }
        var originalCommit = await GitOutputAsync(sourceDirectory, ["rev-parse", "HEAD"], budget, cancellationToken, allowMissing: true).ConfigureAwait(false);
        var expectedCommit = options.ExpectedCommit ?? (options.SourceDirectory is null ? null : _settings.ExpectedSourceCommit);
        if (expectedCommit is not null && expectedCommit.Length > 0 && !string.Equals(originalCommit, expectedCommit, StringComparison.Ordinal))
        {
            throw new InvalidOperationException();
        }

        var beforeHash = await HashTrackedFilesAsync(sourceDirectory, budget, cancellationToken).ConfigureAwait(false);
        var destination = Path.Join(_workspace!.RootPath, "consumer");
        if (options.SourceDirectory is null)
        {
            CopyDirectory(sourceDirectory, destination);
        }
        else
        {
            await RunCheckedAsync("git", ["clone", "--no-local", "--no-checkout", sourceDirectory, destination], _workspace.RootPath, new Dictionary<string, string>(), budget, cancellationToken).ConfigureAwait(false);
            await RunCheckedAsync("git", ["checkout", "--detach", expectedCommit ?? originalCommit], destination, new Dictionary<string, string>(), budget, cancellationToken).ConfigureAwait(false);
        }
        var afterHash = await HashTrackedFilesAsync(destination, budget, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(beforeHash, afterHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException();
        }

        var solution = options.SolutionPath ?? "Consumer.slnx";
        var projects = options.TestProjects?.Count > 0
            ? options.TestProjects
            : (IReadOnlyList<string>)[];
        if (projects.Count > 0)
        {
            foreach (var project in projects)
            {
                var projectPath = ContainedSourcePath(destination, project);
                if (!File.Exists(projectPath) || !IsTestProject(projectPath))
                {
                    throw new InvalidOperationException();
                }
            }
        }
        var expectedProjectCount = projects.Count > 0
            ? projects.Count
            : CountTestProjects(destination, solution);
        IReadOnlyList<string> requiredFragments = options.ExpectedRequiredTestFragments
            ?? (options.SourceDirectory is null ? ["AlphaDecisionTests", "BetaDecisionTests"] : []);
        return new SourceSnapshot(destination, sourceDirectory, originalStatus, originalCommit, beforeHash, _settingsHash!, solution, projects, expectedProjectCount, requiredFragments);
    }

    private async Task<PackageSnapshot> InstallPackageAsync(string consumerRoot, Func<CancellationToken, ValueTask> budget, CancellationToken cancellationToken)
    {
        var packagePath = Path.GetFullPath(_settings.PackagePath);
        if (!File.Exists(packagePath)) throw new FileNotFoundException();
        var packageHash = await HashFileAsync(packagePath, cancellationToken).ConfigureAwait(false);
        using (var archive = ZipFile.OpenRead(packagePath))
        {
            ValidationProof.VerifyPackage(archive, _settings);
        }

        var toolPath = Path.Join(_workspace!.RootPath, "tool");
        var cliHome = Path.Join(_workspace.RootPath, "cli-home");
        Directory.CreateDirectory(toolPath);
        Directory.CreateDirectory(cliHome);
        // Explicitly selected for installation only; a conventional NuGet.Config
        // here would also redirect the consumer's ordinary dependency restore.
        var sourceConfig = Path.Join(_workspace.RootPath, "NuGet.tool.config");
        await File.WriteAllTextAsync(sourceConfig, $"<?xml version=\"1.0\" encoding=\"utf-8\"?><configuration><packageSources><clear /><add key=\"candidate\" value=\"{SecurityElementEscape(Path.GetDirectoryName(packagePath)!)}\" /></packageSources></configuration>", cancellationToken).ConfigureAwait(false);

        // Keep tool installation's package graph private to this workspace, then
        // let the consumer's ordinary restore read the existing shared cache. The
        // package identity proof is anchored to the private .store and the exact
        // local feed above; the fixture dependencies do not need to be recopied.
        var installEnvironment = BuildEnvironment(cliHome, Path.Join(_workspace.RootPath, "nuget"));
        var args = new List<string>
        {
            "tool", "install", PackageId, "--tool-path", toolPath,
            "--add-source", Path.GetDirectoryName(packagePath)!, "--configfile", sourceConfig,
            "--version", _settings.PackageVersion,
        };
        await RunCheckedAsync(_settings.DotnetPath, args, consumerRoot, installEnvironment, budget, cancellationToken).ConfigureAwait(false);

        // dotnet tool install places the materialized package under the tool path's
        // private .store. Keep the search rooted in this workspace so a matching
        // package from a user's global tool cache can never satisfy the identity
        // assertion.
        var storedPackages = Directory.EnumerateFiles(Path.Join(toolPath, ".store"), "*.nupkg", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetFileName(path), $"{PackageId.ToLowerInvariant()}.{_settings.PackageVersion.ToLowerInvariant()}.nupkg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(path), $"{PackageId}.{_settings.PackageVersion}.nupkg", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var hasMatchingStoredPackage = false;
        foreach (var storedPackage in storedPackages)
        {
            if (string.Equals(packageHash, await HashFileAsync(storedPackage, cancellationToken).ConfigureAwait(false), StringComparison.Ordinal))
            {
                hasMatchingStoredPackage = true;
                break;
            }
        }

        if (!hasMatchingStoredPackage)
        {
            throw new InvalidDataException();
        }

        var archiveAssemblyHash = GetArchiveAssemblyHash(packagePath);
        var installedAssembly = Directory.EnumerateFiles(_workspace.RootPath, CliAssembly, SearchOption.AllDirectories).FirstOrDefault();
        if (installedAssembly is null || !string.Equals(archiveAssemblyHash, await HashFileAsync(installedAssembly, cancellationToken).ConfigureAwait(false), StringComparison.Ordinal))
        {
            throw new InvalidDataException();
        }

        var executable = OperatingSystem.IsWindows() ? Path.Join(toolPath, "appsurface.exe") : Path.Join(toolPath, "appsurface");
        if (!File.Exists(executable)) throw new FileNotFoundException();
        var sharedNuGetCache = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(sharedNuGetCache))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidate = string.IsNullOrWhiteSpace(userProfile) ? "" : Path.Join(userProfile, ".nuget", "packages");
            sharedNuGetCache = Directory.Exists(candidate) ? candidate : Path.Join(_workspace.RootPath, "nuget");
        }
        var consumerEnvironment = BuildEnvironment(cliHome, sharedNuGetCache);
        return new PackageSnapshot(packageHash, archiveAssemblyHash, executable, consumerEnvironment);
    }

    private async Task<CoverageSnapshot> RunCoverageAsync(SourceSnapshot source, PackageSnapshot package, ConsumerValidationOptions options, Func<CancellationToken, ValueTask> budget, CancellationToken cancellationToken)
    {
        var output = Path.Join(_workspace!.RootPath, "coverage");
        var args = new List<string> { "coverage", "run", "--solution", source.SolutionPath, "--configuration", "Release", "--output", output, "--test-results", "junit", "--artifact-retention", "managed", "--no-progress-timeout", "10m" };
        foreach (var project in source.TestProjects) args.AddRange(["--test-project", project]);
        _stageDetail = "run";
        await RunCheckedAsync(package.Executable, args, source.Root, MergeEnvironment(package.Environment, options.Environment), budget, cancellationToken).ConfigureAwait(false);

        var gateArgs = new List<string> { "coverage", "gate", "--coverage", Path.Join(output, "coverage.cobertura.xml"), "--output", output, "--min-line", "95", "--min-branch", "85", "--tolerance", "0", "--no-github-summary" };
        if (options.DiffBase is not null) gateArgs.AddRange(["--diff-base", options.DiffBase, "--min-patch-line", "95", "--min-patch-branch", "85"]);
        _stageDetail = "gate";
        await RunCheckedAsync(package.Executable, gateArgs, source.Root, MergeEnvironment(package.Environment, options.Environment), budget, cancellationToken).ConfigureAwait(false);

        _stageDetail = "junit";
        var testCases = ReadJUnitReports(output, source.ExpectedProjectCount, cancellationToken);

        if (testCases.Count == 0 || testCases.Any(test => HasFailure(test) || !IsPermittedSkip(test, options.PermittedSkipPatterns))) throw new InvalidDataException();
        ValidateRequiredTestFragments(source.RequiredTestFragments, testCases);

        foreach (var required in new[] { "coverage.cobertura.xml", "timings.json", "coverage-gate.json", "coverage-gate.md" })
        {
            _stageDetail = "artifacts";
            if (!File.Exists(Path.Join(output, required))) throw new InvalidDataException();
        }

        _stageDetail = "manifests";
        var projectDirectories = Directory.Exists(Path.Join(output, "projects"))
            ? Directory.EnumerateDirectories(Path.Join(output, "projects")).ToArray()
            : [];
        if (projectDirectories.Length != source.ExpectedProjectCount || projectDirectories.Any(project =>
            !File.Exists(Path.Join(project, "coverage-project.json"))
            || !File.Exists(Path.Join(project, "dotnet-test.log"))
            || !File.Exists(Path.Join(project, "coverage.cobertura.xml"))))
        {
            throw new InvalidDataException();
        }

        var junitFiles = Directory.EnumerateFiles(output, "junit-*.xml", SearchOption.TopDirectoryOnly).ToArray();
        _stageDetail = "gate-report";
        if (junitFiles.Length != source.ExpectedProjectCount) throw new InvalidDataException();
        ValidateGateReport(output, options.DiffBase is not null, cancellationToken);
        var canonicalCoverage = Path.Join(output, "coverage.cobertura.xml");
        _stageDetail = "retention";
        if (!File.Exists(canonicalCoverage) || Directory.EnumerateFiles(output, "coverage.cobertura.xml", SearchOption.AllDirectories).Count() != source.ExpectedProjectCount + 1)
        {
            throw new InvalidDataException();
        }

        // Managed retention leaves the canonical per-project reports and removes
        // collector-named coverage-*.xml artifacts. Any remaining collector file
        // would make the retention claim unverifiable.
        var unmanagedCoverage = Directory.EnumerateFiles(output, "coverage-*.xml", SearchOption.AllDirectories).ToArray();
        if (unmanagedCoverage.Length != 0)
        {
            throw new InvalidDataException();
        }

        return new CoverageSnapshot(testCases.Count, projectDirectories.Length, true, true);
    }

    private async Task<bool> VerifySourceIntegrityAsync(SourceSnapshot source, CancellationToken cancellationToken)
    {
        var status = await GitStatusAsync(source.IntegrityRoot, new Func<CancellationToken, ValueTask>(_ => CheckBudgetAsync()), cancellationToken).ConfigureAwait(false);
        var hash = await HashTrackedFilesAsync(source.IntegrityRoot, new Func<CancellationToken, ValueTask>(_ => CheckBudgetAsync()), cancellationToken).ConfigureAwait(false);
        var settingsHash = HashBytes(JsonSerializer.SerializeToUtf8Bytes(_settings));
        return string.Equals(status, source.OriginalStatus, StringComparison.Ordinal)
            && string.Equals(hash, source.BeforeHash, StringComparison.Ordinal)
            && string.Equals(settingsHash, source.SettingsHash, StringComparison.Ordinal);
    }

    private async ValueTask CheckBudgetAsync()
    {
        _workspace?.CheckBudget();
        await ValueTask.CompletedTask;
    }

    private static EvidenceProducerResult Failure(string diagnostic, long elapsed = 0) => new(ProducerId, EvidenceProducerOutcome.Failed, [], diagnostic, ElapsedMilliseconds: elapsed);

    private void ValidateSettings()
    {
        ValidateSettingsContract(_settings);
    }

    /// <summary>Validates the required native-consumer settings without starting a command journey.</summary>
    internal static void ValidateSettingsForTests(ValidationSettings settings) => ValidateSettingsContract(settings);

    /// <summary>Resolves a consumer path only when it remains inside the owned source root.</summary>
    internal static string ContainedSourcePathForTests(string root, string relative) => ContainedSourcePath(root, relative);

    /// <summary>Counts test projects using the same solution inventory contract as acceptance.</summary>
    internal static int CountTestProjectsForTests(string root, string solutionPath) => CountTestProjects(root, solutionPath);

    /// <summary>Checks whether a project is classified as an executable test project.</summary>
    internal static bool IsTestProjectForTests(string path) => IsTestProject(path);

    /// <summary>Validates independent JUnit suite counters against testcase elements.</summary>
    internal static void ValidateJUnitCountsForTests(XElement root, IReadOnlyList<XElement> testCases) => ValidateJUnitCounts(root, testCases);

    /// <summary>Reads only the declared JUnit inventory with report, aggregate and testcase bounds.</summary>
    internal static IReadOnlyList<XElement> ReadJUnitReportsForTests(string output, int expectedProjectCount, CancellationToken cancellationToken = default) =>
        ReadJUnitReports(output, expectedProjectCount, cancellationToken);

    /// <summary>Checks whether a skipped testcase is explicitly permitted with a rationale.</summary>
    internal static bool IsPermittedSkipForTests(XElement testCase, IReadOnlyDictionary<string, string>? permittedPatterns) => IsPermittedSkip(testCase, permittedPatterns);

    /// <summary>Validates that every required testcase fragment is present, passing, and not skipped.</summary>
    internal static void ValidateRequiredTestFragmentsForTests(IReadOnlyList<string> fragments, IReadOnlyList<XElement> testCases) => ValidateRequiredTestFragments(fragments, testCases);

    /// <summary>Validates the exact gate thresholds and zero-tolerance result JSON.</summary>
    internal static void ValidateGateReportForTests(string output, bool hasPatch, CancellationToken cancellationToken = default) => ValidateGateReport(output, hasPatch, cancellationToken);

    private static void ValidateSettingsContract(ValidationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.RepositoryRoot) || string.IsNullOrWhiteSpace(settings.PackagePath) || string.IsNullOrWhiteSpace(settings.PackageVersion) || string.IsNullOrWhiteSpace(settings.DotnetPath) || string.IsNullOrWhiteSpace(settings.ScratchParent) || string.IsNullOrWhiteSpace(settings.EvidenceDirectory)) throw new ArgumentException();
        if (settings.MaximumScratchBytes <= 0 || settings.MinimumFreeBytes < 0) throw new ArgumentOutOfRangeException();
    }

    private Dictionary<string, string> BuildEnvironment(string cliHome, string nugetRoot)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        environment["DOTNET_CLI_HOME"] = cliHome;
        environment["DOTNET_NOLOGO"] = "1";
        environment["NUGET_PACKAGES"] = nugetRoot;
        var dotnet = File.ResolveLinkTarget(_settings.DotnetPath, returnFinalTarget: true)?.FullName ?? _settings.DotnetPath;
        var sdkRoot = Path.GetDirectoryName(dotnet)!;
        environment["DOTNET_ROOT"] = sdkRoot;
        environment["DOTNET_HOST_PATH"] = dotnet;
        environment["PATH"] = sdkRoot + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        environment["MSBUILDDISABLENODEREUSE"] = "1";
        environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        environment["UseSharedCompilation"] = "false";
        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        return environment;
    }

    private static Dictionary<string, string> MergeEnvironment(IReadOnlyDictionary<string, string> baseEnvironment, IReadOnlyDictionary<string, string>? overrides)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in baseEnvironment) result[pair.Key] = pair.Value;
        if (overrides is not null) foreach (var pair in overrides) result[pair.Key] = pair.Value;
        return result;
    }

    private async Task<EvidenceCommandResult> RunCheckedAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string> environment, Func<CancellationToken, ValueTask> budget, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(new EvidenceCommandRequest(fileName, arguments, workingDirectory, TimeSpan.FromMinutes(20)) { Environment = environment, MaximumOutputBytes = 4 * 1024 * 1024, CheckBudget = budget }, cancellationToken).ConfigureAwait(false);
        _commandSummaries.Add(new CommandSummary(
            Path.GetFileName(fileName),
            result.ExitCode,
            HashBytes(Encoding.UTF8.GetBytes(result.StandardOutput)),
            HashBytes(Encoding.UTF8.GetBytes(result.StandardError)),
            result.ElapsedMilliseconds));
        _lastExitCode = result.ExitCode;
        _lastOutput = result.StandardOutput;
        _lastError = result.StandardError;
        ValidationProof.Require(!result.OutputTruncated, "complete command output");
        if (result.ExitCode != 0) throw new InvalidOperationException();
        return result;
    }

    private async Task<string> GitStatusAsync(string directory, Func<CancellationToken, ValueTask> budget, CancellationToken token) =>
        (await GitOutputAsync(directory, ["status", "--porcelain", "--untracked-files=all"], budget, token, allowMissing: true).ConfigureAwait(false)).TrimEnd();

    private async Task<string> GitOutputAsync(string directory, IReadOnlyList<string> args, Func<CancellationToken, ValueTask> budget, CancellationToken token, bool allowMissing)
    {
        try
        {
            var result = await _runner.RunAsync(new EvidenceCommandRequest("git", args, directory, TimeSpan.FromMinutes(2)) { MaximumOutputBytes = 256 * 1024, CheckBudget = budget }, token).ConfigureAwait(false);
            ValidationProof.Require(!result.OutputTruncated, "complete git output");
            if (result.ExitCode != 0 && !allowMissing) throw new InvalidDataException("Required source identity command failed.");
            return result.ExitCode == 0 ? result.StandardOutput.Trim() : string.Empty;
        }
        catch (EvidenceCommandException) when (allowMissing)
        {
            return string.Empty;
        }
    }

    private async Task<string> HashTrackedFilesAsync(string directory, Func<CancellationToken, ValueTask> budget, CancellationToken token)
    {
        var hasLocalGit = Directory.Exists(Path.Join(directory, ".git")) || File.Exists(Path.Join(directory, ".git"));
        var listed = hasLocalGit
            ? await GitOutputAsync(directory, ["ls-files", "-z"], budget, token, allowMissing: false).ConfigureAwait(false)
            : string.Empty;
        var files = listed.Length == 0
            ? EnumerateSafeFiles(directory)
                .Select(path => Path.GetRelativePath(directory, path))
                .Order(StringComparer.Ordinal)
                .ToArray()
            : listed.Split('\0', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relative in files)
        {
            await budget(token).ConfigureAwait(false);
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            await AppendFileHashAsync(hash, Path.Join(directory, relative), budget, token).ConfigureAwait(false);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static async Task AppendFileHashAsync(IncrementalHash hash, string path, Func<CancellationToken, ValueTask> budget, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) return;
            hash.AppendData(buffer, 0, read);
            await budget(token).ConfigureAwait(false);
        }
    }

    private static IEnumerable<string> EnumerateSafeFiles(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                if (IsGeneratedDirectory(directory) || File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) continue;
                pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(current))
            {
                if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException();
                yield return file;
            }
        }
    }

    private static int CountTestProjects(string root, string solutionPath)
    {
        var solution = ContainedSourcePath(root, solutionPath);
        if (!File.Exists(solution)) throw new FileNotFoundException();
        var extension = Path.GetExtension(solution);
        IEnumerable<string> paths = extension.ToLowerInvariant() switch
        {
            ".slnx" => ReadSlnxProjects(solution),
            ".sln" => ReadSlnProjects(solution),
            _ => throw new InvalidOperationException(),
        };
        var count = 0;
        foreach (var path in paths)
        {
            var projectPath = ContainedSourcePath(root, Path.GetRelativePath(root, Path.Join(Path.GetDirectoryName(solution)!, path)));
            if (File.Exists(projectPath) && IsTestProject(projectPath)) count++;
        }

        if (count == 0) throw new InvalidOperationException();
        return count;

        static IEnumerable<string> ReadSlnxProjects(string path)
        {
            using var stream = File.OpenRead(path);
            using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader, LoadOptions.None);
            var projects = document.Descendants().Where(element => element.Name.LocalName == "Project").Select(element => (string?)element.Attribute("Path")).OfType<string>().ToArray();
            if (projects.Length == 0) throw new InvalidOperationException();
            return projects;
        }

        static IEnumerable<string> ReadSlnProjects(string path) =>
            File.ReadLines(path)
                .Where(line => line.Contains(".csproj\"", StringComparison.OrdinalIgnoreCase))
                .Select(line => line.Split('\"', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(5) ?? throw new InvalidOperationException())
                .ToArray();
    }

    private static bool IsTestProject(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader, LoadOptions.None);
        return Path.GetFileNameWithoutExtension(path).EndsWith(".Tests", StringComparison.Ordinal)
            || Path.GetFileNameWithoutExtension(path).EndsWith(".IntegrationTests", StringComparison.Ordinal)
            || document.Descendants().Any(element => element.Name.LocalName == "IsTestProject" && string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
    }

    private static string ContainedSourcePath(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidDataException("Consumer paths must remain source-relative.");
        var path = Path.GetFullPath(Path.Join(root, relative));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Consumer paths must remain inside the owned source.");
        return path;
    }

    private static void ValidateGateReport(string output, bool hasPatch, CancellationToken cancellationToken)
    {
        using var document = ValidationProof.ReadJsonFile(Path.Join(output, "coverage-gate.json"), 1024 * 1024, cancellationToken);
        var root = document.RootElement;
        ValidationProof.Require(root.GetProperty("passed").GetBoolean(), "coverage gate passed");
        ValidationProof.Require(root.GetProperty("tolerancePercent").GetDecimal() == 0m, "coverage gate zero tolerance");
        var thresholds = root.GetProperty("thresholds");
        ValidationProof.Require(thresholds.GetProperty("line").GetDecimal() == 95m && thresholds.GetProperty("branch").GetDecimal() == 85m, "coverage gate thresholds");
        var effective = root.GetProperty("effectiveThresholds");
        ValidationProof.Require(effective.GetProperty("line").GetDecimal() == 95m && effective.GetProperty("branch").GetDecimal() == 85m, "coverage gate effective thresholds");
        if (hasPatch)
        {
            ValidationProof.Require(thresholds.GetProperty("patchLine").GetDecimal() == 95m && thresholds.GetProperty("patchBranch").GetDecimal() == 85m, "patch gate thresholds");
            ValidationProof.Require(effective.GetProperty("patchLine").GetDecimal() == 95m && effective.GetProperty("patchBranch").GetDecimal() == 85m, "patch gate effective thresholds");
        }
    }

    private static string GetArchiveAssemblyHash(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var entry = archive.Entries.Single(candidate => string.Equals(candidate.FullName, $"tools/net10.0/any/{CliAssembly}", StringComparison.OrdinalIgnoreCase));
        using var stream = entry.Open();
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string SecurityElementEscape(string value) => System.Security.SecurityElement.Escape(value) ?? value;

    private static string HashBytes(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        var pending = new Stack<string>([source]);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                if (IsGeneratedDirectory(directory) || File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) continue;
                var relative = Path.GetRelativePath(source, directory);
                Directory.CreateDirectory(Path.Join(destination, relative));
                pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(current))
            {
                if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException();
                File.Copy(file, Path.Join(destination, Path.GetRelativePath(source, file)), overwrite: false);
            }
        }
    }

    private static bool IsGeneratedDirectory(string path) =>
        Path.GetFileName(path) is ".git" or "bin" or "obj";

    private static bool HasFailure(XElement testCase) => testCase.Elements().Any(element => element.Name.LocalName is "failure" or "error");

    private static bool HasSkip(XElement testCase) => testCase.Elements().Any(element => element.Name.LocalName == "skipped");

    private static bool IsPermittedSkip(XElement testCase, IReadOnlyDictionary<string, string>? permittedPatterns)
    {
        if (!HasSkip(testCase)) return true;
        if (permittedPatterns is null || permittedPatterns.Count == 0) return false;
        var attributes = string.Join(" ", testCase.Attributes().Select(attribute => attribute.Value));
        return permittedPatterns.Any(pair => !string.IsNullOrWhiteSpace(pair.Key)
            && !string.IsNullOrWhiteSpace(pair.Value)
            && attributes.Contains(pair.Key, StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateRequiredTestFragments(IReadOnlyList<string> fragments, IReadOnlyList<XElement> testCases)
    {
        foreach (var fragment in fragments)
        {
            var matches = testCases.Where(test => test.Attributes().Any(attribute => attribute.Value.Contains(fragment, StringComparison.Ordinal))).ToArray();
            if (matches.Length == 0 || matches.Any(test => HasFailure(test) || HasSkip(test))) throw new InvalidDataException();
        }
    }

    private static IReadOnlyList<XElement> ReadJUnitReports(string output, int expectedProjectCount, CancellationToken cancellationToken)
    {
        const long maximumReportBytes = 16L * 1024 * 1024;
        const long maximumTotalBytes = 64L * 1024 * 1024;
        const int maximumTestCases = 100_000;
        if (expectedProjectCount <= 0 || expectedProjectCount > maximumTestCases) throw new InvalidDataException("Invalid JUnit project inventory.");
        var reports = Directory.EnumerateFiles(output, "junit-*.xml", SearchOption.TopDirectoryOnly).Take(expectedProjectCount + 1).ToArray();
        if (reports.Length != expectedProjectCount) throw new InvalidDataException("JUnit project inventory differs from the declared scope.");
        var cases = new List<XElement>();
        var totalBytes = 0L;
        foreach (var report in reports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(report, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length == 0 || stream.Length > maximumReportBytes || (totalBytes += stream.Length) > maximumTotalBytes)
                throw new InvalidDataException("JUnit evidence exceeds the report or aggregate byte limit.");
            using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = maximumReportBytes,
            });
            var document = XDocument.Load(reader, LoadOptions.None);
            if (document.Root?.Name.LocalName is not ("testsuite" or "testsuites")) throw new InvalidDataException("Invalid JUnit report root.");
            var reportCases = document.Descendants().Where(node => node.Name.LocalName == "testcase").Take(maximumTestCases - cases.Count + 1).ToArray();
            if (reportCases.Length > maximumTestCases - cases.Count) throw new InvalidDataException("JUnit evidence exceeds the testcase limit.");
            ValidateJUnitCounts(document.Root, reportCases);
            foreach (var testCase in reportCases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Retain assertion inputs only; testcase output and parent links must not retain every document.
                cases.Add(new XElement("testcase", testCase.Attributes(),
                    testCase.Elements().Where(child => child.Name.LocalName is "failure" or "error" or "skipped")
                        .Select(child => new XElement(child.Name.LocalName))));
            }
        }
        return cases;
    }

    private static void ValidateJUnitCounts(XElement root, IReadOnlyList<XElement> testCases)
    {
        IReadOnlyList<XElement> suites = root.Name.LocalName == "testsuite" ? [root] : root.Descendants().Where(node => node.Name.LocalName == "testsuite").ToArray();
        foreach (var suite in suites)
        {
            var cases = suite.Descendants().Where(node => node.Name.LocalName == "testcase").ToArray();
            if (ReadCount(suite, "tests") != cases.Length
                || ReadCount(suite, "failures") != cases.Count(node => node.Elements().Any(child => child.Name.LocalName == "failure"))
                || ReadCount(suite, "errors") != cases.Count(node => node.Elements().Any(child => child.Name.LocalName == "error"))
                || ReadCount(suite, "skipped") != cases.Count(node => node.Elements().Any(child => child.Name.LocalName == "skipped")))
            {
                throw new InvalidDataException();
            }
        }

        static int ReadCount(XElement element, string name) =>
            int.TryParse(element.Attribute(name)?.Value, out var value) ? value : -1;
    }

    private sealed record SourceSnapshot(string Root, string IntegrityRoot, string OriginalStatus, string OriginalCommit, string BeforeHash, string SettingsHash, string SolutionPath, IReadOnlyList<string> TestProjects, int ExpectedProjectCount, IReadOnlyList<string> RequiredTestFragments);
    private sealed record PackageSnapshot(string PackageSha256, string CliAssemblySha256, string Executable, IReadOnlyDictionary<string, string> Environment);
    private sealed record CoverageSnapshot(int TestCaseCount, int ProjectCount, bool GatePassed, bool ManagedRetentionVerified);
    private sealed record CommandSummary(string Executable, int ExitCode, string StandardOutputSha256, string StandardErrorSha256, long ElapsedMilliseconds);
    private sealed record ValidationSummary(string PackageVersion, string PackageSha256, string CliAssemblySha256, int TestCaseCount, int ProjectCount, bool CoverageGatePassed, bool ManagedRetentionVerified, bool SourceIntegrityVerified, IReadOnlyList<string> AssertionIds, IReadOnlyList<CommandSummary> Commands);
}
