using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class DocPolyglotOwnershipLinkerTests
{
    [Fact]
    public void Link_CreatesReciprocalLinksForOneAcceptedModuleAndOwner()
    {
        var python = CreatePythonModule(
            "api/python/sidecar-worker",
            DocPolyglotOwnershipLinker.CreatePythonModuleMarker("sidecar/worker.py"));
        var csharp = CreateCSharpOwner(
            "Namespaces/Sample.Host",
            DocPolyglotOwnershipLinker.CreateCSharpOwnerMarker("sidecar/worker.py", "Sample-Host-Worker", "Worker Host"));

        var linked = DocPolyglotOwnershipLinker.Link([python, csharp], "/docs");

        Assert.Contains("href=\"/docs/Namespaces/Sample.Host#Sample-Host-Worker\"", linked[0].Content, StringComparison.Ordinal);
        Assert.Contains(">Worker Host</a>", linked[0].Content, StringComparison.Ordinal);
        Assert.Contains("href=\"/docs/api/python/sidecar-worker\"", linked[1].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-appsurfacedocs-python-", linked[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-appsurfacedocs-python-", linked[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Link_RemovesAmbiguousAndUnmatchedMarkersWithoutChangingUnrelatedNodes()
    {
        var firstPython = CreatePythonModule(
            "api/python/first",
            DocPolyglotOwnershipLinker.CreatePythonModuleMarker("sidecar/worker.py")
            + DocPolyglotOwnershipLinker.CreatePythonModuleMarker("sidecar/worker.py"));
        var secondPython = CreatePythonModule(
            "api/python/second",
            DocPolyglotOwnershipLinker.CreatePythonModuleMarker("sidecar/worker.py"));
        var unmatchedOwner = CreateCSharpOwner(
            "Namespaces/Sample.Host",
            DocPolyglotOwnershipLinker.CreateCSharpOwnerMarker("sidecar/missing.py", "Sample-Host-Worker", "Worker Host"));
        var ordinary = new DocNode("Guide", "guides/guide.md", "<p>Guide</p>");

        var linked = DocPolyglotOwnershipLinker.Link([firstPython, secondPython, unmatchedOwner, ordinary], "/docs");

        Assert.All(linked.Take(3), node => Assert.DoesNotContain("data-appsurfacedocs-python-", node.Content, StringComparison.Ordinal));
        Assert.Same(ordinary, linked[3]);
    }

    [Fact]
    public void Link_IgnoresMarkersOnNodesWithoutTheRequiredApiMetadata()
    {
        var nonPython = new DocNode(
            "Not Python",
            "api/python/not-python",
            DocPolyglotOwnershipLinker.CreatePythonModuleMarker("sidecar/worker.py"),
            Metadata: new DocMetadata { CodeLanguage = "csharp", PageType = "python-module" });
        var nonCSharp = new DocNode(
            "Not C#",
            "Namespaces/NotCSharp",
            DocPolyglotOwnershipLinker.CreateCSharpOwnerMarker("sidecar/worker.py", "NotCSharp", "Not C#"),
            Metadata: new DocMetadata { CodeLanguage = "python", PageType = "api-reference" });
        var nodes = new[] { nonPython, nonCSharp };

        var linked = DocPolyglotOwnershipLinker.Link(nodes, "/docs");

        Assert.Same(nodes, linked);
    }

    [Fact]
    public void Link_RejectsMultipleValidOwnersForTheSameModule()
    {
        var python = CreatePythonModule(
            "api/python/sidecar-worker",
            DocPolyglotOwnershipLinker.CreatePythonModuleMarker("sidecar/worker.py"));
        var firstOwner = CreateCSharpOwner(
            "Namespaces/Sample.Host",
            DocPolyglotOwnershipLinker.CreateCSharpOwnerMarker("sidecar/worker.py", "Sample-Host-Worker", "Worker Host"));
        var secondOwner = CreateCSharpOwner(
            "Namespaces/Another.Host",
            DocPolyglotOwnershipLinker.CreateCSharpOwnerMarker("sidecar/worker.py", "Another-Host-Worker", "Another Worker Host"));

        var linked = DocPolyglotOwnershipLinker.Link([python, firstOwner, secondOwner], "/docs");

        Assert.All(linked, node => Assert.DoesNotContain("data-appsurfacedocs-python-", node.Content, StringComparison.Ordinal));
        Assert.All(linked, node => Assert.DoesNotContain("doc-polyglot-link", node.Content, StringComparison.Ordinal));
    }

    [Fact]
    public void Link_RemovesMalformedOrMismatchedMarkersWhileRetainingTheOneValidatedRelationship()
    {
        var python = CreatePythonModule(
            "api/python/sidecar-worker",
            DocPolyglotOwnershipLinker.CreatePythonModuleMarker("sidecar/worker.py")
            + DocPolyglotOwnershipLinker.CreatePythonModuleMarker("sidecar/other.py"));
        var csharp = CreateCSharpOwner(
            "Namespaces/Sample.Host",
            DocPolyglotOwnershipLinker.CreateCSharpOwnerMarker("sidecar/worker.py", "Sample-Host-Worker", "Worker Host")
            + "<span data-appsurfacedocs-python-owner=\"%E0%A4%A\" data-appsurfacedocs-python-owner-anchor=\"1not-an-anchor\" data-appsurfacedocs-python-owner-label=\"x\"></span>");

        var linked = DocPolyglotOwnershipLinker.Link([python, csharp], "/docs");

        Assert.Contains("C# host:", linked[0].Content, StringComparison.Ordinal);
        Assert.Contains("Python module:", linked[1].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-appsurfacedocs-python-", linked[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-appsurfacedocs-python-", linked[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Link_RemovesValidMarkersWhosePathOrAnchorDoesNotMatchAnEstablishedLink()
    {
        var python = CreatePythonModule(
            "api/python/sidecar-worker",
            DocPolyglotOwnershipLinker.CreatePythonModuleMarker("sidecar/worker.py")
            + DocPolyglotOwnershipLinker.CreatePythonModuleMarker("sidecar/unmatched.py"));
        var csharp = CreateCSharpOwner(
            "Namespaces/Sample.Host",
            DocPolyglotOwnershipLinker.CreateCSharpOwnerMarker("sidecar/worker.py", "Sample-Host-Worker", "Worker Host")
            + DocPolyglotOwnershipLinker.CreateCSharpOwnerMarker("sidecar/other.py", "Sample-Host-Worker", "Other Path")
            + DocPolyglotOwnershipLinker.CreateCSharpOwnerMarker("sidecar/third.py", "Different-Anchor", "Different Anchor"));

        var linked = DocPolyglotOwnershipLinker.Link([python, csharp], "/docs");

        Assert.Contains("C# host:", linked[0].Content, StringComparison.Ordinal);
        Assert.Contains("Python module:", linked[1].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-appsurfacedocs-python-", linked[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-appsurfacedocs-python-", linked[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Link_RejectsEachIncompleteOwnershipPageShape()
    {
        var invalidPythonNodes = new[]
        {
            new DocNode("wrong language", "api/python/a", DocPolyglotOwnershipLinker.CreatePythonModuleMarker("a.py"), Metadata: new DocMetadata { CodeLanguage = "csharp", PageType = "python-module" }),
            new DocNode("wrong page", "api/python/b", DocPolyglotOwnershipLinker.CreatePythonModuleMarker("b.py"), Metadata: new DocMetadata { CodeLanguage = "python", PageType = "api-reference" }),
            new DocNode("wrong route", "Guides/c", DocPolyglotOwnershipLinker.CreatePythonModuleMarker("c.py"), Metadata: new DocMetadata { CodeLanguage = "python", PageType = "python-module" }),
            new DocNode("null metadata", "api/python/null-metadata", DocPolyglotOwnershipLinker.CreatePythonModuleMarker("null-metadata.py"))
        };
        var invalidCsharpNodes = new[]
        {
            new DocNode("wrong language", "Namespaces/A", DocPolyglotOwnershipLinker.CreateCSharpOwnerMarker("a.py", "A", "A"), Metadata: new DocMetadata { CodeLanguage = "python", PageType = "api-reference" }),
            new DocNode("wrong page", "Namespaces/B", DocPolyglotOwnershipLinker.CreateCSharpOwnerMarker("b.py", "B", "B"), Metadata: new DocMetadata { CodeLanguage = "csharp", PageType = "python-module" }),
            new DocNode("wrong route", "Guides/C", DocPolyglotOwnershipLinker.CreateCSharpOwnerMarker("c.py", "C", "C"), Metadata: new DocMetadata { CodeLanguage = "csharp", PageType = "api-reference" }),
            new DocNode("null metadata", "Namespaces/NullMetadata", DocPolyglotOwnershipLinker.CreateCSharpOwnerMarker("null-metadata.py", "NullMetadata", "Null Metadata"))
        };
        var nodes = invalidPythonNodes.Concat(invalidCsharpNodes).ToArray();

        var linked = DocPolyglotOwnershipLinker.Link(nodes, "/docs");

        Assert.Same(nodes, linked);
    }

    [Fact]
    public void Link_RemovesEveryUnsafeDecodedMarkerValueWithoutAffectingTheValidatedOwner()
    {
        var python = CreatePythonModule(
            "api/python/sidecar-worker",
            DocPolyglotOwnershipLinker.CreatePythonModuleMarker("sidecar/worker.py")
            + "<span data-appsurfacedocs-python-module=\"sidecar%2F..%2Fworker.py\"></span>"
            + "<span data-appsurfacedocs-python-module=\"sidecar%5Cworker.py\"></span>"
            + "<span data-appsurfacedocs-python-module=\"%20\"></span>"
            + "<span data-appsurfacedocs-python-module=\"%2Fworker.py\"></span>"
            + "<span data-appsurfacedocs-python-module=\"sidecar%2F%2Fworker.py\"></span>"
            + "<span data-appsurfacedocs-python-module=\"sidecar%2F.%2Fworker.py\"></span>"
            + "<span data-appsurfacedocs-python-module=\"sidecar%2Fworker.txt\"></span>");
        var csharp = CreateCSharpOwner(
            "Namespaces/Sample.Host",
            DocPolyglotOwnershipLinker.CreateCSharpOwnerMarker("sidecar/worker.py", "Sample-Host-Worker", "Worker Host")
            + "<span data-appsurfacedocs-python-owner=\"sidecar%2F..%2Fworker.py\" data-appsurfacedocs-python-owner-anchor=\"Sample-Host-Worker\" data-appsurfacedocs-python-owner-label=\"Worker%20Host\"></span>"
            + "<span data-appsurfacedocs-python-owner=\"sidecar%2Fworker.py\" data-appsurfacedocs-python-owner-anchor=\"1invalid\" data-appsurfacedocs-python-owner-label=\"Worker%20Host\"></span>"
            + "<span data-appsurfacedocs-python-owner=\"sidecar%2Fworker.py\" data-appsurfacedocs-python-owner-anchor=\"Sample-Host-Worker\" data-appsurfacedocs-python-owner-label=\"%20\"></span>"
            + $"<span data-appsurfacedocs-python-owner=\"sidecar%2Fworker.py\" data-appsurfacedocs-python-owner-anchor=\"Sample-Host-Worker\" data-appsurfacedocs-python-owner-label=\"{new string('x', 513)}\"></span>");

        var linked = DocPolyglotOwnershipLinker.Link([python, csharp], "/docs");

        Assert.Contains("C# host:", linked[0].Content, StringComparison.Ordinal);
        Assert.Contains("Python module:", linked[1].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-appsurfacedocs-python-", linked[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-appsurfacedocs-python-", linked[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Link_AcceptsTheMaximumOwnerLabelLength()
    {
        var label = new string('x', 512);
        var python = CreatePythonModule(
            "api/python/sidecar-worker",
            DocPolyglotOwnershipLinker.CreatePythonModuleMarker("sidecar/worker.py"));
        var csharp = CreateCSharpOwner(
            "Namespaces/Sample.Host",
            DocPolyglotOwnershipLinker.CreateCSharpOwnerMarker("sidecar/worker.py", "Sample-Host-Worker", label));

        var linked = DocPolyglotOwnershipLinker.Link([python, csharp], "/docs");

        Assert.Contains($">{label}</a>", linked[0].Content, StringComparison.Ordinal);
        Assert.Contains("Python module:", linked[1].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-appsurfacedocs-python-", linked[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-appsurfacedocs-python-", linked[1].Content, StringComparison.Ordinal);
    }

    private static DocNode CreatePythonModule(string path, string content) =>
        new(
            "Python module",
            path,
            content,
            Metadata: new DocMetadata { CodeLanguage = "python", PageType = "python-module" });

    private static DocNode CreateCSharpOwner(string path, string content) =>
        new(
            "C# owner",
            path,
            content,
            Metadata: new DocMetadata { CodeLanguage = "csharp", PageType = "api-reference" });
}
