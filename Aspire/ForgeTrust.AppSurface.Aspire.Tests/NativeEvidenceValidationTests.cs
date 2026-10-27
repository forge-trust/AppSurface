using System.IO.Compression;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Evidence.Validation;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class NativeEvidenceValidationTests
{
    [Theory]
    [InlineData("storage", false)]
    [InlineData("consumer", false)]
    [InlineData("consumer", true)]
    public async Task Journeys_ShouldVerifyPackedOrInstalledBehaviorAndCleanOnlyOwnedScratchWithoutProtectedClaims(string mode, bool external)
    {
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var parent = Directory.CreateTempSubdirectory("appsurface-native-validation-tests-").FullName;
        var completed = false;
        try
        {
            var runner = new EvidenceCommandRunner();
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            var identity = await runner.RunAsync(new EvidenceCommandRequest("git", ["rev-parse", "HEAD"], repository, TimeSpan.FromSeconds(30)));
            Assert.Equal(0, identity.ExitCode);
            var feed = Path.Join(parent, "feed");
            Directory.CreateDirectory(feed);
            var configuration = AppContext.BaseDirectory.Contains("Release", StringComparison.Ordinal) ? "Release" : "Debug";
            var pack = await runner.RunAsync(new EvidenceCommandRequest(dotnet,
                ["pack", Path.Join(repository, "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"), "--no-build", "--no-restore", "--configuration", configuration,
                    "--output", feed, "/p:PackageVersion=0.0.0-native-evidence-tests", "/p:RepositoryCommit=" + identity.StandardOutput.Trim()],
                repository, TimeSpan.FromMinutes(5))
            {
                MaximumOutputBytes = 8 * 1024 * 1024,
                Environment = new Dictionary<string, string>
                {
                    ["MSBUILDDISABLENODEREUSE"] = "1",
                    ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
                    ["UseSharedCompilation"] = "false",
                },
            });
            Assert.True(pack.ExitCode == 0 && !pack.OutputTruncated, pack.StandardOutput + pack.StandardError);
            var output = Path.Join(parent, "durable-evidence");
            var sentinel = Path.Join(parent, "caller-owned.txt");
            await File.WriteAllTextAsync(sentinel, "preserve caller bytes");
            var hostPath = await runner.RunAsync(new EvidenceCommandRequest(dotnet, ["--list-sdks"], repository, TimeSpan.FromSeconds(30)));
            Assert.Equal(0, hostPath.ExitCode);
            var resolvedHost = Path.IsPathFullyQualified(dotnet) ? dotnet : ResolveDotnetHost();
            var arguments = new List<string> { "--operation", "procedure", "--package", Path.Join(feed, "ForgeTrust.AppSurface.Cli.0.0.0-native-evidence-tests.nupkg"),
                "--version", "0.0.0-native-evidence-tests", "--source-commit", identity.StandardOutput.Trim(), "--dotnet", resolvedHost,
                "--repository", repository, "--scratch-parent", parent, "--output", output, "--mode", mode };
            if (external)
            {
                var consumer = Path.Join(parent, "external-consumer");
                Directory.CreateDirectory(consumer);
                var fixture = Path.Join(repository, "tools/ForgeTrust.AppSurface.Evidence.Validation/fixtures/consumer");
                foreach (var file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(fixture, file);
                    if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")) continue;
                    var target = TestPathUtils.PathUnder(consumer, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target);
                }
                async Task<string> Git(params string[] args)
                {
                    var command = await runner.RunAsync(new EvidenceCommandRequest("git", args, consumer, TimeSpan.FromSeconds(30)));
                    Assert.True(command.ExitCode == 0 && !command.OutputTruncated, command.StandardError);
                    return command.StandardOutput.Trim();
                }
                await Git("init", "--quiet");
                await Git("config", "user.name", "Native evidence fixture");
                await Git("config", "user.email", "native-evidence@example.invalid");
                await Git("add", ".");
                await Git("commit", "--quiet", "-m", "Initial consumer");
                var diffBase = await Git("rev-parse", "HEAD");
                var source = Path.Join(consumer, "Consumer.Beta/BetaDecision.cs");
                await File.WriteAllTextAsync(source, (await File.ReadAllTextAsync(source)).Replace("< 0", "<= -1", StringComparison.Ordinal));
                await Git("add", ".");
                await Git("commit", "--quiet", "-m", "Exercise changed consumer branch");
                var profile = Path.Join(parent, "consumer-profile.json");
                await File.WriteAllTextAsync(profile, JsonSerializer.Serialize(new ConsumerValidationOptions(consumer,
                    await Git("rev-parse", "HEAD"), "Consumer.slnx",
                    ["Consumer.Alpha.Tests/Consumer.Alpha.Tests.csproj", "Consumer.Beta.Tests/Consumer.Beta.Tests.csproj"],
                    ["AlphaDecisionTests", "BetaDecisionTests"], DiffBase: diffBase,
                    Environment: new Dictionary<string, string> { ["EVIDENCE_CONSUMER_TEST"] = "explicit" }), Program.JsonOptions));
                arguments.AddRange(["--consumer-profile", profile]);
            }
            var exitCode = await Program.RunAsync(arguments.ToArray());
            var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(Path.Join(output, "procedure-plan.json")));
            var report = JsonSerializer.Deserialize<NativeValidationProcedureReport>(
                await File.ReadAllBytesAsync(Path.Join(output, "native-validation-procedure.json")), Program.JsonOptions)!;

            Assert.True(exitCode == 0, "Local procedure failed; retained evidence: " + parent);
            Assert.All(report.ProducerResults, result => Assert.True(result.Outcome == EvidenceProducerOutcome.Passed, result.Diagnostic + "; retained evidence: " + parent));
            Assert.True(report.ProcedurePassed);
            Assert.True(report.CleanupCompleted);
            Assert.Equal(plan.PlanDigest, report.PlanDigest);
            Assert.False(report.ProtectedAdmission);
            Assert.False(report.NativeAcceptance);
            Assert.False(report.GateEligible);
            Assert.False(File.Exists(Path.Join(output, "evidence-manifest.json")));
            Assert.Empty(Directory.EnumerateDirectories(parent, "evidence-workspace-*"));
            Assert.Equal("preserve caller bytes", await File.ReadAllTextAsync(sentinel));
            var producer = Assert.Single(report.ProducerResults);
            Assert.Equal(mode == "storage" ? "packed-storage" : "installed-consumer", producer.ProducerId);
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(output, "artifacts", producer.ProducerId, "proof/result.json")));
            if (mode == "consumer")
            {
                Assert.True(result.RootElement.GetProperty("CoverageGatePassed").GetBoolean());
                Assert.Equal(2, result.RootElement.GetProperty("ProjectCount").GetInt32());
                Assert.Equal(5, result.RootElement.GetProperty("TestCaseCount").GetInt32());
            }
            else
            {
                Assert.True(result.RootElement.GetProperty("currentPreserved").GetBoolean());
                Assert.True(result.RootElement.GetProperty("restoredPreserved").GetBoolean());
                Assert.True(result.RootElement.GetProperty("commands").GetInt32() > 0);
                Assert.True(result.RootElement.GetProperty("originalFiles").EnumerateObject().Any());
                Assert.True(File.Exists(Path.Join(output, "artifacts/packed-storage/proof/transcript.json")));
            }
            completed = true;
        }
        finally
        {
            if (completed) Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task InstalledConsumer_ShouldRefuseMissingCandidateWithoutAssertionsAndReleaseScratch()
    {
        var parent = Directory.CreateTempSubdirectory("appsurface-native-missing-package-").FullName;
        try
        {
            var root = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
            var settings = new ValidationSettings(root, Path.Join(parent, "missing.nupkg"), "0.0.0", "source", ResolveDotnetHost(), parent, parent);
            await using var producer = new InstalledConsumerProducer(settings);
            var declaration = Declaration(producer.Id, ["consumer/package-identity@1"], false);
            var context = new EvidenceProducerContext(new EvidencePlan("1", "p", "pd", "diff", new("p", EvidenceProfileScope.Targeted, [], [declaration], []), [], [], "digest"), declaration, TimeProvider.System);

            var result = await producer.ProduceAsync(context, CancellationToken.None);

            Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
            Assert.Empty(result.SatisfiedAssertionIds);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackedStorage_RefusesCommandFailuresAndReleasesOnlyOwnedScratch(bool missingExecutable)
    {
        var parent = Directory.CreateTempSubdirectory("appsurface-native-command-failure-").FullName;
        try
        {
            var packagePath = Path.Join(parent, "candidate.nupkg");
            using (var package = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(package.CreateEntry("candidate.nuspec").Open()))
                    writer.Write("<package><metadata><id>ForgeTrust.AppSurface.Cli</id><version>1.0.0</version><repository commit='source' /></metadata></package>");
                package.CreateEntry("tools/net10.0/any/ForgeTrust.AppSurface.Cli.dll");
            }
            var sentinel = Path.Join(parent, "caller-owned.txt");
            await File.WriteAllTextAsync(sentinel, "preserve");
            var settings = new ValidationSettings(TestPathUtils.FindRepoRoot(AppContext.BaseDirectory), packagePath,
                "1.0.0", "source", missingExecutable ? Path.Join(parent, "missing-dotnet") : ResolveDotnetHost(), parent, parent,
                MinimumFreeBytes: 0);
            await using (var producer = new PackedStorageProducer(settings))
            {
                var declaration = Declaration(producer.Id, ["storage/published-cli@1"], false);
                var context = new EvidenceProducerContext(new EvidencePlan("1", "p", "pd", "diff",
                    new("p", EvidenceProfileScope.Targeted, [], [declaration], []), [], [], "digest"), declaration, TimeProvider.System);

                var result = await producer.ProduceAsync(context, CancellationToken.None);

                Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
                Assert.Empty(result.SatisfiedAssertionIds);
                Assert.Contains(missingExecutable ? "start_failed" : "storage command exit and complete capture", result.Diagnostic, StringComparison.Ordinal);
            }
            Assert.Empty(Directory.EnumerateDirectories(parent, "evidence-workspace-*"));
            Assert.Equal("preserve", await File.ReadAllTextAsync(sentinel));
            Assert.True(File.Exists(packagePath));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    private static EvidenceProducerDeclaration Declaration(string id, string[] assertions, bool transcript) =>
        new(id, "native-validation", "1", [], assertions,
            transcript ? [new("result", "proof", "application/json", true, 1024 * 1024), new("transcript", "proof", "application/json", true, 16 * 1024 * 1024)]
                : [new("result", "proof", "application/json", true, 1024 * 1024)], 900);

    private static string ResolveDotnetHost()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(host) && Path.IsPathFullyQualified(host)) return host;
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(root)) return Path.Join(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(directory => Path.Join(directory, executable)).First(File.Exists);
    }
}
