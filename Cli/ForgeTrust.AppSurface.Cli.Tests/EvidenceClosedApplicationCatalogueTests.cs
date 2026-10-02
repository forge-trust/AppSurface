using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Pure catalogue metadata controls; these tests issue no runtime admission or platform proof.</summary>
public sealed class EvidenceClosedApplicationCatalogueTests
{
    [Fact]
    public void ProductionResolveRemainsClosedForACompletelyMatchingCandidate()
    {
        var entry = Candidate();
        var plan = Plan(entry.Policy);
        var binding = Binding(entry);
        EvidenceClosedApplicationCatalogue.VerifyCandidateBinding(entry, binding.CatalogueDigest, entry.Policy, plan, binding);

        var error = Assert.Throws<EvidenceAdmissionException>(() =>
            EvidenceClosedApplicationCatalogue.Resolve(entry.Policy, plan, binding));

        Assert.Equal("ASEVD407", error.Code);
        Assert.DoesNotContain(entry.BuildId, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompleteBindingAcceptsInventoryOrderChangesButHashesAllMetadata()
    {
        var entry = Candidate();
        var binding = Binding(entry) with
        {
            Producers = entry.Producers.Reverse().Select(static item => item.Declaration).ToArray(),
            BundleFiles = entry.BundleFiles.Reverse().ToArray(),
        };

        EvidenceClosedApplicationCatalogue.VerifyCandidateBinding(entry, binding.CatalogueDigest, entry.Policy, Plan(entry.Policy), binding);

        Assert.Equal(EvidenceClosedApplicationCatalogue.ComputeEntryDigest(entry),
            EvidenceClosedApplicationCatalogue.ComputeEntryDigest(entry with { BundleFiles = entry.BundleFiles.Reverse().ToArray() }));
        Assert.NotEqual(EvidenceClosedApplicationCatalogue.ComputeEntryDigest(entry),
            EvidenceClosedApplicationCatalogue.ComputeEntryDigest(entry with { BuildId = "different-build" }));
        var other = Candidate("other-app", "other-policy");
        Assert.Equal(EvidenceClosedApplicationCatalogue.ComputeCatalogueDigest([entry, other]),
            EvidenceClosedApplicationCatalogue.ComputeCatalogueDigest([other, entry]));
    }

    [Fact]
    public void SnapshotKeepsNestedPolicyRegistrationsBundleAndCapabilitiesStableAfterSourceMutation()
    {
        var entry = Candidate();
        var snapshot = EvidenceClosedApplicationCatalogue.Snapshot(entry);
        var digest = EvidenceClosedApplicationCatalogue.ComputeEntryDigest(snapshot);
        var profile = entry.Policy.Profiles[0];
        ((string[])profile.Producers[0].RequiredResources)[0] = "mutated";
        ((string[])profile.Producers[0].AssertionIds)[0] = "mutated";
        ((EvidenceArtifactSlot[])profile.Producers[0].ArtifactSlots)[0] = profile.Producers[0].ArtifactSlots[0] with { MaximumBytes = 1 };
        ((string[])profile.Obligations[0].RequiredProducerIds)[0] = "mutated";
        ((EvidencePolicyRule[])entry.Policy.Rules)[0] = entry.Policy.Rules[0] with { Pattern = "mutated" };
        ((EvidenceProfile[])entry.Policy.Profiles)[1] = entry.Policy.Profiles[1] with { Id = "mutated" };
        ((EvidenceClosedBundleFile[])entry.BundleFiles)[0] = entry.BundleFiles[0] with { Sha256 = new string('f', 64) };
        ((string[])entry.Capabilities.ReadOnlyInputs)[0] = "mutated";

        Assert.Equal(digest, EvidenceClosedApplicationCatalogue.ComputeEntryDigest(snapshot));
        Assert.Equal("http", snapshot.Policy.Profiles[0].Producers[0].RequiredResources[0]);
        Assert.Equal("coverage/assertion@1", snapshot.Producers[0].Declaration.AssertionIds[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)snapshot.Capabilities.ReadOnlyInputs)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((IList<EvidenceProfile>)snapshot.Policy.Profiles)[0] = profile);
        var binding = Binding(snapshot);
        EvidenceClosedApplicationCatalogue.VerifyCandidateBinding(snapshot, binding.CatalogueDigest, snapshot.Policy, Plan(snapshot.Policy), binding);
    }

    [Theory]
    [InlineData("resource-deadline")]
    [InlineData("resource-readiness")]
    [InlineData("producer-assertion")]
    [InlineData("producer-artifact")]
    [InlineData("producer-timeout")]
    [InlineData("producer-gate")]
    [InlineData("missing-producer")]
    [InlineData("duplicate-producer")]
    [InlineData("extra-resource")]
    public void CompleteRegistrationDriftRejects(string change)
    {
        var entry = Candidate();
        var binding = Binding(entry);
        var resource = binding.Resources[0];
        var producer = binding.Producers[0];
        binding = change switch
        {
            "resource-deadline" => binding with { Resources = [resource with { DeadlineSeconds = 31 }] },
            "resource-readiness" => binding with { Resources = [resource with { Readiness = "completion" }] },
            "producer-assertion" => binding with { Producers = [producer with { AssertionIds = ["different/assertion@1"] }, binding.Producers[1]] },
            "producer-artifact" => binding with { Producers = [producer with { ArtifactSlots = [producer.ArtifactSlots[0] with { MaximumBytes = 1 }] }, binding.Producers[1]] },
            "producer-timeout" => binding with { Producers = [producer with { TimeoutSeconds = 61 }, binding.Producers[1]] },
            "producer-gate" => binding with { Producers = [producer with { CoverageGate = producer.CoverageGate! with { MinLinePercent = 91 } }, binding.Producers[1]] },
            "missing-producer" => binding with { Producers = [producer] },
            "duplicate-producer" => binding with { Producers = [producer, producer] },
            _ => binding with { Resources = [resource, resource with { Id = "extra" }] },
        };

        Reject(entry, binding);
    }

    [Theory]
    [InlineData("policy-version")]
    [InlineData("unselected-profile")]
    [InlineData("rule")]
    [InlineData("plan-digest")]
    [InlineData("profile")]
    public void WholePolicyAndReresolvedPlanMustMatch(string change)
    {
        var entry = Candidate();
        var policy = entry.Policy;
        var plan = Plan(policy);
        if (change == "policy-version") policy = policy with { Version = "2" };
        if (change == "unselected-profile") policy = policy with { Profiles = [policy.Profiles[0], policy.Profiles[1] with { Id = "changed" }] };
        if (change == "rule") policy = policy with { Rules = [policy.Rules[0] with { Precedence = 1 }] };
        if (change == "plan-digest") plan = plan with { PlanDigest = new string('f', 64) };
        if (change == "profile") plan = plan with { Profile = policy.Profiles[1] };
        var binding = Binding(entry);

        var error = Assert.Throws<EvidenceAdmissionException>(() =>
            EvidenceClosedApplicationCatalogue.VerifyCandidateBinding(entry, binding.CatalogueDigest, policy, plan, binding));

        Assert.Equal("ASEVD404", error.Code);
    }

    [Theory]
    [InlineData("application")]
    [InlineData("version")]
    [InlineData("build")]
    [InlineData("catalogue")]
    [InlineData("entry")]
    [InlineData("provider")]
    [InlineData("platform")]
    [InlineData("protocol")]
    [InlineData("capabilities")]
    [InlineData("bundle-hash")]
    [InlineData("bundle-mode")]
    [InlineData("bundle-length")]
    public void RootBindingMetadataMustMatchTheWholeClosedEntry(string change)
    {
        var entry = Candidate();
        var binding = Binding(entry);
        binding = change switch
        {
            "application" => binding with { ApplicationId = "different" },
            "version" => binding with { ApplicationVersion = "2" },
            "build" => binding with { BuildId = "different-build" },
            "catalogue" => binding with { CatalogueDigest = new string('f', 64) },
            "entry" => binding with { EntryDigest = new string('f', 64) },
            "provider" => binding with { Provider = "other" },
            "platform" => binding with { Platform = "osx-arm64" },
            "protocol" => binding with { WorkerProtocol = "evidence-worker-linux-v1" },
            "capabilities" => binding with { Capabilities = binding.Capabilities with { MaximumTasks = 63 } },
            "bundle-hash" => binding with { BundleFiles = ChangedFile(binding.BundleFiles, binding.BundleFiles[0] with { Sha256 = new string('f', 64) }) },
            "bundle-mode" => binding with { BundleFiles = ChangedFile(binding.BundleFiles, binding.BundleFiles[4] with { Mode = 0x124 }, 4) },
            _ => binding with { BundleFiles = ChangedFile(binding.BundleFiles, binding.BundleFiles[0] with { LengthBytes = 2 }) },
        };

        Reject(entry, binding);
    }

    [Theory]
    [InlineData("root-worker")]
    [InlineData("root-application")]
    [InlineData("same-producer-application")]
    [InlineData("same-worker-application")]
    [InlineData("application-results-group")]
    [InlineData("resource-results-group")]
    public void IdentitySeparationRejectsRootOrSharedApplicationAuthority(string change)
    {
        var entry = Candidate();
        var binding = Binding(entry);
        var ids = binding.Identities;
        binding = binding with { Identities = change switch
        {
            "root-worker" => ids with { WorkerUid = 0 },
            "root-application" => ids with { ApplicationUid = 0 },
            "same-producer-application" => ids with { ApplicationUid = ids.ProducerUid },
            "same-worker-application" => ids with { ApplicationUid = ids.WorkerUid },
            "application-results-group" => ids with { ApplicationGid = ids.ResultsGid },
            _ => ids with { ResourceAccessGid = ids.ResultsGid },
        } };

        Reject(entry, binding);
    }

    [Theory]
    [InlineData("sdk")]
    [InlineData("duplicate-file")]
    [InlineData("case-alias")]
    [InlineData("traversal")]
    [InlineData("absolute-path")]
    [InlineData("uppercase-hash")]
    [InlineData("writable-mode")]
    [InlineData("missing-dcp")]
    [InlineData("excess-files")]
    [InlineData("zero-memory")]
    [InlineData("excess-tasks")]
    [InlineData("undeclared-input")]
    [InlineData("resource-class")]
    [InlineData("producer-class")]
    public void InvalidCandidateInventoryOrCapabilityRejectsWithoutCanaryEcho(string change)
    {
        var entry = Candidate();
        const string canary = "catalogue-canary-779";
        entry = change switch
        {
            "sdk" => entry with { AspireSdkVersion = "13.4.5" },
            "duplicate-file" => entry with { BundleFiles = [.. entry.BundleFiles, entry.BundleFiles[0]] },
            "case-alias" => entry with { BundleFiles = [.. entry.BundleFiles, entry.BundleFiles[0] with { RelativePath = "APPHOST/APPHOST.DLL" }] },
            "traversal" => entry with { BundleFiles = ChangedFile(entry.BundleFiles, entry.BundleFiles[0] with { RelativePath = "../" + canary }) },
            "absolute-path" => entry with { BundleFiles = ChangedFile(entry.BundleFiles, entry.BundleFiles[0] with { RelativePath = "/" + canary }) },
            "uppercase-hash" => entry with { BundleFiles = ChangedFile(entry.BundleFiles, entry.BundleFiles[0] with { Sha256 = new string('A', 64) }) },
            "writable-mode" => entry with { BundleFiles = ChangedFile(entry.BundleFiles, entry.BundleFiles[0] with { Mode = 0x1b6 }) },
            "missing-dcp" => entry with { BundleFiles = entry.BundleFiles.Where(static file => file.Role != EvidenceClosedBundleRole.Dcp).ToArray() },
            "excess-files" => entry with { BundleFiles = Enumerable.Repeat(entry.BundleFiles[0], 257).ToArray() },
            "zero-memory" => entry with { Capabilities = entry.Capabilities with { MemoryBytes = 0 } },
            "excess-tasks" => entry with { Capabilities = entry.Capabilities with { MaximumTasks = 65 } },
            "undeclared-input" => entry with { Capabilities = entry.Capabilities with { ReadOnlyInputs = [canary] } },
            "resource-class" => entry with { Resources = [entry.Resources[0] with { CapabilityClass = canary }] },
            _ => entry with { Producers = [entry.Producers[0] with { ImplementationId = canary }, entry.Producers[1]] },
        };

        var error = Assert.Throws<EvidenceAdmissionException>(() => EvidenceClosedApplicationCatalogue.Snapshot(entry));

        Assert.Equal("ASEVD404", error.Code);
        Assert.DoesNotContain(canary, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogueRejectsDuplicateIdsDuplicatePolicyProfileBindingsAndExcessEntries()
    {
        var entry = Candidate();
        foreach (var entries in new IReadOnlyList<EvidenceClosedApplicationDefinition>[]
        {
            [entry, entry],
            [entry, entry with { Id = "another-app" }],
            Enumerable.Repeat(entry, 9).ToArray(),
        })
        {
            var error = Assert.Throws<EvidenceAdmissionException>(() => EvidenceClosedApplicationCatalogue.ComputeCatalogueDigest(entries));
            Assert.Equal("ASEVD404", error.Code);
        }
    }

    private static void Reject(EvidenceClosedApplicationDefinition entry, EvidenceClosedApplicationBinding binding)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(() =>
            EvidenceClosedApplicationCatalogue.VerifyCandidateBinding(entry,
                EvidenceClosedApplicationCatalogue.ComputeCatalogueDigest([entry]), entry.Policy, Plan(entry.Policy), binding));
        Assert.Equal("ASEVD404", error.Code);
    }

    private static EvidencePlan Plan(EvidencePolicy policy) => new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("src/Feature.cs")]);

    private static EvidenceClosedApplicationBinding Binding(EvidenceClosedApplicationDefinition entry) => new(
        entry.Id, entry.Version, entry.BuildId, EvidenceClosedApplicationCatalogue.ComputeCatalogueDigest([entry]),
        EvidenceClosedApplicationCatalogue.ComputeEntryDigest(entry), "github-actions", "linux-x64", "evidence-worker-linux-v2",
        entry.Resources.Select(static item => item.Declaration).ToArray(), entry.Producers.Select(static item => item.Declaration).ToArray(),
        entry.BundleFiles, entry.Capabilities, new(1001, 1001, 1002, 1002, 1003, 1003, 2001, 2002));

    private static IReadOnlyList<EvidenceClosedBundleFile> ChangedFile(IReadOnlyList<EvidenceClosedBundleFile> files, EvidenceClosedBundleFile changed, int index = 0) =>
        files.Select((file, position) => position == index ? changed : file).ToArray();

    private static EvidenceClosedApplicationDefinition Candidate(string id = "native-http-app", string policyId = "native-http-policy")
    {
        var resource = new EvidenceResourceDeclaration("http", "aspire_health", 30, []);
        var producer = new EvidenceProducerDeclaration("coverage", "coverage", "1.0.0", new[] { "http" }, new[] { "coverage/assertion@1" },
            new EvidenceArtifactSlot[] { new("coverage-report", "merged", "application/xml", true, 1024 * 1024) }, 60,
            new(90, 80, TolerancePercent: 0));
        var second = producer with { Id = "second" };
        var profile = new EvidenceProfile("native-http", EvidenceProfileScope.Targeted, [resource], [producer, second],
            [new("http-behavior", "behavior", "Real resource-dependent tests are required.", new[] { "coverage" }, "coverage/assertion@1")]);
        var policy = new EvidencePolicy(policyId, "1", profile.Id,
            new EvidenceProfile[] { profile, new("no-evidence", EvidenceProfileScope.Targeted, [], [], []) },
            new EvidencePolicyRule[] { new("source", "src/**", profile.Id) });
        var files = new EvidenceClosedBundleFile[]
        {
            new("apphost/AppHost.dll", EvidenceClosedBundleRole.AppHost, 1, new string('a', 64), 0x124),
            new("apphost/AppHost.runtimeconfig.json", EvidenceClosedBundleRole.AppHostRuntimeConfiguration, 1, new string('b', 64), 0x124),
            new("resource/Resource.dll", EvidenceClosedBundleRole.Resource, 1, new string('c', 64), 0x124),
            new("resource/Resource.runtimeconfig.json", EvidenceClosedBundleRole.ResourceRuntimeConfiguration, 1, new string('d', 64), 0x124),
            new("dcp/dcp", EvidenceClosedBundleRole.Dcp, 1, new string('e', 64), 0x16d),
            new("dcp/ext/native-extension", EvidenceClosedBundleRole.DcpExtension, 1, new string('f', 64), 0x16d),
            new("proof-input/declared.txt", EvidenceClosedBundleRole.DeclaredInput, 1, new string('a', 64), 0x124),
        };
        return new(id, "1.0.0", "controlled-candidate-source-build", "13.4.4", policy, profile.Id,
            [new(resource, "native-http-uds", "1.0.0", "native-http")],
            [new(producer, "coverage", "1.0.0"), new(second, "coverage", "1.0.0")], files,
            new(new[] { "proof-input/declared.txt" }, 1024 * 1024, 1024 * 1024, 64, 1024 * 1024, 30, 5));
    }
}
