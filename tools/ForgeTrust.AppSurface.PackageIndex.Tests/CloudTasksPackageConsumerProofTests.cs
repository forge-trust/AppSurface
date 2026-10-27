using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.PackageIndex.Tests
{
    public sealed class CloudTasksPackageConsumerProofTests
    {
        // protects=asset graph selection cannot silently omit additional target frameworks; fails_when=multi-TFM restore passes through as the first target; no_claim=does not prove target-specific packages are equivalent.
        [Fact]
        public void ReadGraph_RejectsMultipleTargets()
        {
            var path = WriteAssets(new { targets = new { net10 = new { }, net9 = new { } }, libraries = new { } });
            try
            {
                var error = Assert.Throws<PackageIndexException>(() => CloudTasksPackageConsumerProof.ReadGraph(path));
                Assert.Contains("exactly one target framework", error.Message, StringComparison.Ordinal);
            }
            finally { File.Delete(path); }
        }

        // protects=assets filtering and optional metadata branches are explicit; fails_when=project libraries or malformed graph identities become package nodes or absent hashes fail to be recorded; no_claim=does not claim a valid NuGet restore.
        [Fact]
        public void ReadGraph_IgnoresMalformedAndProjectNodesAndReadsOptionalMetadata()
        {
            var target = new Dictionary<string, object>
            {
                ["malformed"] = new { },
                ["Project/1.0.0"] = new { dependencies = new { } },
                ["NoHash/1.0.0"] = new { },
                ["WithDependencies/2.0.0"] = new { dependencies = new Dictionary<string, string> { ["Other"] = "3.0.0" } },
            };
            var libraries = new Dictionary<string, object>
            {
                ["Project/1.0.0"] = new { type = "project", sha512 = "project-hash" },
                ["NoHash/1.0.0"] = new { type = "package" },
                ["WithDependencies/2.0.0"] = new { type = "package", sha512 = "package-hash" },
            };
            var project = new
            {
                frameworks = new
                {
                    net10 = new
                    {
                        dependencies = new Dictionary<string, object>
                        {
                            ["ObjectRange"] = new { version = "[1.0.0, )" },
                            ["StringRange"] = "2.0.0",
                            ["OtherValue"] = true,
                        },
                    },
                    net9 = new { dependencies = new { Ignored = "9.0.0" } },
                },
            };
            var path = WriteAssets(new { targets = new Dictionary<string, object> { ["net10.0"] = target }, libraries, project });
            try
            {
                var graph = CloudTasksPackageConsumerProof.ReadGraph(path);
                Assert.Equal(2, graph.Nodes.Count);
                Assert.Equal(string.Empty, graph.Nodes["nohash/1.0.0"].Sha512);
                Assert.Equal("package-hash", graph.Nodes["withdependencies/2.0.0"].Sha512);
                Assert.Equal(new[] { "other:3.0.0" }, graph.Nodes["withdependencies/2.0.0"].Dependencies);
                Assert.Equal("[1.0.0, )", graph.DirectDependencies["ObjectRange"]);
                Assert.Equal("2.0.0", graph.DirectDependencies["StringRange"]);
                Assert.Equal(string.Empty, graph.DirectDependencies["OtherValue"]);
                Assert.DoesNotContain("Ignored", graph.DirectDependencies.Keys);
            }
            finally { File.Delete(path); }
        }

        // protects=approved baseline extraction is anchored to its requested commit; fails_when=checkout HEAD or dirty tracked files leak into baseline tree; no_claim=does not prove baseline package hash parity.
        [Fact]
        public async Task ExtractBaselineAsync_UsesPinnedCommitTreeAndHonorsCancellation()
        {
            var root = PackedProofFixture.FindRepositoryRoot();
            var target = Path.Combine(Path.GetTempPath(), "cloudtasks-baseline-extract-" + Guid.NewGuid().ToString("N"));
            try
            {
                await CloudTasksPackageConsumerProof.ExtractBaselineAsync(root, target, CancellationToken.None);

                Assert.True(File.Exists(Path.Combine(target, "ForgeTrust.AppSurface.slnx")));
                Assert.False(Directory.Exists(Path.Combine(target, ".git")));
                Assert.False(File.Exists(Path.Combine(target, "Durable", "ForgeTrust.AppSurface.Durable.CloudTasks", "ForgeTrust.AppSurface.Durable.CloudTasks.csproj")));
            }
            finally
            {
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            }
        }

        [Fact]
        public void AssertNeutralGraph_AllowsOnlyCoordinatedFirstPartyVersionAndPackageArchiveHashChanges()
        {
            var baseline = Graph(
                Node("ForgeTrust.AppSurface.Durable", "0.0.0-cloudtasksbaseline", "baseline-package-hash", "ForgeTrust.AppSurface.Core", "System.Text.Json"),
                Node("ForgeTrust.AppSurface.Core", "0.0.0-cloudtasksbaseline", "baseline-core-hash"),
                Node("System.Text.Json", "10.0.0", "external-hash"));
            var candidate = Graph(
                Node("ForgeTrust.AppSurface.Durable", "0.2.0-preview.1", "candidate-package-hash", "ForgeTrust.AppSurface.Core", "System.Text.Json"),
                Node("ForgeTrust.AppSurface.Core", "0.2.0-preview.1", "candidate-core-hash"),
                Node("System.Text.Json", "10.0.0", "external-hash"));

            CloudTasksPackageConsumerProof.AssertNeutralGraph(baseline, candidate);
        }

        [Fact]
        public void AssertNeutralGraph_RejectsGoogleGrpcAndGaxProviderEdges()
        {
            foreach (var packageId in new[] { "Google.Cloud.Tasks.V2", "Google.Api.Gax.Grpc", "Grpc.Net.Client" })
            {
                var graph = Graph(Node(packageId, "3.7.0", "provider-hash"));
                var exception = Assert.Throws<PackageIndexException>(
                    () => CloudTasksPackageConsumerProof.AssertNeutralGraph(graph, graph));
                Assert.Contains("forbidden provider dependency", exception.Message, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void AssertNeutralGraph_RejectsFirstPartyDependencyEdgeDrift()
        {
            var baseline = Graph(
                Node("ForgeTrust.AppSurface.Durable", "0.0.0-cloudtasksbaseline", "hash", "ForgeTrust.AppSurface.Core"),
                Node("ForgeTrust.AppSurface.Core", "0.0.0-cloudtasksbaseline", "core-hash"));
            var candidate = Graph(
                Node("ForgeTrust.AppSurface.Durable", "0.2.0-preview.1", "hash", "ForgeTrust.AppSurface.Workers"),
                Node("ForgeTrust.AppSurface.Workers", "0.2.0-preview.1", "worker-hash"));

            var exception = Assert.Throws<PackageIndexException>(
                () => CloudTasksPackageConsumerProof.AssertNeutralGraph(baseline, candidate));
            Assert.Contains("dependency graph changed", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AssertNeutralGraph_RejectsExternalVersionOrContentHashDrift()
        {
            var baseline = Graph(Node("System.Text.Json", "10.0.0", "before"));
            var candidate = Graph(Node("System.Text.Json", "10.0.1", "after"));

            var exception = Assert.Throws<PackageIndexException>(
                () => CloudTasksPackageConsumerProof.AssertNeutralGraph(baseline, candidate));
            Assert.Contains("dependency graph changed", exception.Message, StringComparison.Ordinal);

            var sameVersionDifferentHash = Graph(Node("System.Text.Json", "10.0.0", "after"));
            var hashException = Assert.Throws<PackageIndexException>(
                () => CloudTasksPackageConsumerProof.AssertNeutralGraph(baseline, sameVersionDifferentHash));
            Assert.Contains("content hashes", hashException.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("ForgeTrust.AppSurface.Durable/0.1.0", "forgetrust.appsurface.durable/<coordinated>")]
        [InlineData("Google.Cloud.Tasks.V2/3.7.0", "google.cloud.tasks.v2/3.7.0")]
        public void CanonicalizePackageId_NormalizesOnlyFirstPartyVersion(string input, string expected)
            => Assert.Equal(expected, CloudTasksPackageConsumerProof.CanonicalizePackageId(input, "0.1.0"));

        [Theory]
        [InlineData("ForgeTrust.AppSurface.Durable.CloudTasks", "Publish", null, false, true)]
        [InlineData("ForgeTrust.AppSurface.Durable", "SupportPublish", null, false, true)]
        [InlineData("ForgeTrust.AppSurface.Durable.CloudTasks", "DoNotPublish", "#892", false, false)]
        [InlineData("ForgeTrust.AppSurface.Durable.CloudTasks", "DoNotPublish", "#892", true, true)]
        [InlineData("ForgeTrust.AppSurface.Durable", "DoNotPublish", "#123", true, false)]
        [InlineData("ForgeTrust.AppSurface.Durable.CloudTasks", "DoNotPublish", null, true, false)]
        public void VerificationPlanIncludesReadinessBlockedEntriesWithoutChangingDefaultPlan(
            string packageId,
            string decision,
            string? blocker,
            bool verifyBlocked,
            bool expected)
            => Assert.Equal(expected, PackagePublishPlanResolver.ShouldIncludeForVerification(packageId, Enum.Parse<PackagePublishDecision>(decision), blocker, verifyBlocked));

        [Fact]
        public void FindNeutralProjectReferenceViolations_RejectsDirectGooglePackageReferenceFromNeutralConsumer()
        {
            var root = Path.Combine(Path.GetTempPath(), "cloudtasks-neutral-fixture-" + Guid.NewGuid().ToString("N"));
            var consumer = Path.Combine(root, "Durable", "packed-consumers", "Adopter");
            Directory.CreateDirectory(consumer);
            File.WriteAllText(Path.Combine(consumer, "Adopter.csproj"), "<Project><ItemGroup><PackageReference Include=\"Google.Cloud.Tasks.V2\" /></ItemGroup></Project>");
            try
            {
                var violations = CloudTasksPackageConsumerProof.FindNeutralProjectReferenceViolations(root);
                Assert.Contains("Durable/packed-consumers/Adopter/Adopter.csproj:Google.Cloud.Tasks.V2", violations, StringComparer.Ordinal);
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void ScanPublicApiForGoogleTypes_DetectsProviderTypesInPublicMemberSignatures()
        {
            var package = Path.Combine(Path.GetTempPath(), "cloudtasks-api-leak-" + Guid.NewGuid().ToString("N") + ".nupkg");
            try
            {
                using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
                {
                    var assembly = archive.CreateEntry("lib/net10.0/PackageIndex.Tests.dll");
                    using (var output = assembly.Open())
                    using (var input = File.OpenRead(typeof(CloudTasksPackageConsumerProofTests).Assembly.Location)) input.CopyTo(output);
                    archive.CreateEntry("lib/net10.0/PackageIndex.Tests.xml");
                }

                var leaks = CloudTasksPackageConsumerProof.ScanPublicApiForGoogleTypes(package, Path.Combine(Path.GetTempPath(), "missing-cloudtasks-dependencies"));

                Assert.Contains(leaks, value => value.Contains("Google.Cloud.Tasks.V2", StringComparison.Ordinal));
            }
            finally
            {
                if (File.Exists(package)) File.Delete(package);
            }
        }

        [Fact]
        public async Task RunAsync_UsesValidatedPackagesLockedIsolatedConsumersAndWritesGraphEvidence()
        {
            using var fixture = new PackedProofFixture();
            var runner = new CloudTasksProofRunner(fixture.PackageArtifacts, Version, fixture.LockFilePath);
            var request = fixture.Request;
            var report = await new CloudTasksPackageConsumerProof(runner, fixture.CreatePinnedSourceAsync).RunAsync(request, fixture.Artifacts);

            Assert.Equal(CloudTasksPackageConsumerProof.BaselineCommit, report.BaselineCommit);
            Assert.Equal(4, report.NeutralPackageFiles.Count);
            Assert.Equal(4, report.BaselineGraph.Nodes.Count);
            Assert.Equal(4, report.CandidateGraph.Nodes.Count);
            Assert.Equal(ReadLockedPackageCount(fixture.LockFilePath) + 2, report.CloudTasksGraph.Nodes.Count);
            Assert.True(File.Exists(report.ReportPath));
            var evidence = await File.ReadAllTextAsync(report.ReportPath);
            Assert.Contains("Provider SDK API leaks: none", evidence, StringComparison.Ordinal);
            Assert.Contains("Google.Cloud.Tasks.V2", evidence, StringComparison.Ordinal);
            Assert.All(runner.ConsumerRestoreRequests, command =>
            {
                var environment = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(command.Environment);
                Assert.NotNull(environment["NUGET_PACKAGES"]);
                Assert.NotNull(environment["DOTNET_CLI_HOME"]);
                Assert.Null(environment["DirectoryPackagesPropsPath"]);
                Assert.False(environment.ContainsKey("MSBuildProjectExtensionsPath"));
                Assert.Equal("<Project />", runner.EmptyBuildPropsContents[runner.ConsumerRestoreRequests.IndexOf(command)]);
            });
            Assert.Equal(6, runner.ConsumerRestoreRequests.Count);
        }

        [Fact]
        public async Task RunAsync_RejectsGoogleEdgeInNeutralGraphAndCleansWorkspace()
        {
            using var fixture = new PackedProofFixture();
            var runner = new CloudTasksProofRunner(fixture.PackageArtifacts, Version, fixture.LockFilePath, neutralGraphContainsGoogle: true);
            var exception = await Assert.ThrowsAsync<PackageIndexException>(() =>
                new CloudTasksPackageConsumerProof(runner, fixture.CreatePinnedSourceAsync).RunAsync(fixture.Request, fixture.Artifacts));

            Assert.Contains("forbidden provider dependency", exception.Message, StringComparison.Ordinal);
            Assert.NotEmpty(runner.ConsumerRestoreRequests);
            var workspace = Directory.GetParent(runner.ConsumerRestoreRequests[0].WorkingDirectory)!.FullName;
            Assert.False(Directory.Exists(workspace));
        }

        // protects=version consistency between the request and validator report is checked before package inspection; fails_when=stale report reaches command execution; no_claim=does not repeat package validator checks.
        [Fact]
        public async Task RunAsync_RejectsReportVersionMismatchBeforeCommands()
        {
            using var fixture = new PackedProofFixture();
            var runner = new CloudTasksProofRunner(fixture.PackageArtifacts, Version, fixture.LockFilePath);
            var error = await Assert.ThrowsAsync<PackageIndexException>(() =>
                new CloudTasksPackageConsumerProof(runner, fixture.CreatePinnedSourceAsync).RunAsync(
                    fixture.Request,
                    fixture.Artifacts with { PackageVersion = "0.9.0" }));

            Assert.Contains("does not match the validated artifact report", error.Message, StringComparison.Ordinal);
            Assert.Empty(runner.ConsumerRestoreRequests);
        }

        // protects=runtime package lock closure is complete and content-bound; fails_when=a removed/new transitive node, changed lock hash or missing direct edge is accepted; no_claim=fixture assets do not replace an actual NuGet restore.
        [Theory]
        [InlineData("missing")]
        [InlineData("extra")]
        [InlineData("hash")]
        [InlineData("edge")]
        public async Task RunAsync_RejectsCloudTasksLockClosureDrift(string changedPart)
        {
            using var fixture = new PackedProofFixture();
            var runner = new CloudTasksProofRunner(fixture.PackageArtifacts, Version, fixture.LockFilePath, invalidCloudTasksGraph: changedPart);

            var error = await Assert.ThrowsAsync<PackageIndexException>(() =>
                new CloudTasksPackageConsumerProof(runner, fixture.CreatePinnedSourceAsync).RunAsync(fixture.Request, fixture.Artifacts));

            Assert.Contains(changedPart == "edge" ? "direct packages.lock.json roots" : "differs from packages.lock.json", error.Message, StringComparison.Ordinal);
            Assert.Equal(2, runner.ConsumerRestoreRequests.Count);
        }

        // protects=the candidate revision used for normalized baseline packing is canonical and explicit; fails_when=null request revisions skip Git resolution or invalid output is packed; no_claim=does not authenticate the repository remote.
        [Fact]
        public async Task RunAsync_ResolvesAndUsesCanonicalCandidateRevisionWhenRequestOmitsIt()
        {
            using var fixture = new PackedProofFixture();
            const string candidateRevision = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
            var runner = new CloudTasksProofRunner(fixture.PackageArtifacts, Version, fixture.LockFilePath, candidateRevisionOutput: candidateRevision);

            await new CloudTasksPackageConsumerProof(runner, fixture.CreatePinnedSourceAsync).RunAsync(
                fixture.Request with { SourceCommit = null }, fixture.Artifacts);

            var revisionRequest = Assert.Single(runner.CandidateRevisionRequests);
            Assert.Equal("git", revisionRequest.FileName);
            Assert.Equal(new[] { "rev-parse", "HEAD" }, revisionRequest.Arguments);
            Assert.Equal(fixture.Request.RepositoryRoot, revisionRequest.WorkingDirectory);
            Assert.Equal(30_000, revisionRequest.TimeoutMilliseconds);
            Assert.All(runner.BaselinePackRequests, command => Assert.Contains("-p:SourceRevisionId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", command.Arguments));
        }

        // protects=untrusted Git output cannot affect reproducible baseline packaging; fails_when=empty, short, or nonhex revision is accepted; no_claim=does not run a real Git command.
        [Theory]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("gggggggggggggggggggggggggggggggggggggggg")]
        public async Task RunAsync_RejectsNoncanonicalCandidateRevisionBeforeBaselinePack(string revision)
        {
            using var fixture = new PackedProofFixture();
            var runner = new CloudTasksProofRunner(fixture.PackageArtifacts, Version, fixture.LockFilePath, candidateRevisionOutput: revision);

            var error = await Assert.ThrowsAsync<PackageIndexException>(() =>
                new CloudTasksPackageConsumerProof(runner, fixture.CreatePinnedSourceAsync).RunAsync(
                    fixture.Request with { SourceCommit = null }, fixture.Artifacts));

            Assert.Contains("canonical 40-character candidate Git revision", error.Message, StringComparison.Ordinal);
            Assert.Single(runner.CandidateRevisionRequests);
            Assert.Empty(runner.BaselinePackRequests);
            Assert.False(Directory.Exists(Directory.GetParent(runner.ConsumerRestoreRequests[0].WorkingDirectory)!.FullName));
        }

        // protects=packed API execution proves invalid-token rejection before provider I/O; fails_when=the smoke consumer omits ASCT102 but proof proceeds; no_claim=does not make a live provider request.
        [Fact]
        public async Task RunAsync_RejectsPackedConsumerOutputWithoutInvalidTokenEvidence()
        {
            using var fixture = new PackedProofFixture();
            var runner = new CloudTasksProofRunner(fixture.PackageArtifacts, Version, fixture.LockFilePath, consumerExecutionOutput: "no evidence");

            var error = await Assert.ThrowsAsync<PackageIndexException>(() =>
                new CloudTasksPackageConsumerProof(runner, fixture.CreatePinnedSourceAsync).RunAsync(fixture.Request, fixture.Artifacts));

            Assert.Contains("did not verify invalid-token rejection", error.Message, StringComparison.Ordinal);
            Assert.Single(runner.ConsumerExecutionRequests);
            Assert.False(Directory.Exists(Directory.GetParent(runner.ConsumerExecutionRequests[0].WorkingDirectory)!.FullName));
        }

        // protects=validated package XML documentation and API signatures are scanned before baseline packing; fails_when=provider type text in package XML passes; no_claim=does not establish Google runtime API qualification.
        [Fact]
        public async Task RunAsync_RejectsProviderSdkTypeInValidatedCloudTasksPackageDocumentation()
        {
            using var fixture = new PackedProofFixture();
            fixture.AddCloudTasksDocumentationLeak();
            var runner = new CloudTasksProofRunner(fixture.PackageArtifacts, Version, fixture.LockFilePath);

            var error = await Assert.ThrowsAsync<PackageIndexException>(() =>
                new CloudTasksPackageConsumerProof(runner, fixture.CreatePinnedSourceAsync).RunAsync(fixture.Request, fixture.Artifacts));

            Assert.Contains("public API exposes provider SDK types", error.Message, StringComparison.Ordinal);
            Assert.Empty(runner.BaselinePackRequests);
            Assert.False(Directory.Exists(Directory.GetParent(runner.ConsumerRestoreRequests[0].WorkingDirectory)!.FullName));
        }

        // protects=consumer asset graph is tied to validated neutral package archives; fails_when=missing or mismatched local archive hashes pass; no_claim=the fixture graph is not a real restore.
        [Theory]
        [InlineData("missing")]
        [InlineData("hash")]
        public async Task RunAsync_RejectsNeutralCandidateGraphWithMissingOrWrongPackageIdentity(string invalidIdentity)
        {
            using var fixture = new PackedProofFixture();
            var runner = new CloudTasksProofRunner(fixture.PackageArtifacts, Version, fixture.LockFilePath,
                neutralGraphIdentityFailure: invalidIdentity);

            var error = await Assert.ThrowsAsync<PackageIndexException>(() =>
                new CloudTasksPackageConsumerProof(runner, fixture.CreatePinnedSourceAsync).RunAsync(fixture.Request, fixture.Artifacts));

            Assert.Contains(invalidIdentity == "missing" ? "must resolve validated package" : "content hash different", error.Message, StringComparison.Ordinal);
            Assert.Equal(6, runner.ConsumerRestoreRequests.Count);
            Assert.False(Directory.Exists(Directory.GetParent(runner.ConsumerRestoreRequests[0].WorkingDirectory)!.FullName));
        }

        // protects=neutral project source references cannot smuggle the optional provider into the neutral lane; fails_when=forbidden PackageReference passes the complete proof; no_claim=does not resolve transitive packages.
        [Fact]
        public async Task RunAsync_RejectsForbiddenReferenceInNeutralProjectTreeAndCleansWorkspace()
        {
            using var fixture = new PackedProofFixture();
            var repositoryRoot = fixture.CreateRepositoryRootWithForbiddenReference();
            var runner = new CloudTasksProofRunner(fixture.PackageArtifacts, Version, fixture.LockFilePath);

            var error = await Assert.ThrowsAsync<PackageIndexException>(() =>
                new CloudTasksPackageConsumerProof(runner, fixture.CreatePinnedSourceAsync).RunAsync(
                    fixture.Request with { RepositoryRoot = repositoryRoot }, fixture.Artifacts));

            Assert.Contains("Neutral Durable projects reference", error.Message, StringComparison.Ordinal);
            Assert.Contains("Google.Cloud.Tasks.V2", error.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Directory.GetParent(runner.ConsumerRestoreRequests[0].WorkingDirectory)!.FullName));
        }

        private static CloudTasksPackageGraph Graph(params CloudTasksPackageGraphNode[] nodes)
        {
            var map = new SortedDictionary<string, CloudTasksPackageGraphNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in nodes) map.Add($"{node.Id.ToLowerInvariant()}/{node.Version.ToLowerInvariant()}", node);
            return new CloudTasksPackageGraph(map, new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        private static CloudTasksPackageGraphNode Node(string id, string version, string sha512, params string[] dependencies)
            => new(id, version, sha512, dependencies);

        private static int ReadLockedPackageCount(string path)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.GetProperty("dependencies").GetProperty("net10.0").EnumerateObject().Count();
        }

        private static string WriteAssets(object assets)
        {
            var path = Path.Combine(Path.GetTempPath(), "cloudtasks-assets-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(assets));
            return path;
        }

        private const string Version = "1.0.0-preview.1";

        private sealed class PackedProofFixture : IDisposable
        {
            private static readonly string[] FirstPartyPackages =
            [
                "ForgeTrust.AppSurface.Core",
            "ForgeTrust.AppSurface.Flow",
            "ForgeTrust.AppSurface.Workers",
            "ForgeTrust.AppSurface.Durable",
        ];
            private readonly string _root = Path.Combine(Path.GetTempPath(), "cloudtasks-packed-proof-test-" + Guid.NewGuid().ToString("N"));

            internal PackedProofFixture()
            {
                ArtifactsDirectory = Path.Combine(_root, "artifacts");
                Directory.CreateDirectory(ArtifactsDirectory);
                var entries = new List<PackageArtifactValidationReportEntry>();
                foreach (var id in FirstPartyPackages.Append(CloudTasksPackageConsumerProof.PackageId))
                {
                    var artifact = Path.Combine(ArtifactsDirectory, $"{id}.{Version}.nupkg");
                    CreatePackage(artifact, id == CloudTasksPackageConsumerProof.PackageId ? null : id + ".dll");
                    PackageArtifacts[id] = artifact;
                    entries.Add(new PackageArtifactValidationReportEntry(id, id + ".csproj", PackagePublishDecision.Publish, [], artifact));
                }
                Artifacts = new PackageArtifactValidationReport(Version, entries);
            }

            internal string ArtifactsDirectory { get; }
            internal Dictionary<string, string> PackageArtifacts { get; } = new(StringComparer.OrdinalIgnoreCase);
            internal PackageArtifactValidationReport Artifacts { get; }
            internal CloudTasksPackageConsumerProofRequest Request => new(FindRepositoryRoot(), ArtifactsDirectory, Version, "https://api.nuget.org/v3/index.json", CloudTasksPackageConsumerProof.BaselineCommit);
            internal string LockFilePath => Path.Combine(Request.RepositoryRoot, "Durable", CloudTasksPackageConsumerProof.PackageId, "packages.lock.json");

            internal void AddCloudTasksDocumentationLeak()
            {
                var path = PackageArtifacts[CloudTasksPackageConsumerProof.PackageId];
                using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
                var documentation = archive.CreateEntry("lib/net10.0/CloudTasks.xml");
                using var writer = new StreamWriter(documentation.Open(), Encoding.UTF8);
                writer.Write("<doc><member name=\"T:Leak\">Google.Cloud.Tasks.V2.CloudTasksClient</member></doc>");
            }

            internal string CreateRepositoryRootWithForbiddenReference()
            {
                var root = Path.Combine(_root, "neutral-repository");
                var sourceRoot = FindRepositoryRoot();
                var consumerSource = Path.Combine(sourceRoot, "Durable", "packed-consumers", "CloudTasks");
                var consumerTarget = Path.Combine(root, "Durable", "packed-consumers", "CloudTasks");
                CopyFixtureTree(consumerSource, consumerTarget);
                var lockTarget = Path.Combine(root, "Durable", CloudTasksPackageConsumerProof.PackageId);
                Directory.CreateDirectory(lockTarget);
                File.Copy(LockFilePath, Path.Combine(lockTarget, "packages.lock.json"));
                var directory = Path.Combine(root, "Durable", "packed-consumers", "Adopter");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "Adopter.csproj"), "<Project><ItemGroup><PackageReference Include=\"Google.Cloud.Tasks.V2\" /></ItemGroup></Project>");
                return root;
            }

            private static void CopyFixtureTree(string source, string target)
            {
                Directory.CreateDirectory(target);
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(source, file);
                    var destination = Path.Combine(target, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination);
                }
            }

            internal Task CreatePinnedSourceAsync(string _, string target, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "NuGet.package-gate.config"), "<configuration />");
                foreach (var id in FirstPartyPackages)
                {
                    var projectPath = id switch
                    {
                        "ForgeTrust.AppSurface.Core" => Path.Combine(target, id, id + ".csproj"),
                        "ForgeTrust.AppSurface.Flow" => Path.Combine(target, "Flow", id, id + ".csproj"),
                        "ForgeTrust.AppSurface.Workers" => Path.Combine(target, "Workers", id, id + ".csproj"),
                        _ => Path.Combine(target, "Durable", id, id + ".csproj"),
                    };
                    Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
                    File.WriteAllText(projectPath, "<Project />");
                }
                return Task.CompletedTask;
            }

            public void Dispose()
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            }

            private static void CreatePackage(string path, string? assemblyName)
            {
                using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
                if (assemblyName is not null)
                {
                    var assembly = archive.CreateEntry("lib/net10.0/" + assemblyName);
                    using var writer = new StreamWriter(assembly.Open(), Encoding.UTF8);
                    writer.Write("stable-neutral-package-bytes");
                }
                var docs = archive.CreateEntry("lib/net10.0/README.xml");
                using var docsWriter = new StreamWriter(docs.Open(), Encoding.UTF8);
                docsWriter.Write("<doc><members><member name=\"P:Fixture.Value\" /></members></doc>");
            }

            internal static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
            {
                for (var directory = new FileInfo(sourceFilePath).Directory; directory is not null; directory = directory.Parent)
                    if (File.Exists(Path.Combine(directory.FullName, "ForgeTrust.AppSurface.slnx"))) return directory.FullName;
                throw new InvalidOperationException("Could not locate the AppSurface repository root for packed consumer proof fixtures.");
            }
        }

        private sealed class CloudTasksProofRunner(
            IReadOnlyDictionary<string, string> candidatePackages,
            string candidateVersion,
            string lockFilePath,
            string? invalidCloudTasksGraph = null,
            bool neutralGraphContainsGoogle = false,
            string? candidateRevisionOutput = null,
            string? consumerExecutionOutput = null,
            string? neutralGraphIdentityFailure = null) : ICommandRunner
        {
            internal List<CommandRunRequest> ConsumerRestoreRequests { get; } = [];
            internal List<string> EmptyBuildPropsContents { get; } = [];
            internal List<CommandRunRequest> CandidateRevisionRequests { get; } = [];
            internal List<CommandRunRequest> BaselinePackRequests { get; } = [];
            internal List<CommandRunRequest> ConsumerExecutionRequests { get; } = [];

            public Task<CommandRunResult> RunAsync(CommandRunRequest request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (request.OperationName == "Cloud Tasks candidate source revision")
                {
                    CandidateRevisionRequests.Add(request);
                    return Task.FromResult(new CommandRunResult(candidateRevisionOutput ?? CloudTasksPackageConsumerProof.BaselineCommit, string.Empty));
                }
                if (request.OperationName == "Cloud Tasks pinned baseline pack")
                {
                    BaselinePackRequests.Add(request);
                    var output = GetArgument(request.Arguments, "--output");
                    Directory.CreateDirectory(output);
                    File.Copy(candidatePackages[request.Subject], Path.Combine(output, $"{request.Subject}.{candidateVersion}.nupkg"));
                    return Task.FromResult(new CommandRunResult(string.Empty, string.Empty));
                }
                if (request.OperationName.Contains("consumer restore", StringComparison.OrdinalIgnoreCase))
                {
                    ConsumerRestoreRequests.Add(request);
                    EmptyBuildPropsContents.Add(File.ReadAllText(Path.Combine(request.WorkingDirectory, "Directory.Build.props")));
                    var assetsPath = Path.Combine(request.WorkingDirectory, "obj", "project.assets.json");
                    Directory.CreateDirectory(Path.GetDirectoryName(assetsPath)!);
                    var isCloudTasks = request.WorkingDirectory.EndsWith("cloudtasks-consumer", StringComparison.Ordinal);
                    var isNeutralCandidate = request.WorkingDirectory.EndsWith("neutral-candidate", StringComparison.Ordinal);
                    var assets = isCloudTasks ? CloudTasksAssets() : NeutralAssets(neutralGraphContainsGoogle, isNeutralCandidate);
                    File.WriteAllText(assetsPath, JsonSerializer.Serialize(assets));
                    return Task.FromResult(new CommandRunResult(string.Empty, string.Empty));
                }
                if (request.OperationName == "Cloud Tasks packed consumer execution")
                {
                    ConsumerExecutionRequests.Add(request);
                    return Task.FromResult(new CommandRunResult(consumerExecutionOutput ?? "ASCT102", string.Empty));
                }
                return Task.FromResult(new CommandRunResult(string.Empty, string.Empty));
            }

            private static string GetArgument(IReadOnlyList<string> arguments, string name)
            {
                var index = arguments.ToList().IndexOf(name);
                return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : throw new InvalidOperationException($"Missing {name} argument.");
            }

            private object CloudTasksAssets()
            {
                using var lockDocument = JsonDocument.Parse(File.ReadAllBytes(lockFilePath));
                var target = new Dictionary<string, object>();
                var libraries = new Dictionary<string, object>();
                var lockedPackages = lockDocument.RootElement.GetProperty("dependencies").GetProperty("net10.0").EnumerateObject().ToArray();
                foreach (var package in lockedPackages)
                {
                    if (invalidCloudTasksGraph == "missing" && string.Equals(package.Name, "Google.Api.Gax", StringComparison.OrdinalIgnoreCase)) continue;
                    var version = package.Value.GetProperty("resolved").GetString()!;
                    var key = $"{package.Name}/{version}";
                    var dependencies = package.Value.TryGetProperty("dependencies", out var dependencySet)
                        ? dependencySet.EnumerateObject().ToDictionary(dependency => dependency.Name, dependency => dependency.Value.GetString()!, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>();
                    target[key] = new { type = "package", dependencies };
                    var hash = package.Value.GetProperty("contentHash").GetString();
                    libraries[key] = new { type = "package", sha512 = invalidCloudTasksGraph == "hash" && package.Name == "Google.Api.Gax" ? "altered-content-hash" : hash };
                }
                var cloudTasksKey = $"ForgeTrust.AppSurface.Durable.CloudTasks/{candidateVersion}";
                var directPackageDependencies = lockDocument.RootElement.GetProperty("dependencies").GetProperty("net10.0")
                    .EnumerateObject()
                    .Where(static package => string.Equals(package.Value.GetProperty("type").GetString(), "Direct", StringComparison.Ordinal))
                    .ToDictionary(static package => package.Name, static package => package.Value.GetProperty("requested").GetString()!, StringComparer.OrdinalIgnoreCase);
                if (invalidCloudTasksGraph == "edge") directPackageDependencies.Remove("Grpc.Net.Client");
                target[cloudTasksKey] = new { type = "package", dependencies = directPackageDependencies };
                libraries[cloudTasksKey] = new { type = "package", sha512 = Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(candidatePackages["ForgeTrust.AppSurface.Durable.CloudTasks"]))) };
                var consumerDiKey = "Microsoft.Extensions.DependencyInjection/10.0.8";
                target[consumerDiKey] = new { type = "package", dependencies = new Dictionary<string, string>() };
                libraries[consumerDiKey] = new { type = "package", sha512 = "consumer-di-hash" };
                if (invalidCloudTasksGraph == "extra")
                {
                    const string unexpectedKey = "Unexpected.CloudTasks.Dependency/1.0.0";
                    target[unexpectedKey] = new { type = "package", dependencies = new Dictionary<string, string>() };
                    libraries[unexpectedKey] = new { type = "package", sha512 = "unexpected-hash" };
                }
                var directDependencies = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ForgeTrust.AppSurface.Durable.CloudTasks"] = new { version = $"[{candidateVersion}, )" },
                    ["Microsoft.Extensions.DependencyInjection"] = new { version = "[10.0.8, )" },
                };
                return new
                {
                    targets = new Dictionary<string, object> { ["net10.0"] = target },
                    libraries,
                    project = new { frameworks = new Dictionary<string, object> { ["net10.0"] = new { dependencies = directDependencies } } },
                };
            }

            private object NeutralAssets(bool includeGoogle, bool candidate)
            {
                var ids = new List<(string Id, string Version)>
            {
                ("ForgeTrust.AppSurface.Durable", candidateVersion),
                ("ForgeTrust.AppSurface.Core", candidateVersion),
                ("ForgeTrust.AppSurface.Flow", candidateVersion),
                ("ForgeTrust.AppSurface.Workers", candidateVersion),
            };
                if (includeGoogle) ids.Add(("Google.Cloud.Tasks.V2", "3.7.0"));
                if (neutralGraphIdentityFailure == "missing") ids.RemoveAll(static package => package.Id == "ForgeTrust.AppSurface.Flow");
                var assets = Assets(ids.ToArray(), ("ForgeTrust.AppSurface.Durable", $"[{candidateVersion}, )"));
                if (candidate && neutralGraphIdentityFailure == "hash")
                {
                    var json = JsonSerializer.SerializeToNode(assets)!;
                    var node = json["targets"]!["net10.0"]!["ForgeTrust.AppSurface.Flow/" + candidateVersion]!;
                    json["libraries"]!["ForgeTrust.AppSurface.Flow/" + candidateVersion]!["sha512"] = "wrong-hash";
                    return json;
                }
                return assets;
            }

            private object Assets((string Id, string Version)[] packages, params (string Id, string Version)[] directDependencies)
            {
                var target = new Dictionary<string, object>();
                var libraries = new Dictionary<string, object>();
                foreach (var package in packages)
                {
                    var key = $"{package.Id}/{package.Version}";
                    target[key] = new { type = "package", dependencies = new Dictionary<string, string>() };
                    var hash = candidatePackages.TryGetValue(package.Id, out var packagePath)
                        ? Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(packagePath)))
                        : "test-hash-" + package.Id;
                    libraries[key] = new { type = "package", sha512 = hash };
                }
                var dependencies = directDependencies.ToDictionary(
                    dependency => dependency.Id,
                    dependency => new { version = dependency.Version },
                    StringComparer.OrdinalIgnoreCase);
                return new
                {
                    targets = new Dictionary<string, object> { ["net10.0"] = target },
                    libraries,
                    project = new { frameworks = new Dictionary<string, object> { ["net10.0"] = new { dependencies } } },
                };
            }
        }
    }
}

namespace Google.Cloud.Tasks.V2
{
    public sealed class LeakedSdkType { }
}

namespace ForgeTrust.AppSurface.PackageIndex.Tests
{
    public sealed class LeakedPublicApiFixture
    {
        public Google.Cloud.Tasks.V2.LeakedSdkType ProviderType { get; } = new();
    }
}
