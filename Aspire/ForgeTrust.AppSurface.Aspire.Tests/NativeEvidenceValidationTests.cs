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
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeJourneys_ShouldVerifyPackedAndInstalledBehaviorAndCleanOnlyOwnedScratch(bool external)
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
            var arguments = new List<string> { "--package", Path.Join(feed, "ForgeTrust.AppSurface.Cli.0.0.0-native-evidence-tests.nupkg"),
                "--version", "0.0.0-native-evidence-tests", "--source-commit", identity.StandardOutput.Trim(), "--dotnet", resolvedHost,
                "--repository", repository, "--scratch-parent", parent, "--output", output };
            if (external)
            {
                var consumer = Path.Join(parent, "external-consumer");
                Directory.CreateDirectory(consumer);
                var fixture = Path.Join(repository, "tools/ForgeTrust.AppSurface.Evidence.Validation/fixtures/consumer");
                foreach (var file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(fixture, file);
                    if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")) continue;
                    var target = Path.Join(consumer, relative);
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
            var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(Path.Join(output, "evidence-plan.json")));
            var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(await File.ReadAllBytesAsync(Path.Join(output, "evidence-manifest.json")));

            Assert.True(exitCode == 0, "Native acceptance failed; retained evidence: " + parent);
            Assert.All(manifest.ProducerResults, result => Assert.True(result.Outcome == EvidenceProducerOutcome.Passed, result.Diagnostic + "; retained evidence: " + parent));
            Assert.Equal(EvidenceClaimKind.TargetedComplete, manifest.ClaimKind);
            Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
            Assert.True(manifest.Metrics.CleanupCompleted);
            Assert.Empty(Directory.EnumerateDirectories(parent, "evidence-workspace-*"));
            Assert.Equal("preserve caller bytes", await File.ReadAllTextAsync(sentinel));
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(output, "artifacts/installed-consumer/proof/result.json")));
            Assert.True(result.RootElement.GetProperty("CoverageGatePassed").GetBoolean());
            Assert.Equal(2, result.RootElement.GetProperty("ProjectCount").GetInt32());
            Assert.Equal(5, result.RootElement.GetProperty("TestCaseCount").GetInt32());
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
