using System.Text;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Deployment.GcpCloudRun;

public sealed partial class CloudRunExecutionObserverTests
{
    [Theory]
    [InlineData("projects/demo-project/secrets/database")]
    [InlineData("projects/123456789012/secrets/database")]
    public async Task V1SecretAliasFullProjectReferencesMatch(string reference)
    {
        // protects=secret reference normalization for declared ID and number; fails_when=valid owning-project references are rejected; no_claim=secret material equality.
        using var fixture = new ObserverFixture();
        var root = JsonNode.Parse(fixture.ExecutionJson())!.AsObject();
        root["spec"]!["template"]!["spec"]!["containers"]![0]!["env"]![0]!["valueFrom"]!["secretKeyRef"]!["name"] = reference;
        var result = await Observe(fixture, root.ToJsonString());
        Assert.Equal(CloudRunExecutionState.Succeeded, result.State);
    }

    [Theory]
    [InlineData("projects/other-project/secrets/database")]
    [InlineData("")]
    public async Task V1SecretAliasWrongOrEmptyReferenceIsRejected(string reference)
    {
        // protects=secret ownership boundary; fails_when=foreign or empty references match; no_claim=alias lookup proves ownership.
        using var fixture = new ObserverFixture();
        var root = JsonNode.Parse(fixture.ExecutionJson())!.AsObject();
        root["spec"]!["template"]!["spec"]!["containers"]![0]!["env"]![0]!["valueFrom"]!["secretKeyRef"]!["name"] = reference;
        var result = await Observe(fixture, root.ToJsonString());
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
    }

    [Fact]
    public async Task V1NetworkBareNamesMatchDeclaredProjectAndRegion()
    {
        // protects=bare VPC expansion only within declared workload scope; fails_when=bare names are treated as globally unique; no_claim=shared-VPC ownership.
        using var fixture = new ObserverFixture();
        var root = JsonNode.Parse(fixture.ExecutionJson())!.AsObject();
        root["metadata"]!["annotations"]!["run.googleapis.com/network-interfaces"] = "[{\"network\":\"main\",\"subnetwork\":\"main\"}]";
        var result = await Observe(fixture, root.ToJsonString());
        Assert.Equal(CloudRunExecutionState.Succeeded, result.State);
    }

    [Theory]
    [InlineData("projects/other-project/global/networks/main", "projects/demo-project/regions/us-central1/subnetworks/main")]
    [InlineData("projects/demo-project/global/networks/main", "projects/demo-project/regions/europe-west1/subnetworks/main")]
    public async Task V1NetworkForeignProjectOrRegionIsRejected(string network, string subnetwork)
    {
        // protects=network project/region identity; fails_when=cross-project or cross-region VPC evidence passes; no_claim=network reachability.
        using var fixture = new ObserverFixture();
        var root = JsonNode.Parse(fixture.ExecutionJson())!.AsObject();
        root["metadata"]!["annotations"]!["run.googleapis.com/network-interfaces"] = $"[{{\"network\":\"{network}\",\"subnetwork\":\"{subnetwork}\"}}]";
        var result = await Observe(fixture, root.ToJsonString());
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
    }

    [Theory]
    [InlineData("1", "512Mi")]
    [InlineData("1000m", "512Mi")]
    public async Task V1DocumentedResourceDefaultsAreAccepted(string cpu, string memory)
    {
        // protects=admitted provider resource defaults; fails_when=documented equivalent spellings are rejected; no_claim=resource capacity or scheduling.
        using var fixture = new ObserverFixture();
        var root = JsonNode.Parse(fixture.ExecutionJson())!.AsObject();
        root["spec"]!["template"]!["spec"]!["containers"]![0]!["resources"] = new JsonObject { ["limits"] = new JsonObject { ["cpu"] = cpu, ["memory"] = memory } };
        var result = await Observe(fixture, root.ToJsonString());
        Assert.Equal(CloudRunExecutionState.Succeeded, result.State);
    }

    [Fact]
    public async Task V1UnsupportedResourceDefaultIsRejected()
    {
        // protects=closed resource default set; fails_when=unconfigured capacity is accepted; no_claim=provider resource behavior.
        using var fixture = new ObserverFixture();
        var root = JsonNode.Parse(fixture.ExecutionJson())!.AsObject();
        root["spec"]!["template"]!["spec"]!["containers"]![0]!["resources"] = new JsonObject { ["limits"] = new JsonObject { ["cpu"] = "2", ["memory"] = "512Mi" } };
        var result = await Observe(fixture, root.ToJsonString());
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
    }

    [Fact]
    public async Task V1MatchingSelfLinkIsAcceptedAndWrongSelfLinkRejected()
    {
        // protects=selected execution self-link identity; fails_when=another execution's link is accepted; no_claim=resource manager attestation.
        using var fixture = new ObserverFixture();
        var valid = JsonNode.Parse(fixture.ExecutionJson())!.AsObject();
        valid["metadata"]!["selfLink"] = $"/apis/run.googleapis.com/v1/namespaces/123456789012/executions/{fixture.ExecutionName}";
        Assert.Equal(CloudRunExecutionState.Succeeded, (await Observe(fixture, valid.ToJsonString())).State);
        valid["metadata"]!["selfLink"] = "/apis/run.googleapis.com/v1/namespaces/123456789012/executions/other";
        Assert.Equal(CloudRunExecutionState.Unknown, (await Observe(fixture, valid.ToJsonString())).State);
    }

    [Fact]
    public async Task V1SelfLinkWithoutLeadingSlashIsAnAdmittedEquivalentPath()
    {
        // protects=the documented optional self-link spelling; fails_when=the slashless canonical path is rejected;
        // no_claim=self-link text authenticates the provider response.
        using var fixture = new ObserverFixture();
        var root = JsonNode.Parse(fixture.ExecutionJson())!.AsObject();
        root["metadata"]!["selfLink"] = $"apis/run.googleapis.com/v1/namespaces/123456789012/executions/{fixture.ExecutionName}";
        var result = await Observe(fixture, root.ToJsonString());
        Assert.Equal(CloudRunExecutionState.Succeeded, result.State);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("empty")]
    [InlineData("job-only")]
    [InlineData("location-only")]
    [InlineData("wrong-location")]
    [InlineData("wrong-job")]
    public async Task V2RequiresBothMatchingReturnedIdentityLabels(string labels)
    {
        // protects=required independent v2 location and parent-Job labels;
        // fails_when=missing, incomplete or contradictory labels permit a matched execution;
        // no_claim=labels prove execution submission provenance.
        using var fixture = new ObserverFixture();
        var root = JsonNode.Parse(fixture.V2ExecutionJson())!.AsObject();
        root["labels"] = labels switch
        {
            "job-only" => new JsonObject { ["run.googleapis.com/job"] = fixture.JobName },
            "location-only" => new JsonObject { ["cloud.googleapis.com/location"] = fixture.RegionName },
            "wrong-location" => new JsonObject { ["cloud.googleapis.com/location"] = "europe-west1", ["run.googleapis.com/job"] = fixture.JobName },
            "wrong-job" => new JsonObject { ["cloud.googleapis.com/location"] = fixture.RegionName, ["run.googleapis.com/job"] = "other-job" },
            _ => new JsonObject()
        };
        if (labels == "absent") root.Remove("labels");
        var result = await Observe(fixture, root.ToJsonString());
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.False(result.ConfigurationMatched);
    }

    [Theory]
    [InlineData("v1", "Succeeded")]
    [InlineData("v2", "Succeeded")]
    public async Task PrivateRangesOnlyEgressMatchesTheDeclaredMode(string format, string expected)
    {
        // protects=normalized private-ranges-only egress across both response forms;
        // fails_when=an admitted mode spelling is rejected or mistaken for all traffic;
        // no_claim=effective routing policy or packet reachability.
        using var fixture = new ObserverFixture(egress: "PRIVATE_RANGES_ONLY");
        var json = format == "v1" ? fixture.ExecutionJson() : fixture.V2ExecutionJson();
        var result = await new GcpCloudRunExecutionObserver(new StubReader(json), TimeProvider.System).ObserveAsync(fixture.Request());
        Assert.Equal(expected == "Succeeded" ? CloudRunExecutionState.Succeeded : CloudRunExecutionState.Unknown, result.State);
        Assert.True(result.ConfigurationMatched);
    }

    [Fact]
    public async Task ProjectNameNamespaceWorksWithoutOptionalProjectNumber()
    {
        // protects=legacy profile identity where project number is absent; fails_when=project-name namespace requires a number;
        // no_claim=provider response authenticity.
        using var fixture = new ObserverFixture(includeProjectNumber: false);
        var result = await Observe(fixture, fixture.ExecutionJson());
        Assert.Equal(CloudRunExecutionState.Succeeded, result.State);
    }

    [Fact]
    public async Task BareNetworkNameIsRejectedWhenProjectNumberIsUnavailable()
    {
        // protects=bare network expansion's project-number prerequisite; fails_when=a bare alias is guessed without scoped identity;
        // no_claim=shared-VPC ownership.
        using var fixture = new ObserverFixture(includeProjectNumber: false);
        var root = JsonNode.Parse(fixture.ExecutionJson())!.AsObject();
        root["metadata"]!["annotations"]!["run.googleapis.com/network-interfaces"] = "[{\"network\":\"main\",\"subnetwork\":\"main\"}]";
        var result = await Observe(fixture, root.ToJsonString());
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal("ASDEPLOY175", result.DiagnosticCode);
    }

    [Fact]
    public async Task V1ArgumentsMayBeOmittedWhenTheDeclarationIsEmpty()
    {
        // protects=optional empty argument-vector semantics; fails_when=an omitted args list is rejected;
        // no_claim=execution behavior outside the captured argument vector.
        using var fixture = new ObserverFixture(emptyArguments: true);
        var root = JsonNode.Parse(fixture.ExecutionJson())!.AsObject();
        root["spec"]!["template"]!["spec"]!["containers"]![0]!.AsObject().Remove("args");
        var result = await Observe(fixture, root.ToJsonString());
        Assert.Equal(CloudRunExecutionState.Succeeded, result.State);
    }

    [Theory]
    [InlineData("project-name-namespace", "ASDEPLOY183")]
    [InlineData("wrong-api-version", "ASDEPLOY175")]
    [InlineData("wrong-namespace", "ASDEPLOY172")]
    [InlineData("wrong-location-label", "ASDEPLOY172")]
    [InlineData("second-container", "ASDEPLOY175")]
    [InlineData("non-string-annotation", "ASDEPLOY175")]
    public async Task V1IdentityAndInventoryEvidenceRequiresOneExactAdmittedShape(string mutation, string diagnostic)
    {
        // protects=Cloud Run execution identity and closed single-container metadata inventory;
        // fails_when=alternate project namespace, mismatched identity, or unsupported inventory is accepted;
        // no_claim=provider response authenticity or workload provenance.
        using var fixture = new ObserverFixture();
        var root = JsonNode.Parse(fixture.ExecutionJson())!.AsObject();
        switch (mutation)
        {
            case "project-name-namespace":
                root["metadata"]!["namespace"] = fixture.ProjectName;
                break;
            case "wrong-api-version":
                root["apiVersion"] = "run.googleapis.com/v2";
                break;
            case "wrong-namespace":
                root["metadata"]!["namespace"] = "foreign-project";
                break;
            case "wrong-location-label":
                root["metadata"]!["labels"]!["cloud.googleapis.com/location"] = "europe-west1";
                break;
            case "second-container":
                root["spec"]!["template"]!["spec"]!["containers"]!.AsArray().Add(new JsonObject { ["image"] = "ignored" });
                break;
            case "non-string-annotation":
                root["metadata"]!["annotations"]!["run.googleapis.com/client-name"] = 42;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        var result = await Observe(fixture, root.ToJsonString());
        Assert.Equal(diagnostic == "ASDEPLOY183" ? CloudRunExecutionState.Succeeded : CloudRunExecutionState.Unknown, result.State);
        Assert.Equal(diagnostic, result.DiagnosticCode);
        Assert.Equal(diagnostic == "ASDEPLOY183", result.ConfigurationMatched);
    }

    [Theory]
    [InlineData("missing", "ASDEPLOY175")]
    [InlineData("null", "ASDEPLOY175")]
    [InlineData("number", "ASDEPLOY175")]
    [InlineData("wrong-value", "ASDEPLOY175")]
    public async Task V2ExecutionEnvironmentRequiresTheDocumentedStringValue(string mutation, string diagnostic)
    {
        // protects=explicit v2 execution-environment evidence; fails_when=missing or malformed fields inherit an undocumented default;
        // no_claim=provider-side normalization or actual machine generation.
        using var fixture = new ObserverFixture();
        var root = JsonNode.Parse(fixture.V2ExecutionJson())!.AsObject();
        switch (mutation)
        {
            case "missing":
                root["template"]!.AsObject().Remove("executionEnvironment");
                break;
            case "null":
                root["template"]!["executionEnvironment"] = JsonNode.Parse("null");
                break;
            case "number":
                root["template"]!["executionEnvironment"] = 2;
                break;
            case "wrong-value":
                root["template"]!["executionEnvironment"] = "EXECUTION_ENVIRONMENT_GEN1";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        var result = await new GcpCloudRunExecutionObserver(new StubReader(root.ToJsonString()), TimeProvider.System).ObserveAsync(fixture.Request());
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal(diagnostic, result.DiagnosticCode);
        Assert.False(result.ConfigurationMatched);
    }

    [Theory]
    [InlineData("extra-volume", "ASDEPLOY175")]
    [InlineData("wrong-mount", "ASDEPLOY177")]
    [InlineData("wrong-sql-instance", "ASDEPLOY177")]
    public async Task V2SqlInventoryMustBindTheDeclaredInstanceToTheMountedVolume(string mutation, string diagnostic)
    {
        // protects=declared Cloud SQL instance and single mounted socket binding;
        // fails_when=extra volume or mismatched mount/instance is treated as equivalent;
        // no_claim=database connectivity or SQL authorization.
        using var fixture = new ObserverFixture();
        var root = JsonNode.Parse(fixture.V2ExecutionJson())!.AsObject();
        switch (mutation)
        {
            case "extra-volume":
                root["template"]!["volumes"]!.AsArray().Add(new JsonObject { ["name"] = "extra", ["cloudSqlInstance"] = new JsonObject { ["instances"] = new JsonArray("other:us-central1:db") } });
                break;
            case "wrong-mount":
                root["template"]!["containers"]![0]!["volumeMounts"]![0]!["name"] = "other";
                break;
            case "wrong-sql-instance":
                root["template"]!["volumes"]![0]!["cloudSqlInstance"]!["instances"]![0] = "other:us-central1:db";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        var result = await new GcpCloudRunExecutionObserver(new StubReader(root.ToJsonString()), TimeProvider.System).ObserveAsync(fixture.Request());
        Assert.Equal(CloudRunExecutionState.Unknown, result.State);
        Assert.Equal(diagnostic, result.DiagnosticCode);
        Assert.False(result.ConfigurationMatched);
    }

    [Theory]
    [InlineData("empty-guid", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    [InlineData("wrong-execution-name", "ASDEPLOY172", CloudRunExecutionState.Unknown)]
    [InlineData("v1-wrong-environment", "ASDEPLOY175", CloudRunExecutionState.Unknown)]
    [InlineData("v1-empty-inventories", "ASDEPLOY183", CloudRunExecutionState.Succeeded)]
    [InlineData("v2-missing-mount", "ASDEPLOY175", CloudRunExecutionState.Unknown)]
    [InlineData("v2-network-null", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    [InlineData("v1-invalid-bare-network", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    [InlineData("deletion-timestamp", "ASDEPLOY176", CloudRunExecutionState.Unknown)]
    [InlineData("duplicate-completed", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    [InlineData("v1-invalid-condition-enum", "ASDEPLOY175", CloudRunExecutionState.Unknown)]
    [InlineData("v2-invalid-condition-enum", "ASDEPLOY175", CloudRunExecutionState.Unknown)]
    [InlineData("v2-terminal-success-counts-disagree", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    [InlineData("v1-cancel-without-cancel-count", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    [InlineData("v2-malformed-timeout", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    [InlineData("v2-wrong-vpc-type", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    [InlineData("v2-env-not-array", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    [InlineData("v2-reconciling-missing", "ASDEPLOY175", CloudRunExecutionState.Unknown)]
    [InlineData("v2-network-project-number-alias", "ASDEPLOY183", CloudRunExecutionState.Succeeded)]
    [InlineData("v2-delete-time", "ASDEPLOY176", CloudRunExecutionState.Unknown)]
    [InlineData("v2-malformed-name", "ASDEPLOY172", CloudRunExecutionState.Unknown)]
    [InlineData("v1-unknown-string-annotation", "ASDEPLOY175", CloudRunExecutionState.Unknown)]
    [InlineData("v1-duplicate-environment", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    [InlineData("v1-image-mismatch", "ASDEPLOY177", CloudRunExecutionState.Unknown)]
    [InlineData("v1-network-empty", "ASDEPLOY175", CloudRunExecutionState.Unknown)]
    [InlineData("v2-network-oversized", "ASDEPLOY175", CloudRunExecutionState.Unknown)]
    public async Task RemainingTypedIdentityInventoryAndStateBranchesAreExplicit(string mutation, string diagnostic, CloudRunExecutionState expected)
    {
        // protects=identity, typed configuration inventory and state/count consistency across admitted response formats;
        // fails_when=alternate malformed evidence or contradictory state is accepted;
        // no_claim=fixture evidence is live-provider certified.
        using var fixture = new ObserverFixture();
        var isV2 = mutation.StartsWith("v2-", StringComparison.Ordinal);
        var root = JsonNode.Parse(isV2 ? fixture.V2ExecutionJson() : fixture.ExecutionJson())!.AsObject();
        switch (mutation)
        {
            case "empty-guid": root["metadata"]!["uid"] = "00000000-0000-0000-0000-000000000000"; break;
            case "wrong-execution-name": root["metadata"]!["name"] = "other"; break;
            case "v1-wrong-environment": root["metadata"]!["annotations"]!["run.googleapis.com/execution-environment"] = "gen1"; break;
            case "v1-empty-inventories":
                root["spec"]!["template"]!["spec"]!["volumes"] = new JsonArray();
                root["spec"]!["template"]!["spec"]!["containers"]![0]!["volumeMounts"] = new JsonArray();
                break;
            case "v2-missing-mount": root["template"]!["containers"]![0]!["volumeMounts"] = new JsonArray(); break;
            case "v2-network-null": root["template"]!["vpcAccess"]!["networkInterfaces"] = null; break;
            case "v1-invalid-bare-network": root["metadata"]!["annotations"]!["run.googleapis.com/network-interfaces"] = "[{\"network\":\"Bad_Name\",\"subnetwork\":\"main\"}]"; break;
            case "deletion-timestamp": root["metadata"]!["deletionTimestamp"] = "2026-10-24T12:00:00Z"; break;
            case "duplicate-completed": root["status"]!["conditions"]!.AsArray().Add(new JsonObject { ["type"] = "Completed", ["status"] = "True" }); break;
            case "v1-invalid-condition-enum": root["status"]!["conditions"]![0]!["status"] = "TRUE"; break;
            case "v2-invalid-condition-enum": root["conditions"]![0]!["state"] = "SUCCEEDED"; break;
            case "v2-terminal-success-counts-disagree":
                root["succeededCount"] = 0;
                root["failedCount"] = 1;
                break;
            case "v1-cancel-without-cancel-count":
                root["status"]!["conditions"]![0]!["status"] = "False";
                root["status"]!["conditions"]![0]!["reason"] = "Cancelled";
                root["status"]!["succeededCount"] = 0;
                root["status"]!["failedCount"] = 1;
                root["status"]!["cancelledCount"] = 0;
                break;
            case "v2-malformed-timeout": root["template"]!["timeout"] = "600xs"; break;
            case "v2-wrong-vpc-type": root["template"]!["vpcAccess"] = "invalid"; break;
            case "v2-env-not-array": root["template"]!["containers"]![0]!["env"] = new JsonObject(); break;
            case "v2-reconciling-missing": root.Remove("reconciling"); break;
            case "v2-network-project-number-alias":
                root["template"]!["vpcAccess"]!["networkInterfaces"]![0]!["network"] = "projects/123456789012/global/networks/main";
                root["template"]!["vpcAccess"]!["networkInterfaces"]![0]!["subnetwork"] = "projects/123456789012/regions/us-central1/subnetworks/main";
                break;
            case "v2-delete-time": root["deleteTime"] = "2026-10-24T12:00:00Z"; break;
            case "v2-malformed-name": root["name"] = "not-a-resource-name"; break;
            case "v1-unknown-string-annotation": root["metadata"]!["annotations"]!["run.googleapis.com/effectful"] = "value"; break;
            case "v1-duplicate-environment":
                root["spec"]!["template"]!["spec"]!["containers"]![0]!["env"]!.AsArray().Add(new JsonObject { ["name"] = "ConnectionStrings__database", ["value"] = "duplicate" });
                break;
            case "v1-image-mismatch": root["spec"]!["template"]!["spec"]!["containers"]![0]!["image"] = "us-docker.pkg.dev/demo-project/jobs/migrations@sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"; break;
            case "v1-network-empty": root["metadata"]!["annotations"]!["run.googleapis.com/network-interfaces"] = "[]"; break;
            case "v2-network-oversized":
                root["template"]!["vpcAccess"]!["networkInterfaces"]!.AsArray().Add(new JsonObject { ["network"] = "projects/demo-project/global/networks/other", ["subnetwork"] = "projects/demo-project/regions/us-central1/subnetworks/other" });
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        var result = await new GcpCloudRunExecutionObserver(new StubReader(root.ToJsonString()), TimeProvider.System).ObserveAsync(fixture.Request());
        Assert.Equal(expected, result.State);
        Assert.Equal(diagnostic, result.DiagnosticCode);
        Assert.Equal(expected != CloudRunExecutionState.Unknown, result.ConfigurationMatched);
    }

    [Theory]
    [InlineData("uid-null", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    [InlineData("generation-zero", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    [InlineData("observed-generation-mismatch", "ASDEPLOY176", CloudRunExecutionState.Unknown)]
    [InlineData("unknown-root-field", "ASDEPLOY175", CloudRunExecutionState.Unknown)]
    [InlineData("limits-array", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    [InlineData("command-number", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    [InlineData("args-null", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    [InlineData("timeout-over-limit", "ASDEPLOY177", CloudRunExecutionState.Unknown)]
    [InlineData("generation-fraction", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    [InlineData("resource-unknown-property", "ASDEPLOY175", CloudRunExecutionState.Unknown)]
    [InlineData("empty-command", "ASDEPLOY177", CloudRunExecutionState.Unknown)]
    [InlineData("environment-source-and-value", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    [InlineData("wrong-service-account", "ASDEPLOY177", CloudRunExecutionState.Unknown)]
    [InlineData("undeclared-environment", "ASDEPLOY177", CloudRunExecutionState.Unknown)]
    [InlineData("egress-mismatch", "ASDEPLOY177", CloudRunExecutionState.Unknown)]
    [InlineData("duplicate-network-key", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    [InlineData("completion-null", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    [InlineData("terminal-counter-mismatch", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    public async Task V1MalformedAndContradictoryEvidenceIsNotPromoted(string mutation, string diagnostic, CloudRunExecutionState expected)
    {
        // protects=typed, bounded and internally consistent v1 execution evidence;
        // fails_when=malformed values, undeclared configuration, or contradictory terminal counters are accepted;
        // no_claim=the fixture represents a live provider response.
        using var fixture = new ObserverFixture();
        var root = JsonNode.Parse(fixture.ExecutionJson())!.AsObject();
        switch (mutation)
        {
            case "uid-null": root["metadata"]!["uid"] = null; break;
            case "generation-zero": root["metadata"]!["generation"] = "0"; break;
            case "observed-generation-mismatch": root["status"]!["observedGeneration"] = "8"; break;
            case "unknown-root-field": root["unexpected"] = true; break;
            case "limits-array": root["spec"]!["template"]!["spec"]!["containers"]![0]!["resources"] = new JsonArray(); break;
            case "command-number": root["spec"]!["template"]!["spec"]!["containers"]![0]!["command"]![0] = 7; break;
            case "args-null": root["spec"]!["template"]!["spec"]!["containers"]![0]!["args"] = null; break;
            case "timeout-over-limit": root["spec"]!["template"]!["spec"]!["timeoutSeconds"] = 604801; break;
            case "generation-fraction": root["metadata"]!["generation"] = 7.5; break;
            case "resource-unknown-property": root["spec"]!["template"]!["spec"]!["containers"]![0]!["resources"] = new JsonObject { ["requests"] = new JsonObject() }; break;
            case "empty-command": root["spec"]!["template"]!["spec"]!["containers"]![0]!["command"] = new JsonArray(); break;
            case "environment-source-and-value": root["spec"]!["template"]!["spec"]!["containers"]![0]!["env"]![0]!["value"] = "literal"; break;
            case "wrong-service-account": root["spec"]!["template"]!["spec"]!["serviceAccountName"] = "other@demo-project.iam.gserviceaccount.com"; break;
            case "undeclared-environment": root["spec"]!["template"]!["spec"]!["containers"]![0]!["env"]!.AsArray().Add(new JsonObject { ["name"] = "UNDECLARED", ["value"] = "x" }); break;
            case "egress-mismatch": root["metadata"]!["annotations"]!["run.googleapis.com/vpc-access-egress"] = "private-ranges-only"; break;
            case "duplicate-network-key": root["metadata"]!["annotations"]!["run.googleapis.com/network-interfaces"] = "[{\"network\":\"projects/demo-project/global/networks/main\",\"network\":\"projects/demo-project/global/networks/main\",\"subnetwork\":\"projects/demo-project/regions/us-central1/subnetworks/main\"}]"; break;
            case "completion-null": root["status"]!["completionTime"] = null; break;
            case "terminal-counter-mismatch": root["status"]!["succeededCount"] = 0; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        var result = await Observe(fixture, root.ToJsonString());
        Assert.Equal(expected, result.State);
        Assert.Equal(diagnostic, result.DiagnosticCode);
        Assert.False(result.ConfigurationMatched);
    }

    [Theory]
    [InlineData("v1-running-starting", "ASDEPLOY182", CloudRunExecutionState.Running)]
    [InlineData("v1-running-pending", "ASDEPLOY182", CloudRunExecutionState.Running)]
    [InlineData("v1-running-resources", "ASDEPLOY182", CloudRunExecutionState.Running)]
    [InlineData("v1-running-retrying", "ASDEPLOY182", CloudRunExecutionState.Running)]
    [InlineData("v1-cancel-alias", "ASDEPLOY185", CloudRunExecutionState.Cancelled)]
    [InlineData("v1-cancel-uppercase", "ASDEPLOY185", CloudRunExecutionState.Cancelled)]
    [InlineData("v1-failure-uppercase", "ASDEPLOY184", CloudRunExecutionState.Failed)]
    [InlineData("v2-reconciling-null", "ASDEPLOY175", CloudRunExecutionState.Unknown)]
    [InlineData("v2-terminal-reconciling", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    [InlineData("v2-nonterminal-has-completion", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    [InlineData("v2-fractional-counter", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    [InlineData("v2-wrong-parent-project-number", "ASDEPLOY172", CloudRunExecutionState.Unknown)]
    [InlineData("v2-wrong-execution-path", "ASDEPLOY172", CloudRunExecutionState.Unknown)]
    [InlineData("v2-present-malformed-annotations", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    [InlineData("v2-zero-generation", "ASDEPLOY174", CloudRunExecutionState.Unknown)]
    [InlineData("v2-running-with-failure-count", "ASDEPLOY182", CloudRunExecutionState.Running)]
    public async Task SupportedStateUnionsAndContradictionsKeepTheirDocumentedClassification(string mutation, string diagnostic, CloudRunExecutionState expected)
    {
        // protects=state reason-field unions and terminal count/completion invariants;
        // fails_when=alternate supported reason spellings are rejected or contradictory state is promoted;
        // no_claim=the synthetic state is provider-certified or proves why a workload failed.
        using var fixture = new ObserverFixture();
        var isV2 = mutation.StartsWith("v2-", StringComparison.Ordinal);
        var root = JsonNode.Parse(isV2 ? fixture.V2ExecutionJson() : mutation.StartsWith("v1-running-", StringComparison.Ordinal) ? fixture.RunningJson() : fixture.ExecutionJson())!.AsObject();
        switch (mutation)
        {
            case "v1-running-starting": root["status"]!["conditions"]![0]!["reason"] = "Starting"; break;
            case "v1-running-pending": root["status"]!["conditions"]![0]!["reason"] = "Pending"; break;
            case "v1-running-resources": root["status"]!["conditions"]![0]!["reason"] = "ResourcesAvailable"; break;
            case "v1-running-retrying": root["status"]!["conditions"]![0]!["reason"] = "Retrying"; break;
            case "v1-cancel-alias": root["status"]!["conditions"]![0]!["reason"] = "ExecutionCancelled"; ToCancelled(root["status"]!.AsObject()); break;
            case "v1-cancel-uppercase": root["status"]!["conditions"]![0]!["reason"] = "CANCELLED"; ToCancelled(root["status"]!.AsObject()); break;
            case "v1-failure-uppercase": root["status"]!["conditions"]![0]!["reason"] = "NON_ZERO_EXIT_CODE"; ToFailed(root["status"]!.AsObject()); break;
            case "v2-reconciling-null": root["reconciling"] = null; break;
            case "v2-terminal-reconciling": root["reconciling"] = true; break;
            case "v2-nonterminal-has-completion": ToV2Running(root); root["completionTime"] = "2026-10-24T12:00:00Z"; break;
            case "v2-fractional-counter": root["succeededCount"] = 0.5; break;
            case "v2-wrong-parent-project-number": root["job"] = "projects/foreign/locations/us-central1/jobs/schema-preparation"; break;
            case "v2-wrong-execution-path": root["name"] = "projects/demo-project/locations/us-central1/jobs/schema-preparation/executions/other"; break;
            case "v2-present-malformed-annotations": root["annotations"] = 5; break;
            case "v2-zero-generation": root["generation"] = 0; root["observedGeneration"] = 0; break;
            case "v2-running-with-failure-count": ToV2Running(root); root["runningCount"] = 0; root["failedCount"] = 1; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        TimeProvider clock = expected == CloudRunExecutionState.Running || isV2 ? new DelayCompletingClock() : TimeProvider.System;
        var result = await new GcpCloudRunExecutionObserver(new StubReader(root.ToJsonString()), clock).ObserveAsync(
            fixture.Request(timeout: TimeSpan.FromSeconds(1), interval: TimeSpan.FromSeconds(1)));
        Assert.Equal(expected, result.State);
        Assert.Equal(diagnostic, result.DiagnosticCode);
        Assert.Equal(expected != CloudRunExecutionState.Unknown, result.ConfigurationMatched);
    }

    private static void ToCancelled(JsonObject status)
    {
        status["conditions"]![0]!["status"] = "False";
        status["succeededCount"] = 0;
        status["cancelledCount"] = 1;
    }

    private static void ToFailed(JsonObject status)
    {
        status["conditions"]![0]!["status"] = "False";
        status["succeededCount"] = 0;
        status["failedCount"] = 1;
    }

    private static void ToV2Running(JsonObject root)
    {
        root["conditions"]![0]!["state"] = "CONDITION_PENDING";
        root.Remove("completionTime");
        root["succeededCount"] = 0;
        root["runningCount"] = 1;
    }

    [Fact]
    public async Task V2MatchingLabelsAreAcceptedAndContradictoryLabelsRejected()
    {
        // protects=optional v2 identity labels; fails_when=contradictory labels are ignored; no_claim=labels are submission provenance.
        using var fixture = new ObserverFixture();
        var valid = JsonNode.Parse(fixture.V2ExecutionJson())!.AsObject();
        valid["labels"] = new JsonObject { ["cloud.googleapis.com/location"] = fixture.RegionName, ["run.googleapis.com/job"] = fixture.JobName };
        Assert.Equal(CloudRunExecutionState.Succeeded, (await Observe(fixture, valid.ToJsonString())).State);
        valid["labels"]!["run.googleapis.com/job"] = "other-job";
        Assert.Equal(CloudRunExecutionState.Unknown, (await Observe(fixture, valid.ToJsonString())).State);
    }

    [Fact]
    public async Task V2BenignClientAnnotationIsAcceptedButEffectfulAnnotationIsRejected()
    {
        // protects=v2 annotation allowlist; fails_when=execution-affecting annotations are ignored; no_claim=metadata proves configuration.
        using var fixture = new ObserverFixture();
        var valid = JsonNode.Parse(fixture.V2ExecutionJson())!.AsObject();
        valid["annotations"] = new JsonObject { ["run.googleapis.com/client-version"] = "576.0.0" };
        Assert.Equal(CloudRunExecutionState.Succeeded, (await Observe(fixture, valid.ToJsonString())).State);
        valid["annotations"]!["run.googleapis.com/cloudsql-instances"] = "other:us-central1:db";
        Assert.Equal(CloudRunExecutionState.Unknown, (await Observe(fixture, valid.ToJsonString())).State);
    }

    [Theory]
    [InlineData("alias:projects/demo-project/secrets/database", "alias", CloudRunExecutionState.Succeeded)]
    [InlineData("alias:projects/123456789012/secrets/database", "alias", CloudRunExecutionState.Succeeded)]
    [InlineData("alias:projects/other-project/secrets/database", "alias", CloudRunExecutionState.Unknown)]
    [InlineData("alias:projects/demo-project/secrets/database,alias:projects/demo-project/secrets/database", "alias", CloudRunExecutionState.Unknown)]
    [InlineData("malformed", "alias", CloudRunExecutionState.Unknown)]
    [InlineData("alias:projects/demo-project/secrets/database", "unresolved", CloudRunExecutionState.Unknown)]
    [InlineData("alias:projects/demo-project/secrets/database,extra:projects/demo-project/secrets/other", "alias", CloudRunExecutionState.Unknown)]
    public async Task V1SecretAliasRequiresOneExactResolvedBinding(string aliases, string reference, CloudRunExecutionState expected)
    {
        // protects=execution-local secret alias resolution; fails_when=ambiguous, foreign or unresolved aliases match;
        // no_claim=latest secret material or IAM effectiveness is proved.
        using var fixture = new ObserverFixture();
        var root = JsonNode.Parse(fixture.ExecutionJson())!.AsObject();
        root["metadata"]!["annotations"]!["run.googleapis.com/secrets"] = aliases;
        root["spec"]!["template"]!["spec"]!["containers"]![0]!["env"]![0]!["valueFrom"]!["secretKeyRef"]!["name"] = reference;
        var result = await Observe(fixture, root.ToJsonString());
        Assert.Equal(expected, result.State);
        Assert.Equal(expected == CloudRunExecutionState.Succeeded, result.ConfigurationMatched);
    }

    [Theory]
    [InlineData("safe", CloudRunExecutionState.Succeeded)]
    [InlineData("changed", CloudRunExecutionState.Unknown)]
    public async Task PlainEnvironmentRequiresExactDeclaredValue(string value, CloudRunExecutionState expected)
    {
        // protects=complete plain environment inventory; fails_when=overridden captured environment is accepted;
        // no_claim=configuration values establish application acceptance.
        using var fixture = new ObserverFixture(new Dictionary<string, string> { ["MODE"] = "safe" });
        var root = JsonNode.Parse(fixture.ExecutionJson())!.AsObject();
        root["spec"]!["template"]!["spec"]!["containers"]![0]!["env"]!.AsArray().Add(new JsonObject { ["name"] = "MODE", ["value"] = value });
        var result = await Observe(fixture, root.ToJsonString());
        Assert.Equal(expected, result.State);
        Assert.Equal(expected == CloudRunExecutionState.Succeeded, result.ConfigurationMatched);
    }

    [Theory]
    [InlineData(62, CloudRunExecutionState.Succeeded)]
    [InlineData(63, CloudRunExecutionState.Unknown)]
    public async Task ExecutionJsonDepthHasAnInclusive64LevelBoundary(int arrayLevels, CloudRunExecutionState expected)
    {
        // protects=bounded parser nesting at the observer entry; fails_when=depth 64 is denied or depth 65 is admitted;
        // no_claim=ignored metadata establishes any required execution fact.
        using var fixture = new ObserverFixture();
        var nested = new string('[', arrayLevels) + "0" + new string(']', arrayLevels);
        var json = fixture.ExecutionJson().Replace("\"metadata\":{", "\"metadata\":{\"unrelated_metadata\":" + nested + ",", StringComparison.Ordinal);
        var result = await Observe(fixture, json);
        Assert.Equal(expected, result.State);
        Assert.Equal(expected == CloudRunExecutionState.Succeeded, result.ConfigurationMatched);
        if (expected == CloudRunExecutionState.Unknown) Assert.Equal("ASDEPLOY174", result.DiagnosticCode);
    }

    private static async Task<CloudRunExecutionObservation> Observe(ObserverFixture fixture, string json)
    {
        var reader = new StubReader(json);
        return await new GcpCloudRunExecutionObserver(reader, TimeProvider.System).ObserveAsync(fixture.Request());
    }
}
