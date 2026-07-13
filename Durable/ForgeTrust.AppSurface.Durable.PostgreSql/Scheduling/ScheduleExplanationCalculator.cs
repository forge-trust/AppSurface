namespace ForgeTrust.AppSurface.Durable.PostgreSql;

internal static class ScheduleExplanationCalculator
{
    public static DurableScheduleExplanation Explain(DurableScheduleExplainRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var calculator = ScheduleOccurrenceCalculatorFactory.Create(
            request.ScheduleId,
            request.Schedule,
            request.AnchorUtc);
        var occurrences = new List<DateTimeOffset>(request.OccurrenceCount);
        var cursor = request.AnchorUtc;
        var inclusive = true;

        while (occurrences.Count < request.OccurrenceCount)
        {
            var occurrence = calculator.GetNextOccurrence(cursor, inclusive);
            if (occurrence is null)
            {
                break;
            }

            occurrences.Add(occurrence.Value.ToUniversalTime());
            cursor = occurrence.Value;
            inclusive = false;
        }

        if (request.Schedule is DurableCronSchedule cron && calculator is CronosV1ScheduleCalculator cronos)
        {
            return new DurableScheduleExplanation(
                request.ScheduleId,
                request.Schedule.Kind,
                request.Schedule.OverlapPolicy,
                request.Schedule.MisfirePolicy,
                occurrences,
                cron.Dialect,
                cron.Grammar,
                cron.IanaTimeZoneId,
                CronosV1ScheduleCalculator.EvaluatorVersion,
                cronos.JitterSeed,
                cronos.TimeZoneRulesFingerprint,
                ["CronosV1 daylight-saving semantics apply; returned instants are normalized to UTC."]);
        }

        var notes = request.Schedule is DurableAfterSchedule
            || request.Schedule is DurableEverySchedule { AnchorUtc: null }
            ? new[] { "The preview anchor approximates the database acceptance timestamp used when the schedule is created." }
            : [];
        return new DurableScheduleExplanation(
            request.ScheduleId,
            request.Schedule.Kind,
            request.Schedule.OverlapPolicy,
            request.Schedule.MisfirePolicy,
            occurrences,
            notes: notes);
    }
}
