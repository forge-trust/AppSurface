using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Deployment;

namespace ForgeTrust.AppSurface.Deployment.GcpCloudRun;

/// <summary>Compiles migration-job intent to GCP Cloud Run v2 artifacts and performs read-only deployed parity checks.</summary>
public sealed class GcpCloudRunDeploymentTarget : IDeploymentTarget
{
    private const string IntentFileName = "deployment-intent.v1.json";
    private const string TerraformFileName = "gcp-cloud-run-migration.tf.json";
    private const string PlanFileName = "gcp-cloud-run-migration.plan.json";
    private const int MaxCloudRunEnvironmentVariables = 1000;
    private readonly IGcloudCommandRunner _commandRunner;

    /// <summary>Initializes the target with the production read-only process runner.</summary>
    public GcpCloudRunDeploymentTarget() : this(new GcloudCommandRunner()) { }

    /// <summary>Initializes the target with an injectable command runner.</summary>
    /// <param name="commandRunner">Runner used only by <see cref="VerifyAsync"/>.</param>
    public GcpCloudRunDeploymentTarget(IGcloudCommandRunner commandRunner)
    {
        _commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
    }

    /// <summary>Creates the standard GCP Cloud Run deployment target.</summary>
    public static GcpCloudRunDeploymentTarget Create() => new();

    /// <inheritdoc />
    public string Name => "gcp-cloud-run";

    /// <inheritdoc />
    public IReadOnlySet<DeploymentCapability> Capabilities { get; } = new[]
    {
        DeploymentCapability.PrivateNetwork,
        DeploymentCapability.RelationalConnection,
        DeploymentCapability.RunToCompletionJob,
    }.ToFrozenSet();

    /// <inheritdoc />
    public async Task<DeploymentRenderResult> RenderAsync(DeploymentRenderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Intent);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BindingProfilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.GeneratorVersion);
        cancellationToken.ThrowIfCancellationRequested();
        _ = EnvironmentLabel(request.Intent.Environment);
        var profile = request.BindingProfileRoot is null
            ? await GcpCloudRunBindingProfile.LoadAsync(request.BindingProfilePath, request.Intent.Environment, cancellationToken)
            : await GcpCloudRunBindingProfile.LoadAsync(request.BindingProfilePath, request.Intent.Environment, request.BindingProfileRoot, cancellationToken);
        ValidateIntentBindings(request.Intent, profile);

        var intentBytes = DeploymentCanonicalJson.Serialize(request.Intent);
        var terraform = CreateTerraform(request.Intent, profile);
        var terraformBytes = DeploymentCanonicalJson.Serialize(terraform);
        var expected = request.Intent.MigrationJobs.Select(job => CreateParity(job, profile, request.Intent)).OrderBy(item => item.LogicalId, StringComparer.Ordinal).ToArray();
        var plan = new GcpCloudRunDeploymentPlan(
            "https://appsurface.dev/schemas/gcp-cloud-run-migration-plan.v1.json",
            "1.0",
            Name,
            request.Intent.Environment,
            profile.Project,
            profile.Region,
            request.GeneratorVersion,
            request.Intent.SourceRevision.Value,
            DeploymentCanonicalJson.Hash(intentBytes),
            DeploymentCanonicalJson.Hash(terraformBytes),
            new ReadOnlyDictionary<string, string>(new SortedDictionary<string, string>(request.Intent.MigrationJobs.ToDictionary(job => job.Id.Value, job => $"google_cloud_run_v2_job.appsurface_migration[\"{job.Id.Value}\"]", StringComparer.Ordinal), StringComparer.Ordinal)),
            Array.AsReadOnly(request.Intent.MigrationJobs.SelectMany(job => job.RequiredCapabilities).Distinct().OrderBy(capability => capability).ToArray()),
            expected);
        var planBytes = DeploymentCanonicalJson.Serialize(plan);
        var artifacts = new[]
        {
            DeploymentArtifact.Create(IntentFileName, intentBytes),
            DeploymentArtifact.Create(TerraformFileName, terraformBytes),
            DeploymentArtifact.Create(PlanFileName, planBytes),
        };
        return new DeploymentRenderResult(Name, Array.AsReadOnly(artifacts));
    }

    /// <inheritdoc />
    public async Task<DeploymentVerifyResult> VerifyAsync(DeploymentVerifyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RenderResult is null) throw Failure("ASDEPLOY163", "Deployment artifact set is invalid.", "The render result is missing.", "Regenerate the complete artifact bundle.");
        if (!Enum.IsDefined(request.ParityMode)) throw Failure("ASDEPLOY164", "Verification parity mode is unsupported.", $"'{request.ParityMode}' is not a defined parity mode.", "Use shadow before cutover or owned after the generated configuration becomes authoritative.");
        if (!string.Equals(request.RenderResult.Target, Name, StringComparison.Ordinal)) throw Failure("ASDEPLOY150", "Verification target mismatch.", "The evidence bundle belongs to another deployment target.", "Verify artifacts with the target that rendered them.");
        if (request.RenderResult.Artifacts is null || request.RenderResult.Artifacts.Count != 3 || request.RenderResult.Artifacts.Any(item => item is null) || request.RenderResult.Artifacts.Select(item => item.FileName).Distinct(StringComparer.Ordinal).Count() != 3)
        {
            throw Failure("ASDEPLOY163", "Deployment artifact set is invalid.", "The bundle must contain exactly one of each v1 artifact.", "Regenerate the complete artifact bundle.");
        }
        var artifacts = request.RenderResult.Artifacts.ToDictionary(artifact => artifact.FileName, StringComparer.Ordinal);
        var intent = RequireArtifact(artifacts, IntentFileName);
        var terraform = RequireArtifact(artifacts, TerraformFileName);
        var planArtifact = RequireArtifact(artifacts, PlanFileName);
        GcpCloudRunDeploymentPlan plan;
        try
        {
            plan = JsonSerializer.Deserialize<GcpCloudRunDeploymentPlan>(planArtifact.Content, JsonOptions) ?? throw new JsonException("Plan was empty.");
        }
        catch (JsonException)
        {
            throw Failure("ASDEPLOY151", "Deployment evidence is malformed.", "The plan artifact is not valid v1 JSON.", "Regenerate the artifacts with aspire publish.");
        }

        if (plan.Schema != "https://appsurface.dev/schemas/gcp-cloud-run-migration-plan.v1.json" || plan.SchemaVersion != "1.0" || plan.Target != Name || plan.IntentSha256 != intent.Sha256 || plan.TerraformSha256 != terraform.Sha256 || string.IsNullOrWhiteSpace(plan.Project) || string.IsNullOrWhiteSpace(plan.Region) || plan.ResourceAddresses is null || plan.RequiredCapabilities is null || plan.Expected is null || plan.Expected.Count == 0 || plan.Expected.Any(item => item is null || string.IsNullOrWhiteSpace(item.LogicalId) || string.IsNullOrWhiteSpace(item.PhysicalJobName)) || plan.Expected.Select(item => item.LogicalId).Distinct(StringComparer.Ordinal).Count() != plan.Expected.Count || plan.Expected.Select(item => item.PhysicalJobName).Distinct(StringComparer.Ordinal).Count() != plan.Expected.Count || !HasCrossArtifactIdentity(plan, intent.Content, terraform.Content))
        {
            throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Schema, target, or cross-artifact hashes differ.", "Regenerate and keep the three artifacts together.");
        }

        var diagnostics = new List<DeploymentDiagnostic>();
        var compared = 0;
        foreach (var expected in plan.Expected.OrderBy(item => item.LogicalId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var describe = await RunGcloudAsync([
                "run", "jobs", "describe", expected.PhysicalJobName,
                "--project", plan.Project, "--region", plan.Region, "--format=json", "--quiet"], expected.PhysicalJobName, cancellationToken);
            var actual = ParseDeployedParity(describe.StandardOutput, expected.LogicalId, expected.PhysicalJobName);
            compared += Compare(expected, actual, request.ParityMode, diagnostics);

            var iam = await RunGcloudAsync([
                "run", "jobs", "get-iam-policy", expected.PhysicalJobName,
                "--project", plan.Project, "--region", plan.Region, "--format=json", "--quiet"], expected.PhysicalJobName, cancellationToken);
            if (ContainsPublicPrincipal(iam.StandardOutput))
            {
                diagnostics.Add(DeploymentDiagnostic.Create("ASDEPLOY153", "Cloud Run Job has a forbidden public principal.", "Its IAM policy includes allUsers or allAuthenticatedUsers.", "Remove public invocation before accepting parity."));
            }
            compared++;
        }

        return new DeploymentVerifyResult(diagnostics.Count == 0, compared, diagnostics.AsReadOnly(), "not-independently-verified");
    }

    private static bool HasCrossArtifactIdentity(GcpCloudRunDeploymentPlan plan, byte[] intentBytes, byte[] terraformBytes)
    {
        try
        {
            using var intent = JsonDocument.Parse(intentBytes);
            using var terraform = JsonDocument.Parse(terraformBytes);
            var intentRoot = intent.RootElement;
            var terraformJob = terraform.RootElement.GetProperty("resource").GetProperty("google_cloud_run_v2_job").GetProperty("appsurface_migration");
            var sourceRevision = intentRoot.GetProperty("sourceRevision").GetProperty("value").GetString();
            var jobs = terraformJob.GetProperty("for_each");
            var intentCapabilities = intentRoot.GetProperty("migrationJobs")
                .EnumerateArray()
                .SelectMany(job => job.GetProperty("requiredCapabilities").EnumerateArray())
                .Select(item => JsonSerializer.Deserialize<DeploymentCapability>(item.GetRawText()))
                .Distinct()
                .OrderBy(capability => capability)
                .ToArray();
            var portableJobs = intentRoot.GetProperty("migrationJobs")
                .EnumerateArray()
                .ToDictionary(job => job.GetProperty("id").GetProperty("value").GetString()!, StringComparer.Ordinal);
            if (!string.Equals(plan.Environment, intentRoot.GetProperty("environment").GetString(), StringComparison.Ordinal)
                || !string.Equals(plan.SourceRevision, sourceRevision, StringComparison.Ordinal)
                || !string.Equals(plan.Project, terraformJob.GetProperty("project").GetString(), StringComparison.Ordinal)
                || !string.Equals(plan.Region, terraformJob.GetProperty("location").GetString(), StringComparison.Ordinal)
                || jobs.EnumerateObject().Count() != plan.Expected.Count
                || portableJobs.Count != plan.Expected.Count
                || !plan.RequiredCapabilities.SequenceEqual(intentCapabilities)
                || plan.ResourceAddresses.Count != plan.Expected.Count
                || plan.Expected.Any(expected => !plan.ResourceAddresses.TryGetValue(expected.LogicalId, out var address) || address != $"google_cloud_run_v2_job.appsurface_migration[\"{expected.LogicalId}\"]"))
            {
                return false;
            }

            var taskTemplate = terraformJob.GetProperty("template").GetProperty("template");
            var network = taskTemplate.GetProperty("vpc_access").GetProperty("network_interfaces")[0];
            var cloudSql = taskTemplate.GetProperty("volumes")[0].GetProperty("cloud_sql_instance").GetProperty("instances")[0].GetString()!;
            var labels = terraformJob.GetProperty("labels");
            return plan.Expected.All(expected =>
            {
                if (!jobs.TryGetProperty(expected.LogicalId, out var job)
                    || !portableJobs.TryGetValue(expected.LogicalId, out var portableJob)
                    || !MatchesPortableIntent(expected, portableJob)) return false;
                var environment = job.GetProperty("environment").EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString()!, StringComparer.Ordinal);
                var fromTerraform = new GcpCloudRunMigrationParity(
                    expected.LogicalId,
                    job.GetProperty("name").GetString()!,
                    job.GetProperty("image").GetString()!,
                    FirstString(job, "command"),
                    Strings(job, "command").Count,
                    Strings(job, "args"),
                    new ReadOnlyDictionary<string, string>(new SortedDictionary<string, string>(environment, StringComparer.Ordinal)),
                    Int(job, "task_count"),
                    Int(job, "parallelism"),
                    Int(job, "max_retries"),
                    job.GetProperty("timeout").GetString()!,
                    network.GetProperty("network").GetString()!,
                    network.GetProperty("subnetwork").GetString()!,
                    taskTemplate.GetProperty("vpc_access").GetProperty("egress").GetString()!,
                    cloudSql,
                    job.GetProperty("secret_environment").GetString()!,
                    1,
                    job.GetProperty("secret_id").GetString()!,
                    job.GetProperty("secret_version").GetString()!,
                    job.GetProperty("service_account").GetString()!,
                    labels.GetProperty("appsurface-environment").GetString(),
                    labels.GetProperty("appsurface-source-revision").GetString());
                return ParityEquivalent(expected, fromTerraform, includeProvenance: true);
            });
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException or OverflowException or NullReferenceException)
        {
            return false;
        }
    }

    private async Task<GcloudCommandResult> RunGcloudAsync(IReadOnlyList<string> arguments, string job, CancellationToken cancellationToken)
    {
        GcloudCommandResult result;
        try
        {
            result = await _commandRunner.RunAsync(arguments, TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch (GcloudCommandException exception)
        {
            throw Failure(exception.Code, exception.Problem, exception.Cause, exception.Fix);
        }

        if (result.ExitCode == 0) return result;
        var error = result.StandardError ?? string.Empty;
        if (error.Contains("not found", StringComparison.OrdinalIgnoreCase) || error.Contains("NOT_FOUND", StringComparison.OrdinalIgnoreCase)) throw Failure("ASDEPLOY154", "Cloud Run Job was not found.", $"Job '{job}' does not exist in the selected project and region.", "Check the physical job binding and selected environment.");
        if (error.Contains("permission", StringComparison.OrdinalIgnoreCase) || error.Contains("PERMISSION_DENIED", StringComparison.OrdinalIgnoreCase)) throw Failure("ASDEPLOY155", "Permission denied during read-only verification.", "The active identity cannot describe the Job or its IAM policy.", "Grant the documented read-only inspection permissions.");
        if (error.Contains("credential", StringComparison.OrdinalIgnoreCase) || error.Contains("authentication", StringComparison.OrdinalIgnoreCase) || error.Contains("login", StringComparison.OrdinalIgnoreCase)) throw Failure("ASDEPLOY156", "Google Cloud authentication is missing.", "gcloud has no usable read-only credential.", "Authenticate the intended verification identity.");
        throw Failure("ASDEPLOY157", "gcloud verification command failed.", "The read-only command returned a nonzero exit code.", "Run with safe debug logging and inspect the sanitized gcloud diagnostic.");
    }

    private static void ValidateIntentBindings(DeploymentIntent intent, GcpCloudRunBindingProfile profile)
    {
        var physicalJobs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in intent.MigrationJobs)
        {
            if (!profile.Jobs.TryGetValue(job.Id.Value, out var physicalJobName)) throw MissingBinding("job", job.Id.Value);
            if (!profile.ServiceAccounts.ContainsKey(job.ServiceIdentity.Value)) throw MissingBinding("service account", job.ServiceIdentity.Value);
            if (!profile.Secrets.ContainsKey(job.ConnectionSecret.Id.Value)) throw MissingBinding("secret", job.ConnectionSecret.Id.Value);
            if (!physicalJobs.Add(physicalJobName)) throw Failure("ASDEPLOY158", "Physical Cloud Run Job binding is ambiguous.", $"More than one logical job maps to '{physicalJobName}'.", "Map every logical migration job to a distinct physical Cloud Run Job name.");
            if (job.Environment.Count + 1 > MaxCloudRunEnvironmentVariables)
            {
                throw Failure("ASDEPLOY169", "Cloud Run environment variable limit exceeded.", $"Migration job '{job.Id.Value}' declares {job.Environment.Count} plaintext settings plus one secret-backed setting.", $"Keep the total at or below {MaxCloudRunEnvironmentVariables} variables per container.");
            }

            foreach (var name in job.Environment.Keys.Where(IsInvalidOrReservedEnvironmentName))
            {
                throw Failure("ASDEPLOY168", "Invalid or reserved Cloud Run environment variable rejected.", $"'{name}' is invalid or owned by the runtime or credential mechanism.", "Remove or rename the setting and use the declared service identity and Cloud Run runtime metadata.");
            }
        }
    }

    private static DeploymentValidationException MissingBinding(string kind, string id) => Failure("ASDEPLOY159", "Required GCP binding is missing.", $"No {kind} binding exists for logical id '{id}'.", "Add the physical external binding to the selected profile.");

    private static object CreateTerraform(DeploymentIntent intent, GcpCloudRunBindingProfile profile)
    {
        var jobs = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var job in intent.MigrationJobs)
        {
            var secret = profile.Secrets[job.ConnectionSecret.Id.Value];
            jobs.Add(job.Id.Value, new
            {
                name = profile.Jobs[job.Id.Value],
                image = job.Image.Value,
                command = new[] { job.Command },
                args = job.Arguments,
                environment = job.Environment,
                task_count = job.Execution.Tasks,
                parallelism = job.Execution.Parallelism,
                max_retries = job.Execution.Retries,
                timeout = FormatDuration(job.Execution.Timeout),
                service_account = profile.ServiceAccounts[job.ServiceIdentity.Value],
                secret_id = secret.SecretId,
                secret_version = secret.VersionMode,
                secret_environment = job.ConnectionSecret.EnvironmentVariable,
            });
        }

        return new Dictionary<string, object?>
        {
            ["terraform"] = new { required_providers = new { google = new { source = "hashicorp/google" } } },
            ["resource"] = new Dictionary<string, object>
            {
                ["google_cloud_run_v2_job"] = new Dictionary<string, object>
                {
                    ["appsurface_migration"] = new Dictionary<string, object?>
                    {
                        ["for_each"] = jobs,
                        ["name"] = "${each.value.name}",
                        ["location"] = profile.Region,
                        ["project"] = profile.Project,
                        ["labels"] = new Dictionary<string, string> { ["appsurface-environment"] = EnvironmentLabel(intent.Environment), ["appsurface-source-revision"] = intent.SourceRevision.Value },
                        ["template"] = new Dictionary<string, object?>
                        {
                            ["task_count"] = "${each.value.task_count}",
                            ["parallelism"] = "${each.value.parallelism}",
                            ["template"] = new Dictionary<string, object?>
                            {
                                ["service_account"] = "${each.value.service_account}",
                                ["timeout"] = "${each.value.timeout}",
                                ["max_retries"] = "${each.value.max_retries}",
                                ["vpc_access"] = new { network_interfaces = new[] { new { network = profile.Network.Network, subnetwork = profile.Network.Subnetwork } }, egress = profile.Network.Egress },
                                ["volumes"] = new[] { new { name = "cloudsql", cloud_sql_instance = new { instances = new[] { profile.CloudSqlInstanceConnectionName } } } },
                                ["containers"] = new[]
                                {
                                    new Dictionary<string, object?>
                                    {
                                        ["image"] = "${each.value.image}",
                                        ["command"] = "${each.value.command}",
                                        ["args"] = "${each.value.args}",
                                        ["volume_mounts"] = new[] { new { name = "cloudsql", mount_path = "/cloudsql" } },
                                        ["env"] = new[]
                                        {
                                            new
                                            {
                                                name = "${each.value.secret_environment}",
                                                value_source = new[]
                                                {
                                                    new
                                                    {
                                                        secret_key_ref = new[]
                                                        {
                                                            new { secret = "${each.value.secret_id}", version = "${each.value.secret_version}" },
                                                        },
                                                    },
                                                },
                                            },
                                        },
                                        ["dynamic"] = new Dictionary<string, object>
                                        {
                                            ["env"] = new
                                            {
                                                for_each = "${each.value.environment}",
                                                content = new { name = "${env.key}", value = "${env.value}" },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };
    }

    private static GcpCloudRunMigrationParity CreateParity(MigrationJobIntent job, GcpCloudRunBindingProfile profile, DeploymentIntent intent)
    {
        var secret = profile.Secrets[job.ConnectionSecret.Id.Value];
        return new GcpCloudRunMigrationParity(job.Id.Value, profile.Jobs[job.Id.Value], job.Image.Value, job.Command, 1, job.Arguments, job.Environment, job.Execution.Tasks, job.Execution.Parallelism, job.Execution.Retries, FormatDuration(job.Execution.Timeout), profile.Network.Network, profile.Network.Subnetwork, profile.Network.Egress, profile.CloudSqlInstanceConnectionName, job.ConnectionSecret.EnvironmentVariable, 1, secret.SecretId, secret.VersionMode, profile.ServiceAccounts[job.ServiceIdentity.Value], EnvironmentLabel(intent.Environment), intent.SourceRevision.Value);
    }

    private static GcpCloudRunMigrationParity ParseDeployedParity(string json, string logicalId, string physicalName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("name", out var deployedName) && !deployedName.GetString()!.EndsWith("/jobs/" + physicalName, StringComparison.Ordinal)) throw new JsonException("Physical job identity mismatch.");
            var execution = root.GetProperty("template");
            var task = execution.GetProperty("template");
            var container = task.GetProperty("containers")[0];
            var network = task.GetProperty("vpcAccess").GetProperty("networkInterfaces")[0];
            var command = Strings(container, "command");
            var secretEnvironments = container.GetProperty("env").EnumerateArray().Where(item => item.TryGetProperty("valueSource", out _)).ToArray();
            var secretEnv = secretEnvironments.First();
            var environment = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in container.GetProperty("env").EnumerateArray().Where(item => !item.TryGetProperty("valueSource", out _)))
            {
                environment.Add(item.GetProperty("name").GetString()!, item.GetProperty("value").GetString()!);
            }
            var secret = secretEnv.GetProperty("valueSource").GetProperty("secretKeyRef");
            var cloudSql = task.GetProperty("volumes").EnumerateArray().First(item => item.TryGetProperty("cloudSqlInstance", out _)).GetProperty("cloudSqlInstance").GetProperty("instances")[0].GetString()!;
            var labels = root.TryGetProperty("labels", out var labelElement) ? labelElement : default;
            return new GcpCloudRunMigrationParity(logicalId, physicalName, container.GetProperty("image").GetString()!, command.FirstOrDefault() ?? string.Empty, command.Count, Strings(container, "args"), new ReadOnlyDictionary<string, string>(environment), Int(execution, "taskCount"), Int(execution, "parallelism"), Int(task, "maxRetries"), task.GetProperty("timeout").GetString()!, network.GetProperty("network").GetString()!, network.GetProperty("subnetwork").GetString()!, task.GetProperty("vpcAccess").GetProperty("egress").GetString()!, cloudSql, secretEnv.GetProperty("name").GetString()!, secretEnvironments.Length, secret.GetProperty("secret").GetString()!, secret.GetProperty("version").GetString()!, task.GetProperty("serviceAccount").GetString()!, Label(labels, "appsurface-environment"), Label(labels, "appsurface-source-revision"));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException or OverflowException or NullReferenceException)
        {
            throw Failure("ASDEPLOY160", "Cloud Run Job response is malformed.", "The read-only response lacks required normalized fields.", "Confirm gcloud compatibility and inspect the raw response outside AppSurface logs.");
        }
    }

    private static int Compare(GcpCloudRunMigrationParity expected, GcpCloudRunMigrationParity actual, DeploymentParityMode mode, List<DeploymentDiagnostic> diagnostics)
    {
        var fields = new List<(string Name, object? Expected, object? Actual)>
        {
            ("image", expected.Image, actual.Image), ("command", expected.Command, actual.Command), ("commandElements", expected.CommandElements, actual.CommandElements), ("arguments", string.Join('\0', expected.Arguments), string.Join('\0', actual.Arguments)),
            ("environment", CanonicalEnvironment(expected.Environment), CanonicalEnvironment(actual.Environment)),
            ("tasks", expected.Tasks, actual.Tasks), ("parallelism", expected.Parallelism, actual.Parallelism), ("retries", expected.Retries, actual.Retries), ("timeout", expected.Timeout, actual.Timeout),
            ("network", expected.Network, actual.Network), ("subnetwork", expected.Subnetwork, actual.Subnetwork), ("egress", expected.Egress, actual.Egress), ("cloudSql", expected.CloudSqlInstanceConnectionName, actual.CloudSqlInstanceConnectionName),
            ("secretEnvironment", expected.SecretEnvironmentVariable, actual.SecretEnvironmentVariable), ("secretEnvironments", expected.SecretEnvironments, actual.SecretEnvironments), ("secret", expected.SecretId, actual.SecretId), ("secretVersion", expected.SecretVersion, actual.SecretVersion), ("serviceAccount", expected.ServiceAccount, actual.ServiceAccount),
        };
        if (mode == DeploymentParityMode.Owned)
        {
            fields.Add(("environmentLabel", expected.EnvironmentLabel, actual.EnvironmentLabel));
            fields.Add(("sourceRevisionLabel", expected.SourceRevisionLabel, actual.SourceRevisionLabel));
        }

        foreach (var field in fields.Where(field => !Equals(field.Expected, field.Actual)))
        {
            diagnostics.Add(DeploymentDiagnostic.Create("ASDEPLOY161", "Cloud Run Job parity mismatch.", $"Field '{field.Name}' differs for '{expected.LogicalId}'.", "Reconcile the generated artifact and deployed Job before cutover."));
        }

        return fields.Count;
    }

    private static bool ContainsPublicPrincipal(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("bindings", out var bindings)) return false;
            return bindings.EnumerateArray().SelectMany(binding => binding.GetProperty("members").EnumerateArray()).Select(member => member.GetString()).Any(member => member is "allUsers" or "allAuthenticatedUsers");
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw Failure("ASDEPLOY162", "Cloud Run IAM response is malformed.", "The read-only IAM response cannot be normalized.", "Confirm gcloud compatibility and permissions.");
        }
    }

    private static DeploymentArtifact RequireArtifact(IReadOnlyDictionary<string, DeploymentArtifact> artifacts, string name) => artifacts.TryGetValue(name, out var artifact) ? artifact : throw Failure("ASDEPLOY163", "Deployment artifact is missing.", $"'{name}' is absent.", "Regenerate the complete artifact bundle.");
    private static int Int(JsonElement element, string property) => element.GetProperty(property).ValueKind == JsonValueKind.String ? int.Parse(element.GetProperty(property).GetString()!, CultureInfo.InvariantCulture) : element.GetProperty(property).GetInt32();
    private static IReadOnlyList<string> Strings(JsonElement element, string property) => element.TryGetProperty(property, out var value) ? value.EnumerateArray().Select(item => item.GetString()!).ToArray() : [];
    private static string FirstString(JsonElement element, string property) => Strings(element, property).FirstOrDefault() ?? string.Empty;
    private static string? Label(JsonElement labels, string name) => labels.ValueKind == JsonValueKind.Object && labels.TryGetProperty(name, out var value) ? value.GetString() : null;
    private static string FormatDuration(TimeSpan value) => string.Create(CultureInfo.InvariantCulture, $"{(long)value.TotalSeconds}s");
    private static string EnvironmentLabel(string value)
    {
        var label = value.ToLowerInvariant();
        if (label.Length > 63 || label.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))) throw Failure("ASDEPLOY167", "Aspire environment cannot be represented as a GCP label.", $"Environment '{value}' contains unsupported label characters or exceeds 63 characters.", "Use an Aspire environment containing only ASCII letters, digits, underscores, or hyphens.");
        return label;
    }

    private static string CanonicalEnvironment(IReadOnlyDictionary<string, string> environment) =>
        JsonSerializer.Serialize(environment.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new KeyValuePair<string, string>(item.Key, item.Value)));

    private static bool EnvironmentEquivalent(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual) =>
        expected.Count == actual.Count &&
        expected.All(pair => actual.TryGetValue(pair.Key, out var value) && string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static bool IsInvalidOrReservedEnvironmentName(string name) =>
        string.IsNullOrEmpty(name) ||
        name.Contains('=') ||
        name.StartsWith("X_GOOGLE_", StringComparison.Ordinal) ||
        ReservedEnvironmentNames.Contains(name);

    private static readonly FrozenSet<string> ReservedEnvironmentNames = new[]
    {
        "CLOUD_RUN_EXECUTION",
        "CLOUD_RUN_JOB",
        "CLOUD_RUN_TASK_ATTEMPT",
        "CLOUD_RUN_TASK_COUNT",
        "CLOUD_RUN_TASK_INDEX",
        "GOOGLE_APPLICATION_CREDENTIALS",
        "K_CONFIGURATION",
        "K_REVISION",
        "K_SERVICE",
        "PORT",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static bool ParityEquivalent(GcpCloudRunMigrationParity expected, GcpCloudRunMigrationParity actual, bool includeProvenance) =>
        expected.LogicalId == actual.LogicalId &&
        expected.PhysicalJobName == actual.PhysicalJobName &&
        expected.Image == actual.Image &&
        expected.Command == actual.Command &&
        expected.CommandElements == actual.CommandElements &&
        expected.Arguments.SequenceEqual(actual.Arguments, StringComparer.Ordinal) &&
        EnvironmentEquivalent(expected.Environment, actual.Environment) &&
        expected.Tasks == actual.Tasks && expected.Parallelism == actual.Parallelism && expected.Retries == actual.Retries && expected.Timeout == actual.Timeout &&
        expected.Network == actual.Network && expected.Subnetwork == actual.Subnetwork && expected.Egress == actual.Egress && expected.CloudSqlInstanceConnectionName == actual.CloudSqlInstanceConnectionName &&
        expected.SecretEnvironmentVariable == actual.SecretEnvironmentVariable && expected.SecretEnvironments == actual.SecretEnvironments && expected.SecretId == actual.SecretId && expected.SecretVersion == actual.SecretVersion && expected.ServiceAccount == actual.ServiceAccount &&
        (!includeProvenance || (expected.EnvironmentLabel == actual.EnvironmentLabel && expected.SourceRevisionLabel == actual.SourceRevisionLabel));

    private static bool MatchesPortableIntent(GcpCloudRunMigrationParity expected, JsonElement job)
    {
        var execution = job.GetProperty("execution");
        var environment = job.GetProperty("environment").EnumerateObject()
            .ToDictionary(item => item.Name, item => item.Value.GetString()!, StringComparer.Ordinal);
        return expected.LogicalId == job.GetProperty("id").GetProperty("value").GetString() &&
        expected.Image == job.GetProperty("image").GetProperty("value").GetString() &&
        expected.Command == job.GetProperty("command").GetString() &&
        expected.CommandElements == 1 &&
        expected.Arguments.SequenceEqual(Strings(job, "arguments"), StringComparer.Ordinal) &&
        EnvironmentEquivalent(expected.Environment, environment) &&
        expected.Tasks == execution.GetProperty("tasks").GetInt32() &&
        expected.Parallelism == execution.GetProperty("parallelism").GetInt32() &&
        expected.Retries == execution.GetProperty("retries").GetInt32() &&
        expected.Timeout == FormatDuration(TimeSpan.Parse(execution.GetProperty("timeout").GetString()!, CultureInfo.InvariantCulture)) &&
        expected.SecretEnvironmentVariable == job.GetProperty("connectionSecret").GetProperty("environmentVariable").GetString();
    }
    private static DeploymentValidationException Failure(string code, string problem, string cause, string fix) => new(DeploymentDiagnostic.Create(code, problem, cause, fix));

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}

/// <summary>Safe normalized Cloud Run migration-job configuration used for shadow and owned parity.</summary>
internal sealed record GcpCloudRunMigrationParity(string LogicalId, string PhysicalJobName, string Image, string Command, int CommandElements, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment, int Tasks, int Parallelism, int Retries, string Timeout, string Network, string Subnetwork, string Egress, string CloudSqlInstanceConnectionName, string SecretEnvironmentVariable, int SecretEnvironments, string SecretId, string SecretVersion, string ServiceAccount, string? EnvironmentLabel, string? SourceRevisionLabel);

internal sealed record GcpCloudRunDeploymentPlan([property: JsonPropertyName("$schema")] string Schema, string SchemaVersion, string Target, string Environment, string Project, string Region, string GeneratorVersion, string SourceRevision, string IntentSha256, string TerraformSha256, IReadOnlyDictionary<string, string> ResourceAddresses, IReadOnlyList<DeploymentCapability> RequiredCapabilities, IReadOnlyList<GcpCloudRunMigrationParity> Expected);

/// <summary>Executes one read-only gcloud invocation expressed as an argument array.</summary>
public interface IGcloudCommandRunner
{
    /// <summary>Runs gcloud with bounded time and cancellation.</summary>
    Task<GcloudCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Captured process result. Callers must not log raw output without provider sanitization.</summary>
/// <param name="ExitCode">Process exit code.</param>
/// <param name="StandardOutput">Captured standard output consumed only by safe provider parsing.</param>
/// <param name="StandardError">Captured standard error consumed only by safe classification.</param>
public sealed record GcloudCommandResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Safe classified process-launch or timeout failure.</summary>
public sealed class GcloudCommandException : Exception
{
    /// <summary>Initializes a safe failure without raw process output.</summary>
    /// <param name="code">Stable deployment diagnostic code.</param>
    /// <param name="problem">Safe failure summary.</param>
    /// <param name="cause">Safe classified cause.</param>
    /// <param name="fix">Concrete remediation.</param>
    public GcloudCommandException(string code, string problem, string cause, string fix) : base($"{code}: {problem}") { Code = code; Problem = problem; Cause = cause; Fix = fix; }
    /// <summary>Gets the stable diagnostic code.</summary>
    public string Code { get; }
    /// <summary>Gets the safe problem.</summary>
    public string Problem { get; }
    /// <summary>Gets the safe cause.</summary>
    public string Cause { get; }
    /// <summary>Gets the remediation.</summary>
    public string Fix { get; }
}

/// <summary>Production gcloud process runner. It never uses a shell and kills the process tree on timeout or cancellation.</summary>
public sealed class GcloudCommandRunner : IGcloudCommandRunner
{
    private readonly string _executable;

    /// <summary>Initializes a runner that resolves <c>gcloud</c> through the host process path.</summary>
    public GcloudCommandRunner()
        : this("gcloud")
    {
    }

    internal GcloudCommandRunner(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        _executable = executable;
    }

    /// <inheritdoc />
    public async Task<GcloudCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var startInfo = new ProcessStartInfo { FileName = _executable, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument ?? throw new ArgumentException("gcloud arguments cannot be null.", nameof(arguments)));
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new GcloudCommandException("ASDEPLOY165", "gcloud failed to start.", "The process did not start.", "Install the supported Google Cloud CLI.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new GcloudCommandException("ASDEPLOY165", "gcloud is unavailable.", "The executable could not be started.", "Install the supported Google Cloud CLI and ensure it is on PATH.");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new GcloudCommandException("ASDEPLOY166", "gcloud verification timed out.", "The read-only command exceeded its bounded timeout.", "Check CLI connectivity and retry.");
        }
        catch
        {
            TryKill(process);
            throw;
        }

        return new GcloudCommandResult(process.ExitCode, await stdout, await stderr);
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(Justification = "Best-effort process cleanup handles OS timing races after timeout or cancellation; public failure paths are covered.")]
    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            Trace.TraceWarning(
                "AppSurface GCP verification could not terminate a timed-out gcloud process. Exception={0}; HResult={1}.",
                exception.GetType().FullName,
                exception.HResult);
        }
    }
}
