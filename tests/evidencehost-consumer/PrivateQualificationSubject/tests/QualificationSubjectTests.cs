using EvidenceHost.PrivateQualificationCalculation;
using Xunit;

namespace EvidenceHost.PrivateQualificationSubject;

/// <summary>Exercises actual subject calculations for ordinary, bulk and rejected orders.</summary>
public sealed class QualificationSubjectTests
{
    /// <summary>Verifies ordinary pricing and both sides of the four-item bulk threshold.</summary>
    /// <param name="quantity">The requested item count.</param>
    /// <param name="expectedTotal">The expected total in whole currency units.</param>
    [Theory]
    [InlineData(1, 4)]
    [InlineData(3, 12)]
    [InlineData(4, 12)]
    [InlineData(8, 24)]
    public void CalculateTotal_UsesPriceForOrderSize(int quantity, int expectedTotal)
    {
        var total = FixedPriceQuote.CalculateTotal(quantity);

        Assert.Equal(expectedTotal, total);
    }

    /// <summary>Verifies that a nonpositive order is rejected before a price is returned.</summary>
    /// <param name="quantity">The invalid item count.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CalculateTotal_RejectsNonpositiveQuantity(int quantity)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => FixedPriceQuote.CalculateTotal(quantity));

        Assert.Equal("quantity", exception.ParamName);
    }
}
