using System.Diagnostics;
using System.Globalization;

namespace ForgeTrust.AppSurface.Durable;

/// <summary>Classifies the safely handled state of ambient or persisted durable trace context.</summary>
internal enum DurableTraceContextStatus
{
    Absent,
    Ambient,
    Linked,
    Invalid
}

/// <summary>Represents one validated, versioned W3C context that can become durable causal evidence.</summary>
/// <remarks>
/// The type is internal to the Durable package family. It deliberately contains no baggage, authorization, scope, or
/// payload metadata. Its raw W3C members are persistence and propagation inputs only; instrumentation must not tag or
/// log them.
/// </remarks>
internal sealed record DurableTraceContext(
    string TraceParent,
    string TraceId,
    string SpanId,
    string TraceFlags,
    string? TraceState,
    Guid CorrelationToken)
{
    internal const short ContractVersion = 1;

    internal ActivityContext ToActivityContext()
    {
        var flags = (byte.Parse(TraceFlags, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture) & 0x01) != 0
            ? ActivityTraceFlags.Recorded
            : ActivityTraceFlags.None;
        return new ActivityContext(
            ActivityTraceId.CreateFromString(TraceId.AsSpan()),
            ActivitySpanId.CreateFromString(SpanId.AsSpan()),
            flags,
            TraceState,
            isRemote: true);
    }

    internal static DurableTraceContextCapture CaptureCurrent() => Capture(Activity.Current);

    internal static DurableTraceContextCapture Capture(Activity? activity)
    {
        if (activity is null
            || activity.IdFormat != ActivityIdFormat.W3C
            || activity.TraceId == default
            || activity.SpanId == default)
        {
            return DurableTraceContextCapture.Absent;
        }

        var flags = ((byte)activity.ActivityTraceFlags).ToString("x2");
        return Parse(
            $"00-{activity.TraceId.ToHexString()}-{activity.SpanId.ToHexString()}-{flags}",
            activity.TraceStateString,
            DurableTraceContextStatus.Ambient);
    }

    internal static DurableTraceContextCapture Parse(
        string? traceParent,
        string? traceState,
        DurableTraceContextStatus validStatus = DurableTraceContextStatus.Linked)
    {
        if (!DurableTraceContextValidation.TryParseTraceParent(
                traceParent,
                out var normalizedParent,
                out var traceId,
                out var spanId,
                out var flags))
        {
            return new DurableTraceContextCapture(
                null,
                DurableTraceContextStatus.Invalid,
                DurableProblemCodes.TraceContextInvalid);
        }

        if (!DurableTraceContextValidation.TryNormalizeTraceState(traceState, out var normalizedState))
        {
            return new DurableTraceContextCapture(
                new DurableTraceContext(normalizedParent, traceId, spanId, flags, null, Guid.NewGuid()),
                validStatus,
                DurableProblemCodes.TraceStateRejected);
        }

        return new DurableTraceContextCapture(
            new DurableTraceContext(normalizedParent, traceId, spanId, flags, normalizedState, Guid.NewGuid()),
            validStatus,
            null);
    }
}

/// <summary>Returns a context with a value-free status and optional diagnostic code.</summary>
internal sealed record DurableTraceContextCapture(
    DurableTraceContext? Context,
    DurableTraceContextStatus Status,
    string? DiagnosticCode)
{
    internal static DurableTraceContextCapture Absent { get; } =
        new(null, DurableTraceContextStatus.Absent, null);
}
