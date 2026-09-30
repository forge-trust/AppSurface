using System.Reflection;
using FakeItEasy;
using ForgeTrust.RazorWire.Bridge;
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

namespace ForgeTrust.RazorWire.Tests;

public sealed class RazorWireDialogRenderingTests
{
    private static readonly RazorWireRequestMetadata Metadata = new(Guid.NewGuid(), 1, null);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComponentActions_CorrelatedRenderingPreservesTargetBodyArgumentsAndPhase(bool named)
    {
        var helper = new RecordingViewComponentHelper("<strong>Trusted</strong>");
        using var context = CreateContext(services => services.AddSingleton<IViewComponentHelper>(helper));
        var arguments = new { Id = 42 };
        IRazorWireTargetedStreamAction action = named
            ? new ViewComponentByNameStreamAction("update", "panel&1", "Widget", arguments)
            : new ViewComponentStreamAction("update", "panel&1", typeof(TestComponent), arguments);

        Assert.Equal("panel&1", action.Target);
        var html = await action.RenderCorrelatedAsync(CreateViewContext(context), Metadata, RazorWireDialogPhase.New);

        Assert.Contains("target=\"panel&amp;1\"", html);
        Assert.Contains("<template><strong>Trusted</strong></template>", html);
        Assert.Contains("data-rw-dialog-phase=\"new\"", html);
        Assert.Same(arguments, helper.LastArguments);
        Assert.Equal(named ? "Widget" : typeof(TestComponent), helper.LastIdentifier);
        Assert.NotNull(helper.ContextualizedViewContext);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedComponent_DiscoveryChecksExactTypeBeforeInvoking(bool discovered)
    {
        var helper = new RecordingViewComponentHelper();
        var descriptors = A.Fake<IViewComponentDescriptorCollectionProvider>();
        var descriptor = new ViewComponentDescriptor { TypeInfo = typeof(TestComponent).GetTypeInfo() };
        A.CallTo(() => descriptors.ViewComponents).Returns(new ViewComponentDescriptorCollection(discovered ? [descriptor] : [], 0));
        using var context = CreateContext(services =>
        {
            services.AddSingleton<IViewComponentHelper>(helper);
            services.AddSingleton(descriptors);
        });
        var action = new ViewComponentStreamAction("update", "panel", typeof(TestComponent));

        if (discovered)
        {
            Assert.Contains("<component/>", await action.RenderAsync(CreateViewContext(context)));
            Assert.Equal(1, helper.TypedInvocationCount);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => action.RenderAsync(CreateViewContext(context)));
            Assert.Contains(typeof(TestComponent).FullName!, exception.Message);
            Assert.Contains("Docs/dialog-responses.md", exception.Message);
            Assert.IsType<InvalidOperationException>(exception.InnerException);
            Assert.Equal(0, helper.TypedInvocationCount);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedComponent_SelectorSuccessOrAmbiguityIsPreserved(bool ambiguous)
    {
        var helper = new RecordingViewComponentHelper();
        var selector = A.Fake<IViewComponentSelector>();
        var original = new InvalidOperationException("Ambiguous Widget components");
        if (ambiguous) A.CallTo(() => selector.SelectComponent("Widget")).Throws(original);
        else A.CallTo(() => selector.SelectComponent("Widget")).Returns(new ViewComponentDescriptor());
        using var context = CreateContext(services =>
        {
            services.AddSingleton<IViewComponentHelper>(helper);
            services.AddSingleton(selector);
        });
        var action = new ViewComponentByNameStreamAction("update", "panel", "Widget");

        if (ambiguous)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => action.RenderAsync(CreateViewContext(context)));
            Assert.Same(original, exception.InnerException);
            Assert.Contains(original.Message, exception.Message);
            Assert.Contains("Docs/dialog-responses.md", exception.Message);
            Assert.Equal(0, helper.NamedInvocationCount);
        }
        else
        {
            Assert.Contains("<component/>", await action.RenderAsync(CreateViewContext(context)));
            Assert.Equal(1, helper.NamedInvocationCount);
        }
    }

    [Fact]
    public async Task ComponentContent_RejectsInvalidInputsAndCancelledWork()
    {
        using var context = CreateContext(services => services.AddSingleton<IViewComponentHelper>(new RecordingViewComponentHelper()));
        var viewContext = CreateViewContext(context);
        await Assert.ThrowsAsync<ArgumentNullException>(() => ViewComponentStreamHelper.RenderComponentContentAsync(null!, "Widget", null));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ViewComponentStreamHelper.RenderComponentContentAsync(viewContext, null!, null));
        await Assert.ThrowsAsync<ArgumentException>(() => ViewComponentStreamHelper.RenderComponentContentAsync(viewContext, 42, null));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ViewComponentStreamHelper.RenderComponentContentAsync(viewContext, "Widget", null, new CancellationToken(true)));
    }

    [Fact]
    public async Task DialogCommand_RequiresCorrelationAndObservesCancellation()
    {
        using var context = CreateContext();
        var viewContext = CreateViewContext(context);
        var command = RazorWireDialogStreamAction.Close();
        Assert.Throws<ArgumentNullException>(() => RazorWireDialogStreamAction.Open(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => command.RenderAsync(null!));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.RenderAsync(viewContext, new CancellationToken(true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => command.RenderAsync(viewContext));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RazorWireStreamRendering.RenderActionAsync(command, viewContext, null, RazorWireDialogPhase.Origin, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => command.RenderCorrelatedAsync(null!, Metadata));
        await Assert.ThrowsAsync<ArgumentNullException>(() => command.RenderCorrelatedAsync(viewContext, null!));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.RenderCorrelatedAsync(viewContext, Metadata, new CancellationToken(true)));
    }

    [Fact]
    public async Task DialogPayload_InvalidBodyKindFailsWithoutMarkup()
    {
        using var context = CreateContext();
        var command = RazorWireDialogStreamAction.Open(new RazorWireDialogPayload("Title", (RazorWireDialogBodyKind)99));
        await Assert.ThrowsAsync<InvalidOperationException>(() => command.RenderCorrelatedAsync(CreateViewContext(context), Metadata));
    }

    [Fact]
    public void StreamRendering_RejectsDuplicateCommandsAndUnknownPhases()
    {
        Assert.Throws<InvalidOperationException>(() => RazorWireStreamRendering.FindDialogCommandIndex([RazorWireDialogStreamAction.Close(), RazorWireDialogStreamAction.Close()]));
        Assert.Throws<InvalidOperationException>(() => RazorWireStreamRendering.GetPhase(1, 0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => Metadata.ToHtmlAttributes((RazorWireDialogPhase)99));
        Assert.Equal(RazorWireDialogPhase.Origin, RazorWireStreamRendering.GetPhase(0, -1, null));
        Assert.Equal(RazorWireDialogPhase.Origin, RazorWireStreamRendering.GetPhase(0, 1, RazorWireDialogCommand.Open));
        Assert.Equal(RazorWireDialogPhase.New, RazorWireStreamRendering.GetPhase(1, 0, RazorWireDialogCommand.Open));
        Assert.Equal(RazorWireDialogPhase.Closed, RazorWireStreamRendering.GetPhase(1, 0, RazorWireDialogCommand.Close));
    }

    [Fact]
    public void TargetedMarkup_NullBodyAndNoTemplateRemainDistinct()
    {
        Assert.Equal("<turbo-stream action=\"update\" target=\"panel\"><template></template></turbo-stream>", RazorWireStreamMarkup.RenderTargeted("update", "panel", null, true));
        Assert.Equal("<turbo-stream action=\"remove\" target=\"panel\"></turbo-stream>", RazorWireStreamMarkup.RenderTargeted("remove", "panel", null, false));
    }

    [Fact]
    public async Task TargetedPartial_ExposesTargetAndIncludesSearchedLocationsOnFailure()
    {
        var engine = A.Fake<ICompositeViewEngine>();
        A.CallTo(() => engine.FindView(A<ActionContext>._, "Missing", false)).Returns(ViewEngineResult.NotFound("Missing", []));
        A.CallTo(() => engine.GetView(null, "Missing", false)).Returns(ViewEngineResult.NotFound("Missing", ["/Views/Missing.cshtml"]));
        using var context = CreateContext(services => services.AddSingleton(engine));
        IRazorWireTargetedStreamAction action = new PartialViewStreamAction("update", "panel", "Missing");
        Assert.Equal("panel", action.Target);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => action.RenderAsync(CreateViewContext(context)));
        Assert.Contains("/Views/Missing.cshtml", exception.Message);
        Assert.Contains("Docs/dialog-responses.md", exception.Message);
    }

    private static RazorWireTestContext CreateContext(Action<IServiceCollection>? configure = null) =>
        RazorWireTestContext.CreateActionContext(services =>
        {
            var factory = A.Fake<ITempDataDictionaryFactory>();
            A.CallTo(() => factory.GetTempData(A<HttpContext>._)).Returns(A.Fake<ITempDataDictionary>());
            services.AddSingleton(factory);
            configure?.Invoke(services);
        });

    private static ViewContext CreateViewContext(RazorWireTestContext context) => new(
        context.ActionContext, A.Fake<IView>(),
        new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()),
        A.Fake<ITempDataDictionary>(), TextWriter.Null, new HtmlHelperOptions());
}
