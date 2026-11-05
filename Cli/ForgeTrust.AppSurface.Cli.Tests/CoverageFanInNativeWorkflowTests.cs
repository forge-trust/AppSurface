using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageFanInNativeWorkflowTests
{
    [Fact]
    public async Task Public_collect_and_capture_runs_primary_and_native_reports_through_strict_normalization()
    {
        using var fixture = await Fixture.CreateAsync();
        var policy = await fixture.PolicyAsync();
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
        Assert.Contains(native.Artifacts, artifact => artifact.Role == "collector.cobertura.xml");

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
        var rawPath = Assert.Single(
            Directory.EnumerateFiles(fixture.NativeCollection, "coverage.cobertura.xml", SearchOption.AllDirectories),
            path => path.Contains("collector-results", StringComparison.OrdinalIgnoreCase));
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

        internal async Task<CoverageFanInPolicy> PolicyAsync()
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
                    "FullyQualifiedName~Consumer.Alpha.Tests.AlphaDecisionTests",
                    ["Consumer.Alpha.Tests/Consumer.Alpha.Tests.csproj"])],
                new CoverageGatePolicy(CoverageGateMode.AggregateOnly));
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
