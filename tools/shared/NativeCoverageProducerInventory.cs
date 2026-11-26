using System.Text.Json;

namespace ForgeTrust.AppSurface.Coverage.NativeInventory;

/// <summary>A closed repository-native coverage producer declaration shared by CI and both adapters.</summary>
internal sealed record NativeCoverageProducer(
    string Id,
    string Runner,
    string NativePlatform,
    string OsArchitecture,
    string ProcessArchitecture,
    string TestProject,
    string Filter,
    bool Keycloak);

/// <summary>Single source for the baseline three producers and optional two Keycloak ARM64 producers.</summary>
internal static class NativeCoverageProducerInventory
{
    internal const string CliTestProject = "Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj";
    internal const string KeycloakTestProject = "Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests.csproj";

    internal const string NativeFilter = "FullyQualifiedName~CoverageRunArtifactReaderTests|FullyQualifiedName~CoverageRunOutputGuardSecurityTests|FullyQualifiedName~CoverageGateTests|FullyQualifiedName~CoverageArtifact|FullyQualifiedName~CoverageStorageCommand|FullyQualifiedName~CoverageRunProcessContractTests|FullyQualifiedName~CoverageRunCollectorVersionPreflightTests|FullyQualifiedName~CoverageFanIn|FullyQualifiedName~EvidenceTestProducerPlannerTests|FullyQualifiedName~EvidenceArtifactFileValidationTests|FullyQualifiedName~EvidenceRunTestProducerTests|FullyQualifiedName~TestRunReconcilerTests|FullyQualifiedName~DotnetCliTestRunSourceTests|FullyQualifiedName~TestEvidenceProducerTests|FullyQualifiedName~TestEvidenceProducerProcessTests|FullyQualifiedName~VsTestRunnerProbeTests";

    private static readonly NativeCoverageProducer[] Baseline =
    [
        new("linux", "ubuntu-latest", "Linux", "", "", CliTestProject, NativeFilter, false),
        new("windows", "windows-latest", "Windows", "", "", CliTestProject, NativeFilter, false),
        new("darwin", "macos-latest", "Darwin", "", "", CliTestProject, NativeFilter, false)
    ];

    private static readonly NativeCoverageProducer[] KeycloakArm64 =
    [
        new("darwin-arm64-keycloak", "macos-15", "Darwin", "Arm64", "Arm64", KeycloakTestProject,
            "FullyQualifiedName~ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests", true),
        new("linux-arm64-keycloak", "ubuntu-24.04-arm", "Linux", "Arm64", "Arm64", KeycloakTestProject,
            "FullyQualifiedName~AppSurfaceKeycloakLinuxArm64WorkerTests", true)
    ];

    internal static IReadOnlyList<NativeCoverageProducer> Create(bool includeKeycloak)
        => includeKeycloak ? [.. Baseline, .. KeycloakArm64] : Baseline;

    internal static byte[] SerializeWorkflowInventory(bool includeKeycloak)
    {
        var producers = Create(includeKeycloak);
        var matrix = producers.Select(producer => new
        {
            producerId = producer.Id,
            runner = producer.Runner,
            nativePlatform = producer.NativePlatform,
            osArchitecture = producer.OsArchitecture,
            processArchitecture = producer.ProcessArchitecture,
            testProject = producer.TestProject,
            filter = producer.Filter,
            keycloak = producer.Keycloak
        }).ToArray();
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            cardinality = producers.Count,
            producers,
            matrix = new { include = matrix }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
