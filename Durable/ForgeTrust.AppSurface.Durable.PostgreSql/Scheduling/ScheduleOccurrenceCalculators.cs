using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cronos;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

internal interface IScheduleOccurrenceCalculator
{
    DateTimeOffset? GetNextOccurrence(DateTimeOffset fromUtc, bool inclusive);

    DateTimeOffset? GetPreviousOccurrence(DateTimeOffset fromUtc, bool inclusive);
}

internal static class ScheduleOccurrenceCalculatorFactory
{
    public static IScheduleOccurrenceCalculator Create(
        DurableScheduleId scheduleId,
        DurableSchedule schedule,
        DateTimeOffset acceptedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        return schedule switch
        {
            DurableAtSchedule at => new OneTimeScheduleCalculator(at.AtUtc),
            DurableAfterSchedule after => new OneTimeScheduleCalculator(AddChecked(acceptedAtUtc, after.Delay)),
            DurableEverySchedule every => new EveryScheduleCalculator(
                every.AnchorUtc ?? acceptedAtUtc.ToUniversalTime(),
                every.Interval),
            DurableCronSchedule cron => new CronosV1ScheduleCalculator(scheduleId, cron),
            _ => throw new ScheduleDefinitionException(
                ScheduleDefinitionError.UnsupportedScheduleKind,
                $"Schedule kind '{schedule.Kind}' is not supported."),
        };
    }

    private static DateTimeOffset AddChecked(DateTimeOffset value, TimeSpan duration)
    {
        try
        {
            return value.ToUniversalTime().Add(duration);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ScheduleDefinitionException(
                ScheduleDefinitionError.InstantOutOfRange,
                "The schedule instant is outside the supported timestamp range.",
                exception);
        }
    }
}

internal sealed class OneTimeScheduleCalculator : IScheduleOccurrenceCalculator
{
    private readonly DateTimeOffset occurrenceUtc;

    public OneTimeScheduleCalculator(DateTimeOffset occurrenceUtc)
    {
        this.occurrenceUtc = occurrenceUtc.ToUniversalTime();
    }

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset fromUtc, bool inclusive)
    {
        var comparison = occurrenceUtc.CompareTo(fromUtc.ToUniversalTime());
        return comparison > 0 || (inclusive && comparison == 0) ? occurrenceUtc : null;
    }

    public DateTimeOffset? GetPreviousOccurrence(DateTimeOffset fromUtc, bool inclusive)
    {
        var comparison = occurrenceUtc.CompareTo(fromUtc.ToUniversalTime());
        return comparison < 0 || (inclusive && comparison == 0) ? occurrenceUtc : null;
    }
}

internal sealed class EveryScheduleCalculator : IScheduleOccurrenceCalculator
{
    private readonly long anchorTicks;
    private readonly long intervalTicks;

    public EveryScheduleCalculator(DateTimeOffset anchorUtc, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ScheduleDefinitionException(
                ScheduleDefinitionError.InvalidInterval,
                "Every schedule interval must be positive.");
        }

        anchorTicks = anchorUtc.ToUniversalTime().Ticks;
        intervalTicks = interval.Ticks;
    }

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset fromUtc, bool inclusive)
    {
        var fromTicks = fromUtc.ToUniversalTime().Ticks;
        if (fromTicks < anchorTicks)
        {
            return FromTicks(anchorTicks);
        }

        var delta = fromTicks - anchorTicks;
        var quotient = Math.DivRem(delta, intervalTicks, out var remainder);
        var step = remainder == 0 && inclusive ? quotient : quotient + 1;
        return TryGetOccurrence(step);
    }

    public DateTimeOffset? GetPreviousOccurrence(DateTimeOffset fromUtc, bool inclusive)
    {
        var fromTicks = fromUtc.ToUniversalTime().Ticks;
        if (fromTicks < anchorTicks || (fromTicks == anchorTicks && !inclusive))
        {
            return null;
        }

        var delta = fromTicks - anchorTicks;
        var quotient = Math.DivRem(delta, intervalTicks, out var remainder);
        var step = remainder == 0 && !inclusive ? quotient - 1 : quotient;
        return step < 0 ? null : TryGetOccurrence(step);
    }

    private static DateTimeOffset FromTicks(long ticks) => new(ticks, TimeSpan.Zero);

    private DateTimeOffset? TryGetOccurrence(long step)
    {
        try
        {
            var ticks = checked(anchorTicks + checked(step * intervalTicks));
            return ticks <= DateTimeOffset.MaxValue.Ticks ? FromTicks(ticks) : null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}

internal sealed class CronosV1ScheduleCalculator : IScheduleOccurrenceCalculator
{
    internal const string EvaluatorVersion = "0.13.0";

    private readonly CronExpression expression;
    private readonly TimeZoneInfo timeZone;

    public CronosV1ScheduleCalculator(DurableScheduleId scheduleId, DurableCronSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        if (schedule.Dialect != CronDialect.CronosV1)
        {
            throw new ScheduleDefinitionException(
                ScheduleDefinitionError.UnsupportedDialect,
                $"Cron dialect '{schedule.Dialect}' is not supported by this evaluator.");
        }

        JitterSeed = CronosV1JitterSeed.Derive(scheduleId);
        timeZone = IanaTimeZoneResolver.Resolve(schedule.IanaTimeZoneId);
        TimeZoneRulesFingerprint = global::ForgeTrust.AppSurface.Durable.PostgreSql.TimeZoneRulesFingerprint.Create(timeZone);
        expression = Parse(schedule, JitterSeed);
    }

    public int JitterSeed { get; }

    public string TimeZoneRulesFingerprint { get; }

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset fromUtc, bool inclusive) =>
        expression.GetNextOccurrence(fromUtc.ToUniversalTime(), timeZone, inclusive)?.ToUniversalTime();

    public DateTimeOffset? GetPreviousOccurrence(DateTimeOffset fromUtc, bool inclusive) =>
        expression.GetPreviousOccurrence(fromUtc.ToUniversalTime(), timeZone, inclusive)?.ToUniversalTime();

    private static CronExpression Parse(DurableCronSchedule schedule, int jitterSeed)
    {
        var format = schedule.Grammar switch
        {
            CronGrammar.Standard => CronFormat.Standard,
            CronGrammar.IncludeSeconds => CronFormat.IncludeSeconds,
            _ => throw new ScheduleDefinitionException(
                ScheduleDefinitionError.UnsupportedGrammar,
                $"Cron grammar '{schedule.Grammar}' is not supported by this evaluator."),
        };

        try
        {
            return CronExpression.Parse(schedule.Expression, format, jitterSeed);
        }
        catch (CronFormatException exception)
        {
            throw new ScheduleDefinitionException(
                ScheduleDefinitionError.InvalidCronExpression,
                "The CronosV1 expression is invalid for its configured grammar.",
                exception);
        }
    }
}

internal static class CronosV1JitterSeed
{
    public static int Derive(DurableScheduleId scheduleId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(scheduleId.Value));
        return BinaryPrimitives.ReadInt32BigEndian(digest);
    }
}

internal static class IanaTimeZoneResolver
{
    public static TimeZoneInfo Resolve(string ianaTimeZoneId)
    {
        if (string.IsNullOrWhiteSpace(ianaTimeZoneId))
        {
            throw new ScheduleDefinitionException(
                ScheduleDefinitionError.InvalidTimeZone,
                "An IANA time-zone identifier is required.");
        }

        if (!TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaTimeZoneId, out _))
        {
            throw new ScheduleDefinitionException(
                ScheduleDefinitionError.InvalidTimeZone,
                $"Time zone '{ianaTimeZoneId}' is not an IANA identifier.");
        }

        try
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZoneId);
            if (!timeZone.HasIanaId)
            {
                throw new ScheduleDefinitionException(
                    ScheduleDefinitionError.InvalidTimeZone,
                    $"Time zone '{ianaTimeZoneId}' is not an IANA identifier.");
            }

            return timeZone;
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new ScheduleDefinitionException(
                ScheduleDefinitionError.InvalidTimeZone,
                $"IANA time zone '{ianaTimeZoneId}' is not available on this host.",
                exception);
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new ScheduleDefinitionException(
                ScheduleDefinitionError.InvalidTimeZone,
                $"IANA time zone '{ianaTimeZoneId}' has invalid adjustment rules.",
                exception);
        }
    }
}

internal static class TimeZoneRulesFingerprint
{
    public static string Create(TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        var value = new StringBuilder(512)
            .Append(timeZone.Id)
            .Append('|')
            .Append(timeZone.BaseUtcOffset.Ticks.ToString(CultureInfo.InvariantCulture));

        foreach (var rule in timeZone.GetAdjustmentRules())
        {
            value.Append('|')
                .Append(rule.DateStart.ToString("yyyyMMdd", CultureInfo.InvariantCulture))
                .Append('-')
                .Append(rule.DateEnd.ToString("yyyyMMdd", CultureInfo.InvariantCulture))
                .Append(':')
                .Append(rule.DaylightDelta.Ticks.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(rule.BaseUtcOffsetDelta.Ticks.ToString(CultureInfo.InvariantCulture));
            AppendTransition(value, rule.DaylightTransitionStart);
            AppendTransition(value, rule.DaylightTransitionEnd);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString()))).ToLowerInvariant();
    }

    private static void AppendTransition(StringBuilder value, TimeZoneInfo.TransitionTime transition)
    {
        value.Append(':')
            .Append(transition.IsFixedDateRule ? 'F' : 'V')
            .Append(',')
            .Append(transition.Month.ToString(CultureInfo.InvariantCulture))
            .Append(',')
            .Append(transition.Week.ToString(CultureInfo.InvariantCulture))
            .Append(',')
            .Append(transition.Day.ToString(CultureInfo.InvariantCulture))
            .Append(',')
            .Append(((int)transition.DayOfWeek).ToString(CultureInfo.InvariantCulture))
            .Append(',')
            .Append(transition.TimeOfDay.TimeOfDay.Ticks.ToString(CultureInfo.InvariantCulture));
    }
}

internal enum ScheduleDefinitionError
{
    UnsupportedScheduleKind,
    UnsupportedDialect,
    UnsupportedGrammar,
    InvalidCronExpression,
    InvalidTimeZone,
    InvalidInterval,
    InstantOutOfRange,
}

internal sealed class ScheduleDefinitionException : Exception
{
    public ScheduleDefinitionException(ScheduleDefinitionError error, string message)
        : base(message)
    {
        Error = error;
    }

    public ScheduleDefinitionException(ScheduleDefinitionError error, string message, Exception innerException)
        : base(message, innerException)
    {
        Error = error;
    }

    public ScheduleDefinitionError Error { get; }
}
