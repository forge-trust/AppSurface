using System.Diagnostics;
using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.AppSurface.Durable.Tests;

public sealed class DurableTraceContextTests
{
    [Fact]
    public void Parse_ValidParentAndState_PreservesTheBoundedW3CContext()
    {
        var capture = DurableTraceContext.Parse(
            "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01",
            "vendor=value");

        Assert.Equal(DurableTraceContextStatus.Linked, capture.Status);
        Assert.Null(capture.DiagnosticCode);
        Assert.NotNull(capture.Context);
        Assert.Equal("00-0123456789abcdef0123456789abcdef-0123456789abcdef-01", capture.Context.TraceParent);
        Assert.Equal("vendor=value", capture.Context.TraceState);
        Assert.NotEqual(Guid.Empty, capture.Context.CorrelationToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("not-a-traceparent")]
    [InlineData("00-0123456789abcdef0123456789abcdeF-0123456789abcdef-01")]
    [InlineData("00-0123456789abcdef0123456789abcdef-0123456789abcdef-0g")]
    [InlineData("00-00000000000000000000000000000000-0123456789abcdef-01")]
    [InlineData("00-0123456789abcdef0123456789abcdef-0000000000000000-01")]
    public void Parse_InvalidTraceParent_DropsBothFieldsWithValueFreeDiagnostic(string? traceParent)
    {
        var capture = DurableTraceContext.Parse(traceParent, "vendor=value");

        Assert.Null(capture.Context);
        Assert.Equal(DurableTraceContextStatus.Invalid, capture.Status);
        Assert.Equal(DurableProblemCodes.TraceContextInvalid, capture.DiagnosticCode);
    }

    [Fact]
    public void Parse_RecordedFlagWithReservedBits_PreservesTheSampledBitOnTheActivityLink()
    {
        var capture = DurableTraceContext.Parse(
            "00-0123456789abcdef0123456789abcdef-0123456789abcdef-09",
            traceState: null);

        Assert.NotNull(capture.Context);
        Assert.Equal(ActivityTraceFlags.Recorded, capture.Context.ToActivityContext().TraceFlags);
    }

    [Fact]
    public void Parse_UnrecordedFlag_PreservesAnUnsampledActivityLink()
    {
        var capture = DurableTraceContext.Parse(
            "00-0123456789abcdef0123456789abcdef-0123456789abcdef-00",
            traceState: null);

        Assert.NotNull(capture.Context);
        Assert.Equal(ActivityTraceFlags.None, capture.Context.ToActivityContext().TraceFlags);
    }

    [Fact]
    public void Parse_InvalidTraceState_PreservesValidParentAndReturnsRejectionDiagnostic()
    {
        var capture = DurableTraceContext.Parse(
            "00-0123456789abcdef0123456789abcdef-0123456789abcdef-00",
            new string('x', 513));

        Assert.NotNull(capture.Context);
        Assert.Null(capture.Context.TraceState);
        Assert.Equal(DurableTraceContextStatus.Linked, capture.Status);
        Assert.Equal(DurableProblemCodes.TraceStateRejected, capture.DiagnosticCode);
    }

    [Fact]
    public void Parse_MalformedTraceState_PreservesValidParentAndDropsOnlyTraceState()
    {
        var capture = DurableTraceContext.Parse(
            "00-0123456789abcdef0123456789abcdef-0123456789abcdef-00",
            "vendor=value=unsafe");

        Assert.NotNull(capture.Context);
        Assert.Null(capture.Context.TraceState);
        Assert.Equal(DurableProblemCodes.TraceStateRejected, capture.DiagnosticCode);
    }

    [Theory]
    [InlineData("vendor=")]
    [InlineData("vendor=value,vendor=duplicate")]
    [InlineData("vendor=value,other=value,third=value,fourth=value,fifth=value,sixth=value,seventh=value,eighth=value,ninth=value,tenth=value,eleventh=value,twelfth=value,thirteenth=value,fourteenth=value,fifteenth=value,sixteenth=value,seventeenth=value,eighteenth=value,nineteenth=value,twentieth=value,twentyfirst=value,twentysecond=value,twentythird=value,twentyfourth=value,twentyfifth=value,twentysixth=value,twentyseventh=value,twentyeighth=value,twentyninth=value,thirtieth=value,thirtyfirst=value,thirtysecond=value,thirtythird=value")]
    public void Parse_InvalidTraceStateMembers_PreservesValidParentAndDropsOnlyTraceState(string traceState)
    {
        var capture = DurableTraceContext.Parse(
            "00-0123456789abcdef0123456789abcdef-0123456789abcdef-00",
            traceState);

        Assert.NotNull(capture.Context);
        Assert.Null(capture.Context.TraceState);
        Assert.Equal(DurableProblemCodes.TraceStateRejected, capture.DiagnosticCode);
    }

    [Theory]
    [InlineData("Vendor=value")]
    [InlineData("1simple=value")]
    [InlineData("vendor@system@other=value")]
    [InlineData("vendor#name=value")]
    [InlineData("vendor=opaque\u001fvalue")]
    [InlineData("vendor=opaque\u007fvalue")]
    public void Parse_InvalidTraceStateKeyOrOpaqueValue_PreservesValidParentAndDropsOnlyTraceState(string traceState)
    {
        var capture = DurableTraceContext.Parse(
            "00-0123456789abcdef0123456789abcdef-0123456789abcdef-00",
            traceState);

        Assert.NotNull(capture.Context);
        Assert.Null(capture.Context.TraceState);
        Assert.Equal(DurableProblemCodes.TraceStateRejected, capture.DiagnosticCode);
    }

    [Theory]
    [InlineData("=value")]
    [InlineData("@vendor=value")]
    [InlineData("vendor@=value")]
    [InlineData("tenant@1system=value")]
    public void Parse_MalformedTraceStateTenantKey_PreservesValidParentAndDropsOnlyTraceState(string traceState)
    {
        var capture = DurableTraceContext.Parse(
            "00-0123456789abcdef0123456789abcdef-0123456789abcdef-00",
            traceState);

        Assert.NotNull(capture.Context);
        Assert.Null(capture.Context.TraceState);
        Assert.Equal(DurableProblemCodes.TraceStateRejected, capture.DiagnosticCode);
    }

    [Fact]
    public void Parse_TraceStateWithEveryAllowedKeyCharacter_PreservesTheOpaqueState()
    {
        var capture = DurableTraceContext.Parse(
            "00-0123456789abcdef0123456789abcdef-0123456789abcdef-00",
            "v0123456789_-*/=opaque");

        Assert.NotNull(capture.Context);
        Assert.Equal("v0123456789_-*/=opaque", capture.Context.TraceState);
        Assert.Null(capture.DiagnosticCode);
    }

    [Fact]
    public void Parse_TraceStateExceedingMemberOrFieldLimits_PreservesValidParentAndDropsOnlyTraceState()
    {
        var tooManyMembers = string.Join(
            ',',
            Enumerable.Range(1, 33).Select(index => $"vendor{index}=value"));
        var oversizedKey = $"{new string('a', 257)}=value";
        var oversizedValue = $"vendor={new string('a', 257)}";

        foreach (var traceState in new[] { tooManyMembers, oversizedKey, oversizedValue })
        {
            var capture = DurableTraceContext.Parse(
                "00-0123456789abcdef0123456789abcdef-0123456789abcdef-00",
                traceState);

            Assert.NotNull(capture.Context);
            Assert.Null(capture.Context.TraceState);
            Assert.Equal(DurableProblemCodes.TraceStateRejected, capture.DiagnosticCode);
        }
    }

    [Fact]
    public void Parse_ValidMultiTenantAndEmptyTraceStateMembers_PreservesTheTraceState()
    {
        var capture = DurableTraceContext.Parse(
            "00-0123456789abcdef0123456789abcdef-0123456789abcdef-00",
            ",1@vendor=value,,simple=opaque value,");

        Assert.NotNull(capture.Context);
        Assert.Equal(",1@vendor=value,,simple=opaque value,", capture.Context.TraceState);
        Assert.Null(capture.DiagnosticCode);
    }

    [Fact]
    public void Capture_WithoutListener_ReturnsAbsentAndDoesNotLeakAmbientActivity()
    {
        using var source = new ActivitySource("ForgeTrust.AppSurface.Durable.Tests.no-listener");

        using var activity = source.StartActivity("durable-test", ActivityKind.Consumer);

        Assert.Null(activity);
        Assert.Null(Activity.Current);
        Assert.Equal(DurableTraceContextCapture.Absent, DurableTraceContext.CaptureCurrent());
    }

    [Fact]
    public void Capture_WithListener_ReturnsW3CContextAndDisposesTheAmbientActivity()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "ForgeTrust.AppSurface.Durable.Tests.listener",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.PropagationData,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("ForgeTrust.AppSurface.Durable.Tests.listener");

        DurableTraceContextCapture capture;
        using (var activity = source.StartActivity("durable-test", ActivityKind.Consumer))
        {
            Assert.NotNull(activity);
            Assert.Same(activity, Activity.Current);
            capture = DurableTraceContext.CaptureCurrent();
        }

        Assert.NotNull(capture.Context);
        Assert.Equal(DurableTraceContextStatus.Ambient, capture.Status);
        Assert.Null(Activity.Current);
    }

    [Fact]
    public void StartRoot_WithAmbientActivity_CreatesAnUnparentedExecutionWithCauseLink()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == AppSurfaceActivitySources.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.PropagationData,
        };
        ActivitySource.AddActivityListener(listener);
        using var ambient = new Activity("ambient").SetIdFormat(ActivityIdFormat.W3C).Start();
        var cause = DurableTraceContext.Parse(
            "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01",
            traceState: null).Context;

        using (var activityScope = DurableTraceActivity.StartRoot("durable-test", ActivityKind.Consumer, cause))
        {
            var activity = activityScope.Activity;
            Assert.NotNull(activity);
            Assert.Equal(default, activity.ParentSpanId);
            Assert.NotEqual(ambient.TraceId, activity.TraceId);
            Assert.Same(ambient, Activity.Current);
            var link = Assert.Single(activity.Links);
            Assert.Equal(cause!.TraceId, link.Context.TraceId.ToHexString());
            Assert.Equal(cause.SpanId, link.Context.SpanId.ToHexString());
        }

        Assert.Same(ambient, Activity.Current);
    }

    [Fact]
    public void StartRoot_WithoutAmbientActivity_UsesTheActivitySourceLifetime()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == AppSurfaceActivitySources.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.PropagationData,
        };
        ActivitySource.AddActivityListener(listener);

        var activityScope = DurableTraceActivity.StartRoot("durable-test", ActivityKind.Consumer, committedCause: null);
        var activity = activityScope.Activity;

        Assert.NotNull(activity);
        Assert.Equal(default, activity.ParentSpanId);
        Assert.Same(activity, Activity.Current);

        activityScope.Dispose();
        activityScope.Dispose();

        Assert.Null(Activity.Current);
    }

    [Fact]
    public void StartRoot_WithoutAListener_ReturnsAnEmptyScope()
    {
        using var activityScope = DurableTraceActivity.StartRoot(
            "durable-test",
            ActivityKind.Consumer,
            committedCause: null);

        Assert.Null(activityScope.Activity);
        Assert.Null(Activity.Current);
    }

    [Fact]
    public void StartRoot_WithAmbientActivity_RelaysAListenerFailure()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == AppSurfaceActivitySources.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = static _ => throw new InvalidOperationException("The test listener failed."),
        };
        ActivitySource.AddActivityListener(listener);
        using var ambient = new Activity("ambient").SetIdFormat(ActivityIdFormat.W3C).Start();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DurableTraceActivity.StartRoot("durable-test", ActivityKind.Consumer, committedCause: null));

        Assert.Equal("The test listener failed.", exception.Message);
        Assert.Same(ambient, Activity.Current);
    }

    [Fact]
    public void Apply_UsesOnlyTheFixedValueFreeTelemetryVocabulary()
    {
        using var activity = new Activity("durable-test").Start();
        var token = Guid.NewGuid();

        DurableTraceTelemetry.Apply(
            activity,
            "flow",
            "claim",
            "ready",
            "applied",
            token,
            DurableTraceContextStatus.Linked);

        var tags = activity.TagObjects.ToDictionary(pair => pair.Key, pair => pair.Value);
        var expected = new[]
        {
                DurableTraceTelemetry.ContractVersion,
                DurableTraceTelemetry.ExecutionKind,
                DurableTraceTelemetry.TriggerKind,
                DurableTraceTelemetry.FlowState,
                DurableTraceTelemetry.Outcome,
                DurableTraceTelemetry.CorrelationToken,
                DurableTraceTelemetry.ContextStatus
        };
        Assert.Equal(
            expected.OrderBy(key => key, StringComparer.Ordinal),
            tags.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.DoesNotContain(tags.Keys, key => key.Contains("traceparent", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(tags.Keys, key => key.Contains("tracestate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Apply_WithoutAValidatedContext_OmitsTheCorrelationToken()
    {
        using var activity = new Activity("durable-test").Start();

        DurableTraceTelemetry.Apply(
            activity,
            "flow",
            "claim",
            "ready",
            "applied",
            Guid.Empty,
            DurableTraceContextStatus.Absent);

        Assert.DoesNotContain(activity.TagObjects, pair => pair.Key == DurableTraceTelemetry.CorrelationToken);
    }

    [Fact]
    public void Report_EmitsOnlyAllowlistedValueFreeDiagnosticCodes()
    {
        using var output = new StringWriter();
        using var listener = new TextWriterTraceListener(output);
        Trace.Listeners.Add(listener);
        try
        {
            DurableTraceDiagnostics.Report(DurableProblemCodes.TraceStateRejected);
            DurableTraceDiagnostics.Report("vendor=value=unsafe");
            listener.Flush();
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        var emitted = output.ToString();
        Assert.Contains(DurableProblemCodes.TraceStateRejected, emitted, StringComparison.Ordinal);
        Assert.DoesNotContain("vendor=value=unsafe", emitted, StringComparison.Ordinal);
    }
}
