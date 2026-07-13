using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

internal static class PostgreSqlDurableRetryDelayCalculator
{
    private static readonly byte[] JitterDomain = Encoding.UTF8.GetBytes(
        "ForgeTrust.AppSurface.Durable/retry-jitter/exponential-v1");

    internal static TimeSpan Calculate(
        string algorithm,
        int algorithmVersion,
        int attemptNumber,
        TimeSpan initialDelay,
        TimeSpan maximumDelay,
        long jitterSeed,
        TimeSpan? providerRetryAfter = null)
    {
        if (!string.Equals(algorithm, "exponential-v1", StringComparison.Ordinal) || algorithmVersion != 1)
        {
            throw new InvalidDataException($"Unsupported durable retry algorithm '{algorithm}' version {algorithmVersion}.");
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

        if (providerRetryAfter is { } retryAfter)
        {
            if (retryAfter <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(providerRetryAfter));
            }

            return retryAfter <= maximumDelay ? retryAfter : maximumDelay;
        }

        var baseTicks = SaturatingExponentialTicks(initialDelay.Ticks, maximumDelay.Ticks, attemptNumber - 1);
        var jitterBasisPoints = 8_000 + (int)(StableJitter(jitterSeed, attemptNumber) % 4_001UL);
        var jittered = decimal.Floor((decimal)baseTicks * jitterBasisPoints / 10_000m);
        var jitteredTicks = jittered >= maximumDelay.Ticks
            ? maximumDelay.Ticks
            : decimal.ToInt64(jittered);
        return TimeSpan.FromTicks(Math.Clamp(jitteredTicks, 1, maximumDelay.Ticks));
    }

    private static long SaturatingExponentialTicks(long initialTicks, long maximumTicks, int exponent)
    {
        if (exponent >= 63)
        {
            return maximumTicks;
        }

        if (initialTicks > (maximumTicks >> exponent))
        {
            return maximumTicks;
        }

        return Math.Min(initialTicks << exponent, maximumTicks);
    }

    private static ulong StableJitter(long jitterSeed, int attemptNumber)
    {
        Span<byte> input = stackalloc byte[JitterDomain.Length + sizeof(long) + sizeof(int)];
        JitterDomain.CopyTo(input);
        BinaryPrimitives.WriteInt64BigEndian(input[JitterDomain.Length..], jitterSeed);
        BinaryPrimitives.WriteInt32BigEndian(input[(JitterDomain.Length + sizeof(long))..], attemptNumber);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, hash);
        return BinaryPrimitives.ReadUInt64BigEndian(hash);
    }
}
