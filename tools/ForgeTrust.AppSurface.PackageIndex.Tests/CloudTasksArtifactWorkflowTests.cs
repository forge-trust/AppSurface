using System.Text;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed partial class PackageArtifactValidationTests
{
    private const string CloudTasksProject = "Durable/ForgeTrust.AppSurface.Durable.CloudTasks/ForgeTrust.AppSurface.Durable.CloudTasks.csproj";
    private const string CloudTasksPackage = "ForgeTrust.AppSurface.Durable.CloudTasks";
    private const string WebProject = "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj";
    private const string WebPackage = "ForgeTrust.AppSurface.Web";

    // protects=verification includes the blocked Cloud Tasks archive before manifest creation while publication keeps rejecting its blocker; fails_when=proof omits the validated archive or publish workflow accepts readiness-blocked Cloud Tasks; no_claim=does not execute the real packed consumer or publish an artifact.
    [Fact]
    public async Task PackageArtifactWorkflow_ProvesReadinessBlockedCloudTasksArchiveBeforeManifestAndKeepsPublicationBlocked()
    {
        var request = await PrepareCloudTasksArtifactWorkflowAsync();
        var runner = new CloudTasksArtifactCommandRunner();
        CloudTasksPackageConsumerProofRequest? proofRequest = null;
        PackageArtifactValidationReport? proofArtifacts = null;
        var coverageProof = new RecordingCoverageCliConsumerProofWorkflow(succeeded: true);
        var docsProof = new RecordingDocsPackageConsumerProofWorkflow(succeeded: true);
        var workflow = CreateCloudTasksArtifactWorkflow(runner, coverageProof, docsProof,
            (proof, artifacts, token) =>
            {
                proofRequest = proof;
                proofArtifacts = artifacts;
                token.ThrowIfCancellationRequested();
                Assert.False(File.Exists(request.ArtifactManifestPath));
                return Task.CompletedTask;
            });

        var report = await workflow.RunAsync(request);

        var observed = Assert.IsType<CloudTasksPackageConsumerProofRequest>(proofRequest);
        Assert.Equal(_repositoryRoot, observed.RepositoryRoot);
        Assert.Equal(request.ArtifactsOutputPath, observed.ArtifactsDirectory);
        Assert.Equal(request.PackageVersion, observed.PackageVersion);
        Assert.Equal(request.Source, observed.Source);
        Assert.Null(observed.SourceCommit);
        var artifactReport = Assert.IsType<PackageArtifactValidationReport>(proofArtifacts);
        Assert.Equal(2, artifactReport.Entries.Count);
        var archive = Assert.Single(artifactReport.Entries, entry => entry.PackageId == CloudTasksPackage);
        Assert.Equal(CloudTasksPackage, archive.PackageId);
        Assert.Equal(request.PackageVersion, artifactReport.PackageVersion);
        Assert.True(File.Exists(archive.ArtifactPath));
        Assert.Equal(archive.ArtifactPath, Assert.Single(report.Entries, entry => entry.PackageId == CloudTasksPackage).ArtifactPath);
        Assert.True(File.Exists(request.ArtifactManifestPath));
        Assert.Single(coverageProof.Requests);
        Assert.Single(docsProof.Requests);
        Assert.Equal(["dotnet restore", "dotnet build", "dotnet pack", "dotnet pack"], runner.Requests.Select(item => item.OperationName));

        // Publication uses the default resolver policy. The artifact verification opt-in must not make the blocked
        // package publishable, and the gate must run before reading an artifact manifest or issuing commands.
        var publishRunner = new RecordingExternalCommandRunner([]);
        var publishWorkflow = new PackagePublishWorkflow(
            CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                [CloudTasksProject] = CreateMetadata(CloudTasksProject, CloudTasksPackage),
                [WebProject] = CreateMetadata(WebProject, WebPackage)
            }),
            new PackageArtifactManifestReader(),
            publishRunner,
            new PackagePublishLedgerRenderer());
        var blocked = await Assert.ThrowsAsync<PackageIndexException>(() => publishWorkflow.RunAsync(
            new PackagePublishRequest(
                _repositoryRoot,
                ManifestPath,
                request.ArtifactsOutputPath,
                CombineSafeChildPath(request.ArtifactsOutputPath, "missing-manifest.json"),
                CombineSafeChildPath(request.ArtifactsOutputPath, "publish.md"),
                request.Source,
                "PACKAGE_INDEX_TEST_NUGET_API_KEY"),
            CancellationToken.None));
        Assert.Contains($"{CloudTasksPackage} (#892)", blocked.Message, StringComparison.Ordinal);
        Assert.Empty(publishRunner.Requests);
    }

    // protects=Cloud Tasks proof failure cannot leave a stale or newly written publish manifest; fails_when=the artifact manifest survives the failing proof seam; no_claim=does not simulate failures from the real SDK consumer.
    [Fact]
    public async Task PackageArtifactWorkflow_DeletesStaleManifestWhenCloudTasksProofFails()
    {
        var request = await PrepareCloudTasksArtifactWorkflowAsync();
        Directory.CreateDirectory(request.ArtifactsOutputPath);
        await File.WriteAllTextAsync(request.ArtifactManifestPath, "stale manifest", Encoding.UTF8);
        var runner = new CloudTasksArtifactCommandRunner();
        var coverageProof = new RecordingCoverageCliConsumerProofWorkflow(succeeded: true);
        var docsProof = new RecordingDocsPackageConsumerProofWorkflow(succeeded: true);
        var workflow = CreateCloudTasksArtifactWorkflow(runner, coverageProof, docsProof,
            (_, artifacts, _) =>
            {
                Assert.Equal(request.PackageVersion, artifacts.PackageVersion);
                Assert.Contains(artifacts.Entries, entry => entry.PackageId == CloudTasksPackage);
                Assert.False(File.Exists(request.ArtifactManifestPath));
                File.WriteAllText(request.ArtifactManifestPath, "manifest written by a failing proof");
                throw new InvalidOperationException("simulated Cloud Tasks packed-consumer failure");
            });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync(request));

        Assert.Contains("simulated Cloud Tasks packed-consumer failure", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(request.ArtifactManifestPath));
        Assert.Empty(coverageProof.Requests);
        Assert.Empty(docsProof.Requests);
        Assert.Equal(["dotnet restore", "dotnet build", "dotnet pack", "dotnet pack"], runner.Requests.Select(item => item.OperationName));
    }

    private async Task<PackageArtifactRequest> PrepareCloudTasksArtifactWorkflowAsync()
    {
        await WriteFileAsync("packages/package-index.yml", $$"""
            packages:
              - project: {{WebProject}}
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 0
                use_when: Host an AppSurface web application.
                includes: Shared web hosting components.
                does_not_include: Durable Cloud Tasks activation.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: {{CloudTasksProject}}
                product_family: appsurface
                classification: public
                publish_decision: publish
                release_status: public_preview
                commercial_status: commercial_ready
                release_notes_path: releases/unreleased.md
                readiness_blocker: "#892"
                order: 10
                use_when: Dispatch durable work to Cloud Tasks.
                includes: Cloud Tasks activation and metadata token support.
                does_not_include: Cloud Run hosting or queue administration.
                start_here_path: Durable/ForgeTrust.AppSurface.Durable.CloudTasks/README.md
            """);
        await WriteFileAsync(WebProject, "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# AppSurface Web");
        await WriteFileAsync(CloudTasksProject, "<Project />");
        await WriteFileAsync("Durable/ForgeTrust.AppSurface.Durable.CloudTasks/README.md", "# Cloud Tasks");
        await WriteFileAsync("releases/unreleased.md", "# Unreleased");
        await WriteFileAsync("packages/third-party-payloads.yml", """
            schema_version: 1
            audits:
              - id: cloudtasks-workflow-fixture
                package_id: ForgeTrust.AppSurface.Durable.CloudTasks
                applies_to:
                  - README.md
                evidence_kind: fixture_audit
                source_paths:
                  - packages/package-index.yml
                reason: Keeps this workflow test focused on packed-consumer sequencing.
                reviewed_on: 2026-10-27
                source: test fixture
                revalidate_when: fixture changes.
            """);
        var artifacts = CombineSafeChildPath(_repositoryRoot, "artifacts");
        return new PackageArtifactRequest(
            _repositoryRoot,
            ManifestPath,
            artifacts,
            CombineSafeChildPath(artifacts, "package-validation-report.md"),
            PackageVersion,
            CombineSafeChildPath(artifacts, "package-artifact-manifest.json"),
            CombineSafeChildPath(artifacts, "coverage-proof"),
            CombineSafeChildPath(artifacts, "coverage-proof.md"),
            CombineSafeChildPath(artifacts, "docs-proof"),
            CombineSafeChildPath(artifacts, "docs-proof.md"),
            "https://api.nuget.org/v3/index.json");
    }

    private PackageArtifactWorkflow CreateCloudTasksArtifactWorkflow(
        ICommandRunner runner,
        ICoverageCliConsumerProofWorkflow coverageProof,
        IDocsPackageConsumerProofWorkflow docsProof,
        Func<CloudTasksPackageConsumerProofRequest, PackageArtifactValidationReport, CancellationToken, Task> proof)
    {
        var metadata = new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            [CloudTasksProject] = CreateMetadata(CloudTasksProject, CloudTasksPackage),
            [WebProject] = CreateMetadata(WebProject, WebPackage)
        };
        return new PackageArtifactWorkflow(
            CreateResolver(metadata),
            runner,
            new PackageArtifactValidator(),
            coverageProof,
            docsProof,
            cloudTasksProof: proof);
    }

    private sealed class CloudTasksArtifactCommandRunner : ICommandRunner
    {
        internal List<CommandRunRequest> Requests { get; } = [];

        public Task<CommandRunResult> RunAsync(CommandRunRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.OperationName == "dotnet pack")
            {
                var output = request.Arguments[request.Arguments.ToList().IndexOf("--output") + 1];
                var version = request.Arguments.Single(argument => argument.StartsWith("/p:PackageVersion=", StringComparison.Ordinal))
                    .Split('=', 2)[1];
                var project = request.Arguments[1];
                var packageId = project.EndsWith(CloudTasksProject, StringComparison.Ordinal)
                    ? CloudTasksPackage
                    : project.EndsWith(WebProject, StringComparison.Ordinal)
                        ? WebPackage
                        : throw new InvalidOperationException($"Unexpected pack target '{project}'.");
                WritePackage(output, packageId, version, EmptyDependencies);
            }

            return Task.FromResult(new CommandRunResult(string.Empty, string.Empty));
        }
    }
}
