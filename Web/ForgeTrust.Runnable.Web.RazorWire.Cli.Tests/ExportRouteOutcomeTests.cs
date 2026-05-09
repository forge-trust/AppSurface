using System.Net;

namespace ForgeTrust.Runnable.Web.RazorWire.Cli.Tests;

public class ExportRouteOutcomeTests
{
    [Fact]
    public void Success_Should_Populate_Success_State()
    {
        var outcome = ExportRouteOutcome.Success(
            "/docs",
            "text/html",
            "/tmp/export/docs.html",
            "/docs.html",
            "<html></html>");

        Assert.Equal("/docs", outcome.Route);
        Assert.True(outcome.Succeeded);
        Assert.True(outcome.IsHtml);
        Assert.False(outcome.IsCss);
        Assert.Equal("text/html", outcome.ContentType);
        Assert.Equal("/tmp/export/docs.html", outcome.ArtifactPath);
        Assert.Equal("/docs.html", outcome.ArtifactUrl);
        Assert.Equal("<html></html>", outcome.TextBody);
        Assert.Null(outcome.StatusCode);
        Assert.Null(outcome.Exception);
    }

    [Fact]
    public void Success_Should_Populate_Css_State()
    {
        var outcome = ExportRouteOutcome.Success(
            "/styles/site.css",
            "text/css",
            "/tmp/export/styles/site.css",
            "/styles/site.css",
            "body{color:black;}");

        Assert.Equal("/styles/site.css", outcome.Route);
        Assert.True(outcome.Succeeded);
        Assert.False(outcome.IsHtml);
        Assert.True(outcome.IsCss);
        Assert.Equal("text/css", outcome.ContentType);
        Assert.Equal("/tmp/export/styles/site.css", outcome.ArtifactPath);
        Assert.Equal("/styles/site.css", outcome.ArtifactUrl);
        Assert.Equal("body{color:black;}", outcome.TextBody);
        Assert.Null(outcome.StatusCode);
        Assert.Null(outcome.Exception);
    }

    [Fact]
    public void NonSuccess_Should_Populate_Status_State()
    {
        var outcome = ExportRouteOutcome.NonSuccess("/missing", HttpStatusCode.NotFound);

        Assert.Equal("/missing", outcome.Route);
        Assert.False(outcome.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, outcome.StatusCode);
        Assert.Null(outcome.ContentType);
        Assert.Null(outcome.ArtifactPath);
        Assert.Null(outcome.ArtifactUrl);
        Assert.Null(outcome.TextBody);
        Assert.Null(outcome.Exception);
    }

    [Fact]
    public void Failed_Should_Populate_Exception_State()
    {
        var exception = new InvalidOperationException("boom");

        var outcome = ExportRouteOutcome.Failed("/throws", exception);

        Assert.Equal("/throws", outcome.Route);
        Assert.False(outcome.Succeeded);
        Assert.Same(exception, outcome.Exception);
        Assert.Null(outcome.StatusCode);
        Assert.Null(outcome.ContentType);
        Assert.Null(outcome.ArtifactPath);
        Assert.Null(outcome.ArtifactUrl);
        Assert.Null(outcome.TextBody);
    }
}
