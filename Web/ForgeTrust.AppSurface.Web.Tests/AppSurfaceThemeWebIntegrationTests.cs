using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Theming;
using ForgeTrust.AppSurface.Web.TagHelpers;
using ForgeTrust.AppSurface.Web.Theming;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeTrust.AppSurface.Web.Tests;

public sealed class AppSurfaceThemeWebIntegrationTests
{
    [Fact]
    public void Serializer_SystemEmitsLightDefaultsAndDarkMediaBranch()
    {
        var document = AppSurfaceThemeDocumentSerializer.Serialize(CreateResolution(AppSurfaceThemeMode.System));

        Assert.Equal(
            "data-as-theme=\"appsurface\" data-as-theme-mode=\"system\" data-as-theme-schema=\"1\"",
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
    public void Serializer_ShouldEmitEverySemanticRoleAndCompleteAccessibilityFallbacks()
    {
        var document = AppSurfaceThemeDocumentSerializer.Serialize(CreateResolution(AppSurfaceThemeMode.System));

        foreach (var (name, light, dark) in GetSemanticRoles())
        {
            Assert.Contains($"--as-{name}: {light};", document.HeadContent, StringComparison.Ordinal);
            Assert.Contains($"--as-{name}: {dark};", document.HeadContent, StringComparison.Ordinal);
        }

        foreach (var declaration in new[]
                 {
                     "border: 1px solid var(--rw-form-error-border, var(--as-danger));",
                     "background-color: var(--rw-form-error-bg, var(--as-raised-surface));",
                     "color: var(--rw-form-error-text, var(--as-text));",
                     "--rw-form-error-title: var(--as-text);",
                     "border-radius: var(--rw-form-error-radius, 0.25rem);",
                     "padding: var(--rw-form-error-spacing, 1rem);",
                     "outline: 2px solid var(--rw-form-error-focus, var(--as-focus));",
                     "outline-offset: 2px;",
                     "border-color: CanvasText;",
                     "background-color: Canvas;",
                     "color: CanvasText;",
                     "--rw-form-error-title: CanvasText;",
                     "outline-color: Highlight;"
                 })
        {
            Assert.Contains(declaration, document.HeadContent, StringComparison.Ordinal);
        }

        Assert.Contains(
            "[data-as-theme] [data-rw-form-error-generated=\"true\"]:focus-visible,",
            document.HeadContent,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[data-rw-form-error-generated=\"true\"] {\n  outline:",
            document.HeadContent,
            StringComparison.Ordinal);

        foreach (var declaration in new[]
                 {
                     "--as-canvas: Canvas;", "--as-surface: Canvas;", "--as-raised-surface: Canvas;",
                     "--as-text: CanvasText;", "--as-muted-text: GrayText;", "--as-border: GrayText;",
                     "--as-accent: Highlight;", "--as-accent-strong: Highlight;", "--as-link: LinkText;",
                     "--as-visited-link: VisitedText;", "--as-danger: CanvasText;", "--as-focus: Highlight;"
                 })
        {
            Assert.Contains(declaration, document.HeadContent, StringComparison.Ordinal);
        }
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
        Assert.Throws<ArgumentNullException>(() => AppSurfaceThemeDocumentSerializer.SerializePreference(null!));
        Assert.False(AppSurfaceThemeDocumentSerializer.TrySerialize(invalidMode, out var invalidModeDocument));
        Assert.Same(AppSurfaceThemeDocument.Empty, invalidModeDocument);
        Assert.Same(AppSurfaceThemeDocument.Empty, AppSurfaceThemeDocumentSerializer.Serialize(invalidMode));
        Assert.False(AppSurfaceThemeDocumentSerializer.TrySerialize(emptyId, out var emptyIdDocument));
        Assert.Same(AppSurfaceThemeDocument.Empty, emptyIdDocument);
        Assert.False(AppSurfaceThemeDocumentSerializer.TrySerialize(invalidRoles, out var invalidRolesDocument));
        Assert.Same(AppSurfaceThemeDocument.Empty, invalidRolesDocument);
        Assert.Same(AppSurfaceThemeDocument.Empty, AppSurfaceThemeDocumentSerializer.SerializePreference(invalidRoles));
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
        Assert.Equal(AppSurfaceThemeDocument.SchemaVersion, output.Attributes["data-as-theme-schema"]?.Value);
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
    public void Serializer_ShouldLeaveHeadContentUnchangedWithoutANonce()
    {
        var document = AppSurfaceThemeDocumentSerializer.Serialize(CreateResolution(AppSurfaceThemeMode.Light));

        Assert.Equal(document.HeadContent, AppSurfaceThemeDocumentSerializer.SerializeHeadContent(document));
        Assert.Equal(document.HeadContent, AppSurfaceThemeDocumentSerializer.SerializeHeadContent(document, string.Empty));
    }

    [Fact]
    public void Serializer_ShouldRejectANullDocumentWhenSerializingHeadContent()
    {
        Assert.Throws<ArgumentNullException>(() => AppSurfaceThemeDocumentSerializer.SerializeHeadContent(null!));
    }

    [Fact]
    public void HeadTagHelper_ShouldEmitOnePayloadPerMvcRequest()
    {
        var viewContext = CreateViewContext();
        var first = new AppSurfaceThemeHeadTagHelper(
            new AppSurfaceThemeDocumentProvider(new StubResolver(CreateResolution(AppSurfaceThemeMode.Light))))
        {
            ViewContext = viewContext
        };
        var second = new AppSurfaceThemeHeadTagHelper(
            new AppSurfaceThemeDocumentProvider(new StubResolver(CreateResolution(AppSurfaceThemeMode.Light))))
        {
            ViewContext = viewContext
        };
        var firstOutput = CreateOutput("appsurface-theme-head");
        var secondOutput = CreateOutput("appsurface-theme-head");

        first.Process(CreateContext(), firstOutput);
        second.Process(CreateContext(), secondOutput);

        var html = firstOutput.Content.GetContent() + secondOutput.Content.GetContent();
        Assert.Equal(1, CountOccurrences(html, "<meta name=\"color-scheme\""));
        Assert.Equal(1, CountOccurrences(html, "<style data-as-theme-critical"));
    }

    [Fact]
    public void PreferenceSerializer_ShouldEmitMutuallyExclusiveSystemAndExplicitModeSelectors()
    {
        var document = AppSurfaceThemeDocumentSerializer.SerializePreference(CreateResolution(AppSurfaceThemeMode.Dark));

        Assert.Contains("[data-as-theme=\"appsurface\"][data-as-theme-mode=\"system\"]", document.HeadContent, StringComparison.Ordinal);
        Assert.Contains("[data-as-theme=\"appsurface\"][data-as-theme-mode=\"light\"]", document.HeadContent, StringComparison.Ordinal);
        Assert.Contains("[data-as-theme=\"appsurface\"][data-as-theme-mode=\"dark\"]", document.HeadContent, StringComparison.Ordinal);
        Assert.Contains(
            "[data-as-theme=\"appsurface\"][data-as-theme-mode=\"system\"],\n[data-as-theme=\"appsurface\"][data-as-theme-mode=\"light\"] {",
            document.HeadContent,
            StringComparison.Ordinal);
        Assert.Contains("color-scheme: light !important;", document.HeadContent, StringComparison.Ordinal);
        Assert.Contains("color-scheme: dark !important;", document.HeadContent, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-color-scheme: dark)", document.HeadContent, StringComparison.Ordinal);
        Assert.Equal("system", document.RootThemeMode);
    }

    [Fact]
    public void PreferenceRegistration_ShouldUseSystemDocumentForTheConfiguredPair()
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceTheming(options =>
        {
            options.Pairs.Add(AppSurfaceThemePair.AppSurface());
            options.DefaultMode = AppSurfaceThemeMode.Dark;
        });
        services.AddAppSurfaceWebThemePreferences();
        using var provider = services.BuildServiceProvider();

        var document = provider.GetRequiredService<IAppSurfaceThemeDocumentProvider>().GetDocument();
        Assert.Equal("system", document.RootThemeMode);
        Assert.Contains("[data-as-theme=\"appsurface\"][data-as-theme-mode=\"dark\"]", document.HeadContent, StringComparison.Ordinal);
    }

    [Fact]
    public void PreferenceRegistration_ShouldSupportACustomResolverWithoutARegistry()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppSurfaceThemeResolver>(new StubResolver(CreateResolution(AppSurfaceThemeMode.Dark)));
        services.AddAppSurfaceWebThemePreferences();
        using var provider = services.BuildServiceProvider();

        var document = provider.GetRequiredService<IAppSurfaceThemeDocumentProvider>().GetDocument();

        Assert.Equal("system", document.RootThemeMode);
        Assert.True(document.IsRenderable);
    }

    [Fact]
    public void PreferenceRegistration_ShouldReplaceEarlierPreferenceConfiguration()
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceTheming(options => options.Pairs.Add(AppSurfaceThemePair.AppSurface()));
        services.AddAppSurfaceWebThemePreferences(options => options.StorageKey = "first-theme-key");
        services.AddAppSurfaceWebThemePreferences(options => options.StorageKey = "latest-theme-key");

        Assert.Equal(1, services.Count(descriptor => descriptor.ServiceType == typeof(AppSurfaceThemePreferenceOptions)));
        Assert.Equal(1, services.Count(descriptor => descriptor.ServiceType == typeof(AppSurfaceThemePreferenceBootstrap)));

        using var provider = services.BuildServiceProvider();
        Assert.Equal("latest-theme-key", provider.GetRequiredService<AppSurfaceThemePreferenceOptions>().StorageKey);
        Assert.Equal("latest-theme-key", provider.GetRequiredService<AppSurfaceThemePreferenceBootstrap>().StorageKey);
    }

    [Fact]
    public void PreferenceRegistration_ShouldRequireNeutralTheming()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddAppSurfaceWebThemePreferences());

        Assert.Contains("ASWEBTHEME002", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IAppSurfaceThemeResolver), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreferenceOptions_ShouldRejectUnsafeStorageKeys()
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceTheming(options => options.Pairs.Add(AppSurfaceThemePair.AppSurface()));

        var exception = Assert.Throws<ArgumentException>(
            () => services.AddAppSurfaceWebThemePreferences(options => options.StorageKey = "bad key"));

        Assert.Contains("ASWEBTHEME001", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionRegistration_ShouldRenderTheRegisteredPairOncePerScope()
    {
        var policy = new CountingThemeSelectionPolicy(new AppSurfaceThemeId("appsurface-alt"));
        var services = CreateSelectionServices(policy);
        services.AddAppSurfaceWebThemeSelection();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        var documentProvider = scope.ServiceProvider.GetRequiredService<IAppSurfaceThemeDocumentProvider>();
        var first = documentProvider.GetDocument();
        var second = documentProvider.GetDocument();

        Assert.Same(first, second);
        Assert.Equal(1, policy.CallCount);
        Assert.Contains("data-as-theme=\"appsurface-alt\"", first.RootAttributes, StringComparison.Ordinal);
        Assert.Contains("--as-canvas: #f8fafc;", first.HeadContent, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionRegistration_ShouldFallBackToTheConfiguredDefaultWhenThePolicyHasNoSelection()
    {
        var services = CreateSelectionServices(new CountingThemeSelectionPolicy());
        services.AddAppSurfaceWebThemeSelection();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        var document = scope.ServiceProvider.GetRequiredService<IAppSurfaceThemeDocumentProvider>().GetDocument();

        Assert.Contains("data-as-theme=\"appsurface\"", document.RootAttributes, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectionProvider_ShouldRejectEmptyAndUnknownPairIds(bool useUnknownId)
    {
        var selectedId = useUnknownId ? new AppSurfaceThemeId("unknown") : default;
        var provider = new AppSurfaceThemeSelectionDocumentProvider(
            new CountingThemeSelectionPolicy(selectedId),
            CreateSelectionDocumentCache());

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetDocument());

        Assert.Contains("ASWEBTHEME008", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionProvider_ShouldSanitizePolicyFailuresWithoutFallingBack()
    {
        var expected = new InvalidOperationException("tenant-a secret");
        var provider = new AppSurfaceThemeSelectionDocumentProvider(
            new ThrowingThemeSelectionPolicy(expected),
            CreateSelectionDocumentCache());

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetDocument());

        Assert.Contains("ASWEBTHEME009", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-a", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void SelectionProvider_ShouldPreserveCancellation()
    {
        var expected = new OperationCanceledException("host cancellation");
        var provider = new AppSurfaceThemeSelectionDocumentProvider(
            new ThrowingThemeSelectionPolicy(expected),
            CreateSelectionDocumentCache());

        var actual = Assert.Throws<OperationCanceledException>(() => provider.GetDocument());

        Assert.Same(expected, actual);
    }

    [Fact]
    public void SelectionCache_ShouldResolveTheDefaultOnceAndReuseDocumentsAcrossScopes()
    {
        var resolver = new CountingResolver(CreateResolution(AppSurfaceThemeMode.Dark));
        var registry = CreateRegistry();
        var cache = new AppSurfaceThemeSelectionDocumentCache(registry, resolver);
        var policy = new CountingThemeSelectionPolicy(new AppSurfaceThemeId("appsurface-alt"));

        var first = new AppSurfaceThemeSelectionDocumentProvider(policy, cache).GetDocument();
        var second = new AppSurfaceThemeSelectionDocumentProvider(policy, cache).GetDocument();

        Assert.Equal(1, resolver.ResolveCalls);
        Assert.Same(first, second);
        Assert.Equal("dark", first.RootThemeMode);
        Assert.True(cache.TryGet(new AppSurfaceThemeId("appsurface"), out var defaultDocument));
        Assert.Same(cache.DefaultDocument, defaultDocument);
    }

    [Fact]
    public void SelectionRegistration_ShouldRequireTheNeutralRegistryAndResolver()
    {
        var services = new ServiceCollection();
        services.AddScoped<IAppSurfaceWebThemeSelectionPolicy, CountingThemeSelectionPolicy>();

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddAppSurfaceWebThemeSelection());

        Assert.Contains("ASWEBTHEME003", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IAppSurfaceThemeRegistry), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IAppSurfaceThemeResolver), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionRegistration_ShouldRequireAScopedPolicy()
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceTheming(options => options.Pairs.Add(AppSurfaceThemePair.AppSurface()));

        var missing = Assert.Throws<InvalidOperationException>(() => services.AddAppSurfaceWebThemeSelection());
        Assert.Contains("ASWEBTHEME004", missing.Message, StringComparison.Ordinal);

        services.AddSingleton<IAppSurfaceWebThemeSelectionPolicy, CountingThemeSelectionPolicy>();
        var singleton = Assert.Throws<InvalidOperationException>(() => services.AddAppSurfaceWebThemeSelection());
        Assert.Contains("ASWEBTHEME004", singleton.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionRegistration_ShouldRejectPreferencesInEitherOrder()
    {
        var beforeSelection = CreateSelectionServices(new CountingThemeSelectionPolicy());
        beforeSelection.AddAppSurfaceWebThemePreferences();
        var firstException = Assert.Throws<InvalidOperationException>(() => beforeSelection.AddAppSurfaceWebThemeSelection());

        var afterSelection = CreateSelectionServices(new CountingThemeSelectionPolicy());
        afterSelection.AddAppSurfaceWebThemeSelection();
        var secondException = Assert.Throws<InvalidOperationException>(() => afterSelection.AddAppSurfaceWebThemePreferences());

        Assert.Contains("ASWEBTHEME005", firstException.Message, StringComparison.Ordinal);
        Assert.Contains("ASWEBTHEME005", secondException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionRegistration_ShouldRejectConsumerOwnedProvidersAndDuplicates()
    {
        var custom = CreateSelectionServices(new CountingThemeSelectionPolicy());
        custom.AddSingleton<IAppSurfaceThemeDocumentProvider, EmptyDocumentProvider>();
        var customException = Assert.Throws<InvalidOperationException>(() => custom.AddAppSurfaceWebThemeSelection());

        var duplicate = CreateSelectionServices(new CountingThemeSelectionPolicy());
        duplicate.AddAppSurfaceWebThemeSelection();
        var duplicateException = Assert.Throws<InvalidOperationException>(() => duplicate.AddAppSurfaceWebThemeSelection());

        Assert.Contains("ASWEBTHEME006", customException.Message, StringComparison.Ordinal);
        Assert.Contains("ASWEBTHEME007", duplicateException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionStartupValidator_ShouldRejectALaterDocumentProviderReplacementWithoutInvokingThePolicy()
    {
        var policy = new ThrowingThemeSelectionPolicy(new InvalidOperationException("policy must not run at startup"));
        var services = CreateSelectionServices(policy);
        services.AddAppSurfaceWebThemeSelection();
        services.Replace(ServiceDescriptor.Singleton<IAppSurfaceThemeDocumentProvider, EmptyDocumentProvider>());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var exception = Assert.Throws<InvalidOperationException>(
            () => new AppSurfaceThemeSelectionStartupValidator(provider).Validate());

        Assert.Contains("ASWEBTHEME006", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionStartupValidator_ShouldAcceptTheBuiltInProviderWithoutInvokingThePolicy()
    {
        var policy = new ThrowingThemeSelectionPolicy(new InvalidOperationException("policy must not run at startup"));
        var services = CreateSelectionServices(policy);
        services.AddAppSurfaceWebThemeSelection();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        new AppSurfaceThemeSelectionStartupValidator(provider).Validate();
    }

    [Fact]
    public void SelectionStartupValidator_ShouldRejectALaterNonScopedPolicyRegistration()
    {
        var services = CreateSelectionServices(new CountingThemeSelectionPolicy());
        services.AddAppSurfaceWebThemeSelection();
        services.AddSingleton<IAppSurfaceWebThemeSelectionPolicy, CountingThemeSelectionPolicy>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var exception = Assert.Throws<InvalidOperationException>(
            () => new AppSurfaceThemeSelectionStartupValidator(provider).Validate());

        Assert.Contains("ASWEBTHEME004", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BaseWebTheming_ShouldRemainANoOpAfterSelectionRegistration()
    {
        var services = CreateSelectionServices(new CountingThemeSelectionPolicy());
        services.AddAppSurfaceWebThemeSelection();
        services.AddAppSurfaceWebTheming();

        var descriptor = Assert.Single(
            services,
            service => service.ServiceType == typeof(IAppSurfaceThemeDocumentProvider));
        Assert.Equal(typeof(AppSurfaceThemeSelectionDocumentProvider), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void TenantThemeProofMap_ShouldAllowSeveralExactTenantIdsToShareARegisteredPair()
    {
        var map = TenantThemeMap.Create(
            CreateRegistry(),
            [
                new TenantThemeMapping("tenant-a", new AppSurfaceThemeId("appsurface-alt")),
                new TenantThemeMapping("tenant-b", new AppSurfaceThemeId("appsurface-alt"))
            ]);

        Assert.True(map.TryGet("tenant-a", out var tenantATheme));
        Assert.True(map.TryGet("tenant-b", out var tenantBTheme));
        Assert.Equal(new AppSurfaceThemeId("appsurface-alt"), tenantATheme);
        Assert.Equal(tenantATheme, tenantBTheme);
        Assert.False(map.TryGet("Tenant-A", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" tenant-a")]
    [InlineData("tenant-a ")]
    public void TenantThemeProofMap_ShouldRejectBlankAndWhitespaceTenantIds(string tenantId)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => TenantThemeMap.Create(
                CreateRegistry(),
                [new TenantThemeMapping(tenantId, new AppSurfaceThemeId("appsurface"))]));

        Assert.Contains("non-blank tenant id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TenantThemeProofMap_ShouldRejectExactDuplicatesAndUnknownPairs()
    {
        var duplicate = Assert.Throws<InvalidOperationException>(
            () => TenantThemeMap.Create(
                CreateRegistry(),
                [
                    new TenantThemeMapping("tenant-a", new AppSurfaceThemeId("appsurface")),
                    new TenantThemeMapping("tenant-a", new AppSurfaceThemeId("appsurface-alt"))
                ]));
        var unknown = Assert.Throws<InvalidOperationException>(
            () => TenantThemeMap.Create(
                CreateRegistry(),
                [new TenantThemeMapping("tenant-a", new AppSurfaceThemeId("unknown"))]));

        Assert.Contains("same ordinal tenant id", duplicate.Message, StringComparison.Ordinal);
        Assert.Contains("registered by AddAppSurfaceTheming", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TenantThemeProofMap_ShouldRejectNullMappingsAndSafelyIgnoreANullLookupKey()
    {
        var nullEntry = Assert.Throws<InvalidOperationException>(
            () => TenantThemeMap.Create(CreateRegistry(), [null!]));
        var map = TenantThemeMap.Create(
            CreateRegistry(),
            [new TenantThemeMapping("tenant-a", new AppSurfaceThemeId("appsurface"))]);

        Assert.Contains("null entries", nullEntry.Message, StringComparison.Ordinal);
        Assert.False(map.TryGet(null, out _));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("development")]
    public void TenantThemeProofHostEnvironment_ShouldAllowDevelopment(string environmentName)
    {
        TenantThemeProofHostEnvironment.ThrowUnlessDevelopment(environmentName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void TenantThemeProofHostEnvironment_ShouldRejectEveryNonDevelopmentEnvironment(string? environmentName)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => TenantThemeProofHostEnvironment.ThrowUnlessDevelopment(environmentName));

        Assert.Contains("only in Development", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("with\tspace")]
    [InlineData("contains'quote")]
    [InlineData("contains\"quote")]
    [InlineData("contains\u0000control")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijklmn")]
    public void PreferenceOptions_ShouldRejectMissingAndUnsafeStorageKeys(string? storageKey)
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceTheming(options => options.Pairs.Add(AppSurfaceThemePair.AppSurface()));

        var exception = Assert.Throws<ArgumentException>(
            () => services.AddAppSurfaceWebThemePreferences(options => options.StorageKey = storageKey!));

        Assert.Contains("ASWEBTHEME001", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HeadTagHelper_ShouldEmitNonceProtectedPreferenceBootstrapBeforeCriticalStyle()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AppSurfaceThemePreferenceBootstrap(new AppSurfaceThemePreferenceOptions()));
        using var provider = services.BuildServiceProvider();
        var helper = new AppSurfaceThemeHeadTagHelper(
            new AppSurfaceThemeDocumentProvider(new StubResolver(CreateResolution(AppSurfaceThemeMode.System))),
            provider)
        {
            Nonce = "nonce-value"
        };
        var output = CreateOutput("appsurface-theme-head");

        helper.Process(CreateContext(), output);

        var html = output.Content.GetContent();
        Assert.Contains("<script data-as-theme-preference-bootstrap data-as-theme-storage-key=\"as_theme\" nonce=\"nonce-value\">", html, StringComparison.Ordinal);
        Assert.Contains("<style data-as-theme-critical nonce=\"nonce-value\">", html, StringComparison.Ordinal);
        Assert.True(html.IndexOf("<script", StringComparison.Ordinal) < html.IndexOf("<style", StringComparison.Ordinal));
        Assert.StartsWith("sha256-", AppSurfaceThemePreferenceBootstrap.CspHash, StringComparison.Ordinal);
        Assert.Equal(AppSurfaceThemePreferenceBootstrap.CspHash, AppSurfaceThemePreferenceCsp.ScriptHash);
        Assert.Equal(
            "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(AppSurfaceThemePreferenceBootstrap.Script))),
            AppSurfaceThemePreferenceCsp.ScriptHash);
    }

    [Fact]
    public void PreferenceBootstrap_ShouldRenderWithoutANonceAndRejectNullOptions()
    {
        var bootstrap = new AppSurfaceThemePreferenceBootstrap(new AppSurfaceThemePreferenceOptions { StorageKey = "custom-theme" });

        var html = bootstrap.Render(null);

        Assert.Contains("data-as-theme-storage-key=\"custom-theme\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(" nonce=", html, StringComparison.Ordinal);
        var nullOptionsException = Assert.Throws<ArgumentNullException>(
            () => new AppSurfaceThemePreferenceBootstrap(null!));
        var nullStorageKeyException = Assert.Throws<ArgumentNullException>(
            () => new AppSurfaceThemePreferenceBootstrap(new AppSurfaceThemePreferenceOptions { StorageKey = null! }));

        Assert.Equal("options", nullOptionsException.ParamName);
        Assert.Equal("StorageKey", nullStorageKeyException.ParamName);
    }

    [Fact]
    public void HeadTagHelper_ShouldEmitOnePreferenceBootstrapPerMvcRequest()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AppSurfaceThemePreferenceBootstrap(new AppSurfaceThemePreferenceOptions()));
        using var provider = services.BuildServiceProvider();
        var viewContext = CreateViewContext();
        var first = new AppSurfaceThemeHeadTagHelper(
            new AppSurfaceThemeDocumentProvider(new StubResolver(CreateResolution(AppSurfaceThemeMode.System))),
            provider)
        {
            ViewContext = viewContext
        };
        var second = new AppSurfaceThemeHeadTagHelper(
            new AppSurfaceThemeDocumentProvider(new StubResolver(CreateResolution(AppSurfaceThemeMode.System))),
            provider)
        {
            ViewContext = viewContext
        };
        var firstOutput = CreateOutput("appsurface-theme-head");
        var secondOutput = CreateOutput("appsurface-theme-head");

        first.Process(CreateContext(), firstOutput);
        second.Process(CreateContext(), secondOutput);

        var html = firstOutput.Content.GetContent() + secondOutput.Content.GetContent();
        Assert.Equal(1, CountOccurrences(html, "<script data-as-theme-preference-bootstrap"));
        Assert.Equal(1, CountOccurrences(html, "<style data-as-theme-critical"));
    }

    [Fact]
    public void NeutralTheming_ShouldNotImplicitlyRegisterTheWebRenderingAdapter()
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceTheming(options => options.Pairs.Add(AppSurfaceThemePair.AppSurface()));
        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<IAppSurfaceThemeDocumentProvider>());
    }

    [Fact]
    public void ThemeCspNonce_ShouldReadTheHostOwnedRequestValue()
    {
        var context = new DefaultHttpContext();
        context.Items[AppSurfaceThemeCspNonce.HttpContextItemKey] = "nonce-value";

        Assert.Equal("nonce-value", AppSurfaceThemeCspNonce.Get(context));
    }

    [Fact]
    public void ThemeCspNonce_ShouldReturnNullWhenTheHostHasNotSuppliedAStringNonce()
    {
        var context = new DefaultHttpContext();

        Assert.Null(AppSurfaceThemeCspNonce.Get(context));

        context.Items[AppSurfaceThemeCspNonce.HttpContextItemKey] = 42;

        Assert.Null(AppSurfaceThemeCspNonce.Get(context));
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

    private static ViewContext CreateViewContext() => new()
    {
        HttpContext = new DefaultHttpContext()
    };

    private static IEnumerable<(string Name, string Light, string Dark)> GetSemanticRoles()
    {
        var pair = AppSurfaceThemePair.AppSurface();
        return
        [
            ("canvas", pair.Light.Canvas, pair.Dark.Canvas),
            ("surface", pair.Light.Surface, pair.Dark.Surface),
            ("raised-surface", pair.Light.RaisedSurface, pair.Dark.RaisedSurface),
            ("text", pair.Light.Text, pair.Dark.Text),
            ("muted-text", pair.Light.MutedText, pair.Dark.MutedText),
            ("border", pair.Light.Border, pair.Dark.Border),
            ("accent", pair.Light.Accent, pair.Dark.Accent),
            ("accent-strong", pair.Light.AccentStrong, pair.Dark.AccentStrong),
            ("link", pair.Light.Link, pair.Dark.Link),
            ("visited-link", pair.Light.VisitedLink, pair.Dark.VisitedLink),
            ("danger", pair.Light.Danger, pair.Dark.Danger),
            ("focus", pair.Light.Focus, pair.Dark.Focus)
        ];
    }

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

    private static ServiceCollection CreateSelectionServices(IAppSurfaceWebThemeSelectionPolicy policy)
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceTheming(options =>
        {
            var defaultPair = AppSurfaceThemePair.AppSurface();
            options.DefaultTheme = defaultPair.Id;
            options.DefaultMode = AppSurfaceThemeMode.Light;
            options.Pairs.Add(defaultPair);
            options.Pairs.Add(new AppSurfaceThemePair(new AppSurfaceThemeId("appsurface-alt"), defaultPair.Light, defaultPair.Dark));
        });
        services.AddScoped<IAppSurfaceWebThemeSelectionPolicy>(_ => policy);
        return services;
    }

    private static AppSurfaceThemeSelectionDocumentCache CreateSelectionDocumentCache()
    {
        var registry = CreateRegistry();
        return new AppSurfaceThemeSelectionDocumentCache(registry, registry);
    }

    private static AppSurfaceThemeRegistry CreateRegistry()
    {
        var defaultPair = AppSurfaceThemePair.AppSurface();
        var options = new AppSurfaceThemeRegistryOptions
        {
            DefaultTheme = defaultPair.Id,
            DefaultMode = AppSurfaceThemeMode.Light
        };
        options.Pairs.Add(defaultPair);
        options.Pairs.Add(new AppSurfaceThemePair(new AppSurfaceThemeId("appsurface-alt"), defaultPair.Light, defaultPair.Dark));
        return new AppSurfaceThemeRegistry(options);
    }

    private sealed class CountingThemeSelectionPolicy : IAppSurfaceWebThemeSelectionPolicy
    {
        private readonly bool _select;
        private readonly AppSurfaceThemeId _themeId;

        public CountingThemeSelectionPolicy()
        {
        }

        public CountingThemeSelectionPolicy(AppSurfaceThemeId themeId)
        {
            _select = true;
            _themeId = themeId;
        }

        public int CallCount { get; private set; }

        public bool TrySelect(out AppSurfaceThemeId themeId)
        {
            CallCount++;
            themeId = _themeId;
            return _select;
        }
    }

    private sealed class ThrowingThemeSelectionPolicy(Exception exception) : IAppSurfaceWebThemeSelectionPolicy
    {
        public bool TrySelect(out AppSurfaceThemeId themeId)
        {
            themeId = default;
            throw exception;
        }
    }
}
