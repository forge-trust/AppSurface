using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ForgeTrust.AppSurface.Durable;

/// <summary>Compares a replayed command with a previously persisted semantic fingerprint.</summary>
public enum DurableCommandFingerprintMatch
{
    /// <summary>The schema and semantic digest match exactly.</summary>
    Exact = 0,
    /// <summary>The schema is known but semantic content differs.</summary>
    Conflict = 1,
    /// <summary>The persisted schema is not the schema understood by this request.</summary>
    UnsupportedSchema = 2,
}

/// <summary>
/// Identifies the versioned canonical semantic content of a command-bearing durable mutation.
/// </summary>
public sealed record DurableCommandFingerprint
{
    /// <summary>Initializes a validated command fingerprint.</summary>
    /// <param name="schemaId">Versioned identity of the canonical encoding used to produce the digest.</param>
    /// <param name="sha256">Exactly 64 lowercase hexadecimal SHA-256 characters.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="schemaId"/> is invalid or <paramref name="sha256"/> is not a lowercase SHA-256 digest.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="sha256"/> is null.</exception>
    public DurableCommandFingerprint(string schemaId, string sha256)
    {
        SchemaId = DurableIdentifier.Require(schemaId, nameof(schemaId), 200);
        ArgumentNullException.ThrowIfNull(sha256);
        if (sha256.Length != 64 || sha256.Any(static value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("Command fingerprints require exactly 64 lowercase hexadecimal SHA-256 characters.", nameof(sha256));
        }

        Sha256 = sha256;
    }

    /// <summary>Gets the versioned canonicalization schema.</summary>
    public string SchemaId { get; }

    /// <summary>Gets the lowercase SHA-256 digest of canonical semantic fields.</summary>
    public string Sha256 { get; }

    /// <summary>Compares a persisted fingerprint without reinterpreting an unknown schema.</summary>
    /// <param name="persisted">Previously persisted fingerprint to compare.</param>
    /// <returns>
    /// <see cref="DurableCommandFingerprintMatch.Exact"/> for identical schemas and digests,
    /// <see cref="DurableCommandFingerprintMatch.Conflict"/> for differing digests under the same schema, or
    /// <see cref="DurableCommandFingerprintMatch.UnsupportedSchema"/> for differing schemas.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="persisted"/> is null.</exception>
    public DurableCommandFingerprintMatch Compare(DurableCommandFingerprint persisted)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        if (!string.Equals(SchemaId, persisted.SchemaId, StringComparison.Ordinal))
        {
            return DurableCommandFingerprintMatch.UnsupportedSchema;
        }

        return string.Equals(Sha256, persisted.Sha256, StringComparison.Ordinal)
            ? DurableCommandFingerprintMatch.Exact
            : DurableCommandFingerprintMatch.Conflict;
    }
}

/// <summary>Creates canonical versioned fingerprints for command-bearing mutations.</summary>
internal static class DurableCommandFingerprints
{
    /// <summary>Hashes an ordered sequence of supported semantic values under one schema identity.</summary>
    /// <param name="schemaId">Versioned canonical encoding schema.</param>
    /// <param name="values">Values in their contract-defined order; null values receive an explicit marker.</param>
    /// <returns>A fingerprint containing the schema identity and canonical SHA-256 digest.</returns>
    /// <remarks>
    /// Ordering is significant. Supported values use the closed canonical encodings below rather than culture-sensitive
    /// text conversion. Any change to value ordering, null markers, supported types, or their byte encodings must use a
    /// new <paramref name="schemaId"/> so persisted fingerprints are never compared under incompatible semantics.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when <paramref name="schemaId"/> is invalid.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a value type has no canonical encoding.</exception>
    internal static DurableCommandFingerprint Create(string schemaId, params object?[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, schemaId);
        foreach (var value in values)
        {
            AppendObject(hash, value);
        }

        return new DurableCommandFingerprint(schemaId, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void AppendObject(IncrementalHash hash, object? value)
    {
        if (value is null)
        {
            AppendTag(hash, CanonicalTypeTag.Null);
            return;
        }

        switch (value)
        {
            case string text:
                AppendTag(hash, CanonicalTypeTag.String);
                Append(hash, text);
                break;
            case int number:
                AppendTag(hash, CanonicalTypeTag.Int32);
                Append(hash, number);
                break;
            case long number:
                AppendTag(hash, CanonicalTypeTag.Int64);
                Append(hash, number);
                break;
            case Enum enumValue:
                AppendTag(hash, CanonicalTypeTag.Enum);
                Append(hash, Convert.ToInt64(enumValue, System.Globalization.CultureInfo.InvariantCulture));
                break;
            case bool flag:
                AppendTag(hash, CanonicalTypeTag.Boolean);
                hash.AppendData([flag ? (byte)1 : (byte)0]);
                break;
            case DateTimeOffset timestamp:
                AppendTag(hash, CanonicalTypeTag.DateTimeOffset);
                Append(hash, timestamp.ToUniversalTime().Ticks);
                break;
            case TimeSpan duration:
                AppendTag(hash, CanonicalTypeTag.TimeSpan);
                Append(hash, duration.Ticks);
                break;
            case DurableEncodedPayload payload:
                AppendTag(hash, CanonicalTypeTag.EncodedPayload);
                AppendPayload(hash, payload);
                break;
            case DurableWorkRetryPolicy retry:
                AppendTag(hash, CanonicalTypeTag.WorkRetryPolicy);
                AppendObject(hash, retry.MaximumAttempts);
                AppendObject(hash, retry.MaximumElapsedTime);
                AppendObject(hash, retry.InitialRetryDelay);
                AppendObject(hash, retry.MaximumRetryDelay);
                AppendObject(hash, retry.LeaseDuration);
                AppendObject(hash, retry.RenewalCadence);
                AppendObject(hash, retry.MaximumLeaseLifetime);
                AppendObject(hash, retry.BackoffAlgorithm);
                break;
            case DurableSchedule schedule:
                AppendTag(hash, CanonicalTypeTag.Schedule);
                AppendSchedule(hash, schedule);
                break;
            case DurableScheduleTarget target:
                AppendTag(hash, CanonicalTypeTag.ScheduleTarget);
                AppendObject(hash, target.Kind);
                AppendObject(hash, target.RegisteredName);
                AppendObject(hash, target.RegisteredVersion);
                AppendObject(hash, target.EncodedInput);
                break;
            default:
                throw new InvalidOperationException($"Unsupported durable command fingerprint field type '{value.GetType()}'.");
        }
    }

    private static void AppendPayload(IncrementalHash hash, DurableEncodedPayload payload)
    {
        AppendObject(hash, payload.ContractName);
        AppendObject(hash, payload.ContractVersion);
        AppendObject(hash, payload.Classification);
        AppendObject(hash, payload.RetentionPolicyId);
        AppendObject(hash, payload.Sha256);
    }

    private static void AppendSchedule(IncrementalHash hash, DurableSchedule schedule)
    {
        AppendObject(hash, schedule.Kind);
        AppendObject(hash, schedule.OverlapPolicy.Kind);
        AppendObject(hash, schedule.OverlapPolicy.MaximumConcurrentRuns);
        AppendObject(hash, schedule.MisfirePolicy.Kind);
        AppendObject(hash, schedule.MisfirePolicy.MaximumOccurrences);
        switch (schedule)
        {
            case DurableAtSchedule at:
                AppendObject(hash, at.AtUtc);
                break;
            case DurableAfterSchedule after:
                AppendObject(hash, after.Delay);
                break;
            case DurableEverySchedule every:
                AppendObject(hash, every.Interval);
                AppendObject(hash, every.AnchorUtc);
                break;
            case DurableCronSchedule cron:
                AppendObject(hash, cron.Expression);
                AppendObject(hash, cron.IanaTimeZoneId);
                AppendObject(hash, cron.Grammar);
                AppendObject(hash, cron.Dialect);
                break;
            default:
                throw new InvalidOperationException($"Unsupported durable schedule type '{schedule.GetType()}'.");
        }
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Append(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendTag(IncrementalHash hash, CanonicalTypeTag tag) =>
        hash.AppendData([(byte)tag]);

    private enum CanonicalTypeTag : byte
    {
        Null = 0,
        String = 1,
        Int32 = 2,
        Int64 = 3,
        Enum = 4,
        Boolean = 5,
        DateTimeOffset = 6,
        TimeSpan = 7,
        EncodedPayload = 8,
        WorkRetryPolicy = 9,
        Schedule = 10,
        ScheduleTarget = 11,
    }
}
