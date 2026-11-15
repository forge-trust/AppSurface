using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageFanInNativeCommandTests
{
    private static CoverageCaptureRequest Request()
    {
        var policy = CoverageFanInPolicyTests.Policy() with
        {
            Supplements = [new("native", "Linux", "Category=native")]
        };
        return new(policy, "/source", "/capture", "native", TimingsPath: "/producer/timings.json",
            RawCoveragePaths: new Dictionary<string, string>
            {
                [policy.TestProjects[0]] = "/producer/first.xml",
                [policy.TestProjects[1]] = "/producer/second.xml"
            });
    }

    private static JsonObject Envelope() => JsonSerializer.SerializeToNode(
        new { schemaVersion = 2, operation = "capture", request = Request() }, CoveragePolicyEncoding.Json)!.AsObject();

    [Fact]
    public void Indexed_native_request_round_trips_the_exact_project_map_and_selectors()
    {
        var parsed = CoverageFanInRequestReader.Parse(JsonSerializer.SerializeToUtf8Bytes(Envelope()));
        CoverageFanInRequestReader.ValidateCanonicalPolicy(parsed);
        CoverageFanInRequestReader.ValidateOperationRequest(parsed);
        var request = Assert.IsType<CoverageCaptureRequest>(parsed.Request);
        Assert.Equal(Request().RawCoveragePaths, request.RawCoveragePaths);
        Assert.Null(request.CoveragePath);
        Assert.Null(request.RawCoveragePath);
        Assert.Equal("/producer/timings.json", request.TimingsPath);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"wrong\"")]
    [InlineData("{\"\":\"/raw.xml\"}")]
    [InlineData("{\"project.csproj\":null}")]
    [InlineData("{\"project.csproj\":42}")]
    [InlineData("{\"project.csproj\":\" \"}")]
    public void Indexed_native_cli_rejects_non_object_maps_and_non_text_paths(string json)
    {
        var envelope = Envelope();
        envelope["request"]!["rawCoveragePaths"] = JsonNode.Parse(json);
        Assert.Throws<CoverageFanInCliInputException>(() =>
            CoverageFanInRequestReader.Parse(JsonSerializer.SerializeToUtf8Bytes(envelope)));
    }

    [Theory]
    [InlineData("primary")]
    [InlineData("coverage")]
    [InlineData("raw")]
    [InlineData("timings")]
    [InlineData("import")]
    public void Indexed_native_cli_rejects_conflicting_capture_selectors(string scenario)
    {
        var request = Request();
        request = scenario switch
        {
            "primary" => request with { ProducerId = "primary" },
            "coverage" => request with { CoveragePath = "/normalized.xml" },
            "raw" => request with { RawCoveragePath = "/raw.xml" },
            "timings" => request with { TimingsPath = null },
            _ => request with { ImportDirectory = "/transport", OriginalSourceRoot = "/source" }
        };
        Assert.Throws<CoverageFanInCliInputException>(() =>
            CoverageFanInRequestReader.ValidateOperationRequest(new("capture", request)));
    }

    [Fact]
    public void Native_policy_reserves_metadata_capacity_in_the_existing_storage_inventory()
    {
        var projects = Enumerable.Range(0, 9995).Select(index => $"checks/P{index}.csproj").ToArray();
        var policy = Request().Policy with { TestProjects = projects };
        Assert.Equal(9995, CoveragePolicyEncoding.Freeze(policy).TestProjects.Count);
        var tooMany = projects.Append("checks/Extra.csproj").ToArray();
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(policy with { TestProjects = tooMany }));
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(policy with
        { Supplements = [policy.Supplements[0] with { TestProjects = tooMany }] }));
        Assert.Equal(9995, CoveragePolicyEncoding.Freeze(policy with
        { Supplements = [policy.Supplements[0] with { TestProjects = projects }] }).Supplements[0].TestProjects!.Count);
    }
}
