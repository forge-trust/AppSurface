using System.IO.Compression;
using System.Text;
using ForgeTrust.AppSurface.PackageIndex;

namespace ForgeTrust.AppSurface.PackageIndex.Tests
{
    public sealed class CloudTasksProofFailureTests
    {
        private const string Version = "1.0.0-preview.1";

        // protects=artifact identity must be authorized by PackageArtifactValidationReport; fails_when=unvalidated/mislocated package reaches restore or SDK proof; no_claim=does not exercise a real NuGet restore.
        [Fact]
        public async Task RunAsync_RejectsMissingOrMislocatedValidatedArtifactsBeforeCommands()
        {
            using var fixture = new ArtifactFixture();
            var missing = new PackageArtifactValidationReport(Version,
                fixture.Artifacts.Entries.Where(entry => entry.PackageId != CloudTasksPackageConsumerProof.PackageId).ToArray());
            var runner = new CommandRunnerStub();
            var missingError = await Assert.ThrowsAsync<PackageIndexException>(() =>
                new CloudTasksPackageConsumerProof(runner, fixture.ExtractBaselineAsync).RunAsync(fixture.Request, missing));
            Assert.Contains("requires validated artifact", missingError.Message, StringComparison.Ordinal);
            Assert.Empty(runner.Requests);

            var outsideRoot = Path.Combine(fixture.Root, "outside-artifacts", $"{CloudTasksPackageConsumerProof.PackageId}.{Version}.nupkg");
            Directory.CreateDirectory(Path.GetDirectoryName(outsideRoot)!);
            File.Copy(fixture.Artifacts.Entries.Single(entry => entry.PackageId == CloudTasksPackageConsumerProof.PackageId).ArtifactPath, outsideRoot);
            var misplacedEntries = fixture.Artifacts.Entries.Select(entry => entry.PackageId == CloudTasksPackageConsumerProof.PackageId
                ? entry with { ArtifactPath = outsideRoot }
                : entry).ToArray();
            var misplaced = await Assert.ThrowsAsync<PackageIndexException>(() =>
                new CloudTasksPackageConsumerProof(runner, fixture.ExtractBaselineAsync).RunAsync(
                    fixture.Request with { ArtifactsDirectory = Path.Combine(fixture.Root, "artifacts") },
                    new PackageArtifactValidationReport(Version, misplacedEntries)));
            Assert.Contains("does not match the local candidate package version", misplaced.Message, StringComparison.Ordinal);
            Assert.Empty(runner.Requests);

            var wrongVersionPath = Path.Combine(fixture.ArtifactsDirectory, $"{CloudTasksPackageConsumerProof.PackageId}.0.9.0.nupkg");
            File.Copy(fixture.Artifacts.Entries.Single(entry => entry.PackageId == CloudTasksPackageConsumerProof.PackageId).ArtifactPath, wrongVersionPath);
            var wrongVersionEntries = fixture.Artifacts.Entries.Select(entry => entry.PackageId == CloudTasksPackageConsumerProof.PackageId
                ? entry with { ArtifactPath = wrongVersionPath }
                : entry).ToArray();
            var wrongVersion = await Assert.ThrowsAsync<PackageIndexException>(() =>
                new CloudTasksPackageConsumerProof(runner, fixture.ExtractBaselineAsync).RunAsync(
                    fixture.Request,
                    new PackageArtifactValidationReport(Version, wrongVersionEntries)));
            Assert.Contains("does not match the local candidate package version", wrongVersion.Message, StringComparison.Ordinal);
            Assert.Empty(runner.Requests);
        }


        // protects=lock comparison cannot silently use an incomplete package restore; fails_when=zero or multiple target frameworks proceed as a valid graph; no_claim=does not run NuGet.
        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        public void ReadGraph_RejectsRestoreWithZeroOrMultipleTargetFrameworks(int frameworkCount)
        {
            var path = Path.Combine(Path.GetTempPath(), "cloudtasks-invalid-assets-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var targets = new Dictionary<string, object>();
                for (var index = 0; index < frameworkCount; index++) targets[$"net{10 - index}.0"] = new Dictionary<string, object>();
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { targets, libraries = new Dictionary<string, object>() }));

                var error = Assert.Throws<PackageIndexException>(() => CloudTasksPackageConsumerProof.ReadGraph(path));
                Assert.Contains("exactly one target framework", error.Message, StringComparison.Ordinal);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        // protects=neutral baseline identity comparison requires every validated package; fails_when=a missing candidate or baseline archive is treated as equality; no_claim=does not execute baseline packing.
        [Fact]
        public void CompareNeutralAssemblyIdentity_RejectsMissingCandidateAndBaselineArtifacts()
        {
            var missingCandidate = Assert.Throws<PackageIndexException>(() =>
                CloudTasksPackageConsumerProof.CompareNeutralAssemblyIdentity(
                    new Dictionary<string, CloudTasksPackageArtifact>(StringComparer.OrdinalIgnoreCase),
                    Path.Combine(Path.GetTempPath(), "missing-cloudtasks-baseline"), Version));
            Assert.Contains("Candidate neutral artifact", missingCandidate.Message, StringComparison.Ordinal);

            var candidates = new[]
            {
            "ForgeTrust.AppSurface.Core", "ForgeTrust.AppSurface.Flow", "ForgeTrust.AppSurface.Workers", "ForgeTrust.AppSurface.Durable",
        }.ToDictionary(id => id, id => new CloudTasksPackageArtifact(id, Path.Combine(Path.GetTempPath(), id + ".nupkg"), "candidate-hash"), StringComparer.OrdinalIgnoreCase);
            var missingBaseline = Assert.Throws<PackageIndexException>(() =>
                CloudTasksPackageConsumerProof.CompareNeutralAssemblyIdentity(candidates, Path.Combine(Path.GetTempPath(), "missing-cloudtasks-baseline"), Version));
            Assert.Contains("Pinned baseline artifact", missingBaseline.Message, StringComparison.Ordinal);
        }

        // protects=public API constructors/members/inheritance plus XML docs cannot leak Google types through wrappers; fails_when=any SDK signature position escapes scanning; no_claim=does not prove the production package is leak-free.
        [Fact]
        public void ScanPublicApiForGoogleTypes_DetectsNestedGenericArrayAndXmlDocumentationLeaks()
        {
            var package = Path.Combine(Path.GetTempPath(), "cloudtasks-api-nested-leak-" + Guid.NewGuid().ToString("N") + ".nupkg");
            try
            {
                using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
                {
                    var assembly = archive.CreateEntry("lib/net10.0/PackageIndex.Tests.dll");
                    using (var output = assembly.Open())
                    using (var input = File.OpenRead(typeof(CloudTasksProofFailureTests).Assembly.Location)) input.CopyTo(output);
                    var docs = archive.CreateEntry("lib/net10.0/PackageIndex.Tests.xml");
                    using var writer = new StreamWriter(docs.Open(), Encoding.UTF8);
                    writer.Write("<doc><member name=\"T:Fixture\">Google.Cloud.Tasks.V2.CloudTasksClient</member></doc>");
                }

                var leaks = CloudTasksPackageConsumerProof.ScanPublicApiForGoogleTypes(package, Path.Combine(Path.GetTempPath(), "cloudtasks-no-cache"));

                Assert.Contains(leaks, value => value.Contains(nameof(NestedSdkLeak), StringComparison.Ordinal));
                Assert.Contains("Google.", leaks);
                Assert.Contains(leaks, value => value.Contains(nameof(SdkMemberSignatureLeak) + "..ctor", StringComparison.Ordinal));
                Assert.Contains(leaks, value => value.Contains(nameof(SdkMemberSignatureLeak) + ".ProviderField", StringComparison.Ordinal));
                Assert.Contains(leaks, value => value.Contains(nameof(SdkMemberSignatureLeak) + ".ProviderEvent", StringComparison.Ordinal));
                Assert.Contains(leaks, value => value.Contains(nameof(SdkMemberSignatureLeak) + ".Item", StringComparison.Ordinal));
                Assert.Contains(leaks, value => value.Contains(nameof(SdkMemberSignatureLeak) + ".ByRef", StringComparison.Ordinal));
                Assert.Contains(leaks, value => value.Contains(nameof(SdkInheritanceLeak) + ".base", StringComparison.Ordinal));
                Assert.Contains(leaks, value => value.Contains(nameof(SdkInterfaceLeak) + ".interface", StringComparison.Ordinal));
            }
            finally { if (File.Exists(package)) File.Delete(package); }
        }

        // protects=owner-approved first-party byte differences remain observable; fails_when=matching contracts and entry sets are rejected merely for hash drift; no_claim=source proof is a separate required RunAsync gate.
        [Fact]
        public void AssertNeutralPackageContracts_AllowsRecordedBuildByteDifferences()
        {
            var identity = new CloudTasksPackageFileIdentity(
                "ForgeTrust.AppSurface.Durable", "baseline-nupkg", "candidate-nupkg",
                new Dictionary<string, string> { ["net10.0/Durable.dll"] = "same-assembly" },
                new Dictionary<string, string> { ["net10.0/Durable.dll"] = "changed-assembly" }, "same-contract", "same-contract");

            CloudTasksPackageConsumerProof.AssertNeutralPackageContracts([identity]);
        }

        // protects=neutral library entry sets remain exact; fails_when=removed/added DLL passes; no_claim=does not independently hash real NuGet artifacts.
        [Fact]
        public void AssertNeutralPackageContracts_RejectsChangedAssemblyEntrySets()
        {
            var identity = new CloudTasksPackageFileIdentity(
                "ForgeTrust.AppSurface.Durable", "same-nupkg", "same-nupkg",
                new Dictionary<string, string> { ["net10.0/Durable.dll"] = "before" },
                new Dictionary<string, string> { ["net10.0/Durable.dll"] = "after", ["net10.0/Extra.dll"] = "extra" }, "same-contract", "same-contract");

            var error = Assert.Throws<PackageIndexException>(() => CloudTasksPackageConsumerProof.AssertNeutralPackageContracts([identity]));

            Assert.Contains("library assembly entry set changed", error.Message, StringComparison.Ordinal);
        }

        // protects=packed contracts remain exact even when resolved packages coincide; fails_when=changed dependency ranges pass; no_claim=does not run NuGet.
        [Fact]
        public void AssertNeutralPackageContracts_RejectsDependencyContractDrift()
        {
            var hashes = new Dictionary<string, string> { ["net10.0/Durable.dll"] = "hash" };
            var identity = new CloudTasksPackageFileIdentity("ForgeTrust.AppSurface.Durable", "before", "after", hashes, hashes, "before-range", "after-range");
            var error = Assert.Throws<PackageIndexException>(() => CloudTasksPackageConsumerProof.AssertNeutralPackageContracts([identity]));
            Assert.Contains("packed dependency contract changed", error.Message, StringComparison.Ordinal);
        }

        // protects=pinned source extraction uses exact reviewed revision with bounded commands; fails_when=wrong git/archive options or unbounded runner timing reaches tar; no_claim=does not execute Git or tar binaries.
        [Fact]
        public async Task ExtractBaselineAsync_UsesPinnedRevisionAndBoundedArchiveAndTarCommands()
        {
            var root = Path.Combine(Path.GetTempPath(), "cloudtasks-extract-success-" + Guid.NewGuid().ToString("N"));
            var target = Path.Combine(root, "baseline");
            var runner = new BaselineCommandRunner((request, _) =>
            {
                if (request.OperationName == "Cloud Tasks pinned baseline archive")
                    File.WriteAllText(request.Arguments[^1], "fake tar archive");
                return Task.FromResult(new CommandRunResult(string.Empty, string.Empty));
            });
            try
            {
                await CloudTasksPackageConsumerProof.ExtractBaselineAsync(root, target, CancellationToken.None, runner);

                Assert.Equal(2, runner.Requests.Count);
                Assert.All(runner.Tokens, token => Assert.Equal(CancellationToken.None, token));
                var archive = runner.Requests[0];
                Assert.Equal("git", archive.FileName);
                Assert.Equal(new[] { "archive", CloudTasksPackageConsumerProof.BaselineCommit, "--format=tar", "--output" }, archive.Arguments.Take(4));
                Assert.Equal(5, archive.Arguments.Count);
                Assert.Equal("0", archive.Environment!["GIT_TERMINAL_PROMPT"]);
                Assert.Equal(CloudTasksPackageConsumerProof.BaselineCommit, archive.Subject);
                Assert.Equal(30_000, archive.TimeoutMilliseconds);
                Assert.Equal(root, archive.WorkingDirectory);
                Assert.StartsWith("appsurface-cloudtasks-baseline-", Path.GetFileName(archive.Arguments[^1]), StringComparison.Ordinal);
                Assert.EndsWith(".tar", archive.Arguments[^1], StringComparison.Ordinal);
                Assert.Equal("tar", runner.Requests[1].FileName);
                Assert.Equal(new[] { "-xf", archive.Arguments[^1], "-C", target }, runner.Requests[1].Arguments);
                Assert.Equal(60_000, runner.Requests[1].TimeoutMilliseconds);
                Assert.Equal(target, runner.Requests[1].WorkingDirectory);
                Assert.False(File.Exists(archive.Arguments[^1]));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        // protects=failed baseline archive creation cannot fall through to extraction or leak its partial file; fails_when=tar runs after git failure or temporary archive remains; no_claim=does not prove the baseline commit exists in a real checkout.
        [Fact]
        public async Task ExtractBaselineAsync_GitArchiveFailureStopsTarAndDeletesPartialArchive()
        {
            var root = Path.Combine(Path.GetTempPath(), "cloudtasks-extract-git-failure-" + Guid.NewGuid().ToString("N"));
            var target = Path.Combine(root, "baseline");
            var runner = new BaselineCommandRunner((request, _) =>
            {
                var archivePath = request.Arguments[^1];
                File.WriteAllText(archivePath, "partial archive");
                return Task.FromException<CommandRunResult>(new InvalidOperationException("injected git archive failure"));
            });
            try
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    CloudTasksPackageConsumerProof.ExtractBaselineAsync(root, target, CancellationToken.None, runner));

                Assert.Equal("injected git archive failure", error.Message);
                Assert.Single(runner.Requests);
                Assert.Equal("Cloud Tasks pinned baseline archive", runner.Requests[0].OperationName);
                Assert.False(File.Exists(runner.Requests[0].Arguments[^1]));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        // protects=caller cancellation reaches archive and extraction commands and cleanup runs on interruption; fails_when=tar ignores the caller token or leaves the temporary archive; no_claim=does not test process-tree termination in ProcessCommandRunner.
        [Fact]
        public async Task ExtractBaselineAsync_TarCancellationIsForwardedAndDeletesArchive()
        {
            var root = Path.Combine(Path.GetTempPath(), "cloudtasks-extract-tar-cancel-" + Guid.NewGuid().ToString("N"));
            var target = Path.Combine(root, "baseline");
            using var source = new CancellationTokenSource();
            var runner = new BaselineCommandRunner((request, token) =>
            {
                if (request.OperationName == "Cloud Tasks pinned baseline archive")
                {
                    File.WriteAllText(request.Arguments[^1], "completed archive");
                    return Task.FromResult(new CommandRunResult(string.Empty, string.Empty));
                }
                source.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new CommandRunResult(string.Empty, string.Empty));
            });
            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    CloudTasksPackageConsumerProof.ExtractBaselineAsync(root, target, source.Token, runner));

                Assert.Equal(2, runner.Requests.Count);
                Assert.All(runner.Tokens, token => Assert.Equal(source.Token, token));
                Assert.Equal("Cloud Tasks pinned baseline extraction", runner.Requests[1].OperationName);
                Assert.Equal(60_000, runner.Requests[1].TimeoutMilliseconds);
                Assert.False(File.Exists(runner.Requests[0].Arguments[^1]));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        // protects=pre-canceled requests perform no filesystem or process work; fails_when=temporary target or runner invocation occurs; no_claim=does not model a cancellation racing with process startup.
        [Fact]
        public async Task ExtractBaselineAsync_PreCanceledTokenStopsBeforeCreatingTargetOrCallingRunner()
        {
            var root = Path.Combine(Path.GetTempPath(), "cloudtasks-extract-pre-cancel-" + Guid.NewGuid().ToString("N"));
            var target = Path.Combine(root, "baseline");
            using var source = new CancellationTokenSource();
            source.Cancel();
            var runner = new BaselineCommandRunner((_, _) => Task.FromResult(new CommandRunResult(string.Empty, string.Empty)));

            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    CloudTasksPackageConsumerProof.ExtractBaselineAsync(root, target, source.Token, runner));

                Assert.Empty(runner.Requests);
                Assert.False(Directory.Exists(target));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        private sealed class BaselineCommandRunner(
            Func<CommandRunRequest, CancellationToken, Task<CommandRunResult>> execute) : ICommandRunner
        {
            public List<CommandRunRequest> Requests { get; } = [];
            public List<CancellationToken> Tokens { get; } = [];

            public Task<CommandRunResult> RunAsync(CommandRunRequest request, CancellationToken cancellationToken)
            {
                Requests.Add(request);
                Tokens.Add(cancellationToken);
                return execute(request, cancellationToken);
            }
        }

        private sealed class CommandRunnerStub : ICommandRunner
        {
            public List<CommandRunRequest> Requests { get; } = [];
            public Task<CommandRunResult> RunAsync(CommandRunRequest request, CancellationToken cancellationToken)
            {
                Requests.Add(request);
                return Task.FromResult(new CommandRunResult(string.Empty, string.Empty));
            }
        }

        private sealed class ArtifactFixture : IDisposable
        {
            private static readonly string[] PackageIds =
            [
                "ForgeTrust.AppSurface.Core", "ForgeTrust.AppSurface.Flow", "ForgeTrust.AppSurface.Workers",
            "ForgeTrust.AppSurface.Durable", CloudTasksPackageConsumerProof.PackageId,
        ];
            internal ArtifactFixture()
            {
                Root = Path.Combine(Path.GetTempPath(), "cloudtasks-proof-failures-" + Guid.NewGuid().ToString("N"));
                ArtifactsDirectory = Path.Combine(Root, "artifacts");
                Directory.CreateDirectory(ArtifactsDirectory);
                var entries = PackageIds.Select(id =>
                {
                    var path = Path.Combine(ArtifactsDirectory, $"{id}.{Version}.nupkg");
                    using (ZipFile.Open(path, ZipArchiveMode.Create)) { }
                    return new PackageArtifactValidationReportEntry(id, id + ".csproj", PackagePublishDecision.Publish, [], path);
                }).ToArray();
                Artifacts = new PackageArtifactValidationReport(Version, entries);
            }
            internal string Root { get; }
            internal string ArtifactsDirectory { get; }
            internal PackageArtifactValidationReport Artifacts { get; }
            internal CloudTasksPackageConsumerProofRequest Request => new(Root, ArtifactsDirectory, Version, "https://api.nuget.org/v3/index.json");
            internal Task ExtractBaselineAsync(string _, string target, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(target);
                return Task.CompletedTask;
            }
            public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        }
    }

    public sealed class NestedSdkLeak
    {
        public Dictionary<string, Google.Cloud.Tasks.V2.LeakedSdkType[]>? ProviderTypes { get; set; }
    }
}

namespace Google.Cloud.Tasks.V2
{
    public abstract class SdkTypeBase { }

    public interface ISdkTypeMarker { }
}

namespace ForgeTrust.AppSurface.PackageIndex.Tests
{
    public sealed class SdkInheritanceLeak : Google.Cloud.Tasks.V2.SdkTypeBase { }

    public sealed class SdkInterfaceLeak : Google.Cloud.Tasks.V2.ISdkTypeMarker { }

    public sealed class SdkMemberSignatureLeak
    {
        public SdkMemberSignatureLeak(Google.Cloud.Tasks.V2.LeakedSdkType value) => ProviderField = value;

        public Google.Cloud.Tasks.V2.LeakedSdkType ProviderField;

        public event Action<Google.Cloud.Tasks.V2.LeakedSdkType>? ProviderEvent
        {
            add { }
            remove { }
        }

        public Google.Cloud.Tasks.V2.LeakedSdkType this[Google.Cloud.Tasks.V2.LeakedSdkType key] => key;

        public Google.Cloud.Tasks.V2.LeakedSdkType ByRef(ref Google.Cloud.Tasks.V2.LeakedSdkType value) => value;
    }
}
