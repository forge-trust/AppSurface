using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CliWrap;
using CliWrap.Buffered;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>
/// Runs external commands through CliWrap for protected package publishing and smoke-install workflows.
/// </summary>
internal sealed class CliWrapCommandRunner : IExternalCommandRunner
{
    /// <inheritdoc />
    public async Task<ExternalCommandResult> RunAsync(ExternalCommandRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.TimeoutMilliseconds);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(request.TimeoutMilliseconds);
            var command = Cli.Wrap(request.FileName)
                .WithArguments(request.Arguments)
                .WithWorkingDirectory(request.WorkingDirectory)
                .WithValidation(CommandResultValidation.None);

            if (request.Environment is not null)
            {
                command = command.WithEnvironmentVariables(request.Environment);
            }

            var result = await command.ExecuteBufferedAsync(timeoutCts.Token);
            return new ExternalCommandResult(result.ExitCode, result.StandardOutput, result.StandardError);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ExternalCommandResult(
                -1,
                string.Empty,
                $"{request.OperationName} timed out after {request.TimeoutMilliseconds} ms while {request.TimeoutDescription}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ExternalCommandResult(
                -1,
                string.Empty,
                $"{request.OperationName} failed while {request.TimeoutDescription}: {ex.Message}");
        }
    }
}

/// <summary>
/// External command runner used by publish and smoke workflows when non-zero exit codes are meaningful results.
/// </summary>
internal interface IExternalCommandRunner
{
    /// <summary>
    /// Runs a command and returns stdout, stderr, and exit code without throwing for non-zero exits.
    /// </summary>
    /// <param name="request">Command request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Captured command result.</returns>
    Task<ExternalCommandResult> RunAsync(ExternalCommandRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Command invocation for CliWrap-backed release automation.
/// </summary>
internal sealed record ExternalCommandRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string OperationName,
    string TimeoutDescription,
    int TimeoutMilliseconds,
    IReadOnlyDictionary<string, string?>? Environment = null);

/// <summary>
/// Captured command result including non-zero exit codes.
/// </summary>
internal sealed record ExternalCommandResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Writes the machine-readable package artifact manifest consumed by protected publish jobs.
/// </summary>
internal sealed class PackageArtifactManifestWriter
{
    /// <summary>
    /// Writes a manifest that binds validated package ids to artifact file names and SHA-512 hashes.
    /// </summary>
    /// <param name="report">Package artifact validation report.</param>
    /// <param name="artifactsDirectory">Directory containing the validated package artifacts.</param>
    /// <param name="manifestPath">Destination manifest path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal async Task WriteAsync(
        PackageArtifactValidationReport report,
        string artifactsDirectory,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        var entries = new List<PackageArtifactManifestEntry>(report.Entries.Count);
        foreach (var entry in report.Entries)
        {
            var artifactPath = string.IsNullOrWhiteSpace(entry.ArtifactPath)
                ? Path.Combine(artifactsDirectory, $"{entry.PackageId}.{report.PackageVersion}.nupkg")
                : entry.ArtifactPath;
            if (!File.Exists(artifactPath))
            {
                throw new PackageIndexException($"Validated artifact '{artifactPath}' does not exist.");
            }

            entries.Add(new PackageArtifactManifestEntry(
                entry.PackageId,
                entry.ProjectPath,
                PackagePublishDecisionFormatter.Format(entry.Decision),
                Path.GetFileName(artifactPath),
                await PackageHash.ComputeSha512Async(artifactPath, cancellationToken),
                entry.IsTool));
        }

        var manifest = new PackageArtifactManifest(
            1,
            report.PackageVersion,
            DateTimeOffset.UtcNow,
            entries);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        await using var stream = File.Create(manifestPath);
        await JsonSerializer.SerializeAsync(stream, manifest, PackageArtifactJson.Options, cancellationToken);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(Environment.NewLine), cancellationToken);
    }
}

/// <summary>
/// Reads and validates the artifact manifest written by the package artifact verifier.
/// </summary>
internal sealed class PackageArtifactManifestReader
{
    /// <summary>
    /// Reads a package artifact manifest from disk.
    /// </summary>
    /// <param name="manifestPath">Manifest path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validated artifact manifest.</returns>
    internal async Task<PackageArtifactManifest> ReadAsync(string manifestPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        if (!File.Exists(manifestPath))
        {
            throw new PackageIndexException($"Package artifact manifest '{manifestPath}' does not exist.");
        }

        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<PackageArtifactManifest>(
            stream,
            PackageArtifactJson.Options,
            cancellationToken);
        if (manifest is null)
        {
            throw new PackageIndexException($"Package artifact manifest '{manifestPath}' is empty.");
        }

        Validate(manifest, manifestPath);
        return manifest;
    }

    private static void Validate(PackageArtifactManifest manifest, string manifestPath)
    {
        if (manifest.SchemaVersion != 1)
        {
            throw new PackageIndexException($"Package artifact manifest '{manifestPath}' uses unsupported schema version '{manifest.SchemaVersion}'.");
        }

        PackageVersionValidator.RequirePrerelease(manifest.PackageVersion);
        if (manifest.Entries.Count == 0)
        {
            throw new PackageIndexException($"Package artifact manifest '{manifestPath}' does not contain any entries.");
        }

        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.PackageId))
            {
                throw new PackageIndexException($"Package artifact manifest '{manifestPath}' contains an entry without a package id.");
            }

            if (!packageIds.Add(entry.PackageId))
            {
                throw new PackageIndexException($"Package artifact manifest '{manifestPath}' contains duplicate package id '{entry.PackageId}'.");
            }

            RequireManifestValue(manifestPath, entry.PackageId, "project_path", entry.ProjectPath);
            RequireManifestValue(manifestPath, entry.PackageId, "decision", entry.Decision);
            RequireManifestValue(manifestPath, entry.PackageId, "artifact_file_name", entry.ArtifactFileName);
            RequireManifestValue(manifestPath, entry.PackageId, "sha512", entry.Sha512);
            if (Path.IsPathRooted(entry.ArtifactFileName)
                || !string.Equals(Path.GetFileName(entry.ArtifactFileName), entry.ArtifactFileName, StringComparison.Ordinal))
            {
                throw new PackageIndexException(
                    $"Package artifact manifest '{manifestPath}' entry '{entry.PackageId}' must use an artifact_file_name without directory segments.");
            }
        }
    }

    private static void RequireManifestValue(
        string manifestPath,
        string packageId,
        string propertyName,
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PackageIndexException(
                $"Package artifact manifest '{manifestPath}' entry '{packageId}' is missing '{propertyName}'.");
        }
    }
}

/// <summary>
/// Publishes a validated prerelease package artifact set to NuGet in manifest order.
/// </summary>
internal sealed class PackagePrereleasePublishWorkflow
{
    internal const int PushTimeoutMilliseconds = 180_000;

    private readonly PackagePublishPlanResolver _planResolver;
    private readonly PackageArtifactManifestReader _manifestReader;
    private readonly IExternalCommandRunner _commandRunner;
    private readonly PackagePublishLedgerRenderer _ledgerRenderer;

    internal PackagePrereleasePublishWorkflow(
        PackagePublishPlanResolver planResolver,
        PackageArtifactManifestReader manifestReader,
        IExternalCommandRunner commandRunner,
        PackagePublishLedgerRenderer ledgerRenderer)
    {
        _planResolver = planResolver;
        _manifestReader = manifestReader;
        _commandRunner = commandRunner;
        _ledgerRenderer = ledgerRenderer;
    }

    /// <summary>
    /// Publishes each artifact selected by the checked-in manifest and writes a markdown ledger.
    /// </summary>
    /// <param name="request">Publish request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Publish ledger.</returns>
    internal async Task<PackagePublishLedger> RunAsync(
        PackagePrereleasePublishRequest request,
        CancellationToken cancellationToken)
    {
        ValidatePublishRequest(request);
        var apiKey = Environment.GetEnvironmentVariable(request.ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new PackageIndexException($"Environment variable '{request.ApiKeyEnvironmentVariable}' must contain the NuGet API key.");
        }

        var plan = await _planResolver.ResolveAsync(request.RepositoryRoot, request.ManifestPath, cancellationToken);
        var artifactManifest = await _manifestReader.ReadAsync(request.ArtifactManifestPath, cancellationToken);
        var plannedEntries = PackageArtifactManifestPlanValidator.Validate(
            plan,
            artifactManifest,
            request.ArtifactsInputPath);
        var ledgerEntries = new List<PackagePublishLedgerEntry>(plannedEntries.Count);
        var stopPublishing = false;

        foreach (var entry in plannedEntries)
        {
            if (stopPublishing)
            {
                ledgerEntries.Add(new PackagePublishLedgerEntry(
                    entry.ManifestEntry.PackageId,
                    entry.ManifestEntry.ProjectPath,
                    entry.ManifestEntry.ArtifactFileName,
                    PackagePublishStatus.SkippedAfterFailure,
                    0,
                    "Skipped because an earlier package failed to publish."));
                await PersistLedgerAsync(
                    artifactManifest.PackageVersion,
                    request,
                    ledgerEntries,
                    cancellationToken);
                continue;
            }

            var result = await RunPushAsync(request, entry, apiKey, cancellationToken);

            var output = RedactSensitiveOutput(CombineOutput(result), apiKey);
            if (result.ExitCode == 0)
            {
                ledgerEntries.Add(new PackagePublishLedgerEntry(
                    entry.ManifestEntry.PackageId,
                    entry.ManifestEntry.ProjectPath,
                    entry.ManifestEntry.ArtifactFileName,
                    IsDuplicateOutput(output) ? PackagePublishStatus.DuplicateReported : PackagePublishStatus.Pushed,
                    result.ExitCode,
                    output));
                await PersistLedgerAsync(
                    artifactManifest.PackageVersion,
                    request,
                    ledgerEntries,
                    cancellationToken);
                continue;
            }

            stopPublishing = true;
            ledgerEntries.Add(new PackagePublishLedgerEntry(
                entry.ManifestEntry.PackageId,
                entry.ManifestEntry.ProjectPath,
                entry.ManifestEntry.ArtifactFileName,
                PackagePublishStatus.Failed,
                result.ExitCode,
                output));
            await PersistLedgerAsync(
                artifactManifest.PackageVersion,
                request,
                ledgerEntries,
                cancellationToken);
        }

        var ledger = new PackagePublishLedger(artifactManifest.PackageVersion, request.Source, ledgerEntries);
        return ledger;
    }

    private async Task<ExternalCommandResult> RunPushAsync(
        PackagePrereleasePublishRequest request,
        PlannedPackageArtifact entry,
        string apiKey,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _commandRunner.RunAsync(
                new ExternalCommandRequest(
                    "dotnet",
                    [
                        "nuget",
                        "push",
                        entry.ArtifactPath,
                        "--source",
                        request.Source,
                        "--api-key",
                        apiKey,
                        "--skip-duplicate"
                    ],
                    request.RepositoryRoot,
                    "dotnet nuget push",
                    $"publishing '{entry.ManifestEntry.PackageId}'",
                    PushTimeoutMilliseconds,
                    ReleaseEnvironment.Default),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ExternalCommandResult(-1, string.Empty, ex.Message);
        }
    }

    private async Task PersistLedgerAsync(
        string packageVersion,
        PackagePrereleasePublishRequest request,
        IReadOnlyList<PackagePublishLedgerEntry> ledgerEntries,
        CancellationToken cancellationToken)
    {
        var ledger = new PackagePublishLedger(packageVersion, request.Source, ledgerEntries);
        Directory.CreateDirectory(Path.GetDirectoryName(request.PublishLogPath)!);
        await File.WriteAllTextAsync(request.PublishLogPath, _ledgerRenderer.RenderMarkdown(ledger), cancellationToken);
    }

    private static void ValidatePublishRequest(PackagePrereleasePublishRequest request)
    {
        if (!Directory.Exists(request.RepositoryRoot))
        {
            throw new PackageIndexException($"Repository root '{request.RepositoryRoot}' does not exist.");
        }

        if (!File.Exists(request.ManifestPath))
        {
            throw new PackageIndexException($"Manifest '{Path.GetRelativePath(request.RepositoryRoot, request.ManifestPath)}' does not exist.");
        }

        if (!Directory.Exists(request.ArtifactsInputPath))
        {
            throw new PackageIndexException($"Package artifact input directory '{request.ArtifactsInputPath}' does not exist.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.ArtifactManifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PublishLogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ApiKeyEnvironmentVariable);
    }

    private static string CombineOutput(ExternalCommandResult result)
    {
        return string.Join(
            Environment.NewLine,
            new[] { result.StandardOutput.TrimEnd(), result.StandardError.TrimEnd() }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string RedactSensitiveOutput(string output, string apiKey)
    {
        if (string.IsNullOrEmpty(output))
        {
            return output;
        }

        var redacted = output.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
        return Regex.Replace(
            redacted,
            @"(?i)(api[-_ ]?key\s*[:=]\s*)[^\s]+",
            "$1[redacted]");
    }

    private static bool IsDuplicateOutput(string output)
    {
        return output.Contains("already exists", StringComparison.OrdinalIgnoreCase)
            || output.Contains("conflict", StringComparison.OrdinalIgnoreCase)
            || output.Contains("409", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Restores published public packages from a clean NuGet configuration after publish completes.
/// </summary>
internal sealed class PackageSmokeInstallWorkflow
{
    internal const int RestoreTimeoutMilliseconds = 180_000;
    internal const int ToolInstallTimeoutMilliseconds = 180_000;
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(120)
    ];

    private readonly PackageArtifactManifestReader _manifestReader;
    private readonly PackagePublishPlanResolver _planResolver;
    private readonly IExternalCommandRunner _commandRunner;
    private readonly PackageSmokeInstallReportRenderer _reportRenderer;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    internal PackageSmokeInstallWorkflow(
        PackageArtifactManifestReader manifestReader,
        PackagePublishPlanResolver planResolver,
        IExternalCommandRunner commandRunner,
        PackageSmokeInstallReportRenderer reportRenderer,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _manifestReader = manifestReader;
        _planResolver = planResolver;
        _commandRunner = commandRunner;
        _reportRenderer = reportRenderer;
        _delayAsync = delayAsync;
    }

    /// <summary>
    /// Restores each public package from the configured package source in an isolated workspace.
    /// </summary>
    /// <param name="request">Smoke install request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Smoke install report.</returns>
    internal async Task<PackageSmokeInstallReport> RunAsync(
        PackageSmokeInstallRequest request,
        CancellationToken cancellationToken)
    {
        ValidateSmokeRequest(request);
        var manifest = await _manifestReader.ReadAsync(request.ArtifactManifestPath, cancellationToken);
        var plan = await _planResolver.ResolveAsync(
            request.RepositoryRoot,
            request.ManifestPath,
            cancellationToken);
        var artifactDirectory = Path.GetDirectoryName(Path.GetFullPath(request.ArtifactManifestPath))
            ?? throw new PackageIndexException("Package artifact manifest path must include a directory.");
        var entries = PackageArtifactManifestPlanValidator
            .Validate(plan, manifest, artifactDirectory)
            .Where(entry => string.Equals(entry.ManifestEntry.Decision, "publish", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Directory.CreateDirectory(request.WorkDirectory);
        var nugetConfigPath = Path.Combine(request.WorkDirectory, "NuGet.config");
        var sharedPackagesPath = Path.Combine(request.WorkDirectory, "packages");
        var dotnetHomePath = Path.Combine(request.WorkDirectory, "dotnet-home");
        Directory.CreateDirectory(sharedPackagesPath);
        Directory.CreateDirectory(dotnetHomePath);
        await File.WriteAllTextAsync(nugetConfigPath, RenderNuGetConfig(request.Source), cancellationToken);

        var reportEntries = new List<PackageSmokeInstallReportEntry>(entries.Length);
        foreach (var entry in entries)
        {
            var packageWorkDirectory = Path.Combine(request.WorkDirectory, SanitizeFileName(entry.ManifestEntry.PackageId));
            if (Directory.Exists(packageWorkDirectory))
            {
                Directory.Delete(packageWorkDirectory, recursive: true);
            }

            Directory.CreateDirectory(packageWorkDirectory);
            ExternalCommandResult result;
            if (entry.ManifestEntry.IsTool)
            {
                result = await RunToolInstallWithRetryAsync(
                    request,
                    manifest.PackageVersion,
                    entry.ManifestEntry,
                    packageWorkDirectory,
                    nugetConfigPath,
                    dotnetHomePath,
                    sharedPackagesPath,
                    cancellationToken);
            }
            else
            {
                await WriteSmokeProjectAsync(
                    packageWorkDirectory,
                    entry.ManifestEntry.PackageId,
                    manifest.PackageVersion,
                    cancellationToken);
                result = await RunRestoreWithRetryAsync(
                    entry.ManifestEntry,
                    packageWorkDirectory,
                    nugetConfigPath,
                    dotnetHomePath,
                    sharedPackagesPath,
                    cancellationToken);
            }

            reportEntries.Add(new PackageSmokeInstallReportEntry(
                entry.ManifestEntry.PackageId,
                entry.ManifestEntry.ProjectPath,
                entry.ManifestEntry.IsTool,
                result.ExitCode == 0 ? PackageSmokeInstallStatus.Restored : PackageSmokeInstallStatus.Failed,
                result.ExitCode,
                CombineOutput(result)));
        }

        var report = new PackageSmokeInstallReport(manifest.PackageVersion, request.Source, reportEntries);
        Directory.CreateDirectory(Path.GetDirectoryName(request.ReportPath)!);
        await File.WriteAllTextAsync(request.ReportPath, _reportRenderer.RenderMarkdown(report), cancellationToken);
        return report;
    }

    private async Task<ExternalCommandResult> RunRestoreWithRetryAsync(
        PackageArtifactManifestEntry entry,
        string packageWorkDirectory,
        string nugetConfigPath,
        string dotnetHomePath,
        string sharedPackagesPath,
        CancellationToken cancellationToken)
    {
        return await RunWithRetryAsync(
            () => _commandRunner.RunAsync(
                new ExternalCommandRequest(
                    "dotnet",
                    [
                        "restore",
                        Path.Combine(packageWorkDirectory, "Smoke.csproj"),
                        "--configfile",
                        nugetConfigPath,
                        "--packages",
                        sharedPackagesPath
                    ],
                    packageWorkDirectory,
                    "dotnet restore",
                    $"restoring '{entry.PackageId}'",
                    RestoreTimeoutMilliseconds,
                    CreateSmokeEnvironment(dotnetHomePath, sharedPackagesPath)),
                cancellationToken),
            cancellationToken);
    }

    private async Task<ExternalCommandResult> RunToolInstallWithRetryAsync(
        PackageSmokeInstallRequest request,
        string packageVersion,
        PackageArtifactManifestEntry entry,
        string packageWorkDirectory,
        string nugetConfigPath,
        string dotnetHomePath,
        string sharedPackagesPath,
        CancellationToken cancellationToken)
    {
        var toolPath = Path.Combine(packageWorkDirectory, "tools");
        Directory.CreateDirectory(toolPath);
        return await RunWithRetryAsync(
            () => _commandRunner.RunAsync(
                new ExternalCommandRequest(
                    "dotnet",
                    [
                        "tool",
                        "install",
                        entry.PackageId,
                        "--version",
                        packageVersion,
                        "--tool-path",
                        toolPath,
                        "--configfile",
                        nugetConfigPath,
                        "--add-source",
                        request.Source
                    ],
                    packageWorkDirectory,
                    "dotnet tool install",
                    $"installing tool '{entry.PackageId}'",
                    ToolInstallTimeoutMilliseconds,
                    CreateSmokeEnvironment(dotnetHomePath, sharedPackagesPath)),
                cancellationToken),
            cancellationToken);
    }

    private async Task<ExternalCommandResult> RunWithRetryAsync(
        Func<Task<ExternalCommandResult>> runAsync,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            var result = await runAsync();
            if (result.ExitCode == 0 || attempt == RetryDelays.Length)
            {
                return result;
            }

            await _delayAsync(RetryDelays[attempt], cancellationToken);
        }

        throw new UnreachableException();
    }

    private static void ValidateSmokeRequest(PackageSmokeInstallRequest request)
    {
        if (!Directory.Exists(request.RepositoryRoot))
        {
            throw new PackageIndexException($"Repository root '{request.RepositoryRoot}' does not exist.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.ArtifactManifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ManifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ReportPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Source);
    }

    private static async Task WriteSmokeProjectAsync(
        string packageWorkDirectory,
        string packageId,
        string packageVersion,
        CancellationToken cancellationToken)
    {
        var project = new XDocument(
            new XElement("Project",
                new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup",
                    new XElement("OutputType", "Exe"),
                    new XElement("TargetFramework", "net10.0"),
                    new XElement("ImplicitUsings", "enable"),
                    new XElement("Nullable", "enable")),
                new XElement("ItemGroup",
                    new XElement("PackageReference",
                        new XAttribute("Include", packageId),
                        new XAttribute("Version", packageVersion)))));
        await File.WriteAllTextAsync(Path.Combine(packageWorkDirectory, "Smoke.csproj"), project.ToString(), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(packageWorkDirectory, "Program.cs"), "Console.WriteLine(\"smoke\");" + Environment.NewLine, cancellationToken);
    }

    private static string RenderNuGetConfig(string source)
    {
        var escapedSource = SecurityElement.Escape(source) ?? source;
        return $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget-org" value="{{escapedSource}}" />
              </packageSources>
            </configuration>
            """;
    }

    private static IReadOnlyDictionary<string, string?> CreateSmokeEnvironment(string dotnetHomePath, string sharedPackagesPath)
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

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray());
    }

    private static string CombineOutput(ExternalCommandResult result)
    {
        return string.Join(
            Environment.NewLine,
            new[] { result.StandardOutput.TrimEnd(), result.StandardError.TrimEnd() }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }
}

/// <summary>
/// Renders protected publish outcomes for workflow artifacts.
/// </summary>
internal sealed class PackagePublishLedgerRenderer
{
    internal string RenderMarkdown(PackagePublishLedger ledger)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# NuGet prerelease publish ledger");
        builder.AppendLine();
        builder.AppendLine($"Version: `{ledger.PackageVersion}`");
        builder.AppendLine($"Source: `{ledger.Source}`");
        builder.AppendLine();
        builder.AppendLine("| Package | Project | Artifact | Status | Exit code |");
        builder.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var entry in ledger.Entries)
        {
            builder.AppendLine($"| `{entry.PackageId}` | `{entry.ProjectPath}` | `{entry.ArtifactFileName}` | `{FormatStatus(entry.Status)}` | `{entry.ExitCode}` |");
        }

        builder.AppendLine();
        builder.AppendLine("## Details");
        foreach (var entry in ledger.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Output)))
        {
            builder.AppendLine();
            builder.AppendLine($"### {entry.PackageId}");
            builder.AppendLine();
            builder.AppendLine("```text");
            builder.AppendLine(entry.Output.TrimEnd());
            builder.AppendLine("```");
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string FormatStatus(PackagePublishStatus status)
    {
        return status switch
        {
            PackagePublishStatus.Pushed => "pushed",
            PackagePublishStatus.DuplicateReported => "duplicate-reported",
            PackagePublishStatus.Failed => "failed",
            PackagePublishStatus.SkippedAfterFailure => "skipped-after-failure",
            _ => status.ToString()
        };
    }
}

/// <summary>
/// Renders package smoke install outcomes for workflow artifacts.
/// </summary>
internal sealed class PackageSmokeInstallReportRenderer
{
    internal string RenderMarkdown(PackageSmokeInstallReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Package smoke install report");
        builder.AppendLine();
        builder.AppendLine($"Version: `{report.PackageVersion}`");
        builder.AppendLine($"Source: `{report.Source}`");
        builder.AppendLine();
        builder.AppendLine("| Package | Project | Kind | Status | Exit code |");
        builder.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var entry in report.Entries)
        {
            builder.AppendLine($"| `{entry.PackageId}` | `{entry.ProjectPath}` | `{(entry.IsTool ? "tool" : "package")}` | `{FormatStatus(entry.Status)}` | `{entry.ExitCode}` |");
        }

        builder.AppendLine();
        builder.AppendLine("## Details");
        foreach (var entry in report.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Output)))
        {
            builder.AppendLine();
            builder.AppendLine($"### {entry.PackageId}");
            builder.AppendLine();
            builder.AppendLine("```text");
            builder.AppendLine(entry.Output.TrimEnd());
            builder.AppendLine("```");
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string FormatStatus(PackageSmokeInstallStatus status)
    {
        return status switch
        {
            PackageSmokeInstallStatus.Restored => "restored",
            PackageSmokeInstallStatus.Failed => "failed",
            _ => status.ToString()
        };
    }
}

/// <summary>
/// Request for the protected NuGet prerelease publish workflow.
/// </summary>
/// <param name="RepositoryRoot">Repository root used for plan resolution.</param>
/// <param name="ManifestPath">Checked-in package manifest path.</param>
/// <param name="ArtifactsInputPath">Directory containing validated <c>.nupkg</c> files.</param>
/// <param name="ArtifactManifestPath">Machine-readable artifact manifest path.</param>
/// <param name="PublishLogPath">Markdown publish ledger path.</param>
/// <param name="Source">NuGet source URL.</param>
/// <param name="ApiKeyEnvironmentVariable">Environment variable that supplies the NuGet API key.</param>
internal sealed record PackagePrereleasePublishRequest(
    string RepositoryRoot,
    string ManifestPath,
    string ArtifactsInputPath,
    string ArtifactManifestPath,
    string PublishLogPath,
    string Source,
    string ApiKeyEnvironmentVariable);

/// <summary>
/// Request for the post-publish smoke install workflow.
/// </summary>
/// <param name="RepositoryRoot">Repository root used for path display and validation.</param>
/// <param name="ManifestPath">Checked-in package manifest path used to revalidate the artifact manifest.</param>
/// <param name="ArtifactManifestPath">Machine-readable artifact manifest path.</param>
/// <param name="WorkDirectory">Isolated smoke install work directory.</param>
/// <param name="ReportPath">Markdown smoke install report path.</param>
/// <param name="Source">NuGet source URL.</param>
internal sealed record PackageSmokeInstallRequest(
    string RepositoryRoot,
    string ManifestPath,
    string ArtifactManifestPath,
    string WorkDirectory,
    string ReportPath,
    string Source);

/// <summary>
/// Machine-readable artifact manifest that binds validated package artifacts to immutable hashes.
/// </summary>
/// <param name="SchemaVersion">Manifest schema version.</param>
/// <param name="PackageVersion">Exact prerelease package version.</param>
/// <param name="GeneratedAtUtc">UTC timestamp when the manifest was generated.</param>
/// <param name="Entries">Manifest entries in package publish order.</param>
internal sealed record PackageArtifactManifest(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("package_version")] string PackageVersion,
    [property: JsonPropertyName("generated_at_utc")] DateTimeOffset GeneratedAtUtc,
    [property: JsonPropertyName("entries")] IReadOnlyList<PackageArtifactManifestEntry> Entries);

/// <summary>
/// One package artifact selected from the checked-in package manifest.
/// </summary>
/// <param name="PackageId">NuGet package id.</param>
/// <param name="ProjectPath">Repository-relative project path.</param>
/// <param name="Decision">Publish decision string from the package plan.</param>
/// <param name="ArtifactFileName">Package artifact file name without directory segments.</param>
/// <param name="Sha512">Lowercase hexadecimal SHA-512 hash of the package artifact.</param>
/// <param name="IsTool">Whether the artifact is a .NET tool package.</param>
internal sealed record PackageArtifactManifestEntry(
    [property: JsonPropertyName("package_id")] string PackageId,
    [property: JsonPropertyName("project_path")] string ProjectPath,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("artifact_file_name")] string ArtifactFileName,
    [property: JsonPropertyName("sha512")] string Sha512,
    [property: JsonPropertyName("is_tool")] bool IsTool);

/// <summary>
/// Publish result for a coordinated prerelease package version.
/// </summary>
/// <param name="PackageVersion">Exact prerelease package version.</param>
/// <param name="Source">NuGet source URL.</param>
/// <param name="Entries">Per-package publish outcomes.</param>
internal sealed record PackagePublishLedger(
    string PackageVersion,
    string Source,
    IReadOnlyList<PackagePublishLedgerEntry> Entries);

/// <summary>
/// Publish outcome for one package artifact.
/// </summary>
/// <param name="PackageId">NuGet package id.</param>
/// <param name="ProjectPath">Repository-relative project path.</param>
/// <param name="ArtifactFileName">Package artifact file name.</param>
/// <param name="Status">Publish status.</param>
/// <param name="ExitCode">Exit code from <c>dotnet nuget push</c>, or zero for skipped packages.</param>
/// <param name="Output">Captured publish output with secrets excluded.</param>
internal sealed record PackagePublishLedgerEntry(
    string PackageId,
    string ProjectPath,
    string ArtifactFileName,
    PackagePublishStatus Status,
    int ExitCode,
    string Output);

/// <summary>
/// Publish status values written to the protected publish ledger.
/// </summary>
internal enum PackagePublishStatus
{
    Pushed,
    DuplicateReported,
    Failed,
    SkippedAfterFailure
}

/// <summary>
/// Smoke install result for packages restored after prerelease publishing.
/// </summary>
/// <param name="PackageVersion">Exact prerelease package version.</param>
/// <param name="Source">NuGet source URL.</param>
/// <param name="Entries">Per-package smoke install outcomes.</param>
internal sealed record PackageSmokeInstallReport(
    string PackageVersion,
    string Source,
    IReadOnlyList<PackageSmokeInstallReportEntry> Entries);

/// <summary>
/// Smoke install outcome for one public package.
/// </summary>
/// <param name="PackageId">NuGet package id.</param>
/// <param name="ProjectPath">Repository-relative project path.</param>
/// <param name="IsTool">Whether the package was installed as a .NET tool.</param>
/// <param name="Status">Smoke install status.</param>
/// <param name="ExitCode">Exit code from the final restore or tool install attempt.</param>
/// <param name="Output">Captured install output.</param>
internal sealed record PackageSmokeInstallReportEntry(
    string PackageId,
    string ProjectPath,
    bool IsTool,
    PackageSmokeInstallStatus Status,
    int ExitCode,
    string Output);

/// <summary>
/// Smoke install status values written to the smoke report.
/// </summary>
internal enum PackageSmokeInstallStatus
{
    Restored,
    Failed
}

/// <summary>
/// Computes package artifact hashes for manifest generation and publish verification.
/// </summary>
internal static class PackageHash
{
    internal static async Task<string> ComputeSha512Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA512.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static string ComputeSha512(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA512.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// JSON serializer options shared by artifact manifest readers and writers.
/// </summary>
internal static class PackageArtifactJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };
}

/// <summary>
/// Environment variables that make release automation quieter and deterministic.
/// </summary>
internal static class ReleaseEnvironment
{
    internal static readonly IReadOnlyDictionary<string, string?> Default = new Dictionary<string, string?>
    {
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
    };
}

/// <summary>
/// Formats package publish decisions for machine-readable release artifacts.
/// </summary>
internal static class PackagePublishDecisionFormatter
{
    internal static string Format(PackagePublishDecision decision)
    {
        return decision switch
        {
            PackagePublishDecision.Publish => "publish",
            PackagePublishDecision.SupportPublish => "support_publish",
            PackagePublishDecision.DoNotPublish => "do_not_publish",
            _ => decision.ToString()
        };
    }
}

/// <summary>
/// Verifies that an artifact manifest still matches the checked-in package plan and validated artifacts.
/// </summary>
internal static class PackageArtifactManifestPlanValidator
{
    internal static IReadOnlyList<PlannedPackageArtifact> Validate(
        PackagePublishPlan plan,
        PackageArtifactManifest manifest,
        string artifactsInputPath)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactsInputPath);

        if (plan.Entries.Count != manifest.Entries.Count)
        {
            throw new PackageIndexException(
                $"Package artifact manifest contains {manifest.Entries.Count} entries, but the package plan contains {plan.Entries.Count} entries.");
        }

        var plannedEntries = new List<PlannedPackageArtifact>(plan.Entries.Count);
        for (var index = 0; index < plan.Entries.Count; index++)
        {
            var planEntry = plan.Entries[index];
            var manifestEntry = manifest.Entries[index];
            var expectedDecision = PackagePublishDecisionFormatter.Format(planEntry.Decision);
            if (!string.Equals(planEntry.PackageId, manifestEntry.PackageId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(planEntry.ProjectPath, manifestEntry.ProjectPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(expectedDecision, manifestEntry.Decision, StringComparison.OrdinalIgnoreCase)
                || planEntry.IsTool != manifestEntry.IsTool)
            {
                throw new PackageIndexException(
                    $"Package artifact manifest entry {index + 1} does not match package plan entry '{planEntry.PackageId}'.");
            }

            var artifactPath = Path.Combine(artifactsInputPath, manifestEntry.ArtifactFileName);
            if (!File.Exists(artifactPath))
            {
                throw new PackageIndexException($"Package artifact '{manifestEntry.ArtifactFileName}' for '{manifestEntry.PackageId}' does not exist.");
            }

            var actualHash = PackageHash.ComputeSha512(artifactPath);
            if (!string.Equals(actualHash, manifestEntry.Sha512, StringComparison.OrdinalIgnoreCase))
            {
                throw new PackageIndexException($"Package artifact '{manifestEntry.ArtifactFileName}' for '{manifestEntry.PackageId}' does not match the manifest SHA-512 hash.");
            }

            plannedEntries.Add(new PlannedPackageArtifact(manifestEntry, artifactPath));
        }

        return plannedEntries;
    }
}

/// <summary>
/// Package artifact that has been matched to both the checked-in plan and artifact manifest.
/// </summary>
/// <param name="ManifestEntry">Manifest entry for the artifact.</param>
/// <param name="ArtifactPath">Resolved artifact path on disk.</param>
internal sealed record PlannedPackageArtifact(
    PackageArtifactManifestEntry ManifestEntry,
    string ArtifactPath);
