using System.Text.Json;
using ForgeTrust.AppSurface.Docs;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Collection(AppSurfaceDocsIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AppSurfaceDocsHtmlSanitizerPlaywrightTests
{
    private readonly AppSurfaceDocsPlaywrightFixture _fixture;

    public AppSurfaceDocsHtmlSanitizerPlaywrightTests(AppSurfaceDocsPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("text/html", "title")]
    [InlineData("text/html", "style")]
    [InlineData("application/xhtml+xml", "title")]
    [InlineData("application/xhtml+xml", "style")]
    public async Task Sanitize_LeavesNoBrowserDom_ForAnnotationXmlAttributeBreakoutPayload(
        string encoding,
        string rcdataElement)
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceDocs();
        using var serviceProvider = services.BuildServiceProvider();
        var sanitizer = serviceProvider.GetRequiredService<IAppSurfaceDocsHtmlSanitizer>();
        var payload = $"<math><annotation-xml encoding=\"{encoding}\"><{rcdataElement}><a encoding=\"</{rcdataElement}><img src=x onerror=alert()>\"></annotation-xml></math>";
        var sanitized = sanitizer.Sanitize(payload);

        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var dom = await page.EvaluateAsync<JsonElement>(
            """
            html => {
              const container = document.createElement('div');
              container.innerHTML = html;
              const elements = [...container.querySelectorAll('*')];
              return {
                innerHtml: container.innerHTML,
                childNodeCount: container.childNodes.length,
                text: container.textContent ?? '',
                imageCount: container.querySelectorAll('img').length,
                scriptCount: container.querySelectorAll('script').length,
                eventBearingNodeCount: elements.filter(element =>
                  [...element.attributes].some(attribute => attribute.name.toLowerCase().startsWith('on'))).length
              };
            }
            """,
            sanitized);

        Assert.Equal(string.Empty, dom.GetProperty("innerHtml").GetString());
        Assert.Equal(0, dom.GetProperty("childNodeCount").GetInt32());
        Assert.Equal(string.Empty, dom.GetProperty("text").GetString());
        Assert.Equal(0, dom.GetProperty("imageCount").GetInt32());
        Assert.Equal(0, dom.GetProperty("scriptCount").GetInt32());
        Assert.Equal(0, dom.GetProperty("eventBearingNodeCount").GetInt32());
    }
}
