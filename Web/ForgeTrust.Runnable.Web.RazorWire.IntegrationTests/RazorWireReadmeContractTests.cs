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
        foreach (Match match in Regex.Matches(markdown, @"(?<!!)\[[^\]]+\]\((?<target>[^)\s]+)(?:\s+""[^""]*"")?\)"))
        {
            yield return match.Groups["target"].Value;
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

    private static HashSet<string> ExtractMarkdownHeadingAnchors(string markdown)
    {
        var anchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var match = Regex.Match(line, @"^#{1,6}\s+(?<heading>.+?)\s*$");
            if (match.Success)
            {
                anchors.Add(ToGitHubHeadingAnchor(match.Groups["heading"].Value));
            }
        }

        return anchors;
    }

    private static string ToGitHubHeadingAnchor(string heading)
    {
        var builder = new StringBuilder();
        var previousWasSeparator = false;

        foreach (var rune in heading.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                builder.Append(rune.ToString().ToLowerInvariant());
                previousWasSeparator = false;
            }
            else if (Rune.IsWhiteSpace(rune) || rune.Value == '-')
            {
                if (!previousWasSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                    previousWasSeparator = true;
                }
            }
        }

        return builder.ToString().Trim('-');
    }
}
