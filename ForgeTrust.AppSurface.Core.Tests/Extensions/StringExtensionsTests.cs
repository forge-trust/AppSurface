using ForgeTrust.AppSurface.Core.Extensions;

namespace ForgeTrust.AppSurface.Core.Tests.Extensions;

public class StringExtensionsTests
{
    [Fact]
    public void SplitOnWhiteSpace_WithMultipleSpaces_ReturnsTokens()
    {
        var input = "preload  stylesheet   alternate";
        var result = input.SplitOnWhiteSpace();

        Assert.Equal(new[] { "preload", "stylesheet", "alternate" }, result);
    }

    [Fact]
    public void SplitOnWhiteSpace_WithTabs_ReturnsTokens()
    {
        var input = "preload\tstylesheet\talternate";
        var result = input.SplitOnWhiteSpace();

        Assert.Equal(new[] { "preload", "stylesheet", "alternate" }, result);
    }

    [Fact]
    public void SplitOnWhiteSpace_WithMixedWhitespace_ReturnsTokens()
    {
        var input = "preload \t stylesheet \n alternate \r\n icon";
        var result = input.SplitOnWhiteSpace();

        Assert.Equal(new[] { "preload", "stylesheet", "alternate", "icon" }, result);
    }

    [Fact]
    public void SplitOnWhiteSpace_WithNullOrEmpty_ReturnsEmptyArray()
    {
        string? input = null;
        Assert.Empty(input.SplitOnWhiteSpace());
        Assert.Empty(string.Empty.SplitOnWhiteSpace());
        Assert.Empty("   ".SplitOnWhiteSpace());
    }

    [Fact]
    public void SplitOnWhiteSpace_WithNoWhitespace_ReturnsSingleToken()
    {
        var input = "stylesheet";
        var result = input.SplitOnWhiteSpace();

        Assert.Single(result);
        Assert.Equal("stylesheet", result[0]);
    }
}
