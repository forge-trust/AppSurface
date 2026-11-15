using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Coverage;

internal static class Program
{
    private const int MaximumCapturedCharacters = 256 * 1024;
    private static readonly TimeSpan IdentityCommandTimeout = TimeSpan.FromSeconds(30);

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: Fixture <fresh-output-root> <installed-appsurface-command>");
            return 2;
        }

        try
        {
            return await RunAsync(Path.GetFullPath(args[0]), Path.GetFullPath(args[1])).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException
            or JsonException or OperationCanceledException or TimeoutException)
        {
            Console.Error.WriteLine($"Installed collection fixture failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(string root, string installedCli)
    {
        Directory.CreateDirectory(root);
        var repository = Path.Join(root, "source");
        if (Directory.Exists(repository) || File.Exists(repository))
            throw new IOException("The fixture source destination must be fresh.");

        await CreateSourceAsync(repository).ConfigureAwait(false);
        await InitializeSourceAsync(repository).ConfigureAwait(false);

        var sdk = await RunCommandAsync(repository, "dotnet", ["--version"], IdentityCommandTimeout)
            .ConfigureAwait(false);
        var commit = await RunCommandAsync(repository, "git", ["rev-parse", "HEAD"], IdentityCommandTimeout)
            .ConfigureAwait(false);
        var tree = await RunCommandAsync(repository, "git", ["rev-parse", "HEAD^{tree}"], IdentityCommandTimeout)
            .ConfigureAwait(false);
        var status = await RunCommandAsync(repository, "git", ["status", "--porcelain", "--untracked-files=all"], IdentityCommandTimeout)
            .ConfigureAwait(false);
        if (status.Length != 0)
            throw new InvalidOperationException("Generated fixture source must be clean before collection.");

        var testProject = "checks/Branchy.Tests/Branchy.Tests.csproj";
        var nativeTestProject = "checks/Branchy.Native.Tests/Branchy.Native.Tests.csproj";
        var nativeProjects = new[] { testProject, nativeTestProject }.Order(StringComparer.Ordinal).ToArray();
        var runId = Guid.NewGuid().ToString("N");
        var policy = new CoverageFanInPolicy(
            new CoverageSourceIdentity(commit, tree, sdk, "Release"),
            new CoverageRunIdentity("local", "installed-package-dogfood", runId, 1),
            nativeProjects,
            [new CoverageSupplement("native", CurrentPlatform(), "FullyQualifiedName~DecisionTests", nativeProjects)],
            new CoverageGatePolicy(CoverageGateMode.AggregateOnly));

        var runRoot = Path.Join(root, "runs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        var artifactHome = Path.Join(root, "artifact-store");
        var packageCache = Path.Join(root, "test-package-cache");
        var cliHome = Path.Join(root, "test-cli-home");
        var httpCache = Path.Join(root, "test-http-cache");
        Directory.CreateDirectory(packageCache);
        Directory.CreateDirectory(cliHome);
        Directory.CreateDirectory(httpCache);
        Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", artifactHome);
        Environment.SetEnvironmentVariable("NUGET_PACKAGES", packageCache);
        Environment.SetEnvironmentVariable("NUGET_HTTP_CACHE_PATH", httpCache);
        Environment.SetEnvironmentVariable("DOTNET_CLI_HOME", cliHome);
        Environment.SetEnvironmentVariable("DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER", "1");

        var capture = Path.Join(runRoot, "primary-capture");
        var collection = Path.Join(runRoot, "primary-collection");
        var nativeCapture = Path.Join(runRoot, "native-capture");
        var nativeCollection = Path.Join(runRoot, "native-collection");
        var nativeTransport = Path.Join(runRoot, "transport", "native");
        var composition = Path.Join(runRoot, "composition");
        var accepted = Path.Join(runRoot, "accepted");

        var primary = await RecordAsync("collect", runRoot, (token, progress, diagnostics) =>
            CoverageFanInApiExample.CollectAndCaptureAsync(policy, repository, capture, collection,
                Path.Join(repository, "Consumer.slnx"), "primary", 240, token)).ConfigureAwait(false);
        PrintResult(primary);
        if (primary.Outcome != CoverageOperationOutcome.Succeeded
            || primary.Observation != CoverageObservation.CollectedExecution
            || !primary.Effects.GenerationCommitted
            || !primary.Artifacts.Any(artifact => artifact.Role == "coverage.cobertura.xml")
            || !primary.Artifacts.Any(artifact => artifact.Role == "timings.json"))
            return 1;

        var timingsPath = Path.Join(capture, "timings.json");
        using (var timings = JsonDocument.Parse(await File.ReadAllBytesAsync(timingsPath).ConfigureAwait(false)))
        {
            var projects = timings.RootElement.GetProperty("projects").EnumerateArray().ToArray();
            if (projects.Length != nativeProjects.Length
                || !projects.Select(project => project.GetProperty("project").GetString()).SequenceEqual(nativeProjects)
                || projects.Any(project => project.GetProperty("exitCode").GetInt32() != 0
                    || project.GetProperty("executionStatus").GetString() != "completed"
                    || project.GetProperty("coverageArtifactStatus").GetString() != "produced"))
                return 1;
        }

        var reportPath = Path.Join(capture, "coverage.cobertura.xml");
        var reportText = await File.ReadAllTextAsync(reportPath).ConfigureAwait(false);
        if (!reportText.Contains("lines-valid=\"", StringComparison.Ordinal)
            || !reportText.Contains("branches-valid=\"", StringComparison.Ordinal))
            return 1;

        var native = await RecordAsync("native-collect", runRoot, (token, progress, diagnostics) =>
            CoverageFanIn.CollectAndCaptureAsync(new CoverageCollectRequest(
                policy, repository, nativeCapture, nativeCollection, "native", DeadlineSeconds: 240,
                Parallelism: 1, NoRestore: true), cancellationToken: token,
                progress: progress, diagnostics: diagnostics)).ConfigureAwait(false);
        PrintResult(native);
        if (native.Outcome != CoverageOperationOutcome.Succeeded
            || native.Observation != CoverageObservation.CollectedExecution
            || !native.Effects.GenerationCommitted
            || !native.Artifacts.Any(artifact => Path.GetFileName(artifact.Path) == "collector-inventory.json"))
            return 1;

        var collectedRawPaths = ValidateCollectedNativeSet(nativeCapture, nativeCollection, nativeProjects);
        var apiNativeCapture = Path.Join(runRoot, "native-set-capture");
        var nativeSet = await RecordAsync("native-set-capture", runRoot, (token, progress, diagnostics) =>
            CoverageFanInApiExample.CaptureNativeSetAsync(policy, repository, apiNativeCapture, "native",
                collectedRawPaths, Path.Join(nativeCapture, "timings.json"), token)).ConfigureAwait(false);
        PrintResult(nativeSet);
        if (nativeSet.Outcome != CoverageOperationOutcome.Succeeded
            || nativeSet.Observation != CoverageObservation.TrustedProducer
            || !nativeSet.Effects.GenerationCommitted)
            return 1;
        _ = ValidateNativeCapture(apiNativeCapture, collectedRawPaths,
            Path.Join(nativeCapture, "timings.json"), nativeProjects);

        CopyDirectory(nativeCapture, nativeTransport);
        ValidateNativeManifest(nativeTransport, policy, repository);
        _ = ValidateNativeCapture(nativeTransport, collectedRawPaths, Path.Join(nativeCapture, "timings.json"), nativeProjects);

        var apiNativeParent = Path.Join(runRoot, "direct-api");
        Directory.CreateDirectory(apiNativeParent);
        var apiNative = Path.Join(apiNativeParent, "native-import");
        var manifestPath = Path.Join(nativeTransport, "manifest.json");
        using var transportManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false));
        var originalSourceRoot = transportManifest.RootElement.GetProperty("sourceRoot").GetString()
            ?? throw new InvalidDataException("Native transport has no original source root.");
        var nativeImport = await RecordAsync("native-import", runRoot, (token, progress, diagnostics) =>
            CoverageFanIn.CaptureAsync(new CoverageCaptureRequest(policy, repository, apiNative, "native",
                ImportDirectory: nativeTransport, OriginalSourceRoot: originalSourceRoot), cancellationToken: token,
                progress: progress, diagnostics: diagnostics)).ConfigureAwait(false);
        PrintResult(nativeImport);
        if (nativeImport.Outcome != CoverageOperationOutcome.Succeeded
            || nativeImport.Observation != CoverageObservation.CollectedExecution
            || !nativeImport.Effects.Imported
            || !nativeImport.Effects.GenerationCommitted)
            return 1;

        var composed = await RecordAsync("compose", runRoot, (token, progress, diagnostics) =>
            CoverageFanInApiExample.ComposeAsync(policy, repository, capture, [apiNative], composition, token))
            .ConfigureAwait(false);
        PrintResult(composed);
        if (composed.Outcome != CoverageOperationOutcome.Succeeded || composed.Accepted || composed.Verified)
            return 1;

        var acceptance = await RecordAsync("accept", runRoot, (token, progress, diagnostics) =>
            CoverageFanInApiExample.AcceptAsync(policy, repository, capture, [apiNative], composition, accepted, token))
            .ConfigureAwait(false);
        PrintResult(acceptance);
        if (acceptance.Outcome != CoverageOperationOutcome.Succeeded || !acceptance.Accepted || !acceptance.Verified)
            return 1;

        var verification = await RecordAsync("verify", runRoot, (token, progress, diagnostics) =>
            CoverageFanInApiExample.VerifyAsync(policy, repository, capture, [apiNative], composition, accepted, token))
            .ConfigureAwait(false);
        PrintResult(verification);
        if (verification.Outcome != CoverageOperationOutcome.Succeeded || !verification.Verified)
            return 1;

        await File.WriteAllTextAsync(Path.Join(composition, "coverage.cobertura.xml"), "tampered")
            .ConfigureAwait(false);
        var tampered = await RecordAsync("tamper-verify", runRoot, (token, progress, diagnostics) =>
            CoverageFanInApiExample.VerifyAsync(policy, repository, capture, [apiNative], composition, accepted, token))
            .ConfigureAwait(false);
        PrintResult(tampered);
        if (tampered.Outcome == CoverageOperationOutcome.Succeeded || tampered.Verified)
            return 1;

        var cliRoot = Path.Join(runRoot, "installed-cli");
        Directory.CreateDirectory(cliRoot);
        var cliPrimary = Path.Join(cliRoot, "primary");
        var cliNative = Path.Join(cliRoot, "native-import");
        var cliComposition = Path.Join(cliRoot, "composition");
        var cliAccepted = Path.Join(cliRoot, "accepted");
        var cliPrimaryResult = await InvokeCliAsync(installedCli, repository, cliRoot, "capture",
            new CoverageCaptureRequest(policy, repository, cliPrimary, "primary", reportPath, timingsPath)).ConfigureAwait(false);
        RequireCliSuccess(cliPrimaryResult);
        var cliNativeResult = await InvokeCliAsync(installedCli, repository, cliRoot, "capture",
            new CoverageCaptureRequest(policy, repository, cliNative, "native", ImportDirectory: nativeTransport,
                OriginalSourceRoot: originalSourceRoot)).ConfigureAwait(false);
        RequireCliSuccess(cliNativeResult);
        var cliNativeFacts = cliNativeResult.GetProperty("result");
        if (cliNativeFacts.GetProperty("observation").GetString() != "collectedExecution"
            || !cliNativeFacts.GetProperty("effects").GetProperty("imported").GetBoolean())
            return 1;
        RequireCliSuccess(await InvokeCliAsync(installedCli, repository, cliRoot, "compose",
            new CoverageComposeRequest(policy, repository, cliPrimary, [cliNative], cliComposition)).ConfigureAwait(false));
        var cliAcceptance = await InvokeCliAsync(installedCli, repository, cliRoot, "accept",
            new CoverageAcceptRequest(policy, repository, cliPrimary, [cliNative], cliComposition, cliAccepted)).ConfigureAwait(false);
        RequireCliSuccess(cliAcceptance);
        if (!cliAcceptance.GetProperty("result").GetProperty("accepted").GetBoolean()
            || !cliAcceptance.GetProperty("result").GetProperty("verified").GetBoolean())
            return 1;
        RequireCliSuccess(await InvokeCliAsync(installedCli, repository, cliRoot, "verify",
            new CoverageVerifyRequest(policy, repository, cliPrimary, [cliNative], cliComposition, cliAccepted)).ConfigureAwait(false));
        await File.WriteAllTextAsync(Path.Join(cliComposition, "coverage.cobertura.xml"), "tampered")
            .ConfigureAwait(false);
        var cliTampered = await InvokeCliAsync(installedCli, repository, cliRoot, "verify",
            new CoverageVerifyRequest(policy, repository, cliPrimary, [cliNative], cliComposition, cliAccepted),
            expectedExitCode: 1).ConfigureAwait(false);
        if (cliTampered.GetProperty("operationOutcome").GetString() == "succeeded"
            || cliTampered.GetProperty("result").GetProperty("verified").GetBoolean())
            return 1;

        Console.WriteLine($"Real primary and current-host native collection/import, direct API and installed CLI composition/acceptance/verification, and tamper rejection passed. Source SDK: {sdk}.");
        return 0;
    }

    private static async Task CreateSourceAsync(string repository)
    {
        var library = Path.Join(repository, "checks", "Branchy");
        var tests = Path.Join(repository, "checks", "Branchy.Tests");
        var nativeTests = Path.Join(repository, "checks", "Branchy.Native.Tests");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(tests);
        Directory.CreateDirectory(nativeTests);

        await File.WriteAllTextAsync(Path.Join(repository, "Consumer.slnx"), """
            <Solution>
              <Project Path="checks/Branchy/Branchy.csproj" />
              <Project Path="checks/Branchy.Tests/Branchy.Tests.csproj" />
              <Project Path="checks/Branchy.Native.Tests/Branchy.Native.Tests.csproj" />
            </Solution>
            """).ConfigureAwait(false);

        await File.WriteAllTextAsync(Path.Join(repository, "NuGet.Config"), """
            <configuration>
              <packageSources><clear /><add key="nuget" value="https://api.nuget.org/v3/index.json" /></packageSources>
              <packageSourceMapping><clear /><packageSource key="nuget"><package pattern="*" /></packageSource></packageSourceMapping>
              <fallbackPackageFolders><clear /></fallbackPackageFolders>
            </configuration>
            """).ConfigureAwait(false);

        await File.WriteAllTextAsync(Path.Join(repository, ".gitignore"), "**/bin/\n**/obj/\n**/TestResults/\n")
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Join(library, "Branchy.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Join(library, "Decision.cs"), """
            namespace Branchy;

            public static class Decision
            {
                public static string Classify(int value, bool enabled)
                {
                    if (!enabled)
                        return "disabled";
                    if (value < 0)
                        return "negative";
                    if (value == 0)
                        return "zero";
                    return "positive";
                }
            }
            """).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Join(tests, "Branchy.Tests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <IsPackable>false</IsPackable>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.5.1" />
                <PackageReference Include="coverlet.collector" Version="10.0.1" PrivateAssets="all" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" PrivateAssets="all" />
                <PackageReference Include="JunitXml.TestLogger" Version="8.0.0" PrivateAssets="all" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="../Branchy/Branchy.csproj" />
              </ItemGroup>
            </Project>
            """).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Join(tests, "DecisionTests.cs"), """
            using Branchy;
            using Xunit;

            public sealed class DecisionTests
            {
                [Theory]
                [InlineData(1, false, "disabled")]
                [InlineData(-1, true, "negative")]
                [InlineData(0, true, "zero")]
                [InlineData(1, true, "positive")]
                public void Classify_covers_each_decision_path(int value, bool enabled, string expected)
                {
                    Assert.Equal(expected, Decision.Classify(value, enabled));
                }
            }
            """).ConfigureAwait(false);

        await File.WriteAllTextAsync(Path.Join(nativeTests, "Branchy.Native.Tests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <IsPackable>false</IsPackable>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.5.1" />
                <PackageReference Include="coverlet.collector" Version="10.0.1" PrivateAssets="all" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" PrivateAssets="all" />
                <PackageReference Include="JunitXml.TestLogger" Version="8.0.0" PrivateAssets="all" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="../Branchy/Branchy.csproj" />
              </ItemGroup>
            </Project>
            """).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Join(nativeTests, "NativeDecisionTests.cs"), """
            using Branchy;
            using Xunit;

            public sealed class NativeDecisionTests
            {
                [Theory]
                [InlineData(2, false, "disabled")]
                [InlineData(-2, true, "negative")]
                [InlineData(0, true, "zero")]
                [InlineData(2, true, "positive")]
                public void Classify_covers_each_native_decision_path(int value, bool enabled, string expected)
                {
                    Assert.Equal(expected, Decision.Classify(value, enabled));
                }
            }
            """).ConfigureAwait(false);
    }

    private static async Task InitializeSourceAsync(string repository)
    {
        await RunCommandAsync(repository, "git", ["init"], IdentityCommandTimeout).ConfigureAwait(false);
        await RunCommandAsync(repository, "git", ["config", "user.email", "dogfood@example.invalid"], IdentityCommandTimeout)
            .ConfigureAwait(false);
        await RunCommandAsync(repository, "git", ["config", "user.name", "AppSurface installed-package fixture"], IdentityCommandTimeout)
            .ConfigureAwait(false);
        await RunCommandAsync(repository, "git", ["add", "."], IdentityCommandTimeout).ConfigureAwait(false);
        await RunCommandAsync(repository, "git", ["commit", "-m", "branchy VSTest coverage fixture"], IdentityCommandTimeout)
            .ConfigureAwait(false);
    }

    private static string CurrentPlatform() => OperatingSystem.IsMacOS() ? "Darwin"
        : OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux"
        : throw new PlatformNotSupportedException("Native fixture proof requires Windows, Linux, or macOS.");

    private static void CopyDirectory(string source, string destination)
    {
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Native transport destination must be fresh.");
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Join(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Join(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static IReadOnlyDictionary<string, string> ValidateCollectedNativeSet(
        string capture, string collection, IReadOnlyList<string> projects)
    {
        var captureTimingsPath = Path.Join(capture, "timings.json");
        var collectionTimingsPath = Path.Join(collection, "timings.json");
        var timingsBytes = File.ReadAllBytes(captureTimingsPath);
        if (!timingsBytes.AsSpan().SequenceEqual(File.ReadAllBytes(collectionTimingsPath)))
            throw new InvalidDataException("Collected native timings were not retained byte-for-byte.");
        using var timings = JsonDocument.Parse(timingsBytes);
        var entries = timings.RootElement.GetProperty("projects").EnumerateArray().ToArray();
        if (entries.Length != projects.Count
            || !entries.Select(entry => entry.GetProperty("project").GetString()).SequenceEqual(projects)
            || entries.Any(entry => entry.GetProperty("exitCode").GetInt32() != 0
                || entry.GetProperty("executionStatus").GetString() != "completed"
                || entry.GetProperty("coverageArtifactStatus").GetString() != "produced"))
            throw new InvalidDataException("Native collection did not retain the complete successful project inventory.");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var project = entry.GetProperty("project").GetString()
                ?? throw new InvalidDataException("Native timings project is missing.");
            var invocation = entry.GetProperty("invocationDirectory").GetString()
                ?? throw new InvalidDataException("Native timings invocation directory is missing.");
            var invocationPath = Path.Join(collection, invocation.Replace('/', Path.DirectorySeparatorChar));
            var reports = Directory.EnumerateFiles(invocationPath, "coverage.cobertura.xml", SearchOption.AllDirectories).ToArray();
            if (reports.Length != 1)
                throw new InvalidDataException($"Native collector attachment inventory differs for {project}.");
            result.Add(project, reports[0]);
        }

        _ = ValidateNativeCapture(capture, result, captureTimingsPath, projects);
        return result;
    }

    private static IReadOnlyDictionary<string, string> ValidateNativeCapture(
        string capture, IReadOnlyDictionary<string, string> originalPaths, string expectedTimingsPath,
        IReadOnlyList<string> projects)
    {
        var timingsBytes = File.ReadAllBytes(Path.Join(capture, "timings.json"));
        if (!timingsBytes.AsSpan().SequenceEqual(File.ReadAllBytes(expectedTimingsPath)))
            throw new InvalidDataException("Native timings were not retained byte-for-byte.");
        var inventoryBytes = File.ReadAllBytes(Path.Join(capture, "collector-inventory.json"));
        var expectedPayloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["timings.json"] = timingsBytes,
            ["collector-inventory.json"] = inventoryBytes,
            ["coverage.cobertura.xml"] = File.ReadAllBytes(Path.Join(capture, "coverage.cobertura.xml"))
        };
        using var inventory = JsonDocument.Parse(inventoryBytes);
        var index = inventory.RootElement;
        if (!HasExactProperties(index, "schemaVersion", "reports") || index.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("Native collector inventory schema version differs.");
        var reports = index.GetProperty("reports").EnumerateArray().ToArray();
        if (reports.Length != projects.Count || originalPaths.Count != projects.Count)
            throw new InvalidDataException("Native collector inventory count differs.");
        for (var i = 0; i < projects.Count; i++)
        {
            var fileName = CollectorFileName(i);
            if (!HasExactProperties(reports[i], "project", "file")
                || reports[i].GetProperty("project").GetString() != projects[i]
                || reports[i].GetProperty("file").GetString() != fileName
                || !originalPaths.TryGetValue(projects[i], out var originalPath))
                throw new InvalidDataException("Native collector inventory order or project binding differs.");
            var original = File.ReadAllBytes(originalPath);
            expectedPayloads.Add(fileName, original);
            if (!original.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Join(capture, fileName))))
                throw new InvalidDataException($"Original collector bytes were not retained for {projects[i]}.");
        }

        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Join(capture, "manifest.json")));
        var hashes = manifest.RootElement.GetProperty("files");
        if (!hashes.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal)
            .SetEquals(expectedPayloads.Keys))
            throw new InvalidDataException("Native manifest payload inventory differs.");
        foreach (var (name, bytes) in expectedPayloads)
        {
            var expectedHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (hashes.GetProperty(name).GetString() != expectedHash)
                throw new InvalidDataException($"Native manifest hash differs for {name}.");
        }
        return projects.Select((project, index) => new KeyValuePair<string, string>(project,
            Path.Join(capture, CollectorFileName(index)))).ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
    }

    private static string CollectorFileName(int index) =>
        "collector-" + index.ToString("D4", CultureInfo.InvariantCulture) + ".cobertura.xml";

    private static bool HasExactProperties(JsonElement value, params string[] names) =>
        value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal)
            .SetEquals(names);

    private static void ValidateNativeManifest(string capture, CoverageFanInPolicy policy, string repository)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Join(capture, "manifest.json")));
        var root = manifest.RootElement;
        var source = root.GetProperty("source");
        var run = root.GetProperty("run");
        if (root.GetProperty("producerId").GetString() != "native"
            || root.GetProperty("observation").GetString() != "CollectedExecution"
            || root.GetProperty("sourceRoot").GetString() != repository
            || source.GetProperty("commit").GetString() != policy.Source.Commit
            || source.GetProperty("tree").GetString() != policy.Source.Tree
            || run.GetProperty("runId").GetString() != policy.Run.RunId
            || run.GetProperty("attempt").GetInt32() != policy.Run.Attempt)
            throw new InvalidDataException("Transported native capture lost its source, run, or collection origin.");
    }

    private static async Task<JsonElement> InvokeCliAsync(string executable, string repository, string root,
        string operation, object request, int expectedExitCode = 0)
    {
        var stem = operation + "-" + Guid.NewGuid().ToString("N");
        var requestPath = Path.Join(root, stem + "-request.json");
        var resultPath = Path.Join(root, stem + "-result.json");
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        };
        var envelope = new { schemaVersion = 2, operation, request };
        await File.WriteAllBytesAsync(requestPath, JsonSerializer.SerializeToUtf8Bytes(envelope, options)).ConfigureAwait(false);
        _ = await RunCommandAsync(repository, executable,
            ["coverage", "fan-in", operation, "--request", requestPath, "--result", resultPath, "--json"],
            TimeSpan.FromMinutes(3), expectedExitCode).ConfigureAwait(false);
        if (!File.Exists(resultPath))
            throw new InvalidDataException("Installed CLI did not write its requested result document.");
        using var result = JsonDocument.Parse(await File.ReadAllBytesAsync(resultPath).ConfigureAwait(false));
        var document = result.RootElement;
        if (document.GetProperty("schemaVersion").GetInt32() != 2
            || document.GetProperty("operation").GetString() != operation)
            throw new InvalidDataException("Installed CLI returned an unexpected result envelope.");
        return document.Clone();
    }

    private static void RequireCliSuccess(JsonElement envelope)
    {
        if (!string.Equals(envelope.GetProperty("operationOutcome").GetString(), "succeeded", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Installed CLI fan-in operation did not succeed.");
    }

    private static async Task<CoverageFanInResult> RecordAsync(
        string stage,
        string runRoot,
        Func<CancellationToken, TextWriter, TextWriter, ValueTask<CoverageFanInResult>> operation)
    {
        using var progress = new BoundedTextWriter(MaximumCapturedCharacters);
        using var diagnostics = new BoundedTextWriter(MaximumCapturedCharacters);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var result = await operation(deadline.Token, progress, diagnostics).ConfigureAwait(false);
        var log = $"{result.Operation}:{result.Outcome};observation={result.Observation};accepted={result.Accepted};verified={result.Verified}{Environment.NewLine}"
            + progress.Content + Environment.NewLine + diagnostics.Content;
        await File.WriteAllTextAsync(Path.Join(runRoot, $"{stage}.log"), log).ConfigureAwait(false);
        return result;
    }

    private static void PrintResult(CoverageFanInResult result)
    {
        Console.WriteLine($"{result.Operation}:{result.Outcome};observation={result.Observation};accepted={result.Accepted};verified={result.Verified};committed={result.Effects.GenerationCommitted}");
        foreach (var diagnostic in result.Diagnostics)
            Console.Error.WriteLine($"{diagnostic.Code}:{diagnostic.Stage}:{diagnostic.Field}: {diagnostic.Recovery}");
    }

    private static async Task<string> RunCommandAsync(string workingDirectory, string file, IReadOnlyList<string> arguments,
        TimeSpan timeout, int expectedExitCode = 0)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(file)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start())
            throw new InvalidOperationException($"Could not start {file}.");

        using var deadline = new CancellationTokenSource(timeout);
        var standardOutput = ReadBoundedAsync(process.StandardOutput, deadline.Token);
        var standardError = ReadBoundedAsync(process.StandardError, deadline.Token);
        var exit = process.WaitForExitAsync(deadline.Token);
        var readers = Task.WhenAll(standardOutput, standardError);
        try
        {
            var first = await Task.WhenAny(exit, readers).ConfigureAwait(false);
            if (first == readers && readers.IsFaulted)
                await readers.ConfigureAwait(false);
            await exit.ConfigureAwait(false);
            await readers.ConfigureAwait(false);
        }
        catch
        {
            deadline.Cancel();
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between the check and kill.
            }
            try { await Task.WhenAll(exit, readers).ConfigureAwait(false); }
            catch (Exception) { /* Preserve the original timeout or stream failure. */ }
            throw;
        }

        if (process.ExitCode != expectedExitCode)
            throw new InvalidOperationException($"{file} {string.Join(' ', arguments)} failed: {standardError.Result}");
        return standardOutput.Result.Trim();
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var content = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return content.ToString();
            if (content.Length + read > MaximumCapturedCharacters)
                throw new IOException("Fixture command output exceeded its capture limit.");
            content.Append(buffer, 0, read);
        }
    }

    private sealed class BoundedTextWriter(int maximumCharacters) : TextWriter
    {
        private readonly StringBuilder _content = new();
        private bool _truncated;

        public override Encoding Encoding => Encoding.UTF8;
        public string Content => _content + (_truncated ? "[output truncated]" : string.Empty);

        public override void Write(char value) => Append(value.ToString());
        public override void Write(string? value) => Append(value ?? string.Empty);
        public override void Write(ReadOnlySpan<char> buffer) => Append(buffer.ToString());

        private void Append(string value)
        {
            if (_truncated || value.Length == 0)
                return;
            var remaining = maximumCharacters - _content.Length;
            if (value.Length <= remaining)
            {
                _content.Append(value);
                return;
            }
            if (remaining > 0)
                _content.Append(value.AsSpan(0, remaining));
            _truncated = true;
        }
    }
}
