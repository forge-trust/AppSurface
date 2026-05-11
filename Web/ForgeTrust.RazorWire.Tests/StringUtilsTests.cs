namespace ForgeTrust.RazorWire.Tests;

public class StringUtilsTests
{
    [Theory]
    [InlineData("hello-world", "hello-world")]
    [InlineData("Hello World", "Hello-World")]
    [InlineData("test@example.com", "test-example-com")]
    [InlineData("foo_bar", "foo_bar")]
    [InlineData("multiple   spaces", "multiple-spaces")]
    [InlineData("---leading-trailing---", "leading-trailing")]
    [InlineData("", "id")]
    [InlineData(null, "id")]
    [InlineData("   ", "id")]
    [InlineData("!@#$%", "id")]
    public void ToSafeId_WithoutHash_SanitizesCorrectly(string? input, string expected)
    {
        var result = StringUtils.ToSafeId(input, appendHash: false);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ToSafeId_WithHash_AppendsConsistentHash()
    {
        var input = "test@example.com";
        var result1 = StringUtils.ToSafeId(input, appendHash: true);
        var result2 = StringUtils.ToSafeId(input, appendHash: true);

        // Should be deterministic
        Assert.Equal(result1, result2);

        // Should have hash suffix
        Assert.Contains("-", result1);
        Assert.Matches(@"^test-example-com-[0-9a-f]{4}$", result1);
    }

    [Theory]
    [InlineData("user1", "user1")]
    [InlineData(null, "id")]
    [InlineData("   ", "id")]
    [InlineData("", "id")]
    public void ToSafeId_WithHash_ReturnsHashedIdentifier(string? input, string expectedPrefix)
    {
        var result = StringUtils.ToSafeId(input, appendHash: true);
        Assert.StartsWith(expectedPrefix + "-", result);
        Assert.Matches(@".+-[0-9a-f]{4}$", result);
    }

    [Fact]
    public void ToSafeId_WithHash_ProducesDifferentHashesForDifferentInputs()
    {
        var result1 = StringUtils.ToSafeId("user1", appendHash: true);
        var result2 = StringUtils.ToSafeId("user2", appendHash: true);

        Assert.NotEqual(result1, result2);
    }

    [Fact]
    public void ToSafeId_WithHash_HandlesEmptyInput()
    {
        var result = StringUtils.ToSafeId("", appendHash: true);
        Assert.Equal("id-e3b0", result);
    }

    [Fact]
    public void ToSafeId_WithHash_HandlesWhitespaceInput()
    {
        // Whitespace should have a different hash than empty string
        var result = StringUtils.ToSafeId("   ", appendHash: true);
        Assert.StartsWith("id-", result);
        Assert.NotEqual("id-e3b0", result);
    }
}
