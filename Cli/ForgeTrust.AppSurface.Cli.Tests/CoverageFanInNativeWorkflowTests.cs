using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageFanInNativeWorkflowTests
{
    [Theory]
    [InlineData("--assume-unchanged")]
    [InlineData("--skip-worktree")]
    [InlineData("ignored-submodule")]
    public async Task CollectRejectsSourceChangesHiddenByGitSettings(string setting)
    {
        using var fixture = await Fixture.CreateAsync();
        if (setting == "ignored-submodule")
            await fixture.HideSubmoduleChangeAsync();
        else
            await fixture.HideTrackedEditAsync(setting);
        var policy = await fixture.PolicyAsync();
        using var progress = new StringWriter();

        var result = await CoverageFanIn.CollectAndCaptureAsync(new CoverageCollectRequest(
            policy, fixture.Repository, fixture.PrimaryCapture, fixture.PrimaryCollection, "primary", 180,
            NoRestore: true), progress: progress);

        Assert.Equal(CoverageOperationOutcome.Rejected, result.Outcome);
        Assert.Null(result.Observation);
        Assert.Empty(progress.ToString());
        Assert.False(Directory.Exists(fixture.PrimaryCollection));
        Assert.False(Directory.Exists(fixture.PrimaryCapture));
    }

    [Fact]
    public async Task CollectDoesNotCertifyOldBinariesForTheCurrentCommit()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.BuildAsync();
        await fixture.AddFailingNativeTestAsync();
        var policy = await fixture.PolicyAsync(useAllNative: true);
        var request = new CoverageCollectRequest(policy, fixture.Repository, fixture.NativeCapture,
            fixture.NativeCollection, "native", 180, SolutionPath: fixture.Solution, NoRestore: true, NoBuild: true);

        var skippedBuild = await CoverageFanIn.CollectAndCaptureAsync(request);

        Assert.Equal(CoverageOperationOutcome.Rejected, skippedBuild.Outcome);
        Assert.Equal("noBuild", Assert.Single(skippedBuild.Diagnostics).Field);
        Assert.Null(skippedBuild.Observation);
        Assert.False(Directory.Exists(fixture.NativeCollection));
        Assert.False(Directory.Exists(fixture.NativeCapture));

        var rebuilt = await CoverageFanIn.CollectAndCaptureAsync(request with { NoBuild = false });

        Assert.Equal(CoverageOperationOutcome.Incomplete, rebuilt.Outcome);
        Assert.Null(rebuilt.Observation);
        Assert.False(Directory.Exists(fixture.NativeCapture));
        using var timings = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Join(fixture.NativeCollection, "timings.json")));
        Assert.Contains(timings.RootElement.GetProperty("projects").EnumerateArray(),
            project => project.GetProperty("exitCode").GetInt32() != 0);
    }

    [Theory]
    [InlineData("foreign-platform")]
    [InlineData("populated-collection")]
    [InlineData("failed-project")]
    public async Task NativeCollectionCannotPublishWhenExecutionOrDestinationIsInvalid(string scenario)
    {
        using var fixture = await Fixture.CreateAsync();
        if (scenario == "failed-project")
            await fixture.AddFailingNativeTestAsync();
        var policy = await fixture.PolicyAsync(useAllNative: true);
        if (scenario == "foreign-platform")
        {
            var native = policy.Supplements[0];
            policy = policy with
            {
                Supplements = [native with { Platform = OperatingSystem.IsWindows() ? "Linux" : "Windows" }]
            };
        }
        var sentinel = Path.Join(fixture.NativeCollection, "caller-owned.txt");
        if (scenario == "populated-collection")
        {
            Directory.CreateDirectory(fixture.NativeCollection);
            await File.WriteAllTextAsync(sentinel, "preserve this collection");
        }
        using var progress = new StringWriter();

        var result = await CoverageFanIn.CollectAndCaptureAsync(new CoverageCollectRequest(
            policy, fixture.Repository, fixture.NativeCapture, fixture.NativeCollection, "native", 180,
            SolutionPath: fixture.Solution, Parallelism: 1, NoRestore: true), progress: progress, diagnostics: progress);

        Assert.Equal(scenario == "failed-project" ? CoverageOperationOutcome.Incomplete : CoverageOperationOutcome.Rejected,
            result.Outcome);
        Assert.False(result.Accepted);
        Assert.False(result.Verified);
        Assert.False(result.Effects.GenerationCommitted);
        Assert.False(Directory.Exists(fixture.NativeCapture));
        Assert.DoesNotContain(fixture.Repository, progress.ToString(), StringComparison.Ordinal);
        if (scenario == "populated-collection")
        {
            Assert.Equal("preserve this collection", await File.ReadAllTextAsync(sentinel));
            Assert.Equal([sentinel], Directory.GetFiles(fixture.NativeCollection));
        }
        else if (scenario == "foreign-platform")
            Assert.False(Directory.Exists(fixture.NativeCollection));
        else
        {
            Assert.True(Directory.Exists(fixture.NativeCollection));
            using var timings = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Join(fixture.NativeCollection, "timings.json")));
            Assert.Contains(timings.RootElement.GetProperty("projects").EnumerateArray(),
                project => project.GetProperty("exitCode").GetInt32() != 0);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_collect_and_capture_runs_primary_and_native_reports_through_strict_normalization(bool useAllNative)
    {
        using var fixture = await Fixture.CreateAsync();
        var policy = await fixture.PolicyAsync(useAllNative);
        using var progress = new StringWriter();

        var primary = await CoverageFanIn.CollectAndCaptureAsync(new CoverageCollectRequest(
            policy, fixture.Repository, fixture.PrimaryCapture, fixture.PrimaryCollection, "primary", 180,
            SolutionPath: fixture.Solution, Parallelism: 1, NoRestore: true), progress: progress, diagnostics: progress);

        Assert.True(primary.Outcome == CoverageOperationOutcome.Succeeded,
            JsonSerializer.Serialize(primary) + Environment.NewLine + progress);
        Assert.Equal("collect", primary.Operation);
        Assert.Equal(CoverageObservation.CollectedExecution, primary.Observation);
        Assert.False(primary.Accepted);
        Assert.False(primary.Verified);
        Assert.True(primary.Effects.GenerationCommitted);
        Assert.Contains(primary.Artifacts, artifact => artifact.Role == "coverage.cobertura.xml");
        Assert.Contains(primary.Artifacts, artifact => artifact.Role == "timings.json");
        using (var timings = JsonDocument.Parse(File.ReadAllBytes(Path.Join(fixture.PrimaryCapture, "timings.json"))))
        {
            var projects = timings.RootElement.GetProperty("projects").EnumerateArray().ToArray();
            Assert.Equal(fixture.TestProjects.Length, projects.Length);
            Assert.All(projects, project =>
            {
                Assert.Equal(0, project.GetProperty("exitCode").GetInt32());
                Assert.Equal("completed", project.GetProperty("executionStatus").GetString());
                Assert.Equal("produced", project.GetProperty("coverageArtifactStatus").GetString());
            });
        }

        var native = await CoverageFanIn.CollectAndCaptureAsync(new CoverageCollectRequest(
            policy, fixture.Repository, fixture.NativeCapture, fixture.NativeCollection, "native", 180,
            SolutionPath: fixture.Solution, Parallelism: 1, NoRestore: true), progress: progress, diagnostics: progress);

        Assert.True(native.Outcome == CoverageOperationOutcome.Succeeded,
            JsonSerializer.Serialize(native) + Environment.NewLine + progress);
        Assert.Equal(CoverageObservation.CollectedExecution, native.Observation);
        Assert.False(native.Accepted);
        Assert.True(native.Effects.GenerationCommitted);
        Assert.Contains(native.Artifacts, artifact => artifact.Role ==
            (useAllNative ? "collector-inventory.json" : "collector.cobertura.xml"));

        var composition = await CoverageFanIn.ComposeAsync(new CoverageComposeRequest(
            policy, fixture.Repository, fixture.PrimaryCapture, [fixture.NativeCapture], fixture.Composition));
        Assert.Equal(CoverageOperationOutcome.Succeeded, composition.Outcome);
        Assert.Equal(CoverageObservation.CollectedExecution, composition.Observation);
        Assert.False(composition.Accepted);
        Assert.False(composition.Verified);
        Assert.DoesNotContain("private-test-output-sentinel", progress.ToString());
        Assert.DoesNotContain(fixture.Repository, progress.ToString());

        // Capture checks normalization, and composition independently reloads the exact native
        // inventory before comparing concrete collector and package-owned ReportGenerator points.
        var attachments = Directory.EnumerateFiles(fixture.NativeCollection, "coverage.cobertura.xml", SearchOption.AllDirectories)
            .Where(path => path.Contains("collector-results", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (useAllNative)
        {
            Assert.Equal(2, attachments.Length);
            using var inventory = JsonDocument.Parse(File.ReadAllBytes(Path.Join(fixture.NativeCapture, "collector-inventory.json")));
            Assert.Equal(1, inventory.RootElement.GetProperty("schemaVersion").GetInt32());
            var entries = inventory.RootElement.GetProperty("reports").EnumerateArray().ToArray();
            Assert.Equal(fixture.TestProjects, entries.Select(entry => entry.GetProperty("project").GetString()).ToArray());
            using var timings = JsonDocument.Parse(File.ReadAllBytes(Path.Join(fixture.NativeCapture, "timings.json")));
            var executions = timings.RootElement.GetProperty("projects").EnumerateArray()
                .ToDictionary(entry => entry.GetProperty("project").GetString()!, StringComparer.Ordinal);
            for (var index = 0; index < entries.Length; index++)
            {
                var name = "collector-" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + ".cobertura.xml";
                Assert.Equal(name, entries[index].GetProperty("file").GetString());
                var invocation = executions[fixture.TestProjects[index]].GetProperty("invocationDirectory").GetString()!;
                var attachment = Assert.Single(Directory.EnumerateFiles(
                    TestPathUtils.PathUnder(fixture.NativeCollection, invocation), "coverage.cobertura.xml", SearchOption.AllDirectories));
                Assert.Equal(File.ReadAllBytes(attachment), File.ReadAllBytes(Path.Join(fixture.NativeCapture, name)));
            }
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Join(fixture.NativeCapture, "manifest.json")));
            var sourceRoot = manifest.RootElement.GetProperty("sourceRoot").GetString()!;
            var originals = entries.Select(entry => (XDocument.Load(Path.Join(fixture.NativeCapture,
                entry.GetProperty("file").GetString()!)), sourceRoot)).ToArray();
            var projection = XDocument.Load(Path.Join(fixture.NativeCapture, "coverage.cobertura.xml"));
            ForgeTrust.AppSurface.Evidence.Coverage.CoverageFanInIndependentVerifier.VerifyNativeCollection(originals, projection);
            Assert.False(File.Exists(Path.Join(fixture.NativeCapture, "collector.cobertura.xml")));
            var importedPath = fixture.NativeCapture + "-imported";
            var imported = await CoverageFanIn.CaptureAsync(new CoverageCaptureRequest(
                policy, fixture.Repository, importedPath, "native", ImportDirectory: fixture.NativeCapture,
                OriginalSourceRoot: manifest.RootElement.GetProperty("sourceRoot").GetString()));
            Assert.Equal(CoverageOperationOutcome.Succeeded, imported.Outcome);
            Assert.True(imported.Effects.Imported);
            Assert.Equal(CoverageObservation.CollectedExecution, imported.Observation);
            var importedComposition = fixture.Composition + "-imported";
            Assert.Equal(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.ComposeAsync(new(
                policy, fixture.Repository, fixture.PrimaryCapture, [importedPath], importedComposition))).Outcome);
            var acceptedPath = fixture.Composition + "-accepted";
            var accepted = await CoverageFanIn.AcceptAsync(new(
                policy, fixture.Repository, fixture.PrimaryCapture, [importedPath], importedComposition, acceptedPath));
            Assert.True(accepted.Outcome == CoverageOperationOutcome.Succeeded, JsonSerializer.Serialize(accepted));
            Assert.True(accepted.Accepted);
            Assert.True(accepted.Verified);
            Assert.True(accepted.Effects.Imported);
            Assert.False(accepted.PrimaryImported);
            Assert.Equal(CoverageObservation.CollectedExecution, accepted.Observation);
            var verified = await CoverageFanIn.VerifyAsync(new(
                policy, fixture.Repository, fixture.PrimaryCapture, [importedPath], importedComposition, acceptedPath));
            Assert.Equal(CoverageOperationOutcome.Succeeded, verified.Outcome);
            Assert.True(verified.Verified);
            Assert.True(verified.Effects.Imported);
            Assert.False(verified.PrimaryImported);
            return;
        }
        var rawPath = Assert.Single(attachments);
        var raw = XDocument.Load(rawPath).Root!;
        Assert.Equal(File.ReadAllBytes(rawPath), File.ReadAllBytes(Path.Join(fixture.NativeCapture, "collector.cobertura.xml")));
        using var nativeManifest = JsonDocument.Parse(File.ReadAllBytes(Path.Join(fixture.NativeCapture, "manifest.json")));
        Assert.Equal("appsurface-coverage-merge", nativeManifest.RootElement.GetProperty("policy")
            .GetProperty("instrumentation").GetProperty("normalization").GetString());
        var normalized = XDocument.Load(Path.Join(fixture.NativeCapture, "coverage.cobertura.xml")).Root!;
        foreach (var counter in new[] { "lines-covered", "lines-valid", "branches-covered", "branches-valid" })
            Assert.Equal(raw.Attribute(counter)?.Value, normalized.Attribute(counter)?.Value);
        Assert.True(int.Parse(normalized.Attribute("lines-valid")!.Value,
            System.Globalization.CultureInfo.InvariantCulture) > 0);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string? _previousArtifactHome = Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");
        private const string CurrentDecision = "value >= 0 ? \"non-negative\" : \"negative\"";
        private const string BaselineDecision = "value < 0 ? \"negative\" : \"non-negative\"";
        private readonly TestDirectory _temp = TestDirectory.Create();

        private Fixture() { }

        internal string Repository => Path.Join(_temp.Path, "repo");
        internal string Solution => Path.Join(Repository, "Consumer.slnx");
        internal string[] TestProjects =>
        [
            "Consumer.Alpha.Tests/Consumer.Alpha.Tests.csproj",
            "Consumer.Beta.Tests/Consumer.Beta.Tests.csproj",
        ];
        internal string PrimaryCapture => Path.Join(_temp.Path, "primary-capture");
        internal string PrimaryCollection => Path.Join(_temp.Path, "primary-collection");
        internal string NativeCapture => Path.Join(_temp.Path, "native-capture");
        internal string NativeCollection => Path.Join(_temp.Path, "native-collection");
        internal string Composition => Path.Join(_temp.Path, "composition");

        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", Path.Join(fixture._temp.Path, "store"));
            var consumer = FindRepositoryRoot();
            var source = Path.Join(consumer, "tools", "ForgeTrust.AppSurface.Evidence.Validation", "fixtures", "consumer");
            CopyDirectory(source, fixture.Repository);
            Assert.False(File.Exists(Path.Join(fixture.Repository, "global.json")));
            await File.WriteAllTextAsync(Path.Join(fixture.Repository, ".gitignore"),
                "**/bin/\n**/obj/\n**/TestResults/\n");

            await Command(fixture.Repository, "git", "init");
            await Command(fixture.Repository, "git", "config", "user.email", "coverage-tests@example.invalid");
            await Command(fixture.Repository, "git", "config", "user.name", "Coverage workflow tests");
            var testSource = Path.Join(fixture.Repository, "Consumer.Alpha.Tests", "AlphaDecisionTests.cs");
            var testText = await File.ReadAllTextAsync(testSource);
            await File.WriteAllTextAsync(testSource, testText.Replace("=> Assert.Equal(",
                "{ System.Console.WriteLine(\"private-test-output-sentinel\"); Assert.Equal(", StringComparison.Ordinal)
                .Replace("AlphaDecision.Describe(2));", "AlphaDecision.Describe(2)); }", StringComparison.Ordinal)
                .Replace("AlphaDecision.Describe(-2));", "AlphaDecision.Describe(-2)); }", StringComparison.Ordinal));
            await Command(fixture.Repository, "dotnet", "restore", "Consumer.slnx", "--ignore-failed-sources");

            var sourceFile = Path.Join(fixture.Repository, "Consumer.Alpha", "AlphaDecision.cs");
            var current = await File.ReadAllTextAsync(sourceFile);
            Assert.Contains(CurrentDecision, current, StringComparison.Ordinal);
            await File.WriteAllTextAsync(sourceFile, current.Replace(CurrentDecision, BaselineDecision, StringComparison.Ordinal));
            await Command(fixture.Repository, "git", "add", ".");
            await Command(fixture.Repository, "git", "commit", "-m", "coverage fixture base");
            await File.WriteAllTextAsync(sourceFile, current);
            await Command(fixture.Repository, "git", "add", ".");
            await Command(fixture.Repository, "git", "commit", "-m", "coverage fixture current source");
            Assert.Equal(string.Empty, await Command(fixture.Repository, "git", "status", "--porcelain"));
            return fixture;
        }

        internal async Task<CoverageFanInPolicy> PolicyAsync(bool useAllNative = false)
        {
            var platform = OperatingSystem.IsMacOS() ? "Darwin" : OperatingSystem.IsWindows() ? "Windows" : "Linux";
            var head = await Command(Repository, "git", "rev-parse", "HEAD");
            var tree = await Command(Repository, "git", "rev-parse", "HEAD^{tree}");
            var sdkVersion = await Command(Repository, "dotnet", "--version");
            return new(
                new CoverageSourceIdentity(
                    head,
                    tree,
                    sdkVersion, "Release"),
                new CoverageRunIdentity("local", "issue-897-native-workflow", "fixture-run", 1),
                TestProjects,
                [new CoverageSupplement("native", platform,
                    useAllNative ? "FullyQualifiedName~Consumer." : "FullyQualifiedName~Consumer.Alpha.Tests.AlphaDecisionTests",
                    useAllNative ? TestProjects : ["Consumer.Alpha.Tests/Consumer.Alpha.Tests.csproj"])],
                new CoverageGatePolicy(CoverageGateMode.AggregateOnly));
        }

        internal async Task HideTrackedEditAsync(string flag)
        {
            const string source = "Consumer.Alpha/AlphaDecision.cs";
            await Command(Repository, "git", "update-index", flag, "--", source);
            await File.AppendAllTextAsync(TestPathUtils.PathUnder(Repository, source), "\n// hidden edit\n");
            Assert.Equal(string.Empty, await Command(Repository, "git", "status", "--porcelain", "--untracked-files=all"));
        }

        internal async Task HideSubmoduleChangeAsync()
        {
            var remote = Path.Join(_temp.Path, "submodule-source");
            Directory.CreateDirectory(remote);
            await Command(remote, "git", "init");
            await Command(remote, "git", "config", "user.email", "coverage-tests@example.invalid");
            await Command(remote, "git", "config", "user.name", "Coverage workflow tests");
            await Command(remote, "git", "commit", "--allow-empty", "-m", "pinned dependency");
            var pinned = await Command(remote, "git", "rev-parse", "HEAD");
            await Command(remote, "git", "commit", "--allow-empty", "-m", "different dependency");
            var different = await Command(remote, "git", "rev-parse", "HEAD");
            await Command(Repository, "git", "-c", "protocol.file.allow=always", "submodule", "add", "--name", "dependency", "--", remote, "dependency");
            var submodule = Path.Join(Repository, "dependency");
            await Command(submodule, "git", "checkout", "--detach", pinned);
            await Command(Repository, "git", "config", "-f", ".gitmodules", "submodule.dependency.ignore", "all");
            await Command(Repository, "git", "add", ".gitmodules");
            await Command(Repository, "git", "update-index", "--cacheinfo", $"160000,{pinned},dependency");
            await Command(Repository, "git", "commit", "-m", "pin dependency and ignore submodule changes");
            await Command(submodule, "git", "checkout", "--detach", different);
            Assert.Equal(string.Empty, await Command(Repository, "git", "status", "--porcelain", "--untracked-files=all"));
            Assert.Contains("dependency", await Command(Repository, "git", "status", "--porcelain", "--ignore-submodules=none"),
                StringComparison.Ordinal);
        }

        internal Task<string> BuildAsync() =>
            Command(Repository, "dotnet", "build", "Consumer.slnx", "--configuration", "Release", "--no-restore");

        internal async Task AddFailingNativeTestAsync()
        {
            await File.WriteAllTextAsync(Path.Join(Repository, "Consumer.Alpha.Tests", "RequiredFailureTests.cs"),
                "namespace Consumer.Alpha.Tests; public sealed class RequiredFailureTests { [Xunit.Fact] public void RequiredExecutionFails() => Xunit.Assert.Equal(1, 2); }");
            await Command(Repository, "git", "add", ".");
            await Command(Repository, "git", "commit", "-m", "add required failing producer test");
        }

        private static string FindRepositoryRoot()
        {
            foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            {
                for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                {
                    if (File.Exists(Path.Join(directory.FullName, "tools", "ForgeTrust.AppSurface.Evidence.Validation",
                            "fixtures", "consumer", "Consumer.slnx")))
                        return directory.FullName;
                }
            }
            throw new DirectoryNotFoundException("Could not locate the repository-owned consumer fixture.");
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, directory);
                if (relative.Split(Path.DirectorySeparatorChar).Any(name => name is "bin" or "obj" or ".git"))
                    continue;
                Directory.CreateDirectory(TestPathUtils.PathUnder(destination, relative));
            }
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                if (relative.Split(Path.DirectorySeparatorChar).Any(name => name is "bin" or "obj" or ".git"))
                    continue;
                var target = TestPathUtils.PathUnder(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }

        private static async Task<string> Command(string directory, string executable, params string[] arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    WorkingDirectory = directory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            foreach (var argument in arguments)
                process.StartInfo.ArgumentList.Add(argument);
            // A reusable build server can outlive its launcher while retaining redirected
            // pipe handles. Keep fixture setup self-contained and bound the complete drain,
            // including the period after the immediate process has exited.
            process.StartInfo.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
            process.StartInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            process.StartInfo.Environment["UseSharedCompilation"] = "false";
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            string output;
            string error;
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                output = await stdout;
                error = await stderr;
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw new TimeoutException($"{executable} exceeded the fixture setup deadline.");
            }
            Assert.True(process.ExitCode == 0, $"{executable} {string.Join(' ', arguments)} failed: {error}\n{output}");
            return output.Trim();
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", _previousArtifactHome);
            _temp.Dispose();
        }
    }
}
