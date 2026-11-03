using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Data-only controls for the closed v2 application descriptor wire format.</summary>
public sealed class EvidenceApplicationDescriptorDataTests
{
    private static readonly Guid Generation = Guid.Parse("4523b7bf-72a3-4fc7-a367-86857556e574");
    private static readonly LinuxRunAccountSnapshot Accounts = new(65010, 65011, 65012, 65013, 65014);
    private static readonly LinuxWorkspaceLayout Layout = LinuxWorkspaceLayout.Create(Generation, Accounts);
    private static readonly string EntryHash = new('a', 64);
    private static readonly string PolicyHash = new('b', 64);
    private static readonly EvidenceProducerDeclaration DefaultProducer = new(
        "coverage", "coverage", "1.0.0", ["native-http"], ["appsurface/coverage/behavioral-patch@1"],
        [new("coverage-report", "merged", "application/xml", true, 20_971_520)], 180,
        new(95m, 85m, null, null, "measurable", 0.5m));

    [Fact]
    public void V2DescriptorRoundTripsRestrictedCoverageWithExactClosedProducerShape()
    {
        var request = ParseRequest();
        var descriptor = CreateDescriptor(DefaultProducer);
        var data = Create(request, descriptor);

        using var document = JsonDocument.Parse(data.DescriptorBytes);
        var wire = document.RootElement;
        Assert.Equal("evidence-worker-linux-v2", wire.GetProperty("schema").GetString());
        Assert.Equal("observation", wire.GetProperty("mode").GetString());
        Assert.Equal(string.Empty, wire.GetProperty("proof_digest").GetString());

        var application = wire.GetProperty("application");
        var producer = Assert.Single(application.GetProperty("producers").EnumerateArray());
        var expectedProducerFields = new[]
        {
            "id", "kind", "version", "required_resources", "assertion_ids", "artifact_slots",
            "timeout_seconds", "coverage_gate",
        };
        Assert.Equal(expectedProducerFields.Order(StringComparer.Ordinal),
            producer.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal));
        Assert.False(producer.TryGetProperty("coverage_artifact_retention", out _));
        Assert.False(producer.TryGetProperty("coverage_execution", out _));

        using var ready = JsonDocument.Parse(data.ReadyBytes);
        Assert.True(ready.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(wire.GetRawText(), ready.RootElement.GetProperty("descriptor").GetRawText());

        var parsed = EvidenceLinuxWorkerSupervisor.ParseWorkerDescriptor(wire);
        var roundTrippedProducer = Assert.Single(Assert.IsType<EvidenceLinuxApplicationDescriptor>(parsed.Application).Producers);
        Assert.Equal("managed", roundTrippedProducer.CoverageArtifactRetention);
        Assert.Null(roundTrippedProducer.CoverageExecution);
    }

    [Fact]
    public void ExplicitManagedRetentionHasTheSameClosedWireRepresentationAsDefault()
    {
        var request = ParseRequest();
        var defaultData = Create(request, CreateDescriptor(DefaultProducer));
        var explicitData = Create(request, CreateDescriptor(DefaultProducer with { CoverageArtifactRetention = "managed" }));

        Assert.Equal(defaultData.DescriptorBytes, explicitData.DescriptorBytes);
        Assert.Equal(defaultData.ReadyBytes, explicitData.ReadyBytes);
    }

    [Theory]
    [MemberData(nameof(UnsupportedCoverageDeclarations))]
    public void UnsupportedCoverageSettingsRejectBeforeTheyCanBeDroppedFromTheClosedWire(
        EvidenceProducerDeclaration producer)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(() =>
            EvidenceApplicationDescriptorData.Serialize(CreateDescriptor(producer)));

        Assert.Equal("ASEVD402", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-canary", error.Message, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> UnsupportedCoverageDeclarations()
    {
        yield return [DefaultProducer with { CoverageArtifactRetention = "full" }];
        yield return [DefaultProducer with { CoverageExecution = new EvidenceCoverageExecutionOptions() }];
        yield return [DefaultProducer with { CoverageExecution = new EvidenceCoverageExecutionOptions("Release") }];
        yield return [DefaultProducer with { CoverageExecution = new EvidenceCoverageExecutionOptions(Parallelism: 2) }];
        yield return [DefaultProducer with { CoverageExecution = new EvidenceCoverageExecutionOptions(TestResults: EvidenceCoverageTestResultFormat.Junit) }];
    }

    private static EvidenceWorkerDescriptorData Create(EvidenceSupervisorRequest request,
        EvidenceLinuxApplicationDescriptor descriptor) => EvidenceWorkerDescriptorData.Create(
        request, Generation, workerPid: 123, brokerPid: 456, Accounts, Layout,
        new EvidenceLinuxArtifactIdentity(0, 27, 83, Accounts.WorkerUid, Accounts.WorkerGid),
        EntryHash, PolicyHash, TimeSpan.FromSeconds(60), descriptor);

    private static EvidenceLinuxApplicationDescriptor CreateDescriptor(EvidenceProducerDeclaration producer)
    {
        var resource = new EvidenceResourceDeclaration("native-http", "aspire_health", 90, []);
        var bundle = new[]
        {
            File("AppHost.dll", EvidenceLinuxApplicationBundleRole.AppHost),
            File("AppHost.runtimeconfig.json", EvidenceLinuxApplicationBundleRole.AppHostRuntimeConfiguration),
            File("NativeResource.dll", EvidenceLinuxApplicationBundleRole.Resource),
            File("NativeResource.runtimeconfig.json", EvidenceLinuxApplicationBundleRole.ResourceRuntimeConfiguration),
            File("dcp/dcp", EvidenceLinuxApplicationBundleRole.Dcp, 0x16d),
            File("inputs/declared-input.txt", EvidenceLinuxApplicationBundleRole.DeclaredInput),
        };
        var capabilities = new EvidenceLinuxApplicationCapabilities(
            ["inputs/declared-input.txt"], 268_435_456, 1_073_741_824, 128, 1_048_576, 120, 5);
        return new EvidenceLinuxApplicationDescriptor(
            "issue779-c3-normal", "1", "c3-test-build", new('c', 64), EntryHash, "13.4.4",
            [resource], [producer], bundle, capabilities,
            ApplicationUid: 65015, ApplicationGid: 65016, ResultsGid: 65017, ResourceAccessGid: 65018);
    }

    private static EvidenceLinuxApplicationBundleFile File(string path,
        EvidenceLinuxApplicationBundleRole role, uint mode = 0x124) =>
        new(path, role, 1, new('d', 64), mode);

    private static EvidenceSupervisorRequest ParseRequest()
    {
        var input = new Dictionary<string, object?>
        {
            ["schema"] = "evidence-supervisor-linux-v1",
            ["mode"] = "observation",
            ["tool_root"] = "/opt/tool",
            ["runtime_root"] = "/opt/runtime",
            ["runtime_host"] = "/opt/runtime/dotnet",
            ["entry_path"] = "/opt/tool/Worker.dll",
            ["policy_file"] = "/opt/tool/policy.json",
            ["subject_root"] = "/work/subject",
            ["base_revision"] = new string('a', 40),
            ["subject_revision"] = new string('b', 40),
            ["workflow_identity"] = "workflow/ref",
            ["paths"] = new[] { "src/Item.cs" },
            ["observation_profile_ids"] = new[] { "native-http-normal" },
            ["observation_producer_ids"] = new[] { "coverage" },
            ["job_deadline_utc"] = "2001-01-01T00:00:00Z",
            ["admission_seconds"] = 30,
            ["start_seconds"] = 120,
            ["collection_seconds"] = 60,
            ["cleanup_seconds"] = 600,
            ["stopping_seconds"] = 30,
            ["application_id"] = "issue779-c3-normal",
            ["application_entry_digest"] = EntryHash,
        };
        return EvidenceSupervisorRequest.Parse(JsonSerializer.SerializeToUtf8Bytes(input));
    }
}
