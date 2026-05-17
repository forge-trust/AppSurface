using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Acornima;
using Acornima.Ast;
using Xunit.Abstractions;
using JsRange = Acornima.Range;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class JavaScriptParserDecisionTests
{
    private static readonly string FixtureDirectory = PathUnder(
        TestPathUtils.FindRepoRoot(AppContext.BaseDirectory),
        "Web",
        "ForgeTrust.AppSurface.Docs.Tests",
        "TestData",
        "JavaScriptParserDecision");

    private readonly ITestOutputHelper _output;

    public JavaScriptParserDecisionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static TheoryData<string, string, string, int, int> SupportedDeclarationFixtures()
    {
        return new TheoryData<string, string, string, int, int>
        {
            { "public-function.js", "wireForm", nameof(JavaScriptProbeKind.Function), 7, 0 },
            { "public-arrow-function.js", "createFailureListener", nameof(JavaScriptProbeKind.Function), 5, 0 },
            { "public-const-value.js", "streamTimeoutMs", nameof(JavaScriptProbeKind.Constant), 5, 0 },
            { "window-razorwire-global.js", "window.RazorWire", nameof(JavaScriptProbeKind.Global), 6, 0 },
        };
    }

    [Theory]
    [MemberData(nameof(SupportedDeclarationFixtures))]
    public void Acornima_Should_Attach_Leading_Public_Block_Comments_To_Supported_Declarations(
        string fixtureName,
        string expectedName,
        string expectedKind,
        int expectedLine,
        int expectedColumn)
    {
        var source = ReadFixture(fixtureName);
        var result = ProbeSource(source);

        var item = Assert.Single(result.AttachedDoclets);
        Assert.Equal(expectedName, item.Name);
        Assert.Equal(expectedKind, item.Kind.ToString());
        Assert.Equal(1, item.CommentLocation.Start.Line);
        Assert.Equal(0, item.CommentLocation.Start.Column);
        Assert.Equal(expectedLine, item.NodeLocation.Start.Line);
        Assert.Equal(expectedColumn, item.NodeLocation.Start.Column);
        Assert.True(item.CommentRange.Start >= 0);
        Assert.True(item.CommentRange.End > item.CommentRange.Start);
        Assert.True(item.NodeRange.End > item.NodeRange.Start);
        Assert.Contains("@public", item.CommentText, StringComparison.Ordinal);
        Assert.True(IsOnlyWhitespace(source, item.CommentRange.End, item.NodeRange.Start));
    }

    [Theory]
    [InlineData("public-event-doclet.js", nameof(JavaScriptProbeKind.Event), "razorwire:form:failure")]
    [InlineData("public-typedef-doclet.js", nameof(JavaScriptProbeKind.Typedef), "FormFailureDetail")]
    public void Acornima_Should_Collect_Standalone_Public_Doclets(
        string fixtureName,
        string expectedKind,
        string expectedName)
    {
        var source = ReadFixture(fixtureName);
        var result = ProbeSource(source);

        Assert.Empty(result.AttachedDoclets);
        var item = Assert.Single(result.StandaloneDoclets);
        Assert.Equal(expectedName, item.Name);
        Assert.Equal(expectedKind, item.Kind.ToString());
        Assert.Equal(1, item.CommentLocation.Start.Line);
        Assert.Equal(0, item.CommentLocation.Start.Column);
        Assert.Contains("@public", item.CommentText, StringComparison.Ordinal);
    }

    [Fact]
    public void Acornima_Should_Not_Treat_Dynamic_Window_Members_As_Concrete_Globals()
    {
        const string source = """
            /**
             * Dynamic global assignment should not become a concrete API name.
             * @public
             */
            window[someDynamicName] = {};
            """;

        var result = ProbeSource(source);

        Assert.Empty(result.AttachedDoclets);
        Assert.Empty(result.StandaloneDoclets);
    }

    [Fact]
    public void Acornima_Should_Report_Catchable_Parse_Failure()
    {
        var source = ReadFixture("malformed.js");

        var exception = Assert.ThrowsAny<ParseErrorException>(() => ProbeSource(source));

        Assert.True(exception.LineNumber > 0);
        Assert.True(exception.Column >= 0);
        Assert.Contains("Unexpected", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Acornima_Should_Parse_Real_Assets_And_Record_Lightweight_Costs()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var cases = new[]
        {
            ParseCostCase.RealFile(
                "razorwire.js",
                PathUnder(repoRoot, "Web", "ForgeTrust.RazorWire", "wwwroot", "razorwire", "razorwire.js"),
                expectedSuccess: true),
            ParseCostCase.RealFile(
                "search-client.js",
                PathUnder(repoRoot, "Web", "ForgeTrust.AppSurface.Docs", "wwwroot", "docs", "search-client.js"),
                expectedSuccess: true),
            ParseCostCase.SkippedFile(
                "minisearch.min.js",
                PathUnder(repoRoot, "Web", "ForgeTrust.AppSurface.Docs", "wwwroot", "docs", "minisearch.min.js"),
                "*.min.js remains excluded by default"),
            ParseCostCase.Fixture("malformed.js", ReadFixture("malformed.js"), expectedSuccess: false),
            ParseCostCase.Fixture("synthetic-large-public-doclet.js", CreateSyntheticPublicDocletFixture(750), expectedSuccess: true),
        };

        WarmParser();

        foreach (var testCase in cases)
        {
            if (testCase.Skipped)
            {
                _output.WriteLine(
                    FormattableString.Invariant(
                        $"{testCase.Name}: {testCase.SizeBytes} bytes, skipped, {testCase.SkipReason}"));
                continue;
            }

            var measurement = MeasureParseCost(testCase.Source);

            Assert.Equal(testCase.ExpectedSuccess, measurement.Success);
            _output.WriteLine(
                FormattableString.Invariant(
                    $"{testCase.Name}: {testCase.SizeBytes} bytes, {measurement.Elapsed.TotalMilliseconds:F3} ms, success={measurement.Success}, nodes={measurement.NodeCount}, blockComments={measurement.BlockCommentCount}"));
        }
    }

    [Fact]
    public void Acornima_Bsd3Clause_Compliance_Case_Should_Be_Documented()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var packageVersion = ReadCentralPackageVersion(repoRoot, "Acornima");
        var nuspec = XDocument.Load(FindNuGetPackageFile(repoRoot, "acornima", packageVersion, "acornima.nuspec"));
        var metadata = nuspec.Root?.Element(NuGetNuspecNamespace + "metadata")
            ?? throw new InvalidOperationException("Acornima nuspec metadata was not found.");
        var license = metadata.Element(NuGetNuspecNamespace + "license");

        Assert.Equal("1.6.1", packageVersion);
        Assert.Equal("expression", license?.Attribute("type")?.Value);
        Assert.Equal("BSD-3-Clause", license?.Value);

        var testProject = XDocument.Load(
            PathUnder(repoRoot, "Web", "ForgeTrust.AppSurface.Docs.Tests", "ForgeTrust.AppSurface.Docs.Tests.csproj"));
        Assert.Equal("false", testProject.Descendants("IsPackable").Single().Value);
        Assert.Contains(
            testProject.Descendants("PackageReference"),
            reference => string.Equals(reference.Attribute("Include")?.Value, "Acornima", StringComparison.Ordinal));
        Assert.Equal(
            ["Web/ForgeTrust.AppSurface.Docs.Tests/ForgeTrust.AppSurface.Docs.Tests.csproj"],
            FindProjectsReferencingPackage(repoRoot, "Acornima"));

        var decision = File.ReadAllText(PathUnder(FixtureDirectory, "README.md"));
        Assert.Contains("## License Compliance Case", decision, StringComparison.Ordinal);
        Assert.Contains("Current spike use", decision, StringComparison.Ordinal);
        Assert.Contains("not redistributed", decision, StringComparison.Ordinal);
        Assert.Contains("Future product use", decision, StringComparison.Ordinal);
        Assert.Contains("third-party notice", decision, StringComparison.Ordinal);
        Assert.Contains("No endorsement", decision, StringComparison.Ordinal);
    }

    private static string ReadFixture(string name)
    {
        return File.ReadAllText(PathUnder(FixtureDirectory, name));
    }

    private static XNamespace NuGetNuspecNamespace { get; } =
        XNamespace.Get("http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd");

    private static string ReadCentralPackageVersion(string repoRoot, string packageId)
    {
        var packages = XDocument.Load(PathUnder(repoRoot, "Directory.Packages.props"));
        var version = packages
            .Descendants("PackageVersion")
            .Single(package => string.Equals(package.Attribute("Include")?.Value, packageId, StringComparison.Ordinal))
            .Attribute("Version")
            ?.Value;

        return version ?? throw new InvalidOperationException($"No central package version was found for {packageId}.");
    }

    private static string FindNuGetPackageFile(string repoRoot, string packageId, string packageVersion, string fileName)
    {
        var assetsPath = PathUnder(
            repoRoot,
            "Web",
            "ForgeTrust.AppSurface.Docs.Tests",
            "obj",
            "project.assets.json");
        using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
        var packagesPath = assets.RootElement
            .GetProperty("project")
            .GetProperty("restore")
            .GetProperty("packagesPath")
            .GetString();

        if (string.IsNullOrWhiteSpace(packagesPath))
        {
            throw new InvalidOperationException("NuGet packages path was not found in project.assets.json.");
        }

        return PathUnder(packagesPath, packageId, packageVersion, fileName);
    }

    private static IReadOnlyList<string> FindProjectsReferencingPackage(string repoRoot, string packageId)
    {
        return Directory
            .EnumerateFiles(repoRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(static path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => ProjectReferencesPackage(path, packageId))
            .Select(path => Path.GetRelativePath(repoRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool ProjectReferencesPackage(string projectPath, string packageId)
    {
        var project = XDocument.Load(projectPath);
        return project
            .Descendants("PackageReference")
            .Any(reference => string.Equals(reference.Attribute("Include")?.Value, packageId, StringComparison.Ordinal));
    }

    private static JavaScriptProbeResult ProbeSource(string source)
    {
        var comments = new List<Comment>();
        var parser = new Parser(new ParserOptions
        {
            EcmaVersion = EcmaVersion.Latest,
            OnComment = (in Comment comment) => comments.Add(comment),
        });

        var script = parser.ParseScript(source);
        var attachedCommentStarts = new HashSet<int>();
        var attachedDoclets = new List<ObservedJavaScriptDoclet>();

        foreach (var statement in script.Body)
        {
            switch (statement)
            {
                case FunctionDeclaration function:
                    if (function.Id?.Name is not { } functionName)
                    {
                        break;
                    }

                    AddAttachedDoclet(
                        comments,
                        attachedCommentStarts,
                        attachedDoclets,
                        source,
                        function,
                        function,
                        functionName,
                        JavaScriptProbeKind.Function);
                    break;

                case VariableDeclaration declaration:
                    foreach (var declarator in declaration.Declarations)
                    {
                        var name = declarator.Id is Identifier identifier ? identifier.Name : null;
                        if (name is null)
                        {
                            continue;
                        }

                        var kind = declarator.Init is ArrowFunctionExpression or FunctionExpression
                            ? JavaScriptProbeKind.Function
                            : JavaScriptProbeKind.Constant;
                        AddAttachedDoclet(
                            comments,
                            attachedCommentStarts,
                            attachedDoclets,
                            source,
                            declaration,
                            declaration,
                            name,
                            kind);
                    }

                    break;

                case ExpressionStatement expressionStatement
                    when expressionStatement.Expression is AssignmentExpression assignment
                         && TryGetMemberPath(assignment.Left) is { } memberPath
                         && memberPath.StartsWith("window.", StringComparison.Ordinal):
                    AddAttachedDoclet(
                        comments,
                        attachedCommentStarts,
                        attachedDoclets,
                        source,
                        expressionStatement,
                        expressionStatement,
                        memberPath,
                        JavaScriptProbeKind.Global);
                    break;
            }
        }

        var standaloneDoclets = comments
            .Where(comment => !attachedCommentStarts.Contains(comment.Start))
            .Select(comment => TryCreateStandaloneDoclet(source, comment))
            .Where(static doclet => doclet is not null)
            .Select(static doclet => doclet!)
            .ToArray();

        return new JavaScriptProbeResult(attachedDoclets.ToArray(), standaloneDoclets);
    }

    private static void AddAttachedDoclet(
        IReadOnlyList<Comment> comments,
        ISet<int> attachedCommentStarts,
        ICollection<ObservedJavaScriptDoclet> attachedDoclets,
        string source,
        Node attachmentNode,
        Node provenanceNode,
        string name,
        JavaScriptProbeKind kind)
    {
        var comment = FindLeadingBlockComment(comments, attachmentNode, source);
        if (comment is null)
        {
            return;
        }

        var commentText = GetCommentText(source, comment.Value);
        if (!HasTag(commentText, "public"))
        {
            return;
        }

        attachedCommentStarts.Add(comment.Value.Start);
        attachedDoclets.Add(new ObservedJavaScriptDoclet(
            name,
            kind,
            commentText,
            comment.Value.Range,
            comment.Value.Location,
            provenanceNode.Range,
            provenanceNode.Location));
    }

    private static Comment? FindLeadingBlockComment(IReadOnlyList<Comment> comments, Node node, string source)
    {
        for (var index = comments.Count - 1; index >= 0; index--)
        {
            var comment = comments[index];
            if (comment.Kind != CommentKind.Block || comment.End > node.Start)
            {
                continue;
            }

            return IsOnlyWhitespace(source, comment.End, node.Start) ? comment : null;
        }

        return null;
    }

    private static ObservedJavaScriptDoclet? TryCreateStandaloneDoclet(string source, Comment comment)
    {
        if (comment.Kind != CommentKind.Block)
        {
            return null;
        }

        var commentText = GetCommentText(source, comment);
        if (!HasTag(commentText, "public"))
        {
            return null;
        }

        if (TryReadTagValue(commentText, "event") is { } eventName)
        {
            return new ObservedJavaScriptDoclet(
                eventName,
                JavaScriptProbeKind.Event,
                commentText,
                comment.Range,
                comment.Location,
                comment.Range,
                comment.Location);
        }

        if (TryReadTagValue(commentText, "typedef") is { } typedefName)
        {
            return new ObservedJavaScriptDoclet(
                typedefName,
                JavaScriptProbeKind.Typedef,
                commentText,
                comment.Range,
                comment.Location,
                comment.Range,
                comment.Location);
        }

        return null;
    }

    private static string? TryReadTagValue(string commentText, string tag)
    {
        foreach (var line in commentText.Split('\n').Select(static rawLine => rawLine.Trim().TrimStart('*').Trim()))
        {
            var prefix = "@" + tag;
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var value = line[prefix.Length..].Trim();
            if (value.Length == 0)
            {
                return null;
            }

            var spaceIndex = value.IndexOf(' ', StringComparison.Ordinal);
            return spaceIndex < 0 ? value : value[..spaceIndex];
        }

        return null;
    }

    private static bool HasTag(string commentText, string tag)
    {
        return commentText.Contains("@" + tag, StringComparison.Ordinal);
    }

    private static string? TryGetMemberPath(Node node)
    {
        return node switch
        {
            Identifier identifier => identifier.Name,
            MemberExpression memberExpression => TryGetMemberPath(memberExpression),
            _ => null,
        };
    }

    private static string? TryGetMemberPath(MemberExpression memberExpression)
    {
        var objectPath = TryGetMemberPath(memberExpression.Object);
        if (objectPath is null)
        {
            return null;
        }

        var propertyName = memberExpression.Property switch
        {
            Identifier identifier when !memberExpression.Computed => identifier.Name,
            StringLiteral stringLiteral when memberExpression.Computed => stringLiteral.Value,
            _ => null,
        };

        return propertyName is null
            ? null
            : objectPath + "." + propertyName;
    }

    private static string GetCommentText(string source, Comment comment)
    {
        return source[comment.ContentRange.Start..comment.ContentRange.End].Trim();
    }

    private static string PathUnder(string basePath, params string[] segments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        ArgumentNullException.ThrowIfNull(segments);

        foreach (var segment in segments)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(segment);
            if (Path.IsPathRooted(segment))
            {
                throw new ArgumentException($"Path segment must be relative: {segment}", nameof(segments));
            }
        }

        return Path.Join([basePath, .. segments]);
    }

    private static bool IsOnlyWhitespace(string source, int start, int end)
    {
        if (start > end)
        {
            return false;
        }

        for (var index = start; index < end; index++)
        {
            if (!char.IsWhiteSpace(source[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static string CreateSyntheticPublicDocletFixture(int itemCount)
    {
        var builder = new StringBuilder(capacity: itemCount * 160);
        builder.AppendLine("\"use strict\";");
        builder.AppendLine();
        for (var index = 0; index < itemCount; index++)
        {
            builder.AppendLine("/**");
            builder.AppendLine(CultureInfo.InvariantCulture, $" * Synthetic public function {index}.");
            builder.AppendLine(" * @public");
            builder.AppendLine(CultureInfo.InvariantCulture, $" * @param {{string}} value{index} - Input value.");
            builder.AppendLine(" */");
            builder.AppendLine(CultureInfo.InvariantCulture, $"function syntheticPublic{index}(value{index}) {{");
            builder.AppendLine(CultureInfo.InvariantCulture, $"  return value{index};");
            builder.AppendLine("}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static void WarmParser()
    {
        _ = MeasureParseCost("const warmup = 1;");
    }

    private static ParseMeasurement MeasureParseCost(string source)
    {
        var comments = new List<Comment>();
        var parser = new Parser(new ParserOptions
        {
            EcmaVersion = EcmaVersion.Latest,
            OnComment = (in Comment comment) => comments.Add(comment),
        });

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var script = parser.ParseScript(source);
            stopwatch.Stop();
            return new ParseMeasurement(
                Success: true,
                Elapsed: stopwatch.Elapsed,
                NodeCount: CountNodes(script),
                BlockCommentCount: comments.Count(static comment => comment.Kind == CommentKind.Block));
        }
        catch (ParseErrorException)
        {
            stopwatch.Stop();
            return new ParseMeasurement(
                Success: false,
                Elapsed: stopwatch.Elapsed,
                NodeCount: 0,
                BlockCommentCount: comments.Count(static comment => comment.Kind == CommentKind.Block));
        }
    }

    private static int CountNodes(Node node)
    {
        var count = 1;
        foreach (var child in node.ChildNodes)
        {
            count += CountNodes(child);
        }

        return count;
    }

    private sealed record ParseCostCase(
        string Name,
        string Source,
        long SizeBytes,
        bool ExpectedSuccess,
        bool Skipped,
        string? SkipReason)
    {
        public static ParseCostCase RealFile(string name, string path, bool expectedSuccess)
        {
            return new ParseCostCase(
                name,
                File.ReadAllText(path),
                new FileInfo(path).Length,
                expectedSuccess,
                Skipped: false,
                SkipReason: null);
        }

        public static ParseCostCase Fixture(string name, string source, bool expectedSuccess)
        {
            return new ParseCostCase(
                name,
                source,
                Encoding.UTF8.GetByteCount(source),
                expectedSuccess,
                Skipped: false,
                SkipReason: null);
        }

        public static ParseCostCase SkippedFile(string name, string path, string skipReason)
        {
            return new ParseCostCase(
                name,
                Source: string.Empty,
                new FileInfo(path).Length,
                ExpectedSuccess: false,
                Skipped: true,
                skipReason);
        }
    }

    private sealed record JavaScriptProbeResult(
        IReadOnlyList<ObservedJavaScriptDoclet> AttachedDoclets,
        IReadOnlyList<ObservedJavaScriptDoclet> StandaloneDoclets);

    private sealed record ObservedJavaScriptDoclet(
        string Name,
        JavaScriptProbeKind Kind,
        string CommentText,
        JsRange CommentRange,
        SourceLocation CommentLocation,
        JsRange NodeRange,
        SourceLocation NodeLocation);

    private sealed record ParseMeasurement(
        bool Success,
        TimeSpan Elapsed,
        int NodeCount,
        int BlockCommentCount);

    private enum JavaScriptProbeKind
    {
        Function,
        Constant,
        Global,
        Event,
        Typedef,
    }
}
