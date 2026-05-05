using System.Text;

namespace ForgeTrust.Runnable.MarkdownSnippets.Tests;

public sealed class MarkdownSnippetGeneratorTests : IDisposable
{
    private readonly string _repositoryRoot;

    public MarkdownSnippetGeneratorTests()
    {
        _repositoryRoot = Path.Combine(Path.GetTempPath(), "MarkdownSnippetTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repositoryRoot);
    }

    [Fact]
    public async Task GenerateAsync_RewritesManagedBlockFromSourceMarker()
    {
        await WriteFileAsync(
            "src/example.cs",
            """
            public class Example
            {
                // docs:snippet example:start
                public string Name => "Runnable";
                // docs:snippet example:end
            }
            """);
        await WriteDocumentAsync(
            """
            # Example

            <!-- runnable:snippet id="example" file="src/example.cs" marker="example" lang="csharp" -->
            ```csharp
            stale
            ```
            <!-- /runnable:snippet -->
            """);

        var generated = await new MarkdownSnippetGenerator().GenerateAsync(CreateRequest());

        Assert.Contains("public string Name => \"Runnable\";", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("stale", generated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateToFileAsync_UpdatesDocument()
    {
        await WriteBasicSourceAsync();
        await WriteBasicDocumentAsync("old");

        await new MarkdownSnippetGenerator().GenerateToFileAsync(CreateRequest());

        var document = await File.ReadAllTextAsync(GetDocumentPath());
        Assert.Contains("Console.WriteLine(\"hello\");", document, StringComparison.Ordinal);
        Assert.DoesNotContain("old", document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_PassesWhenDocumentIsGenerated()
    {
        await WriteBasicSourceAsync();
        await WriteBasicDocumentAsync("old");
        await new MarkdownSnippetGenerator().GenerateToFileAsync(CreateRequest());

        await new MarkdownSnippetGenerator().VerifyAsync(CreateRequest());
    }

    [Fact]
    public async Task VerifyAsync_IgnoresManagedClosingDirectiveInsideGeneratedCodeFence()
    {
        await WriteFileAsync(
            "src/sample.html",
            """
            <!-- docs:snippet sample:start -->
            <template>
            <!-- /runnable:snippet -->
            </template>
            <!-- docs:snippet sample:end -->
            """);
        await WriteDocumentAsync(
            """
            <!-- runnable:snippet id="sample" file="src/sample.html" marker="sample" lang="html" -->
            ```html
            old
            ```
            <!-- /runnable:snippet -->
            """);

        var generator = new MarkdownSnippetGenerator();
        await generator.GenerateToFileAsync(CreateRequest());

        var document = await File.ReadAllTextAsync(GetDocumentPath());
        Assert.Contains("<!-- /runnable:snippet -->\n</template>", document, StringComparison.Ordinal);
        await generator.VerifyAsync(CreateRequest());
    }

    [Fact]
    public async Task VerifyAsync_ThrowsWhenDocumentIsStale()
    {
        await WriteBasicSourceAsync();
        await WriteBasicDocumentAsync("old");

        var error = await Assert.ThrowsAsync<MarkdownSnippetException>(
            () => new MarkdownSnippetGenerator().VerifyAsync(CreateRequest()));

        Assert.Contains("stale", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_HandlesHelpAndUnknownCommand()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var helpExitCode = await Program.RunAsync(["--help"], output, error, _repositoryRoot);
        var unknownExitCode = await Program.RunAsync(["nope"], output, error, _repositoryRoot);

        Assert.Equal(0, helpExitCode);
        Assert.Equal(1, unknownExitCode);
        Assert.Contains("Usage:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Unknown command", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_HandlesMissingOptionValueAndUnknownOption()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var missingValueExitCode = await Program.RunAsync(["verify", "--document"], output, error, _repositoryRoot);
        var unknownOptionExitCode = await Program.RunAsync(["verify", "--wat"], output, error, _repositoryRoot);

        Assert.Equal(1, missingValueExitCode);
        Assert.Equal(1, unknownOptionExitCode);
        Assert.Contains("requires a value", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Unknown option", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_HandlesHelp()
    {
        var originalOut = Console.Out;
        await using var output = new StringWriter();

        try
        {
            Console.SetOut(output);

            var exitCode = await Program.Main(["--help"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("Usage:", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task RunAsync_HandlesNoArgsAndCommandHelp()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var noArgsExitCode = await Program.RunAsync([], output, error, _repositoryRoot);
        var commandHelpExitCode = await Program.RunAsync(["verify", "--help"], output, error, _repositoryRoot);

        Assert.Equal(1, noArgsExitCode);
        Assert.Equal(0, commandHelpExitCode);
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage:", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_GenerateAndVerifyUseExplicitPaths()
    {
        await WriteBasicSourceAsync();
        await WriteBasicDocumentAsync("old");

        var output = new StringWriter();
        var error = new StringWriter();
        var args = new[]
        {
            "--repo-root",
            _repositoryRoot,
            "--document",
            Path.Combine("docs", "README.md")
        };

        var generateExitCode = await Program.RunAsync(["generate", .. args], output, error, _repositoryRoot);
        var verifyExitCode = await Program.RunAsync(["verify", .. args], output, error, _repositoryRoot);

        Assert.Equal(0, generateExitCode);
        Assert.Equal(0, verifyExitCode);
        Assert.Contains("Generated docs/README.md.", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Markdown snippets are up to date.", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Parse_DefaultsToRepositoryRootRazorWireReadme()
    {
        var options = MarkdownSnippetCommandOptions.Parse([], _repositoryRoot);

        Assert.Equal(Path.GetFullPath(_repositoryRoot), options.Request.RepositoryRoot);
        Assert.Equal(
            Path.Combine(
                Path.GetFullPath(_repositoryRoot),
                "Web",
                "ForgeTrust.Runnable.Web.RazorWire",
                "README.md"),
            options.Request.DocumentPath);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenRepositoryRootOrDocumentIsMissing()
    {
        var missingRoot = Path.Combine(_repositoryRoot, "missing");
        var missingRootError = await Assert.ThrowsAsync<MarkdownSnippetException>(
            () => new MarkdownSnippetGenerator().GenerateAsync(new MarkdownSnippetRequest(missingRoot, Path.Combine(missingRoot, "README.md"))));

        var missingDocumentError = await Assert.ThrowsAsync<MarkdownSnippetException>(
            () => new MarkdownSnippetGenerator().GenerateAsync(CreateRequest()));

        Assert.Contains("does not exist", missingRootError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Markdown document", missingDocumentError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenDocumentEscapesRepositoryRoot()
    {
        var documentPath = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(documentPath, "# Outside", Encoding.UTF8);

        try
        {
            var error = await Assert.ThrowsAsync<MarkdownSnippetException>(
                () => new MarkdownSnippetGenerator().GenerateAsync(new MarkdownSnippetRequest(_repositoryRoot, documentPath)));

            Assert.Contains("must be under repository root", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(documentPath);
        }
    }

    [Fact]
    public async Task Rewrite_IgnoresSnippetDirectiveInsideCodeFence()
    {
        await WriteDocumentAsync(
            """
            ```md
            <!-- runnable:snippet id="sample" file="src/sample.cs" marker="sample" lang="csharp" -->
            ```
            """);

        var error = await Assert.ThrowsAsync<MarkdownSnippetException>(
            () => new MarkdownSnippetGenerator().GenerateAsync(CreateRequest()));

        Assert.Contains("does not contain", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rewrite_IgnoresSnippetDirectiveAfterDifferentFenceInsideCodeFence()
    {
        await WriteDocumentAsync(
            """
            ```md
            ~~~
            <!-- runnable:snippet id="sample" file="src/sample.cs" marker="sample" lang="csharp" -->
            ```csharp
            old
            ```
            <!-- /runnable:snippet -->
            ```
            """);

        var error = await Assert.ThrowsAsync<MarkdownSnippetException>(
            () => new MarkdownSnippetGenerator().GenerateAsync(CreateRequest()));

        Assert.Contains("does not contain", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_RejectsMissingClosingManagedBlock()
    {
        await WriteBasicSourceAsync();
        await WriteDocumentAsync(
            """
            <!-- runnable:snippet id="sample" file="src/sample.cs" marker="sample" lang="csharp" -->
            ```csharp
            old
            ```
            """);

        var error = await Assert.ThrowsAsync<MarkdownSnippetException>(
            () => new MarkdownSnippetGenerator().GenerateAsync(CreateRequest()));

        Assert.Contains("missing closing marker", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_RejectsOpeningDirectiveWithoutCommentClose()
    {
        await WriteBasicSourceAsync();
        await WriteDocumentAsync(
            """
            <!-- runnable:snippet id="sample" file="src/sample.cs" marker="sample" lang="csharp"
            ```csharp
            old
            ```
            <!-- /runnable:snippet -->
            """);

        var error = await Assert.ThrowsAsync<MarkdownSnippetException>(
            () => new MarkdownSnippetGenerator().GenerateAsync(CreateRequest()));

        Assert.Contains("missing closing '-->'", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_RejectsInvalidBlockAttributes()
    {
        await WriteBasicSourceAsync();
        await WriteDocumentAsync(
            """
            <!-- runnable:snippet id="sample" file="src/sample.cs" marker="sample" lang="c sharp" dedent="maybe" extra -->
            ```csharp
            old
            ```
            <!-- /runnable:snippet -->
            """);

        var error = await Assert.ThrowsAsync<MarkdownSnippetException>(
            () => new MarkdownSnippetGenerator().GenerateAsync(CreateRequest()));

        Assert.Contains("unsupported attribute syntax", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("id=\"sample\" id=\"again\" file=\"src/sample.cs\" marker=\"sample\" lang=\"csharp\"", "more than once")]
    [InlineData("id=\"sample\" file=\"src/sample.cs\" marker=\"sample\" lang=\"csharp\" typo=\"value\"", "unsupported attribute")]
    public async Task GenerateAsync_RejectsDuplicateOrUnknownAttributes(string attributes, string expectedMessage)
    {
        await WriteBasicSourceAsync();
        await WriteDocumentAsync(
            $"""
            <!-- runnable:snippet {attributes} -->
            ```csharp
            old
            ```
            <!-- /runnable:snippet -->
            """);

        var error = await Assert.ThrowsAsync<MarkdownSnippetException>(
            () => new MarkdownSnippetGenerator().GenerateAsync(CreateRequest()));

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("", "must define attributes")]
    [InlineData("id=\"bad id\" file=\"src/sample.cs\" marker=\"sample\" lang=\"csharp\"", "invalid snippet id")]
    [InlineData("id=\"sample\" marker=\"sample\" lang=\"csharp\"", "must define 'file'")]
    [InlineData("id=\"sample\" file=\"src/sample.cs\" marker=\"bad id\" lang=\"csharp\"", "invalid snippet marker")]
    [InlineData("id=\"sample\" file=\"src/sample.cs\" marker=\"sample\" lang=\"c sharp\"", "invalid language")]
    [InlineData("id=\"sample\" file=\"src/sample.cs\" marker=\"sample\" lang=\"csharp\" dedent=\"maybe\"", "invalid boolean")]
    public async Task GenerateAsync_RejectsInvalidRequiredAttributeValues(string attributes, string expectedMessage)
    {
        await WriteBasicSourceAsync();
        await WriteDocumentAsync(
            $"""
            <!-- runnable:snippet {attributes} -->
            ```csharp
            old
            ```
            <!-- /runnable:snippet -->
            """);

        var error = await Assert.ThrowsAsync<MarkdownSnippetException>(
            () => new MarkdownSnippetGenerator().GenerateAsync(CreateRequest()));

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/tmp/sample.cs", "rooted")]
    [InlineData("../sample.cs", "must stay under repository root")]
    [InlineData("src/missing.cs", "does not exist")]
    public async Task GenerateAsync_RejectsUnsafeOrMissingSourcePaths(string sourcePath, string expectedMessage)
    {
        await WriteBasicSourceAsync();
        await WriteDocumentAsync(
            $"""
            <!-- runnable:snippet id="sample" file="{sourcePath}" marker="sample" lang="csharp" -->
            ```csharp
            old
            ```
            <!-- /runnable:snippet -->
            """);

        var error = await Assert.ThrowsAsync<MarkdownSnippetException>(
            () => new MarkdownSnippetGenerator().GenerateAsync(CreateRequest()));

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing-start", "start")]
    [InlineData("missing-end", "end")]
    [InlineData("duplicate-start", "more than once")]
    [InlineData("duplicate-end", "more than once")]
    [InlineData("reversed", "appears before")]
    [InlineData("empty", "has no content")]
    public async Task GenerateAsync_RejectsInvalidSourceMarkers(string caseName, string expectedMessage)
    {
        var source = caseName switch
        {
            "missing-start" => """
                Console.WriteLine("hello");
                // docs:snippet sample:end
                """,
            "missing-end" => """
                // docs:snippet sample:start
                Console.WriteLine("hello");
                """,
            "duplicate-start" => """
                // docs:snippet sample:start
                Console.WriteLine("hello");
                // docs:snippet sample:start
                Console.WriteLine("again");
                // docs:snippet sample:end
                """,
            "duplicate-end" => """
                // docs:snippet sample:start
                Console.WriteLine("hello");
                // docs:snippet sample:end
                // docs:snippet sample:end
                """,
            "reversed" => """
                // docs:snippet sample:end
                Console.WriteLine("hello");
                // docs:snippet sample:start
                """,
            _ => """
                // docs:snippet sample:start

                // docs:snippet sample:end
                """
        };
        await WriteFileAsync("src/sample.cs", source);
        await WriteBasicDocumentAsync("old");

        var error = await Assert.ThrowsAsync<MarkdownSnippetException>(
            () => new MarkdownSnippetGenerator().GenerateAsync(CreateRequest()));

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_DoesNotTreatMarkerTextInsideSourceStringAsMarker()
    {
        await WriteFileAsync(
            "src/sample.cs",
            """
            // docs:snippet sample:start
            var text = "// docs:snippet sample:end";
            // docs:snippet sample:end
            """);
        await WriteBasicDocumentAsync("old");

        var generated = await new MarkdownSnippetGenerator().GenerateAsync(CreateRequest());

        Assert.Contains("var text = \"// docs:snippet sample:end\";", generated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_MatchesMarkerOnBomPrefixedFirstLine()
    {
        await WriteFileAsync(
            "src/sample.cs",
            "\uFEFF// docs:snippet sample:start\nConsole.WriteLine(\"hello\");\n// docs:snippet sample:end\n");
        await WriteBasicDocumentAsync("old");

        var generated = await new MarkdownSnippetGenerator().GenerateAsync(CreateRequest());

        Assert.Contains("Console.WriteLine(\"hello\");", generated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_NormalizesMixedLineEndings()
    {
        var sourcePath = Path.Combine(_repositoryRoot, "src", "sample.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllTextAsync(
            sourcePath,
            "// docs:snippet sample:start\r\nConsole.WriteLine(\"hello\");\n// docs:snippet sample:end\r\n",
            Encoding.UTF8);
        await WriteBasicDocumentAsync("old");

        var generated = await new MarkdownSnippetGenerator().GenerateAsync(CreateRequest());

        Assert.Contains("Console.WriteLine(\"hello\");\n```", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", generated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_DedentsByDefaultAndCanPreserveIndentation()
    {
        await WriteFileAsync(
            "src/sample.cs",
            """
            public class Sample
            {
                // docs:snippet sample:start
                    if (true)
                    {
                        Console.WriteLine("hello");
                    }
                // docs:snippet sample:end
            }
            """);
        await WriteDocumentAsync(
            """
            <!-- runnable:snippet id="sample" file="src/sample.cs" marker="sample" lang="csharp" -->
            ```csharp
            old
            ```
            <!-- /runnable:snippet -->
            <!-- runnable:snippet id="sample-preserved" file="src/sample.cs" marker="sample" lang="csharp" dedent="false" -->
            ```csharp
            old
            ```
            <!-- /runnable:snippet -->
            """);

        var generated = await new MarkdownSnippetGenerator().GenerateAsync(CreateRequest());

        Assert.Contains("if (true)\n{\n    Console.WriteLine(\"hello\");\n}", generated, StringComparison.Ordinal);
        Assert.Contains("        if (true)\n        {\n            Console.WriteLine(\"hello\");\n        }", generated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_UsesLongerFenceWhenContentContainsBackticks()
    {
        await WriteFileAsync(
            "src/sample.md",
            """
            <!-- docs:snippet sample:start -->
            ```csharp
            Console.WriteLine("hello");
            ```
            <!-- docs:snippet sample:end -->
            """);
        await WriteDocumentAsync(
            """
            <!-- runnable:snippet id="sample" file="src/sample.md" marker="sample" lang="md" -->
            ```md
            old
            ```
            <!-- /runnable:snippet -->
            """);

        var generated = await new MarkdownSnippetGenerator().GenerateAsync(CreateRequest());

        Assert.Contains("````md", generated, StringComparison.Ordinal);
        Assert.Contains("```csharp\nConsole.WriteLine(\"hello\");\n```", generated, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repositoryRoot))
        {
            Directory.Delete(_repositoryRoot, recursive: true);
        }
    }

    private MarkdownSnippetRequest CreateRequest()
    {
        return new MarkdownSnippetRequest(_repositoryRoot, GetDocumentPath());
    }

    private string GetDocumentPath()
    {
        return Path.Combine(_repositoryRoot, "docs", "README.md");
    }

    private Task WriteBasicSourceAsync()
    {
        return WriteFileAsync(
            "src/sample.cs",
            """
            public class Sample
            {
                // docs:snippet sample:start
                Console.WriteLine("hello");
                // docs:snippet sample:end
            }
            """);
    }

    private Task WriteBasicDocumentAsync(string body)
    {
        return WriteDocumentAsync(
            $$"""
            # Sample

            <!-- runnable:snippet id="sample" file="src/sample.cs" marker="sample" lang="csharp" -->
            ```csharp
            {{body}}
            ```
            <!-- /runnable:snippet -->
            """);
    }

    private Task WriteDocumentAsync(string content)
    {
        return WriteFileAsync("docs/README.md", content);
    }

    private async Task WriteFileAsync(string relativePath, string content)
    {
        var fullPath = Path.Combine(_repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8);
    }
}
