using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ForgeTrust.AppSurface.Docs.Models;
using Microsoft.Extensions.Logging.Abstractions;
using TreeSitter;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Harvests explicit Python module, class, function, and method docstrings without executing Python source.
/// </summary>
/// <remarks>
/// This built-in harvester is deliberately a bounded product spike. It parses only policy-approved <c>.py</c> files with
/// the Tree-sitter Python grammar, requires a literal top-level <c>__all__</c> public boundary, and projects plain
/// docstring text rather than a Google, NumPy, or Sphinx documentation dialect. It never imports a module, starts a
/// Python interpreter, evaluates an expression, or derives a general Python visibility policy from an absent boundary.
/// </remarks>
public sealed class PythonDocHarvester : IDocHarvester, IDocHarvesterDiagnosticProvider, IDocHarvesterActivation, IDocHarvesterHealthParticipation
{
    private const string HarvesterType = nameof(PythonDocHarvester);
    private const string MaxFileSizeConfigurationKey = "AppSurfaceDocs:Harvest:Python:MaxFileSizeBytes";
    private static readonly Regex UnsafeSlugCharacterRegex = new("[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.NonBacktracking);

    private readonly AppSurfaceDocsOptions _options;
    private readonly ILogger<PythonDocHarvester> _logger;
    private readonly AppSurfaceDocsHarvestPathPolicy _pathPolicy;
    private readonly Func<Language> _createLanguage;
    private readonly Func<Parser, string, Tree?> _parseSource;
    private readonly object _diagnosticsGate = new();
    private long _latestHarvestGeneration;
    private IReadOnlyList<DocHarvestDiagnostic> _lastDiagnostics = [];

    /// <summary>
    /// Initializes a new instance of <see cref="PythonDocHarvester"/>.
    /// </summary>
    /// <param name="options">Normalized AppSurface Docs options that contain Python harvest settings.</param>
    /// <param name="logger">Logger used for non-fatal Python harvest diagnostics.</param>
    public PythonDocHarvester(AppSurfaceDocsOptions options, ILogger<PythonDocHarvester> logger)
        : this(options, logger, new AppSurfaceDocsHarvestPathPolicy(options, NullLogger<AppSurfaceDocsHarvestPathPolicy>.Instance))
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="PythonDocHarvester"/> with a shared harvest path policy.
    /// </summary>
    /// <param name="options">Normalized AppSurface Docs options that contain Python harvest settings.</param>
    /// <param name="logger">Logger used for non-fatal Python harvest diagnostics.</param>
    /// <param name="pathPolicy">Shared harvest path policy used to decide which Python candidates publish.</param>
    internal PythonDocHarvester(
        AppSurfaceDocsOptions options,
        ILogger<PythonDocHarvester> logger,
        AppSurfaceDocsHarvestPathPolicy pathPolicy)
        : this(options, logger, pathPolicy, static () => new Language("Python"))
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="PythonDocHarvester"/> with an injectable native-language factory.
    /// </summary>
    /// <param name="options">Normalized AppSurface Docs options that contain Python harvest settings.</param>
    /// <param name="logger">Logger used for non-fatal Python harvest diagnostics.</param>
    /// <param name="pathPolicy">Shared harvest path policy used to decide which Python candidates publish.</param>
    /// <param name="createLanguage">Factory used to initialize the native Tree-sitter Python grammar.</param>
    /// <param name="parseSource">
    /// Optional parser operation used by tests to model a native parse that outlives a canceled harvest. Production uses
    /// <see cref="Parser.Parse(string)"/>.
    /// </param>
    /// <remarks>
    /// The default constructor supplies the registered Python grammar. This internal seam lets package tests verify that
    /// unavailable native assets are converted into diagnostics rather than escaping the harvest pipeline.
    /// </remarks>
    internal PythonDocHarvester(
        AppSurfaceDocsOptions options,
        ILogger<PythonDocHarvester> logger,
        AppSurfaceDocsHarvestPathPolicy pathPolicy,
        Func<Language> createLanguage,
        Func<Parser, string, Tree?>? parseSource = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(pathPolicy);
        ArgumentNullException.ThrowIfNull(createLanguage);

        _options = options;
        _logger = logger;
        _pathPolicy = pathPolicy;
        _createLanguage = createLanguage;
        _parseSource = parseSource ?? (static (parser, source) => parser.Parse(source));
    }

    /// <summary>
    /// Scans policy-approved Python files under the repository root and returns generated AppSurface Docs API nodes.
    /// </summary>
    /// <param name="rootPath">The repository root used to resolve include and exclude globs.</param>
    /// <param name="cancellationToken">An optional token to observe while reading and parsing files.</param>
    /// <returns>Generated Python module pages plus fragment-addressable symbol nodes.</returns>
    public async Task<IReadOnlyList<DocNode>> HarvestAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        return await HarvestAsync(rootPath, _pathPolicy, progress: null, cancellationToken);
    }

    /// <summary>
    /// Scans Python sources with the repository-scoped path policy captured for the current aggregation pass.
    /// </summary>
    /// <param name="context">The harvest context containing the repository root and active path policy snapshot.</param>
    /// <param name="cancellationToken">An optional token to observe while reading and parsing files.</param>
    /// <returns>Generated Python module pages plus fragment-addressable symbol nodes.</returns>
    internal async Task<IReadOnlyList<DocNode>> HarvestAsync(
        DocHarvestContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return await HarvestAsync(context.RepositoryRoot, context.PathPolicy, context.Progress, cancellationToken);
    }

    private async Task<IReadOnlyList<DocNode>> HarvestAsync(
        string rootPath,
        IHarvestPathPolicy pathPolicy,
        AppSurfaceDocsHarvestProgressSession? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var harvestGeneration = BeginHarvestDiagnostics();
        var diagnostics = new List<DocHarvestDiagnostic>();
        try
        {
            var pythonOptions = _options.Harvest?.Python ?? new AppSurfaceDocsPythonHarvestOptions();
            if (!pythonOptions.Enabled)
            {
                return [];
            }

            var includePatterns = GetUsableIncludePatterns(pythonOptions.IncludeGlobs);
            if (includePatterns.Count == 0)
            {
                diagnostics.Add(CreateDiagnostic(
                    DocHarvestDiagnosticCodes.PythonMissingInclude,
                    DocHarvestDiagnosticSeverity.Warning,
                    "Python harvesting is enabled but no Python source include glob is configured.",
                    "AppSurface Docs does not infer a repository-wide Python public API boundary during this spike, so it did not read any Python source.",
                    "Set AppSurfaceDocs:Harvest:Python:IncludeGlobs to at least one repository-relative .py file or directory pattern, or disable Python harvesting for this host."));
                return [];
            }

            if (progress is not null)
            {
                await progress.TransitionAsync(AppSurfaceDocsHarvestProgressPhase.Discovering);
            }

            if (!TryCreateParser(_createLanguage, _parseSource, out var language, out var parser, out var parserFailure))
            {
                diagnostics.Add(CreateDiagnostic(
                    DocHarvestDiagnosticCodes.PythonParserUnavailable,
                    pythonOptions.StrictHealth ? DocHarvestDiagnosticSeverity.Error : DocHarvestDiagnosticSeverity.Warning,
                    "Python docstring harvesting could not initialize the Tree-sitter parser for this runtime.",
                    parserFailure,
                    "Install a package build that contains TreeSitter.DotNet native assets for this runtime identifier, or disable Python harvesting until the runtime packaging issue is resolved."));
                return [];
            }

            var activeLanguage = language!;
            var activeParser = parser!;
            using (activeLanguage)
            using (activeParser)
            {
                var modules = new List<PythonModule>();
                foreach (var filePath in pathPolicy.EnumerateCandidateFiles(
                             Path.GetFullPath(rootPath),
                             AppSurfaceDocsHarvestSourceKind.Python,
                             "*.py",
                             cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relativePath = NormalizeRelativePath(Path.GetRelativePath(rootPath, filePath));
                    if (!pathPolicy.ShouldIncludeFilePath(relativePath, AppSurfaceDocsHarvestSourceKind.Python))
                    {
                        continue;
                    }

                    try
                    {
                        if (progress is not null)
                        {
                            await progress.TransitionAsync(AppSurfaceDocsHarvestProgressPhase.Parsing);
                            await progress.ReportSourceUnitAsync(0);
                        }

                        var readResult = await AppSurfaceDocsParserInputBudget.ReadUtf8SourceAsync(
                            filePath,
                            relativePath,
                            pythonOptions.MaxFileSizeBytes,
                            MaxFileSizeConfigurationKey,
                            DocHarvestDiagnosticCodes.PythonFileTooLarge,
                            HarvesterType,
                            "Python",
                            "Exclude generated or vendored Python with AppSurfaceDocs:Harvest:Python:ExcludeGlobs, or raise AppSurfaceDocs:Harvest:Python:MaxFileSizeBytes only for authored source that should be documented.",
                            cancellationToken);
                        if (!readResult.Included)
                        {
                            diagnostics.Add(readResult.Diagnostic!);
                            continue;
                        }

                        using var tree = _parseSource(activeParser, readResult.Source!);
                        if (tree is null || tree.RootNode.HasError)
                        {
                            diagnostics.Add(CreateDiagnostic(
                                DocHarvestDiagnosticCodes.PythonParseFailed,
                                DocHarvestDiagnosticSeverity.Warning,
                                $"Skipped Python file '{relativePath}' because the parser reported syntax errors.",
                                "Tree-sitter recovered a partial tree, but this spike does not publish API documentation from malformed Python source.",
                                "Fix the Python syntax, exclude this file, or keep it outside the explicit Python include boundary."));
                            continue;
                        }

                        var module = CreateModule(tree.RootNode, relativePath, diagnostics);
                        if (module is not null)
                        {
                            modules.Add(module);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (IsFileReadException(ex))
                    {
                        diagnostics.Add(CreateDiagnostic(
                            DocHarvestDiagnosticCodes.PythonParseFailed,
                            DocHarvestDiagnosticSeverity.Warning,
                            $"Skipped Python file '{relativePath}' because it could not be read.",
                            "The source file matched Python harvest policy, but AppSurface Docs could not read its content before parsing.",
                            "Fix file permissions or locks, or exclude this file from Python harvesting."));
                    }
                    catch (Exception ex) when (!IsFatalException(ex))
                    {
                        diagnostics.Add(CreateDiagnostic(
                            DocHarvestDiagnosticCodes.PythonParseFailed,
                            DocHarvestDiagnosticSeverity.Warning,
                            $"Skipped Python file '{relativePath}' because the parser did not complete.",
                            $"The Tree-sitter binding returned {ex.GetType().Name} while parsing this source file.",
                            "Fix the source, exclude the file, or report the minimized source shape as a Tree-sitter compatibility issue."));
                    }
                }

                if (progress is not null)
                {
                    await progress.TransitionAsync(AppSurfaceDocsHarvestProgressPhase.Finalizing);
                }

                var nodes = BuildDocNodes(modules, diagnostics);
                if (progress is not null && nodes.Count > 0)
                {
                    await progress.ReportOutputOnlyAsync(nodes.Count);
                }

                return nodes;
            }
        }
        finally
        {
            PublishHarvestDiagnostics(harvestGeneration, diagnostics);
            foreach (var diagnostic in diagnostics)
            {
                _logger.Log(
                    diagnostic.Severity >= DocHarvestDiagnosticSeverity.Error ? LogLevel.Error : LogLevel.Warning,
                    "AppSurface Docs Python harvest diagnostic {DiagnosticCode}: {Problem}",
                    diagnostic.Code,
                    diagnostic.Problem);
            }
        }
    }

    bool IDocHarvesterActivation.IsEnabled => _options.Harvest?.Python?.Enabled == true;

    bool IDocHarvesterHealthParticipation.ParticipatesInStrictHealth
    {
        get
        {
            var pythonOptions = _options.Harvest?.Python ?? new AppSurfaceDocsPythonHarvestOptions();
            return pythonOptions.StrictHealth || GetUsableIncludePatterns(pythonOptions.IncludeGlobs).Count > 0;
        }
    }

    IReadOnlyList<DocHarvestDiagnostic> IDocHarvesterDiagnosticProvider.GetHarvestDiagnostics()
    {
        lock (_diagnosticsGate)
        {
            return _lastDiagnostics;
        }
    }

    private static bool TryCreateParser(
        Func<Language> createLanguage,
        Func<Parser, string, Tree?> parseSource,
        out Language? language,
        out Parser? parser,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(parseSource);

        language = null;
        parser = null;
        failure = string.Empty;
        try
        {
            language = createLanguage();
            parser = new Parser(language);
            using var tree = parseSource(parser, "pass");
            if (tree is null || tree.RootNode.HasError)
            {
                parser.Dispose();
                language.Dispose();
                parser = null;
                language = null;
                failure = "The Tree-sitter Python grammar loaded but could not parse the fixed preflight source.";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (!IsFatalException(ex))
        {
            parser?.Dispose();
            language?.Dispose();
            parser = null;
            language = null;
            failure = $"The Tree-sitter binding returned {ex.GetType().Name} during a fixed parser preflight.";
            return false;
        }
    }

    private long BeginHarvestDiagnostics()
    {
        lock (_diagnosticsGate)
        {
            return ++_latestHarvestGeneration;
        }
    }

    private void PublishHarvestDiagnostics(long harvestGeneration, List<DocHarvestDiagnostic> diagnostics)
    {
        lock (_diagnosticsGate)
        {
            if (harvestGeneration == _latestHarvestGeneration)
            {
                _lastDiagnostics = diagnostics.ToArray();
            }
        }
    }

    private static PythonModule? CreateModule(
        Node root,
        string relativePath,
        ICollection<DocHarvestDiagnostic> diagnostics)
    {
        var moduleStatements = NonCommentNamedChildren(root);
        var boundary = ReadPublicBoundary(moduleStatements, relativePath, diagnostics);
        if (boundary is null)
        {
            if (!IsIgnorablePackageInitializer(relativePath, moduleStatements))
            {
                diagnostics.Add(CreateDiagnostic(
                    DocHarvestDiagnosticCodes.PythonPublicBoundaryMissing,
                    DocHarvestDiagnosticSeverity.Warning,
                    $"Skipped Python module '{relativePath}' because it does not declare a literal top-level __all__ boundary.",
                    "The Python spike intentionally publishes only source declarations selected by one literal top-level __all__ assignment; it does not infer public API from names or imports.",
                    "Add one unannotated literal __all__ list or tuple of exported class and function names, or remove this module from the Python include boundary."));
            }

            return null;
        }

        var declarations = moduleStatements
            .Select(UnwrapDecoratedDefinition)
            .Where(static declaration => declaration is not null)
            .Select(static declaration => CreateDeclaration(declaration!))
            .Where(static declaration => declaration is not null)
            .Select(static declaration => declaration!)
            .GroupBy(static declaration => declaration.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var supportedNames = declarations.Keys.ToHashSet(StringComparer.Ordinal);
        var declaredNames = GetDeclaredModuleNames(moduleStatements);
        var publicNames = boundary.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var exportName in publicNames)
        {
            if (supportedNames.Contains(exportName))
            {
                continue;
            }

            diagnostics.Add(CreateDiagnostic(
                declaredNames.Contains(exportName)
                    ? DocHarvestDiagnosticCodes.PythonExportNotSupported
                    : DocHarvestDiagnosticCodes.PythonExportNotFound,
                DocHarvestDiagnosticSeverity.Warning,
                declaredNames.Contains(exportName)
                    ? $"Skipped Python export '{exportName}' in '{relativePath}' because its declaration is outside this spike's class and function contract."
                    : $"Skipped Python export '{exportName}' in '{relativePath}' because no matching in-module declaration was found.",
                declaredNames.Contains(exportName)
                    ? "The Python harvester currently publishes only named module-level classes and functions selected by literal __all__."
                    : "A literal __all__ entry must match a named class or function declared in the same module.",
                declaredNames.Contains(exportName)
                    ? "Remove variables, imports, aliases, and type declarations from __all__ for this spike, or keep this module outside the Python include boundary."
                    : "Correct the __all__ entry, add the matching class or function declaration, or remove the stale export name."));
        }

        var exportedDeclarations = publicNames
            .Where(declarations.ContainsKey)
            .Select(name => declarations[name])
            .Where(static declaration => declaration.Docstring is not null || declaration.Members.Count > 0)
            .ToArray();
        var moduleDocstring = GetDocstring(root);
        if (string.IsNullOrWhiteSpace(moduleDocstring) && exportedDeclarations.Length == 0)
        {
            return null;
        }

        return new PythonModule(
            relativePath,
            CreateModuleSlug(relativePath),
            moduleDocstring,
            exportedDeclarations);
    }

    private static IReadOnlyList<string>? ReadPublicBoundary(
        IReadOnlyList<Node> statements,
        string relativePath,
        ICollection<DocHarvestDiagnostic> diagnostics)
    {
        var boundaryAssignments = new List<Node>();
        foreach (var statement in statements)
        {
            if (TryGetAllAssignment(statement, out var assignment))
            {
                boundaryAssignments.Add(assignment!);
            }
        }

        if (boundaryAssignments.Count == 0)
        {
            return null;
        }

        if (boundaryAssignments.Count != 1 || !TryReadLiteralExportNames(boundaryAssignments[0], out var names))
        {
            diagnostics.Add(CreateDiagnostic(
                DocHarvestDiagnosticCodes.PythonPublicBoundaryInvalid,
                DocHarvestDiagnosticSeverity.Warning,
                $"Skipped Python module '{relativePath}' because its __all__ boundary is not one unannotated literal list or tuple of string names.",
                "The Python spike does not execute or evaluate dynamic exports, annotations, concatenated literals, augmented assignments, or multiple __all__ declarations.",
                "Use exactly one top-level __all__ = [\"ExportedName\"] or tuple equivalent, or remove this module from the Python include boundary."));
            return null;
        }

        return names;
    }

    private static bool TryGetAllAssignment(Node statement, out Node? assignment)
    {
        assignment = null;
        var candidate = statement.Type is "assignment" or "augmented_assignment"
            ? statement
            : statement.Type == "expression_statement" ? statement.FirstNamedChild : null;
        if (candidate?.Type is not "assignment" and not "augmented_assignment")
        {
            return false;
        }

        var left = candidate.GetChildForField("left") ?? candidate.FirstNamedChild;
        if (left?.Type != "identifier" || !string.Equals(left.Text, "__all__", StringComparison.Ordinal))
        {
            return false;
        }

        assignment = candidate;
        return true;
    }

    private static bool TryReadLiteralExportNames(Node assignment, out IReadOnlyList<string> names)
    {
        names = [];
        var assignmentChildren = NonCommentNamedChildren(assignment);
        if (assignment.Type != "assignment" || assignmentChildren.Count != 2)
        {
            return false;
        }

        var left = assignment.GetChildForField("left") ?? assignmentChildren[0];
        var right = assignment.GetChildForField("right") ?? assignmentChildren[^1];
        if (left.Type != "identifier" || !string.Equals(left.Text, "__all__", StringComparison.Ordinal)
            || right.Type is not "list" and not "tuple")
        {
            return false;
        }

        var values = new List<string>();
        foreach (var value in NonCommentNamedChildren(right))
        {
            if (value.Type != "string" || !TryReadPlainString(value.Text, out var text))
            {
                return false;
            }

            values.Add(text);
        }

        names = values;
        return true;
    }

    private static Node? UnwrapDecoratedDefinition(Node node)
    {
        if (node.Type is "class_definition" or "function_definition")
        {
            return node;
        }

        return node.Type == "decorated_definition"
            ? node.NamedChildren.FirstOrDefault(static child => child.Type is "class_definition" or "function_definition")
            : null;
    }

    private static PythonDeclaration? CreateDeclaration(Node definition)
    {
        var name = definition.GetChildForField("name")?.Text;
        var body = definition.GetChildForField("body");
        if (string.IsNullOrWhiteSpace(name) || body is null)
        {
            return null;
        }

        var kind = definition.Type == "class_definition" ? PythonApiKind.Class : GetFunctionKind(definition);
        var members = kind != PythonApiKind.Class
            ? []
            : NonCommentNamedChildren(body)
                .Select(UnwrapDecoratedDefinition)
                .Where(static child => child?.Type == "function_definition")
                .Select(static child => CreateDeclaration(child!))
                .Where(static declaration => declaration is not null)
                .Select(static declaration => declaration! with { Kind = GetMethodKind(declaration!) })
                .GroupBy(static declaration => declaration.Name, StringComparer.Ordinal)
                .Select(static group => group.Last())
                .Where(static declaration => declaration.Docstring is not null)
                .OrderBy(static declaration => declaration.StartLine)
                .ToArray();

        return new PythonDeclaration(
            name,
            kind,
            definition.StartPosition.Row + 1,
            GetDocstring(body),
            members);
    }

    private static PythonApiKind GetFunctionKind(Node definition)
    {
        return definition.Children.Any(static child => child.Type == "async")
            ? PythonApiKind.AsyncFunction
            : PythonApiKind.Function;
    }

    private static PythonApiKind GetMethodKind(PythonDeclaration declaration)
    {
        return declaration.Kind == PythonApiKind.AsyncFunction
            ? PythonApiKind.AsyncMethod
            : PythonApiKind.Method;
    }

    private static HashSet<string> GetDeclaredModuleNames(IReadOnlyList<Node> statements)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in statements)
        {
            var definition = UnwrapDecoratedDefinition(statement);
            var definitionName = definition?.GetChildForField("name")?.Text;
            if (!string.IsNullOrWhiteSpace(definitionName))
            {
                names.Add(definitionName);
                continue;
            }

            if (statement.Type is "import_statement" or "import_from_statement")
            {
                AddImportedNames(statement, names);
                continue;
            }

            if (statement.Type is "assignment" or "expression_statement")
            {
                var assignment = statement.Type == "assignment" ? statement : statement.FirstNamedChild;
                var left = assignment?.GetChildForField("left") ?? assignment?.FirstNamedChild;
                if (assignment?.Type == "assignment" && left?.Type == "identifier")
                {
                    names.Add(left.Text);
                }
            }
        }

        return names;
    }

    private static string? GetDocstring(Node body)
    {
        var statement = NonCommentNamedChildren(body).FirstOrDefault();
        var text = statement is null ? null : GetDocstringFromStatement(statement);
        return text is null ? null : NormalizeDocstring(text);
    }

    private static bool TryReadPlainString(string literal, out string value)
    {
        value = string.Empty;
        foreach (var delimiter in new[] { "'''", "\"\"\"", "'", "\"" })
        {
            if (!literal.StartsWith(delimiter, StringComparison.Ordinal)
                || !literal.EndsWith(delimiter, StringComparison.Ordinal)
                || literal.Length < delimiter.Length * 2)
            {
                continue;
            }

            value = literal[delimiter.Length..^delimiter.Length];
            return true;
        }

        return false;
    }

    private static string NormalizeDocstring(string value)
    {
        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            lines[index] = ExpandTabs(lines[index]);
        }

        var indentation = lines
            .Skip(1)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Select(static line => line.Length - line.TrimStart(' ').Length)
            .DefaultIfEmpty(0)
            .Min();
        if (lines.Length > 0)
        {
            lines[0] = lines[0].TrimStart(' ');
        }

        for (var index = 1; index < lines.Length; index++)
        {
            var removable = Math.Min(indentation, lines[index].Length - lines[index].TrimStart(' ').Length);
            lines[index] = lines[index][removable..];
        }

        var first = 0;
        var last = lines.Length - 1;
        while (first <= last && string.IsNullOrWhiteSpace(lines[first]))
        {
            first++;
        }

        while (last >= first && string.IsNullOrWhiteSpace(lines[last]))
        {
            last--;
        }

        return first > last ? string.Empty : string.Join("\n", lines[first..(last + 1)]);
    }

    private static string ExpandTabs(string value)
    {
        if (!value.Contains('\t', StringComparison.Ordinal))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var column = 0;
        foreach (var character in value)
        {
            if (character == '\t')
            {
                var count = 8 - (column % 8);
                builder.Append(' ', count);
                column += count;
                continue;
            }

            builder.Append(character);
            column++;
        }

        return builder.ToString();
    }

    private static bool IsIgnorablePackageInitializer(string relativePath, IReadOnlyList<Node> statements)
    {
        if (!relativePath.EndsWith("/__init__.py", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(relativePath, "__init__.py", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var executableStatements = statements.Where(static statement => statement.Type != "comment").ToArray();
        return executableStatements.Length == 0
            || (executableStatements.Length == 1 && GetDocstringFromStatement(executableStatements[0]) is not null);
    }

    private static string? GetDocstringFromStatement(Node statement)
    {
        if (statement.Type == "string")
        {
            return TryReadPlainString(statement.Text, out var directText) ? directText : null;
        }

        var children = NonCommentNamedChildren(statement);
        if (statement.Type != "expression_statement" || children.Count != 1)
        {
            return null;
        }

        var literal = children[0];
        return literal?.Type == "string" && TryReadPlainString(literal.Text, out var text)
            ? text
            : null;
    }

    private static IReadOnlyList<Node> NonCommentNamedChildren(Node node) =>
        node.NamedChildren.Where(static child => child.Type != "comment").ToArray();

    private static void AddImportedNames(Node statement, ISet<string> names)
    {
        var importedNames = statement.Type == "import_from_statement"
            ? statement.GetChildForField("name") is { } importNames
                ? NonCommentNamedChildren(importNames)
                : []
            : NonCommentNamedChildren(statement);
        foreach (var child in importedNames)
        {
            if (child.Type == "aliased_import")
            {
                var alias = child.GetChildForField("alias")?.Text;
                var imported = child.GetChildForField("name")?.Text;
                var boundName = alias ?? imported;
                if (!string.IsNullOrWhiteSpace(boundName))
                {
                    names.Add(statement.Type == "import_statement" && alias is null
                        ? boundName.Split('.')[0]
                        : boundName);
                }
            }
            else if (child.Type is "dotted_name" or "identifier")
            {
                names.Add(statement.Type == "import_statement" ? child.Text.Split('.')[0] : child.Text);
            }
        }
    }

    private static IReadOnlyList<DocNode> BuildDocNodes(
        IReadOnlyList<PythonModule> modules,
        ICollection<DocHarvestDiagnostic> diagnostics)
    {
        var collidingModules = modules
            .GroupBy(static module => module.Slug, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .SelectMany(static group => group)
            .ToHashSet();
        foreach (var module in collidingModules.OrderBy(static module => module.RelativePath, StringComparer.Ordinal))
        {
            diagnostics.Add(CreateDiagnostic(
                DocHarvestDiagnosticCodes.PythonSlugCollision,
                DocHarvestDiagnosticSeverity.Warning,
                $"Skipped Python module '{module.RelativePath}' because its generated API route collides with another Python module.",
                $"The normalized module route seed 'api/python/{module.Slug}' is not unique among configured Python sources.",
                "Rename one source path, narrow the Python include boundary, or retain distinct directory segments so each documented Python module has a unique route."));
        }

        var nodes = new List<DocNode>();
        foreach (var module in modules
                     .Where(module => !collidingModules.Contains(module))
                     .OrderBy(static module => module.RelativePath, StringComparer.Ordinal))
        {
            nodes.AddRange(BuildModuleNodes(module));
        }

        return nodes;
    }

    private static IReadOnlyList<DocNode> BuildModuleNodes(PythonModule module)
    {
        var modulePath = $"api/python/{module.Slug}";
        var title = $"{module.DisplayName} Python API";
        var anchors = CreateAnchorMap(module.Declarations);
        var content = new StringBuilder();
        var outline = new List<DocOutlineItem>();
        var provenance = new List<DocSymbolSourceProvenance>();
        content.Append(DocPolyglotOwnershipLinker.CreatePythonModuleMarker(module.RelativePath));
        content.Append($"<section class=\"doc-type doc-python-api\"><header class=\"doc-type-header\"><span class=\"doc-kind\">Python API</span><h2>{WebUtility.HtmlEncode(title)}</h2></header><div class=\"doc-body\">");
        if (!string.IsNullOrWhiteSpace(module.Docstring))
        {
            AppendDocstring(content, module.Docstring!);
        }

        foreach (var declaration in module.Declarations)
        {
            AppendDeclaration(content, outline, provenance, module.RelativePath, declaration, anchors, headingLevel: 2);
        }

        content.Append("</div></section>");
        var nodes = new List<DocNode>
        {
            new(
                title,
                modulePath,
                content.ToString(),
                Metadata: CreatePythonMetadata(title, "python-module", module, order: 250),
                Outline: outline,
                SymbolSourceProvenance: provenance)
        };

        foreach (var declaration in module.Declarations)
        {
            AddSymbolNodes(nodes, modulePath, module, declaration, anchors, parentDisplayName: null);
        }

        return nodes;
    }

    private static void AddSymbolNodes(
        ICollection<DocNode> nodes,
        string modulePath,
        PythonModule module,
        PythonDeclaration declaration,
        IReadOnlyDictionary<PythonDeclaration, string> anchors,
        string? parentDisplayName)
    {
        var anchor = anchors[declaration];
        var title = parentDisplayName is null ? declaration.Name : $"{parentDisplayName}.{declaration.Name}";
        nodes.Add(
            new DocNode(
                title,
                $"{modulePath}#{anchor}",
                RenderSymbolContent(declaration, anchor, title),
                modulePath,
                Metadata: CreatePythonMetadata(title, GetPageType(declaration.Kind), module, order: 251),
                Outline:
                [
                    new DocOutlineItem
                        {
                            Id = anchor,
                            Title = title,
                            Level = parentDisplayName is null ? 2 : 3
                    }
                ])
            {
                GeneratedApiSymbol = new DocGeneratedApiSymbol("public", "Public API", false),
                HasGeneratedApiSymbolProvenance = true
            });

        foreach (var member in declaration.Members)
        {
            AddSymbolNodes(nodes, modulePath, module, member, anchors, declaration.Name);
        }
    }

    private static void AppendDeclaration(
        StringBuilder content,
        ICollection<DocOutlineItem> outline,
        ICollection<DocSymbolSourceProvenance> provenance,
        string relativePath,
        PythonDeclaration declaration,
        IReadOnlyDictionary<PythonDeclaration, string> anchors,
        int headingLevel)
    {
        var anchor = anchors[declaration];
        outline.Add(new DocOutlineItem { Id = anchor, Title = declaration.Name, Level = headingLevel });
        provenance.Add(new DocSymbolSourceProvenance { AnchorId = anchor, SourcePath = relativePath, StartLine = declaration.StartLine });
        // Like JavaScript API pages, keep the compact outline at levels 2/3 while nesting HTML below the module h2.
        var renderedHeadingLevel = headingLevel + 1;
        content.Append($"<section id=\"{WebUtility.HtmlEncode(anchor)}\" class=\"doc-method-group doc-python-item doc-python-{GetKindSlug(declaration.Kind)}\"><header class=\"doc-method-group-header\"><span class=\"doc-kind\">{GetKindLabel(declaration.Kind)}</span><h{renderedHeadingLevel}>{WebUtility.HtmlEncode(declaration.Name)}</h{renderedHeadingLevel}><span data-appsurfacedocs-symbol-source=\"{WebUtility.HtmlEncode(anchor)}\"></span></header><div class=\"doc-body\">");
        if (!string.IsNullOrWhiteSpace(declaration.Docstring))
        {
            AppendDocstring(content, declaration.Docstring!);
        }

        foreach (var member in declaration.Members)
        {
            AppendDeclaration(content, outline, provenance, relativePath, member, anchors, headingLevel + 1);
        }

        content.Append("</div></section>");
    }

    private static string RenderSymbolContent(PythonDeclaration declaration, string anchor, string title)
    {
        var content = new StringBuilder();
        content.Append($"<section id=\"{WebUtility.HtmlEncode(anchor)}\" class=\"doc-method-group doc-python-item doc-python-{GetKindSlug(declaration.Kind)}\"><header class=\"doc-method-group-header\"><span class=\"doc-kind\">{GetKindLabel(declaration.Kind)}</span><h2>{WebUtility.HtmlEncode(title)}</h2></header><div class=\"doc-body\">");
        if (!string.IsNullOrWhiteSpace(declaration.Docstring))
        {
            AppendDocstring(content, declaration.Docstring!);
        }

        content.Append("</div></section>");
        return content.ToString();
    }

    private static void AppendDocstring(StringBuilder content, string docstring)
    {
        content.Append("<p>");
        content.Append(WebUtility.HtmlEncode(docstring).Replace("\n", "<br />", StringComparison.Ordinal));
        content.Append("</p>");
    }

    private static DocMetadata CreatePythonMetadata(string title, string pageType, PythonModule module, int order)
    {
        var baseMetadata = DocMetadataFactory.CreateApiReferenceMetadata(title, module.DisplayName);
        return baseMetadata with
        {
            PageType = pageType,
            Component = module.DisplayName,
            CodeLanguage = "python",
            CanonicalSlug = $"api/python/{module.Slug}",
            Order = order,
            Keywords = ["Python", "docstring", module.DisplayName],
            Aliases = [$"{module.DisplayName} Python"],
            Breadcrumbs = ["API Reference", "Python", module.DisplayName],
            BreadcrumbsMatchPathTargets = true
        };
    }

    private static string CreateModuleSlug(string relativePath)
    {
        var pathWithoutExtension = relativePath[..^Path.GetExtension(relativePath).Length];
        var segments = pathWithoutExtension.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count > 0 && string.Equals(segments[^1], "__init__", StringComparison.OrdinalIgnoreCase))
        {
            segments.RemoveAt(segments.Count - 1);
        }

        var seed = segments.Count == 0 ? "module" : string.Join('-', segments);
        var slug = UnsafeSlugCharacterRegex.Replace(seed.ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "module" : slug;
    }

    private static string CreateAnchor(PythonDeclaration declaration, string? parentName)
    {
        var name = parentName is null ? declaration.Name : $"{parentName}-{declaration.Name}";
        return $"{GetKindSlug(declaration.Kind)}-{UnsafeSlugCharacterRegex.Replace(name.ToLowerInvariant(), "-").Trim('-')}";
    }

    private static IReadOnlyDictionary<PythonDeclaration, string> CreateAnchorMap(
        IReadOnlyList<PythonDeclaration> declarations)
    {
        var anchors = new Dictionary<PythonDeclaration, string>(ReferenceEqualityComparer.Instance);
        AddAnchors(declarations, parentAnchor: null, anchors, new HashSet<string>(StringComparer.Ordinal));
        return anchors;
    }

    private static void AddAnchors(
        IReadOnlyList<PythonDeclaration> declarations,
        string? parentAnchor,
        IDictionary<PythonDeclaration, string> anchors,
        ISet<string> usedAnchors)
    {
        foreach (var group in declarations
                     .Select(declaration => new AnchorCandidate(declaration, CreateAnchor(declaration, parentAnchor)))
                     .GroupBy(static candidate => candidate.Anchor, StringComparer.Ordinal))
        {
            var candidates = group
                .OrderBy(static candidate => candidate.Declaration.Name, StringComparer.Ordinal)
                .ThenBy(static candidate => candidate.Declaration.StartLine)
                .ToArray();
            for (var index = 0; index < candidates.Length; index++)
            {
                var candidate = candidates[index];
                var anchor = index == 0
                    ? candidate.Anchor
                    : $"{candidate.Anchor}-{Convert.ToHexString(Encoding.UTF8.GetBytes(candidate.Declaration.Name)).ToLowerInvariant()}";
                var uniqueAnchor = anchor;
                for (var suffix = 2; !usedAnchors.Add(uniqueAnchor); suffix++)
                {
                    uniqueAnchor = $"{anchor}-{suffix.ToString(CultureInfo.InvariantCulture)}";
                }

                anchors.Add(candidate.Declaration, uniqueAnchor);
            }
        }

        foreach (var declaration in declarations)
        {
            AddAnchors(declaration.Members, anchors[declaration], anchors, usedAnchors);
        }
    }

    private static string GetKindSlug(PythonApiKind kind) => kind switch
    {
        PythonApiKind.Class => "class",
        PythonApiKind.Function => "function",
        PythonApiKind.AsyncFunction => "async-function",
        PythonApiKind.Method => "method",
        PythonApiKind.AsyncMethod => "async-method",
        _ => "symbol"
    };

    private static string GetKindLabel(PythonApiKind kind) => kind switch
    {
        PythonApiKind.Class => "Python Class",
        PythonApiKind.Function => "Python Function",
        PythonApiKind.AsyncFunction => "Python Async Function",
        PythonApiKind.Method => "Python Method",
        PythonApiKind.AsyncMethod => "Python Async Method",
        _ => "Python Symbol"
    };

    private static string GetPageType(PythonApiKind kind) => $"python-{GetKindSlug(kind)}";

    private static IReadOnlyList<string> GetUsableIncludePatterns(IEnumerable<string>? patterns)
    {
        return (patterns ?? [])
            .Where(static pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(static pattern => AppSurfaceDocsHarvestPathPatternValidator.NormalizeSlashes(pattern.Trim()))
            .Where(AppSurfaceDocsHarvestPathPatternValidator.IsValidConfiguredGlobPattern)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeRelativePath(string value) => value.Replace('\\', '/');

    private static bool IsFileReadException(Exception exception) => exception is IOException or UnauthorizedAccessException;

    private static bool IsFatalException(Exception exception) => exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private static DocHarvestDiagnostic CreateDiagnostic(
        string code,
        DocHarvestDiagnosticSeverity severity,
        string problem,
        string cause,
        string fix) => new(code, severity, HarvesterType, problem, cause, fix);

    private sealed record PythonModule(
        string RelativePath,
        string Slug,
        string? Docstring,
        IReadOnlyList<PythonDeclaration> Declarations)
    {
        public string DisplayName => RelativePath.EndsWith("/__init__.py", StringComparison.OrdinalIgnoreCase)
            ? RelativePath[..^"/__init__.py".Length].Replace('/', '.')
            : Path.ChangeExtension(RelativePath, null)!.Replace('/', '.');
    }

    private sealed record PythonDeclaration(
        string Name,
        PythonApiKind Kind,
        int StartLine,
        string? Docstring,
        IReadOnlyList<PythonDeclaration> Members);

    private sealed record AnchorCandidate(PythonDeclaration Declaration, string Anchor);

    private enum PythonApiKind
    {
        Class,
        Function,
        AsyncFunction,
        Method,
        AsyncMethod
    }
}
