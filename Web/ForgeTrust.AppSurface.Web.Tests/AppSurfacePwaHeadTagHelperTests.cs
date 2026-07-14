using ForgeTrust.AppSurface.Web.TagHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ForgeTrust.AppSurface.Web.Tests;

public sealed class AppSurfacePwaHeadTagHelperTests
{
    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new AppSurfacePwaHeadTagHelper(null!, new PwaOptions()));
        Assert.Throws<ArgumentNullException>(() => new AppSurfacePwaHeadTagHelper(new StubFileVersionProvider(), null!));
    }

    [Fact]
    public void Process_WhenDisabled_EmitsNoHeadTags()
    {
        var helper = new AppSurfacePwaHeadTagHelper(new StubFileVersionProvider(), new PwaOptions())
        {
            ViewContext = CreateViewContext()
        };
        var output = CreateOutput();

        helper.Process(CreateContext(), output);

        Assert.Null(output.TagName);
        Assert.Equal(string.Empty, output.Content.GetContent());
    }

    [Fact]
    public void Process_WhenEnabled_EmitsManifestThemeIconsAndServiceWorkerMetadata()
    {
        var options = PwaOptionsTests.CreateValidOptions();
        options.Offline.Enabled = true;
        options.Offline.OfflineFallbackPath = "/offline.html";
        var fileVersionProvider = new StubFileVersionProvider();
        var helper = new AppSurfacePwaHeadTagHelper(fileVersionProvider, options)
        {
            ViewContext = CreateViewContext("/tenant")
        };
        var output = CreateOutput();

        helper.Process(CreateContext(), output);

        var html = output.Content.GetContent();
        Assert.Contains("<link rel=\"manifest\" href=\"/tenant/manifest.webmanifest\"", html, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"theme-color\" content=\"#2563eb\"", html, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"application-name\" content=\"Field Notes\"", html, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"apple-mobile-web-app-capable\" content=\"yes\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/tenant/icons/app-192.png?v=asset\"", html, StringComparison.Ordinal);
        Assert.Contains("sizes=\"512x512\"", html, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"appsurface:pwa-service-worker\" content=\"/tenant/service-worker.js\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("pwa/register.js", html, StringComparison.Ordinal);
        Assert.Contains("/tenant/icons/app-192.png", fileVersionProvider.Paths);
        Assert.Contains("/tenant/icons/app-512.png", fileVersionProvider.Paths);
    }

    [Fact]
    public void Process_WhenPushOnly_EmitsWorkerHelperWithoutInstallMetadata()
    {
        var options = new PwaOptions();
        options.Scope = "/workspace/";
        options.Push.Enabled = true;
        var helper = new AppSurfacePwaHeadTagHelper(new StubFileVersionProvider(), options)
        {
            ViewContext = CreateViewContext("/tenant")
        };
        var output = CreateOutput();

        helper.Process(CreateContext(), output);

        var html = output.Content.GetContent();
        Assert.DoesNotContain("rel=\"manifest\"", html, StringComparison.Ordinal);
        Assert.Contains("content=\"/tenant/service-worker.js\"", html, StringComparison.Ordinal);
        Assert.Contains("content=\"/tenant/workspace/\"", html, StringComparison.Ordinal);
        Assert.Contains("src=\"/tenant/_appsurface/pwa/register.js?v=", html, StringComparison.Ordinal);
        Assert.Contains("data-appsurface-pwa-worker=\"/tenant/service-worker.js\"", html, StringComparison.Ordinal);
        Assert.Contains("data-appsurface-pwa-scope=\"/tenant/workspace/\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Process_PushMetadata_EncodesHostileValues()
    {
        var options = new PwaOptions();
        options.Push.Enabled = true;
        options.Worker.ServiceWorkerPath = "/worker\"onload=\"bad.js";
        options.Scope = "/scope\"data-bad=\"x/";
        var helper = new AppSurfacePwaHeadTagHelper(new StubFileVersionProvider(), options)
        {
            ViewContext = CreateViewContext()
        };
        var output = CreateOutput();

        helper.Process(CreateContext(), output);

        var html = output.Content.GetContent();
        Assert.Contains("/worker&quot;onload=&quot;bad.js", html, StringComparison.Ordinal);
        Assert.Contains("/scope&quot;data-bad=&quot;x/", html, StringComparison.Ordinal);
        Assert.DoesNotContain("\"onload=\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Process_WhenIconSourceIsUnsafe_EmitsEncodedFallbackWithoutVersioning()
    {
        var options = PwaOptionsTests.CreateValidOptions();
        options.Icons.Clear();
        options.Icons.Add(
            new PwaIcon
            {
                Source = "https://cdn.example.test/icon 192.png?token=<unsafe>",
                Sizes = "192x192",
                Type = "image/png"
            });
        var helper = new AppSurfacePwaHeadTagHelper(new StubFileVersionProvider(), options)
        {
            ViewContext = CreateViewContext("/tenant")
        };
        var output = CreateOutput();

        helper.Process(CreateContext(), output);

        var html = output.Content.GetContent();
        Assert.Contains(
            "href=\"https://cdn.example.test/icon 192.png?token=&lt;unsafe&gt;\"",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain("?v=asset", html, StringComparison.Ordinal);
    }

    private static TagHelperContext CreateContext()
    {
        return new TagHelperContext(
            new TagHelperAttributeList(),
            new Dictionary<object, object>(),
            Guid.NewGuid().ToString("N"));
    }

    private static TagHelperOutput CreateOutput()
    {
        return new TagHelperOutput(
            "appsurface:pwa-head",
            new TagHelperAttributeList(),
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));
    }

    private static ViewContext CreateViewContext(string pathBase = "")
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.PathBase = pathBase;
        return new ViewContext { HttpContext = httpContext };
    }

    private sealed class StubFileVersionProvider : IFileVersionProvider
    {
        public IList<string> Paths { get; } = [];

        public string AddFileVersionToPath(PathString requestPathBase, string path)
        {
            Paths.Add(path);
            return path + "?v=asset";
        }
    }
}
