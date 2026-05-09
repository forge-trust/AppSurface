using ForgeTrust.Runnable.Web.RazorDocs.Services;

namespace ForgeTrust.Runnable.Web.RazorDocs.Tests;

public class RazorDocsCodeLanguageCatalogTests
{
    private readonly RazorDocsCodeLanguageCatalog _catalog = new();

    [Theory]
    [InlineData("cs", "csharp", "csharp", "C#", "csharp", false)]
    [InlineData("csharp", "csharp", "csharp", "C#", "csharp", false)]
    [InlineData("sh", "bash", "bash", "Bash", "shellscript", false)]
    [InlineData("shell", "bash", "bash", "Bash", "shellscript", false)]
    [InlineData("js", "javascript", "javascript", "JavaScript", "javascript", false)]
    [InlineData("md", "markdown", "markdown", "Markdown", "markdown", false)]
    [InlineData("cshtml", "razor", "razor", "Razor", "razor", false)]
    [InlineData("text", "plaintext", "plaintext", "Plain text", null, true)]
    public void Normalize_ShouldMapKnownAliases(
        string input,
        string normalized,
        string classLanguage,
        string label,
        string? textMateLanguageId,
        bool isPlainText)
    {
        var result = _catalog.Normalize(input);

        Assert.Equal(normalized, result.NormalizedLanguage);
        Assert.Equal(classLanguage, result.ClassLanguage);
        Assert.Equal(label, result.Label);
        Assert.Equal(textMateLanguageId, result.TextMateLanguageId);
        Assert.True(result.IsKnown);
        Assert.Equal(isPlainText, result.IsPlainText);
    }

    [Fact]
    public void Normalize_ShouldKeepSafeUnknownLanguageAsConventionalClass()
    {
        var result = _catalog.Normalize("my-language");

        Assert.Equal("unknown", result.NormalizedLanguage);
        Assert.Equal("my-language", result.ClassLanguage);
        Assert.Equal("my-language", result.Label);
        Assert.False(result.IsKnown);
        Assert.True(result.IsPlainText);
    }

    [Fact]
    public void Normalize_ShouldFallbackToPlaintextClass_WhenLanguageIsMalicious()
    {
        var result = _catalog.Normalize("\"><script>");

        Assert.Equal("unknown", result.NormalizedLanguage);
        Assert.Equal("plaintext", result.ClassLanguage);
        Assert.Equal("Unknown", result.Label);
        Assert.False(result.IsKnown);
        Assert.True(result.IsPlainText);
    }

    [Fact]
    public void Normalize_ShouldFallbackToPlaintextClass_WhenSafeCharactersProduceNoSlug()
    {
        var result = _catalog.Normalize(".");

        Assert.Equal("unknown", result.NormalizedLanguage);
        Assert.Equal("plaintext", result.ClassLanguage);
        Assert.Equal("Unknown", result.Label);
        Assert.False(result.IsKnown);
        Assert.True(result.IsPlainText);
    }

    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("ABC", "abc")]
    [InlineData("123", "123")]
    [InlineData("foo_bar.baz", "foo-bar-baz")]
    public void Normalize_ShouldKeepSafeUnknownLanguageCharacters(string input, string expectedClassLanguage)
    {
        var result = _catalog.Normalize(input);

        Assert.Equal("unknown", result.NormalizedLanguage);
        Assert.Equal(expectedClassLanguage, result.ClassLanguage);
        Assert.Equal(expectedClassLanguage, result.Label);
        Assert.False(result.IsKnown);
        Assert.True(result.IsPlainText);
    }

    [Theory]
    [InlineData("CSharp", "csharp")]
    [InlineData("foo_bar.baz", "foo-bar-baz")]
    [InlineData(" \"><script> ", "script")]
    [InlineData("!!!", "")]
    [InlineData("   ", "")]
    [InlineData("foo-", "foo")]
    public void CreateSafeClassSlug_ShouldAllowOnlyLowercaseAsciiDigitsAndHyphens(string input, string expected)
    {
        Assert.Equal(expected, RazorDocsCodeLanguageCatalog.CreateSafeClassSlug(input));
    }
}
