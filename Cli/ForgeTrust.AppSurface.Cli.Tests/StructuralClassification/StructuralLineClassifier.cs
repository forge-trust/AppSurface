using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Evidence.Coverage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>
/// Identifies the sole test-only structural category considered by the #781 spike.
/// </summary>
internal static class StructuralLineClassificationPolicy
{
    /// <summary>
    /// Gets the stable identifier emitted by every audit entry.
    /// </summary>
    internal const string Identifier = "appsurface.structural-line";

    /// <summary>
    /// Gets the first experimental version of the policy.
    /// </summary>
    internal const string Version = "v0";

    /// <summary>
    /// Gets the sole reason code representing an accepted line.
    /// </summary>
    internal const string AcceptanceReason = "structural-auto-property";
}

/// <summary>
/// Describes whether a line met the test-only structural policy.
/// </summary>
internal enum StructuralLineDisposition
{
    /// <summary>
    /// The line met every proof requirement.
    /// </summary>
    Accepted,

    /// <summary>
    /// The line did not meet at least one proof requirement.
    /// </summary>
    Rejected,
}

/// <summary>
/// Records the deterministic explanation for one changed coverage line.
/// </summary>
internal sealed record StructuralLineClassificationEntry(
    string PolicyIdentifier,
    string PolicyVersion,
    string Path,
    int Line,
    bool IsMeasured,
    bool? LineCovered,
    int? CoveredConditions,
    int? ValidConditions,
    string? SourceFingerprint,
    string? SourceProvenance,
    string? ParseOptionsIdentity,
    string? SourceTreePath,
    string? SymbolDocumentationId,
    string? SymbolDisplayName,
    StructuralLineDisposition Disposition,
    string ReasonCode,
    string? Metadata);

/// <summary>
/// Holds sorted, test-only audit evidence and a stable serialization for assertions.
/// </summary>
internal sealed record StructuralLineClassificationAudit(IReadOnlyList<StructuralLineClassificationEntry> Entries)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Serializes the ordered in-memory audit without creating an artifact file.
    /// </summary>
    internal string ToDeterministicJson() => JsonSerializer.Serialize(Entries, SerializerOptions);
}

/// <summary>
/// Binds one explicit fixture source document to its bytes, syntax tree, compiler options, and provenance.
/// </summary>
internal sealed record StructuralSourceDocument(
    string Path,
    byte[] Bytes,
    string Fingerprint,
    CSharpParseOptions ParseOptions,
    SyntaxTree SyntaxTree,
    Encoding Encoding,
    bool IsGenerated = false,
    bool IsSynthesized = false)
{
    /// <summary>
    /// Creates an authored source document whose tree is parsed from the provided bytes and explicit decoder.
    /// </summary>
    internal static StructuralSourceDocument Create(
        string path,
        byte[] bytes,
        CSharpParseOptions parseOptions,
        Encoding? encoding = null,
        bool isGenerated = false,
        bool isSynthesized = false,
        string? fingerprint = null,
        SyntaxTree? syntaxTree = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(parseOptions);

        encoding ??= new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var sourceText = SourceText.From(encoding.GetString(bytes), encoding, SourceHashAlgorithm.Sha256);
        syntaxTree ??= CSharpSyntaxTree.ParseText(sourceText, parseOptions, path);
        return new StructuralSourceDocument(
            path,
            bytes,
            fingerprint ?? Convert.ToHexString(SHA256.HashData(bytes)),
            parseOptions,
            syntaxTree,
            encoding,
            isGenerated,
            isSynthesized);
    }
}

/// <summary>
/// Resolves normalized fixture paths to their intentionally declared source documents.
/// </summary>
internal sealed class StructuralSourceManifest
{
    private readonly IReadOnlyList<StructuralSourceDocument> documents;

    /// <summary>
    /// Initializes the source manifest from explicitly declared fixture documents.
    /// </summary>
    internal StructuralSourceManifest(IEnumerable<StructuralSourceDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        this.documents = documents.ToArray();
    }

    /// <summary>
    /// Finds every document mapped to a normalized repository-relative path.
    /// </summary>
    internal IReadOnlyList<StructuralSourceDocument> Find(string normalizedPath) =>
        documents
            .Where(document => string.Equals(
                StructuralLineClassifier.NormalizePath(document.Path),
                normalizedPath,
                StringComparison.Ordinal))
            .ToArray();
}

/// <summary>
/// Classifies changed coverage lines against explicit fixture-only C# compiler evidence.
/// </summary>
internal sealed class StructuralLineClassifier
{
    private readonly Func<PatchCoverageLine, Exception?>? faultInjection;
    private readonly Dictionary<CSharpCompilation, StructuralSourceEvidenceCache> evidenceCaches =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Initializes the classifier with an optional test seam that exercises the fail-closed fault boundary.
    /// </summary>
    internal StructuralLineClassifier(Func<PatchCoverageLine, Exception?>? faultInjection = null)
    {
        this.faultInjection = faultInjection;
    }

    /// <summary>
    /// Produces one audit entry per changed coverage line without mutating patch evidence or coverage results.
    /// </summary>
    internal StructuralLineClassificationAudit Classify(
        PatchCoverageAnalysis analysis,
        CSharpCompilation compilation,
        StructuralSourceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(manifest);

        var evidenceCache = GetEvidenceCache(compilation);
        var classificationRun = evidenceCache.StartClassificationRun();
        var entries = analysis.Lines
            .Select(line => ClassifyLine(line, manifest, evidenceCache, classificationRun))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ThenBy(entry => entry.Line)
            .ThenBy(entry => entry.ReasonCode, StringComparer.Ordinal)
            .ToArray();
        return new StructuralLineClassificationAudit(entries);
    }

    /// <summary>
    /// Normalizes a fixture path without consulting the checkout or host filesystem.
    /// </summary>
    internal static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path.Replace('\\', '/');
    }

    private StructuralLineClassificationEntry ClassifyLine(
        PatchCoverageLine line,
        StructuralSourceManifest manifest,
        StructuralSourceEvidenceCache evidenceCache,
        int classificationRun)
    {
        StructuralSourceDocument? source = null;
        IPropertySymbol? propertySymbol = null;
        try
        {
            var normalizedPath = NormalizePath(line.Path);
            if (!string.Equals(Path.GetExtension(normalizedPath), ".cs", StringComparison.OrdinalIgnoreCase))
            {
                return Reject(line, normalizedPath, "not-csharp");
            }

            if (!line.IsMeasured)
            {
                return Reject(line, normalizedPath, "not-measured");
            }

            var documents = manifest.Find(normalizedPath);
            if (documents.Count == 0)
            {
                return Reject(line, normalizedPath, "source-missing");
            }

            if (documents.Count != 1)
            {
                return Reject(line, normalizedPath, "source-ambiguous");
            }

            source = documents[0];
            if (source.IsGenerated || source.IsSynthesized)
            {
                return Reject(line, normalizedPath, "source-generated", source);
            }

            var evidence = evidenceCache.Get(source);
            if (!evidence.HasExactSourceIdentity(normalizedPath, classificationRun))
            {
                return Reject(line, normalizedPath, "source-fingerprint-mismatch", source);
            }

            if (!evidence.IsCompilationTree)
            {
                return Reject(line, normalizedPath, "compilation-mismatch", source);
            }

            if (evidence.SyntaxTreeOptions is null)
            {
                return Reject(line, normalizedPath, "compilation-mismatch", source);
            }

            if (!evidence.HasMatchingConditionalSymbols)
            {
                return Reject(line, normalizedPath, "conditional-compilation-mismatch", source);
            }

            if (!evidence.HasMatchingParseOptions)
            {
                return Reject(line, normalizedPath, "compilation-mismatch", source);
            }

            var sourceText = evidence.SourceText;
            if (line.Line <= 0 || line.Line > sourceText.Lines.Count)
            {
                return Reject(line, normalizedPath, "location-unmatched", source);
            }

            var lineSpan = sourceText.Lines[line.Line - 1].Span;
            var properties = evidence.Properties
                .Where(property => property.Span.OverlapsWith(lineSpan))
                .ToArray();
            if (properties.Length == 0)
            {
                return Reject(line, normalizedPath, "location-unmatched", source);
            }

            if (properties.Length != 1)
            {
                return Reject(line, normalizedPath, "location-ambiguous", source);
            }

            var property = properties[0];
            if (!HasNonBraceTokenOnLine(property, lineSpan))
            {
                return Reject(line, normalizedPath, "location-unmatched", source);
            }

            var injectedFailure = faultInjection?.Invoke(line);
            if (injectedFailure is not null)
            {
                throw injectedFailure;
            }

            var propertyEvidence = evidence.GetPropertyEvidence(property);
            propertySymbol = propertyEvidence.Symbol;
            if (propertyEvidence.IsUnbound || propertySymbol is not { } boundPropertySymbol)
            {
                return Reject(line, normalizedPath, "property-unbound", source, propertySymbol);
            }

            if (propertyEvidence.IsInheritedOrOverridden)
            {
                return Reject(line, normalizedPath, "property-inherited-or-overridden", source, propertySymbol);
            }

            if (propertyEvidence.IsInPartialType)
            {
                return Reject(line, normalizedPath, "partial-type", source, propertySymbol);
            }

            if (propertyEvidence.HasSemanticDiagnostic)
            {
                return Reject(line, normalizedPath, "semantic-diagnostic", source, propertySymbol);
            }

            var propertyShapeReason = GetPropertyShapeRejectionReason(property);
            if (propertyShapeReason is not null)
            {
                return Reject(line, normalizedPath, propertyShapeReason, source, propertySymbol);
            }

            return Accept(line, normalizedPath, source, boundPropertySymbol);
        }
        catch (Exception exception)
        {
            return Reject(
                line,
                NormalizePathForAudit(line.Path),
                "analysis-fault",
                source,
                propertySymbol,
                "exceptionType=" + exception.GetType().Name);
        }
    }

    private static bool HasNonBraceTokenOnLine(PropertyDeclarationSyntax property, TextSpan lineSpan)
    {
        return property.DescendantTokens(descendIntoTrivia: false)
            .Where(token => token.Span.OverlapsWith(lineSpan))
            .Any(token => !IsAccessorListDelimiter(property, token));
    }

    private static bool IsAccessorListDelimiter(PropertyDeclarationSyntax property, SyntaxToken token)
    {
        return property.AccessorList is { } accessors
            && (token.Equals(accessors.OpenBraceToken) || token.Equals(accessors.CloseBraceToken));
    }

    private static string? GetPropertyShapeRejectionReason(PropertyDeclarationSyntax property)
    {
        if (property.AttributeLists.Count != 0)
        {
            return "property-attributes";
        }

        if (property.Initializer is not null)
        {
            return "property-initializer";
        }

        if (property.ExpressionBody is not null)
        {
            return "accessor-expression-body";
        }

        if (!property.Modifiers.All(IsAccessibilityModifier))
        {
            return "property-modifiers";
        }

        if (property.AccessorList is not { } accessors || accessors.Accessors.Count != 2)
        {
            return "unsupported-property-shape";
        }

        if (accessors.Accessors.Any(accessor => accessor.Modifiers.Count != 0))
        {
            return "accessor-modifiers";
        }

        if (accessors.Accessors.Any(accessor => accessor.Body is not null))
        {
            return "accessor-body";
        }

        if (accessors.Accessors.Any(accessor => accessor.ExpressionBody is not null))
        {
            return "accessor-expression-body";
        }

        if (accessors.Accessors.Any(accessor => accessor.SemicolonToken.IsMissing))
        {
            return "unsupported-property-shape";
        }

        var accessorKinds = accessors.Accessors.Select(accessor => accessor.Kind()).ToHashSet();
        return accessorKinds.SetEquals([SyntaxKind.GetAccessorDeclaration, SyntaxKind.SetAccessorDeclaration])
                || accessorKinds.SetEquals([SyntaxKind.GetAccessorDeclaration, SyntaxKind.InitAccessorDeclaration])
            ? null
            : "unsupported-property-shape";
    }

    private static bool IsAccessibilityModifier(SyntaxToken modifier)
    {
        return modifier.IsKind(SyntaxKind.PublicKeyword)
            || modifier.IsKind(SyntaxKind.PrivateKeyword)
            || modifier.IsKind(SyntaxKind.ProtectedKeyword)
            || modifier.IsKind(SyntaxKind.InternalKeyword);
    }

    private static StructuralLineClassificationEntry Accept(
        PatchCoverageLine line,
        string normalizedPath,
        StructuralSourceDocument source,
        IPropertySymbol propertySymbol)
    {
        return CreateEntry(
            line,
            normalizedPath,
            source,
            propertySymbol,
            StructuralLineDisposition.Accepted,
            StructuralLineClassificationPolicy.AcceptanceReason,
            null);
    }

    private static StructuralLineClassificationEntry Reject(
        PatchCoverageLine line,
        string normalizedPath,
        string reasonCode,
        StructuralSourceDocument? source = null,
        IPropertySymbol? propertySymbol = null,
        string? metadata = null)
    {
        return CreateEntry(
            line,
            normalizedPath,
            source,
            propertySymbol,
            StructuralLineDisposition.Rejected,
            reasonCode,
            metadata);
    }

    private static StructuralLineClassificationEntry CreateEntry(
        PatchCoverageLine line,
        string normalizedPath,
        StructuralSourceDocument? source,
        IPropertySymbol? propertySymbol,
        StructuralLineDisposition disposition,
        string reasonCode,
        string? metadata)
    {
        return new StructuralLineClassificationEntry(
            StructuralLineClassificationPolicy.Identifier,
            StructuralLineClassificationPolicy.Version,
            normalizedPath,
            line.Line,
            line.IsMeasured,
            line.LineCovered,
            line.CoveredConditions,
            line.ValidConditions,
            source?.Fingerprint,
            source is null ? null : source.IsGenerated || source.IsSynthesized ? "generated" : "authored",
            source is null ? null : FormatParseOptions(source.ParseOptions),
            source?.SyntaxTree.FilePath,
            propertySymbol?.GetDocumentationCommentId(),
            propertySymbol?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            disposition,
            reasonCode,
            metadata);
    }

    private static string FormatParseOptions(CSharpParseOptions options)
    {
        return $"language={options.LanguageVersion};symbols={string.Join(',', options.PreprocessorSymbolNames.Order(StringComparer.Ordinal))}";
    }

    private static string NormalizePathForAudit(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('\\', '/');

    private StructuralSourceEvidenceCache GetEvidenceCache(CSharpCompilation compilation)
    {
        if (evidenceCaches.TryGetValue(compilation, out var evidenceCache))
        {
            return evidenceCache;
        }

        evidenceCache = new StructuralSourceEvidenceCache(compilation);
        evidenceCaches.Add(compilation, evidenceCache);
        return evidenceCache;
    }

    private sealed class StructuralSourceEvidenceCache
    {
        private readonly CSharpCompilation compilation;
        private readonly HashSet<SyntaxTree> compilationTrees;
        private readonly Dictionary<StructuralSourceDocument, StructuralSourceEvidence> evidenceBySource = [];
        private int classificationRun;

        internal StructuralSourceEvidenceCache(CSharpCompilation compilation)
        {
            this.compilation = compilation;
            compilationTrees = new HashSet<SyntaxTree>(compilation.SyntaxTrees, ReferenceEqualityComparer.Instance);
        }

        internal StructuralSourceEvidence Get(StructuralSourceDocument source)
        {
            if (evidenceBySource.TryGetValue(source, out var evidence))
            {
                return evidence;
            }

            evidence = new StructuralSourceEvidence(source, compilation, compilationTrees.Contains(source.SyntaxTree));
            evidenceBySource.Add(source, evidence);
            return evidence;
        }

        internal int StartClassificationRun() => checked(++classificationRun);
    }

    private sealed class StructuralSourceEvidence
    {
        private readonly StructuralSourceDocument source;
        private readonly Lazy<SemanticModel> semanticModel;
        private readonly Dictionary<PropertyDeclarationSyntax, StructuralPropertyEvidence> propertyEvidence = [];
        private string? actualFingerprint;
        private string? decodedText;
        private int verifiedClassificationRun = -1;

        internal StructuralSourceEvidence(
            StructuralSourceDocument source,
            CSharpCompilation compilation,
            bool isCompilationTree)
        {
            this.source = source;
            SourceText = source.SyntaxTree.GetText();
            Properties = source.SyntaxTree.GetRoot()
                .DescendantNodes()
                .OfType<PropertyDeclarationSyntax>()
                .ToArray();
            SyntaxTreeOptions = source.SyntaxTree.Options as CSharpParseOptions;
            IsCompilationTree = isCompilationTree;
            HasMatchingConditionalSymbols = SyntaxTreeOptions is not null
                && source.ParseOptions.PreprocessorSymbolNames
                    .Order(StringComparer.Ordinal)
                    .SequenceEqual(
                        SyntaxTreeOptions.PreprocessorSymbolNames.Order(StringComparer.Ordinal),
                        StringComparer.Ordinal);
            HasMatchingParseOptions = SyntaxTreeOptions is not null
                && source.ParseOptions.Equals(SyntaxTreeOptions);
            semanticModel = new Lazy<SemanticModel>(() => compilation.GetSemanticModel(source.SyntaxTree));
        }

        internal SourceText SourceText { get; }

        internal IReadOnlyList<PropertyDeclarationSyntax> Properties { get; }

        internal CSharpParseOptions? SyntaxTreeOptions { get; }

        internal bool IsCompilationTree { get; }

        internal bool HasMatchingConditionalSymbols { get; }

        internal bool HasMatchingParseOptions { get; }

        internal SemanticModel SemanticModel => semanticModel.Value;

        internal StructuralPropertyEvidence GetPropertyEvidence(PropertyDeclarationSyntax property)
        {
            if (propertyEvidence.TryGetValue(property, out var evidence))
            {
                return evidence;
            }

            var symbol = SemanticModel.GetDeclaredSymbol(property);
            var isUnbound = symbol is null
                || symbol.Type is IErrorTypeSymbol
                || symbol.DeclaringSyntaxReferences.Length != 1
                || !ReferenceEquals(symbol.DeclaringSyntaxReferences[0].SyntaxTree, source.SyntaxTree);
            evidence = new StructuralPropertyEvidence(
                symbol,
                isUnbound,
                !isUnbound
                    && (symbol!.OverriddenProperty is not null
                        || symbol.ExplicitInterfaceImplementations.Length != 0
                        || symbol.ContainingType.TypeKind == TypeKind.Interface),
                property.Ancestors().OfType<TypeDeclarationSyntax>().Any(type =>
                    type.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword))),
                !isUnbound
                    && SemanticModel.GetDiagnostics(property.Span)
                        .Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
            propertyEvidence.Add(property, evidence);
            return evidence;
        }

        internal bool HasExactSourceIdentity(string normalizedPath, int classificationRun)
        {
            if (verifiedClassificationRun != classificationRun)
            {
                actualFingerprint = Convert.ToHexString(SHA256.HashData(source.Bytes));
                decodedText = source.Encoding.GetString(source.Bytes);
                verifiedClassificationRun = classificationRun;
            }

            return string.Equals(actualFingerprint, source.Fingerprint, StringComparison.Ordinal)
                && string.Equals(NormalizePath(source.SyntaxTree.FilePath), normalizedPath, StringComparison.Ordinal)
                && string.Equals(decodedText, SourceText.ToString(), StringComparison.Ordinal);
        }
    }

    private sealed record StructuralPropertyEvidence(
        IPropertySymbol? Symbol,
        bool IsUnbound,
        bool IsInheritedOrOverridden,
        bool IsInPartialType,
        bool HasSemanticDiagnostic);
}
