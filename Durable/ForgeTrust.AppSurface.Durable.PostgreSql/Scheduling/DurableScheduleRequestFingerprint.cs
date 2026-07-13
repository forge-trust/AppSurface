using System.Security.Cryptography;
using System.Text;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

internal static class DurableScheduleRequestFingerprint
{
    public static byte[] ComputeCreate(
        DurableScheduleCreateRequest request,
        DurableEncodedPayload targetInput)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Compute(
            "create",
            request.ScopeId,
            request.ScheduleId,
            expectedRevision: null,
            request.DisplayName,
            request.Schedule,
            request.Target,
            targetInput);
    }

    public static byte[] ComputeUpdate(
        DurableScheduleUpdateRequest request,
        DurableEncodedPayload targetInput)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Compute(
            "update",
            request.ScopeId,
            request.ScheduleId,
            request.ExpectedRevision,
            request.DisplayName,
            request.Schedule,
            request.Target,
            targetInput);
    }

    public static byte[] ComputeCommand(string commandType, DurableScheduleCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        Write(writer, commandType);
        Write(writer, command.ScopeId.Value);
        Write(writer, command.ScheduleId.Value);
        Write(writer, command.ActorId);
        Write(writer, command.ReasonCode);
        writer.Write(command.ExpectedRevision);
        writer.Flush();
        return SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }

    private static byte[] Compute(
        string commandType,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        long? expectedRevision,
        string? displayName,
        DurableSchedule schedule,
        DurableScheduleTarget target,
        DurableEncodedPayload targetInput)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        Write(writer, commandType);
        Write(writer, scopeId.Value);
        Write(writer, scheduleId.Value);
        WriteNullable(writer, displayName);
        writer.Write(expectedRevision.HasValue);
        if (expectedRevision is { } revision)
        {
            writer.Write(revision);
        }

        WriteSchedule(writer, schedule);
        writer.Write((int)target.Kind);
        Write(writer, target.RegisteredName);
        Write(writer, target.RegisteredVersion);
        Write(writer, targetInput.ContractName);
        Write(writer, targetInput.ContractVersion);
        writer.Write((int)targetInput.Classification);
        Write(writer, targetInput.RetentionPolicyId);
        writer.Write(targetInput.Content.Length);
        writer.Write(targetInput.Content.Span);
        writer.Flush();
        return SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }

    private static void WriteSchedule(BinaryWriter writer, DurableSchedule schedule)
    {
        writer.Write((int)schedule.Kind);
        writer.Write((int)schedule.OverlapPolicy.Kind);
        writer.Write(schedule.OverlapPolicy.MaximumConcurrentRuns);
        writer.Write((int)schedule.MisfirePolicy.Kind);
        writer.Write(schedule.MisfirePolicy.MaximumOccurrences);
        switch (schedule)
        {
            case DurableAtSchedule at:
                writer.Write(at.AtUtc.UtcTicks);
                break;
            case DurableAfterSchedule after:
                writer.Write(after.Delay.Ticks);
                break;
            case DurableEverySchedule every:
                writer.Write(every.Interval.Ticks);
                writer.Write(every.AnchorUtc.HasValue);
                if (every.AnchorUtc is { } anchor)
                {
                    writer.Write(anchor.UtcTicks);
                }

                break;
            case DurableCronSchedule cron:
                Write(writer, cron.Expression);
                Write(writer, cron.IanaTimeZoneId);
                writer.Write((int)cron.Dialect);
                writer.Write((int)cron.Grammar);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(schedule));
        }
    }

    private static void WriteNullable(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            Write(writer, value);
        }
    }

    private static void Write(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
}
