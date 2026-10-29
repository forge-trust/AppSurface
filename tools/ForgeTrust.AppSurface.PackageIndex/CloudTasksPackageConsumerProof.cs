using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>Proves the packed Cloud Tasks API, invalid-token no-I/O behavior, and neutral Durable graph isolation.</summary>
internal sealed class CloudTasksPackageConsumerProof
{
    internal const string PackageId = "ForgeTrust.AppSurface.Durable.CloudTasks";
    internal const string DeterministicPackageTimestamp = "2000-01-01T00:00:00Z";
    /// <summary>Names the report file invalidated before every direct packed-proof run.</summary>
    internal const string ReportFileName = "cloudtasks-package-consumer-proof.md";
    internal const string NeutralPackageId = "ForgeTrust.AppSurface.Durable";
    private static readonly string[] NeutralPackages =
    [
        "ForgeTrust.AppSurface.Core",
        "ForgeTrust.AppSurface.Flow",
        "ForgeTrust.AppSurface.Workers",
        NeutralPackageId,
    ];
    private static readonly string[] ForbiddenNeutralPrefixes = ["Google.", "Grpc.", "Gax."];
    private static readonly string[] NeutralProjectPackageIds =
    [
        .. NeutralPackages,
        "ForgeTrust.AppSurface.Durable.Provider",
        "ForgeTrust.AppSurface.Durable.PostgreSql",
        "ForgeTrust.AppSurface.Durable.Testing",
        "ForgeTrust.AppSurface.Cli",
        "ForgeTrust.AppSurface.Web",
    ];
    private readonly ICommandRunner _runner;
    private readonly Func<string, string, string, CancellationToken, Task> _baselineExtractor;

    internal CloudTasksPackageConsumerProof(
        ICommandRunner runner,
        Func<string, string, string, CancellationToken, Task>? baselineExtractor = null)
    {
        _runner = runner;
        _baselineExtractor = baselineExtractor ?? ((root, target, commit, token) => ExtractBaselineAsync(root, target, commit, token, _runner));
    }

    internal async Task<CloudTasksPackageConsumerProofReport> RunAsync(
        CloudTasksPackageConsumerProofRequest request,
        PackageArtifactValidationReport artifacts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(artifacts);
        var proofPath = Path.Combine(request.ArtifactsDirectory, ReportFileName);
        if (File.Exists(proofPath)) File.Delete(proofPath);
        if (!string.Equals(artifacts.PackageVersion, request.PackageVersion, StringComparison.OrdinalIgnoreCase))
            throw new PackageIndexException("Cloud Tasks packed proof package version does not match the validated artifact report.");
        var candidateCommit = request.SourceCommit is null
            ? await ReadRepositoryHeadAsync(request.RepositoryRoot, cancellationToken)
            : RequireCommit(request.SourceCommit, "candidate source commit");
        var baselineMode = request.BaselineRef is null ? "candidate-source" : "historical-acceptance";
        var baselineCommit = request.BaselineRef is null
            ? candidateCommit
            : await ResolveBaselineMergeBaseAsync(request.RepositoryRoot, request.BaselineRef, candidateCommit, cancellationToken);
        var cloudTasks = RequireArtifact(artifacts, PackageId, request);
        var candidateNeutral = NeutralPackages.ToDictionary(
            id => id,
            id => RequireArtifact(artifacts, id, request),
            StringComparer.OrdinalIgnoreCase);
        var workspace = Path.Combine(Path.GetTempPath(), "appsurface-cloudtasks-proof-" + Guid.NewGuid().ToString("N"));
        var baselineRoot = Path.Combine(workspace, "baseline-source");
        var baselineFeed = Path.Combine(workspace, "baseline-feed");
        var candidateFeed = Path.Combine(workspace, "candidate-feed");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(baselineFeed);
        Directory.CreateDirectory(candidateFeed);
        try
        {
            foreach (var item in candidateNeutral.Values.Append(cloudTasks))
            {
                File.Copy(item.Path, Path.Combine(candidateFeed, Path.GetFileName(item.Path)));
            }

            await ProvePublicApiConsumerAsync(request, workspace, candidateFeed, cancellationToken);
            ValidateCloudTasksRuntimeGraph(
                Path.Combine(workspace, "cloudtasks-consumer", "obj", "project.assets.json"),
                Path.Combine(request.RepositoryRoot, "Durable", PackageId, "packages.lock.json"));
            var sdkLeaks = ScanPublicApiForGoogleTypes(cloudTasks.Path, Path.Combine(workspace, "cloudtasks-consumer", ".nuget-packages"));
            if (sdkLeaks.Count != 0)
            {
                throw new PackageIndexException("Cloud Tasks public API exposes provider SDK types: " + string.Join(", ", sdkLeaks));
            }

            await _baselineExtractor(request.RepositoryRoot, baselineRoot, baselineCommit, cancellationToken);
            var sourceHashes = CloudTasksNeutralSourceProof.Verify(baselineRoot, request.RepositoryRoot);
            await PackBaselineNeutralClosureAsync(request, workspace, baselineRoot, baselineFeed, candidateCommit, cancellationToken);
            var assemblyIdentity = CompareNeutralAssemblyIdentity(candidateNeutral, baselineFeed, request.PackageVersion);
            var baselineGraph = await RestoreNeutralConsumerAsync(request, workspace, baselineFeed, "neutral-baseline", request.PackageVersion, cancellationToken);
            var candidateGraph = await RestoreNeutralConsumerAsync(request, workspace, candidateFeed, "neutral-candidate", request.PackageVersion, cancellationToken);
            var baseline = ReadGraph(baselineGraph);
            var candidate = ReadGraph(candidateGraph);
            AssertNeutralGraph(baseline, candidate);
            AssertGraphArtifacts(candidate, candidateNeutral, baseline: false);
            AssertGraphArtifacts(baseline, CompareBaselineArtifacts(baselineFeed, request.PackageVersion), baseline: true);
            AssertNeutralPackageContracts(assemblyIdentity);
            var cloudTasksGraph = ReadGraph(Path.Combine(workspace, "cloudtasks-consumer", "obj", "project.assets.json"));
            AssertGraphArtifacts(cloudTasksGraph, new Dictionary<string, CloudTasksPackageArtifact>(StringComparer.OrdinalIgnoreCase)
            {
                [cloudTasks.Id] = cloudTasks,
            }, baseline: false);
            var isolationViolations = FindNeutralProjectReferenceViolations(request.RepositoryRoot);
            if (isolationViolations.Count != 0)
                throw new PackageIndexException("Neutral Durable projects reference Cloud Tasks or Google transport packages: " + string.Join(", ", isolationViolations));
            var report = new CloudTasksPackageConsumerProofReport(
                cloudTasks.Sha512,
                baselineCommit,
                baselineMode,
                baselineGraph,
                candidateGraph,
                baseline,
                candidate,
                cloudTasksGraph,
                assemblyIdentity,
                sourceHashes,
                proofPath);
            Directory.CreateDirectory(Path.GetDirectoryName(proofPath)!);
            await File.WriteAllTextAsync(proofPath, Render(report), cancellationToken);
            return report;
        }
        finally
        {
            PackageArtifactWorkflow.TryDeleteProofWorkspace(workspace, Directory.Exists, static directory => Directory.Delete(directory, recursive: true));
        }
    }

    internal static IReadOnlyList<string> ScanPublicApiForGoogleTypes(string packagePath, string packageCache)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var sdkAssemblies = archive.Entries
            .Where(static entry => entry.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)
                && entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(static entry => Path.GetFileNameWithoutExtension(entry.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var docLeaks = archive.Entries
            .Where(static entry => entry.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)
                && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .SelectMany(ReadAllText)
            .SelectMany(static text => ForbiddenNeutralPrefixes.Where(prefix => text.Contains(prefix, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var assemblyName in sdkAssemblies)
        {
            var assembly = archive.Entries.First(entry => entry.FullName.EndsWith("/" + assemblyName + ".dll", StringComparison.OrdinalIgnoreCase));
            var tempAssembly = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");
            try
            {
                using (var input = assembly.Open())
                using (var output = File.Create(tempAssembly)) input.CopyTo(output);
                var loadContext = new AssemblyLoadContext("CloudTasksPublicApiScan-" + Guid.NewGuid().ToString("N"), isCollectible: true);
                loadContext.Resolving += (_, requested) =>
                {
                    if (!Directory.Exists(packageCache)) return null;
                    var dependencyPath = Directory.EnumerateFiles(packageCache, requested.Name + ".dll", SearchOption.AllDirectories).FirstOrDefault();
                    return dependencyPath is null ? null : loadContext.LoadFromAssemblyPath(dependencyPath);
                };
                var loaded = loadContext.LoadFromAssemblyPath(tempAssembly);
                foreach (var type in loaded.GetExportedTypes())
                {
                    if (ContainsSdkType(type)) docLeaks.Add(type.FullName ?? type.Name);
                    if (type.BaseType is not null && ContainsSdkType(type.BaseType)) docLeaks.Add($"{type.FullName}.base");
                    if (type.GetInterfaces().Any(ContainsSdkType)) docLeaks.Add($"{type.FullName}.interface");
                    foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    {
                        if (member is MethodInfo genericMethod
                            && genericMethod.IsGenericMethod
                            && genericMethod.GetGenericArguments().Any(ContainsSdkType))
                        {
                            docLeaks.Add($"{type.FullName}.{member.Name}");
                        }

                        var memberTypes = member switch
                        {
                            MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType),
                            ConstructorInfo ctor => ctor.GetParameters().Select(parameter => parameter.ParameterType),
                            PropertyInfo property => new[] { property.PropertyType }.Concat(property.GetIndexParameters().Select(parameter => parameter.ParameterType)),
                            FieldInfo field => [field.FieldType],
                            EventInfo @event when @event.EventHandlerType is not null => [@event.EventHandlerType],
                            _ => [],
                        };
                        if (memberTypes.Any(ContainsSdkType)) docLeaks.Add($"{type.FullName}.{member.Name}");
                    }
                }
                loadContext.Unload();
            }
            catch (ReflectionTypeLoadException ex)
            {
                throw new PackageIndexException($"Could not completely inspect Cloud Tasks public API metadata ({ex.LoaderExceptions.Length} loader errors).", ex);
            }
            finally
            {
                if (File.Exists(tempAssembly)) File.Delete(tempAssembly);
            }
        }
        return docLeaks.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    internal static string CanonicalizePackageId(string id, string coordinatedVersion)
    {
        var slash = id.LastIndexOf('/');
        if (slash <= 0 || slash == id.Length - 1) throw new PackageIndexException("Malformed package graph identity.");
        var package = id[..slash];
        var version = id[(slash + 1)..];
        return package.StartsWith("ForgeTrust.AppSurface.", StringComparison.OrdinalIgnoreCase)
            ? package.ToLowerInvariant() + "/<coordinated>"
            : package.ToLowerInvariant() + "/" + version.ToLowerInvariant();
    }

    private static bool ContainsSdkType(Type type) => ContainsSdkType(type, new HashSet<Type>());

    private static bool ContainsSdkType(Type type, HashSet<Type> visited)
    {
        if (!visited.Add(type)) return false;
        var ns = type.Namespace ?? string.Empty;
        if (ForbiddenNeutralPrefixes.Any(prefix => ns.StartsWith(prefix, StringComparison.Ordinal))) return true;
        if (type.IsByRef || type.IsPointer || type.IsArray)
            return ContainsSdkType(type.GetElementType()!, visited);
        if (type.IsGenericParameter && type.GetGenericParameterConstraints().Any(constraint => ContainsSdkType(constraint, visited)))
            return true;
        return type.IsGenericType && type.GetGenericArguments().Any(argument => ContainsSdkType(argument, visited));
    }

    private static IEnumerable<string> ReadAllText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        yield return reader.ReadToEnd();
    }

    private async Task ProvePublicApiConsumerAsync(CloudTasksPackageConsumerProofRequest request, string workspace, string candidateFeed, CancellationToken token)
    {
        var source = Path.Combine(request.RepositoryRoot, "Durable", "packed-consumers", "CloudTasks");
        var target = Path.Combine(workspace, "cloudtasks-consumer");
        CopyFixture(source, target);
        File.Move(Path.Combine(target, "CloudTasksConsumer.csproj.template"), Path.Combine(target, "CloudTasksConsumer.csproj"));
        PrepareConsumerCommon(target, candidateFeed, request.Source, request.PackageVersion);
        await RestoreAndBuildAsync(target, "CloudTasksConsumer.csproj", request.PackageVersion, "compiling packed Cloud Tasks public API consumer", token);
        var output = await _runner.RunAsync(new CommandRunRequest("dotnet", ["run", "--project", "CloudTasksConsumer.csproj", "--configuration", "Release", "--no-build", "--no-restore"], target,
            "Cloud Tasks packed consumer execution", PackageId, "run", "running the packed consumer", 60_000,
            ProofEnvironment(Path.Combine(target, ".nuget-packages"), Path.Combine(target, ".dotnet-home")), IncludeStandardOutputOnFailure: true), token);
        if (!output.StandardOutput.Contains("ASCT102", StringComparison.Ordinal))
            throw new PackageIndexException("Packed Cloud Tasks consumer did not verify invalid-token rejection before provider I/O.");
    }

    private async Task<string> RestoreNeutralConsumerAsync(CloudTasksPackageConsumerProofRequest request, string workspace, string feed, string name, string version, CancellationToken token)
    {
        var target = Path.Combine(workspace, name);
        CopyFixture(Path.Combine(request.RepositoryRoot, "Durable", "packed-consumers", "CloudTasks", "NeutralConsumer"), target);
        File.Move(Path.Combine(target, "NeutralConsumer.csproj.template"), Path.Combine(target, "NeutralConsumer.csproj"));
        PrepareConsumerCommon(target, feed, request.Source, version);
        await RestoreAndBuildAsync(target, "NeutralConsumer.csproj", version, $"restoring {name} packed neutral graph", token);
        var assets = Path.Combine(target, "obj", "project.assets.json");
        if (!File.Exists(assets)) throw new PackageIndexException($"The {name} restore did not produce project.assets.json.");
        return assets;
    }

    private async Task PackBaselineNeutralClosureAsync(CloudTasksPackageConsumerProofRequest request, string workspace, string baselineRoot, string baselineFeed, string candidateCommit, CancellationToken token)
    {
        var proofCache = Path.Combine(workspace, "baseline-packages");
        var proofCli = Path.Combine(workspace, "baseline-cli");
        Directory.CreateDirectory(proofCache);
        Directory.CreateDirectory(proofCli);
        var environment = ProofEnvironment(proofCache, proofCli);
        foreach (var id in NeutralPackages)
        {
            var project = ProjectPath(baselineRoot, id);
            await _runner.RunAsync(new CommandRunRequest("dotnet", ["restore", project, "--configfile", Path.Combine(baselineRoot, "NuGet.package-gate.config"),
                "-m:1", "-p:Version=" + request.PackageVersion, "-p:PackageVersion=" + request.PackageVersion,
                "-p:RepositoryCommit=" + (request.SourceCommit ?? string.Empty), "-p:SourceRevisionId=" + candidateCommit,
                "-p:DeterministicTimestamp=" + DeterministicPackageTimestamp,
                "-p:PathMap=" + baselineRoot + "=/_/", "-p:ContinuousIntegrationBuild=true", "-p:RestoreLockedMode=false", "-p:UseSharedCompilation=false"], baselineRoot,
                "Cloud Tasks pinned baseline restore", id, "restore", "restoring the pinned baseline package", 180_000, environment, IncludeStandardOutputOnFailure: true), token);
            await _runner.RunAsync(new CommandRunRequest("dotnet", ["pack", project, "--configuration", "Release", "--no-restore", "--output", baselineFeed,
                "-m:1", "-p:Version=" + request.PackageVersion, "-p:PackageVersion=" + request.PackageVersion,
                "-p:RepositoryCommit=" + (request.SourceCommit ?? string.Empty), "-p:SourceRevisionId=" + candidateCommit,
                "-p:DeterministicTimestamp=" + DeterministicPackageTimestamp,
                "-p:PathMap=" + baselineRoot + "=/_/", "-p:ContinuousIntegrationBuild=true", "-p:UseSharedCompilation=false"], baselineRoot,
                "Cloud Tasks pinned baseline pack", id, "pack", "packing the pinned baseline package", 180_000, environment, IncludeStandardOutputOnFailure: true), token);
        }
    }

    private async Task<string> ReadRepositoryHeadAsync(string repositoryRoot, CancellationToken token)
    {
        var result = await _runner.RunAsync(new CommandRunRequest(
            "git", ["rev-parse", "HEAD"], repositoryRoot, "Cloud Tasks candidate source revision", "repository", "read", "resolving the candidate source revision", 30_000,
            new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0" }), token);
        var revision = result.StandardOutput.Trim();
        if (revision.Length != 40 || revision.Any(static character => !Uri.IsHexDigit(character)))
            throw new PackageIndexException("Could not resolve a canonical 40-character candidate Git revision for the Cloud Tasks package proof.");
        return revision.ToLowerInvariant();
    }

    private async Task<string> ResolveBaselineMergeBaseAsync(string repositoryRoot, string baselineRef, string candidateCommit, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(baselineRef) || baselineRef.StartsWith("-", StringComparison.Ordinal)
            || baselineRef.Any(char.IsControl) || baselineRef.Any(char.IsWhiteSpace)
            || baselineRef.Contains("..", StringComparison.Ordinal)
            || baselineRef.Contains("@{", StringComparison.Ordinal)
            || baselineRef.Contains('\\'))
            throw new PackageIndexException("Cloud Tasks historical baseline ref must be a nonblank Git ref without whitespace or control characters.");

        CommandRunResult result;
        try
        {
            result = await _runner.RunAsync(new CommandRunRequest(
                "git", ["merge-base", "--all", baselineRef, candidateCommit], repositoryRoot,
                "Cloud Tasks baseline merge-base", baselineRef, "resolve", "resolving the historical baseline", 30_000,
                new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0" }), token);
        }
        catch (PackageIndexException exception)
        {
            throw new PackageIndexException(
                "Could not resolve the requested Cloud Tasks historical baseline. Fetch the target ref and required Git history (for example, fetch the target branch and use a full-history checkout); historical acceptance never falls back to candidate-source mode.", exception);
        }
        var commits = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (commits.Length != 1)
            throw new PackageIndexException("Cloud Tasks historical baseline ref must have exactly one merge-base with the candidate commit. Fetch the target ref and required Git history; no baseline was selected and there is no fallback.");
        string baselineCommit;
        try { baselineCommit = RequireCommit(commits[0], "historical baseline merge-base"); }
        catch (PackageIndexException exception)
        {
            throw new PackageIndexException("Cloud Tasks historical merge-base output is not a canonical commit. Fetch the target ref and required Git history; there is no fallback.", exception);
        }
        if (string.Equals(baselineCommit, candidateCommit, StringComparison.Ordinal))
            throw new PackageIndexException("Cloud Tasks historical acceptance requires an earlier reviewed baseline; omit the option for ordinary candidate-source verification.");
        return baselineCommit;
    }

    private static string RequireCommit(string commit, string context)
    {
        if (commit.Length != 40 || commit.Any(static character => !Uri.IsHexDigit(character)))
            throw new PackageIndexException($"Cloud Tasks {context} must be a canonical 40-character Git commit SHA.");
        return commit.ToLowerInvariant();
    }

    /// <summary>Extracts the selected source snapshot through the shared bounded, cancellation-cleaned process runner.</summary>
    /// <param name="repositoryRoot">Repository checkout containing the selected commit object.</param>
    /// <param name="target">Destination directory for the archived source tree.</param>
    /// <param name="commit">Canonical 40-character Git commit SHA selected by the proof mode.</param>
    /// <param name="token">Cancellation token forwarded to archive and extraction commands.</param>
    /// <param name="runner">Optional command runner seam; production uses the bounded process runner.</param>
    internal static async Task ExtractBaselineAsync(
        string repositoryRoot,
        string target,
        string commit,
        CancellationToken token,
        ICommandRunner? runner = null)
    {
        token.ThrowIfCancellationRequested();
        commit = RequireCommit(commit, "baseline commit");
        runner ??= new ProcessCommandRunner();
        Directory.CreateDirectory(target);
        var archivePath = Path.Combine(Path.GetTempPath(), "appsurface-cloudtasks-baseline-" + Guid.NewGuid().ToString("N") + ".tar");
        try
        {
            await runner.RunAsync(new CommandRunRequest(
                "git", ["archive", commit, "--format=tar", "--output", archivePath], repositoryRoot,
                "Cloud Tasks baseline archive", commit, "archive", "archiving the selected baseline", 30_000,
                new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0" }), token);
            await runner.RunAsync(new CommandRunRequest(
                "tar", ["-xf", archivePath, "-C", target], target,
                "Cloud Tasks baseline extraction", commit, "extract", "extracting the selected baseline", 60_000), token);
        }
        finally { if (File.Exists(archivePath)) File.Delete(archivePath); }
    }

    private static void PrepareConsumerCommon(string consumerRoot, string feed, string externalSource, string version)
    {
        // Consumers intentionally receive no repository-level MSBuild or package policy imports.
        File.WriteAllText(Path.Combine(consumerRoot, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(consumerRoot, "Directory.Build.targets"), "<Project />");
        File.WriteAllText(Path.Combine(consumerRoot, "Directory.Packages.props"), $"""
            <Project>
              <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile><AppSurfacePackageVersion>{version}</AppSurfacePackageVersion></PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="ForgeTrust.AppSurface.Durable.CloudTasks" Version="$(AppSurfacePackageVersion)" />
                <PackageVersion Include="ForgeTrust.AppSurface.Durable" Version="$(AppSurfacePackageVersion)" />
                <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="10.0.8" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(consumerRoot, "NuGet.config"), $"""
            <?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear/><add key="firstparty" value="{XmlEscape(feed)}"/><add key="external" value="{XmlEscape(externalSource)}"/></packageSources><packageSourceMapping><packageSource key="firstparty"><package pattern="ForgeTrust.*"/></packageSource><packageSource key="external"><package pattern="*"/></packageSource></packageSourceMapping></configuration>
            """);
    }

    private async Task RestoreAndBuildAsync(string root, string projectName, string version, string subject, CancellationToken token)
    {
        var cache = Path.Combine(root, ".nuget-packages");
        var cli = Path.Combine(root, ".dotnet-home");
        Directory.CreateDirectory(cache); Directory.CreateDirectory(cli);
        var env = ProofEnvironment(cache, cli);
        await _runner.RunAsync(new CommandRunRequest("dotnet", ["restore", projectName, "--configfile", "NuGet.config", "-m:1", "-p:AppSurfacePackageVersion=" + version, "-p:RestoreLockedMode=false", "-p:UseSharedCompilation=false"], root, "Cloud Tasks package consumer restore", subject, "restore", "restoring the isolated packed consumer", 180_000, env, IncludeStandardOutputOnFailure: true), token);
        await _runner.RunAsync(new CommandRunRequest("dotnet", ["restore", projectName, "--configfile", "NuGet.config", "--locked-mode", "-m:1", "-p:AppSurfacePackageVersion=" + version, "-p:UseSharedCompilation=false"], root, "Cloud Tasks locked package consumer restore", subject, "restore", "restoring the locked isolated packed consumer", 180_000, env, IncludeStandardOutputOnFailure: true), token);
        await _runner.RunAsync(new CommandRunRequest("dotnet", ["build", projectName, "--configuration", "Release", "--no-restore", "-m:1", "-p:AppSurfacePackageVersion=" + version, "-p:UseSharedCompilation=false"], root, "Cloud Tasks package consumer build", subject, "build", "building the isolated packed consumer", 180_000, env, IncludeStandardOutputOnFailure: true), token);
    }

    private static string ProjectPath(string root, string packageId) => packageId switch
    {
        "ForgeTrust.AppSurface.Core" => Path.Combine(root, "ForgeTrust.AppSurface.Core", "ForgeTrust.AppSurface.Core.csproj"),
        "ForgeTrust.AppSurface.Flow" => Path.Combine(root, "Flow", "ForgeTrust.AppSurface.Flow", "ForgeTrust.AppSurface.Flow.csproj"),
        "ForgeTrust.AppSurface.Workers" => Path.Combine(root, "Workers", "ForgeTrust.AppSurface.Workers", "ForgeTrust.AppSurface.Workers.csproj"),
        "ForgeTrust.AppSurface.Durable" => Path.Combine(root, "Durable", "ForgeTrust.AppSurface.Durable", "ForgeTrust.AppSurface.Durable.csproj"),
        _ => throw new PackageIndexException($"Unknown neutral Cloud Tasks proof package '{packageId}'."),
    };

    private static IReadOnlyDictionary<string, string?> ProofEnvironment(string cache, string cli) => new Dictionary<string, string?>
    {
        ["NUGET_PACKAGES"] = cache,
        ["DOTNET_CLI_HOME"] = cli,
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["MSBuildSDKsPath"] = null,
        ["MSBUILD_EXE_PATH"] = null,
        ["MSBuildExtensionsPath"] = null,
        ["DirectoryBuildPropsPath"] = null,
        ["DirectoryBuildTargetsPath"] = null,
        ["DirectoryPackagesPropsPath"] = null,
        ["CustomBeforeMicrosoftCommonTargets"] = null,
        ["CustomAfterMicrosoftCommonTargets"] = null,
        ["MSBUILDDISABLENODEREUSE"] = "1",
        ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0"
    };

    private static void CopyFixture(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(static part => part is "bin" or "obj")) continue;
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    private static CloudTasksPackageArtifact RequireArtifact(PackageArtifactValidationReport report, string id, CloudTasksPackageConsumerProofRequest request)
    {
        var entry = report.Entries.SingleOrDefault(item => string.Equals(item.PackageId, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new PackageIndexException($"Cloud Tasks packed consumer proof requires validated artifact '{id}'.");
        var path = Path.GetFullPath(entry.ArtifactPath);
        var root = Path.GetFullPath(request.ArtifactsDirectory) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.Ordinal) || !File.Exists(path)
            || !string.Equals(Path.GetFileName(path), $"{id}.{request.PackageVersion}.nupkg", StringComparison.OrdinalIgnoreCase))
            throw new PackageIndexException($"Validated artifact identity for '{id}' does not match the local candidate package version.");
        using var stream = File.OpenRead(path);
        var hash = Convert.ToBase64String(SHA512.HashData(stream));
        return new CloudTasksPackageArtifact(id, path, hash);
    }

    internal static CloudTasksPackageGraph ReadGraph(string assetsPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
        var root = document.RootElement;
        var targets = root.GetProperty("targets").EnumerateObject().ToArray();
        if (targets.Length != 1)
            throw new PackageIndexException($"Isolated Cloud Tasks proof consumer must restore exactly one target framework; found {targets.Length}.");
        var target = targets[0].Value;
        var libraries = root.GetProperty("libraries");
        var packages = new SortedDictionary<string, CloudTasksPackageGraphNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in target.EnumerateObject())
        {
            var slash = item.Name.LastIndexOf('/');
            if (slash < 1) continue;
            var id = item.Name[..slash];
            if (!libraries.TryGetProperty(item.Name, out var metadata) || !metadata.TryGetProperty("type", out var type) || type.GetString() != "package") continue;
            var deps = item.Value.TryGetProperty("dependencies", out var dependencySet)
                ? dependencySet.EnumerateObject().Select(static dependency => dependency.Name.ToLowerInvariant() + ":" + dependency.Value.GetString()).Order(StringComparer.Ordinal).ToArray()
                : [];
            var contentHash = metadata.TryGetProperty("sha512", out var sha) ? sha.GetString() ?? string.Empty : string.Empty;
            packages.Add(id.ToLowerInvariant() + "/" + item.Name[(slash + 1)..].ToLowerInvariant(), new CloudTasksPackageGraphNode(id, item.Name[(slash + 1)..], contentHash, deps));
        }
        var directDependencies = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("project", out var project)
            && project.TryGetProperty("frameworks", out var frameworks))
        {
            foreach (var framework in frameworks.EnumerateObject())
            {
                if (!framework.Value.TryGetProperty("dependencies", out var dependencies)) continue;
                foreach (var dependency in dependencies.EnumerateObject())
                {
                    var version = dependency.Value.ValueKind == JsonValueKind.Object
                        && dependency.Value.TryGetProperty("version", out var range)
                        ? range.GetString() ?? string.Empty
                        : dependency.Value.ValueKind == JsonValueKind.String ? dependency.Value.GetString() ?? string.Empty : string.Empty;
                    directDependencies[dependency.Name] = version;
                }
                break;
            }
        }
        return new CloudTasksPackageGraph(packages, directDependencies);
    }

    internal static void ValidateCloudTasksRuntimeGraph(string assetsPath, string lockFilePath)
    {
        var graph = ReadGraph(assetsPath);
        using var lockDocument = JsonDocument.Parse(File.ReadAllBytes(lockFilePath));
        if (!lockDocument.RootElement.GetProperty("dependencies").TryGetProperty("net10.0", out var locked))
            throw new PackageIndexException("Cloud Tasks package lock file does not contain the net10.0 target closure.");
        var expected = new SortedDictionary<string, (string Version, string Hash, string[] Dependencies)>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in locked.EnumerateObject())
        {
            var value = package.Value;
            var version = value.GetProperty("resolved").GetString() ?? string.Empty;
            var hash = value.GetProperty("contentHash").GetString() ?? string.Empty;
            var dependencies = value.TryGetProperty("dependencies", out var dependencySet)
                ? dependencySet.EnumerateObject().Select(static dependency => dependency.Name.ToLowerInvariant()).Order(StringComparer.Ordinal).ToArray()
                : [];
            expected.Add(package.Name, (version, hash, dependencies));
        }
        var rootPackageIds = graph.DirectDependencies.Keys.Where(id =>
            string.Equals(id, "ForgeTrust.AppSurface.Durable.CloudTasks", StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, "Microsoft.Extensions.DependencyInjection", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = graph.Nodes.Values.Where(node => !rootPackageIds.Contains(node.Id)).ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var missing = expected.Keys.Except(actual.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        var unexpected = actual.Keys.Except(expected.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length != 0 || unexpected.Length != 0)
            throw new PackageIndexException($"Packed Cloud Tasks runtime closure differs from packages.lock.json (missing: {string.Join(", ", missing)}; unexpected: {string.Join(", ", unexpected)}).");
        foreach (var required in expected)
        {
            var node = actual[required.Key];
            if (!string.Equals(node.Version, required.Value.Version, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(node.Sha512, required.Value.Hash, StringComparison.Ordinal))
                throw new PackageIndexException($"Packed Cloud Tasks runtime graph differs from packages.lock.json for {required.Key}/{required.Value.Version} content identity.");
            var actualDependencyIds = node.Dependencies.Select(static dependency => dependency.Split(':', 2)[0]).Order(StringComparer.Ordinal).ToArray();
            if (!actualDependencyIds.SequenceEqual(required.Value.Dependencies, StringComparer.Ordinal))
                throw new PackageIndexException($"Packed Cloud Tasks runtime dependency edges differ from packages.lock.json for {required.Key}.");
        }
        var lockedDirectRoots = locked.EnumerateObject()
            .Where(static package => string.Equals(package.Value.GetProperty("type").GetString(), "Direct", StringComparison.Ordinal))
            .Select(static package => package.Name.ToLowerInvariant())
            .Order(StringComparer.Ordinal)
            .ToArray();
        var cloudTasksRoot = graph.Nodes.Values.SingleOrDefault(static node => string.Equals(node.Id, PackageId, StringComparison.OrdinalIgnoreCase));
        var cloudTasksEdges = cloudTasksRoot?.Dependencies
            .Select(static dependency => dependency.Split(':', 2)[0])
            .Order(StringComparer.Ordinal)
            .ToArray() ?? [];
        if (cloudTasksRoot is null || !cloudTasksEdges.SequenceEqual(lockedDirectRoots, StringComparer.Ordinal))
            throw new PackageIndexException("Packed Cloud Tasks package dependencies do not match its direct packages.lock.json roots.");
    }

    internal static IReadOnlyList<string> FindNeutralProjectReferenceViolations(string repositoryRoot)
    {
        var result = new List<string>();
        var allowedCloudTasksConsumer = Path.GetFullPath(Path.Combine(repositoryRoot, "Durable", "packed-consumers", "CloudTasks", "CloudTasksConsumer.csproj.template"));
        var projects = Directory.EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(repositoryRoot, "*.csproj.template", SearchOption.AllDirectories));
        foreach (var project in projects
                     .Where(static path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                         && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            var normalized = Path.GetFullPath(project);
            var isNeutral = NeutralProjectPackageIds.Any(id => string.Equals(Path.GetFileNameWithoutExtension(project), id, StringComparison.OrdinalIgnoreCase))
                || normalized.Contains(Path.DirectorySeparatorChar + "Durable" + Path.DirectorySeparatorChar + "packed-consumers" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            isNeutral |= normalized.Contains(Path.DirectorySeparatorChar + "Cli" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || normalized.Contains(Path.DirectorySeparatorChar + "durable-worker" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (!isNeutral) continue;
            var doc = System.Xml.Linq.XDocument.Load(project);
            foreach (var package in doc.Descendants().Where(element => element.Name.LocalName == "PackageReference"))
            {
                var id = (string?)package.Attribute("Include") ?? (string?)package.Attribute("Update") ?? string.Empty;
                if (ForbiddenNeutralPrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) result.Add(Path.GetRelativePath(repositoryRoot, project) + ":" + id);
                if (string.Equals(id, PackageId, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(normalized, allowedCloudTasksConsumer, StringComparison.Ordinal))
                    result.Add(Path.GetRelativePath(repositoryRoot, project) + ":" + id);
            }
            foreach (var variable in doc.Descendants().Where(element => element.Name.LocalName == "ProjectReference"))
            {
                var referenced = Path.GetFileNameWithoutExtension((string?)variable.Attribute("Include") ?? string.Empty);
                if (referenced.Equals(PackageId, StringComparison.OrdinalIgnoreCase)) result.Add(Path.GetRelativePath(repositoryRoot, project) + ":" + referenced);
            }
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    internal static IReadOnlyList<CloudTasksPackageFileIdentity> CompareNeutralAssemblyIdentity(
        IReadOnlyDictionary<string, CloudTasksPackageArtifact> candidateArtifacts,
        string baselineFeed,
        string candidateVersion)
    {
        var identities = new List<CloudTasksPackageFileIdentity>();
        foreach (var packageId in NeutralPackages)
        {
            if (!candidateArtifacts.TryGetValue(packageId, out var candidate))
                throw new PackageIndexException($"Candidate neutral artifact '{packageId}' is missing from the validated package report.");
            var baselinePath = Path.Combine(baselineFeed, $"{packageId}.{candidateVersion}.nupkg");
            if (!File.Exists(baselinePath)) throw new PackageIndexException($"Pinned baseline artifact '{packageId}' was not packed.");
            var baselineAssembly = ReadPackageAssemblyHashes(baselinePath);
            var candidateAssembly = ReadPackageAssemblyHashes(candidate.Path);
            var baselineNupkgSha512 = HashFileSha512(baselinePath);
            identities.Add(new CloudTasksPackageFileIdentity(packageId, baselineNupkgSha512, candidate.Sha512, baselineAssembly, candidateAssembly,
                CloudTasksNeutralPackageContract.Read(baselinePath, candidateVersion),
                CloudTasksNeutralPackageContract.Read(candidate.Path, candidateVersion)));
        }
        return identities;
    }

    private static IReadOnlyDictionary<string, CloudTasksPackageArtifact> CompareBaselineArtifacts(
        string baselineFeed,
        string version)
    {
        var result = new Dictionary<string, CloudTasksPackageArtifact>(StringComparer.OrdinalIgnoreCase);
        foreach (var packageId in NeutralPackages)
        {
            var path = Path.Combine(baselineFeed, $"{packageId}.{version}.nupkg");
            if (!File.Exists(path)) throw new PackageIndexException($"Pinned baseline artifact '{packageId}' was not packed.");
            result.Add(packageId, new CloudTasksPackageArtifact(packageId, path, HashFileSha512(path)));
        }
        return result;
    }

    private static void AssertGraphArtifacts(
        CloudTasksPackageGraph graph,
        IReadOnlyDictionary<string, CloudTasksPackageArtifact> artifacts,
        bool baseline)
    {
        foreach (var artifact in artifacts.Values)
        {
            var nodes = graph.Nodes.Values.Where(node => string.Equals(node.Id, artifact.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (nodes.Length != 1)
                throw new PackageIndexException($"The {(baseline ? "pinned baseline" : "candidate")} consumer graph must resolve validated package '{artifact.Id}' exactly once.");
            if (!string.Equals(nodes[0].Sha512, artifact.Sha512, StringComparison.Ordinal))
                throw new PackageIndexException($"The {(baseline ? "pinned baseline" : "candidate")} consumer graph resolved '{artifact.Id}' with a NuGet content hash different from its validated local archive.");
        }
    }

    private static string CanonicalizeDependency(KeyValuePair<string, string> dependency)
        => dependency.Key.StartsWith("ForgeTrust.AppSurface.", StringComparison.OrdinalIgnoreCase)
            ? dependency.Key.ToLowerInvariant() + ":<coordinated>"
            : dependency.Key.ToLowerInvariant() + ":" + dependency.Value.ToLowerInvariant();

    /// <summary>Preserves packed contracts and library entry sets after the required unchanged-source proof.</summary>
    internal static void AssertNeutralPackageContracts(IReadOnlyList<CloudTasksPackageFileIdentity> identities)
    {
        foreach (var file in identities)
        {
            if (!string.Equals(file.BaselineDependencyContract, file.CandidateDependencyContract, StringComparison.Ordinal))
                throw new PackageIndexException($"Neutral package '{file.PackageId}' packed dependency contract changed from the pinned baseline.");
            if (!file.BaselineAssemblySha256.Keys.Order(StringComparer.Ordinal).SequenceEqual(file.CandidateAssemblySha256.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                throw new PackageIndexException($"Neutral package '{file.PackageId}' library assembly entry set changed from the pinned baseline.");
        }
    }

    private static SortedDictionary<string, string> ReadPackageAssemblyHashes(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries.Where(static entry => entry.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)
                     && entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = entry.Open();
            result.Add(entry.FullName[(entry.FullName.IndexOf('/') + 1)..], Convert.ToHexString(SHA256.HashData(stream)));
        }
        if (result.Count == 0) throw new PackageIndexException($"Packed neutral package '{Path.GetFileName(packagePath)}' has no library assemblies to compare.");
        return result;
    }

    private static string HashFileSha512(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToBase64String(SHA512.HashData(stream));
    }

    internal static void AssertNeutralGraph(CloudTasksPackageGraph baseline, CloudTasksPackageGraph candidate)
    {
        foreach (var graph in new[] { baseline, candidate })
        {
            foreach (var node in graph.Nodes.Values)
                if (ForbiddenNeutralPrefixes.Any(prefix => node.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    throw new PackageIndexException($"Neutral Durable graph contains forbidden provider dependency '{node.Id}'.");
        }
        var baselineShape = baseline.Nodes.Values.Select(node => CanonicalizePackageId(node.Id + "/" + node.Version, "ignored") + "|" + string.Join(',', node.Dependencies)).Order(StringComparer.Ordinal).ToArray();
        var candidateShape = candidate.Nodes.Values.Select(node => CanonicalizePackageId(node.Id + "/" + node.Version, "ignored") + "|" + string.Join(',', node.Dependencies)).Order(StringComparer.Ordinal).ToArray();
        if (!baselineShape.SequenceEqual(candidateShape, StringComparer.Ordinal))
            throw new PackageIndexException("Neutral Durable packed dependency graph changed from the pinned baseline; inspect the cloudtasks proof report before retrying.");
        var baselineDirect = baseline.DirectDependencies.Select(CanonicalizeDependency).Order(StringComparer.Ordinal).ToArray();
        var candidateDirect = candidate.DirectDependencies.Select(CanonicalizeDependency).Order(StringComparer.Ordinal).ToArray();
        if (!baselineDirect.SequenceEqual(candidateDirect, StringComparer.Ordinal))
            throw new PackageIndexException("Neutral Durable direct package references changed from the pinned baseline.");
        var externalHashDrift = baseline.Nodes.Values.Join(candidate.Nodes.Values, node => node.Id.ToLowerInvariant(), node => node.Id.ToLowerInvariant(), (left, right) => (left, right))
            .Where(pair => !pair.left.Id.StartsWith("ForgeTrust.AppSurface.", StringComparison.OrdinalIgnoreCase) && !string.Equals(pair.left.Sha512, pair.right.Sha512, StringComparison.Ordinal))
            .Select(pair => pair.left.Id).ToArray();
        if (externalHashDrift.Length != 0) throw new PackageIndexException("Neutral Durable graph changed external package content hashes: " + string.Join(", ", externalHashDrift));
    }

    private static string Render(CloudTasksPackageConsumerProofReport report) => $"""
        # Cloud Tasks packed consumer proof

        Status: **passed**

        Candidate package SHA-512: `{report.CloudTasksPackageSha512}`
        Baseline source: `{report.BaselineCommit}` (`{report.BaselineMode}`)
        Baseline graph nodes: {report.BaselineGraph.Nodes.Count}
        Candidate graph nodes: {report.CandidateGraph.Nodes.Count}
        Cloud Tasks graph nodes: {report.CloudTasksGraph.Nodes.Count}
        Provider SDK API leaks: none
        Neutral Google/GAX/gRPC dependencies: none
        Neutral external content hashes: identical

        ## Neutral package archive hashes

        Authored neutral sources, their generator, and shared build inputs match the selected source snapshot. Candidate-source mode uses the candidate commit; historical-acceptance mode uses the unique merge-base of the requested target ref and candidate. The only central package differences allowed are the two Cloud Tasks SDK pins proven against the checked-in lock file. Packed framework/dependency contracts and library entry sets match. First-party archive and DLL byte differences are permitted only after source parity succeeds. Both raw hashes are retained below; differing binaries are not described as byte-identical. Every restored first-party package hash is bound to its exact local archive. External versions, hashes and edges remain exact.

        | Package | Baseline SHA-512 | Candidate SHA-512 |
        | --- | --- | --- |
        {string.Join(Environment.NewLine, report.NeutralPackageFiles.Select(file => $"| `{file.PackageId}` | `{file.BaselineNupkgSha512}` | `{file.CandidateNupkgSha512}` |"))}

        | Package / library | Baseline SHA-256 | Candidate SHA-256 |
        | --- | --- | --- |
        {string.Join(Environment.NewLine, report.NeutralPackageFiles.SelectMany(file => file.BaselineAssemblySha256.Select(assembly => $"| `{file.PackageId}/{assembly.Key}` | `{assembly.Value}` | `{file.CandidateAssemblySha256[assembly.Key]}` |")))}

        ## Unchanged neutral source and build inputs

        Source hashes cover both baseline and candidate as raw file hashes. The central package file is compared as exact XML after removing only the two approved GCP declarations from the candidate; its two raw hashes may therefore differ.

        | Authored input | SHA-256 |
        | --- | --- |
        {string.Join(Environment.NewLine, report.NeutralSourceSha256.Select(file => $"| `{file.Key}` | `{file.Value}` |"))}

        ## Cloud Tasks package dependency closure

        | Package | Version | NuGet content hash |
        | --- | --- | --- |
        {string.Join(Environment.NewLine, report.CloudTasksGraph.Nodes.Values.Select(node => $"| `{node.Id}` | `{node.Version}` | `{node.Sha512}` |"))}

        ## Neutral dependency graph

        | Package | Baseline version | Candidate version | Dependencies | External content hash |
        | --- | --- | --- | --- | --- |
        {string.Join(Environment.NewLine, report.BaselineGraph.Nodes.Values.Select(node => { var candidate = report.CandidateGraph.Nodes.Values.Single(item => string.Equals(item.Id, node.Id, StringComparison.OrdinalIgnoreCase)); return $"| `{node.Id}` | `{node.Version}` | `{candidate.Version}` | {string.Join(", ", candidate.Dependencies)} | `{(node.Id.StartsWith("ForgeTrust.AppSurface.", StringComparison.OrdinalIgnoreCase) ? "first-party package hash recorded separately" : node.Sha512)}` |"; }))}

        ## Neutral direct references

        Baseline: {string.Join(", ", report.BaselineGraph.DirectDependencies.Select(static dependency => $"`{dependency.Key}` `{dependency.Value}`"))}

        Candidate: {string.Join(", ", report.CandidateGraph.DirectDependencies.Select(static dependency => $"`{dependency.Key}` `{dependency.Value}`"))}
        """;

    private static string XmlEscape(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;
}

/// <summary>Inputs for validating a packed Cloud Tasks consumer and its paired neutral package graphs.</summary>
/// <param name="RepositoryRoot">Candidate source checkout root.</param>
/// <param name="ArtifactsDirectory">Directory containing the exact locally validated package archives.</param>
/// <param name="PackageVersion">Coordinated package version used by the validated artifacts and isolated consumers.</param>
/// <param name="Source">Configured package source for consumer restores.</param>
/// <param name="SourceCommit">Optional canonical candidate commit SHA; omitted resolves checked-out HEAD.</param>
/// <param name="BaselineRef">Optional fetched historical target ref; the unique merge-base with the candidate commit is required. Omitted uses the candidate source snapshot.</param>
internal sealed record CloudTasksPackageConsumerProofRequest(string RepositoryRoot, string ArtifactsDirectory, string PackageVersion, string Source, string? SourceCommit = null, string? BaselineRef = null);
internal sealed record CloudTasksPackageArtifact(string Id, string Path, string Sha512);
internal sealed record CloudTasksPackageGraph(SortedDictionary<string, CloudTasksPackageGraphNode> Nodes, SortedDictionary<string, string> DirectDependencies);
internal sealed record CloudTasksPackageGraphNode(string Id, string Version, string Sha512, IReadOnlyList<string> Dependencies);
internal sealed record CloudTasksPackageFileIdentity(string PackageId, string BaselineNupkgSha512, string CandidateNupkgSha512, IReadOnlyDictionary<string, string> BaselineAssemblySha256, IReadOnlyDictionary<string, string> CandidateAssemblySha256, string BaselineDependencyContract, string CandidateDependencyContract);
/// <summary>Evidence generated by the paired Cloud Tasks package consumer proof.</summary>
/// <param name="CloudTasksPackageSha512">Validated Cloud Tasks archive hash.</param>
/// <param name="BaselineCommit">Selected candidate snapshot or unique historical merge-base commit.</param>
/// <param name="BaselineMode">Either <c>candidate-source</c> or <c>historical-acceptance</c>.</param>
/// <param name="BaselineAssetsPath">Isolated restore assets path for the baseline neutral consumer.</param>
/// <param name="CandidateAssetsPath">Isolated restore assets path for the candidate neutral consumer.</param>
/// <param name="BaselineGraph">Resolved baseline neutral package graph.</param>
/// <param name="CandidateGraph">Resolved candidate neutral package graph.</param>
/// <param name="CloudTasksGraph">Resolved packed Cloud Tasks consumer graph.</param>
/// <param name="NeutralPackageFiles">Retained baseline and candidate archive/library hashes and packed contracts.</param>
/// <param name="NeutralSourceSha256">Raw source/build-input hashes for both sides of source parity.</param>
/// <param name="ReportPath">Path where the human-readable proof report was written.</param>
internal sealed record CloudTasksPackageConsumerProofReport(string CloudTasksPackageSha512, string BaselineCommit, string BaselineMode, string BaselineAssetsPath, string CandidateAssetsPath, CloudTasksPackageGraph BaselineGraph, CloudTasksPackageGraph CandidateGraph, CloudTasksPackageGraph CloudTasksGraph, IReadOnlyList<CloudTasksPackageFileIdentity> NeutralPackageFiles, IReadOnlyDictionary<string, string> NeutralSourceSha256, string ReportPath);
