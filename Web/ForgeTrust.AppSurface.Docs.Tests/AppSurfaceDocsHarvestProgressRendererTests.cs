using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsHarvestProgressRendererTests
{
    [Fact]
    public void Render_WhenHarvestCompleted_EmitsCompletionMarkerWithoutReturnUrl()
    {
        var html = AppSurfaceDocsHarvestProgressRenderer.Render(
            new AppSurfaceDocsHarvestProgressSnapshot
            {
                State = AppSurfaceDocsHarvestRunState.Completed,
                StartedUtc = DateTimeOffset.UtcNow.AddSeconds(-2),
                CompletedUtc = DateTimeOffset.UtcNow,
                TotalHarvesters = 2,
                CompletedHarvesters = 2,
                TotalDocs = 7,
                Status = "Healthy",
                Harvesters =
                [
                    new AppSurfaceDocsHarvesterProgress("MarkdownHarvester", "Succeeded", 5),
                    new AppSurfaceDocsHarvesterProgress("CSharpDocHarvester", "Succeeded", 2)
                ],
                Activity = [new AppSurfaceDocsHarvestActivity(DateTimeOffset.UtcNow, "Harvest completed.")]
            },
            123);

        Assert.Contains("data-appsurface-docs-harvest-complete=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("data-appsurface-docs-harvest-delay=\"123\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-appsurface-docs-harvest-return-url", html, StringComparison.Ordinal);
        Assert.DoesNotContain("docs-harvest-return-link", html, StringComparison.Ordinal);
        Assert.Contains("Docs processed", html, StringComparison.Ordinal);
        Assert.Contains("7", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WhenHarvestRuns_EmitsDocsPerSecondInsteadOfChart()
    {
        var html = AppSurfaceDocsHarvestProgressRenderer.Render(
            new AppSurfaceDocsHarvestProgressSnapshot
            {
                State = AppSurfaceDocsHarvestRunState.Running,
                StartedUtc = DateTimeOffset.UtcNow.AddSeconds(-10),
                TotalHarvesters = 2,
                CompletedHarvesters = 1,
                TotalDocs = 42
            },
            0);

        Assert.Contains("docs-harvest-rate", html, StringComparison.Ordinal);
        Assert.Contains("Docs/sec", html, StringComparison.Ordinal);
        Assert.DoesNotContain("docs-harvest-graph", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WhenDiagnosticsExist_EncodesDiagnosticContent()
    {
        var html = AppSurfaceDocsHarvestProgressRenderer.Render(
            new AppSurfaceDocsHarvestProgressSnapshot
            {
                State = AppSurfaceDocsHarvestRunState.Failed,
                TotalHarvesters = 1,
                CompletedHarvesters = 1,
                Status = "Failed",
                Diagnostics =
                [
                    new AppSurfaceDocsHarvestDiagnosticResponse
                    {
                        Code = "appsurface.test",
                        Severity = "Error",
                        Problem = "<problem>",
                        Fix = "Use <safe> config."
                    }
                ]
            },
            0);

        Assert.Contains("appsurface.test", html, StringComparison.Ordinal);
        Assert.Contains("&lt;problem&gt;", html, StringComparison.Ordinal);
        Assert.Contains("Use &lt;safe&gt; config.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<problem>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WhenSnapshotHasFallbackValues_ClampsAndBoundsOptionalLists()
    {
        var now = DateTimeOffset.UtcNow;
        var html = AppSurfaceDocsHarvestProgressRenderer.Render(
            new AppSurfaceDocsHarvestProgressSnapshot
            {
                State = AppSurfaceDocsHarvestRunState.Completed,
                StartedUtc = now.AddSeconds(5),
                TotalHarvesters = 1,
                CompletedHarvesters = 0,
                TotalDocs = 0,
                Harvesters =
                [
                    new AppSurfaceDocsHarvesterProgress("JavaScriptDocHarvester", "Succeeded", 0),
                    new AppSurfaceDocsHarvesterProgress("CustomHarvester", "Waiting", 0)
                ],
                Activity = Enumerable.Range(0, 9)
                    .Select(index => new AppSurfaceDocsHarvestActivity(now.AddMinutes(-index), $"Activity {index}"))
                    .ToArray(),
                Diagnostics = Enumerable.Range(0, 5)
                    .Select(index => new AppSurfaceDocsHarvestDiagnosticResponse
                    {
                        Code = $"appsurface.test.{index}",
                        Severity = "Warning",
                        Problem = $"Problem {index}"
                    })
                    .ToArray()
            },
            0);

        Assert.Contains("Docs are ready. Taking you back to the page you asked for.", html, StringComparison.Ordinal);
        Assert.Contains("<strong>1s</strong>", html, StringComparison.Ordinal);
        Assert.Contains("JavaScript public API", html, StringComparison.Ordinal);
        Assert.Contains("CustomHarvester", html, StringComparison.Ordinal);
        Assert.Contains("Activity 7", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Activity 8", html, StringComparison.Ordinal);
        Assert.Contains("appsurface.test.3", html, StringComparison.Ordinal);
        Assert.DoesNotContain("appsurface.test.4", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WhenHarvestHasNotStarted_EmitsStartingStatus()
    {
        var html = AppSurfaceDocsHarvestProgressRenderer.Render(
            new AppSurfaceDocsHarvestProgressSnapshot
            {
                State = AppSurfaceDocsHarvestRunState.Idle,
                StartedUtc = DateTimeOffset.UtcNow
            },
            0);

        Assert.Contains("Starting the first docs harvest.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderTurboStream_TargetsHarvestObservatory()
    {
        var html = AppSurfaceDocsHarvestProgressRenderer.RenderTurboStream(
            new AppSurfaceDocsHarvestProgressSnapshot
            {
                State = AppSurfaceDocsHarvestRunState.Completed,
                TotalHarvesters = 1,
                Status = "Harvesting"
            },
            0);

        Assert.Contains("<turbo-stream action=\"update\" target=\"docs-harvest-observatory\">", html, StringComparison.Ordinal);
        Assert.Contains("<template>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-appsurface-docs-harvest-return-url", html, StringComparison.Ordinal);
        Assert.DoesNotContain("docs-harvest-return-link", html, StringComparison.Ordinal);
    }
}
