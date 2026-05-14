using FakeItEasy;
using ForgeTrust.RazorWire.Bridge;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.RazorWire.Tests.Bridge;

public class RazorPartialRendererTests
{
    private readonly IRazorViewEngine _viewEngine;
    private readonly RazorPartialRenderer _sut;

    public RazorPartialRendererTests()
    {
        _viewEngine = A.Fake<IRazorViewEngine>();
        var tempDataFactory = A.Fake<ITempDataDictionaryFactory>();
        var serviceScopeFactory = A.Fake<IServiceScopeFactory>();
        var serviceScope = A.Fake<IServiceScope>();
        var serviceProvider = A.Fake<IServiceProvider>();

        A.CallTo(() => serviceScopeFactory.CreateScope()).Returns(serviceScope);
        A.CallTo(() => serviceScope.ServiceProvider).Returns(serviceProvider);

        _sut = new RazorPartialRenderer(_viewEngine, tempDataFactory, serviceScopeFactory);
    }

    [Fact]
    public async Task RenderPartialToStringAsync_ShouldReturnRenderedString_WhenViewFound()
    {
        // Arrange
        var viewName = "TestView";
        var expectedOutput = "<div>Rendered Content</div>";
        var view = A.Fake<IView>();
        var viewEngineResult = ViewEngineResult.Found(viewName, view);

        A.CallTo(() => _viewEngine.FindView(A<ActionContext>._, viewName, false))
            .Returns(viewEngineResult);

        A.CallTo(() => view.RenderAsync(A<ViewContext>._))
            .Invokes((ViewContext context) => { context.Writer.Write(expectedOutput); })
            .Returns(Task.CompletedTask);

        // Act
        var result = await _sut.RenderPartialToStringAsync(viewName);

        // Assert
        Assert.Equal(expectedOutput, result);
        A.CallTo(() => _viewEngine.GetView(A<string?>._, A<string>._!, A<bool>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task RenderPartialToStringAsync_ShouldThrowInvalidOperationException_WhenViewNotFound()
    {
        // Arrange
        var viewName = "MissingView";
        var findViewResult = ViewEngineResult.NotFound(viewName, new[] { "FindLocation1" });
        var getViewResult = ViewEngineResult.NotFound(viewName, new[] { "GetLocation1" });

        A.CallTo(() => _viewEngine.FindView(A<ActionContext>._, viewName, false))
            .Returns(findViewResult);

        A.CallTo(() => _viewEngine.GetView(null, viewName, false))
            .Returns(getViewResult);

        // Act
        var exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.RenderPartialToStringAsync(viewName));

        // Assert
        Assert.Contains(viewName, exception.Message);
        Assert.Contains("FindLocation1", exception.Message);
        Assert.Contains("GetLocation1", exception.Message);
    }

    [Fact]
    public async Task RenderPartialToStringAsync_ShouldPassModel_WhenProvided()
    {
        // Arrange
        var viewName = "ModelView";
        var model = new { Name = "Test" };
        var view = A.Fake<IView>();
        ViewContext? capturedContext = null;

        var viewEngineResult = ViewEngineResult.Found(viewName, view);
        A.CallTo(() => _viewEngine.FindView(A<ActionContext>._, viewName, false))
            .Returns(viewEngineResult);

        A.CallTo(() => view.RenderAsync(A<ViewContext>._))
            .Invokes((ViewContext context) => capturedContext = context)
            .Returns(Task.CompletedTask);

        // Act
        await _sut.RenderPartialToStringAsync(viewName, model);

        // Assert
        Assert.NotNull(capturedContext);
        Assert.Same(model, capturedContext!.ViewData.Model);
        A.CallTo(() => view.RenderAsync(A<ViewContext>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RenderPartialToStringAsync_ShouldThrowOperationCanceledException_WhenCancelled()
    {
        // Arrange
        var viewName = "CancelledView";
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _sut.RenderPartialToStringAsync(viewName, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task RenderPartialToStringAsync_ShouldPropagateCancellationTokenToHttpContext_WhenProvided()
    {
        // Arrange
        var viewName = "ViewWithToken";
        var view = A.Fake<IView>();
        ViewContext? capturedContext = null;
        using var cts = new CancellationTokenSource();

        var viewEngineResult = ViewEngineResult.Found(viewName, view);
        A.CallTo(() => _viewEngine.FindView(A<ActionContext>._, viewName, false))
            .Returns(viewEngineResult);

        A.CallTo(() => view.RenderAsync(A<ViewContext>._))
            .Invokes((ViewContext context) => capturedContext = context)
            .Returns(Task.CompletedTask);

        // Act
        await _sut.RenderPartialToStringAsync(viewName, cancellationToken: cts.Token);

        // Assert
        Assert.NotNull(capturedContext);
        Assert.Equal(cts.Token, capturedContext!.HttpContext.RequestAborted);
    }

    [Fact]
    public async Task RenderPartialToStringAsync_ShouldFallbackToGetView_WhenFindViewFails()
    {
        // Arrange
        var viewName = "~/Views/Shared/AccessibleView.cshtml"; // Path-like name
        var expectedOutput = "<div>By Path</div>";
        var view = A.Fake<IView>();

        var findViewResult = ViewEngineResult.NotFound(viewName, new[] { "StandardLocation" });
        var getViewResult = ViewEngineResult.Found(viewName, view);

        A.CallTo(() => _viewEngine.FindView(A<ActionContext>._, viewName, false))
            .Returns(findViewResult);

        A.CallTo(() => _viewEngine.GetView(null, viewName, false))
            .Returns(getViewResult);

        A.CallTo(() => view.RenderAsync(A<ViewContext>._))
            .Invokes((ViewContext context) => { context.Writer.Write(expectedOutput); })
            .Returns(Task.CompletedTask);

        // Act
        var result = await _sut.RenderPartialToStringAsync(viewName);

        // Assert
        Assert.Equal(expectedOutput, result);
    }
}
