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
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.RazorWire.Tests;

public class ViewComponentStreamActionTests
{
    [Fact]
    public void Constructor_WithNullValues_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ViewComponentStreamAction(null!, "target", typeof(TestComponent)));
        Assert.Throws<ArgumentNullException>(() => new ViewComponentStreamAction("append", null!, typeof(TestComponent)));
        Assert.Throws<ArgumentNullException>(() => new ViewComponentStreamAction("append", "target", null!));

        Assert.Throws<ArgumentNullException>(() => new ViewComponentByNameStreamAction(null!, "target", "Widget"));
        Assert.Throws<ArgumentNullException>(() => new ViewComponentByNameStreamAction("append", null!, "Widget"));
        Assert.Throws<ArgumentNullException>(() => new ViewComponentByNameStreamAction("append", "target", null!));
    }

    [Fact]
    public async Task RenderAsync_ForTypeAction_InvokesTypedComponentAndEncodesAttributes()
    {
        // Arrange
        var helper = new RecordingViewComponentHelper("<strong>typed</strong>");
        var viewContext = CreateViewContext(helper);
        var action = new ViewComponentStreamAction("append&replace", "<target>", typeof(TestComponent), new { Id = 5 });

        // Act
        var result = await action.RenderAsync(viewContext);

        // Assert
        Assert.Equal(typeof(TestComponent), helper.LastIdentifier);
        Assert.NotNull(helper.ContextualizedViewContext);
        Assert.Contains("action=\"append&amp;replace\"", result);
        Assert.Contains("target=\"&lt;target&gt;\"", result);
        Assert.Contains("<template><strong>typed</strong></template>", result);
    }

    [Fact]
    public async Task RenderAsync_ForNamedAction_InvokesNamedComponentAndEncodesAttributes()
    {
        // Arrange
        var helper = new RecordingViewComponentHelper("<em>named</em>");
        var viewContext = CreateViewContext(helper);
        var action = new ViewComponentByNameStreamAction("replace", "panel&1", "Widget", new { Name = "x" });

        // Act
        var result = await action.RenderAsync(viewContext);

        // Assert
        Assert.Equal("Widget", helper.LastIdentifier);
        Assert.NotNull(helper.ContextualizedViewContext);
        Assert.Contains("action=\"replace\"", result);
        Assert.Contains("target=\"panel&amp;1\"", result);
        Assert.Contains("<template><em>named</em></template>", result);
    }

    private static ViewContext CreateViewContext(RecordingViewComponentHelper helper)
    {
        var tempDataFactory = A.Fake<ITempDataDictionaryFactory>();
        A.CallTo(() => tempDataFactory.GetTempData(A<HttpContext>._)).Returns(A.Fake<ITempDataDictionary>());

        var serviceProvider = new ServiceCollection()
            .AddSingleton<IViewComponentHelper>(helper)
            .AddSingleton<ITempDataDictionaryFactory>(tempDataFactory)
            .BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = serviceProvider };
        return new ViewContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            A.Fake<IView>(),
            new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()),
            A.Fake<ITempDataDictionary>(),
            TextWriter.Null,
            new HtmlHelperOptions());
    }

}
