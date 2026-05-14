using FakeItEasy;
using ForgeTrust.RazorWire.Bridge;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;

namespace ForgeTrust.RazorWire.Tests;

public class RazorWireBridgeTests
{
    private class TestController : Controller
    {
        public TestController()
        {
            ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary());
        }
    }

    [Fact]
    public void Frame_SetsViewDataAndReturnsPartialViewResult()
    {
        // Arrange
        var controller = new TestController();
        var id = "my-frame";
        var partial = "_InnerPartial";
        var model = new { Name = "Test" };

        // Act
        var result = RazorWireBridge.Frame(controller, id, partial, model);

        // Assert
        Assert.Equal(id, controller.ViewData["TurboFrameId"]);
        Assert.Equal("RazorWire/_TurboFrame", result.ViewName);
        var vm = Assert.IsType<TurboFrameViewModel>(result.ViewData.Model);
        Assert.Equal(id, vm.Id);
        Assert.Equal(partial, vm.PartialView);
        Assert.Equal(model, vm.Model);
    }

    [Fact]
    public void FrameComponent_SetsViewDataAndReturnsPartialViewResult()
    {
        // Arrange
        var controller = new TestController();
        var id = "my-frame";
        var component = "MyComponent";
        var model = new { Name = "Test" };

        // Act
        var result = RazorWireBridge.FrameComponent(controller, id, component, model);

        // Assert
        Assert.Equal(id, controller.ViewData["TurboFrameId"]);
        Assert.Equal("RazorWire/_TurboFrame", result.ViewName);
        var vm = Assert.IsType<TurboFrameViewModel>(result.ViewData.Model);
        Assert.Equal(id, vm.Id);
        Assert.Equal(component, vm.ViewComponent);
        Assert.Equal(model, vm.Model);
    }

    [Fact]
    public void CreateStream_ReturnsNewBuilder()
    {
        var builder = RazorWireBridge.CreateStream();
        Assert.NotNull(builder);
    }

    [Fact]
    public void CreateViewContext_ReturnsConfiguredViewContext()
    {
        // Arrange
        var controller = new TestController();
        var httpContext = new DefaultHttpContext();
        var serviceProvider = A.Fake<IServiceProvider>();
        var tempDataFactory = A.Fake<ITempDataDictionaryFactory>();

        A.CallTo(() => serviceProvider.GetService(typeof(ITempDataDictionaryFactory))).Returns(tempDataFactory);
        A.CallTo(() => tempDataFactory.GetTempData(httpContext)).Returns(A.Fake<ITempDataDictionary>());

        httpContext.RequestServices = serviceProvider;
        var routeData = new RouteData();
        var actionDescriptor = new ControllerActionDescriptor();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
            RouteData = routeData,
            ActionDescriptor = actionDescriptor
        };

        // Act
        var viewContext = controller.CreateViewContext();

        // Assert
        Assert.NotNull(viewContext);
        Assert.Same(controller.ControllerContext.HttpContext, viewContext.HttpContext);
        Assert.Same(controller.ControllerContext.RouteData, viewContext.RouteData);
        Assert.Same(controller.ControllerContext.ActionDescriptor, viewContext.ActionDescriptor);
        Assert.Same(controller.ViewData, viewContext.ViewData);
        Assert.NotNull(viewContext.View);
        Assert.NotNull(viewContext.TempData);
        Assert.Same(TextWriter.Null, viewContext.Writer);
    }
}
