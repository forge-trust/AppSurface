using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Deployment.GcpCloudRun;

/// <summary>Pure, presence-aware execution proof. It never reads today's Job or a provider message.</summary>
internal sealed record CloudRunExecutionEvidence(CloudRunExecutionState State, string DiagnosticCode, string? Uid)
{
    private static readonly HashSet<string> BenignAnnotations = new(StringComparer.Ordinal)
    {
        "run.googleapis.com/client-name", "run.googleapis.com/client-version", "run.googleapis.com/creator",
        "run.googleapis.com/lastModifier", "run.googleapis.com/operation-id", "run.googleapis.com/cloudsql-instances",
        "run.googleapis.com/network-interfaces", "run.googleapis.com/vpc-access-egress", "run.googleapis.com/execution-environment",
        "run.googleapis.com/secrets"
    };

    /// <summary>Checks one complete bounded JSON response against an immutable expectation.</summary>
    internal static CloudRunExecutionEvidence Classify(ReadOnlyMemory<byte> bytes, GcpCloudRunDeploymentPlan plan, GcpCloudRunMigrationParity expected, ExecutionResource selected)
    {
        string? uid = null;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            GcpCloudRunJsonAdmission.ValidateFiniteCollections(root);
            Unique(root);
            var v1 = root.TryGetProperty("metadata", out var metadata);
            if (v1 && root.TryGetProperty("name", out _)) throw Bad("ASDEPLOY174");
            if (!v1 && !root.TryGetProperty("name", out _)) throw Bad("ASDEPLOY175");
            var identity = v1 ? metadata : root;
            CheckIdentity(root, identity, v1, plan, selected);
            uid = Text(identity, "uid");
            if (!Guid.TryParseExact(uid, "D", out var parsedUid) || parsedUid == Guid.Empty) throw Bad("ASDEPLOY174");
            var status = v1 ? Object(root, "status") : root;
            var generation = Number(identity, "generation", positive: true, maximum: v1 ? int.MaxValue : long.MaxValue);
            if (Number(status, "observedGeneration", positive: true, maximum: v1 ? int.MaxValue : long.MaxValue) != generation) throw Bad("ASDEPLOY176");
            var spec = v1 ? Object(root, "spec") : root;
            var task = v1 ? Object(Object(spec, "template"), "spec") : Object(root, "template");
            if (v1)
            {
                Only(spec, "parallelism", "taskCount", "template");
                Only(Object(spec, "template"), "spec");
            }
            var containers = Array(task, "containers");
            if (containers.GetArrayLength() != 1) throw Bad("ASDEPLOY175");
            var container = containers[0];
            Only(container, "name", "image", "command", "args", "env", "resources", "volumeMounts");
            CheckResourceDefaults(container);
            var annotations = v1 ? Object(identity, "annotations") : default;
            if (v1)
            {
                foreach (var item in annotations.EnumerateObject())
                    if (!BenignAnnotations.Contains(item.Name) || item.Value.ValueKind != JsonValueKind.String) throw Bad("ASDEPLOY175");
                Only(task, "containers", "maxRetries", "timeoutSeconds", "serviceAccountName", "volumes");
                if (Text(annotations, "run.googleapis.com/execution-environment") != "gen2") throw Bad("ASDEPLOY175");
            }
            else
            {
                Only(task, "containers", "maxRetries", "timeout", "serviceAccount", "vpcAccess", "volumes", "executionEnvironment");
                // The v2 omission is not admitted until a compatibility manifest documents the provider default.
                if (!task.TryGetProperty("executionEnvironment", out var environment) || environment.ValueKind != JsonValueKind.String || environment.GetString() != "EXECUTION_ENVIRONMENT_GEN2") throw Bad("ASDEPLOY175");
            }
            CheckConfiguration(spec, task, container, annotations, v1, plan, expected);
            var state = ClassifyState(status, spec, v1);
            return new(state, state switch
            {
                CloudRunExecutionState.Succeeded => "ASDEPLOY183",
                CloudRunExecutionState.Failed => "ASDEPLOY184",
                CloudRunExecutionState.Cancelled => "ASDEPLOY185",
                _ => "ASDEPLOY182"
            }, uid);
        }
        catch (EvidenceException error) { return new(CloudRunExecutionState.Unknown, error.Code, uid); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException or ArgumentException)
        {
            return new(CloudRunExecutionState.Unknown, "ASDEPLOY174", uid);
        }
    }

    private static void CheckIdentity(JsonElement root, JsonElement identity, bool v1, GcpCloudRunDeploymentPlan plan, ExecutionResource selected)
    {
        if (v1)
        {
            if (Text(root, "apiVersion") != "run.googleapis.com/v1" || Text(root, "kind") != "Execution") throw Bad("ASDEPLOY175");
            var ns = Text(identity, "namespace");
            if (ns != plan.Project && ns != plan.ProjectNumber) throw Bad("ASDEPLOY172");
            if (Text(identity, "name") != selected.Execution) throw Bad("ASDEPLOY172");
            var labels = Object(identity, "labels");
            if (Text(labels, "cloud.googleapis.com/location") != selected.Region || Text(labels, "run.googleapis.com/job") != selected.Job) throw Bad("ASDEPLOY172");
            if (identity.TryGetProperty("selfLink", out var self))
            {
                var link = self.GetString();
                if (link != $"/apis/run.googleapis.com/v1/namespaces/{ns}/executions/{selected.Execution}" && link != $"apis/run.googleapis.com/v1/namespaces/{ns}/executions/{selected.Execution}") throw Bad("ASDEPLOY172");
            }
            Only(root, "apiVersion", "kind", "metadata", "spec", "status");
        }
        else
        {
            if (!ExecutionResource.TryParse(Text(root, "name"), out var actual) || actual is null || !actual.Matches(plan, plan.Expected.Single(x => x.PhysicalJobName == selected.Job)) || actual.Execution != selected.Execution) throw Bad("ASDEPLOY172");
            var parent = Text(root, "job");
            var expectedParentId = $"projects/{plan.Project}/locations/{selected.Region}/jobs/{selected.Job}";
            var expectedParentNumber = plan.ProjectNumber is null ? null : $"projects/{plan.ProjectNumber}/locations/{selected.Region}/jobs/{selected.Job}";
            if (parent != expectedParentId && parent != expectedParentNumber) throw Bad("ASDEPLOY172");
            Only(root, "name", "uid", "creator", "generation", "labels", "annotations", "createTime", "startTime", "completionTime", "updateTime", "deleteTime", "expireTime", "launchStage", "job", "parallelism", "taskCount", "template", "reconciling", "conditions", "observedGeneration", "runningCount", "succeededCount", "failedCount", "cancelledCount", "retriedCount", "logUri", "satisfiesPzs", "client", "clientVersion", "etag");
            if (root.TryGetProperty("annotations", out var annotations) && annotations.EnumerateObject().Any())
                foreach (var annotation in annotations.EnumerateObject())
                    if (annotation.Name is not ("run.googleapis.com/client-name" or "run.googleapis.com/client-version" or "run.googleapis.com/creator" or "run.googleapis.com/lastModifier" or "run.googleapis.com/operation-id")
                        || annotation.Value.ValueKind != JsonValueKind.String) throw Bad("ASDEPLOY175");
            if (root.TryGetProperty("deleteTime", out _)) throw Bad("ASDEPLOY176");
            if (root.TryGetProperty("labels", out var labels))
            {
                if (labels.ValueKind != JsonValueKind.Object) throw Bad("ASDEPLOY174");
                if (labels.TryGetProperty("cloud.googleapis.com/location", out var location) && location.GetString() != selected.Region) throw Bad("ASDEPLOY172");
                if (labels.TryGetProperty("run.googleapis.com/job", out var job) && job.GetString() != selected.Job) throw Bad("ASDEPLOY172");
            }
        }
        if (identity.TryGetProperty("deletionTimestamp", out _)) throw Bad("ASDEPLOY176");
    }

    private static void CheckConfiguration(JsonElement spec, JsonElement task, JsonElement container, JsonElement annotations, bool v1, GcpCloudRunDeploymentPlan plan, GcpCloudRunMigrationParity expected)
    {
        var image = Text(container, "image");
        if (!Regex.IsMatch(image, "^[a-z0-9][a-z0-9._:/-]*@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)) throw Bad("ASDEPLOY174");
        Match(image == expected.Image);
        Match(Strings(container, "command", optional: false).SequenceEqual([expected.Command], StringComparer.Ordinal));
        Match(Strings(container, "args", optional: expected.Arguments.Count == 0).SequenceEqual(expected.Arguments, StringComparer.Ordinal));
        Match(Number(spec, "taskCount", true) == expected.Tasks && Number(spec, "parallelism", true) == expected.Parallelism);
        Match(Number(task, "maxRetries") == expected.Retries);
        var timeout = v1 ? TimeSpan.FromSeconds(Number(task, "timeoutSeconds", true)) : Duration(Text(task, "timeout"));
        Match(timeout == Duration(expected.Timeout));
        Match(Text(task, v1 ? "serviceAccountName" : "serviceAccount") == expected.ServiceAccount);
        CheckEnvironment(container, annotations, v1, plan, expected);
        if (v1)
        {
            // The v1 single-container built-in SQL annotation represents the implicit socket configuration.
            // Explicit or additional volumes/mounts are outside this admitted inventory.
            EmptyInventory(task, "volumes");
            EmptyInventory(container, "volumeMounts");
            Match(Text(annotations, "run.googleapis.com/cloudsql-instances") == expected.CloudSqlInstanceConnectionName);
            var egress = Text(annotations, "run.googleapis.com/vpc-access-egress");
            Match(egress == (expected.Egress == "ALL_TRAFFIC" ? "all-traffic" : "private-ranges-only"));
            using var interfaces = JsonDocument.Parse(Text(annotations, "run.googleapis.com/network-interfaces"), new JsonDocumentOptions { MaxDepth = 8 });
            GcpCloudRunJsonAdmission.ValidateFiniteCollections(interfaces.RootElement);
            Unique(interfaces.RootElement);
            CheckNetwork(interfaces.RootElement, plan, expected);
        }
        else
        {
            var vpc = Object(task, "vpcAccess");
            Only(vpc, "networkInterfaces", "egress");
            CheckNetwork(Array(vpc, "networkInterfaces"), plan, expected);
            var egress = Text(vpc, "egress");
            Match(egress == expected.Egress);
            var volumes = Array(task, "volumes");
            if (volumes.GetArrayLength() != 1) throw Bad("ASDEPLOY175");
            Only(volumes[0], "name", "cloudSqlInstance");
            var volumeName = Text(volumes[0], "name");
            var sql = Object(volumes[0], "cloudSqlInstance");
            Only(sql, "instances");
            Match(Strings(sql, "instances", false).SequenceEqual([expected.CloudSqlInstanceConnectionName], StringComparer.Ordinal));
            var mounts = Array(container, "volumeMounts");
            if (mounts.GetArrayLength() != 1) throw Bad("ASDEPLOY175");
            Only(mounts[0], "name", "mountPath");
            Match(Text(mounts[0], "name") == volumeName && Text(mounts[0], "mountPath") == "/cloudsql");
        }
    }

    private static void CheckEnvironment(JsonElement container, JsonElement annotations, bool v1, GcpCloudRunDeploymentPlan plan, GcpCloudRunMigrationParity expected)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var secrets = 0;
        foreach (var item in Array(container, "env").EnumerateArray())
        {
            Only(item, "name", "value", v1 ? "valueFrom" : "valueSource");
            var name = Text(item, "name");
            if (!values.TryAdd(name, string.Empty)) throw Bad("ASDEPLOY174");
            if (item.TryGetProperty(v1 ? "valueFrom" : "valueSource", out var source))
            {
                if (item.TryGetProperty("value", out _)) throw Bad("ASDEPLOY174");
                secrets++;
                Only(source, "secretKeyRef");
                var secret = Object(source, "secretKeyRef");
                Only(secret, v1 ? "name" : "secret", v1 ? "key" : "version");
                var reference = Text(secret, v1 ? "name" : "secret");
                if (v1 && annotations.TryGetProperty("run.googleapis.com/secrets", out var aliases))
                {
                    var entries = aliases.GetString()!.Split(',');
                    var map = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var entry in entries)
                    {
                        var pair = entry.Split(':', 2);
                        if (pair.Length != 2 || !map.TryAdd(pair[0], pair[1])) throw Bad("ASDEPLOY174");
                    }
                    if (map.Count != 1 || !map.TryGetValue(reference, out reference!)) throw Bad("ASDEPLOY175");
                }
                var expectedReference = $"projects/{plan.Project}/secrets/{expected.SecretId}";
                var numberReference = plan.ProjectNumber is null ? null : $"projects/{plan.ProjectNumber}/secrets/{expected.SecretId}";
                Match(name == expected.SecretEnvironmentVariable &&
                    (reference == expected.SecretId || reference == expectedReference || reference == numberReference) &&
                    Text(secret, v1 ? "key" : "version") == expected.SecretVersion);
            }
            else
            {
                var value = Text(item, "value", allowEmpty: true);
                Match(expected.Environment.TryGetValue(name, out var declared) && declared == value);
            }
        }
        Match(secrets == 1 && values.Count == expected.Environment.Count + 1 && expected.Environment.Keys.All(values.ContainsKey));
    }

    private static void CheckNetwork(JsonElement interfaces, GcpCloudRunDeploymentPlan plan, GcpCloudRunMigrationParity expected)
    {
        if (interfaces.ValueKind != JsonValueKind.Array || interfaces.GetArrayLength() != 1) throw Bad("ASDEPLOY175");
        var item = interfaces[0];
        Only(item, "network", "subnetwork");
        Match(NetworkReference(Text(item, "network"), plan, expected.Network, regional: false) == expected.Network);
        Match(NetworkReference(Text(item, "subnetwork"), plan, expected.Subnetwork, regional: true) == expected.Subnetwork);
    }

    private static string NetworkReference(string observed, GcpCloudRunDeploymentPlan plan, string expected, bool regional)
    {
        if (observed.StartsWith("projects/", StringComparison.Ordinal)) return CanonicalProjectPrefix(observed, plan);
        if (!Regex.IsMatch(observed, "^[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)) throw Bad("ASDEPLOY174");
        var prefix = regional ? $"projects/{plan.Project}/regions/{plan.Region}/subnetworks/" : $"projects/{plan.Project}/global/networks/";
        // A bare name cannot identify a Shared VPC or another region/project.
        if (!expected.StartsWith(prefix, StringComparison.Ordinal) || plan.ProjectNumber is null) throw Bad("ASDEPLOY175");
        return prefix + observed;
    }

    private static string CanonicalProjectPrefix(string reference, GcpCloudRunDeploymentPlan plan)
    {
        var numberPrefix = $"projects/{plan.ProjectNumber}/";
        return plan.ProjectNumber is not null && reference.StartsWith(numberPrefix, StringComparison.Ordinal)
            ? $"projects/{plan.Project}/" + reference[numberPrefix.Length..] : reference;
    }

    private static void CheckResourceDefaults(JsonElement container)
    {
        if (!container.TryGetProperty("resources", out var resources)) return;
        Only(resources, "limits");
        var limits = Object(resources, "limits");
        Only(limits, "cpu", "memory");
        // Rendering has no configurable resource limits: only documented provider defaults are admitted.
        var cpu = Text(limits, "cpu");
        if (cpu is not ("1" or "1000m") || Text(limits, "memory") != "512Mi") throw Bad("ASDEPLOY175");
    }

    private static CloudRunExecutionState ClassifyState(JsonElement status, JsonElement spec, bool v1)
    {
        var conditions = Array(status, "conditions");
        var types = new HashSet<string>(StringComparer.Ordinal);
        JsonElement overall = default;
        foreach (var condition in conditions.EnumerateArray())
        {
            var type = Text(condition, "type");
            if (!types.Add(type)) throw Bad("ASDEPLOY174");
            var value = Text(condition, v1 ? "status" : "state");
            if (v1 ? value is not ("True" or "False" or "Unknown") : value is not ("CONDITION_SUCCEEDED" or "CONDITION_FAILED" or "CONDITION_PENDING" or "CONDITION_RECONCILING")) throw Bad("ASDEPLOY175");
            if (type == "Completed") overall = condition;
        }
        if (overall.ValueKind == JsonValueKind.Undefined) throw Bad("ASDEPLOY174");
        var state = Text(overall, v1 ? "status" : "state");
        var count = Number(spec, "taskCount", true);
        var running = Counter(status, "runningCount", v1);
        var succeeded = Counter(status, "succeededCount", v1);
        var failed = Counter(status, "failedCount", v1);
        var cancelled = Counter(status, "cancelledCount", v1);
        var retried = Counter(status, "retriedCount", v1);
        if (running + succeeded + failed + cancelled > count || retried > count) throw Bad("ASDEPLOY174");
        var terminal = state is "True" or "False" or "CONDITION_SUCCEEDED" or "CONDITION_FAILED";
        var hasCompletion = status.TryGetProperty("completionTime", out var completion);
        string? reason = null;
        string? reasonField = null;
        foreach (var field in v1 ? new[] { "reason" } : new[] { "reason", "executionReason", "revisionReason", "instanceReason" })
        {
            if (!overall.TryGetProperty(field, out _)) continue;
            if (reasonField is not null) throw Bad("ASDEPLOY174");
            reasonField = field;
            reason = Text(overall, field);
        }
        if (!v1 && reasonField is "revisionReason" or "instanceReason") throw Bad("ASDEPLOY175");
        var reconciling = !v1 && status.TryGetProperty("reconciling", out var rec) && rec.ValueKind == JsonValueKind.True;
        if (!v1 && (!status.TryGetProperty("reconciling", out rec) || rec.ValueKind is not (JsonValueKind.False or JsonValueKind.True))) throw Bad("ASDEPLOY175");
        if (terminal)
        {
            if (!hasCompletion || completion.ValueKind != JsonValueKind.String || !ValidTimestamp(completion.GetString()!) || reconciling || running != 0 || succeeded + failed + cancelled != count) throw Bad("ASDEPLOY174");
            if (state is "True" or "CONDITION_SUCCEEDED")
            {
                if (succeeded != count || failed != 0 || cancelled != 0) throw Bad("ASDEPLOY174");
                if (reason is not null && (!v1 || reason != "Completed")) throw Bad("ASDEPLOY175");
                return CloudRunExecutionState.Succeeded;
            }
            if (v1 ? reason is "Cancelled" or "ExecutionCancelled" or "CANCELLED" : reasonField == "executionReason" && reason == "CANCELLED")
            {
                if (cancelled == 0) throw Bad("ASDEPLOY174");
                return CloudRunExecutionState.Cancelled;
            }
            if (failed > 0 && cancelled == 0 && (v1 ? reason is "NonZeroExitCode" or "NON_ZERO_EXIT_CODE" : reasonField == "executionReason" && reason == "NON_ZERO_EXIT_CODE")) return CloudRunExecutionState.Failed;
            throw Bad("ASDEPLOY175");
        }
        if (hasCompletion || succeeded == count || reason is "Cancelled" or "CANCELLED" or "DELETED" or "JOB_STATUS_SERVICE_POLLING_ERROR") throw Bad("ASDEPLOY174");
        if (v1 && reason is not ("Running" or "Pending" or "ResourcesAvailable" or "Retrying" or "CANCELLING" or "CANCELLED" or "Starting")) throw Bad("ASDEPLOY175");
        if (!v1 && reasonField is not null && !(reasonField == "executionReason" && reason is "CANCELLING" or "DELAYED_START_PENDING")
            && !(reasonField == "reason" && reason is "WAITING_FOR_OPERATION" or "IMMEDIATE_RETRY" or "POSTPONED_RETRY")) throw Bad("ASDEPLOY175");
        return CloudRunExecutionState.Running;
    }

    private static long Counter(JsonElement parent, string name, bool allowOmitted) => parent.TryGetProperty(name, out _) ? Number(parent, name) : allowOmitted ? 0 : throw Bad("ASDEPLOY175");

    private static bool ValidTimestamp(string value) => Regex.IsMatch(value,
        "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]{1,9})?(?:Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant)
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);

    private static long Number(JsonElement parent, string name, bool positive = false, long maximum = int.MaxValue)
    {
        var value = parent.GetProperty(name);
        long number;
        if (value.ValueKind == JsonValueKind.Number) number = value.GetInt64();
        else if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) number = parsed;
        else throw Bad("ASDEPLOY174");
        if (number < (positive ? 1 : 0) || number > maximum) throw Bad("ASDEPLOY174");
        return number;
    }

    private static TimeSpan Duration(string value)
    {
        if (!value.EndsWith('s') || !decimal.TryParse(value[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0 || seconds > 604800) throw Bad("ASDEPLOY174");
        return TimeSpan.FromTicks(decimal.ToInt64(seconds * TimeSpan.TicksPerSecond));
    }

    private static string Text(JsonElement parent, string name, bool allowEmpty = false)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String) throw Bad("ASDEPLOY174");
        var text = value.GetString()!;
        if (!allowEmpty && string.IsNullOrEmpty(text)) throw Bad("ASDEPLOY174");
        return text;
    }

    private static JsonElement Object(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Object) throw Bad("ASDEPLOY174");
        return value;
    }

    private static JsonElement Array(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array) throw Bad("ASDEPLOY174");
        return value;
    }

    private static string[] Strings(JsonElement parent, string name, bool optional)
    {
        if (!parent.TryGetProperty(name, out _) && optional) return [];
        return Array(parent, name).EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : throw Bad("ASDEPLOY174")).ToArray();
    }

    private static void EmptyInventory(JsonElement parent, string name)
    {
        if (parent.TryGetProperty(name, out var value) && (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 0)) throw Bad("ASDEPLOY175");
    }

    private static void Only(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Bad("ASDEPLOY174");
        foreach (var property in value.EnumerateObject()) if (!names.Contains(property.Name, StringComparer.Ordinal)) throw Bad("ASDEPLOY175");
    }

    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateObject())
            {
                if (!names.Add(item.Name)) throw Bad("ASDEPLOY174");
                Unique(item.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) Unique(item);
        }
    }

    private static void Match(bool matched) { if (!matched) throw Bad("ASDEPLOY177"); }
    private static EvidenceException Bad(string code) => new(code);
    private sealed class EvidenceException(string code) : Exception
    {
        internal string Code { get; } = code;
    }
}
