using System.Text.Json;

namespace ForgeTrust.AppSurface.Coverage.NativeInventory;

/// <summary>A closed repository-native coverage producer declaration shared by CI and both adapters.</summary>
internal sealed record NativeCoverageProducer(
    string Id,
    string Runner,
    string NativePlatform,
    string OsArchitecture,
    string ProcessArchitecture,
    IReadOnlyList<string> TestProjects,
    string Filter,
    bool Keycloak);

/// <summary>Single source for the baseline three producers and optional two Keycloak ARM64 producers.</summary>
internal static class NativeCoverageProducerInventory
{
    internal const string KeycloakAdapterTestProject = "Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests.csproj";
    internal const string KeycloakCoreTestProject = "Auth/ForgeTrust.AppSurface.Auth.Keycloak.Tests/ForgeTrust.AppSurface.Auth.Keycloak.Tests.csproj";

    private static readonly NativeCoverageProducer[] Baseline = CreateBaseline();

    private static NativeCoverageProducer[] CreateBaseline()
    {
        var projects = NativeCoverageTestInventory.ReadNativeProjects();
        var filter = NativeCoverageTestInventory.ReadFilter();
        return
        [
            new("linux", "ubuntu-latest", "Linux", "", "", projects, filter, false),
            new("windows", "windows-latest", "Windows", "", "", projects, filter, false),
            new("darwin", "macos-latest", "Darwin", "", "", projects, filter, false)
        ];
    }

    private static readonly NativeCoverageProducer[] KeycloakArm64 =
    [
        new("darwin-arm64-keycloak", "macos-15", "Darwin", "Arm64", "Arm64",
            [KeycloakAdapterTestProject, KeycloakCoreTestProject],
            "FullyQualifiedName~ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests|FullyQualifiedName~ForgeTrust.AppSurface.Auth.Keycloak.Tests", true),
        new("linux-arm64-keycloak", "ubuntu-24.04-arm", "Linux", "Arm64", "Arm64", [KeycloakAdapterTestProject],
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
            testProjects = producer.TestProjects,
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
