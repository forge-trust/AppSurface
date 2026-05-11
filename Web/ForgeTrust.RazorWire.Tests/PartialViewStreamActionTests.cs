using FakeItEasy;
using ForgeTrust.RazorWire.Bridge;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;

namespace ForgeTrust.RazorWire.Tests;

public class PartialViewStreamActionTests
{
    [Fact]
    public void Constructor_WithNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new PartialViewStreamAction(null!, "target", "view"));
        Assert.Throws<ArgumentNullException>(() => new PartialViewStreamAction("action", null!, "view"));
        Assert.Throws<ArgumentNullException>(() => new PartialViewStreamAction("action", "target", null!));
    }

    [Fact]
    public void Constructor_WithEmptyOrWhiteSpace_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new PartialViewStreamAction("", "target", "view"));
        Assert.Throws<ArgumentException>(() => new PartialViewStreamAction("action", "", "view"));
        Assert.Throws<ArgumentException>(() => new PartialViewStreamAction("action", "target", "  "));
    }

    [Fact]
    public async Task RenderAsync_WhenViewNotFound_ThrowsInvalidOperationException()
    {
        // Arrange
        var action = "replace";
        var target = "user-123";
        var viewName = "_UserPartial";
        var streamAction = new PartialViewStreamAction(action, target, viewName);

        var httpContext = new DefaultHttpContext();
        var viewEngine = A.Fake<ICompositeViewEngine>();
        var tempDataFactory = A.Fake<ITempDataDictionaryFactory>();

        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(ICompositeViewEngine))).Returns(viewEngine);
        A.CallTo(() => serviceProvider.GetService(typeof(ITempDataDictionaryFactory))).Returns(tempDataFactory);
        httpContext.RequestServices = serviceProvider;

        var viewContext = new ViewContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            A.Fake<IView>(),
            new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()),
            A.Fake<ITempDataDictionary>(),
            TextWriter.Null,
            new HtmlHelperOptions()
        );

        A.CallTo(() => viewEngine.FindView(viewContext, viewName, false))
            .Returns(ViewEngineResult.NotFound(viewName, Array.Empty<string>()));
        A.CallTo(() => viewEngine.GetView(null, viewName, false))
            .Returns(ViewEngineResult.NotFound(viewName, Array.Empty<string>()));

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => streamAction.RenderAsync(viewContext));
        Assert.Contains(viewName, ex.Message);
    }

    [Fact]
    public async Task RenderAsync_WhenFindViewFails_UsesGetViewFallback()
    {
        // Arrange
        var action = "replace";
        var target = "user-123";
        var viewName = "_UserPartial";
        var streamAction = new PartialViewStreamAction(action, target, viewName);

        var httpContext = new DefaultHttpContext();
        var viewEngine = A.Fake<ICompositeViewEngine>();
        var tempDataFactory = A.Fake<ITempDataDictionaryFactory>();

        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(ICompositeViewEngine))).Returns(viewEngine);
        A.CallTo(() => serviceProvider.GetService(typeof(ITempDataDictionaryFactory))).Returns(tempDataFactory);
        httpContext.RequestServices = serviceProvider;

        var viewContext = new ViewContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            A.Fake<IView>(),
            new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()),
            A.Fake<ITempDataDictionary>(),
            TextWriter.Null,
            new HtmlHelperOptions()
        );

        // FindView fails, GetView succeeds
        var viewFake = A.Fake<IView>();
        A.CallTo(() => viewEngine.FindView(viewContext, viewName, false))
            .Returns(ViewEngineResult.NotFound(viewName, Array.Empty<string>()));
        A.CallTo(() => viewEngine.GetView(null, viewName, false))
            .Returns(ViewEngineResult.Found(viewName, viewFake));

        // Act
        var result = await streamAction.RenderAsync(viewContext);

        // Assert
        Assert.Contains("turbo-stream", result);
        Assert.Contains("target=\"user-123\"", result);
        A.CallTo(() => viewFake.RenderAsync(A<ViewContext>.That.Matches(v => v.View == viewFake))).MustHaveHappened();
    }

    [Fact]
    public async Task RenderAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        // Arrange
        var streamAction = new PartialViewStreamAction("action", "target", "view");
        var httpContext = new DefaultHttpContext();
        var services = A.Fake<IServiceProvider>();
        var viewEngine = A.Fake<ICompositeViewEngine>();
        var viewFake = A.Fake<IView>();
        A.CallTo(() => viewEngine.FindView(A<ActionContext>._, A<string>._, A<bool>._))
            .Returns(ViewEngineResult.Found("view", viewFake));
        A.CallTo(() => services.GetService(typeof(ICompositeViewEngine))).Returns(viewEngine);
        A.CallTo(() => services.GetService(typeof(ITempDataDictionaryFactory)))
            .Returns(A.Fake<ITempDataDictionaryFactory>());
        httpContext.RequestServices = services;

        var viewContext = new ViewContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            A.Fake<IView>(),
            new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()),
            A.Fake<ITempDataDictionary>(),
            TextWriter.Null,
            new HtmlHelperOptions()
        );
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => streamAction.RenderAsync(viewContext, cts.Token));
    }
}
