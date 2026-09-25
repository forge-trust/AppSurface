using System.Text.Json.Nodes;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class TailwindConsumerLockTests : IDisposable
{
    private readonly string _directory = TestPathUtils.PathUnder(TailwindTestPaths.TemporaryRoot, "tailwind-consumer-lock", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Materialize_PreservesReviewedThirdPartyGraphAndBindsProducerArchives()
    {
        Directory.CreateDirectory(_directory);
        var output = Path.Combine(_directory, "packages.lock.json");
        var bytes = TailwindConsumerLock.Materialize(ReviewedTemplate(), output, CandidatePackages());
        var reviewed = JsonNode.Parse(File.ReadAllBytes(ReviewedTemplate()))!["dependencies"]!["net10.0"]!.AsObject();
        var actual = JsonNode.Parse(bytes)!["dependencies"]!["net10.0"]!.AsObject();

        Assert.Equal(reviewed.Count, actual.Count);
        foreach (var (id, entry) in reviewed.Where(item => !item.Key.StartsWith("ForgeTrust.", StringComparison.OrdinalIgnoreCase)))
            Assert.True(JsonNode.DeepEquals(entry, actual[id]), $"Third-party lock entry '{id}' changed.");
        Assert.Equal("9.8.7", actual["ForgeTrust.AppSurface.Web.Tailwind"]!["resolved"]!.GetValue<string>());
        Assert.Equal("[9.8.7, )", actual["ForgeTrust.AppSurface.Web.Tailwind"]!["requested"]!.GetValue<string>());
        Assert.Equal("9.8.7", actual["ForgeTrust.AppSurface.Web.Tailwind"]!["dependencies"]!["ForgeTrust.AppSurface.Core"]!.GetValue<string>());
        foreach (var package in CandidatePackages())
            Assert.Equal(Convert.ToBase64String(Convert.FromHexString(package.PackageSha512)), actual[package.PackageId]!["contentHash"]!.GetValue<string>());
        TailwindConsumerLock.RequireUnchanged(output, bytes);
        File.AppendAllText(output, " ");
        Assert.Throws<PackageIndexException>(() => TailwindConsumerLock.RequireUnchanged(output, bytes));
    }

    [Theory]
    [InlineData("missing-first-party", "first-party closure")]
    [InlineData("extra-third-party", "unreviewed third-party")]
    [InlineData("bad-third-party-hash", "invalid SHA-512")]
    [InlineData("bad-first-party-type", "unexpected dependency type")]
    [InlineData("duplicate-property", "duplicate JSON property")]
    public void Materialize_RejectsUnreviewedOrAmbiguousTemplate(string mutation, string message)
    {
        Directory.CreateDirectory(_directory);
        var template = Path.Combine(_directory, "template.json");
        var output = Path.Combine(_directory, "packages.lock.json");
        var root = JsonNode.Parse(File.ReadAllBytes(ReviewedTemplate()))!.AsObject();
        var graph = root["dependencies"]!["net10.0"]!.AsObject();
        switch (mutation)
        {
            case "missing-first-party":
                graph.Remove("ForgeTrust.AppSurface.Core");
                break;
            case "extra-third-party":
                graph["Unreviewed.Package"] = new JsonObject
                {
                    ["type"] = "Transitive",
                    ["resolved"] = "1.0.0",
                    ["contentHash"] = Convert.ToBase64String(new byte[64])
                };
                break;
            case "bad-third-party-hash":
                graph["CliWrap"]!["contentHash"] = "not-a-sha512";
                break;
            case "bad-first-party-type":
                graph["ForgeTrust.AppSurface.Core"]!["type"] = "Direct";
                break;
            case "duplicate-property":
                File.WriteAllText(template, "{\"version\":1,\"version\":1,\"dependencies\":{}}");
                break;
        }
        if (mutation != "duplicate-property") File.WriteAllText(template, root.ToJsonString());

        var error = Assert.Throws<PackageIndexException>(() => TailwindConsumerLock.Materialize(template, output, CandidatePackages()));
        Assert.Contains(message, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
    }

    private static TailwindSubjectPackage[] CandidatePackages() =>
    [
        new("ForgeTrust.AppSurface.Web.Tailwind", "9.8.7", "ForgeTrust.AppSurface.Web.Tailwind.9.8.7.nupkg", new string('a', 128)),
        new("ForgeTrust.AppSurface.Core", "9.8.7", "ForgeTrust.AppSurface.Core.9.8.7.nupkg", new string('b', 128))
    ];

    private static string ReviewedTemplate()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(TestPathUtils.PathUnder(current.FullName, TailwindConsumerLock.RelativeTemplatePath)))
            current = current.Parent;
        return TestPathUtils.PathUnder(current?.FullName ?? throw new DirectoryNotFoundException("Could not find the reviewed native consumer lock."), TailwindConsumerLock.RelativeTemplatePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
