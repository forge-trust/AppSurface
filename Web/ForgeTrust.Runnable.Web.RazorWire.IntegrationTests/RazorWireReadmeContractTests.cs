using System.Text;
using System.Text.RegularExpressions;
using ForgeTrust.Runnable.Core;

namespace ForgeTrust.Runnable.Web.RazorWire.IntegrationTests;

public sealed class RazorWireReadmeContractTests
{
    [Fact]
    public void RazorWireReadme_DocumentedQuickstartCommand_UsesRepoRootSampleProject()
    {
        var readme = ReadRazorWireReadme();

        Assert.Contains(
            "dotnet run --project examples/razorwire-mvc/RazorWireWebExample.csproj",
            readme,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RazorWireReadme_LocalMarkdownLinks_ResolveToExistingFilesAndAnchors()
    {
        var repoRoot = GetRepositoryRoot();
        var readmePath = GetRazorWireReadmePath(repoRoot);
        var readmeDirectory = Path.GetDirectoryName(readmePath)!;
        var readme = File.ReadAllText(readmePath);

        var targets = ExtractMarkdownLinks(readme)
            .Where(target => IsLocalDocumentationTarget(target))
            .ToArray();

        Assert.NotEmpty(targets);

        foreach (var target in targets)
        {
            var (pathPart, fragment) = SplitFragment(target);
            var linkedPath = string.IsNullOrEmpty(pathPart)
                ? readmePath
                : Path.GetFullPath(Path.Combine(readmeDirectory, pathPart));

            Assert.True(
                IsUnderRepositoryRoot(repoRoot, linkedPath),
                $"README link '{target}' resolves outside repository root: '{linkedPath}'.");

            Assert.True(
                File.Exists(linkedPath) || Directory.Exists(linkedPath),
                $"README link '{target}' points to missing path '{linkedPath}'.");

            if (!string.IsNullOrEmpty(fragment) && File.Exists(linkedPath) && IsMarkdownFile(linkedPath))
            {
                var anchors = ExtractMarkdownHeadingAnchors(File.ReadAllText(linkedPath));
                Assert.Contains(
                    fragment,
                    anchors,
                    StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void RazorWireReadme_SourceBackedSnippets_DocumentGeneratorCommand()
    {
        var readme = ReadRazorWireReadme();

        Assert.Contains(
            "tools/ForgeTrust.Runnable.MarkdownSnippets/ForgeTrust.Runnable.MarkdownSnippets.csproj -- generate",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "<!-- runnable:snippet id=\"razorwire-counter\"",
            readme,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RazorWireReadme_LinkAndAnchorExtraction_IgnoresFencedExamples()
    {
        const string markdown = """
            ```md
            [ignored](../missing.md)
            # Ignored & Heading
            ```

            [kept](Docs/antiforgery.md)

            # Kept & Heading
            """;

        var links = ExtractMarkdownLinks(markdown).ToArray();
        var anchors = ExtractMarkdownHeadingAnchors(markdown);

        Assert.DoesNotContain("../missing.md", links);
        Assert.Contains("Docs/antiforgery.md", links);
        Assert.DoesNotContain("ignored--heading", anchors);
        Assert.Contains("kept--heading", anchors);
    }

    [Fact]
    public void RazorWireReadme_LinkContainment_RejectsSymlinkEscapes()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "RazorWireReadmeContract", Guid.NewGuid().ToString("N"));
        var outsideRoot = Path.Combine(Path.GetTempPath(), "RazorWireReadmeOutside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "docs"));
        Directory.CreateDirectory(outsideRoot);
        File.WriteAllText(Path.Combine(outsideRoot, "outside.md"), "# Outside", Encoding.UTF8);
        Directory.CreateSymbolicLink(Path.Combine(repoRoot, "docs", "linked"), outsideRoot);

        try
        {
            var linkedPath = Path.Combine(repoRoot, "docs", "linked", "outside.md");

            Assert.False(IsUnderRepositoryRoot(repoRoot, linkedPath));
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public void RazorWireReadme_HeadingAnchors_IncludeGitHubDuplicateSuffixes()
    {
        const string markdown = """
            # Examples
            # Examples
            # Examples
            """;

        var anchors = ExtractMarkdownHeadingAnchors(markdown);

        Assert.Contains("examples", anchors);
        Assert.Contains("examples-1", anchors);
        Assert.Contains("examples-2", anchors);
    }

    private static string GetRepositoryRoot()
    {
        return PathUtils.FindRepositoryRoot(AppContext.BaseDirectory);
    }

    private static string GetRazorWireReadmePath(string repoRoot)
    {
        return Path.Combine(repoRoot, "Web", "ForgeTrust.Runnable.Web.RazorWire", "README.md");
    }

    private static string ReadRazorWireReadme()
    {
        return File.ReadAllText(GetRazorWireReadmePath(GetRepositoryRoot()));
    }

    private static IEnumerable<string> ExtractMarkdownLinks(string markdown)
    {
        foreach (var line in EnumerateNonFencedMarkdownLines(markdown))
        {
            foreach (Match match in Regex.Matches(line, @"(?<!!)\[[^\]]+\]\((?<target>[^)\s]+)(?:\s+""[^""]*"")?\)"))
            {
                yield return match.Groups["target"].Value;
            }
        }
    }

    private static bool IsLocalDocumentationTarget(string target)
    {
        return !target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
    }

    private static (string PathPart, string Fragment) SplitFragment(string target)
    {
        var hashIndex = target.IndexOf('#', StringComparison.Ordinal);
        if (hashIndex < 0)
        {
            return (target, string.Empty);
        }

        return (target[..hashIndex], target[(hashIndex + 1)..]);
    }

    private static bool IsMarkdownFile(string path)
    {
        return string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderRepositoryRoot(string repoRoot, string path)
    {
        var fullRoot = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relativePath = Path.GetRelativePath(fullRoot, Path.GetFullPath(path));
        var isLexicallyContained = !string.Equals(relativePath, "..", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relativePath);

        return isLexicallyContained
            && !ContainsReparsePointSegment(fullRoot, relativePath.Replace('\\', '/'));
    }

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

    private static HashSet<string> ExtractMarkdownHeadingAnchors(string markdown)
    {
        var anchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var anchorCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in EnumerateNonFencedMarkdownLines(markdown))
        {
            var match = Regex.Match(line, @"^#{1,6}\s+(?<heading>.+?)\s*$");
            if (match.Success)
            {
                var anchor = ToGitHubHeadingAnchor(match.Groups["heading"].Value);
                if (anchorCounts.TryGetValue(anchor, out var count))
                {
                    count++;
                    anchorCounts[anchor] = count;
                    anchors.Add($"{anchor}-{count}");
                    continue;
                }

                anchorCounts.Add(anchor, 0);
                anchors.Add(anchor);
            }
        }

        return anchors;
    }

    private static IEnumerable<string> EnumerateNonFencedMarkdownLines(string markdown)
    {
        MarkdownFence? activeFence = null;

        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = line.Trim();
            var fence = MarkdownFence.TryParse(trimmed);
            if (activeFence is null && fence is not null)
            {
                activeFence = fence;
                continue;
            }

            if (activeFence is not null)
            {
                if (activeFence.Value.IsClosedBy(trimmed))
                {
                    activeFence = null;
                }

                continue;
            }

            yield return line;
        }
    }

    private static string ToGitHubHeadingAnchor(string heading)
    {
        var builder = new StringBuilder();

        foreach (var rune in heading.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                builder.Append(rune.ToString().ToLowerInvariant());
            }
            else if (Rune.IsWhiteSpace(rune) || rune.Value == '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-');
    }

    private readonly record struct MarkdownFence(char Character, int Length)
    {
        public static MarkdownFence? TryParse(string trimmedLine)
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

            return length >= 3 ? new MarkdownFence(character, length) : null;
        }

        public bool IsClosedBy(string trimmedLine)
        {
            var closingFence = TryParse(trimmedLine);
            var character = Character;
            return closingFence is not null
                && closingFence.Value.Character == character
                && closingFence.Value.Length >= Length
                && trimmedLine.All(current => current == character);
        }
    }
}
