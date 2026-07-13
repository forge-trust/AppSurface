namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableRetryDelayCalculatorTests
{
    [Fact]
    public void Calculate_IsDeterministicAttemptBasedAndCapped()
    {
        var first = PostgreSqlDurableRetryDelayCalculator.Calculate(
            "exponential-v1",
            1,
            1,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(1),
            42);
        var repeated = PostgreSqlDurableRetryDelayCalculator.Calculate(
            "exponential-v1",
            1,
            1,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(1),
            42);
        var second = PostgreSqlDurableRetryDelayCalculator.Calculate(
            "exponential-v1",
            1,
            2,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(1),
            42);
        var overflowSafe = PostgreSqlDurableRetryDelayCalculator.Calculate(
            "exponential-v1",
            1,
            int.MaxValue,
            TimeSpan.FromSeconds(1),
            TimeSpan.MaxValue,
            long.MinValue);

        Assert.Equal(first, repeated);
        Assert.InRange(first, TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(1200));
        Assert.True(second > first);
        Assert.InRange(overflowSafe, TimeSpan.FromTicks(1), TimeSpan.MaxValue);
    }

    [Fact]
    public void Calculate_ProviderRetryAfterOverridesJitterButCannotExceedCap()
    {
        var withinCap = PostgreSqlDurableRetryDelayCalculator.Calculate(
            "exponential-v1",
            1,
            4,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(1),
            42,
            TimeSpan.FromSeconds(17));
        var capped = PostgreSqlDurableRetryDelayCalculator.Calculate(
            "exponential-v1",
            1,
            4,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(1),
            42,
            TimeSpan.FromHours(1));

        Assert.Equal(TimeSpan.FromSeconds(17), withinCap);
        Assert.Equal(TimeSpan.FromMinutes(1), capped);
    }

    [Theory]
    [InlineData("linear-v1", 1)]
    [InlineData("exponential-v1", 2)]
    public void Calculate_RejectsUnsupportedAlgorithmOrVersion(string algorithm, int version)
    {
        Assert.Throws<InvalidDataException>(() => PostgreSqlDurableRetryDelayCalculator.Calculate(
            algorithm,
            version,
            1,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(1),
            42));
    }

    [Fact]
    public void Calculate_RejectsInvalidBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PostgreSqlDurableRetryDelayCalculator.Calculate(
            "exponential-v1", 1, 0, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PostgreSqlDurableRetryDelayCalculator.Calculate(
            "exponential-v1", 1, 1, TimeSpan.Zero, TimeSpan.FromMinutes(1), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PostgreSqlDurableRetryDelayCalculator.Calculate(
            "exponential-v1", 1, 1, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PostgreSqlDurableRetryDelayCalculator.Calculate(
            "exponential-v1",
            1,
            1,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(1),
            1,
            TimeSpan.Zero));
    }
}
