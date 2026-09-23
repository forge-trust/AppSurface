namespace ForgeTrust.AppSurface.Docs.Models;

/// <summary>
/// Selects the rendering contract for a Docs details page.
/// </summary>
/// <remarks>
/// This is deliberately an internal discriminator rather than a path-suffix convention. Namespace pages are
/// extensionless, and a public or derived harvester continues to use the legacy content route even when it harvests
/// C# source.
/// </remarks>
internal enum CSharpRenderKind
{
    /// <summary>
    /// The document is rendered through the established HTML-content path.
    /// </summary>
    Legacy = 0,

    /// <summary>
    /// The built-in C# namespace projection is rendered through constrained Razor partials.
    /// </summary>
    TypedNamespace = 1
}

/// <summary>
/// Immutable semantic projection for one built-in C# namespace page.
/// </summary>
/// <remarks>
/// The projection is attached only to the exact built-in <see cref="Services.CSharpDocHarvester"/> aggregation path.
/// It does not alter <see cref="DocNode"/>'s public positional API or create a renderer extension point.
/// </remarks>
/// <param name="FullNamespace">The fully qualified namespace name represented by the page.</param>
/// <param name="Title">The reader-facing title for the namespace page.</param>
/// <param name="ChildNamespaces">The child namespaces resolved from the built-in namespace hierarchy.</param>
/// <param name="Types">The documented C# types declared in the namespace.</param>
/// <param name="Enums">The documented enum declarations in the namespace.</param>
/// <param name="Outline">The page outline entries contributed by the namespace and its composed content.</param>
/// <param name="SymbolSourceProvenance">The source provenance for symbols displayed on the page.</param>
/// <param name="ReaderText">The plain-text projection used for reader order and search indexing.</param>
/// <param name="IntroHtml">The sanitized Markdown introduction, when the namespace has one.</param>
/// <param name="EntryPoints">The optional namespace entry points composed into the page.</param>
internal sealed record CSharpNamespaceDocument(
    string FullNamespace,
    string Title,
    IReadOnlyList<CSharpChildNamespace> ChildNamespaces,
    IReadOnlyList<CSharpTypeDocument> Types,
    IReadOnlyList<CSharpEnumDocument> Enums,
    IReadOnlyList<DocOutlineItem> Outline,
    IReadOnlyList<DocSymbolSourceProvenance> SymbolSourceProvenance,
    string ReaderText,
    string? IntroHtml = null,
    IReadOnlyList<DocNamespaceEntryPoint>? EntryPoints = null);

/// <summary>
/// A child namespace link resolved from the built-in namespace hierarchy.
/// </summary>
/// <param name="Path">The canonical documentation path for the child namespace.</param>
/// <param name="Title">The reader-facing title for the child namespace link.</param>
internal sealed record CSharpChildNamespace(
    string Path,
    string Title);

/// <summary>
/// A documented C# type and its directly documented members.
/// </summary>
/// <param name="AnchorId">The stable fragment identifier for the type declaration.</param>
/// <param name="DisplayName">The safe reader-facing display name for the type.</param>
/// <param name="Documentation">The type documentation, or <see langword="null"/> when none was supplied.</param>
/// <param name="MethodGroups">The documented method overload groups declared by the type.</param>
/// <param name="Properties">The documented properties declared by the type.</param>
/// <param name="SourceHref">The safe source URL for the declaration, when available.</param>
internal sealed record CSharpTypeDocument(
    string AnchorId,
    string DisplayName,
    CSharpDocumentation? Documentation,
    IReadOnlyList<CSharpMethodGroupDocument> MethodGroups,
    IReadOnlyList<CSharpPropertyDocument> Properties,
    string? SourceHref = null);

/// <summary>
/// A documented enum declaration.
/// </summary>
/// <param name="AnchorId">The stable fragment identifier for the enum declaration.</param>
/// <param name="DisplayName">The safe reader-facing display name for the enum.</param>
/// <param name="Documentation">The documentation extracted for the enum declaration.</param>
/// <param name="SourceHref">The safe source URL for the declaration, when available.</param>
internal sealed record CSharpEnumDocument(
    string AnchorId,
    string DisplayName,
    CSharpDocumentation Documentation,
    string? SourceHref = null);

/// <summary>
/// A group of overloads with one stable group anchor.
/// </summary>
/// <param name="AnchorId">The stable fragment identifier shared by the overload group.</param>
/// <param name="Name">The method name displayed for the overload group.</param>
/// <param name="Overloads">The documented overloads in reader and declaration order.</param>
internal sealed record CSharpMethodGroupDocument(
    string AnchorId,
    string Name,
    IReadOnlyList<CSharpMethodDocument> Overloads);

/// <summary>
/// One documented method overload.
/// </summary>
/// <param name="AnchorId">The stable fragment identifier for the method overload.</param>
/// <param name="Signature">The safe display signature for the overload.</param>
/// <param name="Documentation">The documentation extracted for the overload.</param>
/// <param name="SourceHref">The safe source URL for the declaration, when available.</param>
internal sealed record CSharpMethodDocument(
    string AnchorId,
    CSharpSignature Signature,
    CSharpDocumentation Documentation,
    string? SourceHref = null);

/// <summary>
/// One documented property declaration.
/// </summary>
/// <param name="AnchorId">The stable fragment identifier for the property declaration.</param>
/// <param name="Name">The property name displayed in the member heading.</param>
/// <param name="Signature">The safe display signature for the property.</param>
/// <param name="Documentation">The documentation extracted for the property.</param>
/// <param name="SourceHref">The safe source URL for the declaration, when available.</param>
internal sealed record CSharpPropertyDocument(
    string AnchorId,
    string Name,
    CSharpSignature Signature,
    CSharpDocumentation Documentation,
    string? SourceHref = null);

/// <summary>
/// Safe display data for a C# declaration signature.
/// </summary>
/// <param name="Type">The declared type or return type displayed in the signature.</param>
/// <param name="Name">The declaration name displayed in the signature.</param>
/// <param name="Parameters">The method parameters in declaration order.</param>
/// <param name="TypeParameters">The generic type parameters in declaration order.</param>
/// <param name="ExplicitInterface">The explicit interface qualification, when present.</param>
/// <param name="AccessorSignature">The accessor text for a property, when present.</param>
internal sealed record CSharpSignature(
    string Type,
    string Name,
    IReadOnlyList<CSharpSignatureParameter> Parameters,
    IReadOnlyList<string> TypeParameters,
    string? ExplicitInterface = null,
    string? AccessorSignature = null);

/// <summary>
/// Safe display data for a method parameter.
/// </summary>
/// <param name="Modifier">The parameter modifier such as <c>ref</c>, when present.</param>
/// <param name="Type">The parameter type displayed in the signature.</param>
/// <param name="Name">The parameter name displayed in the signature.</param>
/// <param name="DefaultValue">The safe default-value text, when the declaration supplies one.</param>
internal sealed record CSharpSignatureParameter(
    string? Modifier,
    string Type,
    string Name,
    string? DefaultValue = null);

/// <summary>
/// The semantic sections extracted from an XML documentation comment.
/// </summary>
/// <param name="Sections">The supported XML documentation sections in source order.</param>
internal sealed record CSharpDocumentation(
    IReadOnlyList<CSharpDocumentationSection> Sections);

/// <summary>
/// A named documentation section such as summary, parameter, or exception data.
/// </summary>
/// <param name="Kind">The supported semantic category represented by the section.</param>
/// <param name="Name">The parameter or type-parameter name, when the section has one.</param>
/// <param name="CrefTarget">The original <c>see</c> target, when the section carries one.</param>
/// <param name="CrefDisplay">The safe display value for the <c>see</c> target, when present.</param>
/// <param name="Content">The safe XML documentation nodes contained by the section.</param>
internal sealed record CSharpDocumentationSection(
    CSharpDocumentationSectionKind Kind,
    string? Name,
    string? CrefTarget,
    string? CrefDisplay,
    IReadOnlyList<CSharpXmlNode> Content);

/// <summary>
/// The supported XML documentation section categories.
/// </summary>
internal enum CSharpDocumentationSectionKind
{
    /// <summary>Summary documentation.</summary>
    Summary = 0,

    /// <summary>Type parameter documentation.</summary>
    TypeParameter = 1,

    /// <summary>Method or property parameter documentation.</summary>
    Parameter = 2,

    /// <summary>Return-value documentation.</summary>
    Returns = 3,

    /// <summary>Exception documentation.</summary>
    Exception = 4,

    /// <summary>Remarks documentation.</summary>
    Remarks = 5,

    /// <summary>Example documentation.</summary>
    Example = 6
}

/// <summary>
/// A safe XML documentation node. Leaf values are encoded by Razor; targets are retained separately from display text.
/// </summary>
/// <param name="Kind">The supported semantic node category.</param>
/// <param name="Text">The encoded-safe leaf text, when the node has text.</param>
/// <param name="Target">The retained source target, when the node is a reference.</param>
/// <param name="Children">The child nodes for a container node, when present.</param>
/// <param name="Ordered">Whether list children preserve ordered-list semantics.</param>
internal sealed record CSharpXmlNode(
    CSharpXmlNodeKind Kind,
    string? Text = null,
    string? Target = null,
    IReadOnlyList<CSharpXmlNode>? Children = null,
    bool Ordered = false);

/// <summary>
/// The supported XML documentation node kinds.
/// </summary>
internal enum CSharpXmlNodeKind
{
    /// <summary>Plain text.</summary>
    Text = 0,

    /// <summary>Inline code.</summary>
    InlineCode = 1,

    /// <summary>Block code.</summary>
    CodeBlock = 2,

    /// <summary>A paragraph.</summary>
    Paragraph = 3,

    /// <summary>A list.</summary>
    List = 4,

    /// <summary>A list item.</summary>
    ListItem = 5,

    /// <summary>A C# XML <c>see</c> reference.</summary>
    Cref = 6,

    /// <summary>A parameter reference.</summary>
    ParameterReference = 7,

    /// <summary>A type-parameter reference.</summary>
    TypeParameterReference = 8
}
