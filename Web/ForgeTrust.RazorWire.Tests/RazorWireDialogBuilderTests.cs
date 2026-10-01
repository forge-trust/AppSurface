using System.Text.RegularExpressions;
using FakeItEasy;
using ForgeTrust.RazorWire.Bridge;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.RazorWire.Tests;

public sealed class RazorWireDialogBuilderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DialogOverloads_ChainAndRejectSecondOpenOrReplaceWithoutOpen(int kind)
    {
        var builder = new RazorWireStreamBuilder();
        Assert.False(builder.HasActiveDialog);
        var error = Assert.Throws<InvalidOperationException>(() => Replace(builder, kind, "Replacement"));
        Assert.Contains("HasActiveDialog", error.Message);
        Assert.Contains("Docs/dialog-responses.md", error.Message);
        Assert.Same(builder, Open(builder, kind, "Initial"));
        Assert.True(builder.HasActiveDialog);
        error = Assert.Throws<InvalidOperationException>(() => Open(builder, kind, "Duplicate"));
        Assert.Contains("ReplaceDialog", error.Message);
        Assert.True(builder.HasActiveDialog);
        Assert.Same(builder, Replace(builder, kind, "Replacement"));
        Assert.True(builder.HasActiveDialog);
        Assert.Same(builder, builder.CloseDialog());
        Assert.False(builder.HasActiveDialog);
        Assert.Throws<InvalidOperationException>(() => Replace(builder, kind, "After close"));
        Assert.Same(builder, Open(builder, kind, "Reopened"));
        Assert.True(builder.HasActiveDialog);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "")]
    [InlineData(2, " ")]
    [InlineData(3, "\t")]
    public void DialogOverloads_InvalidTitleLeavesExistingSlotUnchanged(int kind, string? title)
    {
        var builder = new RazorWireStreamBuilder();
        Assert.ThrowsAny<ArgumentException>(() => Open(builder, kind, title!));
        Assert.False(builder.HasActiveDialog);
        Open(builder, kind, "Initial");
        Assert.ThrowsAny<ArgumentException>(() => Replace(builder, kind, title!));
        Assert.True(builder.HasActiveDialog);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void PartialAndNamedComponent_RejectInvalidRendererNamesAtCallTime(string? name)
    {
        var builder = new RazorWireStreamBuilder();
        Assert.ThrowsAny<ArgumentException>(() => builder.OpenDialogPartial("Title", name!));
        Assert.ThrowsAny<ArgumentException>(() => builder.OpenDialogComponent("Title", name!));
        Assert.False(builder.HasActiveDialog);
        builder.OpenDialog("Original", null);
        Assert.ThrowsAny<ArgumentException>(() => builder.ReplaceDialogPartial("Title", name!));
        Assert.ThrowsAny<ArgumentException>(() => builder.ReplaceDialogComponent("Title", name!));
        Assert.True(builder.HasActiveDialog);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_RejectsOpenAndCloseWithRequestAwareRecovery(bool close)
    {
        var builder = close ? new RazorWireStreamBuilder().CloseDialog() : new RazorWireStreamBuilder().OpenDialog("Title", null);
        var error = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains("BuildResult()", error.Message);
        Assert.Contains("RenderAsync(viewContext)", error.Message);
        Assert.Contains("Docs/dialog-responses.md", error.Message);
    }

    [Theory]
    [InlineData("open-replace", true)]
    [InlineData("open-close", false)]
    [InlineData("close-open", true)]
    [InlineData("close-close", false)]
    [InlineData("open-close-open-replace", true)]
    public async Task DialogTransitions_EmitOneFinalCommandAtOriginalSlot(string sequence, bool active)
    {
        using var context = CreateContext();
        var builder = new RazorWireStreamBuilder().Update("before", "Before");
        var first = true;
        foreach (var operation in sequence.Split('-'))
        {
            switch (operation)
            {
                case "open": builder.OpenDialog("Open title", "Original body"); break;
                case "replace": builder.ReplaceDialog("Final title", "Final body"); break;
                case "close": builder.CloseDialog(); break;
            }
            if (first)
            {
                builder.Update("between", "Between");
                first = false;
            }
        }
        builder.Update("after", "After");
        Assert.Equal(active, builder.HasActiveDialog);
        await builder.BuildResult().ExecuteResultAsync(context.ActionContext);
        var html = await RazorWireTestContext.ReadBodyAsync(context.ActionContext.HttpContext.Response);
        Assert.Single(Regex.Matches(html, "action=\"rw-dialog\""));
        Assert.Contains($"dialog-command=\"{(active ? "open" : "close")}\"", html);
        var before = html.IndexOf("target=\"before\"", StringComparison.Ordinal);
        var slot = html.IndexOf("action=\"rw-dialog\"", StringComparison.Ordinal);
        var between = html.IndexOf("target=\"between\"", StringComparison.Ordinal);
        var after = html.IndexOf("target=\"after\"", StringComparison.Ordinal);
        Assert.True(before >= 0 && before < slot && slot < between && between < after);
        if (sequence.EndsWith("replace", StringComparison.Ordinal))
        {
            Assert.Contains("Final body", html);
            Assert.DoesNotContain("Original body", html);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("<strong>A&B</strong>")]
    public async Task PlainDialog_EncodesTitleAndMessageAndAllowsEmptyBody(string? message)
    {
        using var context = CreateContext();
        var builder = new RazorWireStreamBuilder().OpenDialog("<Title & \"quoted\">", message);
        var html = await builder.RenderAsync(CreateViewContext(context));
        Assert.Contains("dialog-title=\"&lt;Title &amp; &quot;quoted&quot;&gt;\"", html);
        Assert.Equal(
            context.ActionContext.HttpContext.Request.Headers["X-RazorWire-Request"].ToString(),
            context.ActionContext.HttpContext.Response.Headers["X-RazorWire-Request"].ToString());
        Assert.Contains(message is null ? "<template></template>" : "<template>&lt;strong&gt;A&amp;B&lt;/strong&gt;</template>", html);
        Assert.DoesNotContain("<strong>", html);
        Assert.DoesNotContain("<Title", html);
    }

    [Fact]
    public async Task RenderAsync_WhenResponseStarted_DoesNotEchoRequestHeader()
    {
        using var context = CreateContext();
        var responseFeature = A.Fake<IHttpResponseFeature>();
        var readOnlyHeaders = new HeaderDictionary { IsReadOnly = true };
        A.CallTo(() => responseFeature.Headers).Returns(readOnlyHeaders);
        A.CallTo(() => responseFeature.HasStarted).Returns(true);
        context.ActionContext.HttpContext.Features.Set<IHttpResponseFeature>(responseFeature);

        var builder = new RazorWireStreamBuilder().OpenDialog("Title", "Body");
        var html = await builder.RenderAsync(CreateViewContext(context));

        Assert.Contains("dialog-command=\"open\"", html);
        Assert.True(readOnlyHeaders.IsReadOnly);
        Assert.True(responseFeature.HasStarted);
        Assert.Empty(readOnlyHeaders);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task OverwrittenRenderers_AreNeverResolvedOrInvoked(int kind, bool close)
    {
        // No view engine or component helper is registered: resolving an overwritten
        // renderer would fail instead of silently increasing a test-only call count.
        using var context = CreateContext();
        var builder = Open(new RazorWireStreamBuilder(), kind, "Discarded");
        if (close) builder.CloseDialog();
        else builder.ReplaceDialog("Retained", "Visible body");
        var html = await builder.RenderAsync(CreateViewContext(context));
        Assert.DoesNotContain("Discarded", html);
        Assert.Contains(close ? "dialog-command=\"close\"" : "Visible body", html);
    }

    [Fact]
    public async Task BuildResult_SnapshotsPayloadAndPageActionsBeforeBuilderChanges()
    {
        using var context = CreateContext();
        var builder = new RazorWireStreamBuilder().OpenDialog("Captured", "Original").Update("page", "Before mutation");
        var result = builder.BuildResult();
        builder.ReplaceDialog("Later", "Changed").CloseDialog().Update("extra", "Uncaptured");
        await result.ExecuteResultAsync(context.ActionContext);
        var html = await RazorWireTestContext.ReadBodyAsync(context.ActionContext.HttpContext.Response);
        Assert.Contains("dialog-title=\"Captured\"", html);
        Assert.Contains("Original", html);
        Assert.Contains("Before mutation", html);
        Assert.DoesNotContain("Later", html);
        Assert.DoesNotContain("Uncaptured", html);
        Assert.DoesNotContain("dialog-command=\"close\"", html);
    }

    [Fact]
    public async Task RenderAsync_SnapshotsBeforeAwaitingPartialRenderer()
    {
        var view = new GatedView();
        var engine = A.Fake<ICompositeViewEngine>();
        A.CallTo(() => engine.FindView(A<ViewContext>._, "_Dialog", false)).Returns(ViewEngineResult.Found("_Dialog", view));
        using var context = CreateContext(services => services.AddSingleton(engine));
        var builder = new RazorWireStreamBuilder().OpenDialogPartial("Captured", "_Dialog");
        var rendering = builder.RenderAsync(CreateViewContext(context));
        await view.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        builder.ReplaceDialog("Later", "Changed").Update("extra", "Uncaptured");
        view.Release.SetResult();
        var html = await rendering;
        Assert.Contains("dialog-title=\"Captured\"", html);
        Assert.Contains("Rendered partial", html);
        Assert.DoesNotContain("Later", html);
        Assert.DoesNotContain("Uncaptured", html);
    }

    private static RazorWireStreamBuilder Open(RazorWireStreamBuilder builder, int kind, string title) => kind switch
    {
        0 => builder.OpenDialog(title, "Body"),
        1 => builder.OpenDialogPartial(title, "_Dialog"),
        2 => builder.OpenDialogComponent<TestComponent>(title, new { Value = 1 }),
        3 => builder.OpenDialogComponent(title, "Widget", new { Value = 1 }),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static RazorWireStreamBuilder Replace(RazorWireStreamBuilder builder, int kind, string title) => kind switch
    {
        0 => builder.ReplaceDialog(title, "Replacement body"),
        1 => builder.ReplaceDialogPartial(title, "_Replacement"),
        2 => builder.ReplaceDialogComponent<TestComponent>(title, new { Value = 2 }),
        3 => builder.ReplaceDialogComponent(title, "ReplacementWidget", new { Value = 2 }),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static RazorWireTestContext CreateContext(Action<ServiceCollection>? configure = null)
    {
        var context = RazorWireTestContext.CreateActionContext(services =>
        {
            var factory = A.Fake<ITempDataDictionaryFactory>();
            A.CallTo(() => factory.GetTempData(A<HttpContext>._)).Returns(A.Fake<ITempDataDictionary>());
            services.AddSingleton(factory);
            configure?.Invoke(services);
        });
        context.ActionContext.HttpContext.Request.Headers["X-RazorWire-Request"] = Guid.NewGuid().ToString("D");
        context.ActionContext.HttpContext.Request.Headers["X-RazorWire-Order"] = "1";
        return context;
    }

    private static ViewContext CreateViewContext(RazorWireTestContext context) => new(
        context.ActionContext,
        A.Fake<IView>(),
        new ViewDataDictionary(new EmptyModelMetadataProvider(), context.ActionContext.ModelState),
        A.Fake<ITempDataDictionary>(),
        TextWriter.Null,
        new HtmlHelperOptions());

    private sealed class GatedView : IView
    {
        public string Path => "_Dialog";
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task RenderAsync(ViewContext context)
        {
            Entered.SetResult();
            await Release.Task;
            await context.Writer.WriteAsync("Rendered partial");
        }
    }
}
