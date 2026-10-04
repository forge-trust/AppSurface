using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Testing;
using Npgsql;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Proves the exact candidate CLI tool package against the real PostgreSQL doctor fixture.</summary>
public sealed class DurableDoctorInstalledToolTests
{
    private const string CliPackageId = "ForgeTrust.AppSurface.Cli";
    private const string PostgreSqlPackageId = "ForgeTrust.AppSurface.Durable.PostgreSql";
    private const string ProviderAssemblyName = "ForgeTrust.AppSurface.Durable.PostgreSql.dll";
    private const string ConnectionSecret = DurableDoctorFixture.PasswordSentinel;
    private static readonly TimeSpan PackTimeout = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DoctorTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan KillWaitTimeout = TimeSpan.FromSeconds(5);

    [Fact(Timeout = 600_000)]
    public async Task Installed_candidate_matches_exact_package_bytes_and_runs_the_real_diagnosis_matrix()
    {
        using var testDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(9));
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var cliProject = TestPathUtils.PathUnder(
            repositoryRoot, "Cli", "ForgeTrust.AppSurface.Cli", "ForgeTrust.AppSurface.Cli.csproj");
        var sourceProjects = FindProjectReferenceClosure(repositoryRoot, cliProject);
        var protectedInputs = CaptureProtectedInputs(repositoryRoot, cliProject, sourceProjects);
        var temporaryRoot = TestPathUtils.PathUnder(Path.GetTempPath(), $"appsurface-issue801-tool-{Guid.NewGuid():N}");
        var artifactsPath = TestPathUtils.PathUnder(temporaryRoot, "artifacts");
        var feedPath = TestPathUtils.PathUnder(temporaryRoot, "feed");
        var toolPath = TestPathUtils.PathUnder(temporaryRoot, "tool");
        var packagesPath = TestPathUtils.PathUnder(temporaryRoot, "packages");
        var cliHomePath = TestPathUtils.PathUnder(temporaryRoot, "cli-home");
        var nuGetConfigPath = TestPathUtils.PathUnder(temporaryRoot, "NuGet.Config");
        var candidateVersion = $"0.1.0-issue801.{Guid.NewGuid():N}";
        Directory.CreateDirectory(temporaryRoot);
        Directory.CreateDirectory(feedPath);
        Directory.CreateDirectory(toolPath);
        Directory.CreateDirectory(packagesPath);
        Directory.CreateDirectory(cliHomePath);

        try
        {
            await PackAndInstallCandidateAsync(
                repositoryRoot,
                cliProject,
                artifactsPath,
                feedPath,
                toolPath,
                packagesPath,
                cliHomePath,
                nuGetConfigPath,
                candidateVersion,
                sourceProjects,
                testDeadline.Token);

            AssertProtectedInputsUnchanged(protectedInputs, CaptureProtectedInputs(repositoryRoot, cliProject, sourceProjects));

            VerifyInstalledPackageIdentityAndDependencyBytes(
                temporaryRoot,
                feedPath,
                toolPath,
                packagesPath,
                artifactsPath,
                candidateVersion);

            await using var fixture = await DurableDoctorFixture.CreateAsync();
            await fixture.TrapPruneFunctionBodyAsync(testDeadline.Token);

            var matrixRows = DurableDoctorV1Matrix.Load();
            var installedExecutable = GetInstalledExecutable(toolPath);
            var customConnectionName = $"ISSUE801_{Guid.NewGuid():N}_CONNECTION";
            var customEpochName = $"ISSUE801_{Guid.NewGuid():N}_EPOCH";
            var processEnvironment = CreateBaseProcessEnvironment(cliHomePath, packagesPath);

            var executableCases = new[]
            {
                new InstalledScenario("D01", null, null, null, false),
                new InstalledScenario("D02", DurableDoctorFixture.RuntimeWorkerId, null, null, false),
                new InstalledScenario("D03", DurableDoctorFixture.RuntimeWorkerId, TimeSpan.FromSeconds(5), null, false),
                new InstalledScenario("D04", DurableDoctorFixture.RuntimeWorkerId, TimeSpan.FromSeconds(14), null, false),
                new InstalledScenario("D05", DurableDoctorFixture.RuntimeWorkerId, TimeSpan.FromSeconds(17), null, false),
                new InstalledScenario("D06", DurableDoctorFixture.RuntimeWorkerId, TimeSpan.Zero, null, true),
                new InstalledScenario("D07", DurableDoctorFixture.RuntimeWorkerId, TimeSpan.Zero, OtherEpoch, false, OtherEpoch),
                new InstalledScenario("D08", null, null, null, false, OtherEpoch),
                new InstalledScenario("D09", DurableDoctorFixture.RuntimeWorkerId, TimeSpan.Zero, OtherEpoch, false),
                new InstalledScenario("D10", DurableDoctorFixture.RuntimeWorkerId, TimeSpan.FromSeconds(-2), null, false),
                new InstalledScenario("D24", null, null, null, false, null, true),
                new InstalledScenario("D25", DurableDoctorFixture.RuntimeWorkerId, TimeSpan.Zero, null, false, null, true),
            };
            Assert.Equal(Enumerable.Range(1, 10).Select(static number => $"D{number:00}"),
                executableCases.Take(10).Select(static scenario => scenario.MatrixId));

            foreach (var scenario in executableCases)
            {
                testDeadline.Token.ThrowIfCancellationRequested();
                var row = Assert.Single(matrixRows, candidate => candidate.Id == scenario.MatrixId);
                await ConfigureScenarioAsync(fixture, scenario, testDeadline.Token);
                var before = await fixture.ReadDurableStateFingerprintAsync(testDeadline.Token);
                var connectionString = scenario.Unavailable
                    ? CreateUnavailableConnectionString(fixture.RuntimeConnectionString)
                    : fixture.RuntimeConnectionString;
                var run = await RunDoctorAsync(
                    installedExecutable,
                    toolPath,
                    processEnvironment,
                    customConnectionName,
                    customEpochName,
                    connectionString,
                    fixture.RuntimeEpoch,
                    scenario.WorkerId,
                    json: true,
                    extraArguments: [],
                    testDeadline.Token);

                AssertDoctorProcessOutput(run, row, connectionString, "json", customConnectionName, customEpochName,
                    scenario.WorkerId is null ? null : TimeSpan.FromSeconds(15));
                var after = await fixture.ReadDurableStateFingerprintAsync(testDeadline.Token);
                Assert.Equal(before, after);

                if (scenario.MatrixId is "D01" or "D06")
                {
                    var textRun = await RunDoctorAsync(
                        installedExecutable,
                        toolPath,
                        processEnvironment,
                        customConnectionName,
                        customEpochName,
                        fixture.RuntimeConnectionString,
                        fixture.RuntimeEpoch,
                        scenario.WorkerId,
                        json: false,
                        extraArguments: [],
                        testDeadline.Token);
                    AssertTextProcessOutput(textRun, row, fixture.RuntimeConnectionString, customConnectionName, customEpochName);
                    Assert.Equal(before, await fixture.ReadDurableStateFingerprintAsync(testDeadline.Token));
                }
            }

            var invalidRow = Assert.Single(matrixRows, static row => row.Id == "D28");
            var invalidBefore = await fixture.ReadDurableStateFingerprintAsync(testDeadline.Token);
            var invalidRun = await RunDoctorAsync(
                installedExecutable,
                toolPath,
                processEnvironment,
                customConnectionName,
                customEpochName,
                fixture.RuntimeConnectionString,
                fixture.RuntimeEpoch,
                workerId: null,
                json: true,
                extraArguments: ["--unknown-option"],
                testDeadline.Token);
            AssertDoctorProcessOutput(invalidRun, invalidRow, fixture.RuntimeConnectionString, "json", customConnectionName, customEpochName, null);
            Assert.Equal(invalidBefore, await fixture.ReadDurableStateFingerprintAsync(testDeadline.Token));

            var invalidTextRun = await RunDoctorAsync(
                installedExecutable, toolPath, processEnvironment,
                customConnectionName, customEpochName, fixture.RuntimeConnectionString, fixture.RuntimeEpoch,
                workerId: null, json: false, extraArguments: ["--unknown-issue801-sentinel"], testDeadline.Token);
            Assert.False(invalidTextRun.TimedOut);
            Assert.Equal(invalidRow.ExitCode, invalidTextRun.ExitCode);
            AssertOutputDoesNotExposeSecrets(invalidTextRun, fixture.RuntimeConnectionString);
            Assert.Empty(invalidTextRun.StandardError);
            Assert.StartsWith("Diagnosis: invalid-input (exit 3)\n", invalidTextRun.StandardOutput, StringComparison.Ordinal);
            Assert.EndsWith("\n", invalidTextRun.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("ASDUR413", invalidTextRun.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("'appsurface' 'durable' 'doctor' '--help'", invalidTextRun.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("--unknown-issue801-sentinel", invalidTextRun.StandardOutput, StringComparison.Ordinal);
            Assert.Equal(invalidBefore, await fixture.ReadDurableStateFingerprintAsync(testDeadline.Token));

            var contractRow = Assert.Single(matrixRows, static row => row.Id == "D27");
            await fixture.MutateAsync(
                "ALTER TABLE appsurface_durable.store_metadata RENAME COLUMN minimum_reader_version TO issue801_reader_contract_break",
                testDeadline.Token);
            try
            {
                var contractBefore = await fixture.ReadDurableStateFingerprintAsync(testDeadline.Token);
                var contractRun = await RunDoctorAsync(
                    installedExecutable,
                    toolPath,
                    processEnvironment,
                    customConnectionName,
                    customEpochName,
                    fixture.RuntimeConnectionString,
                    fixture.RuntimeEpoch,
                    workerId: null,
                    json: true,
                    extraArguments: [],
                    testDeadline.Token);
                AssertDoctorProcessOutput(contractRun, contractRow, fixture.RuntimeConnectionString, "json", customConnectionName, customEpochName, null);
                AssertTerminalCategories(contractRun.StandardOutput, contractRow);
                Assert.Equal(contractBefore, await fixture.ReadDurableStateFingerprintAsync(testDeadline.Token));
            }
            finally
            {
                await fixture.MutateAsync(
                    "ALTER TABLE appsurface_durable.store_metadata RENAME COLUMN issue801_reader_contract_break TO minimum_reader_version",
                    testDeadline.Token);
            }

            await AssertSchemaFindingFamiliesAsync(
                installedExecutable,
                toolPath,
                processEnvironment,
                customConnectionName,
                customEpochName,
                testDeadline.Token);

            await AssertRetentionAndCredentialFindingFamiliesAsync(
                installedExecutable,
                toolPath,
                processEnvironment,
                customConnectionName,
                customEpochName,
                testDeadline.Token);

            await AssertCallerCancellationAsync(
                fixture,
                installedExecutable,
                toolPath,
                processEnvironment,
                customConnectionName,
                customEpochName,
                testDeadline.Token);

            AssertProtectedInputsUnchanged(protectedInputs, CaptureProtectedInputs(repositoryRoot, cliProject, sourceProjects));
        }
        finally
        {
            TryDeleteTemporaryDirectory(temporaryRoot);
        }
    }

    private static async Task PackAndInstallCandidateAsync(
        string repositoryRoot,
        string cliProject,
        string artifactsPath,
        string feedPath,
        string toolPath,
        string packagesPath,
        string cliHomePath,
        string nuGetConfigPath,
        string candidateVersion,
        IReadOnlyList<string> sourceProjects,
        CancellationToken cancellationToken)
    {
        var baseEnvironment = CreateBaseProcessEnvironment(cliHomePath, packagesPath);
        var restore = CreateDotnetStartInfo(repositoryRoot, baseEnvironment);
        AddArguments(restore,
            "restore", cliProject,
            "--artifacts-path", artifactsPath,
            "--verbosity", "quiet",
            "--locked-mode",
            "--disable-parallel",
            "-m:1", "-nr:false",
            "-p:UseSharedCompilation=false");
        AssertProcessSucceeded(
            "isolated CLI candidate restore",
            await RunProcessAsync(restore, RestoreTimeout, cancellationToken));

        var pack = CreateDotnetStartInfo(repositoryRoot, baseEnvironment);
        AddArguments(pack,
            "pack", cliProject,
            "--configuration", "Release",
            "--no-restore",
            "--artifacts-path", artifactsPath,
            "--output", feedPath,
            "--verbosity", "quiet",
            "-m:1", "-nr:false",
            "-p:PackageVersion=" + candidateVersion,
            "-p:Version=" + candidateVersion,
            "-p:UseSharedCompilation=false");
        AssertProcessSucceeded(
            "isolated CLI candidate pack",
            await RunProcessAsync(pack, PackTimeout, cancellationToken));

        var packagePath = FindPackage(feedPath, CliPackageId, candidateVersion);
        VerifyNupkgIdentity(packagePath, CliPackageId, candidateVersion);
        await PackRequiredSourcePackagesAsync(
            repositoryRoot,
            sourceProjects,
            packagePath,
            artifactsPath,
            feedPath,
            baseEnvironment,
            candidateVersion,
            cancellationToken);

        var nugetConfig = new XDocument(
            new XElement("configuration",
                new XElement("packageSources",
                    new XElement("clear"),
                    new XElement("add", new XAttribute("key", "issue801-candidate-feed"), new XAttribute("value", feedPath)),
                    new XElement("add", new XAttribute("key", "nuget.org"), new XAttribute("value", "https://api.nuget.org/v3/index.json"))),
                new XElement("packageSourceMapping",
                    new XElement("packageSource", new XAttribute("key", "issue801-candidate-feed"),
                        new XElement("package", new XAttribute("pattern", "ForgeTrust.*"))),
                    new XElement("packageSource", new XAttribute("key", "nuget.org"),
                        new XElement("package", new XAttribute("pattern", "*"))))));
        nugetConfig.Save(nuGetConfigPath);

        var install = CreateDotnetStartInfo(repositoryRoot, baseEnvironment);
        AddArguments(install,
            "tool", "install", CliPackageId,
            "--tool-path", toolPath,
            "--version", candidateVersion,
            "--configfile", nuGetConfigPath);
        AssertProcessSucceeded(
            "isolated candidate tool install",
            await RunProcessAsync(install, InstallTimeout, cancellationToken));
    }

    private static IReadOnlyList<string> FindProjectReferenceClosure(string repositoryRoot, string rootProject)
    {
        var visited = new HashSet<string>(PathComparer);
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(rootProject));
        var projects = new List<string>();

        while (pending.TryPop(out var project))
        {
            if (!visited.Add(project))
            {
                continue;
            }

            var document = XDocument.Load(project);
            foreach (var include in document.Descendants()
                         .Where(static element => element.Name.LocalName == "ProjectReference")
                         .Select(static element => (string?)element.Attribute("Include")))
            {
                if (string.IsNullOrWhiteSpace(include) || include.Contains("$(", StringComparison.Ordinal))
                {
                    continue;
                }

                var normalizedInclude = include.Replace('\\', '/');
                var reference = ResolveProjectReference(repositoryRoot, Path.GetDirectoryName(project)!, normalizedInclude);
                if (!File.Exists(reference))
                {
                    continue;
                }

                pending.Push(reference);
            }

            if (!PathComparer.Equals(project, rootProject))
            {
                projects.Add(project);
            }
        }

        return projects
            .OrderBy(static project => project, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ResolveProjectReference(string repositoryRoot, string projectDirectory, string include)
    {
        if (Path.IsPathRooted(include))
        {
            throw new InvalidDataException("A source ProjectReference escaped its repository-relative form.");
        }

        var projectRelativeDirectory = Path.GetRelativePath(repositoryRoot, projectDirectory).Replace('\\', '/');
        var segments = projectRelativeDirectory == "."
            ? new List<string>()
            : projectRelativeDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var segment in include.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    throw new InvalidDataException("A source ProjectReference escaped the repository root.");
                }
                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return TestPathUtils.PathUnder(repositoryRoot, segments.ToArray());
    }

    private static bool IsPackableSourceProject(string project, string repositoryRoot)
    {
        var relative = Path.GetRelativePath(repositoryRoot, project).Replace('\\', '/');
        if (relative.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains(".Tests/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("examples/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/benchmarks/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var document = XDocument.Load(project);
        var falsePackability = document.Descendants()
            .Where(static element => element.Name.LocalName == "IsPackable")
            .Any(static element => bool.TryParse(element.Value.Trim(), out var isPackable) && !isPackable);
        if (falsePackability)
        {
            return false;
        }

        // RazorWire.Cli is a library project reference unless its independent tool-packaging switch is enabled.
        return !relative.Equals("Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task PackRequiredSourcePackagesAsync(
        string repositoryRoot,
        IReadOnlyList<string> sourceProjects,
        string cliPackage,
        string artifactsPath,
        string feedPath,
        IReadOnlyDictionary<string, string> environment,
        string candidateVersion,
        CancellationToken cancellationToken)
    {
        var packageProjects = sourceProjects
            .Where(project => IsPackableSourceProject(project, repositoryRoot))
            .ToDictionary(GetProjectPackageId, PathComparer);
        var pending = new Queue<string>(ReadPackageDependencyIds(cliPackage));
        pending.Enqueue(PostgreSqlPackageId);
        var packed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (pending.TryDequeue(out var packageId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!packageId.StartsWith("ForgeTrust.", StringComparison.OrdinalIgnoreCase)
                || !packed.Add(packageId)
                || File.Exists(TestPathUtils.PathUnder(feedPath, $"{packageId}.{candidateVersion}.nupkg")))
            {
                continue;
            }

            if (!packageProjects.TryGetValue(packageId, out var project))
            {
                throw new InvalidDataException($"The candidate CLI package depends on source package '{packageId}', but no matching project exists in its reference closure.");
            }

            var pack = CreateDotnetStartInfo(repositoryRoot, environment);
            AddArguments(pack,
                "pack", project,
                "--configuration", "Release",
                "--no-restore",
                "--artifacts-path", artifactsPath,
                "--output", feedPath,
                "--verbosity", "quiet",
                "-m:1", "-nr:false",
                "-p:PackageVersion=" + candidateVersion,
                "-p:Version=" + candidateVersion,
                "-p:UseSharedCompilation=false");
            AssertProcessSucceeded(
                $"isolated dependency pack for {packageId}",
                await RunProcessAsync(pack, PackTimeout, cancellationToken));

            var dependencyPackage = FindPackage(feedPath, packageId, candidateVersion);
            VerifyNupkgIdentity(dependencyPackage, packageId, candidateVersion);
            foreach (var dependencyId in ReadPackageDependencyIds(dependencyPackage))
            {
                pending.Enqueue(dependencyId);
            }
        }
    }

    private static string GetProjectPackageId(string project)
    {
        var document = XDocument.Load(project);
        var packageId = document.Descendants()
            .FirstOrDefault(static element => element.Name.LocalName == "PackageId")
            ?.Value.Trim();
        return string.IsNullOrWhiteSpace(packageId) ? Path.GetFileNameWithoutExtension(project) : packageId;
    }

    private static IReadOnlyList<string> ReadPackageDependencyIds(string packagePath)
    {
        var nuspec = ReadNuspec(packagePath);
        return nuspec.Descendants()
            .Where(static element => element.Name.LocalName == "dependency")
            .Select(static element => (string?)element.Attribute("id"))
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void VerifyInstalledPackageIdentityAndDependencyBytes(
        string temporaryRoot,
        string feedPath,
        string toolPath,
        string packagesPath,
        string artifactsPath,
        string candidateVersion)
    {
        var cliPackage = FindPackage(feedPath, CliPackageId, candidateVersion);
        VerifyPackageHashRoundTrip(cliPackage, packagesPath, CliPackageId, candidateVersion);

        var providerPackage = FindPackage(feedPath, PostgreSqlPackageId, candidateVersion);
        VerifyNupkgIdentity(providerPackage, PostgreSqlPackageId, candidateVersion);

        var cliEntries = ReadNupkgEntries(cliPackage);
        var providerEntries = ReadNupkgEntries(providerPackage);
        var bundledProvider = Assert.Single(cliEntries, static entry =>
            entry.Key.StartsWith("tools/", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(entry.Key), ProviderAssemblyName, StringComparison.Ordinal));
        var providerLibrary = Assert.Single(providerEntries, static entry =>
            entry.Key.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(entry.Key), ProviderAssemblyName, StringComparison.Ordinal));

        // Match the SDK's lowercase artifact pivot exactly, even on a case-insensitive host filesystem.
        var providerBuildRoot = TestPathUtils.PathUnder(artifactsPath, "bin", "ForgeTrust.AppSurface.Durable.PostgreSql");
        var providerReleaseDirectory = Assert.Single(Directory.EnumerateDirectories(providerBuildRoot),
            static directory => string.Equals(Path.GetFileName(directory), "release", StringComparison.Ordinal));
        var builtProviderAssembly = Directory.GetFiles(providerReleaseDirectory, ProviderAssemblyName, SearchOption.AllDirectories)
            .Single();
        var builtProviderHash = HashFile(builtProviderAssembly);
        Assert.Equal(builtProviderHash, HashBytes(bundledProvider.Value));
        Assert.Equal(builtProviderHash, HashBytes(providerLibrary.Value));

        var installedProviderAssembly = Directory.GetFiles(toolPath, ProviderAssemblyName, SearchOption.AllDirectories).Single();
        Assert.Equal(HashBytes(bundledProvider.Value), HashFile(installedProviderAssembly));

        var installedCliPackage = FindCachedPackage(temporaryRoot, packagesPath, CliPackageId, candidateVersion);
        Assert.Equal(HashFile(cliPackage), HashFile(installedCliPackage));
    }

    private static void VerifyPackageHashRoundTrip(string sourcePackage, string packagesPath, string packageId, string version)
    {
        var cachedPackage = FindCachedPackage(Path.GetDirectoryName(packagesPath)!, packagesPath, packageId, version);
        VerifyNupkgIdentity(cachedPackage, packageId, version);
        var sourceHash = HashFile(sourcePackage);
        var installedHash = HashFile(cachedPackage);
        Assert.Equal(sourceHash, installedHash);

        // Compare the archives themselves instead of treating NuGet cache metadata as candidate bytes.
    }

    private static string FindCachedPackage(string temporaryRoot, string packagesPath, string packageId, string version)
    {
        var expectedName = $"{packageId}.{version}.nupkg";
        var candidates = Directory.Exists(packagesPath)
            ? Directory.GetFiles(packagesPath, "*.nupkg", SearchOption.AllDirectories)
                .Where(path => string.Equals(Path.GetFileName(path), expectedName, StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : [];
        if (candidates.Length == 0 && Directory.Exists(temporaryRoot))
        {
            var feedPath = TestPathUtils.PathUnder(temporaryRoot, "feed");
            candidates = Directory.GetFiles(temporaryRoot, "*.nupkg", SearchOption.AllDirectories)
                .Where(path => string.Equals(Path.GetFileName(path), expectedName, StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.StartsWith(feedPath + Path.DirectorySeparatorChar, PathComparison))
                .ToArray();
        }

        return Assert.Single(candidates);
    }

    private static void VerifyNupkgIdentity(string packagePath, string expectedId, string expectedVersion)
    {
        var nuspec = ReadNuspec(packagePath);
        XNamespace namespaceName = nuspec.Root?.Name.Namespace ?? XNamespace.None;
        var metadata = Assert.IsType<XElement>(nuspec.Root?.Element(namespaceName + "metadata"));
        Assert.Equal(expectedId, (string?)metadata.Element(namespaceName + "id"));
        Assert.Equal(expectedVersion, (string?)metadata.Element(namespaceName + "version"));
    }

    private static IReadOnlyDictionary<string, byte[]> ReadNupkgEntries(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            using var contents = new MemoryStream();
            stream.CopyTo(contents);
            entries.Add(entry.FullName, contents.ToArray());
        }
        return entries;
    }

    private static XDocument ReadNuspec(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var entry = Assert.Single(archive.Entries, static candidate => candidate.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static string FindPackage(string feedPath, string packageId, string version)
    {
        var path = TestPathUtils.PathUnder(feedPath, $"{packageId}.{version}.nupkg");
        Assert.True(File.Exists(path), $"The isolated feed is missing {packageId}/{version}.");
        return path;
    }

    private static string GetInstalledExecutable(string toolPath)
    {
        var name = OperatingSystem.IsWindows() ? "appsurface.exe" : "appsurface";
        var executable = TestPathUtils.PathUnder(toolPath, name);
        Assert.True(File.Exists(executable), "The isolated candidate install did not create the appsurface tool shim.");
        return executable;
    }

    private static IReadOnlyDictionary<string, string> CaptureProtectedInputs(
        string repositoryRoot,
        string cliProject,
        IReadOnlyList<string> sourceProjects)
    {
        var paths = new HashSet<string>(PathComparer) { cliProject };
        var cliLockFile = TestPathUtils.PathUnder(Path.GetDirectoryName(cliProject)!, "packages.lock.json");
        if (File.Exists(cliLockFile))
        {
            paths.Add(cliLockFile);
        }

        foreach (var project in sourceProjects.Prepend(cliProject))
        {
            paths.Add(project);
            var lockFile = TestPathUtils.PathUnder(Path.GetDirectoryName(project)!, "packages.lock.json");
            if (File.Exists(lockFile))
            {
                paths.Add(lockFile);
            }

            var projectDirectory = Path.GetDirectoryName(project)!;
            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(projectDirectory);
            while (pendingDirectories.TryPop(out var directory))
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(child);
                    if (name is not ("bin" or "obj" or ".git" or ".vs" or "node_modules" or "TestResults" or "artifacts"))
                    {
                        pendingDirectories.Push(child);
                    }
                }

                foreach (var source in Directory.EnumerateFiles(directory))
                {
                    var extension = Path.GetExtension(source);
                    if (extension is ".cs" or ".csproj" or ".props" or ".targets" or ".json" or ".sql" or ".resx"
                        or ".xml" or ".config" or ".md" or ".txt")
                    {
                        paths.Add(source);
                    }
                }
            }
        }

        foreach (var relativePath in new[]
        {
            "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "NuGet.Config", "global.json",
        })
        {
            var path = TestPathUtils.PathUnder(repositoryRoot, relativePath);
            if (File.Exists(path))
            {
                paths.Add(path);
            }
        }

        return paths.ToDictionary(static path => path, HashFile, PathComparer);
    }

    private static void AssertProtectedInputsUnchanged(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after)
    {
        Assert.Equal(before.Keys.Order(PathComparer), after.Keys.Order(PathComparer));
        foreach (var (path, hash) in before)
        {
            Assert.Equal(hash, after[path]);
        }
    }

    private static async Task AssertSchemaFindingFamiliesAsync(
        string executable,
        string toolPath,
        IReadOnlyDictionary<string, string> environment,
        string connectionEnvironmentName,
        string epochEnvironmentName,
        CancellationToken cancellationToken)
    {
        var families = new (string[] MatrixIds, string Mutation)[]
        {
            (["D11", "D12"], "DROP SCHEMA appsurface_durable CASCADE"),
            (["D13", "D14"], "DELETE FROM appsurface_durable.schema_migration WHERE version = 11; UPDATE appsurface_durable.store_metadata SET schema_version = 10, minimum_reader_version = 10, maximum_reader_version = 10, minimum_writer_version = 10, maximum_writer_version = 10 WHERE singleton"),
            (["D15", "D16"], "UPDATE appsurface_durable.store_metadata SET minimum_reader_version = 1, maximum_reader_version = 10, minimum_writer_version = 1, maximum_writer_version = 10 WHERE singleton"),
            (["D17", "D18"], "UPDATE appsurface_durable.schema_migration SET sha256 = repeat('f', 64) WHERE version = 11"),
        };
        var matrixRows = DurableDoctorV1Matrix.Load();

        foreach (var (matrixIds, mutation) in families)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var fixture = await DurableDoctorFixture.CreateAsync();
            await fixture.TrapPruneFunctionBodyAsync(cancellationToken);
            await fixture.MutateAsync(mutation, cancellationToken);

            foreach (var matrixId in matrixIds)
            {
                var row = Assert.Single(matrixRows, candidate => candidate.Id == matrixId);
                await AssertInstalledMatrixRowAsync(
                    fixture,
                    row,
                    executable,
                    toolPath,
                    environment,
                    connectionEnvironmentName,
                    epochEnvironmentName,
                    cancellationToken);
            }
        }
    }

    private static async Task AssertRetentionAndCredentialFindingFamiliesAsync(
        string executable,
        string toolPath,
        IReadOnlyDictionary<string, string> environment,
        string connectionEnvironmentName,
        string epochEnvironmentName,
        CancellationToken cancellationToken)
    {
        var families = new (string[] MatrixIds, string Mutation)[]
        {
            (["D19"], "ALTER FUNCTION appsurface_durable.prune_runtime_heartbeats(interval,integer,text,uuid) RENAME TO issue801_missing_prune"),
            (["D20"], "DROP INDEX appsurface_durable.ix_runtime_heartbeat_retention"),
            (["D21"], "DROP INDEX appsurface_durable.ix_runtime_heartbeat_retention; CREATE INDEX ix_runtime_heartbeat_retention ON appsurface_durable.runtime_heartbeat (last_heartbeat_at DESC, worker_id)"),
            (["D22", "D23"], ""),
        };
        var matrixRows = DurableDoctorV1Matrix.Load();

        foreach (var (matrixIds, mutation) in families)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var fixture = await DurableDoctorFixture.CreateAsync();
            await fixture.TrapPruneFunctionBodyAsync(cancellationToken);
            if (matrixIds[0] == "D22")
            {
                await fixture.MutateAsync($"GRANT DELETE ON appsurface_durable.runtime_heartbeat TO \"{fixture.RuntimeRole}\"", cancellationToken);
            }
            else if (!string.IsNullOrEmpty(mutation))
            {
                await fixture.MutateAsync(mutation, cancellationToken);
            }

            if (matrixIds[0] == "D20")
            {
                await fixture.SeedHeartbeatAsync(age: TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);
            }
            else if (matrixIds[0] == "D21")
            {
                await fixture.MutateAsync("DELETE FROM appsurface_durable.runtime_heartbeat", cancellationToken);
            }

            foreach (var matrixId in matrixIds)
            {
                var row = Assert.Single(matrixRows, candidate => candidate.Id == matrixId);
                await AssertInstalledMatrixRowAsync(
                    fixture,
                    row,
                    executable,
                    toolPath,
                    environment,
                    connectionEnvironmentName,
                    epochEnvironmentName,
                    cancellationToken);
            }
        }
    }

    private static async Task AssertInstalledMatrixRowAsync(
        DurableDoctorFixture fixture,
        DurableDoctorMatrixRow row,
        string executable,
        string toolPath,
        IReadOnlyDictionary<string, string> environment,
        string connectionEnvironmentName,
        string epochEnvironmentName,
        CancellationToken cancellationToken)
    {
        var before = await fixture.ReadDurableStateFingerprintAsync(cancellationToken);
        var connectionString = fixture.RuntimeConnectionString;
        var run = await RunDoctorAsync(
            executable,
            toolPath,
            environment,
            connectionEnvironmentName,
            epochEnvironmentName,
            connectionString,
            fixture.RuntimeEpoch,
            row.WorkerPair ? DurableDoctorFixture.RuntimeWorkerId : null,
            json: true,
            extraArguments: [],
            cancellationToken);
        AssertDoctorProcessOutput(
            run,
            row,
            connectionString,
            "json",
            connectionEnvironmentName,
            epochEnvironmentName,
            row.WorkerPair ? TimeSpan.FromSeconds(15) : null);
        if (row.TerminalCategories.Count != 0)
        {
            AssertTerminalCategories(run.StandardOutput, row);
        }
        Assert.Equal(before, await fixture.ReadDurableStateFingerprintAsync(cancellationToken));
    }

    private static async Task ConfigureScenarioAsync(
        DurableDoctorFixture fixture,
        InstalledScenario scenario,
        CancellationToken cancellationToken)
    {
        await fixture.MutateAsync($"DELETE FROM appsurface_durable.runtime_heartbeat; UPDATE appsurface_durable.store_metadata SET active_runtime_epoch = '{fixture.RuntimeEpoch:D}' WHERE singleton", cancellationToken);
        if (scenario.StoreEpoch is { } storeEpoch)
        {
            await fixture.MutateAsync(
                $"UPDATE appsurface_durable.store_metadata SET active_runtime_epoch = '{storeEpoch:D}' WHERE singleton",
                cancellationToken);
        }

        if (scenario.WorkerId is not null && scenario.Age is { } age)
        {
            await fixture.SeedHeartbeatAsync(
                scenario.WorkerId,
                age,
                scenario.RuntimeEpoch,
                scenario.Draining,
                cancellationToken);
        }
    }

    private static string CreateUnavailableConnectionString(string baseConnectionString) =>
        new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Host = "127.0.0.1",
            Port = 1,
            Pooling = false,
            Timeout = 1,
            CommandTimeout = 1,
        }.ConnectionString;

    private static async Task<ProcessResult> RunDoctorAsync(
        string executable,
        string workingDirectory,
        IReadOnlyDictionary<string, string> baseEnvironment,
        string connectionEnvironmentName,
        string epochEnvironmentName,
        string connectionString,
        Guid runtimeEpoch,
        string? workerId,
        bool json,
        IReadOnlyList<string> extraArguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var (name, value) in baseEnvironment)
        {
            startInfo.Environment[name] = value;
        }

        startInfo.Environment.Remove("APPSURFACE_DURABLE_CONNECTION");
        startInfo.Environment.Remove("APPSURFACE_DURABLE_RUNTIME_EPOCH");
        startInfo.Environment[connectionEnvironmentName] = connectionString;
        startInfo.Environment[epochEnvironmentName] = runtimeEpoch.ToString("D");

        AddArguments(startInfo,
            "durable", "doctor",
            "--connection-env", connectionEnvironmentName,
            "--runtime-epoch-env", epochEnvironmentName,
            "--format", json ? "json" : "text",
            "--timeout", "10s");
        if (workerId is not null)
        {
            AddArguments(startInfo, "--worker-id", workerId, "--stale-after", "15s");
        }
        AddArguments(startInfo, extraArguments.ToArray());

        Assert.DoesNotContain(connectionString, startInfo.ArgumentList, StringComparer.Ordinal);
        Assert.DoesNotContain(ConnectionSecret, startInfo.ArgumentList, StringComparer.Ordinal);
        return await RunProcessAsync(startInfo, DoctorTimeout, cancellationToken);
    }

    private static void AssertDoctorProcessOutput(
        ProcessResult run,
        DurableDoctorMatrixRow row,
        string connectionString,
        string format,
        string connectionEnvironmentName,
        string epochEnvironmentName,
        TimeSpan? staleAfter)
    {
        Assert.False(run.TimedOut, "The installed doctor child process exceeded its finite timeout and was terminated.");
        Assert.Equal(row.ExitCode, run.ExitCode);
        AssertOutputDoesNotExposeSecrets(run, connectionString);
        Assert.True(string.IsNullOrWhiteSpace(run.StandardError), "The installed doctor emitted unexpected stderr output.");
        Assert.True(run.StandardOutput.EndsWith('\n'), "The installed doctor output must end with a newline.");

        if (format == "text")
        {
            foreach (var code in row.ExpectedCodes)
            {
                Assert.Contains(code, run.StandardOutput, StringComparison.Ordinal);
            }
            return;
        }

        using var document = JsonDocument.Parse(run.StandardOutput);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(row.Scenario == "caller-canceled" ? "canceled" : StatusForExit(row.ExitCode),
            root.GetProperty("status").GetString());
        Assert.Equal(row.ExitCode, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(row.ExpectedChecks.Select(ToStatus).ToArray(),
            root.GetProperty("requestedChecks").EnumerateArray()
                .Select(static check => check.GetProperty("status").GetString()!)
                .ToArray());
        Assert.Equal(row.WorkerPair,
            root.GetProperty("requestedChecks").EnumerateArray().Last().GetProperty("requested").GetBoolean());
        Assert.Equal(row.ExpectedCodes,
            root.GetProperty("findings").EnumerateArray()
                .Select(static finding => finding.GetProperty("code").GetString()!)
                .ToArray());
        AssertNextAction(
            row,
            root.GetProperty("nextAction"),
            connectionEnvironmentName,
            epochEnvironmentName,
            staleAfter,
            format);
        if (row.TerminalCategories.Count != 0)
        {
            AssertTerminalCategories(run.StandardOutput, row);
        }
    }

    private static void AssertTextProcessOutput(
        ProcessResult run,
        DurableDoctorMatrixRow row,
        string connectionString,
        string connectionEnvironmentName,
        string epochEnvironmentName)
    {
        Assert.False(run.TimedOut, "The installed text-mode doctor child process exceeded its finite timeout and was terminated.");
        Assert.Equal(row.ExitCode, run.ExitCode);
        AssertOutputDoesNotExposeSecrets(run, connectionString);
        Assert.True(string.IsNullOrWhiteSpace(run.StandardError), "The installed text-mode doctor emitted unexpected stderr output.");
        Assert.True(run.StandardOutput.EndsWith('\n'), "The installed text-mode doctor output must end with a newline.");
        foreach (var code in row.ExpectedCodes)
        {
            Assert.Contains(code, run.StandardOutput, StringComparison.Ordinal);
        }
        if (row.NextAction == "verify")
        {
            Assert.Contains("Next action: Run the application's composition verifier", run.StandardOutput, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(connectionEnvironmentName, run.StandardOutput, StringComparison.Ordinal);
            Assert.Contains(epochEnvironmentName, run.StandardOutput, StringComparison.Ordinal);
        }
    }

    private static void AssertNextAction(
        DurableDoctorMatrixRow row,
        JsonElement action,
        string connectionEnvironmentName,
        string epochEnvironmentName,
        TimeSpan? staleAfter,
        string format)
    {
        var expectedKind = row.NextAction == "verify" ? "application-verifier" : "command";
        Assert.Equal(expectedKind, action.GetProperty("kind").GetString());
        var command = action.GetProperty("command");
        if (row.NextAction == "verify")
        {
            Assert.Equal(JsonValueKind.Null, command.ValueKind);
            return;
        }

        Assert.Equal("appsurface", command.GetProperty("executable").GetString());
        var arguments = command.GetProperty("arguments").EnumerateArray()
            .Select(static argument => argument.GetString()!).ToArray();
        if (row.NextAction == "status")
        {
            Assert.Equal(["durable", "schema", "status", "--connection-env", connectionEnvironmentName], arguments);
            return;
        }
        if (row.NextAction == "help")
        {
            Assert.Equal(["durable", "doctor", "--help"], arguments);
            return;
        }

        Assert.Contains("durable", arguments);
        Assert.Contains("doctor", arguments);
        AssertArgumentPair(arguments, "--connection-env", connectionEnvironmentName);
        AssertArgumentPair(arguments, "--runtime-epoch-env", epochEnvironmentName);
        AssertArgumentPair(arguments, "--format", format);
        AssertArgumentPair(arguments, "--timeout", "10s");
        if (row.WorkerPair)
        {
            AssertArgumentPair(arguments, "--worker-id", DurableDoctorFixture.RuntimeWorkerId);
            AssertArgumentPair(arguments, "--stale-after", FormatSeconds(staleAfter
                ?? throw new Xunit.Sdk.XunitException("A paired matrix row requires a stale threshold.")));
        }
    }

    private static void AssertArgumentPair(IReadOnlyList<string> arguments, string name, string value)
    {
        var index = -1;
        for (var candidate = 0; candidate < arguments.Count; candidate++)
        {
            if (string.Equals(arguments[candidate], name, StringComparison.Ordinal))
            {
                index = candidate;
                break;
            }
        }
        Assert.True(index >= 0 && index + 1 < arguments.Count, $"The installed doctor's suggested command omitted {name}.");
        Assert.Equal(value, arguments[index + 1]);
    }

    private static void AssertTerminalCategories(string json, DurableDoctorMatrixRow row)
    {
        using var document = JsonDocument.Parse(json);
        var categories = document.RootElement.GetProperty("findings")[0]
            .GetProperty("failedChecks").EnumerateArray()
            .Select(static category => category.GetString()!).ToArray();
        Assert.NotEmpty(categories);
        Assert.All(categories, category => Assert.Contains(category, row.TerminalCategories));
    }

    private static string StatusForExit(int exitCode) => exitCode switch
    {
        0 => "passed",
        1 => "failed",
        2 => "findings",
        3 => "invalid-input",
        4 => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(exitCode)),
    };

    private static string ToStatus(string state) => state switch
    {
        "P" => "passed",
        "F" => "finding",
        "NC" => "not-checked",
        "NR" => "not-requested",
        _ => throw new InvalidDataException("The shared doctor matrix contains an unknown check state."),
    };

    private static void AssertOutputDoesNotExposeSecrets(ProcessResult run, string connectionString)
    {
        var output = run.StandardOutput + run.StandardError;
        Assert.False(output.Contains(ConnectionSecret, StringComparison.Ordinal), "An installed doctor process stream exposed the password sentinel.");
        Assert.False(output.Contains(connectionString, StringComparison.Ordinal), "An installed doctor process stream exposed its connection string.");
        Assert.False(output.Contains("durable-doctor-prune-invocation-sentinel", StringComparison.Ordinal), "An installed doctor process exposed the prune trap exception.");
        Assert.False(output.Contains("CliFx", StringComparison.Ordinal), "An installed doctor process exposed a framework parser error.");
    }

    private static async Task AssertCallerCancellationAsync(
        DurableDoctorFixture fixture,
        string executable,
        string toolPath,
        IReadOnlyDictionary<string, string> baseEnvironment,
        string connectionEnvironmentName,
        string epochEnvironmentName,
        CancellationToken cancellationToken)
    {
        Assert.False(OperatingSystem.IsWindows(),
            "The installed SIGINT proof requires the Unix release-evidence lane; a skipped interrupt cannot prove D26.");

        var matrixRow = Assert.Single(DurableDoctorV1Matrix.Load(), static row => row.Id == "D26");
        var applicationName = $"doctor-installed-cancel-{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.RuntimeConnectionString)
        {
            ApplicationName = applicationName,
        }.ConnectionString;
        var fingerprintBefore = await fixture.ReadDurableStateFingerprintAsync(cancellationToken);
        await using var lockConnection = new NpgsqlConnection(fixture.AdministrativeConnectionString);
        await lockConnection.OpenAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_lock(@key)", lockConnection))
        {
            lockCommand.Parameters.AddWithValue("key", DurableDoctorService.MigrationAdvisoryLock);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var startInfo = CreateDoctorStartInfo(
            executable,
            toolPath,
            baseEnvironment,
            connectionEnvironmentName,
            epochEnvironmentName,
            connectionString,
            fixture.RuntimeEpoch,
            workerId: null,
            json: true);
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        Assert.True(process.Start(), "The installed doctor cancellation child process could not start.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await WaitForAdvisoryLockWaiterAsync(
                fixture,
                applicationName,
                TimeSpan.FromSeconds(7),
                cancellationToken);
            var interrupt = new ProcessStartInfo("kill")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            AddArguments(interrupt, "-INT", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var interruptResult = await RunProcessAsync(interrupt, TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(interruptResult.TimedOut, "The installed doctor interrupt signal helper timed out.");
            Assert.Equal(0, interruptResult.ExitCode);

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(DoctorTimeout);
            try
            {
                await process.WaitForExitAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new Xunit.Sdk.XunitException("The installed doctor did not finish after caller cancellation.");
            }

            var output = await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(KillWaitTimeout, cancellationToken);
            var canceledRun = new ProcessResult(process.ExitCode, output[0], output[1], TimedOut: false);
            AssertDoctorProcessOutput(
                canceledRun,
                matrixRow,
                connectionString,
                "json",
                connectionEnvironmentName,
                epochEnvironmentName,
                staleAfter: null);
            Assert.Equal(fingerprintBefore, await fixture.ReadDurableStateFingerprintAsync(cancellationToken));
        }
        finally
        {
            if (!process.HasExited)
            {
                TryKill(process);
                using var stopWait = new CancellationTokenSource(KillWaitTimeout);
                try
                {
                    await process.WaitForExitAsync(stopWait.Token);
                }
                catch (OperationCanceledException)
                {
                    // The process was killed; do not strand an owned child if its runtime cannot be reaped.
                }
            }
        }
    }

    private static async Task WaitForAdvisoryLockWaiterAsync(
        DurableDoctorFixture fixture,
        string applicationName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(fixture.AdministrativeConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        while (true)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS
                (
                    SELECT 1
                    FROM pg_catalog.pg_stat_activity AS activity
                    JOIN pg_catalog.pg_locks AS lock ON lock.pid = activity.pid
                    WHERE activity.application_name = @application_name
                      AND activity.datname = current_database()
                      AND lock.locktype = 'advisory'
                      AND NOT lock.granted
                )
                """, connection);
            command.Parameters.AddWithValue("application_name", applicationName);
            if (await command.ExecuteScalarAsync(deadline.Token) is true)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token);
        }
    }

    private static ProcessStartInfo CreateDoctorStartInfo(
        string executable,
        string workingDirectory,
        IReadOnlyDictionary<string, string> baseEnvironment,
        string connectionEnvironmentName,
        string epochEnvironmentName,
        string connectionString,
        Guid runtimeEpoch,
        string? workerId,
        bool json)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var (name, value) in baseEnvironment)
        {
            startInfo.Environment[name] = value;
        }
        startInfo.Environment.Remove("APPSURFACE_DURABLE_CONNECTION");
        startInfo.Environment.Remove("APPSURFACE_DURABLE_RUNTIME_EPOCH");
        startInfo.Environment[connectionEnvironmentName] = connectionString;
        startInfo.Environment[epochEnvironmentName] = runtimeEpoch.ToString("D");
        AddArguments(startInfo,
            "durable", "doctor",
            "--connection-env", connectionEnvironmentName,
            "--runtime-epoch-env", epochEnvironmentName,
            "--format", json ? "json" : "text",
            "--timeout", "10s");
        if (workerId is not null)
        {
            AddArguments(startInfo, "--worker-id", workerId, "--stale-after", "15s");
        }
        Assert.DoesNotContain(connectionString, startInfo.ArgumentList, StringComparer.Ordinal);
        Assert.DoesNotContain(ConnectionSecret, startInfo.ArgumentList, StringComparer.Ordinal);
        return startInfo;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException("A bounded installed-tool child process did not start.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var timedOut = false;
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!process.HasExited)
            {
                timedOut = true;
                TryKill(process);
                using var reap = new CancellationTokenSource(KillWaitTimeout);
                try
                {
                    await process.WaitForExitAsync(reap.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException("An installed-tool child process could not be reaped after termination.");
                }
            }

            var output = await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(KillWaitTimeout, cancellationToken);
            return new ProcessResult(process.ExitCode, output[0], output[1], timedOut);
        }
        catch
        {
            if (!process.HasExited)
            {
                TryKill(process);
                using var reap = new CancellationTokenSource(KillWaitTimeout);
                try
                {
                    await process.WaitForExitAsync(reap.Token);
                }
                catch (OperationCanceledException)
                {
                    // Preserve the original process/test failure after making a bounded kill attempt.
                }
            }
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and Kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The process is already exiting or the platform declined a duplicate kill.
        }
    }

    private static void AssertProcessSucceeded(string description, ProcessResult result)
    {
        Assert.False(result.TimedOut, $"The {description} child process exceeded its finite timeout and was terminated.");
        Assert.True(result.ExitCode == 0, $"The {description} child process failed with exit code {result.ExitCode}. Output: {TrimForFailure(result.StandardOutput)} Error: {TrimForFailure(result.StandardError)}");
    }

    private static ProcessStartInfo CreateDotnetStartInfo(string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        return startInfo;
    }

    private static Dictionary<string, string> CreateBaseProcessEnvironment(string cliHomePath, string packagesPath) => new(StringComparer.Ordinal)
    {
        ["DOTNET_CLI_HOME"] = cliHomePath,
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        ["NUGET_PACKAGES"] = packagesPath,
        ["NUGET_HTTP_CACHE_PATH"] = TestPathUtils.PathUnder(Path.GetDirectoryName(packagesPath)!, "http-cache"),
    };

    private static void AddArguments(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    private static string HashFile(string path) => HashBytes(File.ReadAllBytes(path));

    private static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA512.HashData(bytes));

    private static string FormatSeconds(TimeSpan value) =>
        value.Ticks % TimeSpan.TicksPerSecond == 0
            ? $"{value.Ticks / TimeSpan.TicksPerSecond}s"
            : $"{value.TotalSeconds.ToString("0.#######", System.Globalization.CultureInfo.InvariantCulture)}s";

    private static string TrimForFailure(string value)
    {
        var sanitized = value.Replace(ConnectionSecret, "<redacted-password>", StringComparison.Ordinal);
        return sanitized.Length <= 2000 ? sanitized : sanitized[..2000];
    }

    private static void TryDeleteTemporaryDirectory(string path)
    {
        if (!Directory.Exists(path)
            || !Path.GetFileName(path).StartsWith("appsurface-issue801-tool-", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup after all child processes have been reaped.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup after all child processes have been reaped.
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly Guid OtherEpoch = Guid.Parse("d6e5279f-1c12-447c-a44d-3d4c5b45998d");

    private sealed record InstalledScenario(
        string MatrixId,
        string? WorkerId,
        TimeSpan? Age,
        Guid? RuntimeEpoch,
        bool Draining,
        Guid? StoreEpoch = null,
        bool Unavailable = false);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);
}
