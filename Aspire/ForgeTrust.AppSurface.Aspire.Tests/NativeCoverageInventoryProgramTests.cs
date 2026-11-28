using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Validation;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

[Collection("Native evidence program")]
public sealed class NativeCoverageInventoryProgramTests
{
    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 5)]
    public async Task InventoryOperationWritesSelectedClosedProducerSet(bool includeKeycloak, int expectedCount)
    {
        var args = new List<string> { "--operation", "native-coverage-inventory" };
        if (includeKeycloak) args.AddRange(["--include-keycloak-native", "true"]);

        var invocation = await InvokeAsync(args.ToArray());

        Assert.Equal(0, invocation.ExitCode);
        Assert.Empty(invocation.StandardError);
        using var inventory = JsonDocument.Parse(invocation.StandardOutput);
        var root = inventory.RootElement;
        var producers = root.GetProperty("producers").EnumerateArray().ToArray();
        Assert.Equal(expectedCount, root.GetProperty("cardinality").GetInt32());
        Assert.Equal(expectedCount, producers.Length);
        var matrix = root.GetProperty("matrix").GetProperty("include").EnumerateArray().ToArray();
        Assert.Equal(expectedCount, matrix.Length);
        Assert.Equal(
            includeKeycloak
                ? new[] { "linux", "windows", "darwin", "darwin-arm64-keycloak", "linux-arm64-keycloak" }
                : new[] { "linux", "windows", "darwin" },
            producers.Select(producer => producer.GetProperty("id").GetString()));
        var matrixById = matrix.ToDictionary(item => item.GetProperty("producerId").GetString()!, StringComparer.Ordinal);
        foreach (var producer in producers)
        {
            var transported = matrixById[producer.GetProperty("id").GetString()!];
            Assert.Equal(
                producer.GetProperty("testProjects").EnumerateArray().Select(project => project.GetString()),
                transported.GetProperty("testProjects").EnumerateArray().Select(project => project.GetString()));
            Assert.Equal(producer.GetProperty("filter").GetString(), transported.GetProperty("filter").GetString());
            Assert.Equal(producer.GetProperty("runner").GetString(), transported.GetProperty("runner").GetString());
            Assert.Equal(producer.GetProperty("keycloak").GetBoolean(), transported.GetProperty("keycloak").GetBoolean());
        }

        var workflowDirectory = TestPathUtils.PathUnder(TestPathUtils.FindRepoRoot(AppContext.BaseDirectory), ".github", "workflows");
        var baselineProjects = File.ReadAllLines(TestPathUtils.PathUnder(workflowDirectory, "native-coverage-test-projects.txt"));
        var baselineFilter = string.Join('|', File.ReadAllLines(TestPathUtils.PathUnder(workflowDirectory, "native-coverage-test-selectors.txt"))
            .Select(line => line.Split('\t', 2))
            .Where(entry => entry[0] is "native" or "shared-cli")
            .Select(entry => entry[1]));
        Assert.All(producers.Take(3), producer =>
        {
            Assert.False(producer.GetProperty("keycloak").GetBoolean());
            Assert.Equal(baselineProjects, producer.GetProperty("testProjects").EnumerateArray().Select(project => project.GetString()));
            Assert.Equal(baselineFilter, producer.GetProperty("filter").GetString());
        });

        if (includeKeycloak)
        {
            var armLanes = producers.Where(producer => producer.GetProperty("keycloak").GetBoolean()).ToArray();
            Assert.Equal(2, armLanes.Length);
            Assert.Equal(new[] { "darwin-arm64-keycloak", "linux-arm64-keycloak" },
                armLanes.Select(producer => producer.GetProperty("id").GetString()));
            Assert.All(armLanes, producer =>
            {
                Assert.Equal("Arm64", producer.GetProperty("osArchitecture").GetString());
                Assert.Equal("Arm64", producer.GetProperty("processArchitecture").GetString());
            });
            Assert.Equal("macos-15", armLanes[0].GetProperty("runner").GetString());
            Assert.Equal("Darwin", armLanes[0].GetProperty("nativePlatform").GetString());
            Assert.Equal(
                new[]
                {
                    "Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests.csproj",
                    "Auth/ForgeTrust.AppSurface.Auth.Keycloak.Tests/ForgeTrust.AppSurface.Auth.Keycloak.Tests.csproj"
                },
                armLanes[0].GetProperty("testProjects").EnumerateArray().Select(project => project.GetString()));
            Assert.Equal(
                "FullyQualifiedName~ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests|FullyQualifiedName~ForgeTrust.AppSurface.Auth.Keycloak.Tests",
                armLanes[0].GetProperty("filter").GetString());
            Assert.Equal("ubuntu-24.04-arm", armLanes[1].GetProperty("runner").GetString());
            Assert.Equal("Linux", armLanes[1].GetProperty("nativePlatform").GetString());
            Assert.Equal(
                new[]
                {
                    "Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests.csproj"
                },
                armLanes[1].GetProperty("testProjects").EnumerateArray().Select(project => project.GetString()));
            Assert.Equal("FullyQualifiedName~AppSurfaceKeycloakLinuxArm64WorkerTests",
                armLanes[1].GetProperty("filter").GetString());
        }
    }

    [Theory]
    [InlineData("--include-keycloak-native", "false")]
    [InlineData("--unexpected-option", "value")]
    [InlineData("--operation", "native-coverage-inventory")]
    public async Task InventoryOperationRejectsInvalidOrAmbiguousArguments(string option, string value)
    {
        var invocation = await InvokeAsync(["--operation", "native-coverage-inventory", option, value]);

        Assert.Equal(1, invocation.ExitCode);
        Assert.Empty(invocation.StandardOutput);
        Assert.Contains("Validation failed: ArgumentException.", invocation.StandardError, StringComparison.Ordinal);
    }

    private static async Task<InvocationResult> InvokeAsync(string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var originalOutput = System.Console.Out;
        var originalError = System.Console.Error;
        try
        {
            System.Console.SetOut(output);
            System.Console.SetError(error);
            var exitCode = await Program.RunAsync(args);
            return new InvocationResult(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            System.Console.SetOut(originalOutput);
            System.Console.SetError(originalError);
        }
    }

    private sealed record InvocationResult(int ExitCode, string StandardOutput, string StandardError);
}
