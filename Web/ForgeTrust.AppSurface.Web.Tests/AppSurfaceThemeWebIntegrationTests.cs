using ForgeTrust.AppSurface.Theming;
using ForgeTrust.AppSurface.Web.TagHelpers;
using ForgeTrust.AppSurface.Web.Theming;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Web.Tests;

public sealed class AppSurfaceThemeWebIntegrationTests
{
    [Fact]
    public void Serializer_SystemEmitsLightDefaultsAndDarkMediaBranch()
    {
        var document = AppSurfaceThemeDocumentSerializer.Serialize(CreateResolution(AppSurfaceThemeMode.System));

        Assert.Equal(
            "data-as-theme=\"appsurface\" data-as-theme-mode=\"system\"",
            document.RootAttributes);
        Assert.Equal("color-scheme: light dark;", document.RootStyle);
        Assert.Contains("<meta name=\"color-scheme\" content=\"light dark\" />\n<style data-as-theme-critical>\n", document.HeadContent);
        Assert.Contains("--as-canvas: #f8fafc;", document.HeadContent, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-color-scheme: dark)", document.HeadContent, StringComparison.Ordinal);
        Assert.Contains("--as-canvas: #0f172a;", document.HeadContent, StringComparison.Ordinal);
        Assert.Contains(
            "[data-as-theme] [data-rw-form-error-generated=\"true\"]",
            document.HeadContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "var(--rw-form-error-border, var(--as-danger))",
            document.HeadContent,
            StringComparison.Ordinal);
        Assert.DoesNotContain('\r', document.HeadContent);
        Assert.DoesNotContain("nonce", document.HeadContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Serializer_FixedModesEmitOnlySelectedBranch()
    {
        var light = AppSurfaceThemeDocumentSerializer.Serialize(CreateResolution(AppSurfaceThemeMode.Light));
        var dark = AppSurfaceThemeDocumentSerializer.Serialize(CreateResolution(AppSurfaceThemeMode.Dark));

        Assert.Equal("color-scheme: light;", light.RootStyle);
        Assert.DoesNotContain("@media (prefers-color-scheme", light.HeadContent, StringComparison.Ordinal);
        Assert.Contains("--as-canvas: #f8fafc;", light.HeadContent, StringComparison.Ordinal);
        Assert.DoesNotContain("--as-canvas: #0f172a;", light.HeadContent, StringComparison.Ordinal);

        Assert.Equal("color-scheme: dark;", dark.RootStyle);
        Assert.DoesNotContain("@media (prefers-color-scheme", dark.HeadContent, StringComparison.Ordinal);
        Assert.Contains("--as-canvas: #0f172a;", dark.HeadContent, StringComparison.Ordinal);
        Assert.DoesNotContain("--as-canvas: #f8fafc;", dark.HeadContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Serializer_ShouldFailClosedForUnsafeResolutions()
    {
        var pair = AppSurfaceThemePair.AppSurface();
        var malformedRoles = new AppSurfaceThemeRoles(
            "not-a-color", pair.Light.Surface, pair.Light.RaisedSurface, pair.Light.Text, pair.Light.MutedText,
            pair.Light.Border, pair.Light.Accent, pair.Light.AccentStrong, pair.Light.Link, pair.Light.VisitedLink,
            pair.Light.Danger, pair.Light.Focus);
        var invalidMode = new AppSurfaceThemeResolution(pair.Id, (AppSurfaceThemeMode)99, pair.Light, pair.Dark);
        var emptyId = new AppSurfaceThemeResolution(default, AppSurfaceThemeMode.System, pair.Light, pair.Dark);
        var invalidRoles = new AppSurfaceThemeResolution(pair.Id, AppSurfaceThemeMode.System, malformedRoles, pair.Dark);
        var lowContrastRoles = new AppSurfaceThemeRoles(
            pair.Light.Canvas, pair.Light.Surface, pair.Light.RaisedSurface, pair.Light.Canvas, pair.Light.MutedText,
            pair.Light.Border, pair.Light.Accent, pair.Light.AccentStrong, pair.Light.Link, pair.Light.VisitedLink,
            pair.Light.Danger, pair.Light.Focus);
        var invalidContrast = new AppSurfaceThemeResolution(pair.Id, AppSurfaceThemeMode.System, lowContrastRoles, pair.Dark);

        Assert.False(AppSurfaceThemeDocumentSerializer.TrySerialize(null, out var nullDocument));
        Assert.Same(AppSurfaceThemeDocument.Empty, nullDocument);
        Assert.False(AppSurfaceThemeDocumentSerializer.TrySerialize(invalidMode, out var invalidModeDocument));
        Assert.Same(AppSurfaceThemeDocument.Empty, invalidModeDocument);
        Assert.False(AppSurfaceThemeDocumentSerializer.TrySerialize(emptyId, out var emptyIdDocument));
        Assert.Same(AppSurfaceThemeDocument.Empty, emptyIdDocument);
        Assert.False(AppSurfaceThemeDocumentSerializer.TrySerialize(invalidRoles, out var invalidRolesDocument));
        Assert.Same(AppSurfaceThemeDocument.Empty, invalidRolesDocument);
        Assert.False(AppSurfaceThemeDocumentSerializer.TrySerialize(invalidContrast, out var invalidContrastDocument));
        Assert.Same(AppSurfaceThemeDocument.Empty, invalidContrastDocument);
    }

    [Fact]
    public void Registration_RequiresNeutralThemeAndAddsWebDocumentProvider()
    {
        var services = new ServiceCollection();
        var returned = services
            .AddAppSurfaceTheming(options => options.Pairs.Add(AppSurfaceThemePair.AppSurface()))
            .AddAppSurfaceWebTheming();

        Assert.Same(services, returned);
        using var provider = services.BuildServiceProvider();

        var documentProvider = provider.GetRequiredService<IAppSurfaceThemeDocumentProvider>();
        Assert.True(documentProvider.GetDocument().IsRenderable);
    }

    [Fact]
    public void Registration_ShouldFailWhenTheNeutralResolverIsNotRegistered()
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceWebTheming();
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IAppSurfaceThemeDocumentProvider>());

        Assert.Contains(nameof(IAppSurfaceThemeResolver), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentProvider_ShouldResolveAndSerializeOnce()
    {
        var resolver = new CountingResolver(CreateResolution(AppSurfaceThemeMode.System));
        var provider = new AppSurfaceThemeDocumentProvider(resolver);

        var first = provider.GetDocument();
        var second = provider.GetDocument();

        Assert.Equal(1, resolver.ResolveCalls);
        Assert.Same(first, second);
    }

    [Fact]
    public void RootTagHelper_PreservesAttributesAndAddsSafeThemeMetadata()
    {
        var attributes = new TagHelperAttributeList
        {
            new("appsurface-theme-root", true),
            new("class", "shell"),
            new("data-as-theme", "consumer-value")
        };
        var output = CreateOutput("html", attributes);
        var helper = new AppSurfaceThemeRootTagHelper(
            new AppSurfaceThemeDocumentProvider(new StubResolver(CreateResolution(AppSurfaceThemeMode.System))));

        helper.Process(CreateContext(attributes), output);

        Assert.Equal("shell", output.Attributes["class"]?.Value);
        Assert.Equal("appsurface", output.Attributes["data-as-theme"]?.Value);
        Assert.Equal("system", output.Attributes["data-as-theme-mode"]?.Value);
        Assert.Equal("color-scheme: light dark;", output.Attributes["style"]?.Value);
        Assert.NotNull(output.Attributes["appsurface-theme-root"]);
    }

    [Fact]
    public void RootTagHelper_PreservesExistingStyleAndAppendsColorScheme()
    {
        var attributes = new TagHelperAttributeList
        {
            new("appsurface-theme-root", true),
            new("style", "background: var(--custom);"),
            new("class", "shell")
        };
        var output = CreateOutput("html", attributes);
        var helper = new AppSurfaceThemeRootTagHelper(
            new AppSurfaceThemeDocumentProvider(new StubResolver(CreateResolution(AppSurfaceThemeMode.System))));

        helper.Process(CreateContext(attributes), output);

        Assert.Equal("background: var(--custom); color-scheme: light dark;", output.Attributes["style"]?.Value);
        Assert.Equal("appsurface", output.Attributes["data-as-theme"]?.Value);
        Assert.Equal("system", output.Attributes["data-as-theme-mode"]?.Value);
        Assert.Equal("shell", output.Attributes["class"]?.Value);
    }

    [Fact]
    public void RootTagHelper_ReportsExistingColorSchemeWithoutOverwritingIt()
    {
        var attributes = new TagHelperAttributeList
        {
            new("appsurface-theme-root", true),
            new("style", "color-scheme: dark;")
        };
        var output = CreateOutput("html", attributes);
        var helper = new AppSurfaceThemeRootTagHelper(
            new AppSurfaceThemeDocumentProvider(new StubResolver(CreateResolution(AppSurfaceThemeMode.Light))));

        helper.Process(CreateContext(attributes), output);

        Assert.Equal("color-scheme: dark;", output.Attributes["style"]?.Value);
        Assert.Equal("true", output.Attributes["data-as-theme-color-scheme-conflict"]?.Value);
    }

    [Fact]
    public void RootTagHelper_ShouldNotTreatAnUnrelatedCustomPropertyAsAColorSchemeDeclaration()
    {
        var attributes = new TagHelperAttributeList
        {
            new("appsurface-theme-root", true),
            new("style", "--consumer-color-scheme-token: dark;")
        };
        var output = CreateOutput("html", attributes);
        var helper = new AppSurfaceThemeRootTagHelper(
            new AppSurfaceThemeDocumentProvider(new StubResolver(CreateResolution(AppSurfaceThemeMode.Light))));

        helper.Process(CreateContext(attributes), output);

        Assert.Equal(
            "--consumer-color-scheme-token: dark; color-scheme: light;",
            output.Attributes["style"]?.Value);
        Assert.Null(output.Attributes["data-as-theme-color-scheme-conflict"]);
    }

    [Fact]
    public void HeadTagHelper_AddsNonceOnlyToLiveStyle()
    {
        var helper = new AppSurfaceThemeHeadTagHelper(
            new AppSurfaceThemeDocumentProvider(new StubResolver(CreateResolution(AppSurfaceThemeMode.Light))))
        {
            Nonce = "nonce$1\"<&"
        };
        var output = CreateOutput("appsurface-theme-head");

        helper.Process(CreateContext(), output);

        var html = output.Content.GetContent();
        Assert.Null(output.TagName);
        Assert.Contains("<meta name=\"color-scheme\" content=\"light\" />", html, StringComparison.Ordinal);
        Assert.Contains("<style data-as-theme-critical nonce=\"nonce$1&quot;&lt;&amp;\">", html, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(html, "nonce="));
        Assert.DoesNotContain("<meta name=\"color-scheme\" nonce=", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("display:none", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThemeCspNonce_ShouldReadTheHostOwnedRequestValue()
    {
        var context = new DefaultHttpContext();
        context.Items[AppSurfaceThemeCspNonce.HttpContextItemKey] = "nonce-value";

        Assert.Equal("nonce-value", AppSurfaceThemeCspNonce.Get(context));
    }

    [Fact]
    public void TagHelpers_ShouldLeaveTheDocumentUntouchedWhenTheSnapshotIsUnsafe()
    {
        var rootAttributes = new TagHelperAttributeList
        {
            new("appsurface-theme-root", true),
            new("class", "shell"),
            new("style", "background: white;")
        };
        var rootOutput = CreateOutput("html", rootAttributes);
        var rootHelper = new AppSurfaceThemeRootTagHelper(new EmptyDocumentProvider());
        var headOutput = CreateOutput("appsurface-theme-head");
        var headHelper = new AppSurfaceThemeHeadTagHelper(new EmptyDocumentProvider());

        rootHelper.Process(CreateContext(rootAttributes), rootOutput);
        headHelper.Process(CreateContext(), headOutput);

        Assert.Equal("shell", rootOutput.Attributes["class"]?.Value);
        Assert.Equal("background: white;", rootOutput.Attributes["style"]?.Value);
        Assert.Null(rootOutput.Attributes["data-as-theme"]);
        Assert.Equal(string.Empty, headOutput.Content.GetContent());
    }

    private static AppSurfaceThemeResolution CreateResolution(AppSurfaceThemeMode mode)
    {
        var pair = AppSurfaceThemePair.AppSurface();
        return new AppSurfaceThemeResolution(pair.Id, mode, pair.Light, pair.Dark);
    }

    private static TagHelperContext CreateContext(TagHelperAttributeList? attributes = null) =>
        new(
            attributes ?? new TagHelperAttributeList(),
            new Dictionary<object, object>(),
            Guid.NewGuid().ToString("N"));

    private static TagHelperOutput CreateOutput(
        string tagName,
        TagHelperAttributeList? attributes = null) =>
        new(
            tagName,
            attributes ?? new TagHelperAttributeList(),
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }

        return count;
    }

    private sealed class StubResolver(AppSurfaceThemeResolution resolution) : IAppSurfaceThemeResolver
    {
        public AppSurfaceThemeResolution ResolveDefault() => resolution;
    }

    private sealed class CountingResolver(AppSurfaceThemeResolution resolution) : IAppSurfaceThemeResolver
    {
        public int ResolveCalls { get; private set; }

        public AppSurfaceThemeResolution ResolveDefault()
        {
            ResolveCalls++;
            return resolution;
        }
    }

    private sealed class EmptyDocumentProvider : IAppSurfaceThemeDocumentProvider
    {
        public AppSurfaceThemeDocument GetDocument() => AppSurfaceThemeDocument.Empty;
    }
}
