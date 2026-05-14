using FakeItEasy;
using ForgeTrust.RazorWire.Bridge;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.RazorWire.Tests;

public class RazorWireStreamResultTests
{
    [Fact]
    public async Task ExecuteResultAsync_WithRawHtml_WritesBodyAndSetsTurboContentType()
    {
        // Arrange
        using var actionContext = CreateActionContext();
        var result = new RazorWireStreamResult("<turbo-stream>payload</turbo-stream>");

        // Act
        await result.ExecuteResultAsync(actionContext.ActionContext);

        // Assert
        Assert.Equal("text/vnd.turbo-stream.html", actionContext.ActionContext.HttpContext.Response.ContentType);
        Assert.Equal(
            "<turbo-stream>payload</turbo-stream>",
            await RazorWireTestContext.ReadBodyAsync(actionContext.ActionContext.HttpContext.Response));
    }

    [Fact]
    public async Task ExecuteResultAsync_WithController_ReusesControllerViewDataAndTempData()
    {
        // Arrange
        var action = new CapturingStreamAction("<turbo-stream>ok</turbo-stream>");
        var controller = new TestController();
        var tempData = A.Fake<ITempDataDictionary>();
        controller.TempData = tempData;
        controller.ViewData["user"] = "andrew";

        using var actionContext = CreateActionContext();
        var result = new RazorWireStreamResult([action], controller);

        // Act
        await result.ExecuteResultAsync(actionContext.ActionContext);

        // Assert
        Assert.NotNull(action.CapturedViewContext);
        Assert.Equal("andrew", action.CapturedViewContext!.ViewData["user"]);
        Assert.Same(tempData, action.CapturedViewContext.TempData);
    }

    [Fact]
    public async Task ExecuteResultAsync_WithAntiforgeryService_GeneratesTokensBeforeStreaming()
    {
        // Arrange
        var antiforgery = A.Fake<IAntiforgery>();
        A.CallTo(() => antiforgery.GetAndStoreTokens(A<HttpContext>._))
            .Returns(new AntiforgeryTokenSet("request", "cookie", "form", "header"));

        using var actionContext = CreateActionContext(
            services => services.AddSingleton<IAntiforgery>(antiforgery));
        var result = new RazorWireStreamResult("<turbo-stream>payload</turbo-stream>");

        // Act
        await result.ExecuteResultAsync(actionContext.ActionContext);

        // Assert
        A.CallTo(() => antiforgery.GetAndStoreTokens(actionContext.ActionContext.HttpContext)).MustHaveHappenedOnceExactly();
    }

    private static RazorWireTestContext CreateActionContext(Action<ServiceCollection>? configure = null)
    {
        return RazorWireTestContext.CreateActionContext(services =>
        {
            var tempDataFactory = A.Fake<ITempDataDictionaryFactory>();
            A.CallTo(() => tempDataFactory.GetTempData(A<HttpContext>._)).Returns(A.Fake<ITempDataDictionary>());

            services.AddSingleton<ITempDataDictionaryFactory>(tempDataFactory);
            configure?.Invoke(services);
        });
    }

    private sealed class CapturingStreamAction : IRazorWireStreamAction
    {
        private readonly string _html;

        public CapturingStreamAction(string html)
        {
            _html = html;
        }

        public ViewContext? CapturedViewContext { get; private set; }

        public Task<string> RenderAsync(ViewContext viewContext, CancellationToken cancellationToken = default)
        {
            CapturedViewContext = viewContext;
            return Task.FromResult(_html);
        }
    }

    private sealed class TestController : Controller
    {
        public TestController()
        {
            ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary());
        }
    }
}
