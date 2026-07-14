using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using FakeItEasy;
using ForgeTrust.AppSurface.Aspire;
using ForgeTrust.AppSurface.Deployment;
using ForgeTrust.AppSurface.Testing;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable ASPIREPIPELINES001 // Tests pin the adapter to the repository-supported Aspire 13.4.4 API.
#pragma warning disable ASPIREPIPELINES004 // Tests pin the adapter output service to Aspire 13.4.4.

[Collection(AspireDeploymentBuilderCollection.Name)]
public sealed class AppSurfaceDeploymentBuilderExtensionsTests
{
    private const string Image = "us-docker.pkg.dev/example/releases/migrations@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Revision = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    static AppSurfaceDeploymentBuilderExtensionsTests()
    {
        Environment.SetEnvironmentVariable("ASPIRE_DCP_PATH", "dummy");
        Environment.SetEnvironmentVariable("ASPIRE_DASHBOARD_PATH", "dummy");
    }

    [Fact]
    public void AddTarget_RegistersPublishAndNamedVerifySteps()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var bindings = AddParameter(builder, "bindings", "bindings.json", secret: false);
        var revision = AddParameter(builder, "revision", Revision, secret: false);

        var target = builder.AddAppSurfaceDeploymentTarget(
            "gcp-staging",
            new FakeTarget(AllCapabilities),
            bindings,
            revision);

        Assert.Equal("gcp-staging", target.Resource.Name);
        Assert.IsType<AppSurfaceDeploymentTargetResource>(target.Resource);
        Assert.Equal(2, target.Resource.Annotations.OfType<PipelineStepAnnotation>().Count());
        Assert.Equal("appsurface-gcp-publish", AspireDeploymentPipelineAdapter.PublishStepName);
        Assert.Equal("appsurface-gcp-verify", AspireDeploymentPipelineAdapter.VerifyStepName);
        Assert.Equal(WellKnownPipelineSteps.Publish, AspireDeploymentPipelineAdapter.PublishRequiredByStepName);
        Assert.Equal(AspireDeploymentPipelineAdapter.PublishStepName, AspireDeploymentPipelineAdapter.VerifyDependsOnStepName);
        Assert.Contains("without cloud calls", AspireDeploymentPipelineAdapter.PublishStepDescription, StringComparison.Ordinal);
        Assert.Contains("read-only", AspireDeploymentPipelineAdapter.VerifyStepDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void AddTarget_RejectsSecretMetadataParameter()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var bindings = AddParameter(builder, "bindings", "not-read", secret: true);
        var revision = AddParameter(builder, "revision", Revision, secret: false);

        var exception = Assert.Throws<ArgumentException>(() => builder.AddAppSurfaceDeploymentTarget(
            "gcp-staging",
            new FakeTarget(AllCapabilities),
            bindings,
            revision));

        Assert.Contains("ASDEPLOY202", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddTarget_RejectsSecretSourceRevisionParameter()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var bindings = AddParameter(builder, "bindings", "bindings.json", secret: false);
        var revision = AddParameter(builder, "revision", Revision, secret: true);

        var exception = Assert.Throws<ArgumentException>(() => builder.AddAppSurfaceDeploymentTarget(
            "gcp-staging",
            new FakeTarget(AllCapabilities),
            bindings,
            revision));

        Assert.Contains("ASDEPLOY202", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddTarget_RejectsParametersFromAnotherBuilder()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var otherBuilder = DistributedApplication.CreateBuilder([]);
        var bindings = AddParameter(otherBuilder, "bindings", "bindings.json", secret: false);
        var revision = AddParameter(builder, "revision", Revision, secret: false);

        var exception = Assert.Throws<ArgumentException>(() => builder.AddAppSurfaceDeploymentTarget(
            "gcp-staging",
            new FakeTarget(AllCapabilities),
            bindings,
            revision));

        Assert.Contains("ASDEPLOY201", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddTarget_RejectsSourceRevisionFromAnotherBuilder()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var otherBuilder = DistributedApplication.CreateBuilder([]);
        var bindings = AddParameter(builder, "bindings", "bindings.json", secret: false);
        var revision = AddParameter(otherBuilder, "revision", Revision, secret: false);

        var exception = Assert.Throws<ArgumentException>(() => builder.AddAppSurfaceDeploymentTarget(
            "gcp-staging",
            new FakeTarget(AllCapabilities),
            bindings,
            revision));

        Assert.Contains("ASDEPLOY201", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationAnnotation_RejectsDuplicateAnnotation()
    {
        var graph = CreateAnnotatedGraph();

        var exception = Assert.Throws<InvalidOperationException>(() => graph.Project.WithAppSurfaceMigrationJob(_ => { }));

        Assert.Contains("ASDEPLOY203", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildIntent_UsesAssignedResourceAndNeverResolvesSecretValue()
    {
        var secretWasRead = false;
        var graph = CreateAnnotatedGraph(() =>
        {
            secretWasRead = true;
            throw new InvalidOperationException("Secret value must not be read.");
        });
        graph.Project.WithComputeEnvironment(graph.Target);
        var model = new DistributedApplicationModel(graph.Builder.Resources);

        var intent = await AspireDeploymentPipelineAdapter.BuildIntentAsync(
            Assert.IsType<AppSurfaceDeploymentTargetResource>(graph.Target.Resource),
            model,
            CancellationToken.None);

        var job = Assert.Single(intent.MigrationJobs);
        Assert.Equal("migration", job.Id.Value);
        Assert.Equal(Image, job.Image.Value);
        Assert.Equal(["/app/Migrations.dll"], job.Arguments);
        Assert.Equal("connection", job.ConnectionSecret.Parameter.Value);
        Assert.Equal(graph.Builder.Environment.EnvironmentName, intent.Environment);
        Assert.Equal(Revision, intent.SourceRevision.Value);
        Assert.False(secretWasRead);
    }

    [Fact]
    public async Task BuildIntent_RejectsUnassignedAnnotation()
    {
        var sourceWasRead = false;
        var graph = CreateAnnotatedGraph(sourceValue: () =>
        {
            sourceWasRead = true;
            throw new InvalidOperationException("Source must not be read before model validation.");
        });
        var model = new DistributedApplicationModel(graph.Builder.Resources);

        var exception = await Assert.ThrowsAsync<DeploymentValidationException>(() =>
            AspireDeploymentPipelineAdapter.BuildIntentAsync(
                Assert.IsType<AppSurfaceDeploymentTargetResource>(graph.Target.Resource),
                model,
                CancellationToken.None));

        Assert.Equal("ASDEPLOY217", exception.Diagnostic.Code);
        Assert.False(sourceWasRead);
    }

    [Fact]
    public async Task BuildIntent_RejectsTargetWithNoMigrationAnnotations()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var bindings = AddParameter(builder, "bindings", "bindings.json", secret: false);
        var revision = AddParameter(builder, "revision", Revision, secret: false);
        var target = builder.AddAppSurfaceDeploymentTarget(
            "gcp-staging",
            new FakeTarget(AllCapabilities),
            bindings,
            revision);

        var exception = await Assert.ThrowsAsync<DeploymentValidationException>(() =>
            AspireDeploymentPipelineAdapter.BuildIntentAsync(
                Assert.IsType<AppSurfaceDeploymentTargetResource>(target.Resource),
                new DistributedApplicationModel(builder.Resources),
                CancellationToken.None));

        Assert.Equal("ASDEPLOY118", exception.Diagnostic.Code);
    }

    [Fact]
    public async Task BuildIntent_RejectsMissingTargetCapability()
    {
        var graph = CreateAnnotatedGraph(capabilities: new HashSet<DeploymentCapability>
        {
            DeploymentCapability.PrivateNetwork,
            DeploymentCapability.RunToCompletionJob,
        });
        graph.Project.WithComputeEnvironment(graph.Target);
        var model = new DistributedApplicationModel(graph.Builder.Resources);

        var exception = await Assert.ThrowsAsync<DeploymentValidationException>(() =>
            AspireDeploymentPipelineAdapter.BuildIntentAsync(
                Assert.IsType<AppSurfaceDeploymentTargetResource>(graph.Target.Resource),
                model,
                CancellationToken.None));

        Assert.Equal("ASDEPLOY209", exception.Diagnostic.Code);
        Assert.Contains("RelationalConnection", exception.Diagnostic.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildIntent_RejectsConnectionAnnotationThatIsNotSecret()
    {
        var graph = CreateAnnotatedGraph();
        graph.Project.WithComputeEnvironment(graph.Target);
        var annotation = Assert.Single(graph.Project.Resource.Annotations.OfType<AppSurfaceMigrationJobAnnotation>());
        var notSecret = new ParameterResource("connection", _ => "plaintext", secret: false);
        graph.Project.WithAnnotation(annotation with { ConnectionSecret = notSecret }, ResourceAnnotationMutationBehavior.Replace);

        var exception = await Assert.ThrowsAsync<DeploymentValidationException>(() =>
            AspireDeploymentPipelineAdapter.BuildIntentAsync(
                Assert.IsType<AppSurfaceDeploymentTargetResource>(graph.Target.Resource),
                new DistributedApplicationModel(graph.Builder.Resources),
                CancellationToken.None));

        Assert.Equal("ASDEPLOY205", exception.Diagnostic.Code);
    }

    [Fact]
    public async Task BuildIntent_IgnoresAnnotationAssignedToDifferentTarget()
    {
        var graph = CreateAnnotatedGraph();
        graph.Project.WithComputeEnvironment(graph.Target);
        var otherProject = graph.Builder.AddResource(new ProjectResource("other-migration"));
        var annotation = Assert.Single(graph.Project.Resource.Annotations.OfType<AppSurfaceMigrationJobAnnotation>());
        otherProject.WithAnnotation(annotation with { ResourceName = "other-migration" });
        var otherTarget = graph.Builder.AddResource<IComputeEnvironmentResource>(new OtherComputeEnvironment("other-target"));
        otherProject.WithComputeEnvironment(otherTarget);

        var intent = await AspireDeploymentPipelineAdapter.BuildIntentAsync(
            Assert.IsType<AppSurfaceDeploymentTargetResource>(graph.Target.Resource),
            new DistributedApplicationModel(graph.Builder.Resources),
            CancellationToken.None);

        Assert.Equal("migration", Assert.Single(intent.MigrationJobs).Id.Value);
    }

    [Fact]
    public async Task RenderAndWrite_AddsIntentAndWritesCompleteOwnedBundle()
    {
        var output = NewOutputDirectory();
        try
        {
            var providerArtifact = DeploymentArtifact.Create("provider.json", "{}\n"u8.ToArray());
            var graph = CreateAnnotatedGraph(target: new FakeTarget(
                AllCapabilities,
                render: _ => new DeploymentRenderResult("fake", [providerArtifact])));
            graph.Project.WithComputeEnvironment(graph.Target);

            var result = await AspireDeploymentPipelineAdapter.RenderAndWriteAsync(
                Assert.IsType<AppSurfaceDeploymentTargetResource>(graph.Target.Resource),
                new DistributedApplicationModel(graph.Builder.Resources),
                CreateOutputServices(output),
                CancellationToken.None);

            Assert.Equal(["deployment-intent.v1.json", "provider.json"], result.Artifacts.Select(item => item.FileName));
            Assert.True(File.Exists(TestPathUtils.PathUnder(output, "deployment-intent.v1.json")));
            Assert.True(File.Exists(TestPathUtils.PathUnder(output, "provider.json")));
            Assert.True(File.Exists(TestPathUtils.PathUnder(output, DeploymentArtifactBundleWriter.OwnershipMarkerFileName)));
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task RenderAndWrite_AcceptsProviderIntentThatMatchesEvaluatedGraph()
    {
        var output = NewOutputDirectory();
        try
        {
            var graph = CreateAnnotatedGraph(target: new FakeTarget(
                AllCapabilities,
                render: request => new DeploymentRenderResult("fake", [
                    DeploymentArtifact.Create("deployment-intent.v1.json", DeploymentCanonicalJson.Serialize(request.Intent)),
                ])));
            graph.Project.WithComputeEnvironment(graph.Target);

            var result = await AspireDeploymentPipelineAdapter.RenderAndWriteAsync(
                Assert.IsType<AppSurfaceDeploymentTargetResource>(graph.Target.Resource),
                new DistributedApplicationModel(graph.Builder.Resources),
                CreateOutputServices(output),
                CancellationToken.None);

            Assert.Single(result.Artifacts);
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task RenderAndWrite_RejectsProviderIntentThatContradictsEvaluatedGraph()
    {
        var output = NewOutputDirectory();
        try
        {
            var intentArtifact = DeploymentArtifact.Create("deployment-intent.v1.json", "{}\n"u8.ToArray());
            var graph = CreateAnnotatedGraph(target: new FakeTarget(
                AllCapabilities,
                render: _ => new DeploymentRenderResult("fake", [intentArtifact])));
            graph.Project.WithComputeEnvironment(graph.Target);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AspireDeploymentPipelineAdapter.RenderAndWriteAsync(
                    Assert.IsType<AppSurfaceDeploymentTargetResource>(graph.Target.Resource),
                    new DistributedApplicationModel(graph.Builder.Resources),
                    CreateOutputServices(output),
                    CancellationToken.None));

            Assert.Contains("ASDEPLOY221", exception.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task RenderAndWrite_CanonicalizesProviderArtifactOrder()
    {
        var output = NewOutputDirectory();
        try
        {
            var graph = CreateAnnotatedGraph(target: new FakeTarget(
                AllCapabilities,
                render: _ => new DeploymentRenderResult("fake", [
                    DeploymentArtifact.Create("z-provider.json", "{}\n"u8.ToArray()),
                    DeploymentArtifact.Create("a-provider.json", "{}\n"u8.ToArray()),
                ])));
            graph.Project.WithComputeEnvironment(graph.Target);

            var result = await AspireDeploymentPipelineAdapter.RenderAndWriteAsync(
                Assert.IsType<AppSurfaceDeploymentTargetResource>(graph.Target.Resource),
                new DistributedApplicationModel(graph.Builder.Resources),
                CreateOutputServices(output),
                CancellationToken.None);

            Assert.Equal(["a-provider.json", "deployment-intent.v1.json", "z-provider.json"], result.Artifacts.Select(item => item.FileName));
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task RenderAndWrite_RejectsNullProviderArtifactSetBeforeWriting()
    {
        var output = NewOutputDirectory();
        var graph = CreateAnnotatedGraph(target: new FakeTarget(AllCapabilities, render: _ => new DeploymentRenderResult("fake", null!)));
        graph.Project.WithComputeEnvironment(graph.Target);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => AspireDeploymentPipelineAdapter.RenderAndWriteAsync(
            Assert.IsType<AppSurfaceDeploymentTargetResource>(graph.Target.Resource),
            new DistributedApplicationModel(graph.Builder.Resources),
            CreateOutputServices(output),
            CancellationToken.None));

        Assert.Contains("ASDEPLOY211", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task RenderAndWrite_RejectsProviderTargetMismatch()
    {
        var output = NewOutputDirectory();
        try
        {
            var valid = DeploymentArtifact.Create("provider.json", "{}\n"u8.ToArray());
            var graph = CreateAnnotatedGraph(target: new FakeTarget(
                AllCapabilities,
                render: _ => new DeploymentRenderResult("wrong-target", [valid])));
            graph.Project.WithComputeEnvironment(graph.Target);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AspireDeploymentPipelineAdapter.RenderAndWriteAsync(
                    Assert.IsType<AppSurfaceDeploymentTargetResource>(graph.Target.Resource),
                    new DistributedApplicationModel(graph.Builder.Resources),
                    CreateOutputServices(output),
                    CancellationToken.None));

            Assert.Contains("ASDEPLOY220", exception.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task RenderAndWrite_RejectsDuplicateArtifactNameBeforeWriting()
    {
        var output = NewOutputDirectory();
        var artifact = DeploymentArtifact.Create("provider.json", "{}\n"u8.ToArray());
        var graph = CreateAnnotatedGraph(target: new FakeTarget(
            AllCapabilities,
            render: _ => new DeploymentRenderResult("fake", [artifact, artifact])));
        graph.Project.WithComputeEnvironment(graph.Target);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AspireDeploymentPipelineAdapter.RenderAndWriteAsync(
                Assert.IsType<AppSurfaceDeploymentTargetResource>(graph.Target.Resource),
                new DistributedApplicationModel(graph.Builder.Resources),
                CreateOutputServices(output),
                CancellationToken.None));

        Assert.Contains("ASDEPLOY211", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task CompletePublish_ReportsArtifactsAndNegativeAssurance()
    {
        var graph = CreateAnnotatedGraph();
        var resource = Assert.IsType<AppSurfaceDeploymentTargetResource>(graph.Target.Resource);
        var result = new DeploymentRenderResult("fake", [DeploymentArtifact.Create("provider.json", "{}\n"u8.ToArray())]);
        var summaries = new Dictionary<string, string>(StringComparer.Ordinal);
        var reporting = A.Fake<IReportingStep>();

        await AspireDeploymentPipelineAdapter.CompletePublishAsync(
            resource,
            result,
            summaries.Add,
            reporting,
            CancellationToken.None);

        Assert.Equal("fake", summaries["AppSurface target"]);
        Assert.Equal("provider.json", summaries["AppSurface artifacts"]);
        A.CallTo(() => reporting.CompleteAsync(
                "No cloud calls were made. No infrastructure was changed.",
                CompletionState.Completed,
                CancellationToken.None))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task VerifyRendered_ReportsReadOnlySuccessAndNegativeAssurance()
    {
        var graph = CreateAnnotatedGraph(target: new FakeTarget(
            AllCapabilities,
            verify: _ => new DeploymentVerifyResult(true, 12, [], "no-public-principal")));
        var summaries = new Dictionary<string, string>(StringComparer.Ordinal);
        var reporting = A.Fake<IReportingStep>();

        await AspireDeploymentPipelineAdapter.VerifyRenderedAsync(
            Assert.IsType<AppSurfaceDeploymentTargetResource>(graph.Target.Resource),
            new DeploymentRenderResult("fake", []),
            summaries.Add,
            reporting,
            CancellationToken.None);

        Assert.Equal("12", summaries["AppSurface parity fields"]);
        Assert.Equal("no-public-principal", summaries["AppSurface authorization"]);
        A.CallTo(() => reporting.CompleteAsync(
                "Read-only verification completed. No job was executed.",
                CompletionState.Completed,
                CancellationToken.None))
            .MustHaveHappenedOnceExactly();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyRendered_FailsOnParityDrift(bool includeDiagnostic)
    {
        var diagnostics = includeDiagnostic
            ? new[] { DeploymentDiagnostic.Create("ASDEPLOY999", "Drift found.", "Field differs.", "Review it.") }
            : [];
        var graph = CreateAnnotatedGraph(target: new FakeTarget(
            AllCapabilities,
            verify: _ => new DeploymentVerifyResult(false, 12, diagnostics, "unknown")));
        var reporting = A.Fake<IReportingStep>();

        var exception = await Assert.ThrowsAsync<DeploymentValidationException>(() =>
            AspireDeploymentPipelineAdapter.VerifyRenderedAsync(
                Assert.IsType<AppSurfaceDeploymentTargetResource>(graph.Target.Resource),
                new DeploymentRenderResult("fake", []),
                (_, _) => { },
                reporting,
                CancellationToken.None));

        Assert.Equal("ASDEPLOY210", exception.Diagnostic.Code);
        A.CallTo(() => reporting.CompleteAsync(
                A<string>.That.Contains(includeDiagnostic ? "ASDEPLOY999" : "ASDEPLOY210"),
                CompletionState.CompletedWithError,
                CancellationToken.None))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void MigrationOptions_RequireCompleteExplicitContract()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var project = builder.AddResource(new ProjectResource("migration"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            project.WithAppSurfaceMigrationJob(options => options.WithPhase(DeploymentPhase.CandidatePreparation)));

        Assert.Contains("ASDEPLOY207", exception.Message, StringComparison.Ordinal);
        Assert.Empty(project.Resource.Annotations.OfType<AppSurfaceMigrationJobAnnotation>());
    }

    [Fact]
    public void MigrationOptions_RejectParameterFromAnotherBuilder()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var otherBuilder = DistributedApplication.CreateBuilder([]);
        var project = builder.AddResource(new ProjectResource("migration"));
        var image = AddParameter(otherBuilder, "image", Image, secret: false);

        var exception = Assert.Throws<ArgumentException>(() =>
            project.WithAppSurfaceMigrationJob(options => options.WithImage(image)));

        Assert.Contains("ASDEPLOY206", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationOptions_RejectSecretImageAndNonSecretConnection()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var project = builder.AddResource(new ProjectResource("migration"));
        var secretImage = AddParameter(builder, "image", Image, secret: true);
        var connection = AddParameter(builder, "connection", "metadata", secret: false);

        var imageException = Assert.Throws<ArgumentException>(() =>
            project.WithAppSurfaceMigrationJob(options => options.WithImage(secretImage)));
        var connectionException = Assert.Throws<ArgumentException>(() =>
            project.WithAppSurfaceMigrationJob(options => options.WithConnectionSecret(connection, "ConnectionStrings__database")));

        Assert.Contains("ASDEPLOY204", imageException.Message, StringComparison.Ordinal);
        Assert.Contains("ASDEPLOY205", connectionException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationOptions_ReportEachMissingRequiredField()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var image = AddParameter(builder, "image", Image, secret: false);
        var connection = AddParameter(builder, "connection", "not-read", secret: true);

        var missingConnection = Assert.Throws<InvalidOperationException>(() =>
            builder.AddResource(new ProjectResource("missing-connection"))
                .WithAppSurfaceMigrationJob(options => options.WithImage(image)));
        var missingCommand = Assert.Throws<InvalidOperationException>(() =>
            builder.AddResource(new ProjectResource("missing-command"))
                .WithAppSurfaceMigrationJob(options => options.WithImage(image).WithConnectionSecret(connection, "DATABASE")));
        var missingExecution = Assert.Throws<InvalidOperationException>(() =>
            builder.AddResource(new ProjectResource("missing-execution"))
                .WithAppSurfaceMigrationJob(options => options
                    .WithImage(image)
                    .WithConnectionSecret(connection, "DATABASE")
                    .WithCommand("dotnet")));
        var missingNetwork = Assert.Throws<InvalidOperationException>(() =>
            builder.AddResource(new ProjectResource("missing-network"))
                .WithAppSurfaceMigrationJob(options => options
                    .WithImage(image)
                    .WithConnectionSecret(connection, "DATABASE")
                    .WithCommand("dotnet")
                    .WithExecutionPolicy(1, 1, 0, TimeSpan.FromMinutes(10))));

        Assert.Contains("connection secret", missingConnection.Message, StringComparison.Ordinal);
        Assert.Contains("migration command", missingCommand.Message, StringComparison.Ordinal);
        Assert.Contains("execution policy", missingExecution.Message, StringComparison.Ordinal);
        Assert.Contains("private-network requirement", missingNetwork.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "value", "ASDEPLOY212")]
    [InlineData(false, "", "ASDEPLOY213")]
    public async Task BuildIntent_RejectsSecretOrMissingSourceMetadata(bool secret, string value, string diagnostic)
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var target = new FakeTarget(AllCapabilities);
        var binding = new ParameterResource("bindings", _ => "bindings.json", secret: false);
        var revision = new ParameterResource("revision", _ => value, secret);
        var resource = new AppSurfaceDeploymentTargetResource(
            "gcp-staging",
            target,
            binding,
            revision,
            builder.AppHostDirectory,
            builder.Environment.EnvironmentName);
        var targetBuilder = builder.AddResource<IComputeEnvironmentResource>(resource);
        var image = AddParameter(builder, "image", Image, secret: false);
        var connection = AddParameter(builder, "connection", "must-not-be-read", secret: true);
        builder.AddResource(new ProjectResource("migration"))
            .WithAppSurfaceMigrationJob(options => options
                .WithImage(image)
                .WithCommand("dotnet", "/app/Migrations.dll")
                .WithConnectionSecret(connection, "ConnectionStrings__database")
                .RequirePrivateNetwork()
                .WithExecutionPolicy(1, 1, 0, TimeSpan.FromMinutes(10)))
            .WithComputeEnvironment(targetBuilder);

        var exception = await Assert.ThrowsAsync<DeploymentValidationException>(() =>
            AspireDeploymentPipelineAdapter.BuildIntentAsync(
                resource,
                new DistributedApplicationModel(builder.Resources),
                CancellationToken.None));

        Assert.Equal(diagnostic, exception.Diagnostic.Code);
    }

    [Fact]
    public void DeploymentTargetResource_RejectsEndpointProjection()
    {
        var graph = CreateAnnotatedGraph();
        var resource = Assert.IsType<AppSurfaceDeploymentTargetResource>(graph.Target.Resource);

        Assert.Throws<NotSupportedException>(() => resource.GetHostAddressExpression(null!));
        Assert.Null(resource.LastRenderResult);
    }

    [Fact]
    public void ResolveBindingProfilePath_AcceptsRelativePathInsideAppHost()
    {
        var root = TestPathUtils.PathUnder(Path.GetTempPath(), "appsurface-apphost");

        var result = AspireDeploymentPipelineAdapter.ResolveBindingProfilePath(root, TestPathUtils.RelativePath("deployment", "bindings.json"));

        Assert.Equal(TestPathUtils.PathUnder(root, "deployment", "bindings.json"), result);
    }

    [Theory]
    [InlineData("../bindings.json", "ASDEPLOY219")]
    [InlineData("../../bindings.json", "ASDEPLOY219")]
    public void ResolveBindingProfilePath_RejectsParentEscape(string path, string diagnostic)
    {
        var exception = Assert.Throws<DeploymentValidationException>(() =>
            AspireDeploymentPipelineAdapter.ResolveBindingProfilePath(TestPathUtils.PathUnder(Path.GetTempPath(), "apphost"), path));

        Assert.Equal(diagnostic, exception.Diagnostic.Code);
    }

    [Fact]
    public void ResolveBindingProfilePath_RejectsAbsolutePath()
    {
        var absolute = Path.Join(Path.GetPathRoot(Path.GetTempPath())!, "bindings.json");

        var exception = Assert.Throws<DeploymentValidationException>(() =>
            AspireDeploymentPipelineAdapter.ResolveBindingProfilePath(TestPathUtils.PathUnder(Path.GetTempPath(), "apphost"), absolute));

        Assert.Equal("ASDEPLOY218", exception.Diagnostic.Code);
    }

    private static AnnotatedGraph CreateAnnotatedGraph(
        Func<string>? secretValue = null,
        Func<string>? sourceValue = null,
        IReadOnlySet<DeploymentCapability>? capabilities = null,
        IDeploymentTarget? target = null)
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
        });
        var bindings = AddParameter(builder, "bindings", "bindings.json", secret: false);
        var revision = AddParameter(builder, "revision", sourceValue ?? (() => Revision), secret: false);
        var image = AddParameter(builder, "image", Image, secret: false);
        var connection = AddParameter(builder, "connection", secretValue ?? (() => "must-not-be-read"), secret: true);
        var targetBuilder = builder.AddAppSurfaceDeploymentTarget(
            "gcp-staging",
            target ?? new FakeTarget(capabilities ?? AllCapabilities),
            bindings,
            revision);
        var project = builder.AddResource(new ProjectResource("migration"))
            .WithAppSurfaceMigrationJob(options => options
                .WithImage(image)
                .WithPhase(DeploymentPhase.CandidatePreparation)
                .WithCommand("dotnet", "/app/Migrations.dll")
                .WithConnectionSecret(connection, "ConnectionStrings__database")
                .RequirePrivateNetwork()
                .WithExecutionPolicy(1, 1, 0, TimeSpan.FromMinutes(10)));

        return new AnnotatedGraph(builder, targetBuilder, project);
    }

    private static IResourceBuilder<ParameterResource> AddParameter(
        IDistributedApplicationBuilder builder,
        string name,
        string value,
        bool secret) => AddParameter(builder, name, () => value, secret);

    private static IResourceBuilder<ParameterResource> AddParameter(
        IDistributedApplicationBuilder builder,
        string name,
        Func<string> value,
        bool secret) => builder.AddResource(new ParameterResource(name, _ => value(), secret));

    private static IReadOnlySet<DeploymentCapability> AllCapabilities { get; } = new HashSet<DeploymentCapability>
    {
        DeploymentCapability.PrivateNetwork,
        DeploymentCapability.RelationalConnection,
        DeploymentCapability.RunToCompletionJob,
    };

    private sealed record AnnotatedGraph(
        IDistributedApplicationBuilder Builder,
        IResourceBuilder<IComputeEnvironmentResource> Target,
        IResourceBuilder<ProjectResource> Project);

    private static IServiceProvider CreateOutputServices(string output) => new ServiceCollection()
        .AddSingleton<IPipelineOutputService>(new TestOutputService(output))
        .BuildServiceProvider();

    private static string NewOutputDirectory() => TestPathUtils.PathUnder(Path.GetTempPath(), "appsurface-aspire-tests-" + Guid.NewGuid().ToString("N"));

    private sealed class FakeTarget(
        IReadOnlySet<DeploymentCapability> capabilities,
        Func<DeploymentRenderRequest, DeploymentRenderResult>? render = null,
        Func<DeploymentVerifyRequest, DeploymentVerifyResult>? verify = null) : IDeploymentTarget
    {
        public string Name => "fake";

        public IReadOnlySet<DeploymentCapability> Capabilities => capabilities;

        public Task<DeploymentRenderResult> RenderAsync(DeploymentRenderRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(render?.Invoke(request) ?? throw new NotSupportedException());

        public Task<DeploymentVerifyResult> VerifyAsync(DeploymentVerifyRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(verify?.Invoke(request) ?? throw new NotSupportedException());
    }

    private sealed class TestOutputService(string output) : IPipelineOutputService
    {
        public string GetOutputDirectory() => output;

        public string GetOutputDirectory(IResource resource) => output;

        public string GetTempDirectory() => TestPathUtils.PathUnder(output, ".tmp");

        public string GetTempDirectory(IResource resource) => TestPathUtils.PathUnder(output, ".tmp");
    }

    private sealed class OtherComputeEnvironment(string name) : Resource(name), IComputeEnvironmentResource
    {
        public ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference) => throw new NotSupportedException();
    }
}

/// <summary>Serializes tests that construct Aspire application builders because Aspire mutates process-wide host state.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AspireDeploymentBuilderCollection
{
    /// <summary>Gets the xUnit collection name.</summary>
    public const string Name = "Aspire deployment application builder";
}

#pragma warning restore ASPIREPIPELINES001
#pragma warning restore ASPIREPIPELINES004
