namespace ForgeTrust.AppSurface.Docs.Models;

/// <summary>
/// Classifies semantic XML nodes for the constrained C# documentation partials.
/// </summary>
/// <remarks>
/// The view uses this helper to preserve the legacy XML renderer's paragraph rule without rebuilding or trusting
/// source XML at request time. It intentionally recognizes only model data created during snapshot harvesting.
/// </remarks>
internal static class CSharpDocumentationMarkup
{
    /// <summary>
    /// Gets whether the supplied semantic nodes contain an element that owns block layout.
    /// </summary>
    /// <param name="nodes">The snapshot-time XML documentation nodes.</param>
    /// <returns><see langword="true"/> when Razor should not add a wrapping paragraph.</returns>
    internal static bool HasBlockNodes(IReadOnlyList<CSharpXmlNode> nodes)
    {
        return nodes.Any(
            static node => node.Kind is CSharpXmlNodeKind.Paragraph
                or CSharpXmlNodeKind.CodeBlock
                or CSharpXmlNodeKind.List);
    }
}
