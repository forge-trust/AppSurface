using Aspire.Hosting;
using ForgeTrust.AppSurface.Aspire;
using ForgeTrust.AppSurface.Deployment;
using ForgeTrust.AppSurface.Deployment.GcpCloudRun;

var builder = DistributedApplication.CreateBuilder(args);

var bindingProfile = builder.AddParameter("appsurface-gcp-bindings");
var migrationImage = builder.AddParameter("example-migrations-image");
var sourceRevision = builder.AddParameter("appsurface-source-revision");
var connectionSecret = builder.AddParameter("example-connection", secret: true);

var target = builder.AddAppSurfaceDeploymentTarget(
    "gcp-staging",
    GcpCloudRunDeploymentTarget.Create(),
    bindingProfile,
    sourceRevision);

builder
    .AddProject<Projects.WebAppExample>("example-migrations")
    .WithAppSurfaceMigrationJob(options => options
        .WithImage(migrationImage)
        .WithPhase(DeploymentPhase.CandidatePreparation)
        .WithCommand("dotnet", "/app/WebAppExample.dll", "--migrate")
        .WithConnectionSecret(connectionSecret, "ConnectionStrings__example")
        .RequirePrivateNetwork()
        .WithExecutionPolicy(tasks: 1, parallelism: 1, retries: 0, timeout: TimeSpan.FromMinutes(10)))
    .WithComputeEnvironment(target);

await builder.Build().RunAsync();
