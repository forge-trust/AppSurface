using System.Diagnostics;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsConceptDiagnosticsTests
{
    [Fact]
    public void ExpectedFailure_ReportsStableCodeCountsAndUsefulCorrectionWithoutArchiveContent()
    {
        var logger = new CaptureLogger();
        AppSurfaceDocsConceptDiagnostics.Rejected(logger, "docs-switch-artifact-overflow", "guide", "switch-fragment", 1000, 1001,
            "The complete encoded switch fragment exceeds the configured budget.");
        var message = Assert.Single(logger.Messages);
        Assert.Contains("docs-switch-artifact-overflow", message);
        Assert.Contains("1000", message);
        Assert.Contains("1001", message);
        Assert.Contains("re-export and verify", message);
        Assert.Contains("update its matching pins", message);
        Assert.Contains("restart/redeploy", message);
        Assert.Contains("HTML/search-input budget", message);
        Assert.Contains(AppSurfaceDocsConceptDiagnostics.Reference, message);
        Assert.Contains("complete encoded switch fragment", message);
    }

    [Fact]
    public void UnexpectedFailure_UsesProtectedCorrelationAndBoundedMethodsWithoutExceptionMessage()
    {
        var logger = new CaptureLogger();
        using var activity = new Activity("concept-diagnostic").SetIdFormat(ActivityIdFormat.W3C).Start();
        var context = new DefaultHttpContext { TraceIdentifier = "request-correlation" };
        context.Request.QueryString = new QueryString("?returnUrl=secret-query-value");
        try { FailWithSensitiveMessage(); }
        catch (InvalidOperationException exception)
        {
            AppSurfaceDocsConceptDiagnostics.Unexpected(logger, context, exception);
        }
        var message = Assert.Single(logger.Messages);
        Assert.Contains("docs-switch-processing-failed", message);
        Assert.Contains(activity.TraceId.ToString(), message);
        Assert.Contains(nameof(InvalidOperationException), message);
        Assert.Contains(nameof(FailWithSensitiveMessage), message);
        Assert.DoesNotContain("secret-exception-value", message);
        Assert.DoesNotContain("secret-query-value", message);
        Assert.DoesNotContain(".cs:line", message);
    }

    [Fact]
    public void UnexpectedFailure_WithoutThrownStackStillUsesRequestCorrelation()
    {
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            var logger = new CaptureLogger();
            AppSurfaceDocsConceptDiagnostics.Unexpected(logger, new DefaultHttpContext { TraceIdentifier = "request-correlation-183" }, new Exception("secret"));
            Assert.Contains("request-correlation-183", Assert.Single(logger.Messages));
            Assert.DoesNotContain("secret", logger.Messages[0]);
        }
        finally { Activity.Current = previous; }
    }

    private static void FailWithSensitiveMessage() => throw new InvalidOperationException("secret-exception-value");
    private sealed class CaptureLogger : ILogger
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
