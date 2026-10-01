using System.Net.Mime;
using System.Text;
using FakeItEasy;
using ForgeTrust.RazorWire.Forms;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Core.Infrastructure;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace ForgeTrust.RazorWire.Tests;

public class RazorWireAntiforgeryFailureFilterTests
{
    [Fact]
    public void Order_RunsAfterDefaultMvcFiltersWithLateAppBuffer()
    {
        var filter = CreateFilter(Environments.Production);

        Assert.Equal(int.MaxValue - 100, filter.Order);
    }

    [Fact]
    public async Task OnResultExecutionAsync_WhenRazorWireFormAntiforgeryFails_ReturnsTurboStreamToLocalFailureTarget()
    {
        var context = CreateResultExecutingContext(
            accept: "text/vnd.turbo-stream.html, text/html",
            form: new FormCollection(
                new Dictionary<string, StringValues>
                {
                    [RazorWireFormFields.FormMarker] = "1",
                    [RazorWireFormFields.FailureTarget] = "form-errors"
                }));
        var filter = CreateFilter(Environments.Development);

        await filter.OnResultExecutionAsync(context, () => CreateExecutedContext(context));

        var contentResult = Assert.IsType<ContentResult>(context.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, contentResult.StatusCode);
        Assert.Equal("text/vnd.turbo-stream.html", contentResult.ContentType);
        Assert.Contains("target=\"form-errors\"", contentResult.Content);
        Assert.Contains("Antiforgery token validation failed", contentResult.Content);
        Assert.Equal("true", context.HttpContext.Response.Headers[RazorWireFormHeaders.FormHandled]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OnResultExecutionAsync_CorrelatedFailureEchoesMetadata(bool bodyTarget, bool insideFlow)
    {
        var context = CreateResultExecutingContext(
            accept: "text/vnd.turbo-stream.html",
            form: bodyTarget ? null : new FormCollection(new Dictionary<string, StringValues>
            {
                [RazorWireFormFields.FailureTarget] = "dialog-errors"
            }));
        context.HttpContext.Request.Headers[RazorWireFormHeaders.FormRequest] = "true";
        var requestId = Guid.NewGuid().ToString("D");
        var flow = Guid.NewGuid().ToString("D");
        context.HttpContext.Request.Headers["X-RazorWire-Request"] = requestId;
        context.HttpContext.Request.Headers["X-RazorWire-Order"] = "17";
        if (insideFlow) context.HttpContext.Request.Headers["X-RazorWire-Flow"] = flow;

        await CreateFilter(Environments.Development).OnResultExecutionAsync(context, () => CreateExecutedContext(context));

        var result = Assert.IsType<ContentResult>(context.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Equal("text/vnd.turbo-stream.html", result.ContentType);
        Assert.Equal(requestId, context.HttpContext.Response.Headers["X-RazorWire-Request"]);
        Assert.Contains($"data-rw-request=\"{requestId}\"", result.Content);
        Assert.Contains("data-rw-order=\"17\"", result.Content);
        Assert.Contains("data-rw-dialog-phase=\"origin\"", result.Content);
        if (insideFlow) Assert.Contains($"data-rw-flow=\"{flow}\"", result.Content);
        else Assert.DoesNotContain("data-rw-flow=", result.Content);
        Assert.Equal(bodyTarget ? 2 : 1, result.Content!.Split("data-rw-request=", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task OnResultExecutionAsync_InvalidPresentationMetadataPreserves400WithoutStreamMutation()
    {
        var context = CreateResultExecutingContext(accept: "text/vnd.turbo-stream.html");
        context.HttpContext.Request.Headers[RazorWireFormHeaders.FormRequest] = "true";
        context.HttpContext.Request.Headers["X-RazorWire-Request"] = "invalid";
        context.HttpContext.Request.Headers["X-RazorWire-Order"] = "1";
        var logger = A.Fake<ILogger<RazorWireAntiforgeryFailureFilter>>();
        await CreateFilter(Environments.Development, logger: logger).OnResultExecutionAsync(context, () => CreateExecutedContext(context));
        var result = Assert.IsType<ContentResult>(context.Result);
        Assert.Equal(400, result.StatusCode);
        Assert.Equal(MediaTypeNames.Text.Plain, result.ContentType);
        Assert.Contains("Antiforgery token validation failed", result.Content);
        Assert.DoesNotContain("<turbo-stream", result.Content);
        Assert.False(context.HttpContext.Response.Headers.ContainsKey("X-RazorWire-Request"));
        Assert.Equal("false", context.HttpContext.Response.Headers[RazorWireFormHeaders.FormHandled]);
        var log = Assert.Single(Fake.GetCalls(logger));
        var state = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(log.Arguments[2]);
        Assert.True(Assert.IsType<bool>(Assert.Single(state, item => item.Key == "TurboStreamRequested").Value));
    }

    [Fact]
    public async Task OnResultExecutionAsync_WhenHeaderMarksRazorWireFormWithoutTarget_ReturnsTurboStreamForBodySelector()
    {
        var context = CreateResultExecutingContext(accept: "text/vnd.turbo-stream.html");
        context.HttpContext.Request.Headers[RazorWireFormHeaders.FormRequest] = "true";
        var filter = CreateFilter(Environments.Production);

        await filter.OnResultExecutionAsync(context, () => CreateExecutedContext(context));

        var contentResult = Assert.IsType<ContentResult>(context.Result);
        Assert.Contains("targets=\"body\"", contentResult.Content);
        Assert.Contains("action=\"remove\"", contentResult.Content);
        Assert.Contains("Refresh the page and try again.", contentResult.Content);
        Assert.DoesNotContain("Antiforgery token validation failed", contentResult.Content);
        Assert.Equal("true", context.HttpContext.Response.Headers[RazorWireFormHeaders.FormHandled]);
    }

    [Fact]
    public async Task OnResultExecutionAsync_WhenReadableBodyHasFailureTarget_ReturnsTurboStreamToLocalFailureTarget()
    {
        var body = "__RazorWireFormFailureTarget=form-errors";
        var context = CreateResultExecutingContext(accept: "text/vnd.turbo-stream.html");
        context.HttpContext.Request.Headers[RazorWireFormHeaders.FormRequest] = "true";
        context.HttpContext.Request.ContentType = "application/x-www-form-urlencoded";
        context.HttpContext.Request.ContentLength = Encoding.UTF8.GetByteCount(body);
        context.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var filter = CreateFilter(Environments.Development);

        await filter.OnResultExecutionAsync(context, () => CreateExecutedContext(context));

        var contentResult = Assert.IsType<ContentResult>(context.Result);
        Assert.Contains("target=\"form-errors\"", contentResult.Content);
    }

    [Fact]
    public async Task OnResultExecutionAsync_WhenFailureTargetBodyLengthUnknown_DoesNotReadTargetFromBody()
    {
        var body = "__RazorWireForm=1&__RazorWireFormFailureTarget=form-errors";
        var context = CreateResultExecutingContext(accept: "text/vnd.turbo-stream.html");
        context.HttpContext.Request.Headers[RazorWireFormHeaders.FormRequest] = "true";
        context.HttpContext.Request.ContentType = "application/x-www-form-urlencoded";
        context.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var filter = CreateFilter(Environments.Development);

        await filter.OnResultExecutionAsync(context, () => CreateExecutedContext(context));

        var contentResult = Assert.IsType<ContentResult>(context.Result);
        Assert.Contains("targets=\"body\"", contentResult.Content);
        Assert.DoesNotContain("target=\"form-errors\"", contentResult.Content);
    }

    [Fact]
    public async Task OnResultExecutionAsync_WhenReadableBodyHasNoFailureTarget_ReturnsTurboStreamForBodySelector()
    {
        var body = "name=value";
        var context = CreateResultExecutingContext(accept: "text/vnd.turbo-stream.html");
        context.HttpContext.Request.Headers[RazorWireFormHeaders.FormRequest] = "true";
        context.HttpContext.Request.ContentType = "application/x-www-form-urlencoded";
        context.HttpContext.Request.ContentLength = Encoding.UTF8.GetByteCount(body);
        context.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var filter = CreateFilter(Environments.Development);

        await filter.OnResultExecutionAsync(context, () => CreateExecutedContext(context));

        var contentResult = Assert.IsType<ContentResult>(context.Result);
        Assert.Contains("targets=\"body\"", contentResult.Content);
    }

    [Theory]
    [MemberData(nameof(FormReadExceptions))]
    public async Task OnResultExecutionAsync_WhenFailureTargetFormReadFails_ReturnsTurboStreamForBodySelector(Exception exception)
    {
        var context = CreateResultExecutingContext(accept: "text/vnd.turbo-stream.html");
        context.HttpContext.Request.Headers[RazorWireFormHeaders.FormRequest] = "true";
        context.HttpContext.Request.ContentType = "application/x-www-form-urlencoded";
        context.HttpContext.Request.ContentLength = 1;
        context.HttpContext.Features.Set<IFormFeature>(new ThrowingFormFeature(exception));
        var filter = CreateFilter(Environments.Development);

        await filter.OnResultExecutionAsync(context, () => CreateExecutedContext(context));

        var contentResult = Assert.IsType<ContentResult>(context.Result);
        Assert.Contains("targets=\"body\"", contentResult.Content);
    }

    [Fact]
    public async Task OnResultExecutionAsync_WhenFormFeatureReportsUnsupportedContentType_ReturnsTurboStreamForBodySelector()
    {
        var context = CreateResultExecutingContext(accept: "text/vnd.turbo-stream.html");
        context.HttpContext.Request.Headers[RazorWireFormHeaders.FormRequest] = "true";
        context.HttpContext.Request.ContentType = "text/plain";
        context.HttpContext.Request.ContentLength = 1;
        context.HttpContext.Features.Set<IFormFeature>(new ThrowingFormFeature(new InvalidDataException("Should not read")));
        var filter = CreateFilter(Environments.Development);

        await filter.OnResultExecutionAsync(context, () => CreateExecutedContext(context));

        var contentResult = Assert.IsType<ContentResult>(context.Result);
        Assert.Contains("targets=\"body\"", contentResult.Content);
    }

    [Fact]
    public async Task OnResultExecutionAsync_WhenNoAcceptHeader_ReturnsHtml()
    {
        var context = CreateResultExecutingContext();
        context.HttpContext.Request.Headers[RazorWireFormHeaders.FormRequest] = "true";
        var filter = CreateFilter(Environments.Production);

        await filter.OnResultExecutionAsync(context, () => CreateExecutedContext(context));

        var contentResult = Assert.IsType<ContentResult>(context.Result);
        Assert.Equal(MediaTypeNames.Text.Html, contentResult.ContentType);
        Assert.DoesNotContain("<turbo-stream", contentResult.Content);
    }

    [Fact]
    public async Task OnResultExecutionAsync_WhenHtmlHasZeroQuality_ReturnsPlainText()
    {
        var context = CreateResultExecutingContext(accept: "text/html;q=0");
        context.HttpContext.Request.Headers[RazorWireFormHeaders.FormRequest] = "true";
        var filter = CreateFilter(Environments.Production);

        await filter.OnResultExecutionAsync(context, () => CreateExecutedContext(context));

        var contentResult = Assert.IsType<ContentResult>(context.Result);
        Assert.Equal(MediaTypeNames.Text.Plain, contentResult.ContentType);
    }

    [Fact]
    public async Task OnResultExecutionAsync_WhenDevelopmentPlainTextIsAccepted_IncludesDiagnosticDetail()
    {
        var context = CreateResultExecutingContext(accept: "text/plain");
        context.HttpContext.Request.Headers[RazorWireFormHeaders.FormRequest] = "true";
        var filter = CreateFilter(Environments.Development);

        await filter.OnResultExecutionAsync(context, () => CreateExecutedContext(context));

        var contentResult = Assert.IsType<ContentResult>(context.Result);
        Assert.Contains("The token may be missing or stale after a partial form update.", contentResult.Content);
        Assert.Contains("Render the whole <form> with ReplacePartial when replacing a form.", contentResult.Content);
    }

    [Fact]
    public async Task OnResultExecutionAsync_WhenHtmlIsAccepted_ReturnsHtmlDiagnostic()
    {
        var context = CreateResultExecutingContext(
            accept: "text/html",
            form: new FormCollection(
                new Dictionary<string, StringValues>
                {
                    [RazorWireFormFields.FormMarker] = "1"
                }));
        var filter = CreateFilter(Environments.Development);

        await filter.OnResultExecutionAsync(context, () => CreateExecutedContext(context));

        var contentResult = Assert.IsType<ContentResult>(context.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, contentResult.StatusCode);
        Assert.Equal(MediaTypeNames.Text.Html, contentResult.ContentType);
        Assert.Contains("data-rw-form-error-kind=\"antiforgery\"", contentResult.Content);
        Assert.Contains("Antiforgery token validation failed", contentResult.Content);
        Assert.DoesNotContain("<turbo-stream", contentResult.Content);
        Assert.Equal("true", context.HttpContext.Response.Headers[RazorWireFormHeaders.FormHandled]);
    }

    [Fact]
    public async Task OnResultExecutionAsync_WhenPlainTextIsAccepted_ReturnsProductionPlainText()
    {
        var context = CreateResultExecutingContext(accept: "text/plain");
        context.HttpContext.Request.Headers[RazorWireFormHeaders.FormRequest] = "true";
        var filter = CreateFilter(Environments.Production);

        await filter.OnResultExecutionAsync(context, () => CreateExecutedContext(context));

        var contentResult = Assert.IsType<ContentResult>(context.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, contentResult.StatusCode);
        Assert.Equal(MediaTypeNames.Text.Plain, contentResult.ContentType);
        Assert.Contains("We could not submit this form.", contentResult.Content);
        Assert.Contains("Refresh the page and try again.", contentResult.Content);
        Assert.DoesNotContain("Antiforgery token validation failed", contentResult.Content);
        Assert.DoesNotContain("<turbo-stream", contentResult.Content);
        Assert.Equal("true", context.HttpContext.Response.Headers[RazorWireFormHeaders.FormHandled]);
    }

    [Fact]
    public async Task OnResultExecutionAsync_WhenFailureUxDisabled_LeavesResultAlone()
    {
        var originalResult = new AntiforgeryValidationFailedResult();
        var context = CreateResultExecutingContext(
            accept: "text/vnd.turbo-stream.html",
            result: originalResult);
        context.HttpContext.Request.Headers[RazorWireFormHeaders.FormRequest] = "true";
        var filter = CreateFilter(
            Environments.Development,
            options =>
            {
                options.Forms.EnableFailureUx = false;
            });

        await filter.OnResultExecutionAsync(context, () => CreateExecutedContext(context));

        Assert.Same(originalResult, context.Result);
        Assert.False(context.HttpContext.Response.Headers.ContainsKey(RazorWireFormHeaders.FormHandled));
    }

    [Fact]
    public async Task OnResultExecutionAsync_WhenRequestIsNotRazorWireForm_LeavesResultAlone()
    {
        var originalResult = new AntiforgeryValidationFailedResult();
        var context = CreateResultExecutingContext(result: originalResult);
        var filter = CreateFilter(Environments.Development);

        await filter.OnResultExecutionAsync(context, () => CreateExecutedContext(context));

        Assert.Same(originalResult, context.Result);
        Assert.False(context.HttpContext.Response.Headers.ContainsKey(RazorWireFormHeaders.FormHandled));
    }

    private static RazorWireAntiforgeryFailureFilter CreateFilter(
        string environmentName,
        Action<RazorWireOptions>? configureOptions = null,
        ILogger<RazorWireAntiforgeryFailureFilter>? logger = null)
    {
        var options = new RazorWireOptions();
        configureOptions?.Invoke(options);

        return new RazorWireAntiforgeryFailureFilter(
            options,
            new RazorWireFormRequestClassifier(NullLogger<RazorWireFormRequestClassifier>.Instance),
            new TestWebHostEnvironment { EnvironmentName = environmentName },
            logger ?? NullLogger<RazorWireAntiforgeryFailureFilter>.Instance);
    }

    public static TheoryData<Exception> FormReadExceptions()
    {
        return new TheoryData<Exception>
        {
            new InvalidDataException("Invalid form"),
            new IOException("Read failed"),
            new InvalidOperationException("Form already read"),
            new BadHttpRequestException("Bad form")
        };
    }

    private static ResultExecutingContext CreateResultExecutingContext(
        string? accept = null,
        IFormCollection? form = null,
        IActionResult? result = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Path = "/Reactivity/FormFailure";

        if (!string.IsNullOrEmpty(accept))
        {
            httpContext.Request.Headers.Accept = accept;
        }

        if (form is not null)
        {
            httpContext.Request.ContentType = "application/x-www-form-urlencoded";
            httpContext.Features.Set<IFormFeature>(new FormFeature(form));
        }

        return new ResultExecutingContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            [],
            result ?? new AntiforgeryValidationFailedResult(),
            controller: new object());
    }

    private static Task<ResultExecutedContext> CreateExecutedContext(ResultExecutingContext context)
    {
        return Task.FromResult(
            new ResultExecutedContext(
                context,
                context.Filters,
                context.Result,
                context.Controller));
    }

    private sealed class AntiforgeryValidationFailedResult : IActionResult, IAntiforgeryValidationFailedResult
    {
        public Task ExecuteResultAsync(ActionContext context)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingFormFeature : IFormFeature
    {
        private readonly Exception _exception;

        public ThrowingFormFeature(Exception exception)
        {
            _exception = exception;
        }

        public bool HasFormContentType => true;

        public IFormCollection? Form { get; set; }

        public IFormCollection ReadForm()
        {
            throw _exception;
        }

        public Task<IFormCollection> ReadFormAsync(CancellationToken cancellationToken)
        {
            return Task.FromException<IFormCollection>(_exception);
        }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "TestApp";

        public string WebRootPath { get; set; } = string.Empty;

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
