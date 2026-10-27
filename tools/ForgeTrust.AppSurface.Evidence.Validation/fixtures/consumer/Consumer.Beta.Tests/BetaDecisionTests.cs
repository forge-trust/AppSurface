using Consumer.Beta;
using Xunit;

namespace Consumer.Beta.Tests;

public sealed class BetaDecisionTests
{
    [Fact]
    public void NormalizesLowValue() => Assert.Equal(0, BetaDecision.Normalize(-1));

    [Fact]
    public void NormalizesHighValue() => Assert.Equal(10, BetaDecision.Normalize(11));

    [Fact]
    public void PreservesMiddleValue() => Assert.Equal(5, BetaDecision.Normalize(5));
}
