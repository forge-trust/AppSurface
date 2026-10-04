using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class DurableTemplatePublicReplayTests : IDisposable
{
    private const string Version = "0.2.0-preview.13";
    private const string ProviderId = "ForgeTrust.AppSurface.Durable.PostgreSql";
    private const string TestingId = "ForgeTrust.AppSurface.Durable.Testing";
    private const string SourceCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SecretSentinel = "public-replay-secret-output-must-not-be-retained";
    private const string ProjectName = "FirstDurableWorker";
    private const string ShortName = "appsurface-durable-worker";
    private static readonly string[] FirstWorkCheckpoints =
    [
        "[first-work] authorized activation accepted",
        "[first-work] Work reached terminal completion",
        "[first-work] readiness: NotStarted -> Healthy",
        "[first-work] exported appsurface.durable.runtime.activation"
    ];

    private readonly string _root = TestPathUtils.PathUnder(
        OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
        "template-public-replay",
        Guid.NewGuid().ToString("N"));
    private readonly string _repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);

    public DurableTemplatePublicReplayTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void SigningEnvelopeMayDifferButPayloadMustMatch()
    {
        var candidate = Archive("candidate", [("lib/net10.0/fixture.dll", "payload")]);
        var published = Archive("published",
            [("lib/net10.0/fixture.dll", "payload"), (".signature.p7s", "signature")]);
        DurableTemplatePublicReplay.RequirePayloadIdentity(candidate, published);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("traversal")]
    [InlineData("alias")]
    public void ChangedIncompleteOrUnsafePublicPayloadFails(string fault)
    {
        var candidate = Archive("candidate", [("lib/net10.0/fixture.dll", "payload")]);
        (string Path, string Content)[] entries = fault switch
        {
            "changed" => [("lib/net10.0/fixture.dll", "different")],
            "missing" => [],
            "extra" => [("lib/net10.0/fixture.dll", "payload"), ("other", "extra")],
            "duplicate" => [("lib/net10.0/fixture.dll", "payload"), ("LIB/net10.0/fixture.dll", "payload")],
            "traversal" => [("../fixture.dll", "payload")],
            _ => [("lib\\fixture.dll", "payload")]
        };
        var published = Archive("published", entries);
        Assert.Throws<PackageIndexException>(() => DurableTemplatePublicReplay.RequirePayloadIdentity(candidate, published));
    }

    [Fact]
    public void MissingArchiveFailsWithoutAttemptingRestore()
    {
        var candidate = Archive("candidate", [("payload", "same")]);
        Assert.Throws<PackageIndexException>(() =>
            DurableTemplatePublicReplay.RequirePayloadIdentity(candidate, Path.Join(_root, "missing.nupkg")));
    }

    [Fact]
    public async Task PublicReplayUsesNativeInstallAndCompletesTheBoundPublicConsumerProof()
    {
        var fixture = new ReplayFixture(_root, _repositoryRoot);
        var runner = new ReplayRunner(fixture);

        var failure = await Record.ExceptionAsync(() =>
            DurableTemplatePublicReplay.RunAsync(fixture.Request, fixture.Manifest, fixture.PublicCache, runner, CancellationToken.None));
        Assert.True(failure is null, $"{failure?.Message}; operations: {string.Join(", ", runner.Requests.Select(request => request.OperationName))}");

        var install = Assert.Single(runner.Requests, request => request.OperationName == "public-template-install");
        Assert.Equal("dotnet", install.FileName);
        Assert.Equal(
            ["new", "install", $"{DurableTemplateStaging.PackageId}@{Version}", "--nuget-source", fixture.Request.Source, "--force"],
            install.Arguments);
        Assert.Equal(180_000, install.TimeoutMilliseconds);
        Assert.Equal(ExternalCapturePolicy.ReleaseProof, install.CapturePolicy);
        Assert.Equal(install.WorkingDirectory, runner.PublicRoot);
        Assert.Equal(Path.Join(runner.PublicRoot, "cli"), install.Environment!["DOTNET_CLI_HOME"]);
        Assert.Equal(ExternalCapturePolicy.ReleaseProof,
            Assert.Single(runner.Requests, request => request.OperationName == "public-template-uninstall").CapturePolicy);
        Assert.Contains(runner.Requests, request => request.OperationName == "archive-create");
        Assert.Contains(runner.Requests, request => request.OperationName == "feed-create");
        Assert.False(Directory.Exists(runner.PublicRoot));
        Assert.False(Directory.Exists(runner.ProofRoot));

        var receiptPath = Path.Join(fixture.Request.WorkDirectory, "durable-template-public-replay.json");
        var receipt = JsonSerializer.Deserialize<DurableTemplateProofReceipt>(File.ReadAllBytes(receiptPath), PackageArtifactJson.Options);
        Assert.NotNull(receipt);
        Assert.True(receipt.Succeeded);
        Assert.True(receipt.CleanupComplete);
        Assert.Equal("promoted-public-feed-replay", receipt.Mode);
        Assert.Equal(SourceCommit, receipt.SourceCommit);
        Assert.Equal(
            DurableTemplateArtifactContract.ComputeGeneratedContentSha256(fixture.PublishedTemplate, ProjectName, allowSigningEnvelope: true),
            receipt.GeneratedContentSha256);
        Assert.Equal(OperatingSystem.IsLinux(), receipt.TerminalWork);
        if (OperatingSystem.IsLinux())
        {
            var firstWork = Assert.Single(runner.Requests, request => request.OperationName == "first-work");
            Assert.Contains(FirstWorkCheckpoints[0], runner.OutputFor(firstWork));
        }
    }

    [Fact]
    public async Task FailedPublicInstallStillRunsOwnedCleanupAndDoesNotStartConsumerProof()
    {
        var fixture = new ReplayFixture(_root, _repositoryRoot);
        var runner = new ReplayRunner(fixture, failOperation: "public-template-install");

        var error = await Assert.ThrowsAsync<PackageIndexException>(() =>
            DurableTemplatePublicReplay.RunAsync(fixture.Request, fixture.Manifest, fixture.PublicCache, runner, CancellationToken.None));

        Assert.Contains("replay failed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SecretSentinel, error.ToString(), StringComparison.Ordinal);
        Assert.Contains(runner.Requests, request => request.OperationName == "public-template-uninstall");
        Assert.Contains(runner.Requests, request => request.OperationName == "public-template-absence");
        Assert.DoesNotContain(runner.Requests, request => request.OperationName == "sdk");
        Assert.False(Directory.Exists(runner.PublicRoot));
    }

    [Fact]
    public async Task ChangedInstalledTemplatePayloadFailsBeforeConsumerProofAndCleansPublicRoot()
    {
        var fixture = new ReplayFixture(_root, _repositoryRoot, changePublishedTemplatePayload: true);
        var runner = new ReplayRunner(fixture);

        await Assert.ThrowsAsync<PackageIndexException>(() =>
            DurableTemplatePublicReplay.RunAsync(fixture.Request, fixture.Manifest, fixture.PublicCache, runner, CancellationToken.None));

        Assert.DoesNotContain(runner.Requests, request => request.OperationName == "sdk");
        Assert.Contains(runner.Requests, request => request.OperationName == "public-template-uninstall");
        Assert.False(Directory.Exists(runner.PublicRoot));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task PublicInstallMustRetainExactlyOneMatchingArchive(int retainedArchiveCount)
    {
        var fixture = new ReplayFixture(_root, _repositoryRoot);
        var runner = new ReplayRunner(fixture, retainedArchiveCount: retainedArchiveCount);

        await Assert.ThrowsAsync<PackageIndexException>(() =>
            DurableTemplatePublicReplay.RunAsync(fixture.Request, fixture.Manifest, fixture.PublicCache, runner, CancellationToken.None));

        Assert.DoesNotContain(runner.Requests, request => request.OperationName == "sdk");
        Assert.Contains(runner.Requests, request => request.OperationName == "public-template-absence");
        Assert.False(Directory.Exists(runner.PublicRoot));
    }

    [Fact]
    public async Task FailedConsumerReceiptMakesPublicReplayFailAfterBothOwnedRootsAreCleaned()
    {
        var fixture = new ReplayFixture(_root, _repositoryRoot);
        var runner = new ReplayRunner(fixture, failOperation: "sdk");

        var error = await Assert.ThrowsAsync<PackageIndexException>(() =>
            DurableTemplatePublicReplay.RunAsync(fixture.Request, fixture.Manifest, fixture.PublicCache, runner, CancellationToken.None));

        Assert.Contains("replay failed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(runner.Requests, request => request.OperationName == "sdk");
        Assert.DoesNotContain(SecretSentinel, File.ReadAllText(Path.Join(fixture.Request.WorkDirectory, "durable-template-public-replay.json")), StringComparison.Ordinal);
        Assert.Contains(runner.Requests, request => request.OperationName == "public-template-uninstall");
        Assert.False(Directory.Exists(runner.PublicRoot));
        Assert.False(Directory.Exists(runner.ProofRoot));
    }

    [Fact]
    public async Task CleanupFailureIsReportedAndDoesNotRunTheAbsenceCheck()
    {
        var fixture = new ReplayFixture(_root, _repositoryRoot);
        var runner = new ReplayRunner(fixture, failOperation: "public-template-uninstall");

        var error = await Assert.ThrowsAsync<PackageIndexException>(() =>
            DurableTemplatePublicReplay.RunAsync(fixture.Request, fixture.Manifest, fixture.PublicCache, runner, CancellationToken.None));

        Assert.Contains("cleanup failed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runner.Requests, request => request.OperationName == "public-template-absence");
        Assert.False(Directory.Exists(runner.PublicRoot));
    }

    [Fact]
    public async Task AbsenceCheckRejectsAnInstalledShortNameAndReportsCleanupFailure()
    {
        var fixture = new ReplayFixture(_root, _repositoryRoot);
        var runner = new ReplayRunner(fixture, absenceOutput: $"Template short name: {ShortName}\n");

        var error = await Assert.ThrowsAsync<PackageIndexException>(() =>
            DurableTemplatePublicReplay.RunAsync(fixture.Request, fixture.Manifest, fixture.PublicCache, runner, CancellationToken.None));

        Assert.Contains("cleanup failed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(runner.Requests, request => request.OperationName == "public-template-absence");
        Assert.False(Directory.Exists(runner.PublicRoot));
    }

    [UnixFileSystemFact]
    public async Task CleanupPreservesPublicRootContainingLinkedTree()
    {
        var fixture = new ReplayFixture(_root, _repositoryRoot);
        var outsidePath = Path.Join(_root, "outside-sentinel.txt");
        File.WriteAllText(outsidePath, "preserve me");
        var runner = new ReplayRunner(fixture, failOperation: "public-template-uninstall", linkedTreeTarget: outsidePath);

        try
        {
            var error = await Assert.ThrowsAsync<PackageIndexException>(() =>
                DurableTemplatePublicReplay.RunAsync(fixture.Request, fixture.Manifest, fixture.PublicCache, runner, CancellationToken.None));

            var linkPath = Path.Join(runner.PublicRoot, "unsafe-link");
            Assert.Contains("cleanup failed", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(runner.PublicRoot));
            Assert.True(File.Exists(linkPath));
            Assert.Equal("preserve me", File.ReadAllText(outsidePath));
        }
        finally
        {
            File.Delete(Path.Join(runner.PublicRoot, "unsafe-link"));
            runner.DeletePublicRoot();
        }
    }

    [Fact]
    public void PayloadIdentityRejectsAnArchiveBeyondTheCompressedInputLimit()
    {
        var candidate = Archive("candidate", [("payload", "same")]);
        var oversized = Path.Join(_root, "oversized.nupkg");
        using (var stream = File.Create(oversized)) stream.SetLength(128L * 1024 * 1024 + 1);

        Assert.Throws<PackageIndexException>(() => DurableTemplatePublicReplay.RequirePayloadIdentity(candidate, oversized));
    }

    [Fact]
    public void PayloadIdentityRejectsSigningEnvelopeAboveOneMiB()
    {
        var candidate = Archive("candidate", [("payload", "same")]);
        var published = Path.Join(_root, "oversized-signature.nupkg");
        using (var archive = ZipFile.Open(published, ZipArchiveMode.Create))
        {
            using (var payload = new StreamWriter(archive.CreateEntry("payload").Open()))
                payload.Write("same");
            using var signature = archive.CreateEntry(".signature.p7s").Open();
            signature.Write(new byte[1024 * 1024 + 1]);
        }

        Assert.Throws<PackageIndexException>(() => DurableTemplatePublicReplay.RequirePayloadIdentity(candidate, published));
    }

    private string Archive(string name, (string Path, string Content)[] entries)
    {
        var path = Path.Join(_root, name + ".nupkg");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryPath, content) in entries)
        {
            var entry = archive.CreateEntry(entryPath);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class ReplayFixture
    {
        private readonly string _root;
        private readonly string _repositoryRoot;
        private readonly string _artifacts;
        private readonly string _templateContent;
        private readonly byte[] _providerSql;
        private readonly Dictionary<string, string> _candidateArchives = new(StringComparer.Ordinal);

        internal ReplayFixture(string testRoot, string repositoryRoot, bool changePublishedTemplatePayload = false)
        {
            _root = Path.Join(testRoot, Guid.NewGuid().ToString("N"));
            _repositoryRoot = repositoryRoot;
            _templateContent = TestPathUtils.PathUnder(repositoryRoot, DurableTemplateStaging.ContentPath);
            _artifacts = Path.Join(_root, "artifacts");
            PublicCache = Path.Join(_root, "public-cache");
            Request = new(
                repositoryRoot,
                Path.Join(repositoryRoot, "packages/package-index.yml"),
                Path.Join(_artifacts, "package-artifact-manifest.json"),
                Path.Join(_root, "work"),
                Path.Join(_root, "work/report.md"),
                "https://packages.example.test/v3/index.json");
            Directory.CreateDirectory(_artifacts);
            Directory.CreateDirectory(Request.WorkDirectory);
            Directory.CreateDirectory(PublicCache);

            _providerSql = File.ReadAllBytes(Path.Join(repositoryRoot, "Durable/configure-postgresql-roles.sql"));
            var template = CreateTemplateArchive(Path.Join(_artifacts, $"{DurableTemplateStaging.PackageId}.{Version}.nupkg"));
            var provider = CreateSimpleArchive(Path.Join(_artifacts, $"{ProviderId}.{Version}.nupkg"),
                "contentFiles/any/any/configure-postgresql-roles.sql", _providerSql);
            var testing = CreateSimpleArchive(Path.Join(_artifacts, $"{TestingId}.{Version}.nupkg"), "payload.txt", Encoding.UTF8.GetBytes("test package payload"));
            _candidateArchives.Add(DurableTemplateStaging.PackageId, template);
            _candidateArchives.Add(ProviderId, provider);
            _candidateArchives.Add(TestingId, testing);

            PublishedTemplate = CreateSignedCopy(
                template,
                Path.Join(_root, "public-template.nupkg"),
                changePayload: changePublishedTemplatePayload);
            AddPublishedDependency(ProviderId, provider);
            AddPublishedDependency(TestingId, testing);

            var entries = _candidateArchives.Select(pair => new PackageArtifactManifestEntry(
                pair.Key,
                ProjectPath(pair.Key),
                "publish",
                Path.GetFileName(pair.Value),
                PackageHash.ComputeSha512(pair.Value),
                IsTool: false)).ToArray();
            Manifest = new(1, Version, DateTimeOffset.Parse("2026-10-04T00:00:00Z"), entries);
            File.WriteAllBytes(Request.ArtifactManifestPath,
                JsonSerializer.SerializeToUtf8Bytes(Manifest, PackageArtifactJson.Options));
        }

        internal PackageArtifactManifest Manifest { get; }
        internal PackageSmokeInstallRequest Request { get; }
        internal string PublicCache { get; }
        internal string PublishedTemplate { get; }
        internal string CandidateTemplate => _candidateArchives[DurableTemplateStaging.PackageId];
        internal string TemplateContent => _templateContent;
        internal byte[] ProviderSql => _providerSql;

        private string CreateTemplateArchive(string path)
        {
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            WriteArchiveEntry(archive, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />"u8.ToArray());
            WriteArchiveEntry(archive, "_rels/.rels", "<Relationships />"u8.ToArray());
            WriteArchiveEntry(archive, DurableTemplateStaging.PackageId + ".nuspec",
                Encoding.UTF8.GetBytes($"<package><metadata><id>{DurableTemplateStaging.PackageId}</id><version>{Version}</version><authors>Forge Trust</authors><description>Replay fixture</description><packageTypes><packageType name=\"Template\" /></packageTypes></metadata></package>"));
            WriteArchiveEntry(archive, "README.md", "Package readme"u8.ToArray());
            WriteArchiveEntry(archive, "LICENSE", "Package license"u8.ToArray());
            WriteArchiveEntry(archive, "package/services/metadata/core-properties/00000000000000000000000000000001.psmdcp", "<metadata />"u8.ToArray());
            foreach (var source in DurableTemplateStaging.EnumerateContent(_templateContent))
            {
                var relative = Path.GetRelativePath(_templateContent, source).Replace(Path.DirectorySeparatorChar, '/');
                WriteArchiveEntry(archive, "content/durable-worker/" + relative, File.ReadAllBytes(source));
            }
            return path;
        }

        private string CreateSignedCopy(string candidate, string path, bool changePayload)
        {
            using var inputArchive = ZipFile.Open(candidate, ZipArchiveMode.Read);
            using var outputArchive = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var entry in inputArchive.Entries)
            {
                byte[] bytes;
                if (changePayload && entry.FullName == "content/durable-worker/appsettings.json")
                {
                    bytes = "{\"changed\":\"public payload\"}"u8.ToArray();
                }
                else
                {
                    using var input = entry.Open();
                    using var buffer = new MemoryStream();
                    input.CopyTo(buffer);
                    bytes = buffer.ToArray();
                }
                WriteArchiveEntry(outputArchive, entry.FullName, bytes);
            }
            WriteArchiveEntry(outputArchive, ".signature.p7s", Enumerable.Range(0, 256).Select(value => (byte)value).ToArray());
            return path;
        }

        private void AddPublishedDependency(string packageId, string candidate)
        {
            var signed = Path.Join(_root, packageId + ".published.nupkg");
            using (var archive = ZipFile.Open(candidate, ZipArchiveMode.Read))
            using (var output = ZipFile.Open(signed, ZipArchiveMode.Create))
            {
                foreach (var entry in archive.Entries)
                {
                    using var input = entry.Open();
                    using var buffer = new MemoryStream();
                    input.CopyTo(buffer);
                    WriteArchiveEntry(output, entry.FullName, buffer.ToArray());
                }
                WriteArchiveEntry(output, ".signature.p7s", [1, 2, 3, 4]);
            }
            var lowerId = packageId.ToLowerInvariant();
            var lowerVersion = Version.ToLowerInvariant();
            var destination = Path.Join(PublicCache, lowerId, lowerVersion, $"{lowerId}.{lowerVersion}.nupkg");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(signed, destination);
        }

        private string CreateSimpleArchive(string path, string entryPath, byte[] bytes)
        {
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            WriteArchiveEntry(archive, entryPath, bytes);
            return path;
        }

        private static string ProjectPath(string packageId) => packageId switch
        {
            DurableTemplateStaging.PackageId => "Durable/ForgeTrust.AppSurface.Durable.Templates.csproj",
            ProviderId => "Durable/ForgeTrust.AppSurface.Durable.PostgreSql.csproj",
            _ => "Durable/ForgeTrust.AppSurface.Durable.Testing.csproj"
        };

        private static void WriteArchiveEntry(ZipArchive archive, string path, byte[] bytes)
        {
            var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
            using var output = entry.Open();
            output.Write(bytes);
        }
    }

    private sealed class ReplayRunner : IExternalCommandRunner
    {
        private readonly ReplayFixture _fixture;
        private readonly string? _failOperation;
        private readonly int _retainedArchiveCount;
        private readonly string _absenceOutput;
        private readonly string? _linkedTreeTarget;
        private readonly List<ExternalCommandRequest> _requests = [];
        private readonly Dictionary<ExternalCommandRequest, string> _outputs = [];
        private string? _proofRoot;

        internal ReplayRunner(
            ReplayFixture fixture,
            string? failOperation = null,
            int retainedArchiveCount = 1,
            string absenceOutput = "",
            string? linkedTreeTarget = null)
        {
            _fixture = fixture;
            _failOperation = failOperation;
            _retainedArchiveCount = retainedArchiveCount;
            _absenceOutput = absenceOutput;
            _linkedTreeTarget = linkedTreeTarget;
        }

        internal IReadOnlyList<ExternalCommandRequest> Requests => _requests;
        internal string PublicRoot => _requests.First(request => request.OperationName == "public-template-install").WorkingDirectory;
        internal string ProofRoot => _proofRoot ?? string.Empty;
        internal string OutputFor(ExternalCommandRequest request) => _outputs[request];

        public Task<ExternalCommandResult> RunAsync(ExternalCommandRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requests.Add(request);
            if (request.OperationName == "public-template-uninstall" && _linkedTreeTarget is not null)
                File.CreateSymbolicLink(Path.Join(PublicRoot, "unsafe-link"), _linkedTreeTarget);
            if (request.OperationName == "sdk"
                && request.Environment?.TryGetValue("NUGET_PACKAGES", out var packages) == true
                && packages is not null)
                _proofRoot ??= Path.GetDirectoryName(packages);
            if (request.OperationName == _failOperation)
                return Result(1, "", SecretSentinel);
            if (request.FileName == "git") return Result(0, SourceCommit + "\n", "");

            switch (request.OperationName)
            {
                case "public-template-install":
                    RetainPublicArchive(request);
                    return Result(0, "installed\n", "");
                case "sdk":
                    return Result(0, "10.0.102\n", "");
                case "acquisition-create":
                case "archive-create":
                case "feed-create":
                    MaterializeTemplate(Path.Join(request.WorkingDirectory, ProjectName));
                    break;
                case "acquisition-restore":
                    WriteThirdPartyPackage(request);
                    break;
                case "authored-restore":
                case "archive-restore":
                case "feed-restore":
                    SimulateConsumerRestore(request);
                    break;
                case "authored-build":
                case "archive-build":
                case "feed-build":
                case "replacement-build":
                    if (request.OperationName == "replacement-build") SimulateConsumerRestore(request);
                    WriteSqlOutput(request.Arguments[1]);
                    break;
                case "archive-discover":
                case "feed-discover":
                    return Result(0, $"Template short name: {ShortName}\n", "");
                case "first-work":
                case "replacement-first-work":
                    var checkpoints = string.Join('\n', FirstWorkCheckpoints) + "\n";
                    _outputs[request] = checkpoints;
                    return Result(0, checkpoints, "");
                case "public-template-absence":
                    _outputs[request] = _absenceOutput;
                    return Result(0, _absenceOutput, "");
                case "absence":
                    return Result(0, "", "");
            }
            return Result(0, "", "");
        }

        internal void DeletePublicRoot()
        {
            if (Directory.Exists(PublicRoot)) Directory.Delete(PublicRoot, recursive: true);
        }

        private void RetainPublicArchive(ExternalCommandRequest request)
        {
            var cli = request.Environment!["DOTNET_CLI_HOME"]!;
            for (var index = 0; index < _retainedArchiveCount; index++)
            {
                var directory = Path.Join(cli, "template-cache", index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(directory);
                File.Copy(_fixture.PublishedTemplate,
                    Path.Join(directory, $"{DurableTemplateStaging.PackageId}.{Version}.nupkg"));
            }
        }

        private void WriteThirdPartyPackage(ExternalCommandRequest request)
        {
            var path = Path.Join(request.Environment!["NUGET_PACKAGES"]!, "xunit", "2.9.3", "xunit.2.9.3.nupkg");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "acquisition-only third-party package", new UTF8Encoding(false));
        }

        private void SimulateConsumerRestore(ExternalCommandRequest request)
        {
            var root = request.Arguments[1];
            var configIndex = request.Arguments.ToList().IndexOf("--configfile");
            var config = configIndex >= 0
                ? request.Arguments[configIndex + 1]
                : FindInheritedNuGetConfig(request.WorkingDirectory);
            var document = XDocument.Load(config);
            var feed = document.Descendants("add").Single(element => (string?)element.Attribute("key") == "candidate")
                .Attribute("value")!.Value;
            var hostName = Path.GetFileNameWithoutExtension(
                Directory.EnumerateFiles(Path.Join(root, "src"), "*.csproj", SearchOption.AllDirectories).Single());
            var testName = Path.GetFileNameWithoutExtension(
                Directory.EnumerateFiles(Path.Join(root, "tests"), "*.csproj", SearchOption.AllDirectories).Single());
            var libraries = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [$"{hostName}/1.0.0"] = new { type = "project" },
                [$"{ProviderId}/{Version}"] = new { type = "package" },
                [$"{TestingId}/{Version}"] = new { type = "package" }
            };
            var graph = JsonSerializer.Serialize(new { libraries });
            foreach (var asset in new[]
            {
                Path.Join(root, "src", hostName, "obj", "project.assets.json"),
                Path.Join(root, "tests", testName, "obj", "project.assets.json")
            })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(asset)!);
                File.WriteAllText(asset, graph, new UTF8Encoding(false));
            }
            foreach (var packageId in new[] { ProviderId, TestingId })
            {
                var lowerId = packageId.ToLowerInvariant();
                var source = Path.Join(feed, $"{packageId}.{Version}.nupkg");
                var destination = Path.Join(request.Environment!["NUGET_PACKAGES"]!, lowerId, Version.ToLowerInvariant(), $"{lowerId}.{Version.ToLowerInvariant()}.nupkg");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }
        }

        private void WriteSqlOutput(string root)
        {
            var testName = Path.GetFileNameWithoutExtension(
                Directory.EnumerateFiles(Path.Join(root, "tests"), "*.csproj", SearchOption.AllDirectories).Single());
            var path = Path.Join(root, "tests", testName, "bin", "Debug", "net10.0", "configure-postgresql-roles.sql");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, _fixture.ProviderSql);
        }

        private void MaterializeTemplate(string destinationRoot)
        {
            Directory.CreateDirectory(destinationRoot);
            using var archive = ZipFile.OpenRead(_fixture.CandidateTemplate);
            foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("content/durable-worker/", StringComparison.Ordinal)))
            {
                var relative = entry.FullName["content/durable-worker/".Length..];
                if (relative.StartsWith(".template.config/", StringComparison.Ordinal)) continue;
                var generatedRelative = relative.Replace(DurableTemplateArtifactContract.SourceName, ProjectName, StringComparison.Ordinal);
                var destination = TestPathUtils.PathUnder(destinationRoot, generatedRelative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = entry.Open();
                using var buffer = new MemoryStream();
                input.CopyTo(buffer);
                var content = new UTF8Encoding(false, true).GetString(buffer.ToArray())
                    .Replace(DurableTemplateArtifactContract.SourceName, ProjectName, StringComparison.Ordinal);
                File.WriteAllBytes(destination, new UTF8Encoding(false, true).GetBytes(content));
            }
        }

        private static string FindInheritedNuGetConfig(string directory)
        {
            for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            {
                var path = Path.Join(current.FullName, "NuGet.config");
                if (File.Exists(path)) return path;
            }
            throw new InvalidOperationException("The simulated generated consumer has no inherited NuGet.config.");
        }

        private Task<ExternalCommandResult> Result(int exitCode, string stdout, string stderr)
        {
            return Task.FromResult(new ExternalCommandResult(exitCode, stdout, stderr));
        }
    }
}
