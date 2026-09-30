using System.Collections.Concurrent;
using System.Diagnostics;
using FakeItEasy;
using ForgeTrust.RazorWire.Bridge;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Xunit.Abstractions;

namespace ForgeTrust.RazorWire.Tests;

public sealed class RazorWireDialogResultTests
{
    private const string RequestHeader = "X-RazorWire-Request";
    private const string OrderHeader = "X-RazorWire-Order";
    private const string FlowHeader = "X-RazorWire-Flow";
    private const string DocumentationPointer = "Docs/dialog-responses.md";

    private readonly ITestOutputHelper _output;

    public RazorWireDialogResultTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ExecuteResultAsync_DialogResultValidatesMetadataBeforeWritingOrPreparingResponse()
    {
        using var testContext = CreateContext();
        var result = new RazorWireStreamBuilder()
            .OpenDialog("Status", "Ready")
            .BuildResult(StatusCodes.Status422UnprocessableEntity);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => result.ExecuteResultAsync(testContext.ActionContext));

        Assert.Contains(DocumentationPointer, exception.Message);
        Assert.Empty(await RazorWireTestContext.ReadBodyAsync(testContext.ActionContext.HttpContext.Response));
        Assert.Null(testContext.ActionContext.HttpContext.Response.ContentType);
        Assert.Equal(StatusCodes.Status200OK, testContext.ActionContext.HttpContext.Response.StatusCode);
        Assert.False(testContext.ActionContext.HttpContext.Response.Headers.ContainsKey(RequestHeader));
    }

    [Theory]
    [InlineData(RequestHeader, "not-a-uuid")]
    [InlineData(OrderHeader, "0")]
    [InlineData(OrderHeader, "-1")]
    [InlineData(OrderHeader, " 1")]
    [InlineData(OrderHeader, "+1")]
    [InlineData(OrderHeader, "1.0")]
    [InlineData(OrderHeader, "")]
    [InlineData(RequestHeader, "")]
    [InlineData(RequestHeader, "11111111-1111-1111-1111-111111111111x")]
    [InlineData(OrderHeader, "12345678901234567")]
    [InlineData(OrderHeader, "9007199254740992")]
    [InlineData(FlowHeader, "not-a-uuid")]
    [InlineData(FlowHeader, "")]
    public async Task ExecuteResultAsync_DialogResultRejectsMalformedMetadataBeforeOutput(string header, string value)
    {
        using var testContext = CreateContext();
        SetValidMetadata(testContext.ActionContext.HttpContext.Request);
        testContext.ActionContext.HttpContext.Request.Headers[header] = value;
        var result = new RazorWireStreamBuilder().OpenDialog("Status", "Ready").BuildResult();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => result.ExecuteResultAsync(testContext.ActionContext));

        Assert.Contains(DocumentationPointer, exception.Message);
        Assert.Empty(await RazorWireTestContext.ReadBodyAsync(testContext.ActionContext.HttpContext.Response));
        Assert.Null(testContext.ActionContext.HttpContext.Response.ContentType);
    }

    [Theory]
    [InlineData(RequestHeader)]
    [InlineData(OrderHeader)]
    [InlineData(FlowHeader)]
    public async Task ExecuteResultAsync_DialogResultRejectsMultipleHeaderValuesBeforeOutput(string header)
    {
        using var testContext = CreateContext();
        SetValidMetadata(testContext.ActionContext.HttpContext.Request);
        testContext.ActionContext.HttpContext.Request.Headers[header] = new StringValues(["1", "2"]);
        var result = new RazorWireStreamBuilder().OpenDialog("Status", "Ready").BuildResult();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => result.ExecuteResultAsync(testContext.ActionContext));

        Assert.Contains(DocumentationPointer, exception.Message);
        Assert.Empty(await RazorWireTestContext.ReadBodyAsync(testContext.ActionContext.HttpContext.Response));
    }

    [Theory]
    [InlineData(RequestHeader)]
    [InlineData(OrderHeader)]
    public async Task ExecuteResultAsync_PartiallySuppliedCorrelationIsRejectedEvenForPageOnlyOutput(string missingHeader)
    {
        using var testContext = CreateContext();
        SetValidMetadata(testContext.ActionContext.HttpContext.Request);
        testContext.ActionContext.HttpContext.Request.Headers.Remove(missingHeader);
        var result = new RazorWireStreamBuilder().Update("page", "Value").BuildResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => result.ExecuteResultAsync(testContext.ActionContext));
        Assert.Empty(await RazorWireTestContext.ReadBodyAsync(testContext.ActionContext.HttpContext.Response));
    }

    [Fact]
    public async Task ExecuteResultAsync_CloseCorrelatesFollowingActionsWithClosedPhaseAtSafeIntegerBoundary()
    {
        using var testContext = CreateContext();
        SetValidMetadata(testContext.ActionContext.HttpContext.Request, order: 9_007_199_254_740_991);
        await new RazorWireStreamBuilder().CloseDialog().Remove("inside").BuildResult().ExecuteResultAsync(testContext.ActionContext);
        var html = await RazorWireTestContext.ReadBodyAsync(testContext.ActionContext.HttpContext.Response);
        Assert.Contains("dialog-command=\"close\"", html);
        Assert.Contains("data-rw-order=\"9007199254740991\"", html);
        Assert.Contains("data-rw-dialog-phase=\"closed\"", html);
        Assert.DoesNotContain("data-rw-flow=", html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteResultAsync_FinalPartialOrNamedComponentRendersTrustedBodyAndOriginalArguments(bool component)
    {
        var model = new { Name = "Trusted model" };
        var helper = new global::ForgeTrust.RazorWire.Tests.RecordingViewComponentHelper("<strong>Component body</strong>");
        var view = new RecordingPartialView("<strong>Partial body</strong>", () => { });
        var engine = A.Fake<ICompositeViewEngine>();
        A.CallTo(() => engine.FindView(A<ViewContext>._, "_Dialog", false)).Returns(ViewEngineResult.NotFound("_Dialog", []));
        A.CallTo(() => engine.GetView(null, "_Dialog", false)).Returns(ViewEngineResult.Found("_Dialog", view));
        using var testContext = CreateContext(services =>
        {
            services.AddSingleton(engine);
            services.AddSingleton<IViewComponentHelper>(helper);
        });
        SetValidMetadata(testContext.ActionContext.HttpContext.Request);
        var builder = new RazorWireStreamBuilder().OpenDialog("Discarded", "Unused");
        if (component) builder.ReplaceDialogComponent("Selected", "Widget", model);
        else builder.ReplaceDialogPartial("Selected", "_Dialog", model);
        await builder.BuildResult().ExecuteResultAsync(testContext.ActionContext);
        var html = await RazorWireTestContext.ReadBodyAsync(testContext.ActionContext.HttpContext.Response);
        Assert.Contains(component ? "<strong>Component body</strong>" : "<strong>Partial body</strong>", html);
        Assert.DoesNotContain("Unused", html);
        if (component)
        {
            Assert.Same(model, helper.LastArguments);
            Assert.Equal("Widget", helper.LastIdentifier);
            Assert.Equal(1, helper.NamedInvocationCount);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteResultAsync_CancellationDuringOrBeforeRenderingDoesNotCommitResponse(bool duringRender)
    {
        using var cancellation = new CancellationTokenSource();
        var engine = A.Fake<ICompositeViewEngine>();
        var view = new RecordingPartialView("<span>Body</span>", cancellation.Cancel);
        A.CallTo(() => engine.FindView(A<ViewContext>._, "_Dialog", false)).Returns(ViewEngineResult.Found("_Dialog", view));
        using var testContext = CreateContext(services => services.AddSingleton(engine));
        SetValidMetadata(testContext.ActionContext.HttpContext.Request);
        testContext.ActionContext.HttpContext.RequestAborted = cancellation.Token;
        if (!duringRender) cancellation.Cancel();
        var result = new RazorWireStreamBuilder().Update("page", "Value").OpenDialogPartial("Title", "_Dialog").BuildResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result.ExecuteResultAsync(testContext.ActionContext));
        Assert.Empty(await RazorWireTestContext.ReadBodyAsync(testContext.ActionContext.HttpContext.Response));
        Assert.Null(testContext.ActionContext.HttpContext.Response.ContentType);
    }

    [Fact]
    public async Task ExecuteResultAsync_DialogResultRendersOrderedActionsWithResolvedPhasesAndEchoesRequestToken()
    {
        var viewOrder = new ConcurrentQueue<string>();
        using var testContext = CreateContext(services =>
            ConfigurePartialViews(services, viewOrder, "<span>before</span>", "<span>after</span>"));
        var requestId = Guid.NewGuid();
        SetValidMetadata(testContext.ActionContext.HttpContext.Request, requestId, 19, Guid.NewGuid());

        var result = new RazorWireStreamBuilder()
            .AppendPartial("before", "Before")
            .OpenDialog("Dialog & status", "<ready>")
            .AppendPartial("inside", "After")
            .BuildResult();

        await result.ExecuteResultAsync(testContext.ActionContext);
        var response = testContext.ActionContext.HttpContext.Response;
        var rendered = await RazorWireTestContext.ReadBodyAsync(response);

        Assert.Equal(requestId.ToString("D"), response.Headers[RequestHeader]);
        Assert.Equal(new[] { "Before", "After" }, viewOrder.ToArray());
        Assert.Contains("target=\"before\" data-rw-request=", rendered);
        Assert.Contains("data-rw-dialog-phase=\"origin\"", rendered);
        Assert.Contains("action=\"rw-dialog\" dialog-command=\"open\" dialog-title=\"Dialog &amp; status\"", rendered);
        Assert.Contains("<template>&lt;ready&gt;</template>", rendered);
        Assert.Contains("target=\"inside\"", rendered);
        Assert.Contains("data-rw-dialog-phase=\"new\"", rendered);
        Assert.True(rendered.IndexOf("target=\"before\"", StringComparison.Ordinal)
                    < rendered.IndexOf("action=\"rw-dialog\"", StringComparison.Ordinal));
        Assert.True(rendered.IndexOf("action=\"rw-dialog\"", StringComparison.Ordinal)
                    < rendered.IndexOf("target=\"inside\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteResultAsync_DialogResultFailureInLaterPartialIsAtomicAndDoesNotPrepareHeaders()
    {
        var antiforgery = A.Fake<IAntiforgery>();
        var viewOrder = new ConcurrentQueue<string>();
        using var testContext = CreateContext(services =>
        {
            services.AddSingleton<IAntiforgery>(antiforgery);
            ConfigurePartialViews(services, viewOrder, "<span>already rendered in memory</span>");
        });
        SetValidMetadata(testContext.ActionContext.HttpContext.Request);
        var result = new RazorWireStreamBuilder()
            .AppendPartial("first", "Before")
            .OpenDialogPartial("Missing dialog body", "_MissingDialog")
            .BuildResult(StatusCodes.Status422UnprocessableEntity);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => result.ExecuteResultAsync(testContext.ActionContext));

        Assert.Contains("_MissingDialog", exception.Message);
        Assert.Contains(DocumentationPointer, exception.Message);
        Assert.Equal(new[] { "Before" }, viewOrder.ToArray());
        Assert.Empty(await RazorWireTestContext.ReadBodyAsync(testContext.ActionContext.HttpContext.Response));
        Assert.Null(testContext.ActionContext.HttpContext.Response.ContentType);
        Assert.Equal(StatusCodes.Status200OK, testContext.ActionContext.HttpContext.Response.StatusCode);
        Assert.False(testContext.ActionContext.HttpContext.Response.Headers.ContainsKey(RequestHeader));
        A.CallTo(() => antiforgery.GetAndStoreTokens(testContext.ActionContext.HttpContext)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ExecuteResultAsync_PageOnlyHandledValidationEchoesRequestTokenAndCorrelatesTarget()
    {
        using var testContext = CreateContext();
        var requestId = Guid.NewGuid();
        SetValidMetadata(testContext.ActionContext.HttpContext.Request, requestId, 27, Guid.NewGuid());
        var result = new RazorWireStreamBuilder()
            .FormError("form-errors", "Please retry", "Invalid value")
            .BuildResult(StatusCodes.Status422UnprocessableEntity);

        await result.ExecuteResultAsync(testContext.ActionContext);
        var response = testContext.ActionContext.HttpContext.Response;
        var rendered = await RazorWireTestContext.ReadBodyAsync(response);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, response.StatusCode);
        Assert.Equal("true", response.Headers["X-RazorWire-Form-Handled"]);
        Assert.Equal(requestId.ToString("D"), response.Headers[RequestHeader]);
        Assert.Contains("target=\"form-errors\"", rendered);
        Assert.Contains("data-rw-request=", rendered);
        Assert.Contains("data-rw-order=\"27\"", rendered);
        Assert.Contains("data-rw-flow=", rendered);
        Assert.Contains("data-rw-dialog-phase=\"origin\"", rendered);
    }

    [Fact]
    public async Task ExecuteResultAsync_DialogResultRetains422HandledFormContractAndEchoesToken()
    {
        using var testContext = CreateContext();
        var requestId = Guid.NewGuid();
        SetValidMetadata(testContext.ActionContext.HttpContext.Request, requestId, 28);
        var result = new RazorWireStreamBuilder()
            .FormError("form-errors", "Please retry", "Invalid value")
            .OpenDialog("Validation", "Correct the highlighted field.")
            .BuildResult(StatusCodes.Status422UnprocessableEntity);

        await result.ExecuteResultAsync(testContext.ActionContext);
        var response = testContext.ActionContext.HttpContext.Response;
        var rendered = await RazorWireTestContext.ReadBodyAsync(response);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, response.StatusCode);
        Assert.Equal("true", response.Headers["X-RazorWire-Form-Handled"]);
        Assert.Equal(requestId.ToString("D"), response.Headers[RequestHeader]);
        Assert.Contains("data-rw-dialog-phase=\"origin\"", rendered);
        Assert.Contains("action=\"rw-dialog\" dialog-command=\"open\"", rendered);
    }

    [Fact]
    public async Task ExecuteResultAsync_DialogResultMissingNamedComponentHasActionableDiagnosticWhenSelectorReturnsNull()
    {
        var selector = A.Fake<IViewComponentSelector>();
        A.CallTo(() => selector.SelectComponent("MissingWidget")).Returns(null!);
        var helper = new RecordingViewComponentHelper();
        using var testContext = CreateContext(services =>
        {
            services.AddSingleton(selector);
            services.AddSingleton<IViewComponentHelper>(helper);
        });
        SetValidMetadata(testContext.ActionContext.HttpContext.Request);
        var result = new RazorWireStreamBuilder()
            .OpenDialogComponent("Widget", "MissingWidget")
            .BuildResult();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => result.ExecuteResultAsync(testContext.ActionContext));

        Assert.Contains("MissingWidget", exception.Message);
        Assert.Contains(DocumentationPointer, exception.Message);
        Assert.Equal(0, helper.NamedInvocationCount);
        Assert.Empty(await RazorWireTestContext.ReadBodyAsync(testContext.ActionContext.HttpContext.Response));
    }

    [Fact]
    public async Task ExecuteResultAsync_DialogResultRendersSixteenConcurrentLongPartialAndComponentBodiesInIsolation()
    {
        const int resultCount = 16;
        const int bodyLength = 128 * 1024;
        var contexts = Enumerable.Range(0, resultCount)
            .Select(index => CreateLongRenderCase(index, bodyLength))
            .ToArray();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await Task.WhenAll(contexts.Select(item => item.Result.ExecuteResultAsync(item.Context.ActionContext)));
            stopwatch.Stop();
            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            _output.WriteLine(
                "Rendered {0} dialog results with {1:N0}-character partial and component bodies in {2:F0} ms; "
                + "process-wide allocation delta {3:N0} bytes.",
                resultCount,
                bodyLength,
                stopwatch.Elapsed.TotalMilliseconds,
                allocatedBytes);

            foreach (var item in contexts)
            {
                var response = item.Context.ActionContext.HttpContext.Response;
                var rendered = await RazorWireTestContext.ReadBodyAsync(response);
                Assert.Equal(item.RequestId.ToString("D"), response.Headers[RequestHeader]);
                Assert.Equal(new[] { "partial-before", "component", "partial-after" }, item.RenderOrder.ToArray());
                Assert.Contains($"partial-{item.Index}-before:{new string((char)('A' + item.Index), bodyLength)}", rendered);
                Assert.Contains($"component-{item.Index}:{new string((char)('a' + item.Index), bodyLength)}", rendered);
                Assert.Contains($"partial-{item.Index}-after:{new string((char)('A' + item.Index), bodyLength)}", rendered);
                Assert.DoesNotContain($"component-{(item.Index + 1) % resultCount}:", rendered);
                Assert.Equal(3, CountOccurrences(rendered, "<turbo-stream"));
            }
        }
        finally
        {
            foreach (var item in contexts)
            {
                item.Context.Dispose();
            }
        }
    }

    private static LongRenderCase CreateLongRenderCase(int index, int bodyLength)
    {
        var requestId = Guid.NewGuid();
        var renderOrder = new ConcurrentQueue<string>();
        var beforeBody = $"partial-{index}-before:{new string((char)('A' + index), bodyLength)}";
        var afterBody = $"partial-{index}-after:{new string((char)('A' + index), bodyLength)}";
        var componentBody = $"component-{index}:{new string((char)('a' + index), bodyLength)}";
        var componentHelper = new RecordingViewComponentHelper(componentBody, () => renderOrder.Enqueue("component"));
        var context = CreateContext(services =>
        {
            ConfigurePartialViews(services, renderOrder, beforeBody, afterBody);
            services.AddSingleton<IViewComponentHelper>(componentHelper);
        });
        SetValidMetadata(context.ActionContext.HttpContext.Request, requestId, index + 1, Guid.NewGuid());

        var result = new RazorWireStreamBuilder()
            .AppendPartial($"before-{index}", "Before")
            .OpenDialogComponent<TestComponent>($"Dialog {index}")
            .AppendPartial($"after-{index}", "After")
            .BuildResult();

        return new LongRenderCase(index, requestId, context, result, renderOrder);
    }

    private static RazorWireTestContext CreateContext(Action<IServiceCollection>? configure = null)
    {
        return RazorWireTestContext.CreateActionContext(services =>
        {
            var tempDataFactory = A.Fake<ITempDataDictionaryFactory>();
            A.CallTo(() => tempDataFactory.GetTempData(A<HttpContext>._))
                .Returns(A.Fake<ITempDataDictionary>());
            services.AddSingleton(tempDataFactory);
            configure?.Invoke(services);
        });
    }

    private static void ConfigurePartialViews(
        IServiceCollection services,
        ConcurrentQueue<string> renderOrder,
        string beforeBody,
        string? afterBody = null)
    {
        var viewEngine = A.Fake<ICompositeViewEngine>();
        A.CallTo(() => viewEngine.FindView(A<ViewContext>._, A<string>._, false))
            .ReturnsLazily(call =>
            {
                var viewName = call.GetArgument<string>(1)!;
                if (viewName == "_MissingDialog")
                {
                    return ViewEngineResult.NotFound(viewName, ["/Views/Dialogs/_MissingDialog.cshtml"]);
                }

                var body = viewName switch
                {
                    "Before" => beforeBody,
                    "After" when afterBody is not null => afterBody,
                    _ => throw new InvalidOperationException($"Unexpected test partial '{viewName}'.")
                };
                var marker = viewName == "Before" && beforeBody.StartsWith("partial-", StringComparison.Ordinal)
                    ? "partial-before"
                    : viewName == "After" && afterBody?.StartsWith("partial-", StringComparison.Ordinal) == true
                        ? "partial-after"
                        : viewName;
                var view = new RecordingPartialView(body, () => renderOrder.Enqueue(marker));
                return ViewEngineResult.Found(viewName, view);
            });
        A.CallTo(() => viewEngine.GetView(null, A<string>._, false))
            .ReturnsLazily(call => ViewEngineResult.NotFound(call.GetArgument<string>(1)!, []));
        services.AddSingleton(viewEngine);
    }

    private static void SetValidMetadata(
        HttpRequest request,
        Guid? requestId = null,
        long order = 1,
        Guid? flowId = null)
    {
        request.Headers[RequestHeader] = (requestId ?? Guid.NewGuid()).ToString("D");
        request.Headers[OrderHeader] = order.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (flowId is Guid value)
        {
            request.Headers[FlowHeader] = value.ToString("D");
        }
    }

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private sealed record LongRenderCase(
        int Index,
        Guid RequestId,
        RazorWireTestContext Context,
        RazorWireStreamResult Result,
        ConcurrentQueue<string> RenderOrder);

    private sealed class RecordingPartialView(string body, Action onRender) : IView
    {
        public string Path => "test-partial";

        public Task RenderAsync(ViewContext viewContext)
        {
            onRender();
            return viewContext.Writer.WriteAsync(body);
        }
    }

    private sealed class RecordingViewComponentHelper(string body = "<component/>", Action? onInvoke = null) : IViewComponentHelper, IViewContextAware
    {
        private int _namedInvocationCount;

        public int NamedInvocationCount => _namedInvocationCount;

        public void Contextualize(ViewContext viewContext)
        {
        }

        public Task<IHtmlContent> InvokeAsync(Type componentType, object? arguments)
        {
            onInvoke?.Invoke();
            return Task.FromResult<IHtmlContent>(new HtmlString(body));
        }

        public Task<IHtmlContent> InvokeAsync(string name, object? arguments)
        {
            Interlocked.Increment(ref _namedInvocationCount);
            onInvoke?.Invoke();
            return Task.FromResult<IHtmlContent>(new HtmlString(body));
        }
    }
}
