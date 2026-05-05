using System.Text;
using System.Text.RegularExpressions;

namespace ForgeTrust.Runnable.MarkdownSnippets;

internal static class Program
{
    private const string GenerateCommand = "generate";
    private const string VerifyCommand = "verify";

    private static readonly string Usage = """
        ForgeTrust.Runnable.MarkdownSnippets

        Generates and verifies source-owned Markdown snippets.

        Usage:
          dotnet run --project tools/ForgeTrust.Runnable.MarkdownSnippets/ForgeTrust.Runnable.MarkdownSnippets.csproj -- <command> [options]

        Commands:
          generate    Rewrites managed snippet blocks in a Markdown document.
          verify      Checks that managed snippet blocks are already up to date.

        Options:
          --repo-root <path>    Repository root. Defaults to the current directory.
          --document <path>     Markdown document to update. Defaults to Web/ForgeTrust.Runnable.Web.RazorWire/README.md.
          -h, --help            Show this help.
        """;

    internal static async Task<int> Main(string[] args)
    {
        return await RunAsync(args, Console.Out, Console.Error, Directory.GetCurrentDirectory());
    }

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

    private static bool IsHelp(string argument)
    {
        return string.Equals(argument, "--help", StringComparison.Ordinal)
            || string.Equals(argument, "-h", StringComparison.Ordinal);
    }
}

internal sealed record MarkdownSnippetCommandOptions(MarkdownSnippetRequest Request)
{
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
            Path.Combine(repoRoot, "Web", "ForgeTrust.Runnable.Web.RazorWire", "README.md"));

        return new MarkdownSnippetCommandOptions(new MarkdownSnippetRequest(repoRoot, resolvedDocumentPath));
    }

    private static string ReadRequiredValue(string[] args, ref int index, string argument)
    {
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new MarkdownSnippetException($"Option '{argument}' requires a value.");
        }

        index++;
        return args[index];
    }

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
    internal string GetRepositoryRelativeDocumentPath()
    {
        return Path.GetRelativePath(RepositoryRoot, DocumentPath).Replace('\\', '/');
    }
}

internal sealed class MarkdownSnippetGenerator
{
    internal async Task GenerateToFileAsync(MarkdownSnippetRequest request, CancellationToken cancellationToken = default)
    {
        var markdown = await GenerateAsync(request, cancellationToken);
        await File.WriteAllTextAsync(request.DocumentPath, markdown, cancellationToken);
    }

    internal async Task<string> GenerateAsync(MarkdownSnippetRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var markdown = await File.ReadAllTextAsync(request.DocumentPath, cancellationToken);
        return MarkdownSnippetRewriter.Rewrite(request, markdown);
    }

    internal async Task VerifyAsync(MarkdownSnippetRequest request, CancellationToken cancellationToken = default)
    {
        var expected = await GenerateAsync(request, cancellationToken);
        var current = await File.ReadAllTextAsync(request.DocumentPath, cancellationToken);
        if (!string.Equals(current, expected, StringComparison.Ordinal))
        {
            throw new MarkdownSnippetException(
                $"Generated snippets in '{request.GetRepositoryRelativeDocumentPath()}' are stale. Run the Markdown snippet generator.");
        }
    }

    private static void ValidateRequest(MarkdownSnippetRequest request)
    {
        if (!Directory.Exists(request.RepositoryRoot))
        {
            throw new MarkdownSnippetException($"Repository root '{request.RepositoryRoot}' does not exist.");
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

internal static partial class MarkdownSnippetRewriter
{
    private const string OpeningPrefix = "<!-- runnable:snippet ";
    private const string ClosingLine = "<!-- /runnable:snippet -->";

    [GeneratedRegex("(?<name>[A-Za-z][A-Za-z0-9_-]*)=\"(?<value>[^\"]*)\"", RegexOptions.NonBacktracking)]
    private static partial Regex AttributeRegex();

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
            var trimmed = line.Trim();

            var codeFence = MarkdownCodeFence.TryParse(trimmed);
            if (activeCodeFence is null && codeFence is not null)
            {
                activeCodeFence = codeFence;
            }
            else if (activeCodeFence is not null && activeCodeFence.Value.IsClosedBy(trimmed))
            {
                activeCodeFence = null;
            }

            if (activeCodeFence is null && trimmed.StartsWith(OpeningPrefix, StringComparison.Ordinal))
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
                $"Markdown document '{request.GetRepositoryRelativeDocumentPath()}' does not contain any runnable snippet blocks.");
        }

        return builder.ToString();
    }

    private static MarkdownSnippetBlock ParseBlock(MarkdownSnippetRequest request, string[] lines, ref int index)
    {
        var openingLine = lines[index].Trim();
        var attributes = ParseAttributes(openingLine, request.GetRepositoryRelativeDocumentPath(), index + 1);
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
        return new MarkdownSnippetBlock(openingLine, language, content);
    }

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

    private static void ValidateId(string value, string description, MarkdownSnippetRequest request, int lineNumber)
    {
        if (!MarkdownSnippetMarker.IsValidId(value))
        {
            throw new MarkdownSnippetException(
                $"Snippet block in '{request.GetRepositoryRelativeDocumentPath()}' at line {lineNumber} has invalid {description} '{value}'. Use letters, numbers, '.', '_' or '-'.");
        }
    }

    private static void ValidateLanguage(string language, MarkdownSnippetRequest request, int lineNumber)
    {
        if (!Regex.IsMatch(language, "^[A-Za-z0-9_+.#-]+$", RegexOptions.NonBacktracking))
        {
            throw new MarkdownSnippetException(
                $"Snippet block in '{request.GetRepositoryRelativeDocumentPath()}' at line {lineNumber} has invalid language '{language}'.");
        }
    }

    private static int FindClosingLine(
        string[] lines,
        int startIndex,
        MarkdownSnippetRequest request,
        string id)
    {
        MarkdownCodeFence? activeCodeFence = null;

        for (var index = startIndex; index < lines.Length; index++)
        {
            var trimmed = lines[index].Trim();
            var codeFence = MarkdownCodeFence.TryParse(trimmed);
            if (activeCodeFence is null && codeFence is not null)
            {
                activeCodeFence = codeFence;
            }
            else if (activeCodeFence is not null && activeCodeFence.Value.IsClosedBy(trimmed))
            {
                activeCodeFence = null;
            }

            if (activeCodeFence is null && string.Equals(trimmed, ClosingLine, StringComparison.Ordinal))
            {
                return index;
            }
        }

        throw new MarkdownSnippetException(
            $"Snippet block '{id}' in '{request.GetRepositoryRelativeDocumentPath()}' is missing closing marker '{ClosingLine}'.");
    }

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

internal sealed record MarkdownSnippetBlock(string OpeningLine, string Language, string Content)
{
    internal string Render()
    {
        var fence = MarkdownFence.Create(Content);
        var builder = new StringBuilder();
        builder.AppendLine(OpeningLine);
        builder.Append(fence);
        builder.Append(Language);
        builder.AppendLine();
        builder.AppendLine(Content);
        builder.AppendLine(fence);
        builder.AppendLine("<!-- /runnable:snippet -->");
        return builder.ToString();
    }
}

internal static class MarkdownSnippetSourceExtractor
{
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

    private static string Dedent(string[] lines)
    {
        var minimumIndent = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(CountIndent)
            .DefaultIfEmpty(0)
            .Min();

        var dedented = lines.Select(line =>
            line.Length >= minimumIndent ? line[minimumIndent..] : string.Empty);

        return string.Join("\n", dedented).Trim('\n');
    }

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

    internal static bool IsValidId(string value)
    {
        return IdRegex.IsMatch(value);
    }

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

    internal static bool TryGetRepositoryRelativePath(string repositoryRoot, string path, out string relativePath)
    {
        var fullRoot = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        relativePath = Path.GetRelativePath(fullRoot, fullPath).Replace('\\', '/');
        return relativePath != "."
            && !relativePath.StartsWith("../", StringComparison.Ordinal)
            && !string.Equals(relativePath, "..", StringComparison.Ordinal)
            && !Path.IsPathRooted(relativePath);
    }
}

internal sealed class MarkdownSnippetException : Exception
{
    internal MarkdownSnippetException(string message)
        : base(message)
    {
    }
}
