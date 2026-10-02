using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Pure descriptor/runtime-comparison controls; no peer transport or admission authority is injected.</summary>
public sealed class EvidenceLinuxWorkerDescriptorTests
{
    private const string Canary = "worker-descriptor-canary-779";
    private const string Control = "/run/closed-control/broker/control.sock";
    private static readonly DateTimeOffset Clock = DateTimeOffset.Parse("2060-01-01T00:00:00Z");

    [Fact]
    public void ActualRootV1FieldsAndNullableOptionsParseWithoutApplicationOrAuthority()
    {
        var json = DescriptorJson();
        var descriptor = Parse(json);
        EvidenceLinuxWorkerSupervisor.ValidateWorkerRuntimeBinding(descriptor, Control, 777, 888, 1001, 1001, Clock);

        Assert.Null(descriptor.Application);
        Assert.Equal(777, descriptor.BrokerPid);
        Assert.Equal("/run/closed-control/worker-control.json", descriptor.DescriptorPath);
        Assert.Null(descriptor.DiffFile);
        Assert.Null(descriptor.DiffSha256);
        Assert.Equal("/run/subject/repository.slnx", descriptor.Solution);
        Assert.Equal("/usr/share/dotnet/dotnet", descriptor.DotnetPath);
        Assert.Empty(descriptor.ProofDigest);

        json.Remove("diff_file"); json.Remove("diff_sha256"); json.Remove("solution");
        var withoutOptions = Parse(json);
        Assert.Null(withoutOptions.Solution);
        Assert.Null(withoutOptions.DiffFile);
    }

    [Fact]
    public void V2ApplicationUsesTheOuterIdentityMapAndSurvivesJsonDisposalAndMutation()
    {
        var json = DescriptorJson(); json["schema"] = "evidence-worker-linux-v2"; json["application"] = ApplicationJson();
        var descriptor = Parse(json);
        Put(json, "paths.0", "changed.cs"); Put(json, "observation_profile_ids.0", "changed");
        Put(json, "application.producers.0.assertion_ids.0", "changed");

        Assert.Equal("evidence-worker-linux-v2", descriptor.Schema);
        Assert.Equal((uint)1003, descriptor.Application!.ApplicationUid);
        Assert.Equal("src/Feature.cs", descriptor.Paths[0]);
        Assert.Equal("coverage", descriptor.ObservationProfileIds[0]);
        Assert.Equal("coverage/assertion@1", descriptor.Application.Producers[0].AssertionIds[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)descriptor.Paths)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((IList<string>)descriptor.ObservationProfileIds)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((IList<string>)descriptor.ObservationProducerIds)[0] = "changed");
    }

    [Fact]
    public void ExactListAndStageLimitsAndProtectedDiffParseAsCompleteMetadata()
    {
        var json = DescriptorJson();
        json["paths"] = Strings(4096, static i => "src/File" + i + ".cs");
        json["observation_profile_ids"] = Strings(32, static i => "profile-" + i);
        json["observation_producer_ids"] = Strings(32, static i => "producer-" + i);
        json["run_id"] = new string('a', 128) + "/" + new string('b', 127);
        json["admission_seconds"] = 30; json["start_seconds"] = 120; json["collection_seconds"] = 60;
        json["cleanup_seconds"] = 600; json["stopping_seconds"] = 30;
        json["diff_file"] = "/opt/protected/input.diff"; json["diff_sha256"] = new string('a', 64);

        var descriptor = Parse(json);

        Assert.Equal(4096, descriptor.Paths.Count);
        Assert.Equal(32, descriptor.ObservationProfileIds.Count);
        Assert.Equal(32, descriptor.ObservationProducerIds.Count);
        Assert.Equal(600, descriptor.CleanupSeconds);
        Assert.Equal(30, descriptor.StoppingSeconds);
        Assert.Equal("/opt/protected/input.diff", descriptor.DiffFile);
    }

    [Theory]
    [InlineData("application-null")]
    [InlineData("application-object")]
    [InlineData("v2-missing")]
    [InlineData("v2-null")]
    [InlineData("v2-array")]
    [InlineData("v2-worker-application-uid")]
    [InlineData("v2-producer-application-uid")]
    [InlineData("v2-worker-application-gid")]
    [InlineData("v2-producer-results-gid")]
    [InlineData("unsupported")]
    public void SchemaAndApplicationCannotDowngradeOrReuseWorkerProducerAuthority(string change)
    {
        var json = DescriptorJson();
        if (change.StartsWith("v2", StringComparison.Ordinal)) json["schema"] = "evidence-worker-linux-v2";
        if (change == "application-null" || change == "v2-null") json["application"] = null;
        if (change == "application-object") json["application"] = ApplicationJson();
        if (change == "v2-array") json["application"] = new JsonArray();
        if (change.EndsWith("uid", StringComparison.Ordinal) || change.EndsWith("gid", StringComparison.Ordinal)) json["application"] = ApplicationJson();
        if (change == "v2-worker-application-uid") Put(json, "application.application_uid", 1001);
        if (change == "v2-producer-application-uid") Put(json, "application.application_uid", 1002);
        if (change == "v2-worker-application-gid") Put(json, "application.application_gid", 1001);
        if (change == "v2-producer-results-gid") Put(json, "application.results_gid", 1002);
        if (change == "unsupported") json["schema"] = "evidence-worker-linux-v3";
        Reject(json);
    }

    [Theory]
    [InlineData("schema", "null")]
    [InlineData("run_id", "null")]
    [InlineData("worker_pid", "string")]
    [InlineData("broker_pid", "null")]
    [InlineData("worker_uid", "string")]
    [InlineData("subject_gid", "boolean")]
    [InlineData("job_deadline_utc", "wrong-date")]
    [InlineData("job_deadline_utc", "number")]
    [InlineData("entry_sha256", "uppercase")]
    [InlineData("policy_sha256", "short-hash")]
    [InlineData("proof_digest", "wrong")]
    [InlineData("output_parent_identity", "null")]
    [InlineData("output_parent_identity.inode", "string")]
    [InlineData("observation_profile_ids", "null")]
    [InlineData("observation_profile_ids", "object")]
    [InlineData("observation_producer_ids.0", "null")]
    [InlineData("paths.0", "null")]
    [InlineData("paths.0", "boolean")]
    [InlineData("tool_root", "null")]
    [InlineData("descriptor_path", "null")]
    [InlineData("mode", "wrong")]
    [InlineData("provider", "wrong")]
    [InlineData("platform", "wrong")]
    [InlineData("base_revision", "control")]
    [InlineData("workflow_identity", "oversized")]
    public void NullTypeUnsupportedOrOversizedFieldsRejectWithFixed402(string path, string change)
    {
        var json = DescriptorJson();
        JsonNode? value = change switch
        {
            "null" => null, "string" => JsonValue.Create("1"), "boolean" => JsonValue.Create(true), "object" => new JsonObject(),
            "number" => JsonValue.Create(1), "uppercase" => JsonValue.Create(new string('A', 64)),
            "short-hash" => JsonValue.Create(new string('a', 63)), "control" => JsonValue.Create(Canary + "\n"),
            "oversized" => JsonValue.Create(new string('a', 257)), _ => JsonValue.Create(Canary),
        };
        PutNode(json, path, value); Reject(json);
    }

    [Theory]
    [InlineData("worker_pid", 0)]
    [InlineData("worker_pid", 2147483648L)]
    [InlineData("broker_pid", 0)]
    [InlineData("broker_pid", 888)]
    [InlineData("worker_uid", 0)]
    [InlineData("worker_gid", 0)]
    [InlineData("subject_uid", 0)]
    [InlineData("subject_uid", 1001)]
    [InlineData("subject_gid", 1001)]
    [InlineData("subject_gid", 4294967296L)]
    [InlineData("output_parent_identity.inode", 0)]
    [InlineData("output_parent_identity.uid", 1002)]
    [InlineData("output_parent_identity.gid", 1002)]
    [InlineData("admission_seconds", 0)]
    [InlineData("admission_seconds", 31)]
    [InlineData("start_seconds", 0)]
    [InlineData("start_seconds", 121)]
    [InlineData("collection_seconds", 0)]
    [InlineData("collection_seconds", 61)]
    [InlineData("cleanup_seconds", 0)]
    [InlineData("cleanup_seconds", 601)]
    [InlineData("stopping_seconds", 0)]
    [InlineData("stopping_seconds", 31)]
    [InlineData("cleanup_seconds", 4)]
    public void IdentityNumbersAndStageBudgetsMustFitTheRootContract(string path, long value)
    {
        var json = DescriptorJson(); Put(json, path, value); Reject(json);
    }

    [Theory]
    [InlineData("run_id", "run-without-attempt")]
    [InlineData("run_id", "run//attempt")]
    [InlineData("unit", "unit.scope")]
    [InlineData("cgroup", "/system.slice/another.service")]
    [InlineData("cgroup", "/user.slice/evidencehost-0123456789ab-worker.service")]
    [InlineData("tool_root", "/opt/protected/../secret")]
    [InlineData("subject_root", "/opt/protected/subject")]
    [InlineData("output_parent", "/opt/protected")]
    [InlineData("output_parent", "/run/subject/results")]
    [InlineData("policy_file", "/opt/protected-alias/policy.json")]
    [InlineData("policy_file", "relative/policy.json")]
    [InlineData("dotnet_path", "dotnet")]
    [InlineData("test_output_root", "/run//test-output")]
    [InlineData("output_slot", "../results")]
    [InlineData("socket_path", "/run/closed-control/control.sock")]
    [InlineData("socket_path", "/run/closed-control/sockets/control.sock")]
    [InlineData("descriptor_path", "/run/closed-control/other.json")]
    [InlineData("descriptor_path", "/run/other/worker-control.json")]
    [InlineData("solution", "/opt/protected/subject.slnx")]
    [InlineData("paths.0", "../secret.cs")]
    [InlineData("paths.0", "/src/Feature.cs")]
    [InlineData("paths.0", "src\\Feature.cs")]
    [InlineData("observation_profile_ids.0", "../coverage")]
    public void NormalizedRootContainmentAndRunUnitControlIdentityRejectDrift(string path, string value)
    {
        var json = DescriptorJson(); Put(json, path, value); Reject(json);
    }

    [Fact]
    public void DescriptorControlRootCannotOverlapProtectedSubjectOrOutputRoots()
    {
        foreach (var path in new[] { "tool_root", "subject_root", "output_parent" })
        {
            var json = DescriptorJson(); json["descriptor_path"] = "/private/control/worker-control.json";
            json["socket_path"] = "/private/control/broker/control.sock"; json[path] = "/private/control/payload";
            if (path == "tool_root") json["policy_file"] = "/private/control/payload/policy.json";
            if (path == "subject_root") json["solution"] = "/private/control/payload/repository.slnx";
            Reject(json);
        }
    }

    [Fact]
    public void ProtectedDiffMustHaveMatchingHashAndToolContainment()
    {
        var fileWithoutDigest = DescriptorJson(); fileWithoutDigest["diff_file"] = "/opt/protected/input.diff"; Reject(fileWithoutDigest);
        var digestWithoutFile = DescriptorJson(); digestWithoutFile["diff_sha256"] = new string('a', 64); Reject(digestWithoutFile);
        var externalDiff = DescriptorJson(); externalDiff["diff_file"] = "/run/subject/input.diff"; externalDiff["diff_sha256"] = new string('a', 64); Reject(externalDiff);
    }

    [Theory]
    [InlineData("paths", 4097)]
    [InlineData("observation_profile_ids", 33)]
    [InlineData("observation_producer_ids", 33)]
    public void ListLimitsRejectBeforeReadingOversizedContents(string path, int count)
    {
        var json = DescriptorJson(); json[path] = new JsonArray(Enumerable.Repeat<JsonNode?>(null, count).ToArray()); Reject(json);
    }

    [Fact]
    public void DuplicateListsAndOversizedPathBytesAreRejected()
    {
        foreach (var name in new[] { "paths", "observation_profile_ids", "observation_producer_ids" })
        {
            var json = DescriptorJson(); var array = (JsonArray)json[name]!; array.Add(array[0]!.DeepClone()); Reject(json);
        }
        var largePath = DescriptorJson(); Put(largePath, "paths.0", new string('a', 4096)); Reject(largePath);
        var multibytePath = DescriptorJson(); Put(multibytePath, "paths.0", new string('é', 2048)); Reject(multibytePath);
    }

    [Fact]
    public void MissingBrokerDescriptorExtrasUnknownAndDuplicateFieldsCannotBeIgnored()
    {
        foreach (var name in new[] { "broker_pid", "descriptor_path" })
        {
            var json = DescriptorJson(); json.Remove(name); Reject(json);
        }
        var unknown = DescriptorJson(); unknown[Canary] = Canary; Reject(unknown);
        unknown = DescriptorJson(); unknown["output_parent_identity"]![Canary] = Canary; Reject(unknown);
        foreach (var key in new[] { "worker_pid", "Worker_Pid" })
        {
            var raw = DescriptorJson().ToJsonString().Replace("\"worker_pid\":888", "\"" + key + "\":999,\"worker_pid\":888", StringComparison.Ordinal);
            using var document = JsonDocument.Parse(raw); Reject(() => EvidenceLinuxWorkerSupervisor.ParseWorkerDescriptor(document.RootElement));
        }
        Reject(() => EvidenceLinuxWorkerSupervisor.ParseWorkerDescriptor(default));
    }

    [Theory]
    [InlineData("broker")]
    [InlineData("broker-zero")]
    [InlineData("pid")]
    [InlineData("uid")]
    [InlineData("uid-root")]
    [InlineData("gid")]
    [InlineData("gid-root")]
    [InlineData("socket")]
    [InlineData("exact-expiry")]
    [InlineData("expired")]
    public void ParsedMetadataCannotPassRuntimeBindingWithDifferentPeerWorkerOrDeadline(string change)
    {
        var descriptor = Parse(DescriptorJson());
        var broker = change == "broker" ? 778 : change == "broker-zero" ? 0 : 777;
        var pid = change == "pid" ? 889 : 888;
        uint uid = change == "uid" ? 1002u : change == "uid-root" ? 0u : 1001u;
        uint gid = change == "gid" ? 1002u : change == "gid-root" ? 0u : 1001u;
        var socket = change == "socket" ? "/run/other/broker/control.sock" : Control;
        var now = change == "exact-expiry" ? descriptor.JobDeadlineUtc : change == "expired" ? descriptor.JobDeadlineUtc.AddTicks(1) : Clock;
        Reject(() => EvidenceLinuxWorkerSupervisor.ValidateWorkerRuntimeBinding(descriptor, socket, broker, pid, uid, gid, now));
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("too-large")]
    [InlineData("string")]
    [InlineData("null")]
    [InlineData("overflow")]
    [InlineData("sub-tick")]
    [InlineData("false")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("null-descriptor")]
    public void ReadyAllowanceAndWrapperFailClosedWithSafe402(string change)
    {
        var response = new JsonObject { ["ok"] = true, ["descriptor"] = DescriptorJson(), ["job_remaining_seconds"] = 600.0 };
        if (change == "zero") response["job_remaining_seconds"] = 0;
        if (change == "negative") response["job_remaining_seconds"] = -1;
        if (change == "too-large") response["job_remaining_seconds"] = 3600.1;
        if (change == "string") response["job_remaining_seconds"] = Canary;
        if (change == "null") response["job_remaining_seconds"] = null;
        if (change == "false") response["ok"] = false;
        if (change == "unknown") response[Canary] = Canary;
        if (change == "missing") response.Remove("job_remaining_seconds");
        if (change == "null-descriptor") response["descriptor"] = null;
        var raw = response.ToJsonString();
        if (change == "overflow") raw = raw.Replace("600", "1e9999", StringComparison.Ordinal);
        if (change == "sub-tick") raw = raw.Replace("600", "1e-300", StringComparison.Ordinal);
        using var document = JsonDocument.Parse(raw);
        Reject(() => EvidenceLinuxWorkerSupervisor.ParseWorkerRemainingAllowance(document.RootElement));
    }

    [Fact]
    public void RemainingAllowancePreservesFractionAndOneHourCeilingWithoutCreatingSupervisor()
    {
        foreach (var seconds in new[] { 0.125, 3600.0 })
        {
            var response = new JsonObject { ["ok"] = true, ["descriptor"] = DescriptorJson(), ["job_remaining_seconds"] = seconds };
            Assert.Equal(TimeSpan.FromSeconds(seconds), EvidenceLinuxWorkerSupervisor.ParseWorkerRemainingAllowance(Element(response)));
        }
        var raw = new JsonObject { ["ok"] = true, ["descriptor"] = DescriptorJson(), ["job_remaining_seconds"] = 1 }.ToJsonString()
            .Replace("\"ok\":true", "\"ok\":false,\"ok\":true", StringComparison.Ordinal);
        using var document = JsonDocument.Parse(raw); Reject(() => EvidenceLinuxWorkerSupervisor.ParseWorkerRemainingAllowance(document.RootElement));
    }

    private static void Reject(JsonObject json) => Reject(() => Parse(json));
    private static void Reject(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD402", error.Code); Assert.DoesNotContain(Canary, error.Message, StringComparison.Ordinal); Assert.Null(error.InnerException);
    }
    private static EvidenceLinuxWorkerDescriptor Parse(JsonObject json) => EvidenceLinuxWorkerSupervisor.ParseWorkerDescriptor(Element(json));
    private static JsonElement Element(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString()); return document.RootElement.Clone();
    }
    private static JsonArray Strings(int count, Func<int, string> value) => new(Enumerable.Range(0, count).Select(i => (JsonNode?)JsonValue.Create(value(i))).ToArray());
    private static void Put<T>(JsonNode node, string path, T value) => PutNode(node, path, JsonSerializer.SerializeToNode(value));
    private static void PutNode(JsonNode node, string path, JsonNode? value)
    {
        var parts = path.Split('.');
        foreach (var part in parts[..^1]) node = node is JsonArray array ? array[int.Parse(part)]! : node[part]!;
        if (node is JsonArray items) items[int.Parse(parts[^1])] = value; else node[parts[^1]] = value;
    }
    private static JsonObject DescriptorJson() => (JsonObject)JsonNode.Parse("""
        {
          "schema":"evidence-worker-linux-v1", "run_id":"fixture-run/1", "worker_pid":888, "broker_pid":777,
          "worker_uid":1001, "worker_gid":1001, "subject_uid":1002, "subject_gid":1002,
          "unit":"evidencehost-0123456789ab-worker.service", "cgroup":"/system.slice/evidencehost-0123456789ab-worker.service",
          "job_deadline_utc":"2099-01-01T00:00:00Z", "tool_root":"/opt/protected", "subject_root":"/run/subject",
          "output_parent":"/run/output", "output_slot":"fresh-output", "dotnet_path":"/usr/share/dotnet/dotnet",
          "test_output_root":"/run/test-output", "policy_file":"/opt/protected/policy.json", "mode":"observation",
          "socket_path":"/run/closed-control/broker/control.sock", "descriptor_path":"/run/closed-control/worker-control.json",
          "entry_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "base_revision":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "subject_revision":"cccccccccccccccccccccccccccccccccccccccc",
          "workflow_identity":"fixture:issue779:descriptor-controls", "provider":"github-actions", "platform":"linux-x64",
          "proof_digest":"", "policy_sha256":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
          "output_parent_identity":{"device_major":0,"device_minor":16,"inode":123,"uid":1001,"gid":1001},
          "observation_profile_ids":["coverage"], "observation_producer_ids":["coverage"], "paths":["src/Feature.cs"],
          "admission_seconds":10, "start_seconds":10, "collection_seconds":20, "cleanup_seconds":30, "stopping_seconds":5,
          "diff_file":null, "diff_sha256":null, "solution":"/run/subject/repository.slnx"
        }
        """)!;
    private static JsonObject ApplicationJson() => (JsonObject)JsonNode.Parse("""
        {
          "application_id":"native-http-app", "application_version":"1.0.0", "build_id":"immutable-compiled-build",
          "catalogue_digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "entry_digest":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "aspire_sdk_version":"13.4.4",
          "resources":[{"id":"http","readiness":"aspire_health","deadline_seconds":30,"requires":[]}],
          "producers":[{"id":"coverage","kind":"coverage","version":"1.0.0","required_resources":["http"],
            "assertion_ids":["coverage/assertion@1"], "artifact_slots":[{"logical_name":"report","relative_root":"merged","media_type":"application/xml","required":true,"maximum_bytes":1048576}],
            "timeout_seconds":60, "coverage_gate":{"min_line_percent":90,"min_branch_percent":80,"min_patch_line_percent":null,"min_patch_branch_percent":null,"patch_line_mode":"measurable","tolerance_percent":0.5}}],
          "bundle_files":[
            {"relative_path":"apphost/AppHost.dll","role":"apphost","length_bytes":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","mode":292},
            {"relative_path":"apphost/AppHost.runtimeconfig.json","role":"apphost_runtime_configuration","length_bytes":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","mode":292},
            {"relative_path":"resource/Resource.dll","role":"resource","length_bytes":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","mode":292},
            {"relative_path":"resource/Resource.runtimeconfig.json","role":"resource_runtime_configuration","length_bytes":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","mode":292},
            {"relative_path":"dcp/dcp","role":"dcp","length_bytes":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","mode":365},
            {"relative_path":"proof-input/declared.txt","role":"declared_input","length_bytes":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","mode":292}],
          "capabilities":{"read_only_inputs":["proof-input/declared.txt"],"scratch_bytes":1048576,"memory_bytes":1048576,"maximum_tasks":64,"maximum_output_bytes":1048576,"start_seconds":30,"stopping_seconds":5},
          "application_uid":1003,"application_gid":1003,"results_gid":2001,"resource_access_gid":2002
        }
        """)!;
}
