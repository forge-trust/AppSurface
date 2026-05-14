using System.Text;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.MarkdownSnippets;

internal static class Program
{
    private const string GenerateCommand = "generate";
    private const string VerifyCommand = "verify";

    private static readonly string Usage = """
        ForgeTrust.AppSurface.MarkdownSnippets

        Generates and verifies source-owned Markdown snippets.

        Usage:
          dotnet run --project tools/ForgeTrust.AppSurface.MarkdownSnippets/ForgeTrust.AppSurface.MarkdownSnippets.csproj -- <command> [options]

        Commands:
          generate    Rewrites managed snippet blocks in a Markdown document.
          verify      Checks that managed snippet blocks are already up to date.

        Options:
          --repo-root <path>    Repository root. Defaults to the current directory.
          --document <path>     Markdown document to update. Defaults to Web/ForgeTrust.RazorWire/README.md.
          -h, --help            Show this help.
        """;

    /// <summary>
    /// Process entry point that runs the CLI against the current working directory.
    /// </summary>
    /// <param name="args">Command-line arguments passed by the host process.</param>
    /// <returns>The process exit code.</returns>
    internal static async Task<int> Main(string[] args)
    {
        return await RunAsync(args, Console.Out, Console.Error, Directory.GetCurrentDirectory());
    }

    /// <summary>
    /// Runs the Markdown snippet CLI command contract.
    /// </summary>
    /// <param name="args">
    /// Command-line arguments. The first argument must be <c>generate</c>,
    /// <c>verify</c>, <c>--help</c>, or <c>-h</c>. Command options after
    /// <c>generate</c> or <c>verify</c> are parsed by
    /// <see cref="MarkdownSnippetCommandOptions.Parse"/>.
    /// </param>
    /// <param name="standardOut">Destination for help and successful command output.</param>
    /// <param name="standardError">Destination for validation and command failure output.</param>
    /// <param name="currentDirectory">
    /// Working directory used for option defaults. When <c>--repo-root</c> is
    /// omitted, this becomes the repository root; when <c>--document</c> is
    /// omitted, the default document is
    /// <c>Web/ForgeTrust.RazorWire/README.md</c> under that root.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for file IO.</param>
    /// <returns><c>0</c> for success or help, <c>1</c> for invalid input or stale snippets.</returns>
    /// <remarks>
    /// Relative <c>--repo-root</c> values resolve from <paramref name="currentDirectory"/>;
    /// relative <c>--document</c> values resolve from the resolved repository root.
    /// Unknown commands, unknown options, missing option values, invalid snippet
    /// directives, unsafe paths, and stale generated blocks are reported as
    /// <c>1</c> with actionable error text rather than escaping as unhandled exceptions.
    /// </remarks>
    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter standardOut,
        TextWriter standardError,
        string currentDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardOut);
        ArgumentNullException.ThrowIfNull(standardError);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        if (args.Length == 0)
        {
            await standardError.WriteLineAsync(Usage);
            return 1;
        }

        if (IsHelp(args[0]))
        {
            await standardOut.WriteLineAsync(Usage);
            return 0;
        }

        try
        {
            var command = args[0].Trim();
            if (args.Skip(1).Any(IsHelp))
            {
                await standardOut.WriteLineAsync(Usage);
                return 0;
            }

            var normalizedCommand = command.ToLowerInvariant();
            if (normalizedCommand is not GenerateCommand and not VerifyCommand)
            {
                await standardError.WriteLineAsync($"Unknown command '{command}'.");
                await standardError.WriteLineAsync(Usage);
                return 1;
            }

            var options = MarkdownSnippetCommandOptions.Parse(args.Skip(1).ToArray(), currentDirectory);
            var generator = new MarkdownSnippetGenerator();

            if (normalizedCommand == GenerateCommand)
            {
                await generator.GenerateToFileAsync(options.Request, cancellationToken);
                await standardOut.WriteLineAsync($"Generated {options.Request.GetRepositoryRelativeDocumentPath()}.");
                return 0;
            }

            await generator.VerifyAsync(options.Request, cancellationToken);
            await standardOut.WriteLineAsync("Markdown snippets are up to date.");
            return 0;
        }
        catch (MarkdownSnippetException ex)
        {
            await standardError.WriteLineAsync(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Determines whether an argument requests command help.
    /// </summary>
    /// <param name="argument">Argument to inspect.</param>
    /// <returns><c>true</c> for <c>--help</c> or <c>-h</c>.</returns>
    private static bool IsHelp(string argument)
    {
        return string.Equals(argument, "--help", StringComparison.Ordinal)
            || string.Equals(argument, "-h", StringComparison.Ordinal);
    }
}

internal sealed record MarkdownSnippetCommandOptions(MarkdownSnippetRequest Request)
{
    /// <summary>
    /// Parses command options into a repository/document request.
    /// </summary>
    /// <param name="args">Only <c>--repo-root &lt;path&gt;</c> and <c>--document &lt;path&gt;</c> are supported.</param>
    /// <param name="currentDirectory">Current working directory used by omitted or relative <c>--repo-root</c>.</param>
    /// <returns>Parsed options with full repository and document paths.</returns>
    /// <exception cref="MarkdownSnippetException">
    /// Thrown when an option is unknown or a required option value is missing.
    /// </exception>
    /// <remarks>
    /// The default repository root is <paramref name="currentDirectory"/>. The
    /// default document is
    /// <c>Web/ForgeTrust.RazorWire/README.md</c> under the resolved
    /// repository root. A rooted <c>--document</c> is allowed at parse time but
    /// later rejected by generation/verification when it is outside the repository.
    /// </remarks>
    internal static MarkdownSnippetCommandOptions Parse(string[] args, string currentDirectory)
    {
        string? repositoryRoot = null;
        string? documentPath = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--repo-root", StringComparison.Ordinal))
            {
                repositoryRoot = ReadRequiredValue(args, ref index, argument);
                continue;
            }

            if (string.Equals(argument, "--document", StringComparison.Ordinal))
            {
                documentPath = ReadRequiredValue(args, ref index, argument);
                continue;
            }

            throw new MarkdownSnippetException($"Unknown option '{argument}'.");
        }

        var repoRoot = ResolvePath(repositoryRoot, currentDirectory, currentDirectory);
        var resolvedDocumentPath = ResolvePath(
            documentPath,
            repoRoot,
            Path.Combine(repoRoot, "Web", "ForgeTrust.RazorWire", "README.md"));

        return new MarkdownSnippetCommandOptions(new MarkdownSnippetRequest(repoRoot, resolvedDocumentPath));
    }

    /// <summary>
    /// Reads the required value immediately following an option name.
    /// </summary>
    /// <param name="args">Complete option argument array.</param>
    /// <param name="index">Current option index, advanced to the value index on success.</param>
    /// <param name="argument">Option name for diagnostics.</param>
    /// <returns>The raw option value.</returns>
    /// <exception cref="MarkdownSnippetException">
    /// Thrown when the option has no following non-blank value or the next token
    /// looks like another option.
    /// </exception>
    private static string ReadRequiredValue(string[] args, ref int index, string argument)
    {
        if (index + 1 >= args.Length
            || string.IsNullOrWhiteSpace(args[index + 1])
            || args[index + 1].StartsWith("-", StringComparison.Ordinal))
        {
            throw new MarkdownSnippetException($"Option '{argument}' requires a value.");
        }

        index++;
        return args[index];
    }

    /// <summary>
    /// Resolves a rooted, relative, or omitted path to a full path.
    /// </summary>
    /// <param name="value">Optional user-provided path.</param>
    /// <param name="baseDirectory">Directory used for relative values.</param>
    /// <param name="defaultPath">Path used when <paramref name="value"/> is omitted.</param>
    /// <returns>A canonical full path.</returns>
    private static string ResolvePath(string? value, string baseDirectory, string defaultPath)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Path.GetFullPath(defaultPath);
        }

        return Path.IsPathRooted(value)
            ? Path.GetFullPath(value)
            : Path.GetFullPath(Path.Combine(baseDirectory, value));
    }
}

internal sealed record MarkdownSnippetRequest(string RepositoryRoot, string DocumentPath)
{
    /// <summary>
    /// Gets the document path relative to <see cref="RepositoryRoot"/> with
    /// forward slashes for stable diagnostics and CLI output.
    /// </summary>
    /// <returns>The repository-relative document path.</returns>
    /// <remarks>
    /// Request validation ensures the document stays under the repository root
    /// before generation or verification uses this value for user-facing messages.
    /// </remarks>
    internal string GetRepositoryRelativeDocumentPath()
    {
        return Path.GetRelativePath(RepositoryRoot, DocumentPath).Replace('\\', '/');
    }
}

internal sealed class MarkdownSnippetGenerator
{
    /// <summary>
    /// Generates the canonical Markdown document and writes it back to disk.
    /// </summary>
    /// <param name="request">Repository root and Markdown document to update.</param>
    /// <param name="cancellationToken">Cancellation token for file IO.</param>
    /// <exception cref="MarkdownSnippetException">
    /// Thrown when the repository/document path is invalid or snippet extraction fails.
    /// </exception>
    internal async Task GenerateToFileAsync(MarkdownSnippetRequest request, CancellationToken cancellationToken = default)
    {
        var markdown = await GenerateAsync(request, cancellationToken);
        await File.WriteAllTextAsync(request.DocumentPath, markdown, cancellationToken);
    }

    /// <summary>
    /// Generates the canonical Markdown text for all managed snippet blocks.
    /// </summary>
    /// <param name="request">Repository root and Markdown document to read.</param>
    /// <param name="cancellationToken">Cancellation token for file IO.</param>
    /// <returns>The rewritten Markdown document with <c>\n</c> line endings.</returns>
    /// <exception cref="MarkdownSnippetException">
    /// Thrown when the document has no managed blocks, a block is malformed, a
    /// source path escapes the repository, markers are missing or duplicated, or
    /// extracted snippet content is empty.
    /// </exception>
    /// <remarks>
    /// Generation is the mutating workflow used by maintainers after source
    /// sample changes. It normalizes document line endings and replaces only
    /// managed blocks outside existing Markdown code fences.
    /// </remarks>
    internal async Task<string> GenerateAsync(MarkdownSnippetRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var markdown = await File.ReadAllTextAsync(request.DocumentPath, cancellationToken);
        return MarkdownSnippetRewriter.Rewrite(request, markdown);
    }

    /// <summary>
    /// Verifies that the checked-in Markdown already matches generated output.
    /// </summary>
    /// <param name="request">Repository root and Markdown document to verify.</param>
    /// <param name="cancellationToken">Cancellation token for file IO.</param>
    /// <exception cref="MarkdownSnippetException">
    /// Thrown when validation fails or the checked-in Markdown is stale.
    /// </exception>
    /// <remarks>
    /// Verification is the CI workflow. It compares canonical generated text to
    /// the current document after normalizing line endings, so CRLF checkouts do
    /// not fail solely because of platform line-ending conversion.
    /// </remarks>
    internal async Task VerifyAsync(MarkdownSnippetRequest request, CancellationToken cancellationToken = default)
    {
        var expected = await GenerateAsync(request, cancellationToken);
        var current = (await File.ReadAllTextAsync(request.DocumentPath, cancellationToken))
            .ReplaceLineEndings("\n");
        if (!string.Equals(current, expected, StringComparison.Ordinal))
        {
            throw new MarkdownSnippetException(
                $"Generated snippets in '{request.GetRepositoryRelativeDocumentPath()}' are stale. Run the Markdown snippet generator.");
        }
    }

    /// <summary>
    /// Validates repository/document existence and document containment.
    /// </summary>
    /// <param name="request">Request to validate before source files are read.</param>
    /// <exception cref="MarkdownSnippetException">
    /// Thrown when the repository root is missing, the document is missing, or
    /// the document is outside the repository root.
    /// </exception>
    private static void ValidateRequest(MarkdownSnippetRequest request)
    {
        if (!Directory.Exists(request.RepositoryRoot))
        {
            throw new MarkdownSnippetException(
                $"Repository root '{request.RepositoryRoot}' does not exist for Markdown document '{request.DocumentPath}'.");
        }

        if (!File.Exists(request.DocumentPath))
        {
            throw new MarkdownSnippetException(
                $"Markdown document '{Path.GetRelativePath(request.RepositoryRoot, request.DocumentPath)}' does not exist.");
        }

        if (!MarkdownSnippetPath.TryGetRepositoryRelativePath(
                request.RepositoryRoot,
                request.DocumentPath,
                out _))
        {
            throw new MarkdownSnippetException(
                $"Markdown document '{request.DocumentPath}' must be under repository root '{request.RepositoryRoot}'.");
        }
    }
}

/// <summary>
/// Rewrites managed <c>&lt;!-- appsurface:snippet ... --&gt;</c> blocks in Markdown.
/// </summary>
/// <remarks>
/// A managed block must contain an opening directive, a generated fenced code
/// block, and the exact closing directive <c>&lt;!-- /appsurface:snippet --&gt;</c>.
/// Directives are ignored inside existing Markdown code fences, and the managed
/// closing directive is only recognized outside the generated block's code fence.
///
/// Supported attributes are <c>id</c>, <c>file</c>, <c>marker</c>, <c>lang</c>,
/// and optional <c>dedent</c>. Attributes must use quoted
/// <c>name="value"</c> syntax. <c>file</c> is repository-relative; rooted paths
/// and <c>..</c> escapes fail before source files are read. <c>dedent</c>
/// defaults to <c>true</c>, which removes common indentation from extracted
/// non-blank source lines.
/// </remarks>
internal static partial class MarkdownSnippetRewriter
{
    private const string OpeningPrefix = "<!-- appsurface:snippet ";
    private const string ClosingLine = "<!-- /appsurface:snippet -->";

    [GeneratedRegex("(?<name>[A-Za-z][A-Za-z0-9_-]*)=\"(?<value>[^\"]*)\"", RegexOptions.NonBacktracking)]
    private static partial Regex AttributeRegex();

    /// <summary>
    /// Rewrites managed snippet blocks and returns canonical Markdown.
    /// </summary>
    /// <param name="request">Repository root and document path used for path resolution and diagnostics.</param>
    /// <param name="markdown">Original Markdown text.</param>
    /// <returns>Markdown with generated blocks rendered using <c>\n</c> line endings.</returns>
    /// <exception cref="MarkdownSnippetException">
    /// Thrown when no managed blocks are present, a directive is malformed, a
    /// source file is unsafe or missing, markers are invalid, or a block is not closed.
    /// </exception>
    internal static string Rewrite(MarkdownSnippetRequest request, string markdown)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(markdown);

        var normalized = markdown.ReplaceLineEndings("\n");
        var lines = normalized.Split('\n');
        var builder = new StringBuilder(normalized.Length);
        MarkdownCodeFence? activeCodeFence = null;
        var snippetCount = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var structuralLine = TrimMarkdownLinePrefix(line);

            var codeFence = MarkdownCodeFence.TryParse(structuralLine);
            if (activeCodeFence is null && codeFence is not null)
            {
                activeCodeFence = codeFence;
            }
            else if (activeCodeFence is not null && activeCodeFence.Value.IsClosedBy(structuralLine))
            {
                activeCodeFence = null;
            }

            if (activeCodeFence is null && IsOpeningDirectiveLine(line))
            {
                var block = ParseBlock(request, lines, ref index);
                builder.Append(block.Render());
                snippetCount++;
                continue;
            }

            AppendLine(builder, line, index, lines.Length);
        }

        if (snippetCount == 0)
        {
            throw new MarkdownSnippetException(
                $"Markdown document '{request.GetRepositoryRelativeDocumentPath()}' does not contain any executable snippet blocks.");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Parses one managed snippet block and extracts its replacement content.
    /// </summary>
    /// <param name="request">Repository/document request for path resolution and diagnostics.</param>
    /// <param name="lines">Normalized Markdown lines.</param>
    /// <param name="index">Current opening-line index, advanced to the managed closing line.</param>
    /// <returns>The parsed block with source-backed content.</returns>
    /// <exception cref="MarkdownSnippetException">
    /// Thrown when attributes are invalid, the source file path is unsafe, source
    /// markers are invalid, or the managed block is missing its closing directive.
    /// </exception>
    private static MarkdownSnippetBlock ParseBlock(MarkdownSnippetRequest request, string[] lines, ref int index)
    {
        var openingLine = lines[index].TrimEnd();
        var linePrefix = GetLinePrefix(openingLine);
        var openingDirective = openingLine[linePrefix.Length..].Trim();
        var attributes = ParseAttributes(openingDirective, request.GetRepositoryRelativeDocumentPath(), index + 1);
        var id = ReadRequiredAttribute(attributes, "id", request, index + 1);
        var file = ReadRequiredAttribute(attributes, "file", request, index + 1);
        var marker = ReadRequiredAttribute(attributes, "marker", request, index + 1);
        var language = ReadRequiredAttribute(attributes, "lang", request, index + 1);
        var dedent = ReadBooleanAttribute(attributes, "dedent", defaultValue: true, request, index + 1);

        ValidateId(id, "snippet id", request, index + 1);
        ValidateId(marker, "snippet marker", request, index + 1);
        ValidateLanguage(language, request, index + 1);

        var sourcePath = MarkdownSnippetPath.ResolveRepositoryFilePath(request.RepositoryRoot, file, "Snippet source file");
        var source = File.ReadAllText(sourcePath);
        var content = MarkdownSnippetSourceExtractor.Extract(
            source,
            marker,
            file,
            dedent);

        var closeIndex = FindClosingLine(lines, index + 1, request, id);
        index = closeIndex;
        return new MarkdownSnippetBlock(openingLine, linePrefix, language, content);
    }

    private static bool IsOpeningDirectiveLine(string line)
    {
        return TrimMarkdownLinePrefix(line).StartsWith(OpeningPrefix, StringComparison.Ordinal);
    }

    private static string GetLinePrefix(string line)
    {
        return line[..GetMarkdownLinePrefixLength(line)];
    }

    private static string TrimMarkdownLinePrefix(string line)
    {
        return line[GetMarkdownLinePrefixLength(line)..].Trim();
    }

    private static int GetMarkdownLinePrefixLength(string line)
    {
        var index = 0;
        while (index < line.Length)
        {
            while (index < line.Length && (line[index] == ' ' || line[index] == '\t'))
            {
                index++;
            }

            if (index >= line.Length || line[index] != '>')
            {
                return index;
            }

            index++;
            if (index < line.Length && line[index] == ' ')
            {
                index++;
            }
        }

        return index;
    }

    /// <summary>
    /// Parses and validates quoted directive attributes from an opening line.
    /// </summary>
    /// <exception cref="MarkdownSnippetException">
    /// Thrown for missing <c>--&gt;</c>, duplicate attributes, unsupported
    /// attribute syntax, empty attribute sets, or unknown attribute names.
    /// </exception>
    private static Dictionary<string, string> ParseAttributes(string openingLine, string documentPath, int lineNumber)
    {
        var endIndex = openingLine.IndexOf("-->", StringComparison.Ordinal);
        if (endIndex < 0)
        {
            throw new MarkdownSnippetException(
                $"Snippet block in '{documentPath}' at line {lineNumber} is missing closing '-->'.");
        }

        var attributeText = openingLine[OpeningPrefix.Length..endIndex];
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        var matchedLength = 0;
        foreach (Match match in AttributeRegex().Matches(attributeText))
        {
            matchedLength += match.Length;
            var name = match.Groups["name"].Value;
            if (!attributes.TryAdd(name, match.Groups["value"].Value))
            {
                throw new MarkdownSnippetException(
                    $"Snippet block in '{documentPath}' at line {lineNumber} defines attribute '{name}' more than once.");
            }
        }

        var remaining = AttributeRegex().Replace(attributeText, string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(remaining))
        {
            throw new MarkdownSnippetException(
                $"Snippet block in '{documentPath}' at line {lineNumber} contains unsupported attribute syntax '{remaining}'.");
        }

        if (matchedLength == 0)
        {
            throw new MarkdownSnippetException(
                $"Snippet block in '{documentPath}' at line {lineNumber} must define attributes.");
        }

        ValidateAttributeNames(attributes, documentPath, lineNumber);

        return attributes;
    }

    /// <summary>
    /// Ensures all directive attribute names are supported by the snippet contract.
    /// </summary>
    /// <param name="attributes">Parsed directive attributes.</param>
    /// <param name="documentPath">Repository-relative document path for diagnostics.</param>
    /// <param name="lineNumber">One-based directive line number.</param>
    /// <exception cref="MarkdownSnippetException">Thrown when any attribute name is unknown.</exception>
    private static void ValidateAttributeNames(
        IReadOnlyDictionary<string, string> attributes,
        string documentPath,
        int lineNumber)
    {
        foreach (var name in attributes.Keys)
        {
            if (name is not ("id" or "file" or "marker" or "lang" or "dedent"))
            {
                throw new MarkdownSnippetException(
                    $"Snippet block in '{documentPath}' at line {lineNumber} defines unsupported attribute '{name}'.");
            }
        }
    }

    /// <summary>
    /// Reads a required directive attribute and trims its value.
    /// </summary>
    /// <exception cref="MarkdownSnippetException">Thrown when the attribute is missing or blank.</exception>
    private static string ReadRequiredAttribute(
        IReadOnlyDictionary<string, string> attributes,
        string name,
        MarkdownSnippetRequest request,
        int lineNumber)
    {
        if (!attributes.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new MarkdownSnippetException(
                $"Snippet block in '{request.GetRepositoryRelativeDocumentPath()}' at line {lineNumber} must define '{name}'.");
        }

        return value.Trim();
    }

    /// <summary>
    /// Reads an optional boolean directive attribute.
    /// </summary>
    /// <remarks>
    /// <c>dedent</c> uses this helper with a default of <c>true</c>. Only
    /// <c>true</c> and <c>false</c> values accepted by the platform boolean parser are valid.
    /// </remarks>
    /// <exception cref="MarkdownSnippetException">Thrown when the value is not a valid boolean.</exception>
    private static bool ReadBooleanAttribute(
        IReadOnlyDictionary<string, string> attributes,
        string name,
        bool defaultValue,
        MarkdownSnippetRequest request,
        int lineNumber)
    {
        if (!attributes.TryGetValue(name, out var value))
        {
            return defaultValue;
        }

        if (bool.TryParse(value.Trim(), out var parsed))
        {
            return parsed;
        }

        throw new MarkdownSnippetException(
            $"Snippet block in '{request.GetRepositoryRelativeDocumentPath()}' at line {lineNumber} has invalid boolean '{name}' value '{value}'.");
    }

    /// <summary>
    /// Validates a snippet id or source marker id.
    /// </summary>
    /// <param name="value">Candidate id.</param>
    /// <param name="description">Diagnostic description of the id role.</param>
    /// <param name="request">Request used to name the document in diagnostics.</param>
    /// <param name="lineNumber">One-based directive line number.</param>
    /// <exception cref="MarkdownSnippetException">Thrown when the id uses unsupported characters.</exception>
    private static void ValidateId(string value, string description, MarkdownSnippetRequest request, int lineNumber)
    {
        if (!MarkdownSnippetMarker.IsValidId(value))
        {
            throw new MarkdownSnippetException(
                $"Snippet block in '{request.GetRepositoryRelativeDocumentPath()}' at line {lineNumber} has invalid {description} '{value}'. Use letters, numbers, '.', '_' or '-'.");
        }
    }

    /// <summary>
    /// Validates the Markdown code fence language token.
    /// </summary>
    /// <param name="language">Candidate language token.</param>
    /// <param name="request">Request used to name the document in diagnostics.</param>
    /// <param name="lineNumber">One-based directive line number.</param>
    /// <exception cref="MarkdownSnippetException">Thrown when the language token contains unsupported characters.</exception>
    private static void ValidateLanguage(string language, MarkdownSnippetRequest request, int lineNumber)
    {
        if (!Regex.IsMatch(language, "^[A-Za-z0-9_+.#-]+$", RegexOptions.NonBacktracking))
        {
            throw new MarkdownSnippetException(
                $"Snippet block in '{request.GetRepositoryRelativeDocumentPath()}' at line {lineNumber} has invalid language '{language}'.");
        }
    }

    /// <summary>
    /// Finds the managed block closing directive while ignoring text inside Markdown fences.
    /// </summary>
    /// <param name="lines">Normalized Markdown lines.</param>
    /// <param name="startIndex">Line index immediately after the opening directive.</param>
    /// <param name="request">Request used to name the document in diagnostics.</param>
    /// <param name="id">Snippet id for diagnostics.</param>
    /// <returns>The line index containing <c>&lt;!-- /appsurface:snippet --&gt;</c>.</returns>
    /// <exception cref="MarkdownSnippetException">Thrown when no compatible closing directive is found.</exception>
    private static int FindClosingLine(
        string[] lines,
        int startIndex,
        MarkdownSnippetRequest request,
        string id)
    {
        MarkdownCodeFence? activeCodeFence = null;

        for (var index = startIndex; index < lines.Length; index++)
        {
            var structuralLine = TrimMarkdownLinePrefix(lines[index]);
            var codeFence = MarkdownCodeFence.TryParse(structuralLine);
            if (activeCodeFence is null && codeFence is not null)
            {
                activeCodeFence = codeFence;
            }
            else if (activeCodeFence is not null && activeCodeFence.Value.IsClosedBy(structuralLine))
            {
                activeCodeFence = null;
            }

            if (activeCodeFence is null && string.Equals(structuralLine, ClosingLine, StringComparison.Ordinal))
            {
                return index;
            }
        }

        throw new MarkdownSnippetException(
            $"Snippet block '{id}' in '{request.GetRepositoryRelativeDocumentPath()}' is missing closing marker '{ClosingLine}'.");
    }

    /// <summary>
    /// Appends one original Markdown line while preserving the canonical final newline shape.
    /// </summary>
    /// <param name="builder">Destination builder.</param>
    /// <param name="line">Line text without its newline.</param>
    /// <param name="index">Current line index.</param>
    /// <param name="lineCount">Total line count.</param>
    private static void AppendLine(StringBuilder builder, string line, int index, int lineCount)
    {
        builder.Append(line);
        if (index < lineCount - 1)
        {
            builder.Append('\n');
        }
    }
}

internal readonly record struct MarkdownCodeFence(char Character, int Length)
{
    /// <summary>
    /// Parses a Markdown code fence opener or closer.
    /// </summary>
    /// <param name="trimmedLine">Line trimmed of surrounding whitespace.</param>
    /// <returns>A fence when the line begins with at least three backticks or tildes; otherwise <c>null</c>.</returns>
    internal static MarkdownCodeFence? TryParse(string trimmedLine)
    {
        if (trimmedLine.Length < 3 || trimmedLine[0] is not ('`' or '~'))
        {
            return null;
        }

        var character = trimmedLine[0];
        var length = 0;
        foreach (var current in trimmedLine)
        {
            if (current != character)
            {
                break;
            }

            length++;
        }

        return length >= 3 ? new MarkdownCodeFence(character, length) : null;
    }

    /// <summary>
    /// Determines whether a trimmed line closes this fence.
    /// </summary>
    /// <param name="trimmedLine">Line trimmed of surrounding whitespace.</param>
    /// <returns>
    /// <c>true</c> when the line uses the same fence character, has length at
    /// least as long as the opener, and contains only that fence character.
    /// </returns>
    internal bool IsClosedBy(string trimmedLine)
    {
        var closingFence = TryParse(trimmedLine);
        var character = Character;
        return closingFence is not null
            && closingFence.Value.Character == character
            && closingFence.Value.Length >= Length
            && trimmedLine.All(current => current == character);
    }
}

internal sealed record MarkdownSnippetBlock(string OpeningLine, string LinePrefix, string Language, string Content)
{
    /// <summary>
    /// Renders a managed block using an automatically sized backtick fence.
    /// </summary>
    /// <returns>The complete managed block with <c>\n</c> line endings.</returns>
    /// <remarks>
    /// The fence is one backtick longer than the longest backtick run in the
    /// content, with a minimum length of three, so nested Markdown examples stay
    /// literal inside the generated block.
    /// </remarks>
    internal string Render()
    {
        var fence = MarkdownFence.Create(Content);
        var builder = new StringBuilder();
        builder.Append(OpeningLine);
        builder.Append('\n');
        builder.Append(LinePrefix);
        builder.Append(fence);
        builder.Append(Language);
        builder.Append('\n');
        AppendPrefixedContent(builder, LinePrefix, Content);
        builder.Append(fence);
        builder.Append('\n');
        builder.Append(LinePrefix);
        builder.Append("<!-- /appsurface:snippet -->");
        builder.Append('\n');
        return builder.ToString();
    }

    private static void AppendPrefixedContent(StringBuilder builder, string linePrefix, string content)
    {
        foreach (var line in content.Split('\n'))
        {
            builder.Append(linePrefix);
            builder.Append(line);
            builder.Append('\n');
        }

        builder.Append(linePrefix);
    }
}

/// <summary>
/// Extracts source snippets between exact documentation marker lines.
/// </summary>
/// <remarks>
/// Supported marker forms are delegated to <see cref="MarkdownSnippetMarker"/>:
/// C# line comments, Razor comments, and HTML comments. A marker pair must be
/// unique, ordered as <c>:start</c> then <c>:end</c>, and contain at least one
/// non-blank line. Marker-like text in strings or inline comments is ignored
/// because markers must occupy the whole trimmed line. When <c>dedent</c> is
/// enabled, common leading spaces or tabs across non-blank snippet lines are
/// removed and surrounding blank lines are trimmed.
/// </remarks>
internal static class MarkdownSnippetSourceExtractor
{
    /// <summary>
    /// Extracts and optionally dedents the snippet for a marker id.
    /// </summary>
    /// <param name="source">Source file text.</param>
    /// <param name="markerId">Marker id without <c>:start</c> or <c>:end</c>.</param>
    /// <param name="sourcePath">Repository-relative source path for diagnostics.</param>
    /// <param name="dedent">Whether to remove common indentation from extracted lines.</param>
    /// <returns>Extracted snippet content with <c>\n</c> line endings.</returns>
    /// <exception cref="MarkdownSnippetException">
    /// Thrown when start/end markers are missing, duplicated, reversed, or enclose
    /// only blank content.
    /// </exception>
    internal static string Extract(string source, string markerId, string sourcePath, bool dedent)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(markerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var lines = source.ReplaceLineEndings("\n").Split('\n');
        int? startIndex = null;
        int? endIndex = null;
        var startCount = 0;
        var endCount = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            var marker = MarkdownSnippetMarker.TryParse(lines[index], markerId);
            if (marker == MarkdownSnippetMarkerKind.Start)
            {
                startCount++;
                startIndex ??= index;
            }
            else if (marker == MarkdownSnippetMarkerKind.End)
            {
                endCount++;
                endIndex ??= index;
            }
        }

        if (startCount == 0)
        {
            throw new MarkdownSnippetException($"Snippet marker '{markerId}:start' was not found in '{sourcePath}'.");
        }

        if (endCount == 0)
        {
            throw new MarkdownSnippetException($"Snippet marker '{markerId}:end' was not found in '{sourcePath}'.");
        }

        if (startCount > 1)
        {
            throw new MarkdownSnippetException($"Snippet marker '{markerId}:start' appears more than once in '{sourcePath}'.");
        }

        if (endCount > 1)
        {
            throw new MarkdownSnippetException($"Snippet marker '{markerId}:end' appears more than once in '{sourcePath}'.");
        }

        if (endIndex <= startIndex)
        {
            throw new MarkdownSnippetException($"Snippet marker '{markerId}:end' appears before '{markerId}:start' in '{sourcePath}'.");
        }

        var snippetLines = lines
            .Skip(startIndex!.Value + 1)
            .Take(endIndex!.Value - startIndex.Value - 1)
            .ToArray();

        if (snippetLines.Length == 0 || snippetLines.All(string.IsNullOrWhiteSpace))
        {
            throw new MarkdownSnippetException($"Snippet marker '{markerId}' in '{sourcePath}' has no content.");
        }

        return dedent ? Dedent(snippetLines) : string.Join("\n", snippetLines).TrimEnd('\n');
    }

    /// <summary>
    /// Removes common indentation from snippet lines.
    /// </summary>
    /// <param name="lines">Snippet lines between markers.</param>
    /// <returns>Dedented snippet text with surrounding blank lines trimmed.</returns>
    /// <remarks>
    /// Only non-blank lines contribute to the minimum indent. Indentation counts
    /// both spaces and tabs as one character because snippets preserve source text
    /// rather than reformatting it.
    /// </remarks>
    private static string Dedent(string[] lines)
    {
        var minimumIndent = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(CountIndent)
            .DefaultIfEmpty(0)
            .Min();

        var dedented = lines.Select(line =>
            string.IsNullOrWhiteSpace(line)
                ? string.Empty
                : line.Length >= minimumIndent ? line[minimumIndent..] : string.Empty);

        return string.Join("\n", dedented).Trim('\n');
    }

    /// <summary>
    /// Counts leading spaces and tabs on a source line.
    /// </summary>
    /// <param name="line">Line to inspect.</param>
    /// <returns>The number of leading indentation characters.</returns>
    private static int CountIndent(string line)
    {
        var count = 0;
        foreach (var character in line)
        {
            if (character is ' ' or '\t')
            {
                count++;
                continue;
            }

            break;
        }

        return count;
    }
}

internal enum MarkdownSnippetMarkerKind
{
    None,
    Start,
    End
}

internal static class MarkdownSnippetMarker
{
    private static readonly Regex IdRegex = new("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.NonBacktracking);

    /// <summary>
    /// Determines whether a snippet id is safe for marker and directive use.
    /// </summary>
    /// <param name="value">Candidate id.</param>
    /// <returns><c>true</c> when the id starts with an ASCII letter or digit and then uses only letters, digits, <c>_</c>, <c>-</c>, or <c>.</c>.</returns>
    internal static bool IsValidId(string value)
    {
        return IdRegex.IsMatch(value);
    }

    /// <summary>
    /// Parses a whole-line source snippet marker.
    /// </summary>
    /// <param name="line">Source line to inspect. A UTF-8 BOM on the first line is ignored.</param>
    /// <param name="markerId">Expected marker id.</param>
    /// <returns>The marker kind, or <see cref="MarkdownSnippetMarkerKind.None"/>.</returns>
    /// <remarks>
    /// Valid marker forms are exactly <c>// docs:snippet id:start</c>,
    /// <c>@* docs:snippet id:start *@</c>, and
    /// <c>&lt;!-- docs:snippet id:start --&gt;</c>, with matching <c>:end</c>
    /// variants. Whitespace may surround the whole line but not the inner marker
    /// text.
    /// </remarks>
    internal static MarkdownSnippetMarkerKind TryParse(string line, string markerId)
    {
        var trimmed = line.Trim().TrimStart('\uFEFF');
        if (IsMarker(trimmed, markerId, "start"))
        {
            return MarkdownSnippetMarkerKind.Start;
        }

        if (IsMarker(trimmed, markerId, "end"))
        {
            return MarkdownSnippetMarkerKind.End;
        }

        return MarkdownSnippetMarkerKind.None;
    }

    /// <summary>
    /// Matches one exact marker form for a marker id and kind.
    /// </summary>
    /// <param name="trimmed">Source line trimmed of surrounding whitespace and any leading BOM.</param>
    /// <param name="markerId">Expected marker id.</param>
    /// <param name="markerKind">Expected marker kind, usually <c>start</c> or <c>end</c>.</param>
    /// <returns><c>true</c> when the line is an exact supported comment marker.</returns>
    private static bool IsMarker(string trimmed, string markerId, string markerKind)
    {
        var marker = $"docs:snippet {markerId}:{markerKind}";
        return string.Equals(trimmed, $"// {marker}", StringComparison.Ordinal)
            || string.Equals(trimmed, $"@* {marker} *@", StringComparison.Ordinal)
            || string.Equals(trimmed, $"<!-- {marker} -->", StringComparison.Ordinal);
    }
}

internal static class MarkdownFence
{
    /// <summary>
    /// Creates a Markdown backtick fence that safely contains the supplied content.
    /// </summary>
    /// <param name="content">Snippet content to wrap.</param>
    /// <returns>At least three backticks, or one more than the longest run in <paramref name="content"/>.</returns>
    internal static string Create(string content)
    {
        var maxRun = 0;
        var currentRun = 0;

        foreach (var character in content)
        {
            if (character == '`')
            {
                currentRun++;
                maxRun = Math.Max(maxRun, currentRun);
            }
            else
            {
                currentRun = 0;
            }
        }

        return new string('`', Math.Max(3, maxRun + 1));
    }
}

internal static class MarkdownSnippetPath
{
    /// <summary>
    /// Resolves a repository-relative source file path and enforces repository containment.
    /// </summary>
    /// <param name="repositoryRoot">Repository root directory.</param>
    /// <param name="repositoryRelativePath">Source path from a snippet directive.</param>
    /// <param name="description">Human-readable path description for diagnostics.</param>
    /// <returns>The full source file path.</returns>
    /// <exception cref="MarkdownSnippetException">
    /// Thrown when the path is rooted, escapes the repository root, or points to
    /// a missing file.
    /// </exception>
    /// <remarks>
    /// Snippet <c>file</c> attributes must be repository-relative so generated
    /// documentation is portable across machines and CI checkouts.
    /// </remarks>
    internal static string ResolveRepositoryFilePath(string repositoryRoot, string repositoryRelativePath, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        if (Path.IsPathRooted(repositoryRelativePath))
        {
            throw new MarkdownSnippetException($"{description} must be repository-relative, but '{repositoryRelativePath}' is rooted.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, repositoryRelativePath));
        if (!TryGetRepositoryRelativePath(repositoryRoot, fullPath, out var normalizedRelativePath))
        {
            throw new MarkdownSnippetException($"{description} '{repositoryRelativePath}' must stay under repository root.");
        }

        if (!File.Exists(fullPath))
        {
            throw new MarkdownSnippetException($"{description} '{normalizedRelativePath}' does not exist.");
        }

        return fullPath;
    }

    /// <summary>
    /// Converts a path to a repository-relative path when it is safely contained in the repository.
    /// </summary>
    /// <param name="repositoryRoot">Repository root directory.</param>
    /// <param name="path">Candidate full or relative path.</param>
    /// <param name="relativePath">Repository-relative path with forward slashes when successful.</param>
    /// <returns><c>true</c> when <paramref name="path"/> is inside <paramref name="repositoryRoot"/>, is not the root itself, and does not cross a reparse-point segment.</returns>
    /// <remarks>
    /// The containment check starts with lexical path normalization, then walks
    /// the repository-relative path segments and rejects symlinks or other
    /// reparse points before file IO can follow them outside the repository.
    /// </remarks>
    internal static bool TryGetRepositoryRelativePath(string repositoryRoot, string path, out string relativePath)
    {
        var fullRoot = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        relativePath = Path.GetRelativePath(fullRoot, fullPath).Replace('\\', '/');
        var isLexicallyContained = relativePath != "."
            && !relativePath.StartsWith("../", StringComparison.Ordinal)
            && !string.Equals(relativePath, "..", StringComparison.Ordinal)
            && !Path.IsPathRooted(relativePath);

        return isLexicallyContained
            && !ContainsReparsePointSegment(fullRoot, relativePath);
    }

    /// <summary>
    /// Determines whether any existing path segment below the repository root is a reparse point.
    /// </summary>
    /// <param name="fullRoot">Canonical repository root path.</param>
    /// <param name="relativePath">Repository-relative candidate path with forward slashes.</param>
    /// <returns><c>true</c> when an inspected segment is a symlink or other reparse point.</returns>
    private static bool ContainsReparsePointSegment(string fullRoot, string relativePath)
    {
        var currentPath = fullRoot;
        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            try
            {
                if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class MarkdownSnippetException : Exception
{
    /// <summary>
    /// Creates a snippet validation or verification exception with an actionable message.
    /// </summary>
    /// <param name="message">Human-readable failure message.</param>
    internal MarkdownSnippetException(string message)
        : base(message)
    {
    }
}
