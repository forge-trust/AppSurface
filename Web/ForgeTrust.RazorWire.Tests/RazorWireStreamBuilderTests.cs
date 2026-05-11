using System.Text.RegularExpressions;
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

public class RazorWireStreamBuilderTests
{
    [Fact]
    public void Append_RendersCorrectMarkup()
    {
        // Arrange
        var builder = new RazorWireStreamBuilder();

        // Act
        var result = builder.Append("target-id", "<div>content</div>").Build();

        // Assert
        Assert.Contains("action=\"append\"", result);
        Assert.Contains("target=\"target-id\"", result);
        Assert.Contains("<template><div>content</div></template>", result);
    }

    [Fact]
    public void Build_RemoveAction_EmitsTagWithoutTemplate()
    {
        // Arrange
        var builder = new RazorWireStreamBuilder();

        // Act
        var result = builder.Remove("target-id").Build();

        // Assert
        Assert.Equal("<turbo-stream action=\"remove\" target=\"target-id\"></turbo-stream>", result);
    }

    [Fact]
    public void Build_EncodesTargetValue()
    {
        // Arrange
        var builder = new RazorWireStreamBuilder();

        // Act
        var result = builder.Append("<target&name>", "x").Build();

        // Assert
        Assert.Contains("target=\"&lt;target&amp;name&gt;\"", result);
    }

    [Fact]
    public void Build_WithAsyncActions_ThrowsInvalidOperationException()
    {
        // Arrange
        var builder = new RazorWireStreamBuilder()
            .AppendPartial("target-id", "_AnyPartial");

        // Act + Assert
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public async Task RenderAsync_ConcatenatesAllQueuedActions()
    {
        // Arrange
        var builder = new RazorWireStreamBuilder()
            .Append("list", "<li>one</li>")
            .Remove("obsolete");

        var viewContext = CreateViewContext();

        // Act
        var result = await builder.RenderAsync(viewContext);

        // Assert
        Assert.Contains("action=\"append\"", result);
        Assert.Contains("target=\"list\"", result);
        Assert.Contains("<template><li>one</li></template>", result);
        Assert.Contains("action=\"remove\"", result);
        Assert.Contains("target=\"obsolete\"", result);
    }

    [Fact]
    public async Task BuildResult_AllowsQueuingAllFluentActionTypes()
    {
        // Arrange
        var viewEngine = A.Fake<ICompositeViewEngine>();
        var partialView = new StaticPartialView();
        var viewComponentHelper = new RecordingViewComponentHelper();
        var tempDataFactory = A.Fake<ITempDataDictionaryFactory>();
        using var actionContext = RazorWireTestContext.CreateActionContext(services =>
        {
            services
                .AddSingleton(viewEngine)
                .AddSingleton(tempDataFactory)
                .AddSingleton<IViewComponentHelper>(viewComponentHelper);
        });

        A.CallTo(() => tempDataFactory.GetTempData(A<HttpContext>._)).Returns(A.Fake<ITempDataDictionary>());
        A.CallTo(() => viewEngine.FindView(A<ViewContext>._, A<string>._, A<bool>._))
            .ReturnsLazily(call =>
                ViewEngineResult.Found(call.GetArgument<string>(1)!, partialView));

        // Act
        var result = new RazorWireStreamBuilder()
            .Append("target-1", "<div>a</div>")
            .AppendPartial("target-2", "_SomePartial", new { Name = "A" })
            .Prepend("target-3", "<div>b</div>")
            .PrependPartial("target-4", "_SomePartial", new { Name = "B" })
            .Replace("target-5", "<div>c</div>")
            .ReplacePartial("target-6", "_SomePartial", new { Name = "C" })
            .Update("target-7", "<div>d</div>")
            .UpdatePartial("target-8", "_SomePartial", new { Name = "D" })
            .AppendComponent<TestComponent>("target-9", new { Id = 1 })
            .PrependComponent<TestComponent>("target-10", new { Id = 2 })
            .ReplaceComponent<TestComponent>("target-11", new { Id = 3 })
            .UpdateComponent<TestComponent>("target-12", new { Id = 4 })
            .AppendComponent("target-13", "Widget", new { Id = 5 })
            .PrependComponent("target-14", "Widget", new { Id = 6 })
            .ReplaceComponent("target-15", "Widget", new { Id = 7 })
            .UpdateComponent("target-16", "Widget", new { Id = 8 })
            .Remove("target-17")
            .BuildResult();

        await result.ExecuteResultAsync(actionContext.ActionContext);
        var rendered = await RazorWireTestContext.ReadBodyAsync(actionContext.ActionContext.HttpContext.Response);

        // Assert
        Assert.Equal("text/vnd.turbo-stream.html", actionContext.ActionContext.HttpContext.Response.ContentType);
        Assert.Equal(17, Regex.Matches(rendered, "<turbo-stream").Count);
        var expectedTargets = new[]
        {
            "target-1",
            "target-2",
            "target-3",
            "target-4",
            "target-5",
            "target-6",
            "target-7",
            "target-8",
            "target-9",
            "target-10",
            "target-11",
            "target-12",
            "target-13",
            "target-14",
            "target-15",
            "target-16",
            "target-17"
        };
        var previousIndex = -1;
        foreach (var target in expectedTargets)
        {
            var marker = $"target=\"{target}\"";
            var index = rendered.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Expected rendered action for {target}.");
            Assert.True(index > previousIndex, $"Expected actions to render in builder queue order.");
            previousIndex = index;
        }

        Assert.Equal(4, viewComponentHelper.TypedInvocationCount);
        Assert.Equal(4, viewComponentHelper.NamedInvocationCount);
    }

    [Fact]
    public async Task FormError_EncodesTextAndMarksResultHandled()
    {
        var tempDataFactory = A.Fake<ITempDataDictionaryFactory>();
        using var actionContext = RazorWireTestContext.CreateActionContext(services =>
        {
            services.AddSingleton(tempDataFactory);
        });
        A.CallTo(() => tempDataFactory.GetTempData(A<HttpContext>._)).Returns(A.Fake<ITempDataDictionary>());

        var result = new RazorWireStreamBuilder()
            .FormError("form-errors", "<b>Bad</b>", "<script>alert(1)</script>")
            .BuildResult(StatusCodes.Status422UnprocessableEntity);

        await result.ExecuteResultAsync(actionContext.ActionContext);
        var rendered = await RazorWireTestContext.ReadBodyAsync(actionContext.ActionContext.HttpContext.Response);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, actionContext.ActionContext.HttpContext.Response.StatusCode);
        Assert.Equal("true", actionContext.ActionContext.HttpContext.Response.Headers["X-RazorWire-Form-Handled"]);
        Assert.Contains("data-rw-form-error-generated=\"true\"", rendered);
        Assert.Contains("&lt;b&gt;Bad&lt;/b&gt;", rendered);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", rendered);
        Assert.DoesNotContain("<script>alert(1)</script>", rendered);
    }

    [Fact]
    public async Task FormValidationErrors_RendersModelStateErrorsInSortedOrderWithOverflow()
    {
        var modelState = new ModelStateDictionary();
        modelState.AddModelError(string.Empty, "Model error");
        modelState.AddModelError("Name", "Name is required");
        modelState.AddModelError("Message", "<message> is invalid");
        modelState.AddModelError("Email", "Email is required");
        var tempDataFactory = A.Fake<ITempDataDictionaryFactory>();
        using var actionContext = RazorWireTestContext.CreateActionContext(services =>
        {
            services.AddSingleton(tempDataFactory);
        });
        A.CallTo(() => tempDataFactory.GetTempData(A<HttpContext>._)).Returns(A.Fake<ITempDataDictionary>());

        var result = new RazorWireStreamBuilder()
            .FormValidationErrors("form-errors", modelState, maxErrors: 2)
            .BuildResult(StatusCodes.Status422UnprocessableEntity);

        await result.ExecuteResultAsync(actionContext.ActionContext);
        var rendered = await RazorWireTestContext.ReadBodyAsync(actionContext.ActionContext.HttpContext.Response);

        Assert.Contains("Model error", rendered);
        Assert.Contains("data-rw-form-error-field=\"Email\"", rendered);
        Assert.Contains("Email is required", rendered);
        Assert.DoesNotContain("data-rw-form-error-field=\"Name\"", rendered);
        Assert.Contains("There are 2 more validation errors.", rendered);
        Assert.Equal("true", actionContext.ActionContext.HttpContext.Response.Headers["X-RazorWire-Form-Handled"]);
    }

    [Fact]
    public async Task FormValidationErrors_EncodesVisibleModelStateErrors()
    {
        var modelState = new ModelStateDictionary();
        modelState.AddModelError("Message", "<message> is invalid");
        var tempDataFactory = A.Fake<ITempDataDictionaryFactory>();
        using var actionContext = RazorWireTestContext.CreateActionContext(services =>
        {
            services.AddSingleton(tempDataFactory);
        });
        A.CallTo(() => tempDataFactory.GetTempData(A<HttpContext>._)).Returns(A.Fake<ITempDataDictionary>());

        var result = new RazorWireStreamBuilder()
            .FormValidationErrors("form-errors", modelState)
            .BuildResult(StatusCodes.Status422UnprocessableEntity);

        await result.ExecuteResultAsync(actionContext.ActionContext);
        var rendered = await RazorWireTestContext.ReadBodyAsync(actionContext.ActionContext.HttpContext.Response);

        Assert.Contains("&lt;message&gt; is invalid", rendered);
        Assert.DoesNotContain("<message> is invalid", rendered);
        Assert.Equal("true", actionContext.ActionContext.HttpContext.Response.Headers["X-RazorWire-Form-Handled"]);
    }

    [Fact]
    public async Task FormValidationErrors_PreservesSameFieldErrorOrder()
    {
        var modelState = new ModelStateDictionary();
        modelState.AddModelError("DisplayName", "Required");
        modelState.AddModelError("DisplayName", "Must be shorter");
        var tempDataFactory = A.Fake<ITempDataDictionaryFactory>();
        using var actionContext = RazorWireTestContext.CreateActionContext(services =>
        {
            services.AddSingleton(tempDataFactory);
        });
        A.CallTo(() => tempDataFactory.GetTempData(A<HttpContext>._)).Returns(A.Fake<ITempDataDictionary>());

        var result = new RazorWireStreamBuilder()
            .FormValidationErrors("form-errors", modelState)
            .BuildResult(StatusCodes.Status422UnprocessableEntity);

        await result.ExecuteResultAsync(actionContext.ActionContext);
        var rendered = await RazorWireTestContext.ReadBodyAsync(actionContext.ActionContext.HttpContext.Response);
        var requiredIndex = rendered.IndexOf("Required", StringComparison.Ordinal);
        var lengthIndex = rendered.IndexOf("Must be shorter", StringComparison.Ordinal);

        Assert.True(requiredIndex >= 0);
        Assert.True(lengthIndex >= 0);
        Assert.True(requiredIndex < lengthIndex);
    }

    [Fact]
    public async Task FormValidationErrors_WithValidModelState_RendersFallbackMessage()
    {
        var tempDataFactory = A.Fake<ITempDataDictionaryFactory>();
        using var actionContext = RazorWireTestContext.CreateActionContext(services =>
        {
            services.AddSingleton(tempDataFactory);
        });
        A.CallTo(() => tempDataFactory.GetTempData(A<HttpContext>._)).Returns(A.Fake<ITempDataDictionary>());

        var result = new RazorWireStreamBuilder()
            .FormValidationErrors("form-errors", new ModelStateDictionary())
            .BuildResult(StatusCodes.Status422UnprocessableEntity);

        await result.ExecuteResultAsync(actionContext.ActionContext);
        var rendered = await RazorWireTestContext.ReadBodyAsync(actionContext.ActionContext.HttpContext.Response);

        Assert.Contains("We could not submit this form. Check your input and try again.", rendered);
        Assert.DoesNotContain("data-rw-form-error-list", rendered);
    }

    [Fact]
    public async Task FormValidationErrors_WithZeroMaxErrors_RendersOnlyOverflow()
    {
        var modelState = new ModelStateDictionary();
        modelState.AddModelError("Name", "Name is required");
        modelState.AddModelError("Email", "Email is required");
        var tempDataFactory = A.Fake<ITempDataDictionaryFactory>();
        using var actionContext = RazorWireTestContext.CreateActionContext(services =>
        {
            services.AddSingleton(tempDataFactory);
        });
        A.CallTo(() => tempDataFactory.GetTempData(A<HttpContext>._)).Returns(A.Fake<ITempDataDictionary>());

        var result = new RazorWireStreamBuilder()
            .FormValidationErrors("form-errors", modelState, maxErrors: 0)
            .BuildResult(StatusCodes.Status422UnprocessableEntity);

        await result.ExecuteResultAsync(actionContext.ActionContext);
        var rendered = await RazorWireTestContext.ReadBodyAsync(actionContext.ActionContext.HttpContext.Response);

        Assert.DoesNotContain("data-rw-form-error-list", rendered);
        Assert.DoesNotContain("Name is required", rendered);
        Assert.Contains("There are 2 more validation errors.", rendered);
    }

    private static ViewContext CreateViewContext()
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider()
        };

        return new ViewContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            A.Fake<IView>(),
            new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()),
            A.Fake<ITempDataDictionary>(),
            TextWriter.Null,
            new HtmlHelperOptions());
    }

    private sealed class StaticPartialView : IView
    {
        public string Path => "test";

        public Task RenderAsync(ViewContext viewContext)
        {
            return viewContext.Writer.WriteAsync("<partial-view/>");
        }
    }

}
