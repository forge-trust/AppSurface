using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Pure catalogue metadata controls; these tests issue no runtime admission or platform proof.</summary>
public sealed class EvidenceClosedApplicationCatalogueTests
{
    [Fact]
    public void AuthenticatedDescriptorMapsAllNineRolesAndCompleteGrantsWithoutGrantingAdmission()
    {
        var entry = Candidate();
        entry = entry with { BundleFiles = [.. entry.BundleFiles,
            new("apphost/dependency.dll", EvidenceClosedBundleRole.Dependency, 12, new string('c', 64), 0x124),
            new("apphost/AppHost.deps.json", EvidenceClosedBundleRole.DependencyManifest, 23, new string('d', 64), 0x124)] };
        var descriptor = Descriptor(entry);
        var binding = EvidenceClosedApplicationCatalogue.CreateBinding(descriptor, entry.Policy, Plan(entry.Policy));

        Assert.Equal(EvidenceCanonicalJson.Serialize(Binding(entry)), EvidenceCanonicalJson.Serialize(binding));
        Assert.Equal(9, binding.BundleFiles.Select(static file => file.Role).Distinct().Count());
        EvidenceClosedApplicationCatalogue.VerifyCandidateBinding(entry, binding.CatalogueDigest, entry.Policy, Plan(entry.Policy), binding);
        var rejected = Assert.Throws<EvidenceAdmissionException>(() =>
            EvidenceClosedApplicationCatalogue.Resolve(entry.Policy, Plan(entry.Policy), descriptor));
        Assert.Equal("ASEVD407", rejected.Code);
        var contextRejected = Assert.Throws<EvidenceAdmissionException>(() =>
            EvidenceProtectedWorkerInputs.CreateContext(descriptor, entry.Policy, Plan(entry.Policy)));
        Assert.Equal("ASEVD407", contextRejected.Code);
        Assert.False(EvidenceProtectedWorkerInputs.AcceptedConsumerProof(descriptor));
    }

    [Fact]
    public void DescriptorMappingPreservesActualProviderAndPlatformAndCopiesNestedMetadata()
    {
        var entry = Candidate();
        var descriptor = Descriptor(entry) with { Provider = "unregistered-provider", Platform = "unregistered-platform" };
        var binding = EvidenceClosedApplicationCatalogue.CreateBinding(descriptor, entry.Policy, Plan(entry.Policy));
        Assert.Equal(descriptor.Provider, binding.Provider);
        Assert.Equal(descriptor.Platform, binding.Platform);
        Reject(entry, binding);

        descriptor = descriptor with { Provider = "github-actions", Platform = "linux-x64" };
        binding = EvidenceClosedApplicationCatalogue.CreateBinding(descriptor, entry.Policy, Plan(entry.Policy));
        ((string[])descriptor.Application!.Producers[0].RequiredResources)[0] = "changed";
        ((string[])descriptor.Application.Capabilities.ReadOnlyInputs)[0] = "changed";
        Assert.Equal("http", binding.Producers[0].RequiredResources[0]);
        Assert.Equal("proof-input/declared.txt", binding.Capabilities.ReadOnlyInputs[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<EvidenceClosedBundleFile>)binding.BundleFiles).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)binding.Producers[0].AssertionIds).Clear());
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("unknown-schema")]
    [InlineData("missing-application")]
    [InlineData("sdk-version")]
    [InlineData("bad-id")]
    [InlineData("bad-digest")]
    [InlineData("unknown-role")]
    [InlineData("null-file")]
    [InlineData("null-capabilities")]
    [InlineData("null-inputs")]
    [InlineData("null-resources")]
    [InlineData("null-resource")]
    [InlineData("null-producers")]
    [InlineData("null-producer")]
    [InlineData("null-assertions")]
    [InlineData("root-application")]
    [InlineData("shared-results-group")]
    public void DescriptorMappingRejectsMalformedMetadataWithFixedClosedDiagnostic(string change)
    {
        var entry = Candidate();
        var descriptor = Descriptor(entry);
        var application = descriptor.Application!;
        descriptor = change switch
        {
            "v1" => descriptor with { Schema = "evidence-worker-linux-v1" },
            "unknown-schema" => descriptor with { Schema = "unknown-protocol-canary" },
            "missing-application" => descriptor with { Application = null },
            _ => descriptor with { Application = change switch
            {
                "sdk-version" => application with { AspireSdkVersion = "unsupported-sdk-canary" },
                "bad-id" => application with { ApplicationId = "invalid/id-canary" },
                "bad-digest" => application with { EntryDigest = new string('A', 64) },
                "unknown-role" => application with { BundleFiles = [application.BundleFiles[0] with { Role = (EvidenceLinuxApplicationBundleRole)999 }, .. application.BundleFiles.Skip(1)] },
                "null-file" => application with { BundleFiles = [null!, .. application.BundleFiles.Skip(1)] },
                "null-capabilities" => application with { Capabilities = null! },
                "null-inputs" => application with { Capabilities = application.Capabilities with { ReadOnlyInputs = null! } },
                "null-resources" => application with { Resources = null! },
                "null-resource" => application with { Resources = [null!] },
                "null-producers" => application with { Producers = null! },
                "null-producer" => application with { Producers = [null!] },
                "null-assertions" => application with { Producers = [application.Producers[0] with { AssertionIds = null! }] },
                "root-application" => application with { ApplicationUid = 0 },
                _ => application with { ResultsGid = application.ApplicationGid },
            } },
        };

        var rejected = Assert.Throws<EvidenceAdmissionException>(() =>
            EvidenceClosedApplicationCatalogue.CreateBinding(descriptor, entry.Policy, Plan(entry.Policy)));
        Assert.Equal("ASEVD404", rejected.Code);
        Assert.Null(rejected.InnerException);
        Assert.DoesNotContain("canary", rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactTaskCeilingMapsCompleteDescriptorAndParticipatesInCanonicalDigests()
    {
        var entry = EvidenceClosedApplicationCatalogue.Snapshot(Candidate());
        var descriptor = Descriptor(entry);
        var plan = Plan(entry.Policy);
        var binding = EvidenceClosedApplicationCatalogue.CreateBinding(descriptor, entry.Policy, plan);

        Assert.Equal(128, entry.Capabilities.MaximumTasks);
        Assert.Equal(128, descriptor.Application!.Capabilities.MaximumTasks);
        Assert.Equal(128, binding.Capabilities.MaximumTasks);
        Assert.Equal(EvidenceCanonicalJson.Serialize(Binding(entry)), EvidenceCanonicalJson.Serialize(binding));
        EvidenceClosedApplicationCatalogue.VerifyCandidateBinding(entry, binding.CatalogueDigest, entry.Policy, plan, binding);

        var fewerTasks = entry with { Capabilities = entry.Capabilities with { MaximumTasks = 127 } };
        Assert.NotEqual(EvidenceClosedApplicationCatalogue.ComputeEntryDigest(entry),
            EvidenceClosedApplicationCatalogue.ComputeEntryDigest(fewerTasks));
        Assert.NotEqual(EvidenceClosedApplicationCatalogue.ComputeCatalogueDigest([entry]),
            EvidenceClosedApplicationCatalogue.ComputeCatalogueDigest([fewerTasks]));
    }

    [Fact]
    public void OneTaskAboveCeilingRejectsBothCandidateAndDescriptorMetadata()
    {
        var entry = Candidate();
        var excess = entry with { Capabilities = entry.Capabilities with { MaximumTasks = 129 } };
        RejectAudit(() => EvidenceClosedApplicationCatalogue.Snapshot(excess));

        var descriptor = Descriptor(entry);
        var application = descriptor.Application!;
        descriptor = descriptor with
        {
            Application = application with
            {
                Capabilities = application.Capabilities with { MaximumTasks = 129 },
            },
        };
        RejectAudit(() => EvidenceClosedApplicationCatalogue.CreateBinding(descriptor, entry.Policy, Plan(entry.Policy)));
    }

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
            "excess-tasks" => entry with { Capabilities = entry.Capabilities with { MaximumTasks = 129 } },
            "undeclared-input" => entry with { Capabilities = entry.Capabilities with { ReadOnlyInputs = [canary] } },
            "resource-class" => entry with { Resources = [entry.Resources[0] with { CapabilityClass = canary }] },
            _ => entry with { Producers = [entry.Producers[0] with { ImplementationId = canary }, entry.Producers[1]] },
        };

        var error = Assert.Throws<EvidenceAdmissionException>(() => EvidenceClosedApplicationCatalogue.Snapshot(entry));

        Assert.Equal("ASEVD404", error.Code);
        Assert.DoesNotContain(canary, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DependencyManifestInventoryRequiresAReadOnlyDepsFile()
    {
        var entry = Candidate();
        var file = new EvidenceClosedBundleFile("apphost/apphost.deps.json",
            EvidenceClosedBundleRole.DependencyManifest, 1, new string('a', 64), 0x124);
        var complete = entry with { BundleFiles = [.. entry.BundleFiles, file] };
        var binding = Binding(complete);

        EvidenceClosedApplicationCatalogue.VerifyCandidateBinding(complete, binding.CatalogueDigest,
            complete.Policy, Plan(complete.Policy), binding);

        foreach (var invalid in new[] { file with { RelativePath = "apphost/apphost.json" }, file with { Mode = 0x16d } })
        {
            var error = Assert.Throws<EvidenceAdmissionException>(() =>
                EvidenceClosedApplicationCatalogue.Snapshot(entry with { BundleFiles = [.. entry.BundleFiles, invalid] }));
            Assert.Equal("ASEVD404", error.Code);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaterNullBundlePathRejectsWithTheFixedDiagnostic(bool observedInventory)
    {
        var entry = Candidate();
        var binding = Binding(entry);
        var malformed = ChangedFile(entry.BundleFiles,
            entry.BundleFiles[^1] with { RelativePath = null! }, entry.BundleFiles.Count - 1);

        var error = observedInventory
            ? Assert.Throws<EvidenceAdmissionException>(() =>
                EvidenceClosedApplicationCatalogue.VerifyCandidateBinding(entry, binding.CatalogueDigest,
                    entry.Policy, Plan(entry.Policy), binding with { BundleFiles = malformed }))
            : Assert.Throws<EvidenceAdmissionException>(() =>
                EvidenceClosedApplicationCatalogue.Snapshot(entry with { BundleFiles = malformed }));

        Assert.Equal("ASEVD404", error.Code);
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

    [Theory]
    [InlineData("policy-null")]
    [InlineData("policy-id-null")]
    [InlineData("policy-id-empty")]
    [InlineData("policy-id-long")]
    [InlineData("policy-id-character")]
    [InlineData("policy-version")]
    [InlineData("conservative-id")]
    [InlineData("profiles-null")]
    [InlineData("profiles-over-limit")]
    [InlineData("rules-null")]
    [InlineData("rules-over-limit")]
    [InlineData("rule-null")]
    [InlineData("rule-id")]
    [InlineData("rule-pattern-null")]
    [InlineData("rule-pattern-long")]
    [InlineData("rule-pattern-control")]
    [InlineData("rule-profile-id")]
    [InlineData("profile-null")]
    [InlineData("profile-id")]
    [InlineData("profile-scope")]
    [InlineData("resources-null")]
    [InlineData("resources-over-limit")]
    [InlineData("producers-null")]
    [InlineData("producers-over-limit")]
    [InlineData("obligations-null")]
    [InlineData("obligations-over-limit")]
    [InlineData("resource-null")]
    [InlineData("resource-id")]
    [InlineData("resource-readiness-null")]
    [InlineData("resource-readiness-long")]
    [InlineData("resource-deadline-zero")]
    [InlineData("resource-deadline-over-limit")]
    [InlineData("resource-dependencies-null")]
    [InlineData("resource-dependencies-over-limit")]
    [InlineData("resource-dependency-id")]
    [InlineData("resource-dependency-duplicate")]
    [InlineData("producer-null")]
    [InlineData("producer-id")]
    [InlineData("producer-kind")]
    [InlineData("producer-version")]
    [InlineData("producer-timeout-zero")]
    [InlineData("producer-timeout-over-limit")]
    [InlineData("producer-resources-null")]
    [InlineData("producer-resources-over-limit")]
    [InlineData("producer-resource-id")]
    [InlineData("producer-resource-duplicate")]
    [InlineData("assertions-null")]
    [InlineData("assertions-over-limit")]
    [InlineData("assertion-null")]
    [InlineData("assertion-empty")]
    [InlineData("assertion-long")]
    [InlineData("assertion-control")]
    [InlineData("assertion-duplicate")]
    [InlineData("slots-null")]
    [InlineData("slots-over-limit")]
    [InlineData("slot-null")]
    [InlineData("slot-name")]
    [InlineData("slot-root-null")]
    [InlineData("slot-root-empty")]
    [InlineData("slot-root-dot")]
    [InlineData("slot-root-empty-component")]
    [InlineData("slot-root-character")]
    [InlineData("slot-media-null")]
    [InlineData("slot-media-long")]
    [InlineData("slot-bytes-negative")]
    [InlineData("slot-bytes-over-limit")]
    [InlineData("obligation-null")]
    [InlineData("obligation-id")]
    [InlineData("obligation-risk-null")]
    [InlineData("obligation-risk-long")]
    [InlineData("obligation-rationale-empty")]
    [InlineData("obligation-rationale-long")]
    [InlineData("obligation-assertion-null")]
    [InlineData("obligation-assertion-long")]
    [InlineData("obligation-producers-null")]
    [InlineData("obligation-producers-over-limit")]
    [InlineData("obligation-producer-id")]
    public void SnapshotRejectsMalformedNestedPolicyBeforeItCanBecomeCatalogueMetadata(string change)
    {
        var entry = Candidate();
        var policy = entry.Policy;
        var profile = policy.Profiles[0];
        var rule = policy.Rules[0];
        var resource = profile.Resources[0];
        var producer = profile.Producers[0];
        var slot = producer.ArtifactSlots[0];
        var obligation = profile.Obligations[0];
        var invalidId = BoundaryCanary + "/invalid";
        profile = change switch
        {
            "profile-id" => profile with { Id = invalidId },
            "profile-scope" => profile with { Scope = (EvidenceProfileScope)999 },
            "resources-null" => profile with { Resources = null! },
            "resources-over-limit" => profile with { Resources = Enumerable.Repeat(resource, 17).ToArray() },
            "producers-null" => profile with { Producers = null! },
            "producers-over-limit" => profile with { Producers = Enumerable.Repeat(producer, 33).ToArray() },
            "obligations-null" => profile with { Obligations = null! },
            "obligations-over-limit" => profile with { Obligations = Enumerable.Repeat(obligation, 129).ToArray() },
            _ => profile,
        };
        if (change.StartsWith("resource-", StringComparison.Ordinal))
        {
            resource = change switch
            {
                "resource-id" => resource with { Id = invalidId },
                "resource-readiness-null" => resource with { Readiness = null! },
                "resource-readiness-long" => resource with { Readiness = new string('r', 129) },
                "resource-deadline-zero" => resource with { DeadlineSeconds = 0 },
                "resource-deadline-over-limit" => resource with { DeadlineSeconds = 121 },
                "resource-dependencies-null" => resource with { Requires = null! },
                "resource-dependencies-over-limit" => resource with { Requires = Enumerable.Repeat("http", 17).ToArray() },
                "resource-dependency-id" => resource with { Requires = [invalidId] },
                "resource-dependency-duplicate" => resource with { Requires = ["http", "http"] },
                _ => resource,
            };
            profile = profile with { Resources = [change == "resource-null" ? null! : resource] };
        }
        if (change.StartsWith("producer-", StringComparison.Ordinal) || change.StartsWith("assertion", StringComparison.Ordinal)
            || change.StartsWith("slot", StringComparison.Ordinal))
        {
            producer = change switch
            {
                "producer-id" => producer with { Id = invalidId },
                "producer-kind" => producer with { Kind = invalidId },
                "producer-version" => producer with { Version = invalidId },
                "producer-timeout-zero" => producer with { TimeoutSeconds = 0 },
                "producer-timeout-over-limit" => producer with { TimeoutSeconds = 601 },
                "producer-resources-null" => producer with { RequiredResources = null! },
                "producer-resources-over-limit" => producer with { RequiredResources = Enumerable.Repeat("http", 17).ToArray() },
                "producer-resource-id" => producer with { RequiredResources = [invalidId] },
                "producer-resource-duplicate" => producer with { RequiredResources = ["http", "http"] },
                "assertions-null" => producer with { AssertionIds = null! },
                "assertions-over-limit" => producer with { AssertionIds = Enumerable.Repeat("assertion", 129).ToArray() },
                "assertion-null" => producer with { AssertionIds = [null!] },
                "assertion-empty" => producer with { AssertionIds = [string.Empty] },
                "assertion-long" => producer with { AssertionIds = [new string('a', 129)] },
                "assertion-control" => producer with { AssertionIds = [BoundaryCanary + "\n"] },
                "assertion-duplicate" => producer with { AssertionIds = ["assertion", "assertion"] },
                "slots-null" => producer with { ArtifactSlots = null! },
                "slots-over-limit" => producer with { ArtifactSlots = Enumerable.Repeat(slot, 129).ToArray() },
                _ => producer,
            };
            if (change.StartsWith("slot-", StringComparison.Ordinal))
            {
                slot = change switch
                {
                    "slot-name" => slot with { LogicalName = invalidId },
                    "slot-root-null" => slot with { RelativeRoot = null! },
                    "slot-root-empty" => slot with { RelativeRoot = string.Empty },
                    "slot-root-dot" => slot with { RelativeRoot = "reports/./" + BoundaryCanary },
                    "slot-root-empty-component" => slot with { RelativeRoot = "reports//" + BoundaryCanary },
                    "slot-root-character" => slot with { RelativeRoot = BoundaryCanary + "?" },
                    "slot-media-null" => slot with { MediaType = null! },
                    "slot-media-long" => slot with { MediaType = new string('m', 129) },
                    "slot-bytes-negative" => slot with { MaximumBytes = -1 },
                    "slot-bytes-over-limit" => slot with { MaximumBytes = 256L * 1024 * 1024 + 1 },
                    _ => slot,
                };
                producer = producer with { ArtifactSlots = [change == "slot-null" ? null! : slot] };
            }
            profile = profile with { Producers = [change == "producer-null" ? null! : producer, profile.Producers[1]] };
        }
        if (change.StartsWith("obligation-", StringComparison.Ordinal))
        {
            obligation = change switch
            {
                "obligation-id" => obligation with { Id = invalidId },
                "obligation-risk-null" => obligation with { RiskClass = null! },
                "obligation-risk-long" => obligation with { RiskClass = new string('r', 129) },
                "obligation-rationale-empty" => obligation with { Rationale = string.Empty },
                "obligation-rationale-long" => obligation with { Rationale = new string('r', 4097) },
                "obligation-assertion-null" => obligation with { RequiredAssertionId = null! },
                "obligation-assertion-long" => obligation with { RequiredAssertionId = new string('a', 129) },
                "obligation-producers-null" => obligation with { RequiredProducerIds = null! },
                "obligation-producers-over-limit" => obligation with { RequiredProducerIds = Enumerable.Repeat("coverage", 33).ToArray() },
                "obligation-producer-id" => obligation with { RequiredProducerIds = [invalidId] },
                _ => obligation,
            };
            profile = profile with { Obligations = [change == "obligation-null" ? null! : obligation] };
        }
        policy = policy with { Profiles = [change == "profile-null" ? null! : profile, policy.Profiles[1]] };
        rule = change switch
        {
            "rule-id" => rule with { Id = invalidId },
            "rule-pattern-null" => rule with { Pattern = null! },
            "rule-pattern-long" => rule with { Pattern = new string('p', 257) },
            "rule-pattern-control" => rule with { Pattern = BoundaryCanary + "\n" },
            "rule-profile-id" => rule with { ProfileId = invalidId },
            _ => rule,
        };
        policy = change switch
        {
            "policy-null" => null!,
            "policy-id-null" => policy with { Id = null! },
            "policy-id-empty" => policy with { Id = string.Empty },
            "policy-id-long" => policy with { Id = new string('i', 129) },
            "policy-id-character" => policy with { Id = invalidId },
            "policy-version" => policy with { Version = invalidId },
            "conservative-id" => policy with { ConservativeProfileId = invalidId },
            "profiles-null" => policy with { Profiles = null! },
            "profiles-over-limit" => policy with { Profiles = Enumerable.Repeat(profile, 33).ToArray() },
            "rules-null" => policy with { Rules = null! },
            "rules-over-limit" => policy with { Rules = Enumerable.Repeat(rule, 129).ToArray() },
            _ => policy with { Rules = [change == "rule-null" ? null! : rule] },
        };

        RejectAudit(() => EvidenceClosedApplicationCatalogue.Snapshot(entry with { Policy = policy }));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("build-id")]
    [InlineData("profile-id")]
    [InlineData("selected-profile-absent")]
    [InlineData("selected-resources-empty")]
    [InlineData("selected-resources-extra")]
    [InlineData("selected-producers-empty")]
    [InlineData("registrations-resources-null")]
    [InlineData("registrations-resources-empty")]
    [InlineData("registrations-resources-extra")]
    [InlineData("registration-resource-null")]
    [InlineData("registration-resource-declaration-null")]
    [InlineData("resource-capability-version")]
    [InlineData("resource-name")]
    [InlineData("resource-readiness")]
    [InlineData("resource-dependencies-null")]
    [InlineData("resource-dependencies-present")]
    [InlineData("registrations-producers-null")]
    [InlineData("registrations-producers-over-limit")]
    [InlineData("registration-producer-null")]
    [InlineData("registration-producer-declaration-null")]
    [InlineData("producer-implementation-version")]
    [InlineData("producer-declaration-kind")]
    [InlineData("producer-declaration-version")]
    public void SnapshotRequiresTheCompleteClosedCandidateRegistrationShape(string change)
    {
        var entry = Candidate();
        var resource = entry.Resources[0];
        var producer = entry.Producers[0];
        var profile = entry.Policy.Profiles[0];
        var invalidId = BoundaryCanary + "/invalid";
        if (change.StartsWith("selected-", StringComparison.Ordinal) && change != "selected-profile-absent")
        {
            profile = change switch
            {
                "selected-resources-empty" => profile with { Resources = [], Producers = profile.Producers.Select(static item => item with { RequiredResources = [] }).ToArray() },
                "selected-resources-extra" => profile with { Resources = [profile.Resources[0], profile.Resources[0] with { Id = "extra" }] },
                _ => profile with { Producers = [], Obligations = [] },
            };
            entry = entry with { Policy = entry.Policy with { Profiles = [profile, entry.Policy.Profiles[1]] } };
        }
        resource = change switch
        {
            "resource-capability-version" => resource with { CapabilityVersion = "2.0.0" },
            "resource-name" => resource with { ResourceName = BoundaryCanary },
            "resource-readiness" => resource with { Declaration = resource.Declaration with { Readiness = "completion" } },
            "resource-dependencies-null" => resource with { Declaration = resource.Declaration with { Requires = null! } },
            "resource-dependencies-present" => resource with { Declaration = resource.Declaration with { Requires = ["http"] } },
            "registration-resource-declaration-null" => resource with { Declaration = null! },
            _ => resource,
        };
        producer = change switch
        {
            "producer-implementation-version" => producer with { ImplementationVersion = "2.0.0" },
            "producer-declaration-kind" => producer with { Declaration = producer.Declaration with { Kind = "other" } },
            "producer-declaration-version" => producer with { Declaration = producer.Declaration with { Version = "2.0.0" } },
            "registration-producer-declaration-null" => producer with { Declaration = null! },
            _ => producer,
        };
        entry = entry with { Resources = [change == "registration-resource-null" ? null! : resource],
            Producers = [change == "registration-producer-null" ? null! : producer, entry.Producers[1]] };
        entry = change switch
        {
            "id" => entry with { Id = invalidId },
            "version" => entry with { Version = invalidId },
            "build-id" => entry with { BuildId = invalidId },
            "profile-id" => entry with { ProfileId = invalidId },
            "selected-profile-absent" => entry with { ProfileId = "absent-" + BoundaryCanary },
            "registrations-resources-null" => entry with { Resources = null! },
            "registrations-resources-empty" => entry with { Resources = [] },
            "registrations-resources-extra" => entry with { Resources = [resource, resource] },
            "registrations-producers-null" => entry with { Producers = null! },
            "registrations-producers-over-limit" => entry with { Producers = Enumerable.Repeat(producer, 33).ToArray() },
            _ => entry,
        };

        RejectAudit(() => EvidenceClosedApplicationCatalogue.Snapshot(entry));
    }

    [Theory]
    [InlineData("null-list")]
    [InlineData("too-few-files")]
    [InlineData("null-file")]
    [InlineData("empty-path")]
    [InlineData("single-dot-path")]
    [InlineData("empty-path-component")]
    [InlineData("invalid-path-character")]
    [InlineData("unknown-role")]
    [InlineData("null-hash")]
    [InlineData("zero-length")]
    [InlineData("file-length-over-limit")]
    [InlineData("total-length-over-limit")]
    [InlineData("dcp-path")]
    [InlineData("extension-prefix")]
    [InlineData("extension-mode")]
    [InlineData("apphost-suffix")]
    [InlineData("resource-suffix")]
    [InlineData("apphost-runtime-suffix")]
    [InlineData("resource-runtime-suffix")]
    [InlineData("file-directory-collision")]
    [InlineData("duplicate-resource-role")]
    public void SnapshotRejectsInventoriesThatCannotDescribeAClosedReadOnlyBundle(string change)
    {
        var entry = Candidate();
        var file = entry.BundleFiles[0];
        IReadOnlyList<EvidenceClosedBundleFile> files = change switch
        {
            "null-list" => null!,
            "too-few-files" => entry.BundleFiles.Take(5).ToArray(),
            "null-file" => ChangedFile(entry.BundleFiles, null!),
            "empty-path" => ChangedFile(entry.BundleFiles, file with { RelativePath = string.Empty }),
            "single-dot-path" => ChangedFile(entry.BundleFiles, file with { RelativePath = "." }),
            "empty-path-component" => ChangedFile(entry.BundleFiles, file with { RelativePath = "apphost//" + BoundaryCanary }),
            "invalid-path-character" => ChangedFile(entry.BundleFiles, file with { RelativePath = BoundaryCanary + "?.dll" }),
            "unknown-role" => ChangedFile(entry.BundleFiles, file with { Role = (EvidenceClosedBundleRole)999 }),
            "null-hash" => ChangedFile(entry.BundleFiles, file with { Sha256 = null! }),
            "zero-length" => ChangedFile(entry.BundleFiles, file with { LengthBytes = 0 }),
            "file-length-over-limit" => ChangedFile(entry.BundleFiles, file with { LengthBytes = 128L * 1024 * 1024 + 1 }),
            "total-length-over-limit" => entry.BundleFiles.Select(static item => item with { LengthBytes = 128L * 1024 * 1024 }).ToArray(),
            "dcp-path" => ChangedFile(entry.BundleFiles, entry.BundleFiles[4] with { RelativePath = "dcp/" + BoundaryCanary }, 4),
            "extension-prefix" => ChangedFile(entry.BundleFiles, entry.BundleFiles[5] with { RelativePath = "dcp/extensions/" + BoundaryCanary }, 5),
            "extension-mode" => ChangedFile(entry.BundleFiles, entry.BundleFiles[5] with { Mode = 0x124 }, 5),
            "apphost-suffix" => ChangedFile(entry.BundleFiles, file with { RelativePath = "apphost/" + BoundaryCanary }),
            "resource-suffix" => ChangedFile(entry.BundleFiles, entry.BundleFiles[2] with { RelativePath = "resource/" + BoundaryCanary }, 2),
            "apphost-runtime-suffix" => ChangedFile(entry.BundleFiles, entry.BundleFiles[1] with { RelativePath = "apphost/config.json" }, 1),
            "resource-runtime-suffix" => ChangedFile(entry.BundleFiles, entry.BundleFiles[3] with { RelativePath = "resource/config.json" }, 3),
            "file-directory-collision" => entry.BundleFiles.Append(new("apphost", EvidenceClosedBundleRole.Dependency, 1, new string('a', 64), 0x124)).ToArray(),
            _ => entry.BundleFiles.Append(new("resource/Second.dll", EvidenceClosedBundleRole.Resource, 1, new string('a', 64), 0x124)).ToArray(),
        };

        RejectAudit(() => EvidenceClosedApplicationCatalogue.Snapshot(entry with { BundleFiles = files }));
    }

    [Theory]
    [InlineData("plan-null")]
    [InlineData("paths-null")]
    [InlineData("paths-over-limit")]
    [InlineData("resources-null")]
    [InlineData("resources-over-limit")]
    [InlineData("producers-null")]
    [InlineData("producers-over-limit")]
    [InlineData("planner-path-rejection")]
    public void CandidateBindingRejectsUnboundedOrUnplannableObservedInputs(string change)
    {
        var entry = Candidate();
        var plan = Plan(entry.Policy);
        var binding = Binding(entry);
        plan = change switch
        {
            "plan-null" => null!,
            "paths-null" => plan with { ChangedPaths = null! },
            "paths-over-limit" => plan with { ChangedPaths = Enumerable.Repeat(plan.ChangedPaths[0], 4097).ToArray() },
            "planner-path-rejection" => plan with { ChangedPaths = [new("src/../" + BoundaryCanary)] },
            _ => plan,
        };
        binding = change switch
        {
            "resources-null" => binding with { Resources = null! },
            "resources-over-limit" => binding with { Resources = Enumerable.Repeat(binding.Resources[0], 17).ToArray() },
            "producers-null" => binding with { Producers = null! },
            "producers-over-limit" => binding with { Producers = Enumerable.Repeat(binding.Producers[0], 33).ToArray() },
            _ => binding,
        };

        RejectAudit(() => EvidenceClosedApplicationCatalogue.VerifyCandidateBinding(entry, binding.CatalogueDigest,
            entry.Policy, plan, binding));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealPlannerPolicyRejectionsTranslateToTheFixedCatalogueDiagnostic(bool observedPolicy)
    {
        var entry = Candidate();
        var policy = entry.Policy with { Rules = [entry.Policy.Rules[0] with { ProfileId = BoundaryCanary }] };
        var binding = Binding(entry);

        if (observedPolicy)
            RejectAudit(() => EvidenceClosedApplicationCatalogue.VerifyCandidateBinding(entry, binding.CatalogueDigest,
                policy, Plan(entry.Policy), binding));
        else
            RejectAudit(() => EvidenceClosedApplicationCatalogue.Snapshot(entry with { Policy = policy }));
    }

    [Fact]
    public void SnapshotAcceptsExactPolicyCountsAndInventoryByteCeilingAsMetadataOnly()
    {
        var entry = Candidate();
        var policy = entry.Policy with
        {
            Id = new string('i', 128), Version = new string('v', 128),
            Profiles = [entry.Policy.Profiles[0], .. Enumerable.Range(1, 31)
                .Select(static index => new EvidenceProfile("unused-" + index, EvidenceProfileScope.Targeted, [], [], []))],
            Rules = Enumerable.Range(0, 128).Select(index => new EvidencePolicyRule("rule-" + index,
                "src/File" + index + ".cs", entry.ProfileId)).ToArray(),
        };
        var files = entry.BundleFiles.Select((file, index) => file with
        {
            LengthBytes = index < 3 ? 128L * 1024 * 1024 : index == 3 ? 128L * 1024 * 1024 - 3 : 1,
        }).ToArray();

        var snapshot = EvidenceClosedApplicationCatalogue.Snapshot(entry with { Policy = policy, BundleFiles = files });

        Assert.Equal(32, snapshot.Policy.Profiles.Count);
        Assert.Equal(128, snapshot.Policy.Rules.Count);
        Assert.Equal(512L * 1024 * 1024, snapshot.BundleFiles.Sum(static item => item.LengthBytes));
        Assert.Equal(128, snapshot.Policy.Id.Length);
        Assert.Throws<NotSupportedException>(() => ((IList<EvidencePolicyRule>)snapshot.Policy.Rules).Clear());
    }

    [Fact]
    public void SnapshotIncludesUnselectedDeclarationsAtTheirValidBoundsWithoutIssuingAdmission()
    {
        var entry = Candidate();
        var resources = Enumerable.Range(0, 16).Select(static index => new EvidenceResourceDeclaration(
            "resource-" + index, "completion", 120, [])).ToArray();
        var assertions = Enumerable.Range(0, 128).Select(static index => index == 0 ? new string('a', 128) : "assertion-" + index).ToArray();
        var slots = Enumerable.Range(0, 128).Select(static index => new EvidenceArtifactSlot(
            "slot-" + index, index == 0 ? new string('r', 256) : "reports/" + index,
            index == 0 ? new string('m', 128) : "application/xml", true, index == 0 ? 0 : 256L * 1024 * 1024)).ToArray();
        var producers = Enumerable.Range(0, 32).Select(index => new EvidenceProducerDeclaration(
            "producer-" + index, "coverage", "1.0.0", resources.Select(static item => item.Id).ToArray(), assertions, slots, 600)).ToArray();
        var obligations = Enumerable.Range(0, 128).Select(index => new EvidenceObligation(
            "obligation-" + index, index == 0 ? new string('r', 128) : "behavior",
            index == 0 ? new string('r', 4096) : "Declared behavior requires evidence.",
            producers.Select(static item => item.Id).ToArray(), assertions[0])).ToArray();
        var unselected = new EvidenceProfile("bounded-unselected", EvidenceProfileScope.Targeted, resources, producers, obligations);

        var snapshot = EvidenceClosedApplicationCatalogue.Snapshot(entry with
        {
            Policy = entry.Policy with { Profiles = [entry.Policy.Profiles[0], unselected] },
        });

        var frozen = snapshot.Policy.Profiles.Single(static item => item.Id == "bounded-unselected");
        Assert.Equal(16, frozen.Resources.Count);
        Assert.Equal(32, frozen.Producers.Count);
        Assert.Equal(128, frozen.Obligations.Count);
        Assert.Equal(16, frozen.Producers[0].RequiredResources.Count);
        Assert.Equal(128, frozen.Producers[0].AssertionIds.Count);
        Assert.Equal(128, frozen.Producers[0].ArtifactSlots.Count);
        Assert.Equal(32, frozen.Obligations[0].RequiredProducerIds.Count);
        Assert.Equal(256, frozen.Producers[0].ArtifactSlots[0].RelativeRoot.Length);
        assertions[0] = BoundaryCanary;
        slots[0] = slots[0] with { MaximumBytes = 1 };
        Assert.Equal(128, frozen.Producers[0].AssertionIds[0].Length);
        Assert.Equal(0, frozen.Producers[0].ArtifactSlots[0].MaximumBytes);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)frozen.Obligations[0].RequiredProducerIds).Clear());
    }

    private const string BoundaryCanary = "catalogue-boundary-canary-779";

    private static void RejectAudit(Action audit)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(audit);
        Assert.Equal("ASEVD404", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(BoundaryCanary, error.Message, StringComparison.Ordinal);
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

    private static EvidenceLinuxWorkerDescriptor Descriptor(EvidenceClosedApplicationDefinition entry)
    {
        var binding = Binding(entry);
        return new("evidence-worker-linux-v2", "run/attempt-1", 123, 1001, 1001, 1002, 1002,
            "worker.service", "/system.slice/worker.service", DateTimeOffset.UtcNow.AddMinutes(5),
            "/tools", "/subject", "/output", "slot", "/usr/bin/dotnet", "/scratch/test-output", "/tools/policy.json",
            "observation", "/control/broker/control.sock", new string('a', 64), new string('b', 40), new string('c', 40),
            "workflow:protected", "github-actions", "linux-x64", new string('d', 64), new string('e', 64),
            new(8, 1, 42, 1001, 1001), ["native-http"], ["coverage"], ["src/Feature.cs"], 10, 30, 30, 60, 5,
            Application: new(entry.Id, entry.Version, entry.BuildId, binding.CatalogueDigest, binding.EntryDigest,
                entry.AspireSdkVersion, entry.Resources.Select(static item => item.Declaration).ToArray(),
                entry.Producers.Select(static item => item.Declaration).ToArray(),
                entry.BundleFiles.Select(static item => new EvidenceLinuxApplicationBundleFile(item.RelativePath,
                    (EvidenceLinuxApplicationBundleRole)item.Role, item.LengthBytes, item.Sha256, item.Mode)).ToArray(),
                new(entry.Capabilities.ReadOnlyInputs, entry.Capabilities.ScratchBytes, entry.Capabilities.MemoryBytes,
                    entry.Capabilities.MaximumTasks, entry.Capabilities.MaximumOutputBytes, entry.Capabilities.StartSeconds,
                    entry.Capabilities.StoppingSeconds), 1003, 1003, 2001, 2002));
    }

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
            new(new[] { "proof-input/declared.txt" }, 1024 * 1024, 1024 * 1024, 128, 1024 * 1024, 30, 5));
    }
}
