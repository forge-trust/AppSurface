using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>
/// Runs a disposable, package-only proof for the explicitly evaluated Python parser candidate.
/// </summary>
/// <remarks>
/// The workflow never adds the candidate to an AppSurface project. Instead, it copies the supplied archive to a
/// generated local feed, restores a temporary console application from that feed only, and runs the documented
/// <c>TreeSitter.Language("Python")</c> initialization in a separate process. A non-zero, timed-out, or malformed
/// proof result is evidence for candidate rejection rather than an in-process failure that a Docs harvester must catch.
/// </remarks>
internal sealed class PythonParserCandidateProofWorkflow
{
    internal const string CandidatePackageId = "TreeSitter.DotNet";
    internal const string CandidatePackageVersion = "1.3.0";
    internal const long MaximumCompressedPackageBytes = 5L * 1024 * 1024;
    internal const int DotNetCommandTimeoutMilliseconds = 180_000;

    private const string EmptyMsBuildProject = "<Project />\n";

    private static readonly IReadOnlyList<string> RequiredRuntimeIdentifiers =
    [
        "linux-arm",
        "linux-arm64",
        "linux-x64",
        "linux-x86",
        "osx-arm64",
        "osx-x64",
        "win-arm64",
        "win-x64",
        "win-x86"
    ];

    private readonly IExternalCommandRunner _commandRunner;

    /// <summary>
    /// Initializes a package-only candidate proof workflow.
    /// </summary>
    /// <param name="commandRunner">External command runner used for generated consumer restore and execution.</param>
    internal PythonParserCandidateProofWorkflow(IExternalCommandRunner commandRunner)
    {
        ArgumentNullException.ThrowIfNull(commandRunner);
        _commandRunner = commandRunner;
    }

    /// <summary>
    /// Inspects the supplied archive and runs the fixed Python parser smoke corpus in an isolated child process.
    /// </summary>
    /// <param name="request">Repository, candidate archive, workspace, report, and size-budget inputs.</param>
    /// <param name="cancellationToken">Cancellation token propagated to file and process operations.</param>
    /// <returns>A machine-readable record of archive, native-asset, and child-process evidence.</returns>
    /// <exception cref="PackageIndexException">Thrown when the request or supplied archive cannot be inspected safely.</exception>
    internal async Task<PythonParserCandidateProofReport> RunAsync(
        PythonParserCandidateProofRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var archive = await InspectArchiveAsync(request, cancellationToken);
        var rejectionReasons = new List<string>();
        if (archive.CompressedArchiveBytes > request.MaximumCompressedPackageBytes)
        {
            rejectionReasons.Add("compressed_archive_exceeds_budget");
        }

        if (!string.Equals(archive.Metadata.PackageId, CandidatePackageId, StringComparison.Ordinal))
        {
            rejectionReasons.Add("package_id_mismatch");
        }

        if (!string.Equals(archive.Metadata.PackageVersion, CandidatePackageVersion, StringComparison.Ordinal))
        {
            rejectionReasons.Add("package_version_mismatch");
        }

        if (archive.MissingRuntimeIdentifiers.Count != 0)
        {
            rejectionReasons.Add("native_runtime_identifier_missing");
        }

        if (archive.UnexpectedRuntimeIdentifiers.Count != 0)
        {
            rejectionReasons.Add("native_runtime_identifier_unexpected");
        }

        if (!archive.HasCompleteProvenanceMetadata)
        {
            rejectionReasons.Add("provenance_metadata_incomplete");
        }

        PythonParserCandidateProofCommandResult? restore = null;
        PythonParserCandidateProofCommandResult? smoke = null;
        try
        {
            var proofWorkspaceParent = Path.GetDirectoryName(Path.GetFullPath(request.WorkDirectory));
            if (string.IsNullOrWhiteSpace(proofWorkspaceParent))
            {
                throw new PackageIndexException("Python parser candidate proof workspace must have a parent directory.");
            }

            PackageProofWorkDirectory.Prepare(request.WorkDirectory, request.RepositoryRoot, proofWorkspaceParent);
            var feedDirectory = Path.Join(request.WorkDirectory, "local-feed");
            var consumerDirectory = Path.Join(request.WorkDirectory, "consumer");
            var packageCacheDirectory = Path.Join(request.WorkDirectory, "packages");
            var dotNetHomeDirectory = Path.Join(request.WorkDirectory, "dotnet-home");
            Directory.CreateDirectory(feedDirectory);
            Directory.CreateDirectory(consumerDirectory);
            Directory.CreateDirectory(packageCacheDirectory);
            Directory.CreateDirectory(dotNetHomeDirectory);

            File.Copy(
                request.CandidatePackagePath,
                Path.Join(
                    feedDirectory,
                    $"{CandidatePackageId.ToLowerInvariant()}.{CandidatePackageVersion}.nupkg"),
                overwrite: true);
            await File.WriteAllTextAsync(Path.Join(consumerDirectory, "Directory.Build.props"), EmptyMsBuildProject, cancellationToken);
            await File.WriteAllTextAsync(Path.Join(consumerDirectory, "Directory.Build.targets"), EmptyMsBuildProject, cancellationToken);
            await File.WriteAllTextAsync(Path.Join(consumerDirectory, "NuGet.config"), RenderNuGetConfig(feedDirectory), cancellationToken);
            await File.WriteAllTextAsync(Path.Join(consumerDirectory, "PythonParserCandidateSmoke.csproj"), RenderConsumerProject(), cancellationToken);
            await File.WriteAllTextAsync(Path.Join(consumerDirectory, "Program.cs"), RenderSmokeProgram(), cancellationToken);

            var environment = new Dictionary<string, string?>(ReleaseEnvironment.Default)
            {
                ["NUGET_PACKAGES"] = packageCacheDirectory,
                ["DOTNET_CLI_HOME"] = dotNetHomeDirectory
            };
            restore = await RunCommandAsync(
                "dotnet restore Python parser candidate smoke host",
                "restoring the generated candidate-only smoke host",
                ["restore", "PythonParserCandidateSmoke.csproj", "--configfile", "NuGet.config", "--disable-parallel"],
                consumerDirectory,
                environment,
                cancellationToken);
            if (restore.ExitCode != 0)
            {
                rejectionReasons.Add("consumer_restore_failed");
            }
            else
            {
                smoke = await RunCommandAsync(
                    "dotnet run Python parser candidate smoke host",
                    "initializing the native Python grammar and parsing the fixed corpus",
                    ["run", "--project", "PythonParserCandidateSmoke.csproj", "--no-restore"],
                    consumerDirectory,
                    environment,
                    cancellationToken);
                if (smoke.ExitCode != 0)
                {
                    rejectionReasons.Add("consumer_smoke_failed");
                }
                else if (ExtractRuntimeIdentifier(smoke.StandardOutput) is null)
                {
                    rejectionReasons.Add("consumer_smoke_runtime_identifier_missing");
                }
            }
        }
        catch (PackageIndexException ex)
        {
            rejectionReasons.Add("proof_workspace_failed");
            smoke ??= PythonParserCandidateProofCommandResult.NotRun(ex.Message);
        }
        catch (IOException ex)
        {
            rejectionReasons.Add("proof_workspace_failed");
            smoke ??= PythonParserCandidateProofCommandResult.NotRun(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            rejectionReasons.Add("proof_workspace_failed");
            smoke ??= PythonParserCandidateProofCommandResult.NotRun(ex.Message);
        }

        var report = new PythonParserCandidateProofReport(
            archive,
            restore,
            smoke,
            rejectionReasons.Distinct(StringComparer.Ordinal).OrderBy(reason => reason, StringComparer.Ordinal).ToArray());
        await WriteReportAsync(report, request.ReportPath, cancellationToken);
        return report;
    }

    /// <summary>
    /// Renders the generated consumer project that references the exact candidate package identity.
    /// </summary>
    /// <returns>Minimal project XML for the disposable smoke host.</returns>
    internal static string RenderConsumerProject() =>
        $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="{{CandidatePackageId}}" Version="{{CandidatePackageVersion}}" />
          </ItemGroup>
        </Project>
        """;

    /// <summary>
    /// Renders the fixed valid, malformed, and large-input parser corpus.
    /// </summary>
    /// <returns>Source for the disposable child process.</returns>
    internal static string RenderSmokeProgram() =>
        """
        using System.Runtime.InteropServices;
        using System.Text;
        using TreeSitter;

        using var language = new Language("Python");
        using var parser = new Parser(language);
        using var validTree = parser.Parse("def answer():\n    \"\"\"Returns the answer.\"\"\"\n    return 42\n");
        using var malformedTree = parser.Parse("def broken(:\n    return\n");
        var largeSource = string.Concat(Enumerable.Repeat("value = 1\n", 100_000));
        using var largeTree = parser.Parse(largeSource);

        if (validTree is null || malformedTree is null || largeTree is null)
        {
            throw new InvalidOperationException("Tree-sitter returned no parse tree for a fixed smoke input.");
        }

        if (validTree.RootNode.HasError)
        {
            throw new InvalidOperationException("The valid fixed smoke input produced a Tree-sitter error node.");
        }

        if (!malformedTree.RootNode.HasError)
        {
            throw new InvalidOperationException("The malformed fixed smoke input did not produce a Tree-sitter error node.");
        }

        var largeSourceBytes = Encoding.UTF8.GetByteCount(largeSource);
        if (largeTree.RootNode.EndIndex != largeSourceBytes)
        {
            throw new InvalidOperationException($"The large fixed smoke input was not fully consumed: expected {largeSourceBytes} bytes but parsed {largeTree.RootNode.EndIndex}.");
        }

        Console.WriteLine($"RID={RuntimeInformation.RuntimeIdentifier}");
        Console.WriteLine($"VALID={validTree.RootNode.Type}");
        Console.WriteLine($"VALID_HAS_ERROR={validTree.RootNode.HasError}");
        Console.WriteLine($"MALFORMED={malformedTree.RootNode.Type}");
        Console.WriteLine($"MALFORMED_HAS_ERROR={malformedTree.RootNode.HasError}");
        Console.WriteLine($"LARGE_SOURCE_BYTES={largeSourceBytes}");
        Console.WriteLine($"LARGE_END_INDEX={largeTree.RootNode.EndIndex}");
        Console.WriteLine($"LARGE={largeTree.RootNode.Type}");
        """;

    private static async Task<PythonParserCandidateArchiveEvidence> InspectArchiveAsync(
        PythonParserCandidateProofRequest request,
        CancellationToken cancellationToken)
    {
        await using var packageStream = File.OpenRead(request.CandidatePackagePath);
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(packageStream, cancellationToken));
        var fileInfo = new FileInfo(request.CandidatePackagePath);
        using var archive = ZipFile.OpenRead(request.CandidatePackagePath);
        var nativeAssets = archive.Entries
            .Select(entry => new { Entry = entry, RuntimeIdentifier = GetNativeRuntimeIdentifier(entry.FullName) })
            .Where(candidate => candidate.RuntimeIdentifier is not null)
            .GroupBy(candidate => candidate.RuntimeIdentifier!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new PythonParserNativeRuntimeEvidence(
                group.Key,
                group.Select(candidate => candidate.Entry.FullName).OrderBy(path => path, StringComparer.Ordinal).ToArray()))
            .ToArray();
        var actualRuntimeIdentifiers = nativeAssets.Select(asset => asset.RuntimeIdentifier).ToHashSet(StringComparer.Ordinal);
        var missingRuntimeIdentifiers = RequiredRuntimeIdentifiers.Where(rid => !actualRuntimeIdentifiers.Contains(rid)).ToArray();
        var unexpectedRuntimeIdentifiers = actualRuntimeIdentifiers.Where(rid => !RequiredRuntimeIdentifiers.Contains(rid, StringComparer.Ordinal)).OrderBy(rid => rid, StringComparer.Ordinal).ToArray();
        var metadata = ReadNuspecMetadata(archive);
        var licenseAndNoticePaths = archive.Entries
            .Select(entry => entry.FullName)
            .Where(IsLicenseOrNoticePath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        return new PythonParserCandidateArchiveEvidence(
            Path.GetFileName(request.CandidatePackagePath),
            sha256,
            fileInfo.Length,
            archive.Entries.Count,
            archive.Entries.Sum(entry => entry.Length),
            metadata,
            nativeAssets,
            missingRuntimeIdentifiers,
            unexpectedRuntimeIdentifiers,
            licenseAndNoticePaths,
            new PythonParserCandidateProvenanceReviewEvidence(
                "metadata_recorded_not_accepted",
                "Archive metadata and license/notice paths are recorded for a separate human redistribution review; this candidate proof does not approve a dependency."));
    }

    private static PythonParserCandidateNuspecMetadata ReadNuspecMetadata(ZipArchive archive)
    {
        var nuspecEntries = archive.Entries
            .Where(entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)
                && !entry.FullName.Contains('/', StringComparison.Ordinal))
            .ToArray();
        if (nuspecEntries.Length != 1)
        {
            throw new PackageIndexException($"Python parser candidate archive must contain exactly one root .nuspec file, found {nuspecEntries.Length}.");
        }

        try
        {
            using var stream = nuspecEntries[0].Open();
            var document = XDocument.Load(stream);
            var metadata = document.Root?.Elements().SingleOrDefault(element => element.Name.LocalName == "metadata");
            if (metadata is null)
            {
                throw new PackageIndexException("Python parser candidate .nuspec is missing metadata.");
            }

            var repository = metadata.Elements().SingleOrDefault(element => element.Name.LocalName == "repository");
            return new PythonParserCandidateNuspecMetadata(
                GetElementValue(metadata, "id"),
                GetElementValue(metadata, "version"),
                GetElementValue(metadata, "license"),
                repository?.Attribute("type")?.Value ?? string.Empty,
                repository?.Attribute("url")?.Value ?? string.Empty,
                repository?.Attribute("commit")?.Value ?? string.Empty);
        }
        catch (PackageIndexException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException)
        {
            throw new PackageIndexException($"Python parser candidate .nuspec could not be read: {ex.Message}");
        }
    }

    private static string GetElementValue(XElement element, string name) =>
        element.Elements().SingleOrDefault(child => child.Name.LocalName == name)?.Value.Trim() ?? string.Empty;

    private static string? GetNativeRuntimeIdentifier(string entryPath)
    {
        var segments = entryPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 4
            && string.Equals(segments[0], "runtimes", StringComparison.Ordinal)
            && string.Equals(segments[2], "native", StringComparison.Ordinal)
            ? segments[1]
            : null;
    }

    private static bool IsLicenseOrNoticePath(string entryPath)
    {
        var fileName = Path.GetFileName(entryPath);
        return fileName.StartsWith("license", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("notice", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("copying", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("third-party", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<PythonParserCandidateProofCommandResult> RunCommandAsync(
        string operationName,
        string timeoutDescription,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        var result = await _commandRunner.RunAsync(
            new ExternalCommandRequest(
                "dotnet",
                arguments,
                workingDirectory,
                operationName,
                timeoutDescription,
                DotNetCommandTimeoutMilliseconds,
                environment),
            cancellationToken);
        return new PythonParserCandidateProofCommandResult(
            operationName,
            string.Join(' ', arguments),
            result.ExitCode,
            NormalizeEvidenceText(result.StandardOutput, workingDirectory),
            NormalizeEvidenceText(result.StandardError, workingDirectory));
    }

    private static async Task WriteReportAsync(
        PythonParserCandidateProofReport report,
        string reportPath,
        CancellationToken cancellationToken)
    {
        var reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
        if (string.IsNullOrWhiteSpace(reportDirectory))
        {
            throw new PackageIndexException($"Python parser candidate report path '{reportPath}' does not have a directory.");
        }

        Directory.CreateDirectory(reportDirectory);
        await using var stream = File.Create(reportPath);
        await JsonSerializer.SerializeAsync(
            stream,
            report,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase },
            cancellationToken);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(Environment.NewLine), cancellationToken);
    }

    private static string RenderNuGetConfig(string localFeedDirectory)
    {
        var escapedDirectory = SecurityElement.Escape(Path.GetFullPath(localFeedDirectory)) ?? localFeedDirectory;
        return $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
            <add key="candidate" value="{{escapedDirectory}}" />
          </packageSources>
        </configuration>
        """;
    }

    private static string? ExtractRuntimeIdentifier(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("RID=", StringComparison.Ordinal))?[4..];

    private static string NormalizeEvidenceText(string value, string workingDirectory) =>
        Regex.Replace(
            value.Replace(Path.GetFullPath(workingDirectory), "<consumer>", StringComparison.Ordinal),
            @"\(in \d+ ms\)",
            "(in <duration>)");

    private static void ValidateRequest(PythonParserCandidateProofRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CandidatePackagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ReportPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.MaximumCompressedPackageBytes);
        if (!Directory.Exists(request.RepositoryRoot))
        {
            throw new PackageIndexException($"Python parser candidate proof repository root '{request.RepositoryRoot}' does not exist.");
        }

        if (!File.Exists(request.CandidatePackagePath))
        {
            throw new PackageIndexException($"Python parser candidate package '{request.CandidatePackagePath}' does not exist.");
        }

        if (!string.Equals(Path.GetExtension(request.CandidatePackagePath), ".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            throw new PackageIndexException("Python parser candidate package must be a .nupkg file.");
        }

        var candidatePackagePath = Path.GetFullPath(request.CandidatePackagePath);
        var workDirectory = Path.GetFullPath(request.WorkDirectory);
        var workDirectoryPrefix = workDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? workDirectory
            : workDirectory + Path.DirectorySeparatorChar;
        if (string.Equals(candidatePackagePath, workDirectory, PackageIndexGenerator.RepositoryPathComparison)
            || candidatePackagePath.StartsWith(workDirectoryPrefix, PackageIndexGenerator.RepositoryPathComparison))
        {
            throw new PackageIndexException("Python parser candidate package must not be contained by the disposable proof workspace.");
        }
    }
}

/// <summary>
/// Inputs for the isolated Python parser candidate proof.
/// </summary>
/// <param name="RepositoryRoot">Repository root protected from proof-workspace cleanup.</param>
/// <param name="CandidatePackagePath">Exact local candidate archive to inspect and place in the generated local feed.</param>
/// <param name="WorkDirectory">Disposable proof workspace.</param>
/// <param name="ReportPath">Machine-readable JSON evidence destination.</param>
/// <param name="MaximumCompressedPackageBytes">Maximum accepted compressed package delta.</param>
internal sealed record PythonParserCandidateProofRequest(
    string RepositoryRoot,
    string CandidatePackagePath,
    string WorkDirectory,
    string ReportPath,
    long MaximumCompressedPackageBytes = PythonParserCandidateProofWorkflow.MaximumCompressedPackageBytes);

/// <summary>
/// Archive, metadata, native-runtime, and process evidence for one parser candidate.
/// </summary>
/// <param name="Archive">Measured candidate archive and declared metadata.</param>
/// <param name="Restore">Generated consumer restore result, if the workspace was prepared.</param>
/// <param name="Smoke">Generated consumer process result, if restore succeeded.</param>
/// <param name="RejectionReasons">Deterministic reasons that prevent the candidate from becoming an AppSurface dependency.</param>
internal sealed record PythonParserCandidateProofReport(
    PythonParserCandidateArchiveEvidence Archive,
    PythonParserCandidateProofCommandResult? Restore,
    PythonParserCandidateProofCommandResult? Smoke,
    IReadOnlyList<string> RejectionReasons)
{
    /// <summary>
    /// Gets whether the automated archive and local smoke checks have no rejection reason.
    /// A true value only permits further review; it is not legal approval or multi-RID acceptance.
    /// </summary>
    public bool IsEligibleForFurtherReview => RejectionReasons.Count == 0;
}

/// <summary>
/// Immutable inspection result for the supplied candidate archive.
/// </summary>
internal sealed record PythonParserCandidateArchiveEvidence(
    string PackageFileName,
    string Sha256,
    long CompressedArchiveBytes,
    int ArchiveEntryCount,
    long UncompressedArchiveBytes,
    PythonParserCandidateNuspecMetadata Metadata,
    IReadOnlyList<PythonParserNativeRuntimeEvidence> NativeRuntimeAssets,
    IReadOnlyList<string> MissingRuntimeIdentifiers,
    IReadOnlyList<string> UnexpectedRuntimeIdentifiers,
    IReadOnlyList<string> LicenseAndNoticePaths,
    PythonParserCandidateProvenanceReviewEvidence ProvenanceReview)
{
    /// <summary>
    /// Gets whether the archive declares the minimum license and source provenance fields for later human review.
    /// </summary>
    public bool HasCompleteProvenanceMetadata =>
        !string.IsNullOrWhiteSpace(Metadata.LicenseExpression)
        && !string.IsNullOrWhiteSpace(Metadata.RepositoryType)
        && !string.IsNullOrWhiteSpace(Metadata.RepositoryUrl)
        && !string.IsNullOrWhiteSpace(Metadata.RepositoryCommit);
}

/// <summary>
/// NuGet metadata retained as provenance evidence without treating it as a legal approval.
/// </summary>
internal sealed record PythonParserCandidateNuspecMetadata(
    string PackageId,
    string PackageVersion,
    string LicenseExpression,
    string RepositoryType,
    string RepositoryUrl,
    string RepositoryCommit);

/// <summary>
/// Explicit boundary between machine-recorded provenance facts and a human redistribution approval.
/// </summary>
/// <param name="Status">Deterministic review state for this candidate proof.</param>
/// <param name="Explanation">Why the candidate proof cannot constitute legal or supply-chain approval.</param>
internal sealed record PythonParserCandidateProvenanceReviewEvidence(string Status, string Explanation);

/// <summary>
/// Full native asset inventory declared for one runtime identifier.
/// </summary>
/// <param name="RuntimeIdentifier">Runtime identifier containing the native payload.</param>
/// <param name="NativeAssetPaths">Archive paths for every native file under the runtime identifier.</param>
internal sealed record PythonParserNativeRuntimeEvidence(
    string RuntimeIdentifier,
    IReadOnlyList<string> NativeAssetPaths)
{
    /// <summary>
    /// Gets the number of enumerated native asset paths.
    /// </summary>
    public int NativeFileCount => NativeAssetPaths.Count;
}

/// <summary>
/// Captured generated-consumer command result.
/// </summary>
internal sealed record PythonParserCandidateProofCommandResult(
    string Operation,
    string Command,
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    /// <summary>
    /// Creates evidence for a command that was intentionally not run after an earlier failure.
    /// </summary>
    /// <param name="reason">Safe reason that prevented command execution.</param>
    /// <returns>Non-executed command evidence.</returns>
    internal static PythonParserCandidateProofCommandResult NotRun(string reason) =>
        new("not-run", string.Empty, -1, string.Empty, reason);
}
