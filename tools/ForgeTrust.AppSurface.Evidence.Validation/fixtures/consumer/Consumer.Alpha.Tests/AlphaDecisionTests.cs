using Consumer.Alpha;
using Xunit;

namespace Consumer.Alpha.Tests;

public sealed class AlphaDecisionTests
{
    [Fact]
    public void DescribesNonNegativeValue() => Assert.Equal("non-negative", AlphaDecision.Describe(2));

    [Fact]
    public void DescribesNegativeValue() => Assert.Equal("negative", AlphaDecision.Describe(-2));
}
