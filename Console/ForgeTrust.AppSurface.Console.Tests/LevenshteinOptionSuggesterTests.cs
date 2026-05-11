namespace ForgeTrust.AppSurface.Console.Tests;

public class LevenshteinOptionSuggesterTests
{
    private readonly LevenshteinOptionSuggester _suggester = new();

    [Fact]
    public void GetSuggestions_WithKnownTypos_ReturnsClosestMatch()
    {
        var validOptions = new[] { "--help", "--version", "--config", "--force" };
        var suggestions = _suggester.GetSuggestions("--hepl", validOptions).ToList();

        Assert.Single(suggestions);
        Assert.Equal("--help", suggestions[0]);
    }

    [Fact]
    public void GetSuggestions_WhenNoCloseMatch_ReturnsEmpty()
    {
        var validOptions = new[] { "--help", "--version" };
        var suggestions = _suggester.GetSuggestions("--something-completely-different", validOptions).ToList();

        Assert.Empty(suggestions);
    }

    [Fact]
    public void GetSuggestions_WithEmptyInput_ReturnsEmpty()
    {
        var validOptions = new[] { "--help" };

        Assert.Empty(_suggester.GetSuggestions(string.Empty, validOptions));
        Assert.Empty(_suggester.GetSuggestions(null!, validOptions));
    }

    [Fact]
    public void GetSuggestions_WithExactMatch_ReturnsExactMatch()
    {
        var validOptions = new[] { "--help", "--version" };
        var suggestions = _suggester.GetSuggestions("--help", validOptions).ToList();

        Assert.Single(suggestions);
        Assert.Equal("--help", suggestions[0]);
    }

    [Fact]
    public void GetSuggestions_IsCaseInsensitive()
    {
        var validOptions = new[] { "--help", "--Version" };
        var suggestions = _suggester.GetSuggestions("--HEPL", validOptions).ToList();

        Assert.Single(suggestions);
        Assert.Equal("--help", suggestions[0]);
    }

    [Fact]
    public void GetSuggestions_WithNullValidOption_IgnoresNull()
    {
        var validOptions = new string[] { null!, "--help" };
        var suggestions = _suggester.GetSuggestions("--hepl", validOptions).ToList();

        Assert.Single(suggestions);
        Assert.Equal("--help", suggestions[0]);
    }

    [Fact]
    public void GetSuggestions_WithNullOrEmptyMatchStrings_ReturnsCorrectDistance()
    {
        var validOptions = new[] { string.Empty, "a", "abcd" };
        // "abcd" is distance 4 from string.Empty. Max distance is 3.
        // "a" is distance 1.
        // Empty is distance 0.
        // If unknown is empty, we test it in earlier test, but let's test a very long unknown.
        var suggestions = _suggester.GetSuggestions("abcdef", validOptions).ToList();

        // length diff empty -> 6 (> 3)
        // length diff a -> 5 (> 3)
        // length diff abcd -> 2 (<= 3), levenshtein is 2
        Assert.Single(suggestions);
        Assert.Equal("abcd", suggestions[0]);
    }

    [Fact]
    public void ComputeLevenshteinDistance_WithEmptyS_ReturnsCorrectDistance()
    {
        // s is empty, t is empty -> 0
        var result1 = LevenshteinOptionSuggester.ComputeLevenshteinDistance(string.Empty, string.Empty);
        Assert.Equal(0, result1);

        // s is empty, t is "test" -> 4
        var result2 = LevenshteinOptionSuggester.ComputeLevenshteinDistance(string.Empty, "test");
        Assert.Equal(4, result2);
    }

    [Fact]
    public void ComputeLevenshteinDistance_WithEmptyT_ReturnsCorrectDistance()
    {
        // s is "test", t is empty -> 4
        var result1 = LevenshteinOptionSuggester.ComputeLevenshteinDistance("test", string.Empty);
        Assert.Equal(4, result1);
    }
}
