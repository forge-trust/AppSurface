using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Pure closed-protocol controls; no transport, admission, child execution or native proof is synthesized.</summary>
public sealed class EvidenceLinuxApplicationProtocolTests
{
    private const string Canary = "application-protocol-canary-779";
    private const string Lease = "0123456789abcdef0123456789abcdef";
    private const long GiB = 1024L * 1024 * 1024;

    [Theory]
    [InlineData("ASEVD402")]
    [InlineData("ASEVD410")]
    public void ApplicationRequestPreservesAdmissionFailuresDespiteInvalidOperationInheritance(string code)
    {
        var failure = new EvidenceAdmissionException(code, "Protected channel failure.");
        Assert.IsAssignableFrom<InvalidOperationException>(failure);
        Assert.Same(failure, EvidenceLinuxWorkerSupervisor.NormalizeApplicationRequestFailure(failure));
    }

    [Fact]
    public void ApplicationRequestNormalizesBrokerOutputRejectionWithoutRetainingSuppliedValues()
    {
        var failure = EvidenceLinuxWorkerSupervisor.NormalizeApplicationRequestFailure(
            new EvidenceAdmissionException("ASEVD420", Canary));
        Assert.Equal("ASEVD410", failure.Code);
        Assert.DoesNotContain(Canary, failure.Message);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public void ApplicationRequestNormalizesUnclassifiedInvalidOperationWithoutAnAdmissionCode()
    {
        var failure = EvidenceLinuxWorkerSupervisor.NormalizeApplicationRequestFailure(new InvalidOperationException(Canary));
        Assert.Equal("ASEVD410", failure.Code);
        Assert.DoesNotContain(Canary, failure.Message);
        Assert.Null(failure.InnerException);
    }

    [Theory]
    [InlineData("resources")]
    [InlineData("producers")]
    public void RequiredApplicationDeclarationsCannotBeEmpty(string name)
    {
        var json = DescriptorJson();
        Assert.NotEmpty(Parse(json).Resources);
        Assert.NotEmpty(Parse(json).Producers);
        json[name] = new JsonArray();
        RejectDescriptor(json);
    }

    [Fact]
    public void BundleBelowMinimumRejectsBeforeItCanBindAnyApplication()
    {
        var json = DescriptorJson();
        Assert.True(Parse(json).BundleFiles.Count >= 6);
        var shortened = new JsonArray(json["bundle_files"]!.AsArray().Take(5).Select(static item => item!.DeepClone()).ToArray());
        json["bundle_files"] = shortened;
        RejectDescriptor(json);
    }

    [Fact]
    public void CompleteDescriptorPreservesAllDeclarationsAndNineClosedBundleRolesAfterJsonDisposal()
    {
        var json = DescriptorJson();
        var descriptor = Parse(json);
        var startup = EvidenceLinuxWorkerSupervisor.ParseApplicationStartReceipt(Element(StartJson()), descriptor);
        var ready = EvidenceLinuxWorkerSupervisor.ParseApplicationResourceReceipt(Element(ReadyJson()), descriptor, startup, "http");
        Put(json, "producers.0.assertion_ids.0", "changed");
        Put(json, "producers.0.artifact_slots.0.maximum_bytes", 1);
        Put(json, "resources.0.deadline_seconds", 1);
        Put(json, "capabilities.read_only_inputs.0", "changed");
        Put(json, "bundle_files.0.sha256", new string('f', 64));

        Assert.Equal("13.4.4", descriptor.AspireSdkVersion);
        Assert.Equal(9, descriptor.BundleFiles.Select(static item => item.Role).Distinct().Count());
        Assert.Equal("coverage/assertion@1", descriptor.Producers[0].AssertionIds[0]);
        Assert.Equal(1024 * 1024, descriptor.Producers[0].ArtifactSlots[0].MaximumBytes);
        Assert.Equal(90m, descriptor.Producers[0].CoverageGate!.MinLinePercent);
        Assert.Equal(80m, descriptor.Producers[0].CoverageGate!.MinBranchPercent);
        Assert.Equal(95m, descriptor.Producers[0].CoverageGate!.MinPatchLinePercent);
        Assert.Null(descriptor.Producers[0].CoverageGate!.MinPatchBranchPercent);
        Assert.Equal("codecov", descriptor.Producers[0].CoverageGate!.PatchLineMode);
        Assert.Equal(0.5m, descriptor.Producers[0].CoverageGate!.TolerancePercent);
        Assert.Equal(30, descriptor.Resources[0].DeadlineSeconds);
        Assert.Equal("proof-input/declared.txt", descriptor.Capabilities.ReadOnlyInputs[0]);
        Assert.Equal(new string('a', 64), descriptor.BundleFiles[0].Sha256);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)descriptor.Producers[0].RequiredResources)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((IList<EvidenceArtifactSlot>)descriptor.Producers[0].ArtifactSlots)[0] = descriptor.Producers[0].ArtifactSlots[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<EvidenceResourceDeclaration>)descriptor.Resources)[0] = descriptor.Resources[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)descriptor.Capabilities.ReadOnlyInputs)[0] = "changed");
        Assert.Equal(Lease, startup.LeaseId);
        Assert.Equal(321, startup.AppHostPid);
        Assert.Equal(200, ready.HttpStatus);
        Assert.Equal(4096, ready.ReceivedBytes);
    }

    [Fact]
    public void ExactFiniteLimitsAcceptAndFullListsRemainComplete()
    {
        var json = DescriptorJson();
        var resources = new JsonArray();
        for (var i = 0; i < 16; i++)
        {
            var resource = json["resources"]![0]!.DeepClone();
            resource["id"] = "resource-" + i;
            resource["deadline_seconds"] = 120;
            resources.Add(resource);
        }
        json["resources"] = resources;
        var producers = new JsonArray();
        for (var i = 0; i < 32; i++)
        {
            var producer = json["producers"]![0]!.DeepClone();
            producer["id"] = "producer-" + i;
            producer["timeout_seconds"] = 600;
            producer["required_resources"] = new JsonArray(Enumerable.Range(0, 16).Select(i => (JsonNode?)JsonValue.Create("resource-" + i)).ToArray());
            producers.Add(producer);
        }
        json["producers"] = producers;
        producers[0]!["assertion_ids"] = new JsonArray(Enumerable.Range(0, 128).Select(i => (JsonNode?)JsonValue.Create("coverage/assertion@" + i)).ToArray());
        var slots = new JsonArray();
        for (var i = 0; i < 128; i++)
        {
            var slot = producers[0]!["artifact_slots"]![0]!.DeepClone();
            slot["logical_name"] = "report-" + i; slot["maximum_bytes"] = 256L * 1024 * 1024;
            slots.Add(slot);
        }
        producers[0]!["artifact_slots"] = slots;
        var files = (JsonArray)json["bundle_files"]!;
        for (var i = files.Count; i < 256; i++)
            files.Add(File("dependency/file-" + i, "dependency"));
        for (var i = 0; i < 3; i++) files[i]!["length_bytes"] = 128L * 1024 * 1024;
        files[3]!["length_bytes"] = 128L * 1024 * 1024 - 252;
        Put(json, "capabilities.scratch_bytes", GiB); Put(json, "capabilities.memory_bytes", GiB);
        Put(json, "capabilities.maximum_tasks", 128); Put(json, "capabilities.maximum_output_bytes", 1024 * 1024);
        Put(json, "capabilities.start_seconds", 120); Put(json, "capabilities.stopping_seconds", 30);

        var descriptor = Parse(json);

        Assert.Equal(16, descriptor.Resources.Count);
        Assert.Equal(32, descriptor.Producers.Count);
        Assert.Equal(16, descriptor.Producers[0].RequiredResources.Count);
        Assert.Equal(128, descriptor.Producers[0].AssertionIds.Count);
        Assert.Equal(128, descriptor.Producers[0].ArtifactSlots.Count);
        Assert.Equal(256, descriptor.BundleFiles.Count);
        Assert.Equal(512L * 1024 * 1024, descriptor.BundleFiles.Sum(static item => item.LengthBytes));
        Assert.Equal(GiB, descriptor.Capabilities.MemoryBytes);
        Assert.Equal(GiB, descriptor.Capabilities.ScratchBytes);
        Assert.Equal(128, descriptor.Capabilities.MaximumTasks);
        Assert.Equal(120, descriptor.Capabilities.StartSeconds);
        Assert.Equal(30, descriptor.Capabilities.StoppingSeconds);
    }

    [Fact]
    public void ExactTaskCeilingIsAcceptedAndDefensivelyCaptured()
    {
        var json = DescriptorJson();
        var descriptor = Parse(json);

        Put(json, "capabilities.maximum_tasks", 129);

        Assert.Equal(128, descriptor.Capabilities.MaximumTasks);
    }

    [Fact]
    public void OneTaskAboveCeilingRejectsDescriptorWithFixedDiagnostic()
    {
        var json = DescriptorJson();
        Put(json, "capabilities.maximum_tasks", 129);

        RejectDescriptor(json);
    }

    [Theory]
    [InlineData("application_id", "null")]
    [InlineData("application_version", "number")]
    [InlineData("build_id", "non-ascii")]
    [InlineData("catalogue_digest", "uppercase")]
    [InlineData("entry_digest", "short")]
    [InlineData("aspire_sdk_version", "sdk")]
    [InlineData("resources", "null")]
    [InlineData("resources", "object")]
    [InlineData("resources.0", "null")]
    [InlineData("resources.0.requires", "null")]
    [InlineData("resources.0.requires", "number")]
    [InlineData("resources.0.readiness", "unknown")]
    [InlineData("producers", "null")]
    [InlineData("producers.1", "null")]
    [InlineData("producers.0.kind", "non-ascii")]
    [InlineData("producers.0.version", "null")]
    [InlineData("producers.0.assertion_ids.0", "null")]
    [InlineData("producers.0.artifact_slots.0", "null")]
    [InlineData("producers.0.artifact_slots.0.required", "string-boolean")]
    [InlineData("producers.0.coverage_gate", "number")]
    [InlineData("producers.0.coverage_gate.min_line_percent", "string-number")]
    [InlineData("producers.0.coverage_gate.patch_line_mode", "unknown")]
    [InlineData("bundle_files", "null")]
    [InlineData("bundle_files.8", "null")]
    [InlineData("bundle_files.8.relative_path", "null")]
    [InlineData("bundle_files.0.relative_path", "traversal")]
    [InlineData("bundle_files.0.relative_path", "absolute")]
    [InlineData("bundle_files.0.role", "number")]
    [InlineData("bundle_files.0.role", "unknown")]
    [InlineData("bundle_files.0.sha256", "uppercase")]
    [InlineData("bundle_files.8.relative_path", "wrong-manifest")]
    [InlineData("capabilities", "null")]
    [InlineData("capabilities.read_only_inputs", "null")]
    [InlineData("capabilities.read_only_inputs.0", "null")]
    [InlineData("capabilities.read_only_inputs.0", "undeclared")]
    [InlineData("application_uid", "string-number")]
    [InlineData("results_gid", "negative")]
    [InlineData("build_id", "oversized")]
    public void NullTypeIdentityPathOrClosedValueRejectsWithSafe402(string path, string change)
    {
        var json = DescriptorJson();
        JsonNode? replacement = change switch
        {
            "null" => null,
            "number" => JsonValue.Create(1),
            "object" => new JsonObject(),
            "uppercase" => JsonValue.Create(new string('A', 64)),
            "short" => JsonValue.Create(new string('a', 63)),
            "sdk" => JsonValue.Create("13.4.5"),
            "non-ascii" => JsonValue.Create("café"),
            "string-boolean" => JsonValue.Create("true"),
            "string-number" => JsonValue.Create("1"),
            "negative" => JsonValue.Create(-1),
            "oversized" => JsonValue.Create(new string('a', 1024 * 1024 + 1)),
            "traversal" => JsonValue.Create("../" + Canary),
            "absolute" => JsonValue.Create("/" + Canary),
            "wrong-manifest" => JsonValue.Create("dependency/" + Canary + ".json"),
            _ => JsonValue.Create(Canary),
        };
        PutNode(json, path, replacement);
        RejectDescriptor(json);
    }

    [Theory]
    [InlineData("capabilities.scratch_bytes", 0)]
    [InlineData("capabilities.scratch_bytes", GiB + 1)]
    [InlineData("capabilities.memory_bytes", 0)]
    [InlineData("capabilities.memory_bytes", GiB + 1)]
    [InlineData("capabilities.maximum_tasks", 0)]
    [InlineData("capabilities.maximum_tasks", 129)]
    [InlineData("capabilities.maximum_output_bytes", 0)]
    [InlineData("capabilities.maximum_output_bytes", 1024 * 1024 + 1)]
    [InlineData("capabilities.start_seconds", 0)]
    [InlineData("capabilities.start_seconds", 121)]
    [InlineData("capabilities.stopping_seconds", 0)]
    [InlineData("capabilities.stopping_seconds", 31)]
    [InlineData("resources.0.deadline_seconds", 0)]
    [InlineData("resources.0.deadline_seconds", 121)]
    [InlineData("producers.0.timeout_seconds", 0)]
    [InlineData("producers.0.timeout_seconds", 601)]
    [InlineData("producers.0.artifact_slots.0.maximum_bytes", -1)]
    [InlineData("producers.0.artifact_slots.0.maximum_bytes", 256L * 1024 * 1024 + 1)]
    [InlineData("producers.0.coverage_gate.min_line_percent", -1)]
    [InlineData("producers.0.coverage_gate.min_patch_branch_percent", 101)]
    [InlineData("producers.0.coverage_gate.tolerance_percent", 101)]
    [InlineData("bundle_files.0.length_bytes", 0)]
    [InlineData("bundle_files.0.length_bytes", 128L * 1024 * 1024 + 1)]
    [InlineData("bundle_files.0.mode", 438)]
    [InlineData("bundle_files.0.mode", 365)]
    [InlineData("bundle_files.4.mode", 292)]
    [InlineData("bundle_files.5.mode", 292)]
    [InlineData("bundle_files.8.mode", 365)]
    public void BoundsAndImmutableModesRejectOutsideClosedGrants(string path, long value)
    {
        var json = DescriptorJson(); Put(json, path, value); RejectDescriptor(json);
    }

    [Theory]
    [InlineData("")]
    [InlineData("resources.0")]
    [InlineData("producers.0")]
    [InlineData("producers.0.artifact_slots.0")]
    [InlineData("producers.0.coverage_gate")]
    [InlineData("bundle_files.0")]
    [InlineData("capabilities")]
    public void UnknownFieldsAtEveryObjectDepthReject(string path)
    {
        var json = DescriptorJson(); ((JsonObject)At(json, path))[Canary] = Canary; RejectDescriptor(json);
    }

    [Fact]
    public void MissingDuplicateAndCaseAliasedFieldsRejectInsteadOfSelectingOne()
    {
        var json = DescriptorJson(); json.Remove("application_id"); RejectDescriptor(json);
        foreach (var duplicate in new[] { "application_id", "Application_Id" })
        {
            var raw = DescriptorJson().ToJsonString().Replace("\"application_id\":", "\"" + duplicate + "\":\"" + Canary + "\",\"application_id\":", StringComparison.Ordinal);
            using var document = JsonDocument.Parse(raw);
            SafeError("ASEVD402", () => EvidenceLinuxWorkerSupervisor.ParseApplicationDescriptor(document.RootElement, 1001, 1001, 1002, 1002));
        }
        SafeError("ASEVD402", () => EvidenceLinuxWorkerSupervisor.ParseApplicationDescriptor(default, 1001, 1001, 1002, 1002));
        SafeError("ASEVD402", () => EvidenceLinuxWorkerSupervisor.ParseApplicationDescriptor(Element(JsonValue.Create(Canary)!), 1001, 1001, 1002, 1002));
    }

    [Theory]
    [InlineData("resources")]
    [InlineData("producers")]
    [InlineData("producers.0.required_resources")]
    [InlineData("producers.0.assertion_ids")]
    [InlineData("producers.0.artifact_slots")]
    [InlineData("bundle_files")]
    public void DuplicateDeclarationsAndInventoryReject(string path)
    {
        var json = DescriptorJson(); var array = (JsonArray)At(json, path); array.Add(array[0]!.DeepClone()); RejectDescriptor(json);
    }

    [Theory]
    [InlineData("resources", 17)]
    [InlineData("producers", 33)]
    [InlineData("resources.0.requires", 17)]
    [InlineData("producers.0.required_resources", 17)]
    [InlineData("producers.0.assertion_ids", 129)]
    [InlineData("producers.0.artifact_slots", 129)]
    [InlineData("bundle_files", 257)]
    [InlineData("capabilities.read_only_inputs", 2)]
    public void ArrayCountsRejectBeforeReadingOversizedContents(string path, int count)
    {
        var json = DescriptorJson(); PutNode(json, path, new JsonArray(Enumerable.Repeat<JsonNode?>(null, count).ToArray())); RejectDescriptor(json);
    }

    [Theory]
    [InlineData("case-alias")]
    [InlineData("file-prefix")]
    [InlineData("missing-role")]
    [InlineData("aggregate-bytes")]
    [InlineData("undeclared-resource")]
    [InlineData("self-dependency")]
    [InlineData("empty-input")]
    public void CompleteInventoryAndDependencyRelationsMustBeCoherent(string change)
    {
        var json = DescriptorJson(); var files = (JsonArray)json["bundle_files"]!;
        if (change == "case-alias") files.Add(File("APPHOST/APPHOST.DLL", "dependency"));
        if (change == "file-prefix") files.Add(File("apphost/AppHost.dll/dependency.dat", "dependency"));
        if (change == "missing-role") files.RemoveAt(4);
        if (change == "aggregate-bytes") for (var i = 0; i < 4; i++) files[i]!["length_bytes"] = 128L * 1024 * 1024;
        if (change == "undeclared-resource") Put(json, "producers.0.required_resources.0", Canary);
        if (change == "self-dependency") PutNode(json, "resources.0.requires", new JsonArray("http"));
        if (change == "empty-input") PutNode(json, "capabilities.read_only_inputs", new JsonArray());
        RejectDescriptor(json);
    }

    [Fact]
    public void ThreeUidsAndFiveGidsRequirePositivePairwiseSeparateKernelIdentities()
    {
        for (var i = 0; i < 3; i++)
        {
            var uids = new uint[] { 1001, 1002, 1003 }; uids[i] = 0;
            var json = DescriptorJson(); json["application_uid"] = uids[2];
            SafeError("ASEVD402", () => Parse(json, uids[0], 1001, uids[1], 1002));
        }
        for (var i = 0; i < 3; i++) for (var j = i + 1; j < 3; j++)
        {
            var uids = new uint[] { 1001, 1002, 1003 }; uids[j] = uids[i];
            var json = DescriptorJson(); json["application_uid"] = uids[2];
            SafeError("ASEVD402", () => Parse(json, uids[0], 1001, uids[1], 1002));
        }
        for (var i = 0; i < 5; i++) for (var j = i; j < 5; j++)
        {
            var gids = new uint[] { 1001, 1002, 1003, 2001, 2002 }; gids[j] = i == j ? 0 : gids[i];
            var json = DescriptorJson(); json["application_gid"] = gids[2]; json["results_gid"] = gids[3]; json["resource_access_gid"] = gids[4];
            SafeError("ASEVD402", () => Parse(json, 1001, gids[0], 1002, gids[1]));
        }
    }

    [Theory]
    [InlineData("ok", "false")]
    [InlineData("owned", "false")]
    [InlineData("owned", "string")]
    [InlineData("lease_id", "uppercase")]
    [InlineData("lease_id", "short")]
    [InlineData("apphost_pid", "zero")]
    [InlineData("apphost_pid", "overflow")]
    [InlineData("application_uid", "wrong")]
    [InlineData("application_gid", "wrong")]
    [InlineData("cgroup", "wrong")]
    [InlineData("lease_id", "null")]
    public void StartupAckCannotEstablishOwnershipFromBadIdentityOrUnownedProcess(string field, string change)
    {
        var response = StartJson(); response[field] = AckChange(change, field);
        SafeError("ASEVD410", () => EvidenceLinuxWorkerSupervisor.ParseApplicationStartReceipt(Element(response), Parse(DescriptorJson())));
    }

    [Theory]
    [InlineData("ok", "false")]
    [InlineData("kernel_peer_checked", "false")]
    [InlineData("kernel_peer_checked", "string")]
    [InlineData("healthy", "false")]
    [InlineData("healthy", "string")]
    [InlineData("http_status", "wrong")]
    [InlineData("received_bytes", "negative")]
    [InlineData("received_bytes", "too-large")]
    [InlineData("application_uid", "wrong")]
    [InlineData("lease_id", "wrong")]
    [InlineData("resource_id", "wrong")]
    [InlineData("cgroup", "wrong")]
    [InlineData("cgroup", "null")]
    public void IndependentReadinessRequiresKernelPeerExactLeaseResourceAndBoundedHealthyHttp(string field, string change)
    {
        var descriptor = Parse(DescriptorJson());
        var started = EvidenceLinuxWorkerSupervisor.ParseApplicationStartReceipt(Element(StartJson()), descriptor);
        var response = ReadyJson(); response[field] = AckChange(change, field);
        SafeError("ASEVD410", () => EvidenceLinuxWorkerSupervisor.ParseApplicationResourceReceipt(Element(response), descriptor, started, "http"));
    }

    [Fact]
    public void AckUnknownMissingDuplicatedFieldsAndWrongExpectedLeaseRejectWithoutEcho()
    {
        var descriptor = Parse(DescriptorJson());
        var started = EvidenceLinuxWorkerSupervisor.ParseApplicationStartReceipt(Element(StartJson()), descriptor);
        var start = StartJson(); start[Canary] = Canary;
        SafeError("ASEVD410", () => EvidenceLinuxWorkerSupervisor.ParseApplicationStartReceipt(Element(start), descriptor));
        start = StartJson(); start.Remove("owned");
        SafeError("ASEVD410", () => EvidenceLinuxWorkerSupervisor.ParseApplicationStartReceipt(Element(start), descriptor));
        var ready = ReadyJson(); ready[Canary] = Canary;
        SafeError("ASEVD410", () => EvidenceLinuxWorkerSupervisor.ParseApplicationResourceReceipt(Element(ready), descriptor, started, "http"));
        ready = ReadyJson(); ready.Remove("kernel_peer_checked");
        SafeError("ASEVD410", () => EvidenceLinuxWorkerSupervisor.ParseApplicationResourceReceipt(Element(ready), descriptor, started, "http"));
        SafeError("ASEVD410", () => EvidenceLinuxWorkerSupervisor.ParseApplicationResourceReceipt(Element(ReadyJson()), descriptor, started with { ApplicationUid = 999 }, "http"));
        SafeError("ASEVD410", () => EvidenceLinuxWorkerSupervisor.ParseApplicationResourceReceipt(Element(ReadyJson()), descriptor, started, Canary));
        var duplicated = StartJson().ToJsonString().Replace("\"owned\":true", "\"owned\":false,\"owned\":true", StringComparison.Ordinal);
        using var document = JsonDocument.Parse(duplicated);
        SafeError("ASEVD410", () => EvidenceLinuxWorkerSupervisor.ParseApplicationStartReceipt(document.RootElement, descriptor));
    }

    private static JsonNode? AckChange(string change, string field) => change switch
    {
        "null" => null, "false" => JsonValue.Create(false), "string" => JsonValue.Create("true"),
        "uppercase" => JsonValue.Create(new string('A', 32)), "short" => JsonValue.Create(new string('a', 31)),
        "zero" => JsonValue.Create(0), "overflow" => JsonValue.Create((long)int.MaxValue + 1),
        "negative" => JsonValue.Create(-1), "too-large" => JsonValue.Create(4097),
        "wrong" when field is "application_uid" or "application_gid" or "http_status" => JsonValue.Create(999),
        _ => JsonValue.Create(Canary),
    };

    private static void SafeError(string code, Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action); Assert.Equal(code, error.Code);
        Assert.DoesNotContain(Canary, error.Message, StringComparison.Ordinal); Assert.Null(error.InnerException);
    }
    private static void RejectDescriptor(JsonObject value) => SafeError("ASEVD402", () => Parse(value));
    private static EvidenceLinuxApplicationDescriptor Parse(JsonObject value, uint workerUid = 1001, uint workerGid = 1001,
        uint producerUid = 1002, uint producerGid = 1002) => EvidenceLinuxWorkerSupervisor.ParseApplicationDescriptor(Element(value), workerUid, workerGid, producerUid, producerGid);
    private static JsonElement Element(JsonNode value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString()); return document.RootElement.Clone();
    }
    private static JsonNode At(JsonNode node, string path)
    {
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries)) node = node is JsonArray array ? array[int.Parse(part)]! : node[part]!;
        return node;
    }
    private static void Put<T>(JsonNode root, string path, T value) => PutNode(root, path, JsonSerializer.SerializeToNode(value));
    private static void PutNode(JsonNode root, string path, JsonNode? value)
    {
        var split = path.LastIndexOf('.'); var parent = split < 0 ? root : At(root, path[..split]); var key = path[(split + 1)..];
        if (parent is JsonArray array) array[int.Parse(key)] = value; else parent[key] = value;
    }
    private static JsonObject File(string path, string role, uint mode = 292) => new()
    {
        ["relative_path"] = path, ["role"] = role, ["length_bytes"] = 1, ["sha256"] = new string('a', 64), ["mode"] = mode,
    };
    private static JsonObject DescriptorJson()
    {
        var producer = new JsonObject
        {
            ["id"] = "coverage", ["kind"] = "coverage", ["version"] = "1.0.0", ["required_resources"] = new JsonArray("http"),
            ["assertion_ids"] = new JsonArray("coverage/assertion@1"), ["timeout_seconds"] = 60,
            ["artifact_slots"] = new JsonArray(new JsonObject { ["logical_name"] = "report", ["relative_root"] = "merged", ["media_type"] = "application/xml", ["required"] = true, ["maximum_bytes"] = 1024 * 1024 }),
            ["coverage_gate"] = new JsonObject { ["min_line_percent"] = 90, ["min_branch_percent"] = 80, ["min_patch_line_percent"] = 95,
                ["min_patch_branch_percent"] = null, ["patch_line_mode"] = "codecov", ["tolerance_percent"] = 0.5m },
        };
        var second = producer.DeepClone(); second["id"] = "second"; second["coverage_gate"] = null;
        return new()
        {
            ["application_id"] = "native-http-app", ["application_version"] = "1.0.0", ["build_id"] = "immutable-test-metadata",
            ["catalogue_digest"] = new string('a', 64), ["entry_digest"] = new string('b', 64), ["aspire_sdk_version"] = "13.4.4",
            ["resources"] = new JsonArray(new JsonObject { ["id"] = "http", ["readiness"] = "aspire_health", ["deadline_seconds"] = 30, ["requires"] = new JsonArray() }),
            ["producers"] = new JsonArray(producer, second),
            ["bundle_files"] = new JsonArray(File("apphost/AppHost.dll", "apphost"), File("apphost/AppHost.runtimeconfig.json", "apphost_runtime_configuration"),
                File("resource/Resource.dll", "resource"), File("resource/Resource.runtimeconfig.json", "resource_runtime_configuration"),
                File("dcp/dcp", "dcp", 365), File("dcp/ext/native-extension", "dcp_extension", 365), File("dependency/helper.dll", "dependency"),
                File("proof-input/declared.txt", "declared_input"), File("apphost/AppHost.deps.json", "dependency_manifest")),
            ["capabilities"] = new JsonObject { ["read_only_inputs"] = new JsonArray("proof-input/declared.txt"), ["scratch_bytes"] = 1024 * 1024,
                ["memory_bytes"] = 1024 * 1024, ["maximum_tasks"] = 128, ["maximum_output_bytes"] = 1024 * 1024, ["start_seconds"] = 30, ["stopping_seconds"] = 5 },
            ["application_uid"] = 1003, ["application_gid"] = 1003, ["results_gid"] = 2001, ["resource_access_gid"] = 2002,
        };
    }
    private static JsonObject StartJson() => new()
    {
        ["ok"] = true, ["lease_id"] = Lease, ["apphost_pid"] = 321, ["application_uid"] = 1003, ["application_gid"] = 1003,
        ["cgroup"] = "/system.slice/issue779-app-" + Lease + ".service", ["owned"] = true,
    };
    private static JsonObject ReadyJson() => new()
    {
        ["ok"] = true, ["lease_id"] = Lease, ["resource_id"] = "http", ["application_uid"] = 1003,
        ["cgroup"] = "/system.slice/issue779-app-" + Lease + ".service", ["kernel_peer_checked"] = true,
        ["http_status"] = 200, ["healthy"] = true, ["received_bytes"] = 4096,
    };
}
