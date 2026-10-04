using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

/// <summary>Exercises durable-template commands through the real CLI parser and its existing command-runner seam.</summary>
public sealed class DurableTemplateCliTests : IDisposable
{
    private const string Version = "0.2.0-preview.806";
    private const string SourceCommit = "0123456789abcdef0123456789abcdef01234567";
    private const string ProviderId = "ForgeTrust.AppSurface.Durable.PostgreSql";
    private const string TestingId = "ForgeTrust.AppSurface.Durable.Testing";
    private static readonly string[] FirstWorkCheckpoints =
    [
        "[first-work] authorized activation accepted",
        "[first-work] Work reached terminal completion",
        "[first-work] readiness: NotStarted -> Healthy",
        "[first-work] exported appsurface.durable.runtime.activation"
    ];

    private readonly string _root = CreateTestRoot();
    private readonly string _repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);

    [Fact]
    public async Task VerifyDurableTemplate_RunsTheRealCandidateParserAndProofThroughTheInjectedRunner()
    {
        var candidate = await CreateCandidateAsync();
        var runner = new ProofCommandRunner(candidate);
        var report = TestPathUtils.PathUnder(_root, "proof", "receipt.json");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(
            ["verify-durable-template", "--repo-root", _repositoryRoot,
                "--artifacts-input", candidate.Artifacts,
                "--artifact-manifest", candidate.Manifest,
                "--package-version", Version,
                "--source-commit", SourceCommit,
                "--report", report],
            stdout, stderr, _repositoryRoot, preflightCommandRunner: runner);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr.ToString());
        Assert.Contains("Durable template installed-artifact proof passed after owned cleanup.", stdout.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(report));
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(report));
        Assert.True(receipt.RootElement.GetProperty("Succeeded").GetBoolean());
        Assert.True(receipt.RootElement.GetProperty("CleanupComplete").GetBoolean());
        Assert.True(receipt.RootElement.GetProperty("ExactArchiveInstall").GetBoolean());
        Assert.True(receipt.RootElement.GetProperty("FeedInstall").GetBoolean());
        Assert.True(receipt.RootElement.GetProperty("SampleReplacement").GetBoolean());
        Assert.Contains(runner.Requests, request => request.OperationName == "archive-install");
        Assert.Contains(runner.Requests, request => request.OperationName == "feed-tests");
        Assert.Contains(runner.Requests, request => request.OperationName == "replacement-build");
    }

    [Fact]
    public async Task VerifyDurableTemplateEvidence_ValidatesTheCliSuppliedSourceAndReceiptDirectory()
    {
        var (artifacts, manifest, evidence) = await CreateCompleteEvidenceAsync();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(
            ["verify-durable-template-evidence", "--repo-root", _root,
                "--artifacts-input", artifacts,
                "--artifact-manifest", manifest,
                "--durable-template-evidence", evidence,
                "--preflight-source-commit", SourceCommit],
            stdout, stderr, _root);

        Assert.True(exitCode == 0, stderr.ToString());
        Assert.Empty(stderr.ToString());
        Assert.Contains("Durable template source, candidate and three-OS evidence validated before credentials.", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyDurableTemplateEvidence_RejectsMissingTrustedInputsAtTheCliBoundary()
    {
        var (artifacts, manifest, _) = await CreateCompleteEvidenceAsync();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(
            ["verify-durable-template-evidence", "--repo-root", _root,
                "--artifacts-input", artifacts,
                "--artifact-manifest", manifest],
            stdout, stderr, _root);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout.ToString());
        Assert.Contains("requires source-bound Linux, macOS and Windows receipts", stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-clock")]
    [InlineData("duplicate-clock")]
    [InlineData("unknown-clock")]
    [InlineData("missing-docker-host-value")]
    [InlineData("duplicate-docker-hosts")]
    public async Task VerifyDurableTemplateTiming_RejectsMalformedClockAndDockerHostOptionsSafely(string scenario)
    {
        var runner = new ProofCommandRunner(candidate: null);
        string[] arguments = scenario switch
        {
            "missing-clock" => ["verify-durable-template-timing", "--repo-root", _root],
            "duplicate-clock" => ["verify-durable-template-timing", "--clock", "primed", "--clock", "cold"],
            "unknown-clock" => ["verify-durable-template-timing", "--clock", "overnight"],
            "missing-docker-host-value" => ["verify-durable-template-timing", "--clock", "cold", "--docker-hosts"],
            "duplicate-docker-hosts" => ["verify-durable-template-timing", "--clock", "cold",
                "--docker-hosts", "tcp://172.16.0.1:2375", "--docker-hosts", "tcp://172.16.0.2:2375"],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(arguments, stdout, stderr, _root, preflightCommandRunner: runner);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout.ToString());
        Assert.NotEmpty(stderr.ToString());
        Assert.DoesNotContain("stack trace", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task VerifyDurableTemplateTiming_RejectsMissingColdHostsBeforeReadingArtifacts()
    {
        var runner = new ProofCommandRunner(candidate: null);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(
            ["verify-durable-template-timing", .. TimingInputs(), "--clock", "cold"],
            stdout, stderr, _root, preflightCommandRunner: runner);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout.ToString());
        Assert.Contains(OperatingSystem.IsLinux()
                ? "Cold timing requires five private workflow-owned Docker endpoints."
                : "Release timing requires its Linux runner.",
            stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task VerifyDurableTemplateTiming_RejectsAParsedColdHostBeforeLaunchingDocker()
    {
        var candidate = await CreateCandidateAsync();
        var runner = new ProofCommandRunner(candidate);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var hosts = "https://not-a-private-docker-host,tcp://172.16.0.2:2375,tcp://172.16.0.3:2375,tcp://172.16.0.4:2375,tcp://172.16.0.5:2375";

        var exitCode = await Program.RunAsync(
            ["verify-durable-template-timing", .. candidate.TimingArguments(TestPathUtils.PathUnder(_root, "timing", "receipt.json")),
                "--clock", "cold", "--docker-hosts", hosts],
            stdout, stderr, _repositoryRoot, preflightCommandRunner: runner);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout.ToString());
        if (OperatingSystem.IsLinux())
        {
            Assert.Contains("private workflow-owned Docker bridge endpoint", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains(runner.Requests, request => request.FileName == "git");
            Assert.Contains(runner.Requests, request => request.FileName == "dotnet" && request.Arguments.SequenceEqual(["--version"]));
        }
        else
        {
            Assert.Contains("Release timing requires its Linux runner.", stderr.ToString(), StringComparison.Ordinal);
            Assert.Empty(runner.Requests);
        }
        Assert.DoesNotContain(runner.Requests, request => request.FileName == "docker");
    }

    [Fact]
    public async Task PublishPrerelease_ForwardsDurableEvidenceOptionsToThePublisher()
    {
        var artifacts = TestPathUtils.PathUnder(_root, "publisher-artifacts");
        var evidence = TestPathUtils.PathUnder(_root, "publisher-evidence");
        var manifest = TestPathUtils.PathUnder(_root, "publisher-manifest.json");
        PackagePublishRequest? dispatched = null;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(
            ["publish-prerelease", "--repo-root", _root,
                "--artifacts-input", artifacts,
                "--artifact-manifest", manifest,
                "--durable-template-evidence", evidence,
                "--preflight-source-commit", SourceCommit],
            stdout, stderr, _root,
            publishPrereleaseAsync: (request, _) =>
            {
                dispatched = request;
                return Task.FromResult(new PackagePublishLedger(Version, request.Source, []));
            });

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr.ToString());
        Assert.NotNull(dispatched);
        Assert.Equal(Path.GetFullPath(artifacts), dispatched!.ArtifactsInputPath);
        Assert.Equal(Path.GetFullPath(manifest), dispatched.ArtifactManifestPath);
        Assert.Equal(Path.GetFullPath(evidence), dispatched.DurableTemplateEvidenceDirectory);
        Assert.Equal(SourceCommit, dispatched.DurableTemplateSourceCommit);
    }

    [Fact]
    public async Task HelpForDurableTemplateCommandPrecedesOptionParsing()
    {
        var runner = new ProofCommandRunner(candidate: null);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(
            ["verify-durable-template-timing", "--clock", "not-a-clock", "--help"],
            stdout, stderr, _root, preflightCommandRunner: runner);

        Assert.Equal(0, exitCode);
        Assert.Contains("verify-durable-template-timing", stdout.ToString(), StringComparison.Ordinal);
        Assert.Empty(stderr.ToString());
        Assert.Empty(runner.Requests);
    }

    private string[] TimingInputs()
        => ["--repo-root", _repositoryRoot,
            "--artifacts-input", TestPathUtils.PathUnder(_root, "not-created-artifacts"),
            "--artifact-manifest", TestPathUtils.PathUnder(_root, "not-created-manifest.json"),
            "--package-version", Version,
            "--source-commit", SourceCommit,
            "--report", TestPathUtils.PathUnder(_root, "not-created-receipt.json")];

    private async Task<CandidateBundle> CreateCandidateAsync()
    {
        var artifacts = TestPathUtils.PathUnder(_root, "candidate-artifacts");
        Directory.CreateDirectory(artifacts);
        var stagedTemplate = TestPathUtils.PathUnder(_root, "staged-template");
        DurableTemplateStaging.Stage(TestPathUtils.PathUnder(_repositoryRoot, DurableTemplateStaging.ContentPath), stagedTemplate, Version);
        var templateArchive = TestPathUtils.PathUnder(artifacts, $"{DurableTemplateStaging.PackageId}.{Version}.nupkg");
        CreateTemplateArchive(stagedTemplate, templateArchive);

        var providerArchive = TestPathUtils.PathUnder(artifacts, $"{ProviderId}.{Version}.nupkg");
        var providerSql = TestPathUtils.PathUnder(_repositoryRoot, "Durable", "configure-postgresql-roles.sql");
        CreateProviderArchive(providerArchive, providerSql);
        var testingArchive = TestPathUtils.PathUnder(artifacts, $"{TestingId}.{Version}.nupkg");
        await File.WriteAllTextAsync(testingArchive, "deterministic testing candidate");

        var rows = new[]
        {
            new PackageArtifactValidationReportEntry(DurableTemplateStaging.PackageId, "Durable/ForgeTrust.AppSurface.Durable.Templates.csproj", PackagePublishDecision.Publish, [], templateArchive),
            new PackageArtifactValidationReportEntry(ProviderId, "Durable/ForgeTrust.AppSurface.Durable.PostgreSql.csproj", PackagePublishDecision.Publish, [], providerArchive),
            new PackageArtifactValidationReportEntry(TestingId, "Durable/ForgeTrust.AppSurface.Durable.Testing.csproj", PackagePublishDecision.Publish, [], testingArchive)
        };
        var manifest = TestPathUtils.PathUnder(_root, "candidate-artifact-manifest.json");
        await new PackageArtifactManifestWriter().WriteAsync(new PackageArtifactValidationReport(Version, rows), artifacts, manifest, CancellationToken.None);
        return new CandidateBundle(_repositoryRoot, artifacts, manifest, templateArchive, providerArchive, testingArchive, providerSql);
    }

    private async Task<(string Artifacts, string Manifest, string Evidence)> CreateCompleteEvidenceAsync()
    {
        var candidate = await CreateCandidateAsync();
        var producerManifest = await new PackageArtifactManifestReader().ReadAsync(candidate.Manifest, CancellationToken.None);

        var evidence = TestPathUtils.PathUnder(_root, "trusted-evidence");
        Directory.CreateDirectory(evidence);
        var receiptArtifacts = producerManifest.Entries
            .Select(entry => new DurableTemplateProofArtifact(entry.PackageId, producerManifest.PackageVersion, entry.Sha512)).ToArray();
        var generatedContentHash = DurableTemplateArtifactContract.ComputeGeneratedContentSha256(candidate.TemplateArchive, "FirstDurableWorker");
        foreach (var (rid, firstWork) in new[] { ("linux-x64", true), ("osx-arm64", false), ("win-x64", false) })
        {
            var receipt = new DurableTemplateProofReceipt(
                1, SourceCommit, producerManifest.PackageVersion, rid, "10.0.102", DurableTemplateConsumerProof.PostgreSqlImage,
                "candidate-local-feed-correctness", receiptArtifacts, CompletePhases(firstWork),
                true, true, true, true, firstWork, firstWork, firstWork, firstWork,
                true, true, string.Empty, GeneratedContentSha256: generatedContentHash,
                NativeTools: NativeIdentity(), SampleReplacement: true, RunnerImage: "fixture/20261004.1");
            DurableTemplateConsumerProof.WriteReceipt(Path.Join(evidence, rid + ".json"), receipt);
        }
        foreach (var mode in new[] { DurableTemplateTimingMode.Primed, DurableTemplateTimingMode.Cold })
            DurableTemplateTimingWorkflow.WriteReceipt(Path.Join(evidence, mode == DurableTemplateTimingMode.Primed ? "timing-primed.json" : "timing-cold.json"),
                CreateTimingReceipt(mode, producerManifest));
        return (candidate.Artifacts, candidate.Manifest, evidence);
    }

    private static DurableTemplateTimingProofReceipt CreateTimingReceipt(DurableTemplateTimingMode mode, PackageArtifactManifest manifest)
    {
        var artifacts = manifest.Entries
            .Select(entry => new DurableTemplateTimingArtifact(entry.PackageId, manifest.PackageVersion, entry.Sha512)).ToArray();
        var cacheHash = new string('c', 64);
        var request = new DurableTemplateTimingProofRequest(
            "cli-dispatch-series", mode, DurableTemplateTimingFeedKind.CandidateLocal, "candidate-local-feed",
            SourceCommit, manifest.PackageVersion, "linux-x64", "ubuntu-latest", "10.0.102", DurableTemplateConsumerProof.PostgreSqlImage,
            mode == DurableTemplateTimingMode.Primed ? cacheHash : null, 1000, artifacts);
        var imageDigest = DurableTemplateConsumerProof.PostgreSqlImage.Split('@', 2)[1];
        var emptyCacheHash = Convert.ToHexStringLower(SHA256.HashData([]));
        var samples = Enumerable.Range(1, 5).Select(index => new DurableTemplateTimingSample(
            index, "cli-sample-" + index, request.SeriesId, mode, request.FeedKind, request.FeedIdentity,
            SourceCommit, manifest.PackageVersion, DurableTemplateTimingProof.ComputeArtifactSetSha256(artifacts),
            request.RuntimeIdentifier, request.RunnerImage, request.SdkVersion, new string((char)('0' + index), 64),
            mode == DurableTemplateTimingMode.Primed, mode == DurableTemplateTimingMode.Primed ? imageDigest : string.Empty,
            imageDigest, new string((char)('a' + index), 64), mode == DurableTemplateTimingMode.Primed ? cacheHash : emptyCacheHash,
            mode == DurableTemplateTimingMode.Cold, true, false, new string((char)('f' - index), 64),
            new string((char)('1' + index), 64), index * 110_000L, index * 110_000L + 100_000L, 45,
            [new(DurableTemplateTimingCommandPhase.TemplateInstall,
                    DurableTemplateTimingProof.ComputeCommandArgumentsSha256("dotnet", ["new", "install", $"{DurableTemplateStaging.PackageId}@{manifest.PackageVersion}"]), 10, 0, false),
             new(DurableTemplateTimingCommandPhase.ProjectCreate,
                    DurableTemplateTimingProof.ComputeCommandArgumentsSha256("dotnet", ["new", DurableTemplateArtifactContract.ShortName, "-n", "FirstDurableWorker"]), 10, 0, false),
             new(DurableTemplateTimingCommandPhase.FirstDurableWorkTest,
                    DurableTemplateTimingProof.ComputeCommandArgumentsSha256("dotnet", ["test", "FirstDurableWorker", "--filter", "FullyQualifiedName~FirstDurableWork", "--logger", "console;verbosity=detailed"]), 20, 0, false)],
            new(true, true, true, true), true, true, string.Empty)).ToArray();
        var receipt = DurableTemplateTimingProof.Evaluate(request, samples);
        Assert.True(receipt.Succeeded,
            $"{mode} timing fixture failed: {receipt.FailureCode}; "
            + string.Join("; ", receipt.Samples.SelectMany(sample => sample.ValidationFailures)));
        return receipt;
    }

    private static NativePostgreSqlToolIdentity NativeIdentity()
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["initdb"] = new('c', 64),
            ["postgres"] = new('d', 64),
            ["pg_ctl"] = new('e', 64),
            ["psql"] = new('f', 64)
        };
        return new("/native/bin", "/native/bin/initdb", "/native/bin/postgres", "/native/bin/pg_ctl", "/native/bin/psql",
            "16.5", 16, "16.5", 160005, hashes);
    }

    private static DurableTemplateProofPhase[] CompletePhases(bool firstWork)
    {
        var phases = new List<string>
        {
            "sdk", "archive-install", "acquisition-create", "acquisition-restore", "uninstall", "absence",
            "authored-restore", "authored-build", "authored-format", "authored-tests",
            "archive-install", "archive-discover", "archive-create", "archive-restore", "archive-build", "archive-format", "archive-tests", "uninstall", "absence",
            "feed-install", "feed-discover", "feed-create", "feed-restore", "feed-build", "feed-format", "feed-tests"
        };
        if (firstWork) phases.Add("first-work");
        phases.AddRange(["replacement-build", "replacement-tests"]);
        if (firstWork) phases.Add("replacement-first-work");
        phases.AddRange(["uninstall", "absence"]);
        return phases.Select(id => new DurableTemplateProofPhase(id, 1, 0, false)).ToArray();
    }

    private static void CreateTemplateArchive(string stagedContent, string archivePath)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        WriteArchiveText(archive, "[Content_Types].xml", "<Types />");
        WriteArchiveText(archive, "_rels/.rels", "<Relationships />");
        WriteArchiveText(archive, "README.md", "Temporary exact candidate archive used by the CLI test.");
        WriteArchiveText(archive, "LICENSE", "Test fixture metadata only.");
        WriteArchiveText(archive, DurableTemplateStaging.PackageId + ".nuspec",
            $"<package><metadata><id>{DurableTemplateStaging.PackageId}</id><version>{Version}</version><authors>Forge Trust</authors><description>Test candidate</description><packageTypes><packageType name=\"Template\" /></packageTypes></metadata></package>");
        WriteArchiveText(archive, $"package/services/metadata/core-properties/{Guid.NewGuid():N}.psmdcp", "<metadata />");
        foreach (var file in DurableTemplateStaging.EnumerateContent(stagedContent))
        {
            var relative = Path.GetRelativePath(stagedContent, file).Replace(Path.DirectorySeparatorChar, '/');
            var entry = archive.CreateEntry("content/durable-worker/" + relative, CompressionLevel.NoCompression);
            using var input = File.OpenRead(file);
            using var output = entry.Open();
            input.CopyTo(output);
        }
    }

    private static void CreateProviderArchive(string archivePath, string sqlPath)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("contentFiles/any/any/configure-postgresql-roles.sql", CompressionLevel.NoCompression);
        using var input = File.OpenRead(sqlPath);
        using var output = entry.Open();
        input.CopyTo(output);
    }

    private static void WriteArchiveText(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string CreateTestRoot()
    {
        var temporary = Path.GetFullPath(Path.GetTempPath());
        if (OperatingSystem.IsMacOS() && temporary.StartsWith("/var/", StringComparison.Ordinal)) temporary = "/private" + temporary;
        if (OperatingSystem.IsMacOS() && temporary.StartsWith("/tmp/", StringComparison.Ordinal)) temporary = "/private" + temporary;
        return TestPathUtils.PathUnder(temporary, "durable-template-cli-tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record CandidateBundle(
        string RepositoryRoot,
        string Artifacts,
        string Manifest,
        string TemplateArchive,
        string ProviderArchive,
        string TestingArchive,
        string ProviderSql)
    {
        internal string[] TimingArguments(string report)
            => ["--repo-root", RepositoryRoot,
                "--artifacts-input", Artifacts,
                "--artifact-manifest", Manifest,
                "--package-version", Version,
                "--source-commit", SourceCommit,
                "--report", report];
    }

    private sealed class ProofCommandRunner(CandidateBundle? candidate, string timingSdkOutput = "10.0.102\n") : IExternalCommandRunner
    {
        private readonly Dictionary<string, string> _candidateArchives = candidate is null
            ? new(StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DurableTemplateStaging.PackageId] = candidate.TemplateArchive,
                [ProviderId] = candidate.ProviderArchive,
                [TestingId] = candidate.TestingArchive
            };

        internal List<ExternalCommandRequest> Requests { get; } = [];

        public Task<ExternalCommandResult> RunAsync(ExternalCommandRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (request.FileName == "git") return Result(0, SourceCommit + Environment.NewLine);

            var toolName = Path.GetFileNameWithoutExtension(request.FileName);
            if (new[] { "initdb", "postgres", "pg_ctl", "psql" }.Contains(toolName, StringComparer.Ordinal)
                && request.Arguments.SequenceEqual(["--version"]))
                return Result(0, $"{toolName} (PostgreSQL) 16.5{Environment.NewLine}");

            if (request.FileName != "dotnet")
                throw new InvalidOperationException($"Unexpected external command '{request.FileName}'.");

            if (request.Arguments.SequenceEqual(["--version"])) return Result(0, timingSdkOutput);
            if (request.OperationName is "acquisition-create" or "archive-create" or "feed-create")
            {
                MaterializeTemplate(Path.Join(request.WorkingDirectory, "FirstDurableWorker"));
                return Result(0);
            }
            if (request.OperationName.EndsWith("-discover", StringComparison.Ordinal))
                return Result(0, "Template short name: appsurface-durable-worker\n");
            if (request.OperationName is "acquisition-restore" or "authored-restore" or "archive-restore" or "feed-restore")
            {
                if (request.OperationName != "acquisition-restore")
                    WriteRestoredGraph(request.Arguments[1], request.Environment!["NUGET_PACKAGES"]!);
                return Result(0);
            }
            if (request.OperationName.EndsWith("-build", StringComparison.Ordinal))
            {
                var projectRoot = request.Arguments[1];
                WriteRestoredGraph(projectRoot, request.Environment!["NUGET_PACKAGES"]!);
                WriteSqlOutput(projectRoot);
                return Result(0);
            }
            if (request.OperationName is "first-work" or "replacement-first-work")
                return Result(0, string.Join(Environment.NewLine, FirstWorkCheckpoints) + Environment.NewLine);

            return Result(0);
        }

        private void MaterializeTemplate(string generatedRoot)
        {
            Directory.CreateDirectory(generatedRoot);
            using var archive = ZipFile.OpenRead(candidate!.TemplateArchive);
            foreach (var entry in archive.Entries.Where(item => item.FullName.StartsWith("content/durable-worker/", StringComparison.Ordinal)))
            {
                var relative = entry.FullName["content/durable-worker/".Length..];
                if (relative.StartsWith(".template.config/", StringComparison.Ordinal)) continue;
                var generatedRelative = relative.Replace(DurableTemplateArtifactContract.SourceName, "FirstDurableWorker", StringComparison.Ordinal);
                var destination = TestPathUtils.PathUnder(generatedRoot, generatedRelative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = entry.Open();
                using var buffer = new MemoryStream();
                input.CopyTo(buffer);
                var source = new UTF8Encoding(false, true).GetString(buffer.ToArray());
                var content = source.Replace(DurableTemplateArtifactContract.SourceName, "FirstDurableWorker", StringComparison.Ordinal);
                File.WriteAllBytes(destination, new UTF8Encoding(false).GetBytes(content));
            }
        }

        private void WriteRestoredGraph(string projectRoot, string cache)
        {
            foreach (var (packageId, source) in new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProviderId] = candidate!.ProviderArchive,
                [TestingId] = candidate.TestingArchive
            })
            {
                var lowerId = packageId.ToLowerInvariant();
                var destination = TestPathUtils.PathUnder(cache, lowerId, Version.ToLowerInvariant(), $"{lowerId}.{Version.ToLowerInvariant()}.nupkg");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }

            var libraries = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["FirstDurableWorker/1.0.0"] = new { type = "project" },
                ["AppSurfaceDurableWorker/1.0.0"] = new { type = "project" },
                [$"{ProviderId}/{Version}"] = new { type = "package" },
                [$"{TestingId}/{Version}"] = new { type = "package" }
            };
            var json = JsonSerializer.Serialize(new { libraries });
            foreach (var project in Directory.EnumerateFiles(projectRoot, "*.csproj", SearchOption.AllDirectories))
            {
                var assets = TestPathUtils.PathUnder(Path.GetDirectoryName(project)!, "obj", "project.assets.json");
                Directory.CreateDirectory(Path.GetDirectoryName(assets)!);
                File.WriteAllText(assets, json, new UTF8Encoding(false));
            }
        }

        private void WriteSqlOutput(string projectRoot)
        {
            var testProject = Directory.EnumerateFiles(projectRoot, "*.Tests.csproj", SearchOption.AllDirectories).Single();
            var output = TestPathUtils.PathUnder(Path.GetDirectoryName(testProject)!, "bin", "Debug", "net10.0", "configure-postgresql-roles.sql");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(candidate!.ProviderSql, output, overwrite: true);
        }

        private static Task<ExternalCommandResult> Result(int exitCode, string output = "")
            => Task.FromResult(new ExternalCommandResult(exitCode, output, string.Empty));
    }
}
