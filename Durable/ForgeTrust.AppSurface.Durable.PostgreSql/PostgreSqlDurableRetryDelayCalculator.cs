namespace ForgeTrust.AppSurface.Durable.PostgreSql;

internal static class PostgreSqlDurableRetryDelayCalculator
{
    internal static TimeSpan Calculate(
        string algorithm,
        int attemptNumber,
        TimeSpan initialDelay,
        TimeSpan maximumDelay)
    {
        if (!string.Equals(algorithm, "exponential-v1", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported durable retry algorithm '{algorithm}'.");
        }

        if (attemptNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptNumber));
        }

        if (initialDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initialDelay));
        }

        if (maximumDelay <= TimeSpan.Zero || maximumDelay < initialDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDelay));
        }

        var exponent = attemptNumber - 1;
        if (exponent >= 63 || initialDelay.Ticks > (maximumDelay.Ticks >> exponent))
        {
            return maximumDelay;
        }

        return TimeSpan.FromTicks(Math.Min(initialDelay.Ticks << exponent, maximumDelay.Ticks));
    }
}
