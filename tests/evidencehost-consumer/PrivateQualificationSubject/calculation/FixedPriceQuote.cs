namespace EvidenceHost.PrivateQualificationCalculation;

/// <summary>A small subject calculation whose actual branches are measured by the coverage producer.</summary>
public static class FixedPriceQuote
{
    /// <summary>Prices one to three items at four units each and bulk orders at three units each.</summary>
    /// <param name="quantity">The positive requested item count.</param>
    /// <returns>The calculated order total.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The quantity is zero or negative.</exception>
    public static int CalculateTotal(int quantity)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        if (quantity >= 4)
        {
            return quantity * 3;
        }

        return quantity * 4;
    }
}
