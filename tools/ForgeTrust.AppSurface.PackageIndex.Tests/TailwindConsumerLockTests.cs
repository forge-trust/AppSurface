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

    [Theory]
    [InlineData("wrong-version", "lock version 1")]
    [InlineData("null-version", "lock version 1")]
    [InlineData("root-array", "must be a JSON object")]
    [InlineData("missing-graph", "missing or unexpected fields")]
    [InlineData("null-graph", "no dependency graph")]
    [InlineData("missing-framework", "missing or unexpected fields")]
    [InlineData("null-framework", "no net10.0 graph")]
    [InlineData("extra-root-field", "missing or unexpected fields")]
    [InlineData("scalar-entry", "must be an object")]
    [InlineData("missing-entry-field", "missing or unexpected fields")]
    [InlineData("empty-resolved", "no resolved version")]
    [InlineData("null-resolved", "no resolved version")]
    [InlineData("null-dependencies", "invalid dependencies")]
    [InlineData("object-dependency", "invalid dependency version")]
    [InlineData("empty-dependency", "empty dependency version")]
    [InlineData("unbound-first-party-dependency", "unbound first-party dependency")]
    [InlineData("extra-first-party-requested", "unexpected dependency type")]
    [InlineData("missing-direct-requested", "unexpected dependency type")]
    [InlineData("direct-third-party", "unreviewed third-party")]
    [InlineData("requested-third-party", "unreviewed third-party")]
    [InlineData("null-hash", "invalid SHA-512")]
    [InlineData("short-hash", "invalid SHA-512")]
    public void Materialize_RejectsMalformedLockGraph(string mutation, string message)
    {
        Directory.CreateDirectory(_directory);
        var template = Path.Combine(_directory, "template.json");
        var output = Path.Combine(_directory, "packages.lock.json");
        var root = JsonNode.Parse(File.ReadAllBytes(ReviewedTemplate()))!.AsObject();
        var frameworks = root["dependencies"]!.AsObject();
        var graph = frameworks["net10.0"]!.AsObject();
        var direct = graph["ForgeTrust.AppSurface.Web.Tailwind"]!.AsObject();
        var core = graph["ForgeTrust.AppSurface.Core"]!.AsObject();
        var thirdParty = graph["CliWrap"]!.AsObject();
        switch (mutation)
        {
            case "wrong-version":
                root["version"] = 2;
                break;
            case "null-version":
                root["version"] = null;
                break;
            case "missing-graph":
                root.Remove("dependencies");
                break;
            case "null-graph":
                root["dependencies"] = null;
                break;
            case "missing-framework":
                frameworks.Remove("net10.0");
                break;
            case "null-framework":
                frameworks["net10.0"] = null;
                break;
            case "extra-root-field":
                root["unexpected"] = true;
                break;
            case "scalar-entry":
                graph["CliWrap"] = "3.10.1";
                break;
            case "missing-entry-field":
                thirdParty.Remove("resolved");
                break;
            case "empty-resolved":
                thirdParty["resolved"] = "";
                break;
            case "null-resolved":
                thirdParty["resolved"] = null;
                break;
            case "null-dependencies":
                direct["dependencies"] = null;
                break;
            case "object-dependency":
                direct["dependencies"]!["CliWrap"] = new JsonObject();
                break;
            case "empty-dependency":
                direct["dependencies"]!["CliWrap"] = "";
                break;
            case "unbound-first-party-dependency":
                direct["dependencies"]!["ForgeTrust.Unknown"] = "1.0.0";
                break;
            case "extra-first-party-requested":
                core["requested"] = "[0.0.0-ci.local, )";
                break;
            case "missing-direct-requested":
                direct.Remove("requested");
                break;
            case "direct-third-party":
                thirdParty["type"] = "Direct";
                break;
            case "requested-third-party":
                thirdParty["requested"] = "[3.10.1, )";
                break;
            case "null-hash":
                thirdParty["contentHash"] = null;
                break;
            case "short-hash":
                thirdParty["contentHash"] = Convert.ToBase64String(new byte[63]);
                break;
        }

        File.WriteAllText(template, mutation == "root-array" ? "[]" : root.ToJsonString());
        var error = Assert.Throws<PackageIndexException>(() => TailwindConsumerLock.Materialize(template, output, CandidatePackages()));
        Assert.Contains(message, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Materialize_RejectsDuplicateProducerPackageIdentity()
    {
        Directory.CreateDirectory(_directory);
        var output = Path.Combine(_directory, "packages.lock.json");
        var packages = CandidatePackages();
        var error = Assert.Throws<PackageIndexException>(() => TailwindConsumerLock.Materialize(ReviewedTemplate(), output, [packages[0], packages[0]]));
        Assert.Contains("duplicate identities", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Materialize_RejectsDuplicatePropertyNestedInsideArray()
    {
        Directory.CreateDirectory(_directory);
        var template = Path.Combine(_directory, "template.json");
        var output = Path.Combine(_directory, "packages.lock.json");
        File.WriteAllText(template, "{\"version\":1,\"unexpected\":[{\"id\":1,\"id\":2}],\"dependencies\":{}}");
        var error = Assert.Throws<PackageIndexException>(() => TailwindConsumerLock.Materialize(template, output, CandidatePackages()));
        Assert.Contains("duplicate JSON property", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Materialize_RejectsOversizedLockBeforeParsing()
    {
        Directory.CreateDirectory(_directory);
        var template = Path.Combine(_directory, "oversized.json");
        var output = Path.Combine(_directory, "packages.lock.json");
        using (var stream = File.Create(template)) stream.SetLength(16 * 1024 * 1024 + 1);

        var error = Assert.Throws<PackageIndexException>(() => TailwindConsumerLock.Materialize(template, output, CandidatePackages()));

        Assert.Contains("16 MiB JSON limit", error.Message, StringComparison.Ordinal);
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
