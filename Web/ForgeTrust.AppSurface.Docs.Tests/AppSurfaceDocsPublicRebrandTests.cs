using System.Text;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsPublicRebrandTests
{
    private static readonly string[] PublicSurfaceRoots =
    [
        "README.md",
        "README.md.yml",
        "CHANGELOG.md",
        ".github",
        "packages",
        "releases",
        "Cli/ForgeTrust.AppSurface.Cli",
        "Web/ForgeTrust.AppSurface.Docs",
        "Web/ForgeTrust.AppSurface.Docs.Standalone"
    ];

    private static readonly string[] TextFileExtensions =
    [
        ".cs",
        ".cshtml",
        ".csproj",
        ".md",
        ".yml",
        ".json",
        ".js"
    ];

    private static readonly string[] ProseFileExtensions =
    [
        ".cs",
        ".cshtml",
        ".csproj",
        ".md",
        ".yml"
    ];

    [Fact]
    public void PublicSurface_ShouldNotContainLegacyRazorDocsBranding()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var offenders = PublicSurfaceRoots
            .Select(root => CombineUnderRepoRoot(repoRoot, root))
            .SelectMany(EnumerateTextFiles)
            .SelectMany(file => FindLegacyMentions(repoRoot, file))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Public AppSurface Docs surfaces still contain legacy RazorDocs mentions:" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void PublicProse_ShouldUseSpacedAppSurfaceDocsBranding()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var offenders = PublicSurfaceRoots
            .Select(root => CombineUnderRepoRoot(repoRoot, root))
            .SelectMany(EnumerateProseFiles)
            .SelectMany(file => FindUnspacedAppSurfaceDocsProseMentions(repoRoot, file))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Public AppSurface Docs prose should use the spaced brand name outside literal code/config references:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    private static IEnumerable<string> EnumerateTextFiles(string path)
    {
        if (File.Exists(path))
        {
            return ShouldScanFile(path) ? [path] : [];
        }

        if (!Directory.Exists(path))
        {
            return [];
        }

        return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(ShouldScanFile);
    }

    private static string CombineUnderRepoRoot(string repoRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException($"Public surface root must be relative: {relativePath}");
        }

        var repoRootFullPath = Path.GetFullPath(repoRoot);
        var candidateFullPath = Path.GetFullPath(Path.Join(repoRootFullPath, relativePath));
        var relativeToRoot = Path.GetRelativePath(repoRootFullPath, candidateFullPath);
        if (Path.IsPathRooted(relativeToRoot)
            || relativeToRoot.Equals("..", StringComparison.Ordinal)
            || relativeToRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativeToRoot.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Public surface root must stay under the repository root: {relativePath}");
        }

        return candidateFullPath;
    }

    private static IEnumerable<string> EnumerateProseFiles(string path)
    {
        return EnumerateTextFiles(path)
            .Where(file => ProseFileExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase));
    }

    private static bool ShouldScanFile(string path)
    {
        if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || path.Contains($"{Path.DirectorySeparatorChar}TestResults{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return false;
        }

        return TextFileExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> FindLegacyMentions(string repoRoot, string file)
    {
        var lineNumber = 0;
        foreach (var line in File.ReadLines(file))
        {
            lineNumber++;
            if (line.Contains("RazorDocs", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Razor Docs", StringComparison.OrdinalIgnoreCase))
            {
                yield return $"{Path.GetRelativePath(repoRoot, file)}:{lineNumber}: {line.Trim()}";
            }
        }
    }

    private static IEnumerable<string> FindUnspacedAppSurfaceDocsProseMentions(string repoRoot, string file)
    {
        var lineNumber = 0;
        var inFence = false;
        foreach (var line in File.ReadLines(file))
        {
            lineNumber++;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            var segments = IsMarkdownLike(file)
                ? line.Split('`')
                : [RemoveXmlCodeSpans(line)];
            for (var i = 0; i < segments.Length; i += 2)
            {
                if (ContainsStandaloneAppSurfaceDocsToken(segments[i]))
                {
                    yield return $"{Path.GetRelativePath(repoRoot, file)}:{lineNumber}: {line.Trim()}";
                    break;
                }
            }
        }
    }

    private static bool ContainsStandaloneAppSurfaceDocsToken(string value)
    {
        for (var index = value.IndexOf("AppSurfaceDocs", StringComparison.Ordinal);
             index >= 0;
             index = value.IndexOf("AppSurfaceDocs", index + "AppSurfaceDocs".Length, StringComparison.Ordinal))
        {
            var before = index == 0 ? '\0' : value[index - 1];
            var afterIndex = index + "AppSurfaceDocs".Length;
            var after = afterIndex >= value.Length ? '\0' : value[afterIndex];

            if (!IsIdentifierCharacter(before)
                && !IsIdentifierCharacter(after)
                && !IsLiteralConfigReference(after)
                && !IsExactStringLiteral(before, after))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIdentifierCharacter(char value)
    {
        return char.IsAsciiLetterOrDigit(value) || value == '_';
    }

    private static bool IsLiteralConfigReference(char after)
    {
        return after == ':';
    }

    private static bool IsExactStringLiteral(char before, char after)
    {
        return before == '"' && after == '"';
    }

    private static bool IsMarkdownLike(string file)
    {
        var extension = Path.GetExtension(file);
        return string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".yml", StringComparison.OrdinalIgnoreCase);
    }

    private static string RemoveXmlCodeSpans(string line)
    {
        var start = line.IndexOf("<c>", StringComparison.Ordinal);
        if (start < 0)
        {
            return line;
        }

        var builder = new StringBuilder(line.Length);
        var cursor = 0;
        while (start >= 0)
        {
            builder.Append(line, cursor, start - cursor);
            var end = line.IndexOf("</c>", start, StringComparison.Ordinal);
            if (end < 0)
            {
                return builder.ToString();
            }

            cursor = end + "</c>".Length;
            start = line.IndexOf("<c>", cursor, StringComparison.Ordinal);
        }

        builder.Append(line, cursor, line.Length - cursor);
        return builder.ToString();
    }
}
