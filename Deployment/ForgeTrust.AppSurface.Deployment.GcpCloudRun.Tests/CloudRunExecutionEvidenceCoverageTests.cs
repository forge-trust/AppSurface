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

    private static async Task<CloudRunExecutionObservation> Observe(ObserverFixture fixture, string json)
    {
        var reader = new StubReader(json);
        return await new GcpCloudRunExecutionObserver(reader, TimeProvider.System).ObserveAsync(fixture.Request());
    }
}
