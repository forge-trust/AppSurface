using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Validation;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class NativeConsumerTamperTests
{
    // Value: protects=Consumer identity, artifact inventory and final source checks reject real journey tampering;
    // fails_when=The corresponding installed-byte, required-artifact or final-integrity guard is removed;
    // why_new=Positive installed journeys and pure input contracts do not force these post-command failures;
    // seam=EvidenceCommandRunner capture adapter (used by the production runner's default capture path);
    // no_claim=These negative fixtures do not establish downstream consumer acceptance or native Windows execution.
    [Theory]
    [InlineData("installed-dll", "package", "")]
    [InlineData("project-log", "coverage", "manifests")]
    [InlineData("original-source", "integrity", "retention")]
    public async Task ProduceAsync_RejectsTamperedRealJourneyAndCleansOnlyOwnedScratch(
        string mutation, string expectedStage, string expectedDetail)
    {
        var repository = Environment.GetEnvironmentVariable("APPSURFACE_TAMPER_TEST_REPOSITORY")
            ?? TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var parent = Directory.CreateTempSubdirectory("native-consumer-tamper-").FullName;
        try
        {
            var dotnet = ResolveDotnetHost();
            var candidate = await PrepareCandidateAsync(repository, parent, dotnet);
            var candidateHash = Hash(candidate.Path);
            var source = TestPathUtils.PathUnder(parent, "original-consumer");
            CopyFixture(repository, source);
            await GitAsync(source, "init", "--quiet");
            await GitAsync(source, "config", "user.name", "Native tamper fixture");
            await GitAsync(source, "config", "user.email", "native-tamper@example.invalid");
            await GitAsync(source, "add", ".");
            await GitAsync(source, "commit", "--quiet", "-m", "Consumer fixture");
            var sourceCommit = await GitAsync(source, "rev-parse", "HEAD");
            var sourceFile = TestPathUtils.PathUnder(source, "Consumer.Alpha", "AlphaDecision.cs");
            var sourceHash = Hash(sourceFile);
            var sentinel = TestPathUtils.PathUnder(parent, "caller.txt");
            await File.WriteAllTextAsync(sentinel, "preserve caller bytes");
            var scratch = TestPathUtils.PathUnder(parent, "scratch");
            Directory.CreateDirectory(scratch);
            var artifacts = TestPathUtils.PathUnder(parent, "evidence");
            var mutationApplied = false;
            string? changedPath = null;
            string? beforeHash = null;
            string? afterHash = null;

            void MutateAtCommandEof()
            {
                if (mutationApplied) return;
                var workspace = Directory.EnumerateDirectories(scratch, "evidence-workspace-*").SingleOrDefault();
                if (workspace is null) return;
                if (mutation == "installed-dll")
                {
                    var store = TestPathUtils.PathUnder(workspace, "tool", ".store");
                    var executable = TestPathUtils.PathUnder(workspace, "tool",
                        OperatingSystem.IsWindows() ? "appsurface.exe" : "appsurface");
                    if (!Directory.Exists(store) || !File.Exists(executable)) return;
                    changedPath = Directory.EnumerateFiles(store, "ForgeTrust.AppSurface.Cli.dll", SearchOption.AllDirectories).Single();
                    beforeHash = Hash(changedPath);
                    using (var output = new FileStream(changedPath, FileMode.Append, FileAccess.Write))
                        output.WriteByte(42);
                    afterHash = Hash(changedPath);
                }
                else
                {
                    var coverage = TestPathUtils.PathUnder(workspace, "coverage");
                    if (!File.Exists(TestPathUtils.PathUnder(coverage, "coverage-gate.json"))
                        || !File.Exists(TestPathUtils.PathUnder(coverage, "coverage-gate.md"))) return;
                    if (mutation == "project-log")
                    {
                        changedPath = Directory.EnumerateFiles(
                            TestPathUtils.PathUnder(coverage, "projects"), "dotnet-test.log", SearchOption.AllDirectories).First();
                        beforeHash = Hash(changedPath);
                        File.Delete(changedPath);
                    }
                    else
                    {
                        changedPath = sourceFile;
                        beforeHash = Hash(changedPath);
                        File.AppendAllText(changedPath, "\n// mutated after coverage gate\n");
                        afterHash = Hash(changedPath);
                    }
                }
                mutationApplied = true;
            }

            var captureIndex = 0;
            EofBoundary? boundary = null;
            var runner = new EvidenceCommandRunner(stream =>
            {
                if (captureIndex++ % 2 == 0) boundary = new EofBoundary(MutateAtCommandEof);
                return new EofStream(stream, boundary!);
            });
            var declaration = new EvidenceProducerDeclaration("installed-consumer", "native-validation", "1", [],
                ["consumer/package-identity@1", "consumer/executed-tests@1", "consumer/coverage-gate@1", "consumer/source-integrity@1"],
                [new("result", "proof", "application/json", true, 1024 * 1024)], 1800);
            var plan = new EvidencePlan("1", "negative-fixture", "policy", "diff",
                new("negative-fixture", EvidenceProfileScope.Targeted, [], [declaration], []), [], [], "fixture");
            var context = new EvidenceProducerContext(plan, declaration, TimeProvider.System,
                new EvidenceArtifactWriter(declaration, artifacts));
            var settings = new ValidationSettings(repository, candidate.Path, candidate.Version, candidate.Commit,
                dotnet, scratch, artifacts, MinimumFreeBytes: 0,
                Consumer: new ConsumerValidationOptions(source, sourceCommit, "Consumer.slnx",
                    ExpectedRequiredTestFragments: ["AlphaDecisionTests", "BetaDecisionTests"]));
            EvidenceProducerResult result;
            await using (var producer = new InstalledConsumerProducer(settings, runner))
            {
                result = await producer.ProduceAsync(context, CancellationToken.None);
                Assert.True(mutationApplied, "The requested real journey mutation must occur before rejection.");
                Assert.NotNull(changedPath);
                Assert.NotNull(beforeHash);
                if (mutation == "project-log") Assert.False(File.Exists(changedPath));
                else Assert.NotEqual(beforeHash, afterHash);
                Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
                Assert.Empty(result.SatisfiedAssertionIds);
                using var failure = System.Text.Json.JsonDocument.Parse(
                    await File.ReadAllTextAsync(TestPathUtils.PathUnder(artifacts, "proof", "result.json")));
                Assert.False(failure.RootElement.GetProperty("completed").GetBoolean());
                Assert.Equal(expectedStage, failure.RootElement.GetProperty("stage").GetString());
                Assert.Equal(expectedDetail, failure.RootElement.GetProperty("detail").GetString());
                Assert.Equal(0, failure.RootElement.GetProperty("exitCode").GetInt32());
                Assert.True(await context.Artifacts!.VerifyWrittenArtifactsAsync());
            }
            Assert.Empty(Directory.EnumerateDirectories(scratch));
            Assert.Equal("preserve caller bytes", await File.ReadAllTextAsync(sentinel));
            Assert.Equal(candidateHash, Hash(candidate.Path));
            Assert.True(File.Exists(TestPathUtils.PathUnder(source, "Consumer.slnx")));
            if (mutation == "original-source") Assert.NotEqual(sourceHash, Hash(sourceFile));
            else Assert.Equal(sourceHash, Hash(sourceFile));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    private static async Task<Candidate> PrepareCandidateAsync(string repository, string parent, string dotnet)
    {
        // Optional explicit candidate supports isolated exports without Git/build output.
        // Normal repository execution packs its own candidate without rebuilding shared outputs.
        var supplied = Environment.GetEnvironmentVariable("APPSURFACE_TAMPER_TEST_PACKAGE");
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            using var archive = ZipFile.OpenRead(supplied);
            using var input = archive.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).Open();
            var metadata = XDocument.Load(input).Descendants().Single(element => element.Name.LocalName == "metadata");
            var version = metadata.Elements().Single(element => element.Name.LocalName == "version").Value;
            var commit = (string?)metadata.Elements().Single(element => element.Name.LocalName == "repository").Attribute("commit");
            Assert.False(string.IsNullOrWhiteSpace(commit));
            return new Candidate(supplied, version, commit!);
        }
        var identity = await GitAsync(repository, "rev-parse", "HEAD");
        const string ownVersion = "0.0.0-native-consumer-tamper-tests";
        var feed = TestPathUtils.PathUnder(parent, "feed");
        var configuration = AppContext.BaseDirectory.Contains("Release", StringComparison.Ordinal) ? "Release" : "Debug";
        var pack = await new EvidenceCommandRunner().RunAsync(new EvidenceCommandRequest(dotnet,
            ["pack", TestPathUtils.PathUnder(repository, "Cli", "ForgeTrust.AppSurface.Cli", "ForgeTrust.AppSurface.Cli.csproj"),
                "--no-build", "--no-restore", "--configuration", configuration, "--output", feed,
                "/p:PackageVersion=" + ownVersion, "/p:RepositoryCommit=" + identity], repository, TimeSpan.FromMinutes(5)));
        Assert.True(pack.ExitCode == 0 && !pack.OutputTruncated, pack.StandardOutput + pack.StandardError);
        return new Candidate(TestPathUtils.PathUnder(feed, "ForgeTrust.AppSurface.Cli." + ownVersion + ".nupkg"), ownVersion, identity);
    }

    private static void CopyFixture(string repository, string destination)
    {
        var source = TestPathUtils.PathUnder(repository, "tools", "ForgeTrust.AppSurface.Evidence.Validation", "fixtures", "consumer");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or ".git")) continue;
            var target = TestPathUtils.PathUnder(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static async Task<string> GitAsync(string directory, params string[] arguments)
    {
        var result = await new EvidenceCommandRunner().RunAsync(new EvidenceCommandRequest(
            "git", arguments, directory, TimeSpan.FromSeconds(30)));
        Assert.True(result.ExitCode == 0 && !result.OutputTruncated, result.StandardError);
        return result.StandardOutput.Trim();
    }

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

    private static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private sealed record Candidate(string Path, string Version, string Commit);

    private sealed class EofBoundary(Action mutate)
    {
        private int _completed;
        public void Complete()
        {
            if (Interlocked.Increment(ref _completed) == 2) mutate();
        }
    }

    private sealed class EofStream(Stream inner, EofBoundary boundary) : Stream
    {
        private int _completed;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            if (read == 0 && Interlocked.Exchange(ref _completed, 1) == 0) boundary.Complete();
            return read;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
