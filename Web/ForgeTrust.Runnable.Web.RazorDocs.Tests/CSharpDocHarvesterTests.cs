using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using FakeItEasy;
using ForgeTrust.Runnable.Web.RazorDocs.Services;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.Runnable.Web.RazorDocs.Tests;

public class CSharpDocHarvesterTests : IDisposable
{
    private readonly CSharpDocHarvester _harvester;
    private readonly string _testRoot;

    public CSharpDocHarvesterTests()
    {
        var loggerFake = A.Fake<ILogger<CSharpDocHarvester>>();
        _harvester = new CSharpDocHarvester(loggerFake);
        _testRoot = Path.Combine(Path.GetTempPath(), "RazorDocsTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testRoot);
    }

    [Fact]
    public async Task HarvestAsync_ShouldIgnoreExcludedDirectories()
    {
        // Arrange
        var binDir = Path.Combine(_testRoot, "bin");
        Directory.CreateDirectory(binDir);
        await File.WriteAllTextAsync(Path.Combine(binDir, "Ignored.cs"), "public class Ignored {}");

        var srcDir = Path.Combine(_testRoot, "src");
        Directory.CreateDirectory(srcDir);
        await File.WriteAllTextAsync(
            Path.Combine(srcDir, "Included.cs"),
            "/// <summary>Docs</summary>\npublic class Included {}");

        // Act
        var results = (await _harvester.HarvestAsync(_testRoot)).ToList();

        // Assert
        _ = results.Single(n => n.Path == "Namespaces" && n.Title == "Namespaces");
        _ = results.Single(n => n.Path == "Namespaces/Global" && n.Title == "Global");
        Assert.Contains(results, n => n.Title == "Included" && n.ParentPath == "Namespaces/Global");
        Assert.DoesNotContain(results, n => n.Title == "Ignored");
    }

    [Fact]
    public async Task HarvestAsync_ShouldIgnoreCommonAgentDirectories()
    {
        // Arrange
        var agentDir = Path.Combine(_testRoot, ".codex");
        Directory.CreateDirectory(agentDir);
        await File.WriteAllTextAsync(
            Path.Combine(agentDir, "AgentFile.cs"),
            "/// <summary>Should be skipped</summary>\npublic class AgentFile {}");

        await File.WriteAllTextAsync(
            Path.Combine(_testRoot, "Included.cs"),
            "/// <summary>Keep this</summary>\npublic class Included {}");

        // Act
        var results = (await _harvester.HarvestAsync(_testRoot)).ToList();

        // Assert
        _ = results.Single(n => n.Path == "Namespaces" && n.Title == "Namespaces");
        _ = results.Single(n => n.Path == "Namespaces/Global" && n.Title == "Global");
        Assert.Contains(results, n => n.Title == "Included" && n.ParentPath == "Namespaces/Global");
        Assert.DoesNotContain(results, n => n.Title == "AgentFile");
    }

    [Fact]
    public async Task HarvestAsync_ShouldIgnoreDotPrefixedDirectories_IncludingGithub()
    {
        // Arrange
        var hiddenDir = Path.Combine(_testRoot, ".github");
        Directory.CreateDirectory(hiddenDir);
        await File.WriteAllTextAsync(
            Path.Combine(hiddenDir, "Ignored.cs"),
            "/// <summary>Skip this</summary>\npublic class Ignored {}");

        await File.WriteAllTextAsync(
            Path.Combine(_testRoot, "Included.cs"),
            "/// <summary>Keep this</summary>\npublic class Included {}");

        // Act
        var results = (await _harvester.HarvestAsync(_testRoot)).ToList();

        // Assert
        _ = results.Single(n => n.Path == "Namespaces" && n.Title == "Namespaces");
        _ = results.Single(n => n.Path == "Namespaces/Global" && n.Title == "Global");
        Assert.Contains(results, n => n.Title == "Included" && n.ParentPath == "Namespaces/Global");
        Assert.DoesNotContain(results, n => n.Title == "Ignored");
    }

    [Fact]
    public async Task HarvestAsync_ShouldIncludeDotPrefixedFiles()
    {
        // Arrange
        await File.WriteAllTextAsync(
            Path.Combine(_testRoot, ".hidden.cs"),
            "/// <summary>Hidden docs</summary>\npublic class Hidden {}");

        // Act
        var results = (await _harvester.HarvestAsync(_testRoot)).ToList();

        // Assert
        _ = results.Single(n => n.Path == "Namespaces" && n.Title == "Namespaces");
        _ = results.Single(n => n.Path == "Namespaces/Global" && n.Title == "Global");
        Assert.Contains(results, n => n.Title == "Hidden" && n.ParentPath == "Namespaces/Global");
        var globalPage = results.Single(n => n.Path == "Namespaces/Global");
        Assert.Contains("Hidden docs", globalPage.Content);
    }

    [Fact]
    public async Task HarvestAsync_ShouldExtractDocumentationFromDifferentTypes()
    {
        // Arrange
        var code = @"
            namespace Test;
            
            /// <summary>Class Summary</summary>
            public class MyClass {}

            /// <summary>Record Summary</summary>
            public record MyRecord(int Id);

            /// <summary>Struct Summary</summary>
            public struct MyStruct {}

            /// <summary>Interface Summary</summary>
            public interface IMyInterface {}

            /// <summary>Enum Summary</summary>
            public enum MyEnum { A, B }
        ";
        await File.WriteAllTextAsync(Path.Combine(_testRoot, "Types.cs"), code);

        // Act
        var results = (await _harvester.HarvestAsync(_testRoot)).ToList();

        // Assert
        Assert.True(results.Count >= 7);

        var namespaceNode = results.Single(n => n.Path == "Namespaces/Test");
        Assert.Contains("Class Summary", namespaceNode.Content);
        Assert.Contains("Record Summary", namespaceNode.Content);
        Assert.Contains("Struct Summary", namespaceNode.Content);
        Assert.Contains("Interface Summary", namespaceNode.Content);
        Assert.Contains("Enum Summary", namespaceNode.Content);

        // Sub-nodes should exist but have empty content (navigation stubs)
        Assert.Contains(
            results,
            n => n.Title == "MyClass" && string.IsNullOrEmpty(n.Content) && n.ParentPath == "Namespaces/Test");
    }

    [Fact]
    public async Task HarvestAsync_ShouldAttachApiReferenceMetadataDefaults()
    {
        var code = """
            namespace ForgeTrust.Runnable.Web.RazorWire;

            /// <summary>Bridge docs.</summary>
            public class RazorWireBridge {}
            """;
        await File.WriteAllTextAsync(Path.Combine(_testRoot, "Bridge.cs"), code);

        var results = (await _harvester.HarvestAsync(_testRoot)).ToList();
        var namespaceNode = results.Single(n => n.Path == "Namespaces/ForgeTrust.Runnable.Web.RazorWire");
        var typeStub = results.Single(n => n.Title == "RazorWireBridge" && n.ParentPath == namespaceNode.Path);

        Assert.Equal("api-reference", namespaceNode.Metadata?.PageType);
        Assert.Equal("developer", namespaceNode.Metadata?.Audience);
        Assert.Equal("RazorWire", namespaceNode.Metadata?.Component);
        Assert.Equal("API Reference", namespaceNode.Metadata?.NavGroup);

        Assert.Equal("api-reference", typeStub.Metadata?.PageType);
        Assert.Equal("RazorWire", typeStub.Metadata?.Component);
    }

    [Fact]
    public async Task HarvestAsync_ShouldEmitTypedOutlineEntries_ForNamespacePages()
    {
        var code = """
            namespace Test;

            /// <summary>Type docs.</summary>
            public class Calculator
            {
                /// <summary>Add docs.</summary>
                public int Add(int left, int right) => left + right;

                /// <summary>Name docs.</summary>
                public string Name { get; } = "calc";
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(_testRoot, "Calculator.cs"), code);

        var results = (await _harvester.HarvestAsync(_testRoot)).ToList();
        var namespaceNode = results.Single(n => n.Path == "Namespaces/Test");

        Assert.NotNull(namespaceNode.Outline);
        Assert.Collection(
            namespaceNode.Outline!,
            type =>
            {
                Assert.Equal("Calculator", type.Title);
                Assert.Equal(2, type.Level);
            },
            method =>
            {
                Assert.Equal("Add", method.Title);
                Assert.Equal(3, method.Level);
            },
            property =>
            {
                Assert.Equal("Name", property.Title);
                Assert.Equal(3, property.Level);
            });
    }

    [Fact]
    public async Task HarvestAsync_ShouldEmitSymbolSourceProvenanceAndPlaceholders_ForDocumentedApiSymbols()
    {
        var code = """
            namespace Test;

            /// <summary>Type docs.</summary>
            public class Calculator
            {
                /// <summary>Add docs.</summary>
                public int Add(int left, int right) => left + right;

                /// <summary>Name docs.</summary>
                public string Name { get; } = "calc";
            }

            /// <summary>Mode docs.</summary>
            public enum Mode
            {
                Fast
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(_testRoot, "Calculator.cs"), code);

        var results = (await _harvester.HarvestAsync(_testRoot)).ToList();
        var namespaceNode = results.Single(n => n.Path == "Namespaces/Test");

        Assert.NotNull(namespaceNode.SymbolSourceProvenance);
        Assert.Collection(
            namespaceNode.SymbolSourceProvenance!,
            type =>
            {
                Assert.Equal("Test-Calculator", type.AnchorId);
                Assert.Equal("Calculator.cs", type.SourcePath);
                Assert.Equal(4, type.StartLine);
            },
            method =>
            {
                Assert.StartsWith("Test-Calculator-Add", method.AnchorId, StringComparison.Ordinal);
                Assert.Equal("Calculator.cs", method.SourcePath);
                Assert.Equal(7, method.StartLine);
            },
            property =>
            {
                Assert.StartsWith("Test-Calculator-string-Name", property.AnchorId, StringComparison.Ordinal);
                Assert.Equal("Calculator.cs", property.SourcePath);
                Assert.Equal(10, property.StartLine);
            },
            enumProvenance =>
            {
                Assert.Equal("Test-Mode", enumProvenance.AnchorId);
                Assert.Equal("Calculator.cs", enumProvenance.SourcePath);
                Assert.Equal(14, enumProvenance.StartLine);
            });

        Assert.Contains("data-razordocs-symbol-source=\"Test-Calculator\"", namespaceNode.Content);
        Assert.Contains("data-razordocs-symbol-source=\"Test-Mode\"", namespaceNode.Content);
        Assert.Equal(4, Regex.Matches(namespaceNode.Content, "data-razordocs-symbol-source=").Count);
    }

    [Fact]
    public async Task HarvestAsync_ShouldEmitTypeSourceProvenance_WhenOnlyMembersHaveDocs()
    {
        var code = """
            namespace Test;

            public class Calculator
            {
                /// <summary>Add docs.</summary>
                public int Add(int left, int right) => left + right;
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(_testRoot, "Calculator.cs"), code);

        var results = (await _harvester.HarvestAsync(_testRoot)).ToList();
        var namespaceNode = results.Single(n => n.Path == "Namespaces/Test");

        Assert.Contains("data-razordocs-symbol-source=\"Test-Calculator\"", namespaceNode.Content);
        Assert.Contains(
            namespaceNode.SymbolSourceProvenance!,
            provenance => provenance.AnchorId == "Test-Calculator"
                          && provenance.SourcePath == "Calculator.cs"
                          && provenance.StartLine > 0);
    }

    [Fact]
    public async Task HarvestAsync_ShouldHandleMalformedXmlGracefully()
    {
        // Arrange
        var code = @"
            /// <summary>Broken XML
            public class Broken {}
        ";
        await File.WriteAllTextAsync(Path.Combine(_testRoot, "Broken.cs"), code);

        // Act
        var results = await _harvester.HarvestAsync(_testRoot);

        // Assert
        // The harvester catches the exception and returns null from ExtractDoc,
        // so the node should just be skipped and not added to results.
        Assert.Empty(results);
    }

    [Fact]
    public async Task HarvestAsync_ShouldGenerateReadableSignaturesAndSafeAnchors()
    {
        // Arrange
        var code = @"
            namespace Test;
            public class SignatureTest {
                /// <summary>Method Docs</summary>
                public void MyMethod(int id, string name, ref bool flag) {}
            }
        ";
        await File.WriteAllTextAsync(Path.Combine(_testRoot, "Signatures.cs"), code);

        // Act
        var results = (await _harvester.HarvestAsync(_testRoot)).ToList();

        // Assert
        var namespaceNode = results.Single(n => n.Path == "Namespaces/Test");
        Assert.Contains("MyMethod", namespaceNode.Content);
        Assert.Contains("id=\"Test-SignatureTest-MyMethod-int-string-ref-bool\"", namespaceNode.Content);
    }

    [Fact]
    public async Task HarvestAsync_ShouldDifferentiateMethodOverloads()
    {
        // Arrange: Create a file with overloaded methods
        var testFile = Path.Combine(_testRoot, "Calculator.cs");
        await File.WriteAllTextAsync(
            testFile,
            @"
namespace TestNamespace;

public class Calculator
{
    /// <summary>Process an integer value.</summary>
    public void Process(int value) { }

    /// <summary>Process a reference to an integer.</summary>
    public void Process(ref int value) { }
}
");

        // Act
        var results = (await _harvester.HarvestAsync(_testRoot)).ToList();
        var namespaceNode = results.Single(n => n.Path == "Namespaces/TestNamespace");
        var overloadIdMatches = Regex.Matches(namespaceNode.Content, "<details id=\"TestNamespace-Calculator-Process");

        // Assert: Should have two distinct Process methods
        Assert.Equal(2, overloadIdMatches.Count);
        Assert.Contains("<span class=\"sig-type\">int</span> <span class=\"sig-parameter\">value</span>", namespaceNode.Content);
        Assert.Contains("<span class=\"sig-modifier\">ref</span> <span class=\"sig-type\">int</span> <span class=\"sig-parameter\">value</span>", namespaceNode.Content);
    }

    [Fact]
    public async Task HarvestAsync_ShouldUseDistinctAnchors_ForMethodGroupsAndParameterlessOverloads()
    {
        var testFile = Path.Combine(_testRoot, "Calculator.cs");
        await File.WriteAllTextAsync(
            testFile,
            """
            namespace TestNamespace;

            public class Calculator
            {
                /// <summary>Run the calculator.</summary>
                public void Run() { }
            }
            """);

        var results = (await _harvester.HarvestAsync(_testRoot)).ToList();
        var namespaceNode = results.Single(n => n.Path == "Namespaces/TestNamespace");

        Assert.Contains("id=\"TestNamespace-Calculator-Run-method-group\"", namespaceNode.Content);
        Assert.Contains("<details id=\"TestNamespace-Calculator-Run\"", namespaceNode.Content);
        Assert.Contains(
            namespaceNode.Outline!,
            item => item.Title == "Run"
                    && item.Id == "TestNamespace-Calculator-Run-method-group"
                    && item.Level == 3);
    }

    [Fact]
    public void AddOutlineItem_ShouldSkipIncompleteAndDuplicateEntries()
    {
        var namespacePage = new CSharpDocHarvester.NamespaceDocPage(
            "TestNamespace",
            "Namespaces/TestNamespace",
            "TestNamespace",
            DocMetadataFactory.CreateApiReferenceMetadata("TestNamespace", "TestNamespace"));

        CSharpDocHarvester.AddOutlineItem(namespacePage, "   ", "valid-id", level: 2);
        CSharpDocHarvester.AddOutlineItem(namespacePage, "Valid", "   ", level: 2);
        CSharpDocHarvester.AddOutlineItem(namespacePage, "Valid", "valid-id", level: 2);
        CSharpDocHarvester.AddOutlineItem(namespacePage, "Duplicate", "valid-id", level: 3);

        var item = Assert.Single(namespacePage.Outline);
        Assert.Equal("Valid", item.Title);
        Assert.Equal("valid-id", item.Id);
        Assert.Equal(2, item.Level);
    }

    [Fact]
    public async Task HarvestAsync_ShouldGenerateDistinctQualifiedAnchors_ForTypesWithSameName()
    {
        // Arrange
        var testFile = Path.Combine(_testRoot, "Collision.cs");
        await File.WriteAllTextAsync(
            testFile,
            @"
namespace NamespaceA
{
    /// <summary>Summary A</summary>
    public class SharedName {}
}

namespace NamespaceB
{
    /// <summary>Summary B</summary>
    public class SharedName {}
}
");

        // Act
        var results = (await _harvester.HarvestAsync(_testRoot)).ToList();

        var types = results.Where(n => n.Title == "SharedName").ToList();

        // Assert
        Assert.Equal(2, types.Count);
        Assert.NotEqual(types[0].Path, types[1].Path);

        // Verify qualified anchors in their PATHs (stubs)
        // One should be #NamespaceA-SharedName, other #NamespaceB-SharedName
        var pathA = types.Any(t => t.Path.EndsWith("#NamespaceA-SharedName"));
        var pathB = types.Any(t => t.Path.EndsWith("#NamespaceB-SharedName"));

        Assert.True(pathA, "Should contain anchor for NamespaceA.SharedName");
        Assert.True(pathB, "Should contain anchor for NamespaceB.SharedName");

        var namespaceA = results.Single(n => n.Path == "Namespaces/NamespaceA");
        var namespaceB = results.Single(n => n.Path == "Namespaces/NamespaceB");
        Assert.Contains("id=\"NamespaceA-SharedName\"", namespaceA.Content);
        Assert.Contains("id=\"NamespaceB-SharedName\"", namespaceB.Content);
    }

    [Fact]
    public async Task HarvestAsync_ShouldExtractRemarks_WhenPresent()
    {
        // Arrange
        var code = @"
            namespace Test;
            /// <summary>Summary</summary>
            /// <remarks>Remarks here</remarks>
            public class RemarksTest {}
        ";
        await File.WriteAllTextAsync(Path.Combine(_testRoot, "Remarks.cs"), code);

        // Act
        var results = (await _harvester.HarvestAsync(_testRoot)).ToList();

        var namespaceNode = results.Single(n => n.Path == "Namespaces/Test");

        // Assert
        Assert.Contains("<div class=\"doc-summary", namespaceNode.Content);
        Assert.Contains("Summary", namespaceNode.Content);
        Assert.Contains("<div class=\"doc-remarks", namespaceNode.Content);
        Assert.Contains("Remarks here", namespaceNode.Content);
    }

    [Fact]
    public async Task HarvestAsync_ShouldHandleNestedTypes()
    {
        // Arrange
        var code = @"
            namespace Test;
            public class Outer {
                /// <summary>Inner Summary</summary>
                public class Inner {}
            }
        ";
        await File.WriteAllTextAsync(Path.Combine(_testRoot, "Nested.cs"), code);

        // Act
        var results = (await _harvester.HarvestAsync(_testRoot)).ToList();
        var namespaceNode = results.Single(n => n.Path == "Namespaces/Test");

        // Assert
        // Check content for inner summary
        Assert.Contains("Inner Summary", namespaceNode.Content);

        // Check for nested ID: Test.Outer.Inner -> Test-Outer-Inner
        Assert.Contains("id=\"Test-Outer-Inner\"", namespaceNode.Content);
    }

    [Fact]
    public async Task HarvestAsync_ShouldHandleExceptionDuringFileProcessing()
    {
        // Arrange
        var filePath = Path.Combine(_testRoot, "Exception.cs");
        await File.WriteAllTextAsync(filePath, "docs");

        // Skip on Windows - this test requires Unix file permissions
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        // On macOS/Linux, we can use chmod 000
        File.SetUnixFileMode(filePath, UnixFileMode.None);

        // Act
        try
        {
            var results = await _harvester.HarvestAsync(_testRoot);

            // Assert
            Assert.Empty(results);
        }
        finally
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    [Fact]
    public async Task HarvestAsync_ShouldRenderStructuredXmlSections_AndNormalizeSignatures()
    {
        // Arrange
        var code = @"
using System.Runtime.CompilerServices;
namespace Test;

public class RichDocs
{
    /// <summary>
    /// Main <paramref name=""value""/> and <typeparamref name=""TResult""/> with
    /// <see cref=""T:System.String""/>, <see cref=""System.Int32""/>, <see langword=""null""/>,
    /// <see href=""https://example.com/docs""/>, <see>inline-see</see> and <see/>.
    /// Also <c>inline-code</c>.
    /// <para>Standalone paragraph.</para>
    /// <para> </para>
    /// <list type=""number"">
    /// <item><description>First</description></item>
    /// <item><description><paramref name=""value""/> second</description></item>
    /// </list>
    /// <list></list>
    /// <code>
    /// var answer = 42;
    /// </code>
    /// <unknown>fallback</unknown>
    /// <!-- coverage comment -->
    /// </summary>
    /// <typeparam name=""TResult"">Result type.</typeparam>
    /// <param name=""value""><para>Input value.</para></param>
    /// <param name=""callerFilePath"">Filtered path.</param>
    /// <param name=""callerLineNumber"">Filtered line.</param>
    /// <returns><code>return default;</code></returns>
    /// <exception cref=""T:System.InvalidOperationException"">Boom</exception>
    /// <remarks>Use <b>carefully</b>.</remarks>
    /// <example> </example>
    public TResult Compute<TResult>(int value = 42, [CallerFilePath] string source = """", [CallerLineNumber] int line = 0)
        => default!;

    /// <summary>Legacy path.</summary>
    public void Legacy(int value, [CallerFilePath] string callerFilePath = """", [CallerLineNumber] int callerLineNumber = 0) { }
}
";
        await File.WriteAllTextAsync(Path.Combine(_testRoot, "RichDocs.cs"), code);

        // Act
        var results = (await _harvester.HarvestAsync(_testRoot)).ToList();
        var namespaceNode = results.Single(n => n.Path == "Namespaces/Test");

        // Assert
        Assert.Contains("<div class=\"doc-summary\">", namespaceNode.Content);
        Assert.Contains("<div class=\"doc-typeparams\">", namespaceNode.Content);
        Assert.Contains("<div class=\"doc-params\">", namespaceNode.Content);
        Assert.Contains("<div class=\"doc-returns\">", namespaceNode.Content);
        Assert.Contains("<div class=\"doc-exceptions\">", namespaceNode.Content);
        Assert.Contains("<div class=\"doc-remarks\">", namespaceNode.Content);

        // Inline and block XML tags are transformed into readable HTML.
        Assert.Contains("<code>value</code>", namespaceNode.Content);
        Assert.Contains("<code>TResult</code>", namespaceNode.Content);
        Assert.Contains("<code>System.String</code>", namespaceNode.Content);
        Assert.Contains("<code>System.Int32</code>", namespaceNode.Content);
        Assert.Contains("<code>null</code>", namespaceNode.Content);
        Assert.Contains("<code>https://example.com/docs</code>", namespaceNode.Content);
        Assert.Contains("<code>inline-see</code>", namespaceNode.Content);
        Assert.Contains("<code>inline-code</code>", namespaceNode.Content);
        Assert.Contains("<ol>", namespaceNode.Content);
        Assert.Contains("<li>First</li>", namespaceNode.Content);
        Assert.Contains("<pre><code>var answer = 42;</code></pre>", namespaceNode.Content);
        Assert.Contains("fallback", namespaceNode.Content);

        // Compiler-injected doc params are filtered from the rendered parameter table.
        Assert.DoesNotContain("<code>callerFilePath</code>", namespaceNode.Content);
        Assert.DoesNotContain("<code>callerLineNumber</code>", namespaceNode.Content);

        // Display signature hides caller metadata parameters while preserving defaults.
        Assert.Contains("TResult", namespaceNode.Content);
        Assert.Contains("Compute", namespaceNode.Content);
        Assert.Contains("Legacy", namespaceNode.Content);
        Assert.Contains("<span class=\"sig-type\">int</span> <span class=\"sig-parameter\">value</span>", namespaceNode.Content);
    }

    [Fact]
    public async Task HarvestAsync_ShouldRenderEmptyNamedKeyRows_WithoutCodeLabel()
    {
        // Arrange
        var code = @"
namespace Test;
public class EmptyKeyDoc
{
    /// <summary>Summary</summary>
    /// <param>Unnamed parameter docs.</param>
    public void Method(int value) { }
}
";
        await File.WriteAllTextAsync(Path.Combine(_testRoot, "EmptyKeyDoc.cs"), code);

        // Act
        var results = (await _harvester.HarvestAsync(_testRoot)).ToList();
        var namespaceNode = results.Single(n => n.Path == "Namespaces/Test");

        // Assert
        Assert.Contains("<div class=\"doc-params\">", namespaceNode.Content);
        Assert.DoesNotContain("<code></code>", namespaceNode.Content);
        Assert.Contains("Unnamed parameter docs.", namespaceNode.Content);
    }

    [Fact]
    public async Task HarvestAsync_ShouldRenderDocumentedProperties_WithHighlightedSignatures()
    {
        // Arrange
        var code = @"
namespace Test;
public class PropertyDocs
{
    /// <summary>Count docs.</summary>
    public int Count { get; set; }

    /// <summary>Name docs.</summary>
    public string Name => ""value"";
}
";
        await File.WriteAllTextAsync(Path.Combine(_testRoot, "PropertyDocs.cs"), code);

        // Act
        var results = (await _harvester.HarvestAsync(_testRoot)).ToList();
        var namespaceNode = results.Single(n => n.Path == "Namespaces/Test");

        // Assert
        Assert.Contains("<span class=\"doc-kind\">Property</span>", namespaceNode.Content);
        Assert.Contains("<span class=\"sig-type\">int</span> <span class=\"sig-parameter\">Count</span> <span class=\"sig-operator\">{ get; set; }</span>", namespaceNode.Content);
        Assert.Contains("<span class=\"sig-type\">string</span> <span class=\"sig-parameter\">Name</span> <span class=\"sig-operator\">{ get; }</span>", namespaceNode.Content);
        Assert.Contains("Count docs.", namespaceNode.Content);
        Assert.Contains("Name docs.", namespaceNode.Content);
    }

    [Fact]
    public async Task HarvestAsync_ShouldHandleXmlEdgeCases_AndSkipEmptyDocPayloads()
    {
        // Arrange
        var code = @"
using System.Runtime.CompilerServices;
namespace Test;
public class EdgeCases
{
    /// <summary>
    /// Edge content <paramref/> with <typeparamref/>.
    /// <list>
    /// <item>Raw list entry</item>
    /// </list>
    /// </summary>
    /// <typeparam>Unnamed generic docs.</typeparam>
    /// <param name=""value"">Value docs.</param>
    /// <exception>Unknown failure.</exception>
    public void Mixed<T>(int value) { }

    /// <summary>Suffix caller attributes.</summary>
    public void SuffixAttrs([CallerFilePathAttribute] string source = """", [CallerLineNumberAttribute] int line = 0) { }

    /// <param name=""callerFilePath"">Ignored path.</param>
    /// <param name=""callerLineNumber"">Ignored line.</param>
    public void Filtered([CallerFilePath] string callerFilePath = """", [CallerLineNumber] int callerLineNumber = 0) { }
}
";
        await File.WriteAllTextAsync(Path.Combine(_testRoot, "EdgeCases.cs"), code);

        // Act
        var results = (await _harvester.HarvestAsync(_testRoot)).ToList();
        var namespaceNode = results.Single(n => n.Path == "Namespaces/Test");

        // Assert
        Assert.Contains("Mixed", namespaceNode.Content);
        Assert.Contains("SuffixAttrs", namespaceNode.Content);
        Assert.Contains("Raw list entry", namespaceNode.Content);
        Assert.Contains("Unknown failure.", namespaceNode.Content);
        Assert.DoesNotContain("<code></code>", namespaceNode.Content);
        // Filtered should not appear: it only contains compiler-generated caller metadata docs.
        Assert.DoesNotContain("id=\"Test-EdgeCases-Filtered", namespaceNode.Content);
    }

    [Fact]
    public void PrivateHelpers_ShouldHandleNullAndWhitespaceBranches()
    {
        // Arrange
        var typelessParameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier("value"));
        var typelessMethod = SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                "Compute")
            .WithParameterList(
                SyntaxFactory.ParameterList(
                    SyntaxFactory.SingletonSeparatedList(typelessParameter)));

        // Act: typeless method parameter falls back to object.
        var signatureResult = CSharpDocHarvester.GetMethodId(typelessMethod, "Test.EdgeCases");
        var signatureBuilder = new StringBuilder();
        CSharpDocHarvester.AppendHighlightedParameter(signatureBuilder, typelessParameter);

        var propertyWithoutAccessor = SyntaxFactory.PropertyDeclaration(
            SyntaxFactory.ParseTypeName("int"),
            "Count");
        var propertyWithEmptyAccessorList = propertyWithoutAccessor.WithAccessorList(SyntaxFactory.AccessorList());
        var nullAccessorSignature = CSharpDocHarvester.GetPropertyAccessorSignature(propertyWithoutAccessor);
        var emptyAccessorSignature = CSharpDocHarvester.GetPropertyAccessorSignature(propertyWithEmptyAccessorList);

        var simplifiedShortCref = CSharpDocHarvester.SimplifyCref("T:");
        var rootNamespacePath = CSharpDocHarvester.BuildNamespaceDocPath("   ");

        var namespacePages = new Dictionary<string, CSharpDocHarvester.NamespaceDocPage>(StringComparer.OrdinalIgnoreCase);
        var namespacePage = CSharpDocHarvester.GetOrCreateNamespacePage(namespacePages, "   ");
        var namespacePath = namespacePage.Path;

        // Assert
        Assert.Contains("object", signatureResult);
        Assert.Contains("<span class=\"sig-type\">object</span>", signatureBuilder.ToString());
        Assert.Equal(string.Empty, nullAccessorSignature);
        Assert.Equal(string.Empty, emptyAccessorSignature);
        Assert.Null(simplifiedShortCref);
        Assert.Equal("Namespaces", rootNamespacePath);
        Assert.Equal("Namespaces/Global", namespacePath);
    }

    [Fact]
    public async Task HarvestAsync_ShouldRenderPropertySignatureWithoutOperator_WhenAccessorListIsEmpty()
    {
        // Arrange
        var code = @"
namespace Test;
public class BrokenPropertyDocs
{
    /// <summary>Broken property docs.</summary>
    public int Value { }
}
";
        await File.WriteAllTextAsync(Path.Combine(_testRoot, "BrokenPropertyDocs.cs"), code);

        // Act
        var results = (await _harvester.HarvestAsync(_testRoot)).ToList();
        var namespaceNode = results.Single(n => n.Path == "Namespaces/Test");

        // Assert
        Assert.Contains("<span class=\"sig-type\">int</span> <span class=\"sig-parameter\">Value</span>", namespaceNode.Content);
        Assert.Contains("Broken property docs.", namespaceNode.Content);
    }

    [Fact]
    public async Task HarvestAsync_ShouldRenderExplicitInterfaceMethodSignature()
    {
        // Arrange
        var code = @"
namespace Test;
public interface IRunner
{
    void Run();
}

public class Runner : IRunner
{
    /// <summary>Runs explicitly.</summary>
    void IRunner.Run() { }
}
";
        await File.WriteAllTextAsync(Path.Combine(_testRoot, "Runner.cs"), code);

        // Act
        var results = (await _harvester.HarvestAsync(_testRoot)).ToList();
        var namespaceNode = results.Single(n => n.Path == "Namespaces/Test");

        // Assert
        Assert.Contains("<span class=\"sig-type\">IRunner.</span>", namespaceNode.Content);
        Assert.Contains("<span class=\"sig-method\">Run</span>", namespaceNode.Content);
        Assert.Contains("Runs explicitly.", namespaceNode.Content);
    }

    [Fact]
    public async Task HarvestAsync_ShouldCreateIntermediateNamespacePages_AndRenderGenericTypeName()
    {
        // Arrange
        var code = @"
namespace Root.Middle.Leaf;
/// <summary>Generic docs.</summary>
public class GenericThing<TItem> {}
";
        await File.WriteAllTextAsync(Path.Combine(_testRoot, "GenericThing.cs"), code);

        // Act
        var results = (await _harvester.HarvestAsync(_testRoot)).ToList();
        var rootNode = results.Single(n => n.Path == "Namespaces/Root");
        var middleNode = results.Single(n => n.Path == "Namespaces/Root.Middle");
        var leafNode = results.Single(n => n.Path == "Namespaces/Root.Middle.Leaf");

        // Assert
        Assert.Contains("/Namespaces/Root.Middle.html", rootNode.Content);
        Assert.Contains("/Namespaces/Root.Middle.Leaf.html", middleNode.Content);
        Assert.Contains("<h2>GenericThing&lt;TItem&gt;</h2>", leafNode.Content);
    }

    [Fact]
    public async Task HarvestAsync_ShouldCreateGlobalNamespacePage_ForTypesWithoutNamespace()
    {
        // Arrange
        var code = @"
/// <summary>Global docs.</summary>
public class GlobalType {}
";
        await File.WriteAllTextAsync(Path.Combine(_testRoot, "GlobalType.cs"), code);

        // Act
        var results = (await _harvester.HarvestAsync(_testRoot)).ToList();
        var globalNode = results.Single(n => n.Path == "Namespaces/Global");

        // Assert
        Assert.Equal("Global", globalNode.Title);
        Assert.Contains("Global docs.", globalNode.Content);
    }

    [Fact]
    public void GetNamespaceTitle_ShouldReturnNamespaces_ForEmptyNamespace()
    {
        var title = CSharpDocHarvester.GetNamespaceTitle(string.Empty);

        Assert.Equal("Namespaces", title);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try
            {
                Directory.Delete(_testRoot, true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }
}
