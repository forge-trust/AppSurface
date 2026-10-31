using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Deployment;
using ForgeTrust.AppSurface.Deployment.GcpCloudRun;

// This sample deliberately uses only synthetic fixtures and an injected bounded reader.
// It never starts gcloud, submits a job, or mutates a cloud resource.
string? logicalJobId = null;
string? requestedBundleOutput = null;
var bundleOutputProvided = false;
var swapped = false;
for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--swapped" when !swapped:
            swapped = true;
            break;
        case "--bundle-output" when !bundleOutputProvided:
            bundleOutputProvided = true;
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("--bundle-output requires a directory.");
                return 2;
            }

            requestedBundleOutput = args[++index];
            break;
        case "schema-preparation" or "catalog-refresh" when logicalJobId is null:
            logicalJobId = args[index];
            break;
        default:
            Console.Error.WriteLine("Usage: dotnet run -- [schema-preparation|catalog-refresh] [--swapped] [--bundle-output <directory>]");
            return 2;
    }
}

logicalJobId ??= "schema-preparation";

var project = "demo-project";
var region = "us-central1";
var executionId = logicalJobId + "-run";
var imageDigest = logicalJobId == "schema-preparation" ? new string('a', 64) : new string('b', 64);
var image = $"us-docker.pkg.dev/{project}/migrations/{logicalJobId}@sha256:{imageDigest}";
var root = Path.Combine(Path.GetTempPath(), "appsurface-cloud-run-consumer-" + Guid.NewGuid().ToString("N"));
var bundleOutput = requestedBundleOutput is null ? Path.Combine(root, "bundle") : Path.GetFullPath(requestedBundleOutput);
Directory.CreateDirectory(root);
try
{
    var profilePath = Path.Combine(root, "GcpCloudRunBindingProfile.v1.json");
    await File.WriteAllTextAsync(profilePath, JsonSerializer.Serialize(new
    {
        schemaVersion = "1.0",
        environment = "Staging",
        project,
        projectNumber = "123456789012",
        region,
        jobs = new Dictionary<string, string> { [logicalJobId] = logicalJobId },
        cloudSqlInstanceConnectionName = $"{project}:{region}:database",
        network = new { network = $"projects/{project}/global/networks/private", subnetwork = $"projects/{project}/regions/{region}/subnetworks/private", egress = "ALL_TRAFFIC" },
        serviceAccounts = new Dictionary<string, string> { ["migration-runtime"] = $"migration@{project}.iam.gserviceaccount.com" },
        secrets = new Dictionary<string, object> { ["database"] = new { secretId = "db-connection", versionMode = "latest" } }
    }));

    var intent = new DeploymentIntent(
        "Staging",
        new SourceRevision(new string('a', 40)),
        [new MigrationJobIntent(
            new DeploymentLogicalId(logicalJobId),
            DeploymentPhase.CandidatePreparation,
            new ImmutableImageReference(image),
            "dotnet",
            ["migrate"],
            new DeploymentExecutionPolicy(1, 1, 0, TimeSpan.FromSeconds(60)),
            new SecretBinding(new DeploymentLogicalId("database"), new DeploymentLogicalId("database-parameter"), "CONNECTIONSTRINGS_DATABASE"),
            new DatabaseBinding(new DeploymentLogicalId("database"), "ConnectionStrings:database", new DeploymentLogicalId("database")),
            new DeploymentLogicalId("migration-runtime"),
            environment: new Dictionary<string, string> { ["MODE"] = "safe" })]);

    var rendered = await new GcpCloudRunDeploymentTarget().RenderAsync(
        new DeploymentRenderRequest(intent, profilePath, bundleOutput, "example"));
    await DeploymentArtifactBundleWriter.WriteAsync(bundleOutput, rendered.Target, rendered.Artifacts);

    var executionResource = $"projects/{project}/locations/{region}/jobs/{logicalJobId}/executions/{executionId}";
    var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", logicalJobId, swapped ? "swapped-execution.json" : "execution.json");
    var reader = new FixtureReader(await File.ReadAllBytesAsync(fixturePath));
    var observation = await new GcpCloudRunExecutionObserver(reader, TimeProvider.System).ObserveAsync(
        new CloudRunExecutionObservationRequest(rendered, logicalJobId, executionResource, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10)));
    Console.WriteLine(JsonSerializer.Serialize(observation));
    Console.Error.WriteLine($"bundleOutput={bundleOutput}");
    return observation.State == CloudRunExecutionState.Succeeded && observation.ConfigurationMatched ? 0 : 2;
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

sealed class FixtureReader(byte[] response) : IBoundedGcloudExecutionReader
{
    public Task<BoundedGcloudExecutionReadResult> ReadAsync(string executionResourceName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new BoundedGcloudExecutionReadResult(BoundedGcloudExecutionReadOutcome.Complete, 0, response));
    }
}
