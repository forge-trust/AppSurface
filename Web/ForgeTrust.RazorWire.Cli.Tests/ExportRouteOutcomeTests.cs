using System.Net;

namespace ForgeTrust.RazorWire.Cli.Tests;

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
    public void Success_Should_Treat_Html_With_Charset_As_Html()
    {
        var outcome = ExportRouteOutcome.Success(
            "/docs",
            "text/html; charset=utf-8",
            "/tmp/export/docs.html",
            "/docs.html",
            "<html></html>");

        Assert.True(outcome.IsHtml);
        Assert.False(outcome.IsCss);
    }

    [Fact]
    public void Success_Should_Treat_Css_With_Charset_As_Css()
    {
        var outcome = ExportRouteOutcome.Success(
            "/styles/site.css",
            "text/css; charset=utf-8",
            "/tmp/export/styles/site.css",
            "/styles/site.css",
            "body{color:black;}");

        Assert.False(outcome.IsHtml);
        Assert.True(outcome.IsCss);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Success_Should_Not_Treat_Missing_Content_Type_As_Text(string? contentType)
    {
        var outcome = ExportRouteOutcome.Success(
            "/download",
            contentType,
            "/tmp/export/download",
            "/download",
            textBody: null);

        Assert.False(outcome.IsHtml);
        Assert.False(outcome.IsCss);
    }

    [Fact]
    public void Success_Should_Populate_Binary_Asset_State()
    {
        var outcome = ExportRouteOutcome.Success(
            "/img/logo.png",
            "image/png",
            "/tmp/export/img/logo.png",
            "/img/logo.png",
            textBody: null);

        Assert.Equal("/img/logo.png", outcome.Route);
        Assert.True(outcome.Succeeded);
        Assert.False(outcome.IsHtml);
        Assert.False(outcome.IsCss);
        Assert.Equal("image/png", outcome.ContentType);
        Assert.Equal("/tmp/export/img/logo.png", outcome.ArtifactPath);
        Assert.Equal("/img/logo.png", outcome.ArtifactUrl);
        Assert.Null(outcome.TextBody);
        Assert.Null(outcome.StatusCode);
        Assert.Null(outcome.Exception);
    }

    [Theory]
    [InlineData(null, "/tmp/export/docs.html", "/docs.html", "route")]
    [InlineData("", "/tmp/export/docs.html", "/docs.html", "route")]
    [InlineData(" ", "/tmp/export/docs.html", "/docs.html", "route")]
    [InlineData("/docs", null, "/docs.html", "artifactPath")]
    [InlineData("/docs", "", "/docs.html", "artifactPath")]
    [InlineData("/docs", " ", "/docs.html", "artifactPath")]
    [InlineData("/docs", "/tmp/export/docs.html", null, "artifactUrl")]
    [InlineData("/docs", "/tmp/export/docs.html", "", "artifactUrl")]
    [InlineData("/docs", "/tmp/export/docs.html", " ", "artifactUrl")]
    public void Success_Should_Throw_When_Required_Text_Is_Missing(
        string? route,
        string? artifactPath,
        string? artifactUrl,
        string expectedParamName)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(
            () => ExportRouteOutcome.Success(route!, "text/html", artifactPath!, artifactUrl!, "<html></html>"));

        Assert.Equal(expectedParamName, ex.ParamName);
    }

    [Theory]
    [InlineData(HttpStatusCode.Continue)]
    [InlineData(HttpStatusCode.MultipleChoices)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public void NonSuccess_Should_Populate_Status_State(HttpStatusCode statusCode)
    {
        var outcome = ExportRouteOutcome.NonSuccess("/missing", statusCode);

        Assert.Equal("/missing", outcome.Route);
        Assert.False(outcome.Succeeded);
        Assert.Equal(statusCode, outcome.StatusCode);
        Assert.Null(outcome.ContentType);
        Assert.Null(outcome.ArtifactPath);
        Assert.Null(outcome.ArtifactUrl);
        Assert.Null(outcome.TextBody);
        Assert.Null(outcome.Exception);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void NonSuccess_Should_Throw_When_Route_Is_Missing(string? route)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => ExportRouteOutcome.NonSuccess(route!, HttpStatusCode.NotFound));

        Assert.Equal("route", ex.ParamName);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.NoContent)]
    public void NonSuccess_Should_Throw_When_Status_Code_Is_Successful(HttpStatusCode statusCode)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ExportRouteOutcome.NonSuccess("/ok", statusCode));

        Assert.Equal("statusCode", ex.ParamName);
        Assert.Equal(statusCode, ex.ActualValue);
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Failed_Should_Throw_When_Route_Is_Missing(string? route)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => ExportRouteOutcome.Failed(route!, new InvalidOperationException("boom")));

        Assert.Equal("route", ex.ParamName);
    }

    [Fact]
    public void Failed_Should_Throw_When_Exception_Is_Null()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => ExportRouteOutcome.Failed("/throws", null!));

        Assert.Equal("exception", ex.ParamName);
    }
}
