using System.Text.Json;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Tests;

/// <summary>
/// Proves the checked-in Issue 164 source fixture crosses the private semantic boundary without widening the public API.
/// </summary>
public sealed class Issue164CSharpSemanticContractTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "AppSurfaceDocsIssue164", Guid.NewGuid().ToString("N"));

    public Issue164CSharpSemanticContractTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Fixture_ShouldProjectTheExpectedTypedCSharpSemanticTree()
    {
        File.Copy(FixturePath("ApiFixtures.cs"), Path.Join(_root, "ApiFixtures.cs"));
        var harvester = new CSharpDocHarvester(NullLogger<CSharpDocHarvester>.Instance);

        var results = await harvester.HarvestAsync(CreateContext());

        var namespaceNode = Assert.Single(results, node => node.Path == "Namespaces/Issue164.Api");
        var document = Assert.IsType<CSharpNamespaceDocument>(namespaceNode.CSharpNamespaceDocument);
        var type = Assert.Single(document.Types);
        var methodGroup = Assert.Single(type.MethodGroups);
        var property = Assert.Single(type.Properties);
        var @enum = Assert.Single(document.Enums);

        Assert.Equal(string.Empty, namespaceNode.Content);
        Assert.Equal("FixtureService<TItem>", type.DisplayName);
        Assert.Contains(type.Documentation!.Sections, section => section.Kind == CSharpDocumentationSectionKind.TypeParameter);
        Assert.Equal("Process", methodGroup.Name);
        Assert.Equal(2, methodGroup.Overloads.Count);
        Assert.Equal("attempt", methodGroup.Overloads[0].Signature.Parameters[1].Name);
        Assert.Equal("1", methodGroup.Overloads[0].Signature.Parameters[1].DefaultValue);
        Assert.Equal("Name", property.Name);
        Assert.Equal("FixtureState", @enum.DisplayName);
        Assert.Contains("Hostile XML-like text: <script>must remain text</script>.", document.ReaderText, StringComparison.Ordinal);
        Assert.Empty(((IDocHarvesterDiagnosticProvider)harvester).GetHarvestDiagnostics());
    }

    [Fact]
    public void CompatibilityManifest_ShouldRecordTheStableReaderContracts()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(FixturePath("compatibility-manifest.json")));
        var root = manifest.RootElement;
        var requiredDom = root.GetProperty("requiredDom");

        Assert.Equal(164, root.GetProperty("issue").GetInt32());
        Assert.Equal("Namespaces/Issue164.Api", root.GetProperty("namespacePath").GetString());
        Assert.Equal(1, requiredDom.GetProperty("shellH1Count").GetInt32());
        Assert.True(requiredDom.GetProperty("firstOverloadOpen").GetBoolean());
        Assert.True(requiredDom.GetProperty("typedValuesAreEncoded").GetBoolean());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private DocHarvestContext CreateContext()
    {
        var configuredPolicy = AppSurfaceDocsHarvestPathPolicy.CreateDefault();
        var vcsIgnorePolicy = new AppSurfaceDocsHarvestVcsIgnorePolicy(
            _root,
            new AppSurfaceDocsHarvestVcsIgnoreOptions(),
            NullLogger.Instance);
        return new DocHarvestContext(
            _root,
            new AppSurfaceDocsHarvestPathPolicySnapshot(configuredPolicy, vcsIgnorePolicy));
    }

    private static string FixturePath(string name)
    {
        return Path.Join(AppContext.BaseDirectory, "TestData", "Issue164CSharpApi", name);
    }
}
