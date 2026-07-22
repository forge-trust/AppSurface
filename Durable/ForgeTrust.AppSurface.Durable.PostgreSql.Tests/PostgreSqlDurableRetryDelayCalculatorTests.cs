namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableRetryDelayCalculatorTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    public void Calculate_IsJitterFreeAttemptBasedAndCapped(int attemptNumber, int expectedSeconds)
    {
        var delay = PostgreSqlDurableRetryDelayCalculator.Calculate(
            "exponential-v1",
            attemptNumber,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(1));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void Calculate_CapsBeforeShiftCanOverflow()
    {
        var capped = PostgreSqlDurableRetryDelayCalculator.Calculate(
            "exponential-v1",
            int.MaxValue,
            TimeSpan.FromSeconds(1),
            TimeSpan.MaxValue);

        Assert.Equal(TimeSpan.MaxValue, capped);
    }

    [Fact]
    public void Calculate_RejectsUnsupportedAlgorithm()
    {
        Assert.Throws<InvalidDataException>(() => PostgreSqlDurableRetryDelayCalculator.Calculate(
            "linear-v1",
            1,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Calculate_RejectsInvalidBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PostgreSqlDurableRetryDelayCalculator.Calculate(
            "exponential-v1", 0, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => PostgreSqlDurableRetryDelayCalculator.Calculate(
            "exponential-v1", 1, TimeSpan.Zero, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => PostgreSqlDurableRetryDelayCalculator.Calculate(
            "exponential-v1", 1, TimeSpan.FromSeconds(1), TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => PostgreSqlDurableRetryDelayCalculator.Calculate(
            "exponential-v1", 1, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1)));
    }
}
