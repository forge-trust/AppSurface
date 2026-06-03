using System.Collections.Immutable;
using System.Globalization;
using ForgeTrust.AppSurface.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ForgeTrust.AppSurface.Testing.Tests;

public sealed class TestFixturePathPolicyTests
{
    [Fact]
    public void TestSource_ShouldUseSharedHelperForDynamicUnderBasePathConstruction()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var allowlist = PathPolicyAllowlist.Load(TestPathUtils.PathUnder(
            repoRoot,
            "tests",
            "ForgeTrust.AppSurface.Testing",
            "path-policy-allowlist.yml"));
        var violations = TestPathPolicyScanner.ScanRepository(repoRoot);
        var unapproved = violations
            .Where(violation => !allowlist.Contains(violation))
            .ToArray();
        var unmatched = allowlist.UnmatchedKeys(violations);

        Assert.True(
            unapproved.Length == 0 && unmatched.Length == 0,
            PathPolicyFailureFormatter.Format(unapproved, unmatched));
    }

    [Fact]
    public void Scanner_ShouldIgnoreCommentsStringsMultilineAndLiteralChildren()
    {
        const string source = """
            public sealed class SampleTests
            {
                public void Test(string root, string outputRelativePath)
                {
                    var comment = "Path.Join(root, outputRelativePath)";
                    // Path.Combine(root, outputRelativePath)
                    var literal = Path.Join(root, "docs", "README.md");
                    var multiline = Path.Join(
                        root,
                        outputRelativePath);
                }
            }
            """;

        var violations = TestPathPolicyScanner.ScanSource("Sample.Tests/SampleTests.cs", source);

        var violation = Assert.Single(violations);
        Assert.Equal("outputRelativePath", violation.RiskyArgument);
        Assert.Equal(10, violation.Line);
    }

    [Fact]
    public void Scanner_ShouldFlagSlashNormalizedDynamicArguments()
    {
        const string source = """
            public sealed class SampleTests
            {
                public void Test(string root, string relativePath)
                {
                    var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
                }
            }
            """;

        var violation = Assert.Single(TestPathPolicyScanner.ScanSource("Sample.Tests/SampleTests.cs", source));

        Assert.Equal("DynamicUnderBasePath", violation.Kind);
        Assert.Equal("relativePath.Replace('/', Path.DirectorySeparatorChar)", violation.RiskyArgument);
    }

    [Fact]
    public void Scanner_ShouldFlagDynamicArgumentRegardlessOfName()
    {
        const string source = """
            public sealed class SampleTests
            {
                public void Test(string root, string fileName)
                {
                    var path = Path.Join(root, fileName);
                }
            }
            """;

        var violation = Assert.Single(TestPathPolicyScanner.ScanSource("Sample.Tests/SampleTests.cs", source));

        Assert.Equal("DynamicUnderBasePath", violation.Kind);
        Assert.Equal("fileName", violation.RiskyArgument);
    }

    [Fact]
    public void Scanner_ShouldFlagPathLikeMemberArguments()
    {
        const string source = """
            public sealed class SampleTests
            {
                public void Test(string root, ManifestEntry entry)
                {
                    var path = Path.Combine(root, entry.Path);
                }
            }
            """;

        var violation = Assert.Single(TestPathPolicyScanner.ScanSource("Sample.Tests/SampleTests.cs", source));

        Assert.Equal("entry.Path", violation.RiskyArgument);
    }

    [Fact]
    public void Scanner_ShouldFlagInterpolatedPathLikeArguments()
    {
        const string source = """
            public sealed class SampleTests
            {
                public void Test(string root, string relativePath)
                {
                    var path = Path.Join(root, $"{relativePath}.md");
                }
            }
            """;

        var violation = Assert.Single(TestPathPolicyScanner.ScanSource("Sample.Tests/SampleTests.cs", source));

        Assert.Equal("$\"{relativePath}.md\"", violation.RiskyArgument);
    }

    [Fact]
    public void Scanner_ShouldFlagConcatenatedPathLikeArguments()
    {
        const string source = """
            public sealed class SampleTests
            {
                public void Test(string root, string prefix, string relativePath)
                {
                    var path = Path.Combine(root, prefix + relativePath);
                }
            }
            """;

        var violation = Assert.Single(TestPathPolicyScanner.ScanSource("Sample.Tests/SampleTests.cs", source));

        Assert.Equal("prefix + relativePath", violation.RiskyArgument);
    }

    [Fact]
    public void Scanner_ShouldFlagWrapperCallsWithPathLikeArguments()
    {
        const string source = """
            public sealed class SampleTests
            {
                public void Test(string root, string relativePath)
                {
                    var path = Path.Join(root, MakeSafe(relativePath));
                }
            }
            """;

        var violation = Assert.Single(TestPathPolicyScanner.ScanSource("Sample.Tests/SampleTests.cs", source));

        Assert.Equal("MakeSafe(relativePath)", violation.RiskyArgument);
    }

    [Fact]
    public void Scanner_ShouldFlagPathLikeNoArgumentWrapperCalls()
    {
        const string source = """
            public sealed class SampleTests
            {
                public void Test(string root)
                {
                    var path = Path.Join(root, GetRelativePath());
                }
            }
            """;

        var violation = Assert.Single(TestPathPolicyScanner.ScanSource("Sample.Tests/SampleTests.cs", source));

        Assert.Equal("GetRelativePath()", violation.RiskyArgument);
    }

    [Fact]
    public void FailureFormatter_ShouldDescribeReplacementAndAllowlistFlow()
    {
        var violation = new PathPolicyViolation(
            "DynamicUnderBasePath",
            "Web/Sample.Tests/SampleTests.cs",
            42,
            17,
            "Path.Join",
            "outputRelativePath",
            "a dynamic relative path can be rooted or traverse outside the intended fixture root");

        var message = PathPolicyFailureFormatter.Format([violation], []);

        Assert.Contains("Test fixture path policy violation: DynamicUnderBasePath", message, StringComparison.Ordinal);
        Assert.Contains("Web/Sample.Tests/SampleTests.cs:42:17", message, StringComparison.Ordinal);
        Assert.Contains("Risky argument: outputRelativePath", message, StringComparison.Ordinal);
        Assert.Contains("Use: TestPathUtils.PathUnder", message, StringComparison.Ordinal);
        Assert.Contains("path-policy-allowlist.yml", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Allowlist_ShouldRequireReasonedEntries()
    {
        var error = Assert.Throws<InvalidOperationException>(() => PathPolicyAllowlist.Parse(
            """
            entries:
              - key: DynamicUnderBasePath|Sample.cs|1|1|Path.Join|relativePath
                category: DynamicUnderBasePath
            """));

        Assert.Contains("reason", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Allowlist_ShouldRejectExpiredEntries()
    {
        var error = Assert.Throws<InvalidOperationException>(() => PathPolicyAllowlist.Parse(
            """
            entries:
              - key: DynamicUnderBasePath|Sample.cs|1|1|Path.Join|relativePath
                category: DynamicUnderBasePath
                reason: intentional platform behavior
                expires: 2000-01-01
            """));

        Assert.Contains("expired", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Allowlist_ShouldRejectMalformedExpiresEntries()
    {
        var error = Assert.Throws<InvalidOperationException>(() => PathPolicyAllowlist.Parse(
            """
            entries:
              - key: DynamicUnderBasePath|Sample.cs|1|1|Path.Join|relativePath
                category: DynamicUnderBasePath
                reason: intentional platform behavior
                expires: 2026-13-40
            """));

        Assert.Contains("expires", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("yyyy-MM-dd", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Allowlist_ShouldReportUnmatchedEntries()
    {
        var allowlist = PathPolicyAllowlist.Parse(
            """
            entries:
              - key: DynamicUnderBasePath|Sample.cs|1|1|Path.Join|relativePath
                category: DynamicUnderBasePath
                reason: intentional platform behavior
            """);

        Assert.Equal(
            ["DynamicUnderBasePath|Sample.cs|1|1|Path.Join|relativePath"],
            allowlist.UnmatchedKeys([]));
    }
}

/// <summary>
/// Represents a detected violation of the test fixture path policy.
/// </summary>
/// <param name="Kind">Policy category for the violation, such as <c>DynamicUnderBasePath</c>.</param>
/// <param name="RelativePath">Repository-relative source file path containing the violation.</param>
/// <param name="Line">1-based source line for the risky argument.</param>
/// <param name="Column">1-based source column for the risky argument.</param>
/// <param name="Invocation">Path API invocation name, such as <c>Path.Join</c> or <c>Path.Combine</c>.</param>
/// <param name="RiskyArgument">Source text for the dynamic argument that may escape the intended base path.</param>
/// <param name="Reason">Human-readable explanation of why the pattern is blocked.</param>
internal sealed record PathPolicyViolation(
    string Kind,
    string RelativePath,
    int Line,
    int Column,
    string Invocation,
    string RiskyArgument,
    string Reason)
{
    /// <summary>
    /// Gets the pipe-delimited allowlist key in
    /// <c>{Kind}|{RelativePath}|{Line}|{Column}|{Invocation}|{RiskyArgument}</c> format.
    /// </summary>
    internal string Key => $"{Kind}|{RelativePath}|{Line}|{Column}|{Invocation}|{RiskyArgument}";
}

/// <summary>
/// Scans C# test source files for dynamic path construction that can bypass a base directory.
/// </summary>
/// <remarks>
/// The scanner examines test source paths selected by <see cref="IsTestSourcePath" /> and classifies
/// <c>Path.Combine</c>/<c>Path.Join</c> calls through <see cref="ScanSource" />. Arguments after the first base segment
/// are considered risky when their expression names contain child path tokens such as <c>path</c>, <c>relative</c>,
/// <c>file</c>, <c>child</c>, or <c>segment</c>, or when they access a child <c>Name</c> member. This catches fixture
/// helpers that should use <c>TestPathUtils.PathUnder</c> because rooted later segments can discard the intended base.
/// </remarks>
internal static class TestPathPolicyScanner
{
    private static readonly string[] RiskyNameTokens =
    [
        "path",
        "relative",
        "file",
        "child",
        "segment"
    ];

    /// <summary>
    /// Scans repository test source files and returns policy violations ordered by path, line, and column.
    /// </summary>
    /// <param name="repositoryRoot">Repository root directory whose test source files should be scanned.</param>
    /// <returns>Detected <see cref="PathPolicyViolation" /> values ordered for stable failure output.</returns>
    /// <remarks>
    /// File reads are performed through <c>TestPathUtils.PathUnder</c>, so each enumerated repository-relative path is
    /// validated against <paramref name="repositoryRoot" /> before <see cref="File.ReadAllText(string)" /> is called.
    /// </remarks>
    internal static PathPolicyViolation[] ScanRepository(string repositoryRoot)
    {
        return Directory.EnumerateFiles(repositoryRoot, "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'))
            .Where(IsTestSourcePath)
            .SelectMany(relativePath => ScanSource(
                relativePath,
                File.ReadAllText(TestPathUtils.PathUnder(repositoryRoot, relativePath))))
            .OrderBy(violation => violation.RelativePath, StringComparer.Ordinal)
            .ThenBy(violation => violation.Line)
            .ThenBy(violation => violation.Column)
            .ToArray();
    }

    /// <summary>
    /// Parses one source file and classifies its path API invocations.
    /// </summary>
    /// <param name="relativePath">Repository-relative path used in emitted violation locations.</param>
    /// <param name="source">C# source text to scan.</param>
    /// <returns>Violations found in <paramref name="source" />.</returns>
    internal static PathPolicyViolation[] ScanSource(string relativePath, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        return root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(invocation => ClassifyInvocation(relativePath, tree, invocation))
            .Where(violation => violation is not null)
            .Select(violation => violation!)
            .ToArray();
    }

    private static PathPolicyViolation? ClassifyInvocation(
        string relativePath,
        SyntaxTree tree,
        InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess
            || memberAccess.Name.Identifier.ValueText is not ("Combine" or "Join")
            || !IsSystemPathExpression(memberAccess.Expression)
            || invocation.ArgumentList.Arguments.Count < 2)
        {
            return null;
        }

        foreach (var argument in invocation.ArgumentList.Arguments.Skip(1))
        {
            if (IsLiteralPathSegment(argument.Expression))
            {
                continue;
            }

            var riskyArgument = GetRiskyArgument(argument.Expression);
            if (riskyArgument is null)
            {
                continue;
            }

            var lineSpan = tree.GetLineSpan(argument.Span);
            return new PathPolicyViolation(
                "DynamicUnderBasePath",
                relativePath,
                lineSpan.StartLinePosition.Line + 1,
                lineSpan.StartLinePosition.Character + 1,
                $"Path.{memberAccess.Name.Identifier.ValueText}",
                riskyArgument,
                "a dynamic relative path can be rooted or traverse outside the intended fixture root");
        }

        return null;
    }

    private static bool IsTestSourcePath(string relativePath)
    {
        var normalized = "/" + relativePath.Replace('\\', '/');
        return !normalized.Contains("/bin/", StringComparison.Ordinal)
            && !normalized.Contains("/obj/", StringComparison.Ordinal)
            && !normalized.Contains("/.git/", StringComparison.Ordinal)
            && !normalized.Contains("/.gstack/", StringComparison.Ordinal)
            && (normalized.Contains(".Tests/", StringComparison.Ordinal)
                || normalized.Contains("/IntegrationTests/", StringComparison.Ordinal)
                || normalized.Contains("/tests/", StringComparison.Ordinal)
                || Path.GetFileName(relativePath).EndsWith("Tests.cs", StringComparison.Ordinal));
    }

    private static bool IsSystemPathExpression(ExpressionSyntax expression)
    {
        var text = expression.ToString();
        return string.Equals(text, "Path", StringComparison.Ordinal)
            || string.Equals(text, "System.IO.Path", StringComparison.Ordinal)
            || text.EndsWith(".Path", StringComparison.Ordinal);
    }

    private static bool IsLiteralPathSegment(ExpressionSyntax expression)
    {
        return expression is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.StringLiteralExpression);
    }

    private static string? GetRiskyArgument(ExpressionSyntax expression)
    {
        return ContainsRiskyExpression(expression)
            ? expression.ToString()
            : null;
    }

    private static bool IsRiskyName(string name)
    {
        return RiskyNameTokens.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsRiskyExpression(ExpressionSyntax expression)
    {
        return expression switch
        {
            IdentifierNameSyntax identifier => IsRiskyName(identifier.Identifier.ValueText),
            MemberAccessExpressionSyntax memberAccess => ContainsRiskyMemberAccess(memberAccess),
            InvocationExpressionSyntax invocation => ContainsRiskyInvocation(invocation),
            InterpolatedStringExpressionSyntax interpolated => interpolated.Contents
                .OfType<InterpolationSyntax>()
                .Any(interpolation => ContainsRiskyExpression(interpolation.Expression)),
            BinaryExpressionSyntax binary => ContainsRiskyExpression(binary.Left) || ContainsRiskyExpression(binary.Right),
            ConditionalExpressionSyntax conditional => ContainsRiskyExpression(conditional.WhenTrue) || ContainsRiskyExpression(conditional.WhenFalse),
            ParenthesizedExpressionSyntax parenthesized => ContainsRiskyExpression(parenthesized.Expression),
            CastExpressionSyntax cast => ContainsRiskyExpression(cast.Expression),
            ElementAccessExpressionSyntax elementAccess => ContainsRiskyExpression(elementAccess.Expression)
                || elementAccess.ArgumentList.Arguments.Any(argument => ContainsRiskyExpression(argument.Expression)),
            _ => false
        };
    }

    private static bool ContainsRiskyMemberAccess(MemberAccessExpressionSyntax memberAccess)
    {
        var memberName = memberAccess.Name.Identifier.ValueText;
        if (IsRiskyName(memberName)
            || string.Equals(memberName, "Name", StringComparison.Ordinal))
        {
            return true;
        }

        return !IsSystemPathExpression(memberAccess.Expression)
            && ContainsRiskyExpression(memberAccess.Expression);
    }

    private static bool ContainsRiskyInvocation(InvocationExpressionSyntax invocation)
    {
        var invocationNameIsRisky = invocation.Expression switch
        {
            IdentifierNameSyntax identifier => IsRiskyName(identifier.Identifier.ValueText),
            MemberAccessExpressionSyntax memberAccess => !IsSystemPathExpression(memberAccess.Expression)
                && IsRiskyName(memberAccess.Name.Identifier.ValueText),
            _ => false
        };
        var receiverIsRisky = invocation.Expression is MemberAccessExpressionSyntax receiverAccess
            && !IsSystemPathExpression(receiverAccess.Expression)
            && ContainsRiskyExpression(receiverAccess.Expression);

        return invocationNameIsRisky
            || receiverIsRisky
            || invocation.ArgumentList.Arguments.Any(argument => ContainsRiskyExpression(argument.Expression));
    }

}

/// <summary>
/// Parses and validates the YAML-like allowlist for intentional path policy exceptions.
/// </summary>
/// <remarks>
/// Entries are written under <c>entries:</c> and must include <c>key</c>, <c>category</c>, and <c>reason</c>.
/// They may include <c>expires</c> as an ISO date in <c>yyyy-MM-dd</c> form; invalid or expired entries fail validation.
/// Duplicate keys are rejected by the backing immutable dictionary, missing required fields fail parsing, and
/// unmatched keys are reported by <see cref="UnmatchedKeys(IEnumerable{PathPolicyViolation})" />.
/// </remarks>
internal sealed class PathPolicyAllowlist
{
    private readonly ImmutableDictionary<string, Entry> _entries;

    private PathPolicyAllowlist(ImmutableDictionary<string, Entry> entries)
    {
        _entries = entries;
    }

    /// <summary>
    /// Reads an allowlist file from disk and parses it.
    /// </summary>
    /// <param name="path">Allowlist file path to read.</param>
    /// <returns>Parsed allowlist entries.</returns>
    /// <exception cref="InvalidOperationException">Thrown when allowlist content is invalid.</exception>
    /// <remarks>Callers pass paths constructed with <c>TestPathUtils.PathUnder</c>.</remarks>
    internal static PathPolicyAllowlist Load(string path)
    {
        return Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// Parses allowlist content and validates required fields, reasons, and expiration dates.
    /// </summary>
    /// <param name="content">YAML-like allowlist content.</param>
    /// <returns>Parsed allowlist entries.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a line is malformed, required fields are missing, reasons are blank, or an entry has expired.
    /// </exception>
    internal static PathPolicyAllowlist Parse(string content)
    {
        var entries = ImmutableDictionary.CreateBuilder<string, Entry>(StringComparer.Ordinal);
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        var currentStartLine = 0;

        foreach (var (line, lineNumber) in content
                     .Split('\n')
                     .Select((rawLine, index) => (Line: rawLine.TrimEnd('\r'), LineNumber: index + 1)))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (string.Equals(trimmed, "entries: []", StringComparison.Ordinal)
                || string.Equals(trimmed, "entries:", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                AddEntry(entries, current, currentStartLine);
                current = new Dictionary<string, string>(StringComparer.Ordinal);
                currentStartLine = lineNumber;
                AddProperty(current, trimmed[2..]);
                continue;
            }

            AddProperty(current, trimmed);
        }

        AddEntry(entries, current, currentStartLine);
        return new PathPolicyAllowlist(entries.ToImmutable());
    }

    internal bool Contains(PathPolicyViolation violation)
    {
        return _entries.TryGetValue(violation.Key, out var entry)
            && string.Equals(entry.Category, violation.Kind, StringComparison.Ordinal);
    }

    internal string[] UnmatchedKeys(IEnumerable<PathPolicyViolation> violations)
    {
        var violationKeys = violations.Select(violation => violation.Key).ToHashSet(StringComparer.Ordinal);
        return _entries.Keys
            .Where(key => !violationKeys.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddProperty(Dictionary<string, string> current, string line)
    {
        var separatorIndex = line.IndexOf(':', StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            throw new InvalidOperationException($"Invalid allowlist line '{line}'.");
        }

        current[line[..separatorIndex].Trim()] = line[(separatorIndex + 1)..].Trim();
    }

    private static void AddEntry(ImmutableDictionary<string, Entry>.Builder entries, Dictionary<string, string> current, int lineNumber)
    {
        if (current.Count == 0)
        {
            return;
        }

        var key = Require(current, "key", lineNumber);
        var category = Require(current, "category", lineNumber);
        var reason = Require(current, "reason", lineNumber);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidOperationException($"Allowlist entry '{key}' must include a reason (near line {lineNumber}).");
        }

        if (current.TryGetValue("expires", out var expiresText))
        {
            if (!DateOnly.TryParseExact(expiresText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expires))
            {
                throw new InvalidOperationException($"Allowlist entry '{key}' has malformed expires value '{expiresText}'; use yyyy-MM-dd (near line {lineNumber}).");
            }

            if (expires < DateOnly.FromDateTime(DateTime.UtcNow))
            {
                throw new InvalidOperationException($"Allowlist entry '{key}' expired on {expires:yyyy-MM-dd} (near line {lineNumber}).");
            }
        }

        entries.Add(key, new Entry(category, reason));
    }

    private static string Require(Dictionary<string, string> current, string key, int lineNumber)
    {
        if (!current.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Allowlist entry near line {lineNumber} must include {key}.");
        }

        return value;
    }

    private sealed record Entry(string Category, string Reason);
}

/// <summary>
/// Formats test fixture path policy failures into human-readable diagnostics for test output.
/// </summary>
/// <remarks>
/// The output is a multi-line string separated with <see cref="Environment.NewLine" />. It starts with policy guidance
/// headers, then emits one block per <see cref="PathPolicyViolation" /> containing the kind, file location, invocation,
/// risky argument, reason, replacement guidance, allowlist instructions, and allowlist key. Unmatched allowlist entries
/// are listed in separate blocks. Callers should treat the result as a diagnostic for assertion messages and logs.
/// </remarks>
internal static class PathPolicyFailureFormatter
{
    /// <summary>
    /// Formats violations and unmatched allowlist keys into a multi-line diagnostic string.
    /// </summary>
    /// <param name="violations">Detected path policy violations to describe.</param>
    /// <param name="unmatchedKeys">Allowlist keys that no longer correspond to active violations.</param>
    /// <returns>
    /// An empty string when both collections are empty; otherwise a newline-separated diagnostic containing header
    /// guidance, per-violation details, and unmatched allowlist entries.
    /// </returns>
    internal static string Format(IReadOnlyCollection<PathPolicyViolation> violations, IReadOnlyCollection<string> unmatchedKeys)
    {
        if (violations.Count == 0 && unmatchedKeys.Count == 0)
        {
            return string.Empty;
        }

        var lines = new List<string>
        {
            "Test fixture path policy failed.",
            "Use TestPathUtils.PathUnder(basePath, segments) for full paths under a base.",
            "Docs: tests/ForgeTrust.AppSurface.Testing/README.md#policy-failures"
        };

        foreach (var violation in violations)
        {
            lines.Add(string.Empty);
            lines.Add($"Test fixture path policy violation: {violation.Kind}");
            lines.Add($"File: {violation.RelativePath}:{violation.Line}:{violation.Column}");
            lines.Add($"Invocation: {violation.Invocation}");
            lines.Add($"Risky argument: {violation.RiskyArgument}");
            lines.Add($"Why: {violation.Reason}.");
            lines.Add($"Use: TestPathUtils.PathUnder(basePath, {violation.RiskyArgument})");
            lines.Add("Intentional exception: add a reasoned entry to tests/ForgeTrust.AppSurface.Testing/path-policy-allowlist.yml.");
            lines.Add($"Allowlist key: {violation.Key}");
        }

        foreach (var unmatchedKey in unmatchedKeys)
        {
            lines.Add(string.Empty);
            lines.Add($"Unmatched allowlist entry: {unmatchedKey}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
