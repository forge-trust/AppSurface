using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ForgeTrust.AppSurface.Deployment;

namespace ForgeTrust.AppSurface.Deployment.GcpCloudRun;

/// <summary>Validates one immutable Cloud Run artifact snapshot for execution observation.</summary>
/// <remarks>The observer admission limit is deliberately separate from legacy verification, whose public behavior is preserved.</remarks>
internal static class GcpCloudRunBundleValidator
{
    private const int MaxArtifactBytes = 4 * 1024 * 1024;
    private const int MaxJsonDepth = 64;
    private static readonly Regex ImmutableImage = new("^[a-z0-9][a-z0-9._:/-]*@sha256:[0-9a-f]{64}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex ProjectId = new("^[a-z][a-z0-9-]{4,28}[a-z0-9]\\z", RegexOptions.CultureInvariant);
    private static readonly Regex Region = new("^[a-z]+(?:-[a-z0-9]+)+[0-9]\\z", RegexOptions.CultureInvariant);
    private static readonly Regex JobName = new("^[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?\\z", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> IntentProperties = new(StringComparer.Ordinal)
    {
        "$schema", "schemaVersion", "environment", "sourceRevision", "migrationJobs", "value", "id", "phase", "image", "command", "arguments", "execution", "tasks", "parallelism", "retries", "timeout", "connectionSecret", "parameter", "environmentVariable", "delivery", "database", "configurationKey", "requireUnixSocket", "migrationOwner", "serviceIdentity", "requiredCapabilities", "requirePrivateNetwork", "environment",
    };
    private static readonly HashSet<string> TerraformProperties = new(StringComparer.Ordinal)
    {
        "terraform", "required_providers", "google", "source", "resource", "google_cloud_run_v2_job", "appsurface_migration", "for_each", "name", "location", "project", "labels", "template", "task_count", "parallelism", "service_account", "timeout", "max_retries", "vpc_access", "network_interfaces", "network", "subnetwork", "egress", "volumes", "cloud_sql_instance", "instances", "containers", "image", "command", "args", "environment", "secret_id", "secret_version", "secret_environment", "volume_mounts", "mount_path", "env", "value_source", "secret_key_ref", "secret", "version", "dynamic", "content", "locals", "appsurface_execution_identity", "project_number", "each", "key", "value",
    };
    private static readonly HashSet<string> PlanProperties = new(StringComparer.Ordinal)
    {
        "$schema", "schemaVersion", "target", "environment", "project", "region", "generatorVersion", "sourceRevision", "projectNumber", "intentSha256", "terraformSha256", "resourceAddresses", "requiredCapabilities", "expected", "logicalId", "physicalJobName", "image", "command", "commandElements", "arguments", "environment", "tasks", "parallelism", "retries", "timeout", "network", "subnetwork", "egress", "cloudSqlInstanceConnectionName", "secretEnvironmentVariable", "secretEnvironments", "secretId", "secretVersion", "serviceAccount", "environmentLabel", "sourceRevisionLabel",
    };

    /// <summary>Validates the observation-specific finite input and closed JSON admission rules.</summary>
    internal static GcpCloudRunDeploymentPlan ValidateForObservation(DeploymentRenderResult renderResult)
    {
        ArgumentNullException.ThrowIfNull(renderResult);
        var artifacts = renderResult.Artifacts;
        if (artifacts is null || artifacts.Count != 3)
        {
            throw Failure("ASDEPLOY171", "Deployment observation bundle is invalid.", "An artifact is missing.", "Provide one complete published Cloud Run bundle.");
        }
        var snapshot = new List<DeploymentArtifact>(3);
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < 3; index++)
        {
            var artifact = artifacts[index];
            if (artifact is null)
                throw Failure("ASDEPLOY171", "Deployment observation bundle is invalid.", "An artifact is missing.", "Provide one complete published Cloud Run bundle.");
            if (!names.Add(artifact.FileName))
                throw Failure("ASDEPLOY163", "Deployment artifact set is invalid.", "The bundle contains duplicate artifact names.", "Provide exactly one of each supported v1 artifact.");
            var content = artifact.Content;
            if (content.Length > MaxArtifactBytes)
            {
                throw Failure("ASDEPLOY171", "Deployment observation bundle is too large.", "An artifact exceeds the 4 MiB observation admission limit.", "Publish a bounded Cloud Run bundle.");
            }

            ValidateJson(content, artifact.FileName);
            snapshot.Add(DeploymentArtifact.Create(artifact.FileName, content));
        }

        try
        {
            ValidatePortableIntent(snapshot.Single(item => item.FileName == GcpCloudRunDeploymentTarget.IntentFileName).Content);
            ValidatePlanImages(snapshot.Single(item => item.FileName == GcpCloudRunDeploymentTarget.PlanFileName).Content);
        }
        catch (DeploymentValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw Failure("ASDEPLOY171", "Deployment observation bundle is incomplete.", "A required canonical field could not be normalized.", "Regenerate the complete bundle.");
        }
        var plan = GcpCloudRunDeploymentTarget.ValidateBundleLegacy(new DeploymentRenderResult(renderResult.Target, snapshot.AsReadOnly()));
        if (!ProjectId.IsMatch(plan.Project) || !Region.IsMatch(plan.Region) || plan.Expected.Any(item => !JobName.IsMatch(item.PhysicalJobName) || string.IsNullOrWhiteSpace(item.LogicalId)))
            throw Failure("ASDEPLOY171", "Deployment observation identity is malformed.", "The plan contains a noncanonical project, region, or Job name.", "Regenerate the complete bundle.");
        ValidateTerraformProjection(plan, snapshot.Single(item => item.FileName == GcpCloudRunDeploymentTarget.TerraformFileName).Content);
        if (plan.Expected.Any(item => item is null || !ImmutableImage.IsMatch(item.Image)))
        {
            throw Failure("ASDEPLOY172", "Deployment observation image identity is invalid.", "Every expected image must be a full immutable SHA-256 reference.", "Regenerate the bundle from validated migration intent.");
        }

        if (plan.ProjectNumber is not null && !IsCanonicalProjectNumber(plan.ProjectNumber))
        {
            throw Failure("ASDEPLOY170", "GCP project number is malformed.", "The project number is not canonical positive signed-64-bit decimal.", "Use the canonical numeric project number.");
        }

        return plan;
    }

    private static void ValidatePortableIntent(byte[] content)
    {
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("environment", out var environment) || environment.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("sourceRevision", out var revision) || revision.GetProperty("value").ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("migrationJobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array || jobs.GetArrayLength() == 0)
        {
            throw Failure("ASDEPLOY171", "Deployment observation intent is incomplete.", "The portable intent lacks required execution fields.", "Regenerate the complete bundle.");
        }
        RequireIntentKeys(root, "$schema", "schemaVersion", "environment", "sourceRevision", "migrationJobs");
        RequireIntentKeys(root.GetProperty("sourceRevision"), "value");

        foreach (var job in jobs.EnumerateArray())
        {
            RequireIntentKeys(job, "id", "phase", "image", "command", "arguments", "execution", "connectionSecret", "database", "serviceIdentity", "requiredCapabilities", "requirePrivateNetwork", "environment");
            RequireIntentKeys(job.GetProperty("id"), "value");
            RequireIntentKeys(job.GetProperty("image"), "value");
            RequireString(job, "id", nested: "value");
            var image = RequireString(job, "image", nested: "value");
            if (!ImmutableImage.IsMatch(image)) throw Failure("ASDEPLOY172", "Deployment observation image identity is invalid.", "The portable intent image is not digest pinned.", "Regenerate the bundle from validated migration intent.");
            RequireString(job, "command");
            RequireStringArray(job, "arguments");
            var execution = RequireObject(job, "execution");
            RequireIntentKeys(execution, "tasks", "parallelism", "retries", "timeout");
            foreach (var field in new[] { "tasks", "parallelism", "retries", "timeout" })
            {
                if (!execution.TryGetProperty(field, out _)) throw Failure("ASDEPLOY171", "Deployment observation intent is incomplete.", $"Execution field '{field}' is missing.", "Regenerate the complete bundle.");
            }
            var secret = RequireObject(job, "connectionSecret");
            RequireIntentKeys(secret, "id", "parameter", "environmentVariable", "delivery");
            RequireIntentKeys(secret.GetProperty("id"), "value");
            RequireIntentKeys(secret.GetProperty("parameter"), "value");
            RequireString(secret, "id", nested: "value");
            RequireString(secret, "environmentVariable");
            var database = RequireObject(job, "database");
            RequireIntentKeys(database, "id", "configurationKey", "connectionSecret", "requireUnixSocket", "migrationOwner");
            RequireIntentKeys(database.GetProperty("id"), "value");
            RequireIntentKeys(database.GetProperty("connectionSecret"), "value");
            RequireString(database, "id", nested: "value");
            RequireString(database, "connectionSecret", nested: "value");
            RequireString(job, "serviceIdentity", nested: "value");
        }
    }

    private static void RequireIntentKeys(JsonElement element, params string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal).SetEquals(keys))
            throw Failure("ASDEPLOY171", "Deployment observation intent schema is unsupported.", "The portable intent contains missing or extra fields.", "Regenerate the complete bundle.");
    }

    private static void ValidatePlanImages(byte[] content)
    {
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
        if (!document.RootElement.TryGetProperty("expected", out var expected) || expected.ValueKind != JsonValueKind.Array)
            throw Failure("ASDEPLOY171", "Deployment observation plan is incomplete.", "The expected parity array is missing.", "Regenerate the complete bundle.");
        foreach (var item in expected.EnumerateArray())
        {
            if (!item.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.String || !ImmutableImage.IsMatch(image.GetString()!))
                throw Failure("ASDEPLOY172", "Deployment observation image identity is invalid.", "Every expected image must be a full immutable SHA-256 reference.", "Regenerate the bundle from validated migration intent.");
        }
    }

    private static void ValidateTerraformProjection(GcpCloudRunDeploymentPlan plan, byte[] content)
    {
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
        var root = document.RootElement;
        var rootKeys = root.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var expectedRoot = plan.ProjectNumber is null ? new[] { "terraform", "resource" } : new[] { "terraform", "resource", "locals" };
        if (!rootKeys.SetEquals(expectedRoot)) throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform root contains missing or extra sections.", "Regenerate and keep the three artifacts together.");
        var terraform = GetObject(root, "terraform");
        RequireKeys(terraform, "required_providers");
        var providers = GetObject(terraform, "required_providers");
        RequireKeys(providers, "google");
        var google = GetObject(providers, "google");
        RequireKeys(google, "source");
        if (google.GetProperty("source").GetString() != "hashicorp/google")
            throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform provider source is noncanonical.", "Regenerate and keep the three artifacts together.");
        var resource = GetObject(root, "resource");
        RequireKeys(resource, "google_cloud_run_v2_job");
        var cloudRun = GetObject(resource, "google_cloud_run_v2_job");
        RequireKeys(cloudRun, "appsurface_migration");
        var jobs = GetObject(cloudRun, "appsurface_migration");
        RequireKeys(jobs, "for_each", "name", "location", "project", "labels", "template");
        if (jobs.GetProperty("name").GetString() != "${each.value.name}" || jobs.GetProperty("location").GetString() != plan.Region || jobs.GetProperty("project").GetString() != plan.Project)
            throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform contains a noncanonical provider identity projection.", "Regenerate and keep the three artifacts together.");
        var forEach = GetObject(jobs, "for_each");
        if (forEach.EnumerateObject().Count() != plan.Expected.Count) throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform job inventory is incomplete.", "Regenerate and keep the three artifacts together.");
        foreach (var expected in plan.Expected)
        {
            if (!forEach.TryGetProperty(expected.LogicalId, out var item)) throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform is missing an expected logical job.", "Regenerate and keep the three artifacts together.");
            RequireKeys(item, "name", "image", "command", "args", "environment", "task_count", "parallelism", "max_retries", "timeout", "service_account", "secret_id", "secret_version", "secret_environment");
            if (item.GetProperty("name").GetString() != expected.PhysicalJobName || item.GetProperty("image").GetString() != expected.Image || !StringArrayEquals(item.GetProperty("command"), [expected.Command]) || !StringArrayEquals(item.GetProperty("args"), expected.Arguments) || !JsonNumberEquals(item.GetProperty("task_count"), expected.Tasks) || !JsonNumberEquals(item.GetProperty("parallelism"), expected.Parallelism) || !JsonNumberEquals(item.GetProperty("max_retries"), expected.Retries) || item.GetProperty("timeout").GetString() != expected.Timeout || item.GetProperty("service_account").GetString() != expected.ServiceAccount || item.GetProperty("secret_id").GetString() != expected.SecretId || item.GetProperty("secret_version").GetString() != expected.SecretVersion || item.GetProperty("secret_environment").GetString() != expected.SecretEnvironmentVariable || !EnvironmentEquals(item.GetProperty("environment"), expected.Environment))
                throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform contains a noncanonical logical job projection.", "Regenerate and keep the three artifacts together.");
        }

        var template = GetObject(jobs, "template");
        RequireKeys(template, "task_count", "parallelism", "template");
        if (template.GetProperty("task_count").GetString() != "${each.value.task_count}" || template.GetProperty("parallelism").GetString() != "${each.value.parallelism}")
            throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform task-count projection is noncanonical.", "Regenerate and keep the three artifacts together.");
        var taskTemplate = GetObject(template, "template");
        RequireKeys(taskTemplate, "service_account", "timeout", "max_retries", "vpc_access", "volumes", "containers");
        if (taskTemplate.GetProperty("service_account").GetString() != "${each.value.service_account}" || taskTemplate.GetProperty("timeout").GetString() != "${each.value.timeout}" || taskTemplate.GetProperty("max_retries").GetString() != "${each.value.max_retries}")
            throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform contains a noncanonical task template expression.", "Regenerate and keep the three artifacts together.");
        var vpc = GetObject(taskTemplate, "vpc_access");
        RequireKeys(vpc, "network_interfaces", "egress");
        if (vpc.GetProperty("egress").GetString() != plan.Expected[0].Egress || vpc.GetProperty("network_interfaces").ValueKind != JsonValueKind.Array || vpc.GetProperty("network_interfaces").GetArrayLength() != 1)
            throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform contains a noncanonical network projection.", "Regenerate and keep the three artifacts together.");
        var network = vpc.GetProperty("network_interfaces")[0];
        RequireKeys(network, "network", "subnetwork");
        if (network.GetProperty("network").GetString() != plan.Expected[0].Network || network.GetProperty("subnetwork").GetString() != plan.Expected[0].Subnetwork)
            throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform contains a noncanonical network identity.", "Regenerate and keep the three artifacts together.");
        var volumes = taskTemplate.GetProperty("volumes");
        if (volumes.ValueKind != JsonValueKind.Array || volumes.GetArrayLength() != 1) throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform must declare exactly one Cloud SQL volume.", "Regenerate and keep the three artifacts together.");
        var volume = volumes[0];
        RequireKeys(volume, "name", "cloud_sql_instance");
        var cloudSql = GetObject(volume, "cloud_sql_instance");
        RequireKeys(cloudSql, "instances");
        if (volume.GetProperty("name").GetString() != "cloudsql" || cloudSql.GetProperty("instances").GetArrayLength() != 1 || cloudSql.GetProperty("instances")[0].GetString() != plan.Expected[0].CloudSqlInstanceConnectionName)
            throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform contains a noncanonical Cloud SQL projection.", "Regenerate and keep the three artifacts together.");
        var containers = taskTemplate.GetProperty("containers");
        if (containers.ValueKind != JsonValueKind.Array || containers.GetArrayLength() != 1) throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform must declare exactly one migration container.", "Regenerate and keep the three artifacts together.");
        var container = containers[0];
        RequireKeys(container, "image", "command", "args", "volume_mounts", "env", "dynamic");
        if (container.GetProperty("image").GetString() != "${each.value.image}" || container.GetProperty("command").GetString() != "${each.value.command}" || container.GetProperty("args").GetString() != "${each.value.args}")
            throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform contains a noncanonical container expression.", "Regenerate and keep the three artifacts together.");
        var mounts = container.GetProperty("volume_mounts");
        if (mounts.ValueKind != JsonValueKind.Array || mounts.GetArrayLength() != 1)
            throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform contains a noncanonical Cloud SQL mount.", "Regenerate and keep the three artifacts together.");
        RequireKeys(mounts[0], "name", "mount_path");
        if (mounts[0].GetProperty("name").GetString() != "cloudsql" || mounts[0].GetProperty("mount_path").GetString() != "/cloudsql")
            throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform contains a noncanonical Cloud SQL mount.", "Regenerate and keep the three artifacts together.");
        var env = container.GetProperty("env");
        if (env.ValueKind != JsonValueKind.Array || env.GetArrayLength() != 1) throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform must declare exactly one secret environment entry.", "Regenerate and keep the three artifacts together.");
        var secretEnv = env[0];
        RequireKeys(secretEnv, "name", "value_source");
        if (secretEnv.GetProperty("name").GetString() != "${each.value.secret_environment}") throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform contains a noncanonical secret environment expression.", "Regenerate and keep the three artifacts together.");
        var valueSource = secretEnv.GetProperty("value_source");
        if (valueSource.ValueKind != JsonValueKind.Array || valueSource.GetArrayLength() != 1) throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform secret delivery shape is unsupported.", "Regenerate and keep the three artifacts together.");
        RequireKeys(valueSource[0], "secret_key_ref");
        var secretKeyRef = valueSource[0].GetProperty("secret_key_ref");
        if (secretKeyRef.ValueKind != JsonValueKind.Array || secretKeyRef.GetArrayLength() != 1)
            throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform secret delivery shape is unsupported.", "Regenerate and keep the three artifacts together.");
        var secretRef = secretKeyRef[0];
        if (secretRef.ValueKind != JsonValueKind.Object)
            throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform secret delivery shape is unsupported.", "Regenerate and keep the three artifacts together.");
        RequireKeys(secretRef, "secret", "version");
        if (secretRef.GetProperty("secret").GetString() != "${each.value.secret_id}" || secretRef.GetProperty("version").GetString() != "${each.value.secret_version}") throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform secret delivery expressions are noncanonical.", "Regenerate and keep the three artifacts together.");
        var dynamic = GetObject(container, "dynamic");
        RequireKeys(dynamic, "env");
        var dynamicEnv = GetObject(dynamic, "env");
        RequireKeys(dynamicEnv, "for_each", "content");
        if (dynamicEnv.GetProperty("for_each").GetString() != "${each.value.environment}") throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform contains a noncanonical environment expression.", "Regenerate and keep the three artifacts together.");
        var contentObject = GetObject(dynamicEnv, "content");
        RequireKeys(contentObject, "name", "value");
        if (contentObject.GetProperty("name").GetString() != "${env.key}" || contentObject.GetProperty("value").GetString() != "${env.value}") throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform dynamic environment expressions are noncanonical.", "Regenerate and keep the three artifacts together.");
        if (plan.ProjectNumber is not null)
        {
            var locals = GetObject(root, "locals");
            RequireKeys(locals, "appsurface_execution_identity");
            var identity = GetObject(locals, "appsurface_execution_identity");
            RequireKeys(identity, "project", "project_number");
            if (identity.GetProperty("project").GetString() != plan.Project || identity.GetProperty("project_number").GetString() != plan.ProjectNumber) throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform execution identity locals differ from the plan.", "Regenerate and keep the three artifacts together.");
        }
    }

    private static JsonElement GetObject(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
            throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", $"Terraform property '{property}' is missing or malformed.", "Regenerate and keep the three artifacts together.");
        return value;
    }

    private static void RequireKeys(JsonElement element, params string[] keys)
    {
        var expected = keys.ToHashSet(StringComparer.Ordinal);
        var actual = element.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected))
            throw Failure("ASDEPLOY152", "Deployment evidence identity mismatch.", "Terraform contains missing or extra execution-affecting properties.", "Regenerate and keep the three artifacts together.");
    }

    private static JsonElement RequireObject(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
            throw Failure("ASDEPLOY171", "Deployment observation intent is incomplete.", $"Field '{property}' is missing or malformed.", "Regenerate the complete bundle.");
        return value;
    }

    private static bool StringArrayEquals(JsonElement value, IReadOnlyList<string> expected)
    {
        return value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == expected.Count && value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null).SequenceEqual(expected, StringComparer.Ordinal);
    }

    private static bool JsonNumberEquals(JsonElement value, int expected) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var actual) && actual == expected;

    private static bool EnvironmentEquals(JsonElement value, IReadOnlyDictionary<string, string> expected)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var actual = value.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString() : null, StringComparer.Ordinal);
        return actual.Count == expected.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var actualValue) && actualValue == pair.Value);
    }

    private static string RequireString(JsonElement parent, string property, string? nested = null)
    {
        var value = nested is null ? parent.GetProperty(property) : RequireObject(parent, property).GetProperty(nested);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw Failure("ASDEPLOY171", "Deployment observation intent is incomplete.", $"Field '{property}' is missing or malformed.", "Regenerate the complete bundle.");
        return value.GetString()!;
    }

    private static void RequireStringArray(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw Failure("ASDEPLOY171", "Deployment observation intent is incomplete.", $"Field '{property}' is missing or malformed.", "Regenerate the complete bundle.");
    }

    private static void ValidateJson(byte[] content, string fileName)
    {
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = MaxJsonDepth, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            GcpCloudRunJsonAdmission.ValidateFiniteCollections(document.RootElement);
            ValidateDuplicates(document.RootElement, fileName);
            var allowed = fileName switch
            {
                GcpCloudRunDeploymentTarget.IntentFileName => IntentProperties,
                GcpCloudRunDeploymentTarget.TerraformFileName => TerraformProperties,
                GcpCloudRunDeploymentTarget.PlanFileName => PlanProperties,
                _ => throw Failure("ASDEPLOY171", "Deployment observation bundle contains an unexpected artifact.", $"Artifact '{fileName}' is not part of the closed bundle.", "Provide exactly the supported v1 artifacts."),
            };
            RejectUnknown(document.RootElement, allowed, fileName);
        }
        catch (JsonException)
        {
            throw Failure("ASDEPLOY171", "Deployment observation bundle JSON is invalid.", $"Artifact '{fileName}' is malformed or exceeds the supported JSON depth.", "Regenerate the complete bundle.");
        }
    }

    private static void ValidateDuplicates(JsonElement element, string fileName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw Failure("ASDEPLOY171", "Deployment observation JSON is ambiguous.", $"Artifact '{fileName}' contains a duplicate property.", "Regenerate the complete bundle.");
                ValidateDuplicates(property.Value, fileName);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) ValidateDuplicates(item, fileName);
        }
    }

    private static void RejectUnknown(JsonElement element, HashSet<string> allowed, string fileName)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Failure("ASDEPLOY171", "Deployment observation JSON is invalid.", $"Artifact '{fileName}' must contain a JSON object.", "Regenerate the complete bundle.");
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name)) throw Failure("ASDEPLOY171", "Deployment observation schema is unsupported.", $"Artifact '{fileName}' contains unknown property '{property.Name}'.", "Regenerate the bundle with the supported schema.");
            if (property.Value.ValueKind == JsonValueKind.Object && property.Name is "resourceAddresses" or "labels" or "environment" or "for_each")
            {
                foreach (var value in property.Value.EnumerateObject())
                {
                    if (value.Value.ValueKind == JsonValueKind.Object) RejectUnknown(value.Value, allowed, fileName);
                }
            }
            else if (property.Value.ValueKind == JsonValueKind.Object) RejectUnknown(property.Value, allowed, fileName);
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object) RejectUnknown(item, allowed, fileName);
                }
            }
        }
    }

    private static bool IsCanonicalProjectNumber(string value) =>
        Regex.IsMatch(value, "^[1-9][0-9]*\\z", RegexOptions.CultureInvariant) &&
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0;

    private static DeploymentValidationException Failure(string code, string problem, string cause, string fix) =>
        new(DeploymentDiagnostic.Create(code, problem, cause, fix));
}
