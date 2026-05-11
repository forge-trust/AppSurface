using System.IO;
using System.Threading;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.RazorWire.Tests;

internal sealed class RazorWireTestContext : IDisposable
{
    private readonly ServiceProvider _serviceProvider;

    private RazorWireTestContext(ActionContext actionContext, ServiceProvider serviceProvider)
    {
        ActionContext = actionContext;
        _serviceProvider = serviceProvider;
    }

    public ActionContext ActionContext { get; }

    public static RazorWireTestContext CreateActionContext(
        Action<ServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        configureServices?.Invoke(services);

        var provider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = provider,
            Response =
            {
                Body = new MemoryStream()
            }
        };

        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor());

        return new RazorWireTestContext(actionContext, provider);
    }

    public static async Task<string> ReadBodyAsync(HttpResponse response)
    {
        response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
    }
}

internal sealed class RecordingViewComponentHelper : IViewComponentHelper, IViewContextAware
{
    private readonly IHtmlContent _content;
    private int _typedInvocationCount;
    private int _namedInvocationCount;

    public RecordingViewComponentHelper(string html = "<component/>")
    {
        _content = new HtmlString(html);
    }

    public int TypedInvocationCount => _typedInvocationCount;

    public int NamedInvocationCount => _namedInvocationCount;

    public object? LastIdentifier { get; private set; }

    public object? LastArguments { get; private set; }

    public ViewContext? ContextualizedViewContext { get; private set; }

    public void Contextualize(ViewContext viewContext)
    {
        ContextualizedViewContext = viewContext;
    }

    public Task<IHtmlContent> InvokeAsync(Type componentType, object? arguments)
    {
        Interlocked.Increment(ref _typedInvocationCount);
        LastIdentifier = componentType;
        LastArguments = arguments;
        return Task.FromResult(_content);
    }

    public Task<IHtmlContent> InvokeAsync(string name, object? arguments)
    {
        Interlocked.Increment(ref _namedInvocationCount);
        LastIdentifier = name;
        LastArguments = arguments;
        return Task.FromResult(_content);
    }
}

internal sealed class TestComponent : ViewComponent;
