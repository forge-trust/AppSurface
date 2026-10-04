using System.Text;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed partial class PackageArtifactValidationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DurableTemplateWorkflow_StagesOnlyCandidatePinsAndWithholdsManifestOnFailedProof(bool proofSucceeded)
    {
        var request = await PrepareTemplateWorkflowAsync();
        var runner = new TemplatePackRunner();
        DurableTemplateConsumerProofRequest? observed = null;
        IReadOnlyList<PackageArtifactValidationReportEntry>? observedArchives = null;
        var workflow = CreateTemplateWorkflow(runner, (proof, archives, token) =>
        {
            observed = proof;
            observedArchives = archives;
            Assert.Equal(request.PackageVersion, proof.PackageVersion);
            Assert.Equal(request.RepositoryRoot, proof.RepositoryRoot);
            Assert.Equal(request.ArtifactsOutputPath, proof.ArtifactsDirectory);
            Assert.Equal(request.Source, proof.ThirdPartySource);
            Assert.Equal(OperatingSystem.IsLinux(), proof.RunFirstWork);
            Assert.False(File.Exists(request.ArtifactManifestPath));
            Assert.False(Directory.Exists(runner.StagedRoot));
            token.ThrowIfCancellationRequested();
            return Task.FromResult(TemplateWorkflowReceipt(proofSucceeded));
        });
        var authoredPins = File.ReadAllBytes(TestPathUtils.PathUnder(_repositoryRoot,
            DurableTemplateStaging.ContentPath, "Directory.Packages.props"));
        if (proofSucceeded)
        {
            var report = await workflow.RunAsync(request);
            Assert.Single(report.Entries);
            Assert.True(File.Exists(request.ArtifactManifestPath));
        }
        else
        {
            var error = await Assert.ThrowsAsync<PackageIndexException>(() => workflow.RunAsync(request));
            Assert.Contains("installed-artifact proof failed", error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(request.ArtifactManifestPath));
        }

        Assert.NotNull(observed);
        Assert.Single(observedArchives!);
        Assert.Equal(["dotnet restore", "dotnet build", "dotnet pack"], runner.Requests.Select(item => item.OperationName));
        Assert.False(Directory.Exists(runner.StagedRoot));
        Assert.Equal(authoredPins, File.ReadAllBytes(TestPathUtils.PathUnder(_repositoryRoot,
            DurableTemplateStaging.ContentPath, "Directory.Packages.props")));
    }

    [Fact]
    public async Task DurableTemplateWorkflow_RemovesPrivateStageAfterPackFailureAndNeverCallsProof()
    {
        var request = await PrepareTemplateWorkflowAsync();
        var runner = new TemplatePackRunner(failPack: true);
        var calls = 0;
        var workflow = CreateTemplateWorkflow(runner, (_, _, _) =>
        {
            calls++;
            return Task.FromResult(TemplateWorkflowReceipt(true));
        });

        await Assert.ThrowsAsync<PackageIndexException>(() => workflow.RunAsync(request));

        Assert.Equal(0, calls);
        Assert.NotNull(runner.StagedRoot);
        Assert.False(Directory.Exists(runner.StagedRoot));
        Assert.False(File.Exists(request.ArtifactManifestPath));
    }

    private async Task<PackageArtifactRequest> PrepareTemplateWorkflowAsync()
    {
        const string project = "Durable/ForgeTrust.AppSurface.Durable.Templates/ForgeTrust.AppSurface.Durable.Templates.csproj";
        await WriteFileAsync(project, "<Project />");
        await WriteFileAsync("Durable/ForgeTrust.AppSurface.Durable.Templates/README.md", "# Templates");
        const string webProject = "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj";
        await WriteFileAsync(webProject, "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        await WriteFileAsync("packages/package-index.yml", $$"""
            packages:
              - project: {{webProject}}
                product_family: appsurface
                classification: public
                publish_decision: do_not_publish
                publish_reason: Unrelated to this template-only fixture.
                order: 0
                use_when: Host web applications.
                includes: Base web hosting.
                does_not_include: Template installation.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: {{project}}
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 1
                use_when: Create a worker.
                includes: Authored template.
                does_not_include: Runtime execution.
                start_here_path: Durable/ForgeTrust.AppSurface.Durable.Templates/README.md
              - project: Durable/ForgeTrust.AppSurface.Durable.Templates/content/durable-worker/src/AppSurfaceDurableWorker/AppSurfaceDurableWorker.csproj
                product_family: internal_support
                classification: excluded
                publish_decision: do_not_publish
                publish_reason: Application-owned source.
                note: Authored standalone host.
                order: 2
            """);
        await WriteFileAsync("packages/third-party-payloads.yml", """
            schema_version: 1
            audits:
              - id: template-workflow-fixture-proof
                package_id: ForgeTrust.AppSurface.Durable.Templates
                applies_to:
                  - README.md
                evidence_kind: fixture_audit
                source_paths:
                  - packages/package-index.yml
                reason: Keeps the template test focused on command orchestration.
                reviewed_on: 2026-10-04
                source: test fixture
                revalidate_when: fixture changes.
            """);
        var source = TestPathUtils.PathUnder(TestPathUtils.FindRepoRoot(AppContext.BaseDirectory), DurableTemplateStaging.ContentPath);
        var canonicalRoot = _repositoryRoot;
        if (OperatingSystem.IsMacOS() && canonicalRoot.StartsWith("/var/", StringComparison.Ordinal)) canonicalRoot = "/private" + canonicalRoot;
        if (OperatingSystem.IsMacOS() && canonicalRoot.StartsWith("/tmp/", StringComparison.Ordinal)) canonicalRoot = "/private" + canonicalRoot;
        DurableTemplateStaging.Stage(source, TestPathUtils.PathUnder(canonicalRoot, DurableTemplateStaging.ContentPath), "0.2.0-preview.13");
        var output = TestPathUtils.PathUnder(canonicalRoot, "artifacts");
        return new(canonicalRoot, TestPathUtils.PathUnder(canonicalRoot, "packages/package-index.yml"), output, TestPathUtils.PathUnder(output, "report.md"), PackageVersion,
            TestPathUtils.PathUnder(output, "manifest.json"), TestPathUtils.PathUnder(output, "coverage-proof"),
            TestPathUtils.PathUnder(output, "coverage.md"), TestPathUtils.PathUnder(output, "docs-proof"),
            TestPathUtils.PathUnder(output, "docs.md"), "https://api.nuget.org/v3/index.json");
    }

    private PackageArtifactWorkflow CreateTemplateWorkflow(ICommandRunner runner,
        Func<DurableTemplateConsumerProofRequest, IReadOnlyList<PackageArtifactValidationReportEntry>, CancellationToken, Task<DurableTemplateProofReceipt>> proof)
    {
        var projects = new PackageProjectScanner().DiscoverProjects(_repositoryRoot);
        var metadata = projects.ToDictionary(path => path, path =>
            path.EndsWith("ForgeTrust.AppSurface.Durable.Templates.csproj", StringComparison.Ordinal)
                ? CreateMetadata(path, DurableTemplateStaging.PackageId)
                : path.EndsWith("ForgeTrust.AppSurface.Web.csproj", StringComparison.Ordinal)
                    ? CreateMetadata(path, "ForgeTrust.AppSurface.Web")
                    : new PackageProjectMetadata(path, "AppSurfaceDurableWorker", "net10.0", false, false, "Exe", []));
        return new(CreateResolver(metadata), runner, new PackageArtifactValidator(),
            new RecordingCoverageCliConsumerProofWorkflow(true), new RecordingDocsPackageConsumerProofWorkflow(true), templateProof: proof);
    }

    private static DurableTemplateProofReceipt TemplateWorkflowReceipt(bool succeeded) =>
        new(1, "", PackageVersion, "linux-x64", "10.0.100", DurableTemplateConsumerProof.PostgreSqlImage,
            "candidate-local-feed-correctness", [], [], true, true, true, false, true, true, true, true, true, succeeded,
            succeeded ? "" : "TemplateProofFailed");

    private sealed class TemplatePackRunner(bool failPack = false) : ICommandRunner
    {
        internal List<CommandRunRequest> Requests { get; } = [];
        internal string? StagedRoot { get; private set; }

        public Task<CommandRunResult> RunAsync(CommandRunRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.OperationName != "dotnet pack") return Task.FromResult(new CommandRunResult("", ""));
            var staged = request.Arguments.Single(arg => arg.StartsWith("/p:TemplateContentRoot=", StringComparison.Ordinal)).Split('=', 2)[1];
            StagedRoot = Path.GetDirectoryName(staged);
            Assert.True(Directory.Exists(staged));
            DurableTemplateArtifactContract.ValidateProjectVersions(staged, PackageVersion);
            if (failPack) throw new PackageIndexException("Controlled template pack failure.");
            var output = request.Arguments[request.Arguments.ToList().IndexOf("--output") + 1];
            var content = DurableTemplateStaging.EnumerateContent(staged).ToDictionary(
                path => "content/durable-worker/" + Path.GetRelativePath(staged, path).Replace('\\', '/'), File.ReadAllBytes);
            content.Add("[Content_Types].xml", Encoding.UTF8.GetBytes("<Types />"));
            content.Add("_rels/.rels", Encoding.UTF8.GetBytes("<Relationships />"));
            content.Add("LICENSE", Encoding.UTF8.GetBytes("MIT"));
            content.Add("package/services/metadata/core-properties/aaaa.psmdcp", Encoding.UTF8.GetBytes("properties"));
            WritePackage(output, DurableTemplateStaging.PackageId, PackageVersion, EmptyDependencies,
                packageTypes: ["Template"], rawEntries: content);
            return Task.FromResult(new CommandRunResult("", ""));
        }
    }
}
