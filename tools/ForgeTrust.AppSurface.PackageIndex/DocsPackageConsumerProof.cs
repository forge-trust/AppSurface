using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>
/// Runs the pre-publish restore proof for the packed AppSurface Docs package.
/// </summary>
internal interface IDocsPackageConsumerProofWorkflow
{
    /// <summary>
    /// Restores the validated Docs package from local artifacts in a clean consumer project and verifies its resolved graph.
    /// </summary>
    /// <param name="request">Proof paths, package version, and third-party package source.</param>
    /// <param name="validationReport">Validated artifacts produced by the package artifact workflow.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A structured report containing command and graph-verification evidence.</returns>
    Task<DocsPackageConsumerProofReport> RunAsync(
        DocsPackageConsumerProofRequest request,
        PackageArtifactValidationReport validationReport,
        CancellationToken cancellationToken);
}

/// <summary>
/// Proves that a clean package consumer resolves the packed Docs artifact and the reviewed stable parser and sanitizer graph.
/// </summary>
/// <remarks>
/// <para>
/// The generated consumer is intentionally independent of the repository Docs consumer fixture, which uses project references.
/// It has its own package lock file and configuration with a local-only mapping for first-party packages. The public source
/// is restricted to the exact non-first-party package ids represented by the committed locks for the validated Docs
/// first-party closure, so a newly published or unintended first-party package cannot satisfy the restore.
/// </para>
/// <para>
/// The proof does not build an application: restoring and inspecting <c>project.assets.json</c> is the relevant package
/// contract. It confirms that Docs, AngleSharp, AngleSharp.Css, and HtmlSanitizer resolve as packages at their exact
/// expected versions.
/// </para>
/// </remarks>
internal sealed class DocsPackageConsumerProofWorkflow : IDocsPackageConsumerProofWorkflow
{
    internal const string DocsPackageId = StableDocsDependencyContract.DocsPackageId;
    internal const int DotNetCommandTimeoutMilliseconds = 180_000;

    private static readonly IReadOnlyList<ExpectedPackage> ExpectedPackages =
    [
        new(DocsPackageId, IsVersionFromRequest: true),
        .. StableDocsDependencyContract.Dependencies.Select(dependency => new ExpectedPackage(dependency.Id, dependency.Version))
    ];

    private readonly IExternalCommandRunner _commandRunner;

    /// <summary>
    /// Initializes a new Docs package consumer proof workflow.
    /// </summary>
    /// <param name="commandRunner">External command runner used for isolated consumer restores.</param>
    internal DocsPackageConsumerProofWorkflow(IExternalCommandRunner commandRunner)
    {
        _commandRunner = commandRunner;
    }

    /// <inheritdoc />
    public async Task<DocsPackageConsumerProofReport> RunAsync(
        DocsPackageConsumerProofRequest request,
        PackageArtifactValidationReport validationReport,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(validationReport);

        DocsPackageConsumerProofSelectedArtifact selectedArtifact;
        try
        {
            selectedArtifact = SelectDocsPackage(validationReport, request.PackageVersion);
        }
        catch (PackageIndexException ex)
        {
            return DocsPackageConsumerProofReport.Failed(request.PackageVersion, request.WorkDirectory, request.Source, ex.Message);
        }

        try
        {
            PackageProofWorkDirectory.Prepare(
                request.WorkDirectory,
                request.RepositoryRoot,
                request.ArtifactsDirectory);
        }
        catch (PackageIndexException ex)
        {
            return DocsPackageConsumerProofReport.Failed(
                request.PackageVersion,
                request.WorkDirectory,
                request.Source,
                ex.Message,
                selectedArtifact);
        }

        var consumerDirectory = Path.Join(request.WorkDirectory, "consumer");
        var logsDirectory = Path.Join(request.WorkDirectory, "logs");
        var nuGetConfigPath = Path.Join(consumerDirectory, "NuGet.config");
        var consumerProjectPath = Path.Join(consumerDirectory, "Docs.ConsumerProof.csproj");
        var consumerLockFilePath = Path.Join(consumerDirectory, "packages.lock.json");
        var assetsFilePath = Path.Join(consumerDirectory, "obj", "project.assets.json");
        var temporaryCacheDirectory = Path.Join(Path.GetTempPath(), "appsurface-docs-consumer-proof", Guid.NewGuid().ToString("N"));
        var sharedPackagesPath = Path.Join(temporaryCacheDirectory, "packages");
        var dotNetHomePath = Path.Join(temporaryCacheDirectory, "dotnet-home");
        Directory.CreateDirectory(consumerDirectory);
        Directory.CreateDirectory(logsDirectory);
        Directory.CreateDirectory(sharedPackagesPath);
        Directory.CreateDirectory(dotNetHomePath);

        try
        {
            IReadOnlyList<string> thirdPartyPackageIds;
            try
            {
                thirdPartyPackageIds = ReadPackedDocsThirdPartyPackageIds(
                    request.RepositoryRoot,
                    validationReport);
            }
            catch (PackageIndexException ex)
            {
                return DocsPackageConsumerProofReport.Failed(
                    request.PackageVersion,
                    request.WorkDirectory,
                    request.Source,
                    ex.Message,
                    selectedArtifact);
            }

            await File.WriteAllTextAsync(
                Path.Join(consumerDirectory, "Directory.Packages.props"),
                RenderConsumerDirectoryPackagesProps(),
                cancellationToken);
            await File.WriteAllTextAsync(
                nuGetConfigPath,
                RenderMappedNuGetConfig(request.ArtifactsDirectory, request.Source, thirdPartyPackageIds),
                cancellationToken);
            await File.WriteAllTextAsync(
                consumerProjectPath,
                RenderConsumerProject(request.PackageVersion),
                cancellationToken);

            var context = new DocsPackageConsumerProofContext(
                request,
                selectedArtifact,
                consumerDirectory,
                logsDirectory,
                nuGetConfigPath,
                consumerProjectPath,
                consumerLockFilePath,
                assetsFilePath,
                sharedPackagesPath,
                dotNetHomePath,
                thirdPartyPackageIds,
                _commandRunner);
            var commands = new List<DocsPackageConsumerProofCommandResult>();

            if (!await RunRequiredAsync(
                context,
                commands,
                ["restore", consumerProjectPath, "--configfile", nuGetConfigPath, "--force-evaluate"],
                "dotnet restore consumer",
                "creating the consumer lock file",
                cancellationToken))
            {
                return BuildReport(context, commands, null, commands[^1].FailureReason);
            }

            if (!await RunRequiredAsync(
                context,
                commands,
                ["restore", consumerProjectPath, "--configfile", nuGetConfigPath, "--locked-mode"],
                "dotnet restore consumer --locked-mode",
                "verifying the consumer lock file",
                cancellationToken))
            {
                return BuildReport(context, commands, null, commands[^1].FailureReason);
            }

            DocsPackageConsumerGraphVerification verification;
            try
            {
                verification = VerifyConsumerGraph(assetsFilePath, consumerLockFilePath, request.PackageVersion);
            }
            catch (PackageIndexException ex)
            {
                return BuildReport(context, commands, null, ex.Message);
            }

            return BuildReport(context, commands, verification, string.Empty);
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryCacheDirectory);
        }
    }

    /// <summary>
    /// Selects the validated Docs artifact that the consumer proof restores.
    /// </summary>
    /// <param name="report">Validation report produced from the just-packed artifact directory.</param>
    /// <param name="packageVersion">Exact package version expected in the selected artifact file name.</param>
    /// <returns>The selected artifact and its SHA-512 hash.</returns>
    /// <exception cref="PackageIndexException">
    /// Thrown when the report does not contain exactly one existing Docs package artifact matching <paramref name="packageVersion" />.
    /// </exception>
    internal static DocsPackageConsumerProofSelectedArtifact SelectDocsPackage(
        PackageArtifactValidationReport report,
        string packageVersion)
    {
        ArgumentNullException.ThrowIfNull(report);
        var matches = report.Entries
            .Where(entry => string.Equals(entry.PackageId, DocsPackageId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new PackageIndexException(
                $"Docs package consumer proof requires exactly one validated package row for '{DocsPackageId}', found {matches.Length}.");
        }

        var entry = matches[0];
        if (string.IsNullOrWhiteSpace(entry.ArtifactPath) || !File.Exists(entry.ArtifactPath))
        {
            throw new PackageIndexException($"Docs package consumer proof requires an existing validated artifact for '{DocsPackageId}'.");
        }

        var expectedFileName = $"{DocsPackageId}.{packageVersion}.nupkg";
        if (!string.Equals(Path.GetFileName(entry.ArtifactPath), expectedFileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new PackageIndexException(
                $"Docs package consumer proof selected artifact '{entry.ArtifactPath}' does not match package version '{packageVersion}'.");
        }

        return new DocsPackageConsumerProofSelectedArtifact(
            entry.PackageId,
            entry.ProjectPath,
            entry.ArtifactPath,
            Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(entry.ArtifactPath))));
    }

    /// <summary>
    /// Renders the isolated consumer's central package-management boundary.
    /// </summary>
    /// <returns>Props that disable inherited central package management and enable the consumer lock file.</returns>
    internal static string RenderConsumerDirectoryPackagesProps() =>
        """
        <Project>
          <PropertyGroup>
            <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
            <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
          </PropertyGroup>
        </Project>
        """;

    /// <summary>
    /// Renders the clean package consumer project.
    /// </summary>
    /// <param name="packageVersion">Exact Docs package version to restore.</param>
    /// <returns>Consumer project XML.</returns>
    internal static string RenderConsumerProject(string packageVersion)
    {
        var escapedVersion = SecurityElement.Escape(packageVersion) ?? packageVersion;
        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{{DocsPackageId}}" Version="{{escapedVersion}}" />
              </ItemGroup>
            </Project>
            """;
    }

    /// <summary>
    /// Renders source mapping that permits first-party packages only from local artifacts and enumerates allowed public packages.
    /// </summary>
    /// <param name="localSource">Directory containing freshly packed first-party artifacts.</param>
    /// <param name="nugetOrgSource">Source used for reviewed third-party package identities.</param>
    /// <param name="thirdPartyPackageIds">Exact third-party package ids allowed from the public source.</param>
    /// <returns>NuGet configuration XML with package-source mapping.</returns>
    /// <exception cref="ArgumentException">Thrown when no third-party package ids are supplied.</exception>
    internal static string RenderMappedNuGetConfig(
        string localSource,
        string nugetOrgSource,
        IEnumerable<string> thirdPartyPackageIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(nugetOrgSource);
        ArgumentNullException.ThrowIfNull(thirdPartyPackageIds);
        var publicPackageIds = thirdPartyPackageIds
            .Where(packageId => !string.IsNullOrWhiteSpace(packageId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (publicPackageIds.Length == 0)
        {
            throw new ArgumentException("At least one reviewed third-party package id is required.", nameof(thirdPartyPackageIds));
        }

        var escapedLocalSource = SecurityElement.Escape(Path.GetFullPath(localSource)) ?? localSource;
        var escapedNuGetOrgSource = SecurityElement.Escape(nugetOrgSource) ?? nugetOrgSource;
        var mappings = string.Join(
            Environment.NewLine,
            publicPackageIds.Select(packageId =>
                $"      <package pattern=\"{SecurityElement.Escape(packageId)}\" />"));
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
                  <package pattern="ForgeTrust.*" />
                </packageSource>
                <packageSource key="nuget-org">
            {{mappings}}
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """;
    }

    /// <summary>
    /// Reads the public package identities for the validated, packed Docs first-party closure.
    /// </summary>
    /// <param name="repositoryRoot">Repository root that contains the committed lock files for packed first-party projects.</param>
    /// <param name="validationReport">Validated package report that establishes the first-party package closure.</param>
    /// <returns>Sorted, deduplicated third-party package ids.</returns>
    /// <exception cref="PackageIndexException">
    /// Thrown when the report does not contain the complete Docs closure, or a required committed lock file is absent or malformed.
    /// </exception>
    /// <remarks>
    /// The traversal follows the validator-confirmed first-party dependency graph from Docs and scans each participating
    /// project's lock file for package entries. This captures public dependencies introduced by packed first-party
    /// packages (for example <c>Microsoft.Extensions.*</c>) that do not appear in the Docs project's source-only lock graph.
    /// </remarks>
    internal static IReadOnlyList<string> ReadPackedDocsThirdPartyPackageIds(
        string repositoryRoot,
        PackageArtifactValidationReport validationReport)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(validationReport);
        var entriesByPackageId = validationReport.Entries.ToDictionary(
            entry => entry.PackageId,
            StringComparer.OrdinalIgnoreCase);
        var pendingPackageIds = new Queue<string>();
        var visitedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var thirdPartyPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pendingPackageIds.Enqueue(DocsPackageId);

        while (pendingPackageIds.TryDequeue(out var packageId))
        {
            if (!visitedPackageIds.Add(packageId))
            {
                continue;
            }

            if (!entriesByPackageId.TryGetValue(packageId, out var entry))
            {
                throw new PackageIndexException(
                    $"Docs package consumer proof requires validated first-party dependency '{packageId}' in the packed Docs closure.");
            }

            var projectDirectory = Path.GetDirectoryName(entry.ProjectPath);
            if (string.IsNullOrWhiteSpace(projectDirectory))
            {
                throw new PackageIndexException(
                    $"Docs package consumer proof cannot locate the project directory for validated package '{packageId}'.");
            }

            var lockFilePath = Path.Join(repositoryRoot, projectDirectory, "packages.lock.json");
            foreach (var thirdPartyPackageId in ReadThirdPartyPackageIdsFromLockFile(lockFilePath))
            {
                thirdPartyPackageIds.Add(thirdPartyPackageId);
            }

            foreach (var firstPartyDependencyId in entry.ExpectedDependencyPackageIds
                         .Where(dependencyId => dependencyId.StartsWith("ForgeTrust.", StringComparison.OrdinalIgnoreCase)))
            {
                pendingPackageIds.Enqueue(firstPartyDependencyId);
            }
        }

        return thirdPartyPackageIds.OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Reads package (not project) identities from one committed project lock file.
    /// </summary>
    /// <param name="lockFilePath">Committed lock file path.</param>
    /// <returns>Third-party package identities represented by lock entries.</returns>
    /// <exception cref="PackageIndexException">Thrown when the lock file is absent or malformed.</exception>
    internal static IReadOnlyList<string> ReadThirdPartyPackageIdsFromLockFile(string lockFilePath)
    {
        if (!File.Exists(lockFilePath))
        {
            throw new PackageIndexException($"Docs package consumer proof requires committed lock file '{lockFilePath}'.");
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(lockFilePath));
            if (!document.RootElement.TryGetProperty("dependencies", out var frameworks)
                || frameworks.ValueKind != JsonValueKind.Object)
            {
                throw new PackageIndexException($"Docs package consumer proof lock file '{lockFilePath}' has no dependency graph.");
            }

            return frameworks
                .EnumerateObject()
                .SelectMany(framework => framework.Value.ValueKind == JsonValueKind.Object
                    ? framework.Value.EnumerateObject().ToArray()
                    : [])
                .Where(dependency => !dependency.Name.StartsWith("ForgeTrust.", StringComparison.OrdinalIgnoreCase)
                    && IsPackageLockEntry(dependency.Value))
                .Select(dependency => dependency.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException ex)
        {
            throw new PackageIndexException($"Docs package consumer proof could not parse lock file '{lockFilePath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Inspects a generated consumer's assets and lock files for the packed Docs package and stable dependency graph.
    /// </summary>
    /// <param name="assetsFilePath">Generated <c>project.assets.json</c> path.</param>
    /// <param name="lockFilePath">Generated consumer lock file path.</param>
    /// <param name="packageVersion">Expected Docs package version.</param>
    /// <returns>Verified resolved package identities and paths.</returns>
    /// <exception cref="PackageIndexException">Thrown when either generated input is absent, malformed, or resolves a wrong package identity.</exception>
    internal static DocsPackageConsumerGraphVerification VerifyConsumerGraph(
        string assetsFilePath,
        string lockFilePath,
        string packageVersion)
    {
        if (!File.Exists(assetsFilePath))
        {
            throw new PackageIndexException($"Docs package consumer proof expected assets file '{assetsFilePath}'.");
        }

        if (!File.Exists(lockFilePath))
        {
            throw new PackageIndexException($"Docs package consumer proof expected lock file '{lockFilePath}'.");
        }

        var expectedPackages = ExpectedPackages
            .Select(expected => expected with { Version = expected.IsVersionFromRequest ? packageVersion : expected.Version })
            .ToArray();
        VerifyLockFile(lockFilePath, packageVersion, expectedPackages);
        VerifyAssetsFile(assetsFilePath, packageVersion, expectedPackages);
        return new DocsPackageConsumerGraphVerification(
            assetsFilePath,
            lockFilePath,
            expectedPackages.Select(expected => new DocsPackageConsumerProofResolvedPackage(expected.Id, expected.Version!)).ToArray());
    }

    private static async Task<bool> RunRequiredAsync(
        DocsPackageConsumerProofContext context,
        List<DocsPackageConsumerProofCommandResult> commands,
        IReadOnlyList<string> arguments,
        string operationName,
        string timeoutDescription,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var externalResult = await context.CommandRunner.RunAsync(
            new ExternalCommandRequest(
                "dotnet",
                arguments,
                context.ConsumerDirectory,
                operationName,
                timeoutDescription,
                DotNetCommandTimeoutMilliseconds,
                CreateProofEnvironment(context.DotNetHomePath, context.SharedPackagesPath)),
            cancellationToken);
        stopwatch.Stop();

        var logPrefix = $"{commands.Count + 1:000}-{SanitizeFileName(operationName)}";
        var stdoutPath = Path.Join(context.LogsDirectory, $"{logPrefix}.stdout.log");
        var stderrPath = Path.Join(context.LogsDirectory, $"{logPrefix}.stderr.log");
        await File.WriteAllTextAsync(stdoutPath, externalResult.StandardOutput, cancellationToken);
        await File.WriteAllTextAsync(stderrPath, externalResult.StandardError, cancellationToken);
        var succeeded = externalResult.ExitCode == 0;
        commands.Add(new DocsPackageConsumerProofCommandResult(
            operationName,
            "dotnet",
            arguments,
            context.ConsumerDirectory,
            externalResult.ExitCode,
            succeeded,
            succeeded ? string.Empty : $"Expected exit code 0, got {externalResult.ExitCode}.",
            stopwatch.Elapsed,
            stdoutPath,
            stderrPath,
            externalResult.StandardOutput,
            externalResult.StandardError));
        return succeeded;
    }

    private static DocsPackageConsumerProofReport BuildReport(
        DocsPackageConsumerProofContext context,
        IReadOnlyList<DocsPackageConsumerProofCommandResult> commands,
        DocsPackageConsumerGraphVerification? graphVerification,
        string firstFailure)
    {
        var commandFailure = commands.FirstOrDefault(command => !command.Succeeded)?.FailureReason;
        return new DocsPackageConsumerProofReport(
            context.Request.PackageVersion,
            context.Request.WorkDirectory,
            context.Request.Source,
            context.SelectedArtifact,
            context.NuGetConfigPath,
            context.ConsumerProjectPath,
            context.ConsumerLockFilePath,
            context.AssetsFilePath,
            context.ThirdPartyPackageIds,
            context.LogsDirectory,
            commands,
            graphVerification,
            string.IsNullOrWhiteSpace(firstFailure) ? commandFailure ?? string.Empty : firstFailure,
            $"dotnet run --project {ShellQuote(Path.Join(context.Request.RepositoryRoot, "tools", "ForgeTrust.AppSurface.PackageIndex", "ForgeTrust.AppSurface.PackageIndex.csproj"))} -- verify-packages --repo-root {ShellQuote(context.Request.RepositoryRoot)} --package-version {ShellQuote(context.Request.PackageVersion)} --artifacts-output {ShellQuote(context.Request.ArtifactsDirectory)} --source {ShellQuote(context.Request.Source)} --docs-proof-work-dir {ShellQuote(context.Request.WorkDirectory)} --docs-proof-report {ShellQuote(Path.Join(context.Request.ArtifactsDirectory, "docs-package-consumer-proof.md"))}");
    }

    private static void VerifyLockFile(
        string lockFilePath,
        string packageVersion,
        IReadOnlyList<ExpectedPackage> expectedPackages)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(lockFilePath));
            if (!document.RootElement.TryGetProperty("dependencies", out var frameworks)
                || frameworks.ValueKind != JsonValueKind.Object)
            {
                throw new PackageIndexException(
                    $"Docs package consumer proof lock file '{lockFilePath}' does not resolve '{DocsPackageId}' at '{packageVersion}' as a package.");
            }

            var expectedDependencies = expectedPackages.Where(expected => !expected.IsVersionFromRequest).ToArray();
            if (!frameworks.EnumerateObject().Any(framework => HasResolvedDocsLockGraph(
                    framework.Value,
                    packageVersion,
                    expectedDependencies)))
            {
                throw new PackageIndexException(
                    $"Docs package consumer proof lock file '{lockFilePath}' does not resolve '{DocsPackageId}' at '{packageVersion}' with the exact stable parser and sanitizer dependency edges.");
            }
        }
        catch (JsonException ex)
        {
            throw new PackageIndexException($"Docs package consumer proof could not parse lock file '{lockFilePath}': {ex.Message}");
        }
    }

    private static void VerifyAssetsFile(
        string assetsFilePath,
        string packageVersion,
        IReadOnlyList<ExpectedPackage> expectedPackages)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(assetsFilePath));
            if (!document.RootElement.TryGetProperty("libraries", out var libraries)
                || libraries.ValueKind != JsonValueKind.Object)
            {
                throw new PackageIndexException($"Docs package consumer proof assets file '{assetsFilePath}' has no libraries graph.");
            }

            foreach (var expected in expectedPackages)
            {
                var libraryKey = $"{expected.Id}/{expected.Version}";
                if (!libraries.TryGetProperty(libraryKey, out var library)
                    || library.ValueKind != JsonValueKind.Object
                    || !library.TryGetProperty("type", out var type)
                    || !string.Equals(type.GetString(), "package", StringComparison.OrdinalIgnoreCase))
                {
                    throw new PackageIndexException(
                        $"Docs package consumer proof assets file '{assetsFilePath}' does not resolve '{libraryKey}' as a package.");
                }
            }

            var expectedDependencies = expectedPackages.Where(expected => !expected.IsVersionFromRequest).ToArray();
            if (!document.RootElement.TryGetProperty("targets", out var targets)
                || targets.ValueKind != JsonValueKind.Object
                || !targets.EnumerateObject().Any(target => HasResolvedDocsAssetsGraph(
                    target.Value,
                    packageVersion,
                    expectedDependencies)))
            {
                throw new PackageIndexException(
                    $"Docs package consumer proof assets file '{assetsFilePath}' does not resolve '{DocsPackageId}' with the exact stable parser and sanitizer dependency edges.");
            }
        }
        catch (JsonException ex)
        {
            throw new PackageIndexException($"Docs package consumer proof could not parse assets file '{assetsFilePath}': {ex.Message}");
        }
    }

    private static bool HasResolvedDocsLockGraph(
        JsonElement framework,
        string packageVersion,
        IReadOnlyList<ExpectedPackage> expectedDependencies)
    {
        return framework.ValueKind == JsonValueKind.Object
            && framework.TryGetProperty(DocsPackageId, out var dependency)
            && dependency.ValueKind == JsonValueKind.Object
            && dependency.TryGetProperty("resolved", out var resolved)
            && string.Equals(resolved.GetString(), packageVersion, StringComparison.Ordinal)
            && dependency.TryGetProperty("type", out var type)
            && string.Equals(type.GetString(), "Direct", StringComparison.OrdinalIgnoreCase)
            && dependency.TryGetProperty("dependencies", out var dependencies)
            && HasExactStableParserAndSanitizerDependencies(dependencies, expectedDependencies);
    }

    private static bool HasResolvedDocsAssetsGraph(
        JsonElement target,
        string packageVersion,
        IReadOnlyList<ExpectedPackage> expectedDependencies)
    {
        var docsLibraryKey = $"{DocsPackageId}/{packageVersion}";
        return target.ValueKind == JsonValueKind.Object
            && target.TryGetProperty(docsLibraryKey, out var docsPackage)
            && docsPackage.ValueKind == JsonValueKind.Object
            && docsPackage.TryGetProperty("type", out var type)
            && string.Equals(type.GetString(), "package", StringComparison.OrdinalIgnoreCase)
            && docsPackage.TryGetProperty("dependencies", out var dependencies)
            && HasExactStableParserAndSanitizerDependencies(dependencies, expectedDependencies);
    }

    private static bool HasExactStableParserAndSanitizerDependencies(
        JsonElement dependencies,
        IReadOnlyList<ExpectedPackage> expectedDependencies) =>
        dependencies.ValueKind == JsonValueKind.Object
        && expectedDependencies.All(expected =>
            dependencies.TryGetProperty(expected.Id, out var version)
            && version.ValueKind == JsonValueKind.String
            && string.Equals(version.GetString(), $"[{expected.Version}]", StringComparison.Ordinal));

    private static bool IsPackageLockEntry(JsonElement dependency) =>
        dependency.ValueKind == JsonValueKind.Object
        && dependency.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && !string.Equals(type.GetString(), "Project", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string?> CreateProofEnvironment(string dotNetHomePath, string sharedPackagesPath) =>
        new Dictionary<string, string?>
        {
            ["CI"] = "true",
            ["DOTNET_CLI_HOME"] = dotNetHomePath,
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["NUGET_PACKAGES"] = sharedPackagesPath
        };

    private static void ValidateRequest(DocsPackageConsumerProofRequest request)
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

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalidCharacters.Contains(character) ? '-' : character);
        }

        return builder.ToString().Replace(' ', '-');
    }

    private static void DeleteTemporaryDirectory(string temporaryDirectory)
    {
        try
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Retained temporary cache files never affect proof evidence, which lives in the requested work directory.
        }
        catch (UnauthorizedAccessException)
        {
            // Retained temporary cache files never affect proof evidence, which lives in the requested work directory.
        }
    }

    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private sealed record ExpectedPackage(string Id, string? Version = null, bool IsVersionFromRequest = false);
}

/// <summary>
/// Request for an isolated packed Docs consumer restore proof.
/// </summary>
/// <param name="RepositoryRoot">Repository root used for safety checks and lock-graph discovery.</param>
/// <param name="ArtifactsDirectory">Directory containing freshly validated local package artifacts.</param>
/// <param name="PackageVersion">Exact Docs package version under proof.</param>
/// <param name="WorkDirectory">Isolated work directory that can be deleted and recreated.</param>
/// <param name="Source">Public source used only for reviewed third-party package ids.</param>
internal sealed record DocsPackageConsumerProofRequest(
    string RepositoryRoot,
    string ArtifactsDirectory,
    string PackageVersion,
    string WorkDirectory,
    string Source);

/// <summary>
/// Identity and hash of the validated Docs artifact selected for consumer restore.
/// </summary>
/// <param name="PackageId">Docs package id.</param>
/// <param name="ProjectPath">Project that produced the selected artifact.</param>
/// <param name="ArtifactPath">Validated local package artifact path.</param>
/// <param name="Sha512">Base64 SHA-512 hash of the selected artifact.</param>
internal sealed record DocsPackageConsumerProofSelectedArtifact(
    string PackageId,
    string ProjectPath,
    string ArtifactPath,
    string Sha512);

/// <summary>
/// One resolved package identity verified in the generated consumer's assets graph.
/// </summary>
/// <param name="Id">Package id.</param>
/// <param name="Version">Exact resolved package version.</param>
internal sealed record DocsPackageConsumerProofResolvedPackage(string Id, string Version);

/// <summary>
/// Structural proof that the generated consumer resolved all required package identities.
/// </summary>
/// <param name="AssetsFilePath">Generated NuGet assets file inspected by the proof.</param>
/// <param name="LockFilePath">Generated consumer lock file verified in locked mode.</param>
/// <param name="ResolvedPackages">Docs and parser/sanitizer package identities verified as packages.</param>
internal sealed record DocsPackageConsumerGraphVerification(
    string AssetsFilePath,
    string LockFilePath,
    IReadOnlyList<DocsPackageConsumerProofResolvedPackage> ResolvedPackages);

/// <summary>
/// One command in the packed Docs consumer proof ledger.
/// </summary>
/// <param name="OperationName">Human-readable operation name.</param>
/// <param name="FileName">Executable name.</param>
/// <param name="Arguments">Command arguments.</param>
/// <param name="WorkingDirectory">Command working directory.</param>
/// <param name="ExitCode">Observed exit code.</param>
/// <param name="Succeeded">Whether the command exited successfully.</param>
/// <param name="FailureReason">Failure details when the command did not succeed.</param>
/// <param name="Duration">Measured command duration.</param>
/// <param name="StandardOutputPath">Captured stdout log path.</param>
/// <param name="StandardErrorPath">Captured stderr log path.</param>
/// <param name="StandardOutput">Captured stdout.</param>
/// <param name="StandardError">Captured stderr.</param>
internal sealed record DocsPackageConsumerProofCommandResult(
    string OperationName,
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    int ExitCode,
    bool Succeeded,
    string FailureReason,
    TimeSpan Duration,
    string StandardOutputPath,
    string StandardErrorPath,
    string StandardOutput,
    string StandardError);

/// <summary>
/// Structured evidence emitted by the packed Docs consumer proof.
/// </summary>
/// <param name="PackageVersion">Exact Docs package version under proof.</param>
/// <param name="WorkDirectory">Isolated proof workspace.</param>
/// <param name="Source">Public package source.</param>
/// <param name="SelectedArtifact">Validated Docs package selected for restore.</param>
/// <param name="NuGetConfigPath">Generated source-mapping configuration.</param>
/// <param name="ConsumerProjectPath">Generated consumer project.</param>
/// <param name="ConsumerLockFilePath">Generated consumer lock file.</param>
/// <param name="AssetsFilePath">Generated consumer assets file.</param>
/// <param name="ThirdPartyPackageIds">Exact third-party package ids permitted from the public source.</param>
/// <param name="LogsDirectory">Command stdout and stderr log directory.</param>
/// <param name="Commands">Command ledger.</param>
/// <param name="GraphVerification">Resolved graph evidence, when restore and inspection succeeded.</param>
/// <param name="FirstFailure">First failure, or empty when proof succeeded.</param>
/// <param name="ReproduceCommand">Command that reruns the package verifier with the same proof directory.</param>
internal sealed record DocsPackageConsumerProofReport(
    string PackageVersion,
    string WorkDirectory,
    string Source,
    DocsPackageConsumerProofSelectedArtifact? SelectedArtifact,
    string NuGetConfigPath,
    string ConsumerProjectPath,
    string ConsumerLockFilePath,
    string AssetsFilePath,
    IReadOnlyList<string> ThirdPartyPackageIds,
    string LogsDirectory,
    IReadOnlyList<DocsPackageConsumerProofCommandResult> Commands,
    DocsPackageConsumerGraphVerification? GraphVerification,
    string FirstFailure,
    string ReproduceCommand)
{
    /// <summary>
    /// Gets whether every command and graph verification succeeded.
    /// </summary>
    internal bool Succeeded => string.IsNullOrWhiteSpace(FirstFailure)
        && GraphVerification is not null
        && Commands.All(command => command.Succeeded);

    /// <summary>
    /// Builds a report for failures that occur before the proof workspace is fully initialized.
    /// </summary>
    /// <param name="packageVersion">Package version under proof.</param>
    /// <param name="workDirectory">Requested proof directory.</param>
    /// <param name="source">Requested public source.</param>
    /// <param name="firstFailure">Failure message.</param>
    /// <param name="selectedArtifact">Selected artifact, if selection already succeeded.</param>
    /// <returns>Failed proof report.</returns>
    internal static DocsPackageConsumerProofReport Failed(
        string packageVersion,
        string workDirectory,
        string source,
        string firstFailure,
        DocsPackageConsumerProofSelectedArtifact? selectedArtifact = null) =>
        new(
            packageVersion,
            workDirectory,
            source,
            selectedArtifact,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            [],
            string.Empty,
            [],
            null,
            firstFailure,
            string.Empty);
}

/// <summary>
/// Renders packed Docs consumer proof evidence for standalone and aggregate package reports.
/// </summary>
internal static class DocsPackageConsumerProofReportRenderer
{
    /// <summary>
    /// Renders a standalone markdown report.
    /// </summary>
    /// <param name="report">Proof report.</param>
    /// <returns>Markdown content.</returns>
    internal static string RenderMarkdown(DocsPackageConsumerProofReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Docs package consumer proof");
        builder.AppendLine();
        RenderSection(builder, report);
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>
    /// Appends proof evidence to an aggregate package report.
    /// </summary>
    /// <param name="builder">Destination markdown builder.</param>
    /// <param name="report">Proof report.</param>
    internal static void RenderSection(StringBuilder builder, DocsPackageConsumerProofReport report)
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

        if (!string.IsNullOrWhiteSpace(report.NuGetConfigPath))
        {
            builder.AppendLine($"Consumer NuGet config: `{report.NuGetConfigPath}`");
            builder.AppendLine($"Consumer project: `{report.ConsumerProjectPath}`");
            builder.AppendLine($"Consumer lock file: `{report.ConsumerLockFilePath}`");
            builder.AppendLine($"Consumer assets file: `{report.AssetsFilePath}`");
        }

        if (report.ThirdPartyPackageIds.Count > 0)
        {
            builder.AppendLine($"Allowed public package ids: `{string.Join(", ", report.ThirdPartyPackageIds)}`");
        }

        if (report.GraphVerification is not null)
        {
            builder.AppendLine();
            builder.AppendLine("## Resolved package graph");
            builder.AppendLine();
            builder.AppendLine("| Package | Version |");
            builder.AppendLine("| --- | --- |");
            foreach (var package in report.GraphVerification.ResolvedPackages)
            {
                builder.AppendLine($"| `{package.Id}` | `{package.Version}` |");
            }
        }

        if (report.Commands.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Commands");
            builder.AppendLine();
            builder.AppendLine("| Command | Exit | Status | stdout | stderr |");
            builder.AppendLine("| --- | ---: | --- | --- | --- |");
            foreach (var command in report.Commands)
            {
                builder.AppendLine(
                    $"| `{EscapeCode(command.OperationName)}` | {command.ExitCode} | `{(command.Succeeded ? "passed" : "failed")}` | `{command.StandardOutputPath}` | `{command.StandardErrorPath}` |");
            }
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
    }

    private static string EscapeCode(string value) => value.Replace('`', '\'');
}

/// <summary>
/// Runtime paths and inputs shared by the packed Docs consumer proof.
/// </summary>
internal sealed record DocsPackageConsumerProofContext(
    DocsPackageConsumerProofRequest Request,
    DocsPackageConsumerProofSelectedArtifact SelectedArtifact,
    string ConsumerDirectory,
    string LogsDirectory,
    string NuGetConfigPath,
    string ConsumerProjectPath,
    string ConsumerLockFilePath,
    string AssetsFilePath,
    string SharedPackagesPath,
    string DotNetHomePath,
    IReadOnlyList<string> ThirdPartyPackageIds,
    IExternalCommandRunner CommandRunner);
