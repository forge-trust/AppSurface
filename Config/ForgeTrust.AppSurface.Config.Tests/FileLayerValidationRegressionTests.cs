using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class FileLayerValidationRegressionTests
{
    [Theory]
    [InlineData("00")]
    [InlineData("000")]
    [InlineData("01")]
    [InlineData("+0")]
    [InlineData("-0")]
    [InlineData("2147483648")]
    public void NoncanonicalArrayIndexesCannotSelectValuesBehindCollisionMarkers(string index)
    {
        using var files = new Files("""
            {"Items":[{"Port":1,"Port":2},{"Port":3}],"Literal":{"00":{"Port":4}}}
            """);
        var request = Request($"Items:{index}:Port");
        Assert.Equal(ConfigProviderValueStatus.Missing, files.Provider.Resolve<int>(request).Status);
        var audit = Audit(files.Provider, request.Key.Value);
        Assert.Equal(ConfigAuditEntryState.Missing, audit.State);
        Assert.Null(audit.Value);
        Assert.All(audit.Sources, source => Assert.Equal(ConfigAuditSourceKind.Missing, source.Kind));
        Assert.Equal("config-key-collision", files.Provider.Resolve<int>(Request("Items:0:Port")).Diagnostic!.Code);
        Assert.Equal(4, files.Provider.Resolve<int>(Request("Literal:00:Port")).Value);
    }

    [Theory]
    [InlineData("\"Container\":0,\"Container\":1")]
    [InlineData("\"Container\":[{\"Port\":1,\"Port\":2}]")]
    [InlineData("\"Container\":{\"Port\":1},\"Container\":{\"Port\":2}")]
    public void InvalidHigherLayerCannotReplaceLowerShapeOrRemoveItsOrigins(string invalid)
    {
        using var files = new Files("""{"Container":{"Stable":"keep"},"Sibling":1}""");
        files.Add("config_z.json", "{" + invalid + ",\"Sibling\":2}");

        AssertRetained(files.Provider, "Container:Stable", "keep", "appsettings.json");
        Assert.Equal(2, files.Provider.Resolve<int>(Request("Sibling")).Value);
        Assert.Equal("config-key-collision", files.Provider.Resolve<object>(Request("Container")).Diagnostic!.Code);
        var sibling = Audit(files.Provider, "Sibling");
        Assert.Equal(new[] { "config_z.json", "appsettings.json" }, sibling.Sources.Select(source => Path.GetFileName(source.FilePath)));
        Assert.All(sibling.Sources, source => Assert.NotNull(source.Location));
    }

    [Theory]
    [InlineData("\"Node\":0,\"Node\":1")]
    [InlineData("\"node\":0")]
    public void InvalidNestedReplacementPreservesLowerDescendantsAndMergesValidSibling(string invalid)
    {
        using var files = new Files("""{"Container":{"Node":{"Stable":"keep"},"Sibling":1}}""");
        files.Add("config_z.json", "{\"Container\":{" + invalid + ",\"Sibling\":2}}");

        AssertRetained(files.Provider, "Container:Node:Stable", "keep", "appsettings.json");
        Assert.Equal(2, files.Provider.Resolve<int>(Request("Container:Sibling")).Value);
        Assert.Equal("config-key-collision", files.Provider.Resolve<object>(Request("Container")).Diagnostic!.Code);
        Assert.Equal("config-key-collision", files.Provider.Resolve<object>(Request("Container:Node")).Diagnostic!.Code);
        Assert.Equal(new[] { "config_z.json", "appsettings.json" }, Audit(files.Provider, "Container:Sibling").Sources.Select(source => Path.GetFileName(source.FilePath)));
    }

    [Theory]
    [InlineData("\"Port\":1,\"Port\":2", "config-key-collision")]
    [InlineData("\"Bad:Key\":1", "config-key-unrepresentable")]
    public void InvalidObjectCannotReplaceLowerArray(string invalid, string code)
    {
        using var files = new Files("""{"Container":[{"Stable":"keep"}],"Sibling":1}""");
        files.Add("config_z.json", "{\"Container\":{" + invalid + "},\"Sibling\":2}");

        AssertRetained(files.Provider, "Container:0:Stable", "keep", "appsettings.json");
        Assert.Equal(code, files.Provider.Resolve<object>(Request("Container")).Diagnostic!.Code);
        Assert.Equal(2, files.Provider.Resolve<int>(Request("Sibling")).Value);
    }

    [Fact]
    public void InvalidFirstLayerKeepsValidNestedSiblingsAndCollisionDiscoveryAcrossLaterLayers()
    {
        using var files = new Files("""{"Container":{"Bad":1,"Bad":2,"Sibling":"keep"},"BadRoot":1,"BadRoot":2}""");
        files.Add("config_z.json", """{"Container":{"Other":3},"BadRoot":4}""");

        AssertRetained(files.Provider, "Container:Sibling", "keep", "appsettings.json");
        Assert.Equal(3, files.Provider.Resolve<int>(Request("Container:Other")).Value);
        var discovered = ((IConfigAuditKeyEnumerator)files.Provider).EnumerateKeys("Production");
        foreach (var key in new[] { "Container", "Container:Bad", "BadRoot" })
        {
            var entry = Assert.Single(discovered, entry => entry.LogicalKey.Equals(AppSurfaceConfigKey.Parse(key)));
            Assert.Null(entry.RawValue);
            Assert.Contains(entry.Diagnostics, diagnostic => diagnostic.Code == "config-key-collision");
            Assert.Equal("config-key-collision", files.Provider.Resolve<object>(Request(key)).Diagnostic!.Code);
        }
        var badRoot = Assert.Single(discovered, entry => entry.LogicalKey.Value == "BadRoot");
        Assert.Equal(new[] { "config_z.json", "appsettings.json", "appsettings.json" }, badRoot.Sources.Select(source => Path.GetFileName(source.FilePath)));
    }

    [Theory]
    [InlineData("{\"Items\":[{\"Port\":1,\"Port\":2}]}", ConfigAuditDiscoveredValueKind.Array)]
    [InlineData("{\"Items\":{\"Port\":1},\"Items\":{\"Port\":2}}", ConfigAuditDiscoveredValueKind.Object)]
    public void RejectedAggregatesRemainDiscoverableWithoutPublishingTheirValues(string json, ConfigAuditDiscoveredValueKind kind)
    {
        using var files = new Files(json);
        var discovered = ((IConfigAuditKeyEnumerator)files.Provider).EnumerateKeys("Production");
        var entry = Assert.Single(discovered, entry => entry.LogicalKey.Value == "Items");
        Assert.Equal(kind, entry.ValueKind);
        Assert.Null(entry.RawValue);
        Assert.Contains(entry.Diagnostics, diagnostic => diagnostic.Code == "config-key-collision");
        Assert.All(entry.Sources, source => Assert.Equal("appsettings.json", Path.GetFileName(source.FilePath)));
        Assert.Equal(ConfigProviderValueStatus.Terminal, files.Provider.Resolve<object>(Request("Items")).Status);
    }

    private static void AssertRetained(FileBasedConfigProvider provider, string key, string expected, string sourceFile)
    {
        var value = provider.Resolve<string>(Request(key));
        Assert.Equal(ConfigProviderValueStatus.Found, value.Status);
        Assert.Equal(expected, value.Value);
        var audit = Audit(provider, key);
        Assert.Equal(ConfigAuditEntryState.Resolved, audit.State);
        Assert.Equal(expected, Assert.IsType<JsonElement>(audit.Value).GetString());
        var source = Assert.Single(audit.Sources);
        Assert.Equal(sourceFile, Path.GetFileName(source.FilePath));
        Assert.Equal(key, source.ConfigPath);
        Assert.NotNull(source.Location);
    }

    private static ConfigProviderRequest Request(string key) => new("Production", AppSurfaceConfigKey.Parse(key));
    private static ConfigValueResolution Audit(FileBasedConfigProvider provider, string key) =>
        ((IConfigDiagnosticProvider)provider).Resolve(Request(key), typeof(JsonElement), ConfigAuditSourceRole.Base);

    private sealed class Files : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "config-layer-regression-" + Guid.NewGuid().ToString("N"));
        internal FileBasedConfigProvider Provider { get; }

        internal Files(string json)
        {
            Directory.CreateDirectory(_directory);
            Add("appsettings.json", json);
            Provider = new FileBasedConfigProvider(new Location(_directory), NullLogger<FileBasedConfigProvider>.Instance);
        }

        internal void Add(string name, string json) => File.WriteAllText(Path.Combine(_directory, name), json);
        public void Dispose() => Directory.Delete(_directory, true);
    }

    private sealed record Location(string Directory) : IConfigFileLocationProvider;
}
