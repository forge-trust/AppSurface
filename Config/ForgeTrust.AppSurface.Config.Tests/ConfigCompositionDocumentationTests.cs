using System.Text;
using System.Text.RegularExpressions;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigCompositionDocumentationTests
{
    private const string CanonicalUrl = "https://appsurface.dev/config/secret-references";
    private const string GuideRelativePath = "Config/ForgeTrust.AppSurface.Config/docs/file-secret-references.md";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public void DiagnosticCatalog_EveryGeneratedDocsFragmentTargetsARealGuideAnchor()
    {
        var root = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var anchors = GetAnchors(File.ReadAllText(RepositoryPath(root, GuideRelativePath)));
        // Read emitted code literals as well as catalog cases, so adding a new failure need not update a test allowlist.
        // This exercises the intentionally internal diagnostic seam; it never inspects private members.
        var codes = ReadCoreSources(root)
            .SelectMany(source => Matches(source, "\"(?<code>(?:secret|config-composition)-[a-z0-9-]+)\""))
            .Select(match => match.Groups["code"].Value)
            .Append("future-unrecognized-code")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Contains("secret-descriptor-invalid", codes);
        Assert.Contains("secret-claim-overlap", codes);
        Assert.Contains("secret-provider-access-denied", codes);

        foreach (var code in codes)
        {
            var failure = new ConfigCompositionFailure("Service:ApiKey", code);
            var uri = new Uri(failure.Docs);
            Assert.Equal(CanonicalUrl, uri.GetLeftPart(UriPartial.Path));
            Assert.NotEmpty(uri.Fragment);
            var anchor = Uri.UnescapeDataString(uri.Fragment[1..]);
            Assert.True(anchors.Contains(anchor), $"Diagnostic '{code}' links to missing guide anchor '#{anchor}'.");
        }
    }

    [Fact]
    public void CompositionApiLinks_TargetTheSameGuideAnchorsAsTheFailureCatalog()
    {
        var root = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var anchors = GetAnchors(File.ReadAllText(RepositoryPath(root, GuideRelativePath)));
        var urls = ReadCoreSources(root)
            .SelectMany(source => Matches(source, Regex.Escape(CanonicalUrl) + "#[a-z0-9-]+"))
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(urls);

        foreach (var url in urls)
        {
            var anchor = new Uri(url).Fragment[1..];
            Assert.True(anchors.Contains(anchor), $"API documentation links to missing guide anchor '#{anchor}'.");
        }
    }

    [Fact]
    public void GuideSidecar_UsesTheDiagnosticCanonicalSlugAndExistingRelatedPages()
    {
        var root = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var sidecar = File.ReadAllText(RepositoryPath(root, GuideRelativePath + ".yml"));
        var canonicalSlug = Scalar(sidecar, "canonical_slug") ?? Scalar(sidecar, "slug");
        Assert.Equal(new Uri(CanonicalUrl).AbsolutePath.Trim('/'), canonicalSlug);
        Assert.False(string.IsNullOrWhiteSpace(Scalar(sidecar, "title")));
        Assert.False(string.IsNullOrWhiteSpace(Scalar(sidecar, "summary")));
        Assert.Equal("reference", Scalar(sidecar, "page_type"));

        var relatedPages = sidecar.ReplaceLineEndings("\n").Split('\n')
            .SkipWhile(line => line.Trim() != "related_pages:").Skip(1)
            .TakeWhile(line => line.StartsWith("  - ", StringComparison.Ordinal))
            .Select(line => line[4..].Trim().Trim('\'', '"'))
            .ToArray();
        Assert.NotEmpty(relatedPages);
        foreach (var page in relatedPages)
            Assert.True(File.Exists(RepositoryPath(root, page)), $"The guide sidecar names a missing related page: {page}");
    }

    [Theory]
    [InlineData(GuideRelativePath)]
    [InlineData("Config/ForgeTrust.AppSurface.Config/README.md")]
    [InlineData("Config/ForgeTrust.AppSurface.Config.GoogleSecretManager/README.md")]
    [InlineData("Config/ForgeTrust.AppSurface.Config.LocalSecrets/README.md")]
    [InlineData("examples/file-secret-references/README.md")]
    public void CompositionDocumentation_RelativeLinksResolveToFilesAndActualFragments(string relativeDocument)
    {
        var root = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var document = RepositoryPath(root, relativeDocument);
        Assert.True(File.Exists(document), $"Missing composition document: {relativeDocument}");
        var markdown = WithoutFencedCode(File.ReadAllText(document));
        var targets = Matches(markdown, @"\[[^\]\r\n]+\]\((?<target><[^>\r\n]+>|[^\s)]+)(?:\s+""[^""]*"")?\)")
            .Select(match => match.Groups["target"].Value.Trim('<', '>'))
            .Concat(Matches(markdown, @"(?m)^\s{0,3}\[[^\]]+\]:\s*(?<target><[^>\r\n]+>|\S+)")
                .Select(match => match.Groups["target"].Value.Trim('<', '>')))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(targets);

        foreach (var target in targets)
        {
            // External links and site routes are not local filesystem paths; the canonical guide URL is checked above.
            if (target.StartsWith('/') || Uri.TryCreate(target, UriKind.Absolute, out _)) continue;
            var parts = target.Split('#', 2);
            var relativePath = Uri.UnescapeDataString(parts[0].Split('?', 2)[0]);
            var destination = relativePath.Length == 0
                ? document
                : RepositoryPathFromDocument(root, document, relativePath);
            Assert.True(File.Exists(destination) || Directory.Exists(destination),
                $"{relativeDocument} links to missing relative destination '{target}'.");
            if (parts.Length < 2 || !destination.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
            var anchor = Uri.UnescapeDataString(parts[1]);
            var anchors = GetAnchors(File.ReadAllText(destination));
            Assert.True(anchors.Contains(anchor), $"{relativeDocument} links to missing heading/anchor '{target}'.");
        }
    }

    private static IEnumerable<string> ReadCoreSources(string root) => Directory
        .EnumerateFiles(TestPathUtils.PathUnder(root, "Config", "ForgeTrust.AppSurface.Config"), "*.cs", SearchOption.TopDirectoryOnly)
        .Order(StringComparer.Ordinal)
        .Select(File.ReadAllText);

    private static string RepositoryPath(string repositoryRoot, string relativePath) =>
        TestPathUtils.PathUnder(repositoryRoot, relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries));

    private static string RepositoryPathFromDocument(string repositoryRoot, string document, string relativePath)
    {
        var normalizedDestination = Path.GetFullPath(relativePath, Path.GetDirectoryName(document)!);
        var relativeRepoPath = Path.GetRelativePath(repositoryRoot, normalizedDestination);
        var segments = relativeRepoPath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        return TestPathUtils.PathUnder(repositoryRoot, segments);
    }

    private static string? Scalar(string yaml, string key)
    {
        var match = Matches(yaml, @"(?m)^" + Regex.Escape(key) + @":\s*(?<value>[^\r\n]+)").SingleOrDefault();
        return match?.Groups["value"].Value.Trim().Trim('\'', '"');
    }

    private static IEnumerable<Match> Matches(string source, string pattern) =>
        Regex.Matches(source, pattern, RegexOptions.CultureInvariant, RegexTimeout).Cast<Match>();

    private static HashSet<string> GetAnchors(string markdown)
    {
        markdown = WithoutFencedCode(markdown);
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var match in Matches(markdown, @"(?m)^\s{0,3}#{1,6}\s+(?<heading>[^\r\n]+)"))
        {
            var heading = match.Groups["heading"].Value.TrimEnd(' ', '#').ToLowerInvariant();
            // GitHub/standard generated anchors retain letters, digits, '-' and '_', drop formatting punctuation,
            // and replace spaces with '-'. These guides use ATX headings rather than custom heading ids.
            var anchor = new string(heading.Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c is '-' or '_')
                .Select(c => char.IsWhiteSpace(c) ? '-' : c).ToArray());
            var unique = anchor;
            for (var suffix = 1; !anchors.Add(unique); suffix++) unique = anchor + "-" + suffix;
        }
        foreach (var match in Matches(markdown, "<(?:a|h[1-6])\\b[^>]*\\b(?:id|name)=[\"'](?<anchor>[^\"']+)[\"']"))
            anchors.Add(match.Groups["anchor"].Value);
        return anchors;
    }

    private static string WithoutFencedCode(string markdown)
    {
        var result = new StringBuilder();
        char? fence = null;
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                if (fence is null) fence = trimmed[0];
                else if (trimmed[0] == fence) fence = null;
                continue;
            }
            if (fence is null) result.AppendLine(line);
        }
        return result.ToString();
    }
}
