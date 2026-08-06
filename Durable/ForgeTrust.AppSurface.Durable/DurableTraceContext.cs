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
/// <param name="TraceParent">The normalized version-<c>00</c> W3C parent persisted only as a propagation input.</param>
/// <param name="TraceId">The normalized trace identifier parsed from <paramref name="TraceParent"/>.</param>
/// <param name="SpanId">The normalized span identifier parsed from <paramref name="TraceParent"/>.</param>
/// <param name="TraceFlags">The normalized W3C trace flags parsed from <paramref name="TraceParent"/>.</param>
/// <param name="TraceState">The optional bounded opaque state, or <see langword="null"/> when it was absent or rejected.</param>
/// <param name="CorrelationToken">The runtime-generated value-free token associated with this causal evidence.</param>
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

    /// <summary>Creates the remote W3C context used exclusively for a causal <see cref="ActivityLink"/>.</summary>
    /// <remarks>The recorded bit is retained, all reserved trace-flag bits are ignored, and the normalized opaque state is propagated without becoming a telemetry tag.</remarks>
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

    /// <summary>Captures the current W3C activity as ambient durable context.</summary>
    /// <returns>An absent capture when no valid W3C activity is current; otherwise an ambient capture with a new correlation token.</returns>
    internal static DurableTraceContextCapture CaptureCurrent() => Capture(Activity.Current);

    /// <summary>Captures a sampled activity as ambient durable context.</summary>
    /// <param name="activity">The activity to capture, if one was created by a listener.</param>
    /// <returns>An absent capture for a missing or non-W3C activity; otherwise a validated ambient capture.</returns>
    internal static DurableTraceContextCapture Capture(Activity? activity)
    {
        if (activity is null
            || activity.IdFormat != ActivityIdFormat.W3C
            || activity.TraceId == default
            || activity.SpanId == default)
        {
            return DurableTraceContextCapture.Absent;
        }

        var flags = ((byte)activity.ActivityTraceFlags).ToString("x2", CultureInfo.InvariantCulture);
        return Parse(
            $"00-{activity.TraceId.ToHexString()}-{activity.SpanId.ToHexString()}-{flags}",
            activity.TraceStateString,
            DurableTraceContextStatus.Ambient);
    }

    /// <summary>Parses persisted or ambient W3C members into a bounded durable capture.</summary>
    /// <remarks>
    /// An invalid parent returns <see cref="DurableTraceContextStatus.Invalid"/> with <c>ASDUR212</c> and drops both
    /// values. A valid parent with rejected state retains the parent, drops the state, and returns <c>ASDUR213</c>.
    /// Every retained context receives a new runtime-generated correlation token.
    /// </remarks>
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

    /// <summary>Captures a fresh execution activity while retaining the committed cause's value-free status.</summary>
    /// <remarks>
    /// A listener-created execution activity has a fresh W3C context and is therefore ambient when captured directly.
    /// Durable telemetry instead reports the status of the committed trigger (<c>linked</c>, <c>absent</c>, or
    /// <c>invalid</c>) while persisting the fresh execution context. A missing activity preserves the supplied capture.
    /// </remarks>
    internal static DurableTraceContextCapture CaptureExecution(
        Activity? activity,
        DurableTraceContextCapture committedCause)
    {
        ArgumentNullException.ThrowIfNull(committedCause);
        if (activity is null)
        {
            return committedCause;
        }

        var execution = Capture(activity);
        return execution.Context is null
            ? execution
            : execution with { Status = committedCause.Status };
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
