using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsHarvestProgressRendererTests
{
    [Fact]
    public void Render_WhenHarvestCompleted_EmitsCompletionMarkerAndReturnLink()
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
            "/docs/search?q=api",
            123);

        Assert.Contains("data-appsurface-docs-harvest-complete=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("data-appsurface-docs-harvest-return-url=\"/docs/search?q=api\"", html, StringComparison.Ordinal);
        Assert.Contains("data-appsurface-docs-harvest-delay=\"123\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/docs/search?q=api\"", html, StringComparison.Ordinal);
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
            "/docs",
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
            "/docs",
            0);

        Assert.Contains("appsurface.test", html, StringComparison.Ordinal);
        Assert.Contains("&lt;problem&gt;", html, StringComparison.Ordinal);
        Assert.Contains("Use &lt;safe&gt; config.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<problem>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderTurboStream_TargetsHarvestObservatory()
    {
        var html = AppSurfaceDocsHarvestProgressRenderer.RenderTurboStream(
            new AppSurfaceDocsHarvestProgressSnapshot
            {
                State = AppSurfaceDocsHarvestRunState.Running,
                TotalHarvesters = 1,
                Status = "Harvesting"
            },
            "/docs",
            0);

        Assert.Contains("<turbo-stream action=\"update\" target=\"docs-harvest-observatory\">", html, StringComparison.Ordinal);
        Assert.Contains("<template>", html, StringComparison.Ordinal);
    }
}
